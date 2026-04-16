// v1
/**
* Telemetry data gathering methods for both engines.
* Track methods record altitude/distance/biome failures into TelemetryContext.
* Capture methods are Harmony postfixes for transpiled-engine biome tracking.
* Log methods write diagnostic heartbeats and location start banners.
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LPA
{
    public static class TelemetryHelpers
    {
        public static int FilterTotalCalls = 0;
        public static int FilterAcceptedZones = 0;
        public static float GlobalMinAltitudeSeen = float.MaxValue;
        public static float GlobalMaxAltitudeSeen = float.MinValue;

        private static long _totalInnerIterations = 0;
        private static long _lastInnerLogValue = 0;

        public static void TrackGlobalAltitude(float altitudeP)
        {
            if (altitudeP < GlobalMinAltitudeSeen)
            {
                GlobalMinAltitudeSeen = altitudeP;
            }
            if (altitudeP > GlobalMaxAltitudeSeen)
            {
                GlobalMaxAltitudeSeen = altitudeP;
            }
        }

        public static void TrackGlobalAltitude(float altitudeP, Vector3 pointP)
        {
            if (altitudeP < GlobalMinAltitudeSeen)
            {
                GlobalMinAltitudeSeen = altitudeP;
            }
            if (altitudeP > GlobalMaxAltitudeSeen)
            {
                GlobalMaxAltitudeSeen = altitudeP;
            }
        }

        public static void TrackAltitudeFailure(object instanceP, float heightP, float minAltP, float maxAltP, Vector3 pointP)
        {
            if (DiagnosticLog.MinimalLogging)
            {
                return;
            }
            TelemetryContext context = TranspiledCompletionHandler.GetContext(instanceP.GetHashCode());
            if (context == null)
            {
                return;
            }

            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pointP);
            RecordAltitudeFailure(context, biome, heightP, minAltP, maxAltP);
        }

        public static void TrackDistanceFailure(object instanceP, float distanceP, float minDistP, float maxDistP)
        {
            if (DiagnosticLog.MinimalLogging)
            {
                return;
            }
            TelemetryContext context = TranspiledCompletionHandler.GetContext(instanceP.GetHashCode());
            if (context == null)
            {
                return;
            }

            if (maxDistP != 0f && distanceP > maxDistP)
            {
                context.DistanceTooFar++;
            }
            else if (distanceP < minDistP)
            {
                context.DistanceTooClose++;
            }
        }

        public static void TrackAltitudeFailureCtx(TelemetryContext ctxP, float heightP, float minAltP, float maxAltP, Vector3 pointP)
        {
            if (DiagnosticLog.MinimalLogging || ctxP == null)
            {
                return;
            }
            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pointP);
            RecordAltitudeFailure(ctxP, biome, heightP, minAltP, maxAltP);
        }

        public static void TrackDistanceFailureCtx(TelemetryContext ctxP, float distanceP, float minDistP, float maxDistP)
        {
            if (DiagnosticLog.MinimalLogging || ctxP == null)
            {
                return;
            }
            if (maxDistP != 0f && distanceP > maxDistP)
            {
                ctxP.DistanceTooFar++;
            }
            else if (distanceP < minDistP)
            {
                ctxP.DistanceTooClose++;
            }
        }

        public static void CaptureWrongBiomeCtx(TelemetryContext ctxP, Heightmap.Biome biomeP)
        {
            if (DiagnosticLog.MinimalLogging || ctxP == null)
            {
                return;
            }
            ctxP.BiomeFailures.TryGetValue(biomeP, out long count);
            ctxP.BiomeFailures[biomeP] = count + 1;
        }

        // Harmony postfix on WorldGenerator.GetBiome. __result is framework-mandated and point is the original method argument.
        public static void CaptureWrongBiome(Vector3 point, Heightmap.Biome __result)
        {
            if (DiagnosticLog.MinimalLogging)
            {
                return;
            }
            int hash = TranspiledCompletionHandler.CurrentInstanceHash;
            if (hash == 0 || GenerationProgress.CurrentLocation == null)
            {
                return;
            }
            if ((GenerationProgress.CurrentLocation.m_biome & __result) != 0)
            {
                return;
            }

            TelemetryContext context = TranspiledCompletionHandler.GetContext(hash);
            if (context == null)
            {
                return;
            }
            context.BiomeFailures.TryGetValue(__result, out long count);
            context.BiomeFailures[__result] = count + 1;
        }

        // like above
        public static void CaptureWrongBiomeArea(Vector3 point, Heightmap.BiomeArea __result)
        {
            if (DiagnosticLog.MinimalLogging)
            {
                return;
            }
            int hash = TranspiledCompletionHandler.CurrentInstanceHash;
            if (hash == 0 || GenerationProgress.CurrentLocation == null)
            {
                return;
            }
            if ((GenerationProgress.CurrentLocation.m_biomeArea & __result) != 0)
            {
                return;
            }

            TelemetryContext context = TranspiledCompletionHandler.GetContext(hash);
            if (context == null)
            {
                return;
            }
            context.BiomeAreaFailures.TryGetValue(__result, out long count);
            context.BiomeAreaFailures[__result] = count + 1;
        }

        public static void IncrementShadow(object instanceP, string fieldNameP)
        {
            if (DiagnosticLog.MinimalLogging)
            {
                return;
            }
            TelemetryContext context = TranspiledCompletionHandler.GetContext(instanceP.GetHashCode());
            if (context == null)
            {
                return;
            }
            context.ShadowCounters.TryGetValue(fieldNameP, out long count);
            context.ShadowCounters[fieldNameP] = count + 1;
        }

        public static void ResetInnerLoopCounter()
        {
            _totalInnerIterations = 0;
            _lastInnerLogValue = 0;
        }

        public static void LogInnerLoopProgress(object instanceP)
        {
            if (!ModConfig.DiagnosticMode.Value)
            {
                return;
            }
            _totalInnerIterations++;
            int interval = ModConfig.InnerProgressInterval.Value;
            if (interval <= 0 || _totalInnerIterations < _lastInnerLogValue + interval)
            {
                return;
            }

            _lastInnerLogValue = _totalInnerIterations;

            ReportData data = TranspiledStateExtractor.Analyze(instanceP);
        }

        public static void LogProgress(object instanceP)
        {
            if (!ModConfig.DiagnosticMode.Value)
            {
                return;
            }
            int interval = ModConfig.ProgressInterval.Value;
            if (interval <= 0)
            {
                return;
            }
            Type type = instanceP.GetType();
            bool hasField = TranspiledEngineFieldCache.CounterFields.TryGetValue(type, out System.Reflection.FieldInfo field);
            if (!hasField)
            {
                return;
            }
            long current = Convert.ToInt64(field.GetValue(instanceP));
            if (current < interval || current % interval != 0)
            {
                return;
            }
            ReportData data = TranspiledStateExtractor.Analyze(instanceP);
        }

        public static void LogLocationStart(ZoneSystem.ZoneLocation locationP, PlacementMode modeP)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[START] Placing [{locationP.m_prefabName}]");

            List<string> reqs = new List<string>();
            if (locationP.m_biome != 0)
            {
                reqs.Add($"{locationP.m_biome}");
            }
            float maxDist = ModConfig.WorldRadius;
            if (locationP.m_maxDistance > 0.1f)
            {
                maxDist = locationP.m_maxDistance;
            }
            if (locationP.m_minDistance > 0 || maxDist < ModConfig.WorldRadius)
            {
                reqs.Add($"Distance: {locationP.m_minDistance:F0}-{maxDist:F0}m");
            }
            if (locationP.m_minAltitude > -1000 || locationP.m_maxAltitude < 10000)
            {
                reqs.Add($"Altitude: {locationP.m_minAltitude:F0}-{locationP.m_maxAltitude:F0}m");
            }
            if (locationP.m_minTerrainDelta > 0 || locationP.m_maxTerrainDelta < 100)
            {
                reqs.Add($"Terrain: {locationP.m_minTerrainDelta:F1}-{locationP.m_maxTerrainDelta:F1}");
            }
            if (locationP.m_inForest)
            {
                reqs.Add($"Forest: {locationP.m_forestTresholdMin:F2}-{locationP.m_forestTresholdMax:F2}");
            }
            if (reqs.Count > 0)
            {
                sb.AppendLine($"       Requires: {string.Join(" | ", reqs)}");
            }

            if (GlobalMaxAltitudeSeen > float.MinValue)
            {
                sb.AppendLine($"       World Altitude: Min {GlobalMinAltitudeSeen:F1}m, Max {GlobalMaxAltitudeSeen:F1}m");
            }

            sb.AppendLine("*****************************************");//this many star hotels are the best.

            DiagnosticLog.WriteTimestampedLog(sb.ToString().TrimEnd());
        }

        public static float GetAnomalyFloor(Heightmap.Biome biomeP)
        {
            switch (biomeP)
            {
                case Heightmap.Biome.Mountain:
                    return 50f;
                case Heightmap.Biome.Plains:
                case Heightmap.Biome.BlackForest:
                case Heightmap.Biome.Meadows:
                case Heightmap.Biome.Swamp:
                    return 1f;
                default:
                    return -10000f;
            }
        }

        /**
        * Shared implementation for both Track variants. Routes a failed altitude
        * check into the appropriate TelemetryContext bucket: TooHigh, Underwater
        * (below 0m), Anomalous (0m to biome-specific floor), or Standard.
        */
        private static void RecordAltitudeFailure(TelemetryContext ctxP, Heightmap.Biome biomeP, float heightP, float minAltP, float maxAltP)
        {
            if (heightP > maxAltP)
            {
                ctxP.AltitudeTooHigh.TryGetValue(biomeP, out long count);
                ctxP.AltitudeTooHigh[biomeP] = count + 1;

                bool hasStats = ctxP.AltHighStats.TryGetValue(biomeP, out AltitudeStat stats);
                if (!hasStats)
                {
                    stats = new AltitudeStat();
                    ctxP.AltHighStats[biomeP] = stats;
                }
                stats.Add(heightP);
            }
            else if (heightP < minAltP)
            {
                if (heightP < 0f)
                {
                    ctxP.AltitudeTooLow_Underwater.TryGetValue(biomeP, out long count);
                    ctxP.AltitudeTooLow_Underwater[biomeP] = count + 1;

                    bool hasStats = ctxP.AltLowStats_Underwater.TryGetValue(biomeP, out AltitudeStat stats);
                    if (!hasStats)
                    {
                        stats = new AltitudeStat();
                        ctxP.AltLowStats_Underwater[biomeP] = stats;
                    }
                    stats.Add(heightP);
                }
                else
                {
                    float anomalyFloor = GetAnomalyFloor(biomeP);
                    if (heightP < anomalyFloor)
                    {
                        ctxP.AltitudeTooLow_Anomalous.TryGetValue(biomeP, out long count);
                        ctxP.AltitudeTooLow_Anomalous[biomeP] = count + 1;

                        bool hasStats = ctxP.AltLowStats_Anomalous.TryGetValue(biomeP, out AltitudeStat stats);
                        if (!hasStats)
                        {
                            stats = new AltitudeStat();
                            ctxP.AltLowStats_Anomalous[biomeP] = stats;
                        }
                        stats.Add(heightP);
                    }
                    else
                    {
                        ctxP.AltitudeTooLow_Standard.TryGetValue(biomeP, out long count);
                        ctxP.AltitudeTooLow_Standard[biomeP] = count + 1;

                        bool hasStats = ctxP.AltLowStats_Standard.TryGetValue(biomeP, out AltitudeStat stats);
                        if (!hasStats)
                        {
                            stats = new AltitudeStat();
                            ctxP.AltLowStats_Standard[biomeP] = stats;
                        }
                        stats.Add(heightP);
                    }
                }
            }
        }
    }
}
