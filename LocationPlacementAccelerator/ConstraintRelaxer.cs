// v1
/**
* Smart recovery system for vital location types. When a critical type
* (boss altar, vendor, quest camp) fails to place, this analyzes the
* failure data to identify the tightest constraint (bottleneck), relaxes
* that constraint by a configurable magnitude, and re-queues the location
* for another attempt. Up to MaxRelaxationAttempts retries per type.
* I was thinking about making this more general purpose, maybe user
* configurabe but I think this would be best handled perhaps using
* EWD yamls entries or something instead of confusing ridiculously long
* cfg entries. So another TODO... 
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class ConstraintRelaxer
    {
        public class OriginalStats
        {
            public float MinAlt, MaxAlt, MinDist, MaxDist, MinTerr, MaxTerr, ExtRad;
            public int Quantity;
        }

        public static Dictionary<string, int> RelaxationAttempts = new Dictionary<string, int>();
        private static Dictionary<string, OriginalStats> _originalStats = new Dictionary<string, OriginalStats>();
        public static object CapturedOuterLoop = null;

        public static void CaptureStateMachine(object smP)
        {
            CapturedOuterLoop = smP;
        }

        public static void Reset()
        {
            RelaxationAttempts.Clear();
            _originalStats.Clear();
            CapturedOuterLoop = null;
        }

        public static void RestoreQuantities()
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null)
            {
                return;
            }

            foreach (KeyValuePair<string, OriginalStats> kvp in _originalStats)
            {
                ZoneLocation loc = null;
                for (int i = 0; i < zs.m_locations.Count; i++)
                {
                    if (zs.m_locations[i].m_prefabName == kvp.Key)
                    {
                        loc = zs.m_locations[i];
                        break;
                    }
                }

                if (loc != null && loc.m_quantity != kvp.Value.Quantity)
                {
                    loc.m_quantity = kvp.Value.Quantity;
                    if (ModConfig.DiagnosticMode.Value)
                    {
                        DiagnosticLog.WriteLog($"[Adjuster] Restored {kvp.Key} m_quantity to {kvp.Value.Quantity}.");
                    }
                }
            }
        }

        public static bool TryRelax(ReportData dataP)
        {
            if (dataP == null || dataP.Loc == null)
            {
                return false;
            }

            int maxAttempts = ModConfig.MaxRelaxationAttempts.Value;
            if (maxAttempts <= 0)
            {
                return false;
            }

            string prefabName = dataP.Loc.m_prefabName;

            int origQty = dataP.Loc.m_quantity;
            if (Interleaver.OriginalLocations != null)
            {
                for (int i = 0; i < Interleaver.OriginalLocations.Count; i++)
                {
                    if (Interleaver.OriginalLocations[i].m_prefabName == prefabName)
                    {
                        origQty = Interleaver.OriginalLocations[i].m_quantity;
                        break;
                    }
                }
            }

            /**
            * Use the placed count from the caller's ReportData.
            * DO NOT iterate m_locationInstances here. 
            * In the parallel path this method runs on a worker thread while DrainAndCommit() on the main
            * thread is concurrently calling RegisterLocation() -> m_locationInstances.Add().
            * Iterating a Dictionary while another thread structurally modifies it
            * throws InvalidOperationException("Collection was modified").
            */
            int globalPlaced = dataP.Placed;

            if (!PlayabilityPolicy.NeedsRelaxation(prefabName, globalPlaced, origQty))
            {
                return false;
            }

            bool isFirstAttempt = !RelaxationAttempts.TryGetValue(prefabName, out int attempts);
            if (isFirstAttempt)
            {
                attempts = 0;
                RelaxationAttempts[prefabName] = 0;
                _originalStats[prefabName] = new OriginalStats
                {
                    MinAlt = dataP.Loc.m_minAltitude,
                    MaxAlt = dataP.Loc.m_maxAltitude,
                    MinDist = dataP.Loc.m_minDistance,
                    MaxDist = dataP.Loc.m_maxDistance,
                    MinTerr = dataP.Loc.m_minTerrainDelta,
                    MaxTerr = dataP.Loc.m_maxTerrainDelta,
                    ExtRad = dataP.Loc.m_exteriorRadius,
                    Quantity = origQty
                };
            }

            if (attempts >= maxAttempts)
            {
                DiagnosticLog.WriteTimestampedLog(
                    $"[Adjuster] {prefabName} failed after {maxAttempts} relaxation attempts. Abandoning.",
                    BepInEx.Logging.LogLevel.Warning);
                RelaxationTracker.MarkRelaxationExhausted(prefabName);
                return false;
            }

            RelaxationAttempts[prefabName] = attempts + 1;

            PlacementBottleneck bottleneck = PlacementBottleneck.Unknown;
            float maxFailureRate = -1f;

            void AnalyzeConstraint(long errP, long inputP, PlacementBottleneck nameP)
            {
                if (inputP <= 0)
                {
                    return;
                }
                float rate = (float)errP / inputP;
                if (rate >= maxFailureRate)
                {
                    maxFailureRate = rate;
                    bottleneck = nameP;
                }
            }

            AnalyzeConstraint(dataP.ErrDist, dataP.InDist, PlacementBottleneck.Distance);
            AnalyzeConstraint(dataP.ErrBiome, dataP.InBiome, PlacementBottleneck.Biome);
            AnalyzeConstraint(dataP.ErrAlt, dataP.InAlt, PlacementBottleneck.Altitude);
            AnalyzeConstraint(dataP.ErrTerrain, dataP.InTerr, PlacementBottleneck.Terrain);
            AnalyzeConstraint(dataP.ErrSim + dataP.ErrNotSim, dataP.InSim, PlacementBottleneck.Similarity);

            float preMinAlt = dataP.Loc.m_minAltitude;
            float preMaxAlt = dataP.Loc.m_maxAltitude;
            float preMinDist = dataP.Loc.m_minDistance;
            float preMaxDist = dataP.Loc.m_maxDistance;
            float preMaxTerr = dataP.Loc.m_maxTerrainDelta;
            float preExtRad = dataP.Loc.m_exteriorRadius;

            ApplyRelaxation(dataP.Loc, bottleneck, attempts + 1, maxAttempts);

            string attemptDesc = BuildAttemptDescription(
                bottleneck, attempts + 1,
                preMinAlt, preMaxAlt, preMinDist, preMaxDist, preMaxTerr, preExtRad,
                dataP.Loc.m_minAltitude, dataP.Loc.m_maxAltitude,
                dataP.Loc.m_minDistance, dataP.Loc.m_maxDistance,
                dataP.Loc.m_maxTerrainDelta, dataP.Loc.m_exteriorRadius);
            RelaxationTracker.MarkRelaxationAttempt(prefabName, attemptDesc);

            Interleaver.SyncRelaxation(dataP.Loc);

            int minimumNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefabName, origQty);
            int toPlace = Mathf.Max(1, minimumNeeded - globalPlaced);

            SurveyMode.ClearCache(dataP.PrefabName);

            int fallbackBase = 200000;
            if (dataP.Loc.m_prioritized)
            {
                fallbackBase = 100000;
            }
            List<ZoneLocation> newPackets = Interleaver.CreateRelaxedPackets(dataP.Loc, toPlace, fallbackBase);

            bool inserted = false;
            if (CapturedOuterLoop != null)
            {
                Type smType = CapturedOuterLoop.GetType();

                FieldInfo orderedField = null;
                FieldInfo indexField = null;
                FieldInfo[] allFields = smType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < allFields.Length; i++)
                {
                    if (allFields[i].FieldType == typeof(List<ZoneLocation>) && allFields[i].Name.Contains("ordered"))
                    {
                        orderedField = allFields[i];
                    }
                    if (allFields[i].FieldType == typeof(int) && allFields[i].Name.Contains("<i>"))
                    {
                        indexField = allFields[i];
                    }
                }

                if (orderedField != null && indexField != null)
                {
                    List<ZoneLocation> ordered = orderedField.GetValue(CapturedOuterLoop) as List<ZoneLocation>;
                    int idx = (int)indexField.GetValue(CapturedOuterLoop);
                    if (ordered != null)
                    {
                        int insertAt = Math.Min(idx + 1, ordered.Count);
                        ordered.InsertRange(insertAt, newPackets);
                        inserted = true;
                        DiagnosticLog.WriteLog($"[Adjuster] {prefabName} ({newPackets.Count} Chunks) inserted at index {insertAt} for immediate retry.");
                    }
                }
            }

            if (!inserted)
            {
                ZoneSystem zs = ZoneSystem.instance;
                if (zs != null)
                {
                    zs.m_locations.AddRange(newPackets);
                }
            }

            TranspiledEnginePatches.ResetLocationLog();
            DiagnosticLog.WriteLog($"[Adjuster] {prefabName} re-queued for retry.");

            return true;
        }

        private static void ApplyRelaxation(ZoneLocation locP, PlacementBottleneck bottleneckP, int attemptNumberP, int maxAttemptsP)
        {
            float mag = ModConfig.RelaxationMagnitude.Value;

            DiagnosticLog.WriteTimestampedLog(
                $"[Adjuster] RELAXING {locP.m_prefabName} (Attempt {attemptNumberP}/{maxAttemptsP}). Bottleneck: {bottleneckP}. Attempting immediate retry.",
                BepInEx.Logging.LogLevel.Message);

            switch (bottleneckP)
            {
                case PlacementBottleneck.Altitude:
                    float original = locP.m_minAltitude;
                    locP.m_minAltitude = locP.m_minAltitude - Mathf.Max(5f, Mathf.Abs(locP.m_minAltitude) * mag);
                    DiagnosticLog.WriteLog($"   -> MinAltitude stepped down from {original:F0}m to {locP.m_minAltitude:F0}m");
                    locP.m_maxAltitude += Mathf.Max(10f, Mathf.Abs(locP.m_maxAltitude) * mag);
                    break;

                case PlacementBottleneck.Distance:
                    float maxDist = ModConfig.WorldRadius;
                    if (locP.m_maxDistance > 0.1f)
                    {
                        maxDist = locP.m_maxDistance;
                    }
                    locP.m_maxDistance = maxDist + (maxDist * mag);
                    locP.m_minDistance = Mathf.Max(0f, locP.m_minDistance - (locP.m_minDistance * mag));
                    break;

                case PlacementBottleneck.Terrain:
                    locP.m_maxTerrainDelta += Mathf.Max(2f, locP.m_maxTerrainDelta * mag);
                    locP.m_minTerrainDelta = Mathf.Max(0f, locP.m_minTerrainDelta - (locP.m_minTerrainDelta * mag));
                    break;

                case PlacementBottleneck.Similarity:
                    locP.m_exteriorRadius = Mathf.Max(0f, locP.m_exteriorRadius - (locP.m_exteriorRadius * mag));
                    break;

                default:
                    locP.m_maxTerrainDelta += 5f;
                    float defMaxDist = ModConfig.WorldRadius;
                    if (locP.m_maxDistance > 0.1f)
                    {
                        defMaxDist = locP.m_maxDistance;
                    }
                    locP.m_maxDistance = defMaxDist * 1.1f;
                    locP.m_minAltitude -= 10f;
                    break;
            }
        }

        private static string BuildAttemptDescription(
            PlacementBottleneck bottleneckP, int attemptNumP,
            float preMinAltP, float preMaxAltP, float preMinDistP, float preMaxDistP,
            float preMaxTerrP, float preExtRadP,
            float postMinAltP, float postMaxAltP, float postMinDistP, float postMaxDistP,
            float postMaxTerrP, float postExtRadP)
        {
            string constraint;
            switch (bottleneckP)
            {
                case PlacementBottleneck.Altitude:
                    constraint = $"Altitude {preMinAltP:F0}..{preMaxAltP:F0}-->{postMinAltP:F0}..{postMaxAltP:F0}";
                    break;
                case PlacementBottleneck.Distance:
                    constraint = $"Distance {preMinDistP:F0}..{preMaxDistP:F0}-->{postMinDistP:F0}..{postMaxDistP:F0}";
                    break;
                case PlacementBottleneck.Terrain:
                    constraint = $"TerrainDelta {preMaxTerrP:F1}-->{postMaxTerrP:F1}";
                    break;
                case PlacementBottleneck.Similarity:
                    constraint = $"ExteriorRadius {preExtRadP:F0}-->{postExtRadP:F0}";
                    break;
                default:
                    constraint = "Constraints loosened";
                    break;
            }
            return $"[attempt {attemptNumP}]: {constraint}";
        }

        public static string GetRelaxationSummary(string prefabNameP, ZoneLocation currentLocP)
        {
            bool hasAttempts = RelaxationAttempts.TryGetValue(prefabNameP, out int attempts);
            if (!hasAttempts || attempts == 0)
            {
                return "";
            }

            bool hasOrig = _originalStats.TryGetValue(prefabNameP, out OriginalStats orig);
            if (!hasOrig)
            {
                return $"(Relaxed {attempts} times)";
            }

            List<string> changes = new List<string>();
            if (Mathf.Abs(currentLocP.m_minAltitude - orig.MinAlt) > 1f)
            {
                changes.Add($"MinAlt: {orig.MinAlt:F0}->{currentLocP.m_minAltitude:F0}");
            }
            if (Mathf.Abs(currentLocP.m_maxDistance - orig.MaxDist) > 1f)
            {
                changes.Add($"MaxDist: {orig.MaxDist:F0}->{currentLocP.m_maxDistance:F0}");
            }
            if (Mathf.Abs(currentLocP.m_minDistance - orig.MinDist) > 1f)
            {
                changes.Add($"MinDist: {orig.MinDist:F0}->{currentLocP.m_minDistance:F0}");
            }
            if (Mathf.Abs(currentLocP.m_maxTerrainDelta - orig.MaxTerr) > 0.1f)
            {
                changes.Add($"MaxTerr: {orig.MaxTerr:F1}->{currentLocP.m_maxTerrainDelta:F1}");
            }
            if (Mathf.Abs(currentLocP.m_exteriorRadius - orig.ExtRad) > 1f)
            {
                changes.Add($"ExtRadius: {orig.ExtRad:F0}->{currentLocP.m_exteriorRadius:F0}");
            }

            if (changes.Count == 0)
            {
                return $"(Relaxed {attempts} times)";
            }
            return $"(Relaxed {attempts}x: {string.Join(", ", changes)})";
        }
    }
}
