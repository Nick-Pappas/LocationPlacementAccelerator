// v1.0.6

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
*
* THE TODO! done?! 
* 1.0.1: Passed prioritization context to RelaxationTracker calls so 
* severity evaluation correctly categorizes failures as Red, Orange, or Yellow.
*
* 1.0.2: Fixed relaxation math to strictly respect the configured magnitude percentage.
* Removed massive hardcoded leaps on Unknown bottlenecks. Added clamping to ensure
* land structures (MinAlt >= 0) are never relaxed to spawn underwater, and underwater
* structures (MaxAlt <= 0) are never relaxed to spawn on land. 
*
* 1.0.3: Distance relaxation now clamps m_maxDistance to ModConfig.WorldRadius so 
* I do not pretend to relax to a value beyond the actual playable disk. The min 
* distance side already clamped to 0; max side now mirrors that with WorldRadius.
*
* 1.0.4: Added RestoreAllStats() for the LPA public API path. The existing
* RestoreQuantities only puts m_quantity back; it leaves the relaxed
* altitude/distance/terrain/exteriorRadius values mutated on the ZoneLocations
* in ZoneSystem.m_locations. World-gen tolerates this because the stats are
* not read after EndGeneration. The API path needs a clean rollback though
* or sequential calls see "relaxed" constraints from earlier calls. This
* function takes the OriginalStats snapshot and restores every field. World-gen
* is unchanged - it still only calls RestoreQuantities.
*
* 1.0.5: RelaxationAttempts and _originalStats switched to ConcurrentDictionary.
* TryRelax (worker thread) writes both before the attempts >= maxAttempts gate,
* so even MaxRelaxationAttempts = 0 triggers concurrent writes when multiple
* prefabs fail at the same moment. Same crash class as the OccupiedZoneIndices
* HashSet race, non-thread-safe collection mutated from parallel workers.
*
* 1.0.6: Keyed on the logical TYPE KEY (Interleaver.GetTypeKey) instead of the prefab name. 
* So the idea is that EWD clones are entirely different location types that share a prefab keyed per type, each
* clone relaxes completely independently - its own attempt count, its own original-stats snapshot, its own restore and so on.... with ZERO clone awareness anywhere in this file. 
* That is the whole point of the virtual key: to the relaxer a clone is simply a separate prefab.
* The only two kind of not trivial edits are the two restore loops and SyncRelaxation's (in Interleaver), which now match m_locations entries by GetTypeKey(loc) == key instead of by
* prefab name, so a Mountain-variant relaxation will (?) never bleed onto a BlackForest variant.
* PlayabilityPolicy lookups (NeedsRelaxation, GetMinimumNeededCount) stay keyed on the real prefab name and playability is a property of the prefab's role, and those are config keys.
* For any world without clones the type key equals the prefab name, so nothing changes. I am not sure how the hell I am gonna test this, without having making those crazy yamls. Ask Dhakhar or JP to give me some. 
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

        public static System.Collections.Concurrent.ConcurrentDictionary<string, int> RelaxationAttempts
            = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(System.StringComparer.Ordinal);
        private static System.Collections.Concurrent.ConcurrentDictionary<string, OriginalStats> _originalStats
            = new System.Collections.Concurrent.ConcurrentDictionary<string, OriginalStats>(System.StringComparer.Ordinal);
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
                    if (Interleaver.GetTypeKey(zs.m_locations[i]) == kvp.Key)
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

        /**
        * Full rollback for the LPA public API path. Where RestoreQuantities only puts m_quantity back, this restores every relaxed field on the
        * matching ZoneLocation in ZoneSystem.m_locations (altitude bounds,distance bounds, terrain delta bounds, exterior radius). 
        * The snapshot was taken on first relaxation in TryRelax / EnsureSnapshot (whichever fires first), so this is a total no-op when nothing was
        * relaxed during the call. Called from LPA.API.RunCustomPlacement's finally so consecutive API calls start each with the original world-defined constraints.
        */
        public static void RestoreAllStats()
        {
            ZoneSystem zs = ZoneSystem.instance;
            if (zs == null || _originalStats.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, OriginalStats> kvp in _originalStats)
            {
                OriginalStats orig = kvp.Value;
                for (int i = 0; i < zs.m_locations.Count; i++)
                {
                    ZoneLocation loc = zs.m_locations[i];
                    if (Interleaver.GetTypeKey(loc) != kvp.Key)
                    {
                        continue;
                    }
                    loc.m_minAltitude = orig.MinAlt;
                    loc.m_maxAltitude = orig.MaxAlt;
                    loc.m_minDistance = orig.MinDist;
                    loc.m_maxDistance = orig.MaxDist;
                    loc.m_minTerrainDelta = orig.MinTerr;
                    loc.m_maxTerrainDelta = orig.MaxTerr;
                    loc.m_exteriorRadius = orig.ExtRad;
                    loc.m_quantity = orig.Quantity;
                }
                if (ModConfig.DiagnosticMode.Value)
                {
                    DiagnosticLog.WriteLog($"[Adjuster] API restored all stats for {kvp.Key}.");
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
            // Accounting key: distinct clones sharing this prefab each get their own type key, so their relaxation state never collides in theory.
            // prefabName is kept only for the PlayabilityPolicy config lookups and the human-readable log lines below.
            string typeKey = Interleaver.GetTypeKey(dataP.Loc);

            int origQty = Interleaver.GetOriginalQuantity(dataP.Loc);

            /**
            * Use the placed count from the caller's ReportData.
            * DO NOT iterate m_locationInstances here. 
            * In the parallel path this method runs on a worker thread while DrainAndCommit() on the main
            * thread is concurrently calling RegisterLocation() -> m_locationInstances.Add().
            * Iterating a Dictionary while another thread structurally modifies it throws InvalidOperationException("Collection was modified"). 
            */
            int globalPlaced = dataP.Placed;

            if (!PlayabilityPolicy.NeedsRelaxation(prefabName, globalPlaced, origQty))
            {
                return false;
            }

            bool isFirstAttempt = !RelaxationAttempts.TryGetValue(typeKey, out int attempts);
            if (isFirstAttempt)
            {
                attempts = 0;
                RelaxationAttempts[typeKey] = 0;
                _originalStats[typeKey] = new OriginalStats
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
                RelaxationTracker.MarkRelaxationExhausted(typeKey);
                return false;
            }

            RelaxationAttempts[typeKey] = attempts + 1;

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

            ApplyRelaxation(dataP.Loc, typeKey, bottleneck, attempts + 1, maxAttempts);

            string attemptDesc = BuildAttemptDescription(
                bottleneck, attempts + 1,
                preMinAlt, preMaxAlt, preMinDist, preMaxDist, preMaxTerr, preExtRad,
                dataP.Loc.m_minAltitude, dataP.Loc.m_maxAltitude,
                dataP.Loc.m_minDistance, dataP.Loc.m_maxDistance,
                dataP.Loc.m_maxTerrainDelta, dataP.Loc.m_exteriorRadius);

            RelaxationTracker.MarkRelaxationAttempt(typeKey, attemptDesc, dataP.Loc.m_prioritized);

            Interleaver.SyncRelaxation(dataP.Loc);

            int minimumNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefabName, origQty);
            int toPlace = Mathf.Max(1, minimumNeeded - globalPlaced);

            SurveyMode.ClearCache(typeKey);

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

        private static void ApplyRelaxation(ZoneLocation locP, string typeKeyP, PlacementBottleneck bottleneckP, int attemptNumberP, int maxAttemptsP)
        {
            float mag = ModConfig.RelaxationMagnitude.Value;

            DiagnosticLog.WriteTimestampedLog(
                $"[Adjuster] RELAXING {locP.m_prefabName} (Attempt {attemptNumberP}/{maxAttemptsP}). Bottleneck: {bottleneckP}. Attempting immediate retry.",
                BepInEx.Logging.LogLevel.Message);

            bool hasOrig = _originalStats.TryGetValue(typeKeyP, out OriginalStats orig);
            if (!hasOrig)
            {
                orig = new OriginalStats
                {
                    MinAlt = locP.m_minAltitude,
                    MaxAlt = locP.m_maxAltitude,
                    MinDist = locP.m_minDistance,
                    MaxDist = locP.m_maxDistance,
                    MinTerr = locP.m_minTerrainDelta,
                    MaxTerr = locP.m_maxTerrainDelta,
                    ExtRad = locP.m_exteriorRadius
                };
            }

            bool relaxAlt = (bottleneckP == PlacementBottleneck.Altitude || bottleneckP == PlacementBottleneck.Unknown);
            bool relaxDist = (bottleneckP == PlacementBottleneck.Distance || bottleneckP == PlacementBottleneck.Unknown);
            bool relaxTerr = (bottleneckP == PlacementBottleneck.Terrain || bottleneckP == PlacementBottleneck.Unknown);
            bool relaxSim = (bottleneckP == PlacementBottleneck.Similarity || bottleneckP == PlacementBottleneck.Unknown);

            if (relaxAlt)
            {
                float minAltStep = Mathf.Max(1f, Mathf.Abs(locP.m_minAltitude) * mag);
                locP.m_minAltitude -= minAltStep;
                if (orig.MinAlt >= 0f && locP.m_minAltitude < 0f)
                {
                    locP.m_minAltitude = 0f;
                }

                float maxAltStep = Mathf.Max(1f, Mathf.Abs(locP.m_maxAltitude) * mag);
                locP.m_maxAltitude += maxAltStep;
                if (orig.MaxAlt <= 0f && locP.m_maxAltitude > 0f)
                {
                    locP.m_maxAltitude = 0f;
                }

                DiagnosticLog.WriteLog($"   -> Altitude relaxed to {locP.m_minAltitude:F0}m..{locP.m_maxAltitude:F0}m");
            }

            if (relaxDist)
            {
                if (locP.m_maxDistance > 0.1f)
                {
                    float maxDistStep = Mathf.Max(1f, locP.m_maxDistance * mag);
                    locP.m_maxDistance += maxDistStep;
                    /**
                    * No point relaxing past the playable disk - any zone beyond WorldRadius
                    * is empty and would just inflate the reported max in logs while doing
                    * nothing useful. Mirrors the 0-floor on m_minDistance.
                    */
                    if (locP.m_maxDistance > ModConfig.WorldRadius)
                    {
                        locP.m_maxDistance = ModConfig.WorldRadius;
                    }
                }

                if (locP.m_minDistance > 0f)
                {
                    float minDistStep = Mathf.Max(1f, locP.m_minDistance * mag);
                    locP.m_minDistance -= minDistStep;
                    if (locP.m_minDistance < 0f)
                    {
                        locP.m_minDistance = 0f;
                    }
                }

                DiagnosticLog.WriteLog($"   -> Distance relaxed to {locP.m_minDistance:F0}m..{locP.m_maxDistance:F0}m");
            }

            if (relaxTerr)
            {
                float maxTerrStep = Mathf.Max(1f, locP.m_maxTerrainDelta * mag);
                locP.m_maxTerrainDelta += maxTerrStep;

                if (locP.m_minTerrainDelta > 0f)
                {
                    float minTerrStep = Mathf.Max(1f, locP.m_minTerrainDelta * mag);
                    locP.m_minTerrainDelta -= minTerrStep;
                    if (locP.m_minTerrainDelta < 0f)
                    {
                        locP.m_minTerrainDelta = 0f;
                    }
                }

                DiagnosticLog.WriteLog($"   -> TerrainDelta relaxed to {locP.m_minTerrainDelta:F1}..{locP.m_maxTerrainDelta:F1}");
            }

            if (relaxSim)
            {
                if (locP.m_exteriorRadius > 0f)
                {
                    float extRadStep = Mathf.Max(1f, locP.m_exteriorRadius * mag);
                    locP.m_exteriorRadius -= extRadStep;
                    if (locP.m_exteriorRadius < 0f)
                    {
                        locP.m_exteriorRadius = 0f;
                    }
                }

                DiagnosticLog.WriteLog($"   -> ExteriorRadius relaxed to {locP.m_exteriorRadius:F0}");
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
            // Resolve through the location's type key so a clone reads its own relaxation record, not a same-prefab brother-sisters. prefabNameP is only the fallback when no location is supplied.
            string key = prefabNameP;
            if (currentLocP != null)
            {
                key = Interleaver.GetTypeKey(currentLocP);
            }

            bool hasAttempts = RelaxationAttempts.TryGetValue(key, out int attempts);
            if (!hasAttempts || attempts == 0)
            {
                return "";
            }

            bool hasOrig = _originalStats.TryGetValue(key, out OriginalStats orig);
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