// v1
/**
* Formats placement reports for the diagnostic log. Produces both heartbeat
* (mid-placement progress snapshots) and full funnel reports (post-placement
* analysis showing each filter stage's pass/fail counts).
*
* The funnel report walks the vanilla placement filter chain in order:
* Distance --> Biome --> Altitude --> Forest --> Similarity --> Terrain --> Vegetation
* and shows how many darts survived each stage.
* 
*/
#nullable disable
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LPA
{
    public static class ReportFormatter
    {
        private class FunnelStep
        {
            public string Name;
            public string ConfigInfo;
            public string PassedContext;
            public long Input;
            public long Failures;
            public Action<StringBuilder, string, TelemetryContext> FailurePrinter;

            public long Passed
            {
                get
                {
                    return Input - Failures;
                }
            }
        }

        public static void WriteReport(ReportData dataP, bool isHeartbeatP, string prefabNameP = null, HeartbeatType heartbeatTypeP = HeartbeatType.Inner)
        {
            if (dataP == null)
            {
                return;
            }
            string name = prefabNameP ?? dataP.Loc.m_prefabName;
            if (isHeartbeatP)
            {
                LogHeartbeat(dataP, name, heartbeatTypeP);
            }
            else
            {
                LogFullReport(dataP, name);
            }
        }

        private static void LogHeartbeat(ReportData dataP, string prefabNameP, HeartbeatType typeP)
        {
            bool hasContext = TranspiledCompletionHandler.AggregateSessions.TryGetValue(prefabNameP, out TelemetryContext context);
            if (!hasContext)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine();

            string prefix = typeP == HeartbeatType.Outer ? "[PROGRESS-OUTER]" : "[PROGRESS-INNER]";
            sb.AppendLine($"{prefix} {prefabNameP}: {dataP.Placed}/{dataP.OriginalQuantity}. Cost: {dataP.CurrentOuter:N0}/{dataP.LimitOuter:N0}");

            if (TelemetryHelpers.GlobalMaxAltitudeSeen > float.MinValue)
            {
                sb.AppendLine($"           (World Altitude Profile: Min {TelemetryHelpers.GlobalMinAltitudeSeen:F1}m, Max {TelemetryHelpers.GlobalMaxAltitudeSeen:F1}m)");
            }

            if (dataP.ErrZone > 0 || dataP.ErrArea > 0)
            {
                sb.AppendLine("           PHASE 1 FAILURES (Zone Search):");
                if (dataP.ErrZone > 0)
                {
                    sb.AppendLine($"                  - Zone Occupied            : {dataP.ErrZone,12:N0}");
                }
                if (dataP.ErrArea > 0)
                {
                    sb.AppendLine($"                  - Wrong Biome Area         : {dataP.ErrArea,12:N0}");
                    PrintDict(sb, "                     └─ ", context.BiomeAreaFailures);
                }
            }

            sb.AppendLine("           PHASE 2 FAILURES (Placement Filters):");

            List<FailureEntry> failures = new List<FailureEntry>();
            if (dataP.ErrDist > 0)
            {
                failures.Add(new FailureEntry("Distance Filter", dataP.ErrDist, (StringBuilder sP, string padP, TelemetryContext ctxP) => PrintDist(sP, padP, ctxP)));
            }
            if (dataP.ErrBiome > 0)
            {
                failures.Add(new FailureEntry("Wrong Biome Type", dataP.ErrBiome, (StringBuilder sP, string padP, TelemetryContext ctxP) => PrintDict(sP, padP, ctxP.BiomeFailures)));
            }
            if (dataP.ErrAlt > 0)
            {
                failures.Add(new FailureEntry("Wrong Altitude", dataP.ErrAlt, (StringBuilder sP, string padP, TelemetryContext ctxP) => PrintAlt(sP, "                     ", ctxP)));
            }
            if (dataP.ErrForest > 0)
            {
                failures.Add(new FailureEntry("Forest Check", dataP.ErrForest, null));
            }
            if (dataP.ErrTerrain > 0)
            {
                failures.Add(new FailureEntry("Terrain Check", dataP.ErrTerrain, null));
            }
            if (dataP.ErrSim + dataP.ErrNotSim > 0)
            {
                failures.Add(new FailureEntry("Similarity Check", dataP.ErrSim + dataP.ErrNotSim, null));
            }
            if (dataP.ErrVeg > 0)
            {
                failures.Add(new FailureEntry("Vegetation Density", dataP.ErrVeg, null));
            }

            failures.Sort((FailureEntry aP, FailureEntry bP) => bP.Count.CompareTo(aP.Count));
            int showCount = Math.Min(5, failures.Count);
            for (int i = 0; i < showCount; i++)
            {
                FailureEntry fail = failures[i];
                sb.AppendLine($"                  - {fail.Name.PadRight(25)}: {fail.Count,12:N0}");
                fail.DetailsPrinter?.Invoke(sb, "                     └─ ", context);
            }

            if (typeP == HeartbeatType.Outer)
            {
                sb.AppendLine("─────────────────────────────────────────────────────────");
            }

            DiagnosticLog.WriteTimestampedLog(sb.ToString().TrimEnd());
        }

        private static void LogFullReport(ReportData dataP, string prefabNameP)
        {
            bool hasContext = TranspiledCompletionHandler.AggregateSessions.TryGetValue(prefabNameP, out TelemetryContext context);
            if (!hasContext)
            {
                context = new TelemetryContext();
            }

            StringBuilder report = new StringBuilder();
            string status;
            LogLevel level;
            if (dataP.IsComplete)
            {
                status = "COMPLETE";
                level = LogLevel.Info;
            }
            else
            {
                bool wasRelaxed = ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefabNameP, out int ra) && ra > 0;
                int minNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefabNameP, dataP.OriginalQuantity);
                if (wasRelaxed && dataP.Placed >= minNeeded)
                {
                    status = "RELAXED";
                    level = LogLevel.Message;
                }
                else
                {
                    status = "FAILURE";
                    level = LogLevel.Warning;
                }
            }

            report.AppendLine($"[{status}] {prefabNameP}: {dataP.Placed}/{dataP.OriginalQuantity}. Cost: {dataP.CurrentOuter:N0}/{dataP.LimitOuter:N0} outer loop budget and {dataP.InDist:N0} inner loop iterations.");
            string relaxSummary = ConstraintRelaxer.GetRelaxationSummary(prefabNameP, dataP.Loc);
            if (!string.IsNullOrEmpty(relaxSummary))
            {
                report.AppendLine($"           {relaxSummary}");
            }
            if (TelemetryHelpers.GlobalMaxAltitudeSeen > float.MinValue)
            {
                report.AppendLine($"(World Altitude Profile: Min {TelemetryHelpers.GlobalMinAltitudeSeen:F1}m, Max {TelemetryHelpers.GlobalMaxAltitudeSeen:F1}m)");
            }
            report.AppendLine("────────────────────────────────────────────────────────");

            report.AppendLine($"PHASE 1 (Zone Search): {dataP.CurrentOuter:N0} Checks");
            if (dataP.ErrZone > 0)
            {
                report.AppendLine($"[x] Occupied Zones: {dataP.ErrZone:N0}");
            }
            if (dataP.ErrArea > 0)
            {
                report.AppendLine($"[x] Wrong Biome Area: {dataP.ErrArea:N0}");
                PrintDict(report, "    └─ ", context.BiomeAreaFailures);
            }
            string zoneAreaName = dataP.Loc.m_biomeArea.ToString();
            report.AppendLine($"[!] Valid Zones: {dataP.ValidZones:N0}");
            report.AppendLine($"    └─ {zoneAreaName}");

            if (dataP.ValidZones <= 0 || dataP.InDist <= 0)
            {
                report.AppendLine("────────────────────────────────────────────────────────");
                DiagnosticLog.WriteTimestampedLog(report.ToString(), level);
                return;
            }

            report.AppendLine();
            report.AppendLine($"PHASE 2 (Placement): {dataP.InDist:N0} Points Sampled in the {dataP.ValidZones:N0} {zoneAreaName} zones");
            List<FunnelStep> steps = new List<FunnelStep>();
            float effectiveMax = ModConfig.WorldRadius;
            if (dataP.Loc.m_maxDistance > 0.1f)
            {
                effectiveMax = dataP.Loc.m_maxDistance;
            }

            steps.Add(new FunnelStep { Name = "DISTANCE FILTER", ConfigInfo = $"(Min: {dataP.Loc.m_minDistance:F0}, Max: {effectiveMax:F0})", PassedContext = $"Range {dataP.Loc.m_minDistance:F0}-{effectiveMax:F0}", Input = dataP.InDist, Failures = dataP.ErrDist, FailurePrinter = (StringBuilder sP, string indentP, TelemetryContext ctxP) => PrintDist(sP, indentP, ctxP) });
            steps.Add(new FunnelStep { Name = "BIOME MATCH", ConfigInfo = $"(Required: {dataP.Loc.m_biome})", PassedContext = $"{dataP.Loc.m_biome}", Input = dataP.InBiome, Failures = dataP.ErrBiome, FailurePrinter = (StringBuilder sP, string indentP, TelemetryContext ctxP) => PrintDict(sP, $"{indentP}    └─ ", ctxP.BiomeFailures) });

            FunnelStep altStep = new FunnelStep { Name = "ALTITUDE CHECK", ConfigInfo = $"(Min: {dataP.Loc.m_minAltitude:F0}, Max: {dataP.Loc.m_maxAltitude:F0})", PassedContext = $"Alt {dataP.Loc.m_minAltitude:F0} to {dataP.Loc.m_maxAltitude:F0}", Input = dataP.InAlt, Failures = dataP.ErrAlt, FailurePrinter = (StringBuilder sP, string indentP, TelemetryContext ctxP) => PrintAlt(sP, $"{indentP}    ", ctxP) };
            FunnelStep terrStep = new FunnelStep { Name = "TERRAIN DELTA", ConfigInfo = $"(Min: {dataP.Loc.m_minTerrainDelta:F1}, Max: {dataP.Loc.m_maxTerrainDelta:F1})", PassedContext = $"Delta {dataP.Loc.m_minTerrainDelta:F1} to {dataP.Loc.m_maxTerrainDelta:F1}", Input = dataP.InTerr, Failures = dataP.ErrTerrain, FailurePrinter = (StringBuilder sP, string indentP, TelemetryContext ctxP) => sP.AppendLine($"{indentP}    └─ Slope/Flatness mismatch: {dataP.ErrTerrain:N0}") };
            string groupName = string.IsNullOrEmpty(dataP.Loc.m_group) ? "Default" : dataP.Loc.m_group;
            FunnelStep simStep = new FunnelStep { Name = "SIMILARITY CHECK", ConfigInfo = $"(Group: {groupName})", PassedContext = "Proximity Clear", Input = dataP.InSim, Failures = dataP.ErrSim + dataP.ErrNotSim, FailurePrinter = (StringBuilder sP, string indentP, TelemetryContext ctxP) => { if (dataP.ErrSim > 0) { sP.AppendLine($"{indentP}    └─ Too Close: {dataP.ErrSim:N0}"); } if (dataP.ErrNotSim > 0) { sP.AppendLine($"{indentP}    └─ Too Far: {dataP.ErrNotSim:N0}"); } } };

            FunnelStep forestStep = null;
            if (dataP.Loc.m_inForest)
            {
                forestStep = new FunnelStep { Name = "FOREST FACTOR", ConfigInfo = $"(Min: {dataP.Loc.m_forestTresholdMin:F2}, Max: {dataP.Loc.m_forestTresholdMax:F2})", PassedContext = $"Forest {dataP.Loc.m_forestTresholdMin:F2}-{dataP.Loc.m_forestTresholdMax:F2}", Input = dataP.InForest, Failures = dataP.ErrForest };
            }

            steps.Add(altStep);
            if (forestStep != null)
            {
                steps.Add(forestStep);
            }
            steps.Add(simStep);
            steps.Add(terrStep);

            steps.Add(new FunnelStep { Name = "VEGETATION DENSITY", ConfigInfo = $"(Min: {dataP.Loc.m_minimumVegetation:F2}, Max: {dataP.Loc.m_maximumVegetation:F2})", PassedContext = "Density Match", Input = dataP.InVeg, Failures = dataP.ErrVeg });

            string indent = "";
            for (int i = 0; i < steps.Count; i++)
            {
                FunnelStep step = steps[i];
                if (i > 0 && steps[i - 1].Passed == 0)
                {
                    break;
                }

                bool allFuturePerfect = true;
                for (int k = i; k < steps.Count; k++)
                {
                    if (steps[k].Failures != 0)
                    {
                        allFuturePerfect = false;
                        break;
                    }
                }

                if (allFuturePerfect)
                {
                    StringBuilder joinedNames = new StringBuilder();
                    for (int k = i; k < steps.Count; k++)
                    {
                        if (k > i)
                        {
                            joinedNames.Append(" -> ");
                        }
                        joinedNames.Append(steps[k].Name.Replace(" CHECK", "").Replace(" FILTER", "").Replace(" MATCH", ""));
                    }
                    report.AppendLine($"{indent}└─ PASSED REMAINING CHECKS ({joinedNames}): {step.Passed:N0}");
                    break;
                }

                if (i == 0)
                {
                    report.AppendLine($"1. {step.Name} {step.ConfigInfo}");
                }
                else
                {
                    report.AppendLine($"{indent}└─ {i + 1}. {step.Name} {step.ConfigInfo}: {step.Input:N0} points checked");
                }

                string statusIndent = (i == 0) ? "" : indent + "   ";

                if (step.Failures > 0)
                {
                    report.AppendLine($"{statusIndent}[x] Failed: {step.Failures:N0}");
                    step.FailurePrinter?.Invoke(report, statusIndent, context);
                }

                if (step.Passed > 0)
                {
                    report.AppendLine($"{statusIndent}[!] Passed: {step.Passed:N0}");
                    report.AppendLine($"{statusIndent}    └─ {step.PassedContext}");
                    indent += "       ";

                    bool isLastStep = (i == steps.Count - 1);
                    bool nextWillBePerfect = false;
                    if (!isLastStep)
                    {
                        nextWillBePerfect = true;
                        for (int k = i + 1; k < steps.Count; k++)
                        {
                            if (steps[k].Failures != 0)
                            {
                                nextWillBePerfect = false;
                                break;
                            }
                        }
                    }
                    if (!isLastStep && !nextWillBePerfect)
                    {
                        report.AppendLine($"{indent}|");
                    }
                }
            }
            report.AppendLine("────────────────────────────────────────────────────────");
            DiagnosticLog.WriteTimestampedLog(report.ToString(), level);
        }

        private static void PrintDict<T>(StringBuilder sbP, string prefixP, Dictionary<T, long> dictP)
        {
            if (dictP == null)
            {
                return;
            }
            List<KeyValuePair<T, long>> sorted = new List<KeyValuePair<T, long>>(dictP);
            sorted.Sort((KeyValuePair<T, long> aP, KeyValuePair<T, long> bP) => bP.Value.CompareTo(aP.Value));
            int showCount = Math.Min(5, sorted.Count);
            for (int i = 0; i < showCount; i++)
            {
                sbP.AppendLine($"{prefixP}{sorted[i].Key}: {sorted[i].Value:N0}");
            }
        }

        private static void PrintDist(StringBuilder sbP, string prefixP, TelemetryContext contextP)
        {
            if (contextP.DistanceTooClose > 0)
            {
                sbP.AppendLine($"{prefixP}Below Min: {contextP.DistanceTooClose:N0}");
            }
            if (contextP.DistanceTooFar > 0)
            {
                sbP.AppendLine($"{prefixP}Above Max: {contextP.DistanceTooFar:N0}");
            }
        }

        private static void PrintAlt(StringBuilder sbP, string prefixP, TelemetryContext contextP)
        {
            HashSet<Heightmap.Biome> allBiomes = new HashSet<Heightmap.Biome>();
            foreach (Heightmap.Biome b in contextP.AltLowStats_Standard.Keys)
            {
                allBiomes.Add(b);
            }
            foreach (Heightmap.Biome b in contextP.AltLowStats_Anomalous.Keys)
            {
                allBiomes.Add(b);
            }
            foreach (Heightmap.Biome b in contextP.AltLowStats_Underwater.Keys)
            {
                allBiomes.Add(b);
            }
            foreach (Heightmap.Biome b in contextP.AltHighStats.Keys)
            {
                allBiomes.Add(b);
            }

            long totalLow = 0;
            foreach (long val in contextP.AltitudeTooLow_Standard.Values)
            {
                totalLow += val;
            }
            foreach (long val in contextP.AltitudeTooLow_Anomalous.Values)
            {
                totalLow += val;
            }
            foreach (long val in contextP.AltitudeTooLow_Underwater.Values)
            {
                totalLow += val;
            }

            if (totalLow > 0)
            {
                sbP.AppendLine($"{prefixP}└─ Too Low: {totalLow:N0}");

                List<Heightmap.Biome> sortedBiomes = new List<Heightmap.Biome>(allBiomes);
                sortedBiomes.Sort((Heightmap.Biome aP, Heightmap.Biome bP) => string.Compare(aP.ToString(), bP.ToString(), StringComparison.Ordinal));

                foreach (Heightmap.Biome biome in sortedBiomes)
                {
                    bool hasWater = contextP.AltitudeTooLow_Underwater.TryGetValue(biome, out long waterVal);
                    bool hasAnom = contextP.AltitudeTooLow_Anomalous.TryGetValue(biome, out long anomVal);
                    bool hasStd = contextP.AltitudeTooLow_Standard.TryGetValue(biome, out long stdVal);
                    if (!hasWater && !hasAnom && !hasStd)
                    {
                        continue;
                    }

                    sbP.AppendLine($"{prefixP}   └─ {biome}:");
                    if (hasWater)
                    {
                        string stats = contextP.AltLowStats_Underwater[biome].GetString();
                        string lineEnd = (hasAnom || hasStd) ? "├─" : "└─";
                        sbP.AppendLine($"{prefixP}      {lineEnd} Underwater (<0m): {waterVal:N0} {stats}");
                    }
                    if (hasAnom)
                    {
                        string stats = contextP.AltLowStats_Anomalous[biome].GetString();
                        float floor = TelemetryHelpers.GetAnomalyFloor(biome);
                        string lineEnd = hasStd ? "├─" : "└─";
                        sbP.AppendLine($"{prefixP}      {lineEnd} Anomalous (0m to {floor:F0}m): {anomVal:N0} {stats}");
                    }
                    if (hasStd)
                    {
                        string stats = contextP.AltLowStats_Standard[biome].GetString();
                        sbP.AppendLine($"{prefixP}      └─ Standard Failures: {stdVal:N0} {stats}");
                    }
                }
            }

            long totalHigh = 0;
            foreach (long val in contextP.AltitudeTooHigh.Values)
            {
                totalHigh += val;
            }

            if (totalHigh > 0)
            {
                sbP.AppendLine($"{prefixP}└─ Too High: {totalHigh:N0}");

                List<KeyValuePair<Heightmap.Biome, long>> sortedHigh = new List<KeyValuePair<Heightmap.Biome, long>>(contextP.AltitudeTooHigh);
                sortedHigh.Sort((KeyValuePair<Heightmap.Biome, long> aP, KeyValuePair<Heightmap.Biome, long> bP) => bP.Value.CompareTo(aP.Value));

                foreach (KeyValuePair<Heightmap.Biome, long> kvp in sortedHigh)
                {
                    string stats = "";
                    bool hasHighStats = contextP.AltHighStats.TryGetValue(kvp.Key, out AltitudeStat altStat);
                    if (hasHighStats)
                    {
                        stats = altStat.GetString();
                    }
                    sbP.AppendLine($"{prefixP}   └─ {kvp.Key}: {kvp.Value:N0} {stats}");
                }
            }
        }

        // Used only by LogHeartbeat to hold failure entries for sorting.
        private struct FailureEntry
        {
            public string Name;
            public long Count;
            public Action<StringBuilder, string, TelemetryContext> DetailsPrinter;

            public FailureEntry(string nameP, long countP, Action<StringBuilder, string, TelemetryContext> detailsPrinterP)
            {
                Name = nameP;
                Count = countP;
                DetailsPrinter = detailsPrinterP;
            }
        }
    }
}
