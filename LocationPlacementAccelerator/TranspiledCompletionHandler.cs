// v1
/**
* Handles transpiled-engine packet completion: aggregates per-location telemetry,
* determines success/failure, triggers relaxation, and writes final reports.
*
* ActiveSessions: per-coroutine-instance --> TelemetryContext (live during placement).
* AggregateSessions: per-prefab --> merged TelemetryContext (survives across packets).
* AggregateReports: per-prefab --> merged ReportData (survives across packets).
*
* Also holds transpiled-engine pipeline bookkeeping state:
*   CurrentInstanceHash: set by OuterLoopPrefix, read by CaptureWrongBiome/Area.
*   CachedOccupiedZone:  set on success, read by survey zone marking.
*   
* 1.0.1: Passed priority context into RelaxationTracker for UI severity grading.
*/
#nullable disable
using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class TranspiledCompletionHandler
    {
        public static Dictionary<int, TelemetryContext> ActiveSessions = new Dictionary<int, TelemetryContext>();
        public static Dictionary<string, TelemetryContext> AggregateSessions = new Dictionary<string, TelemetryContext>();
        public static Dictionary<string, ReportData> AggregateReports = new Dictionary<string, ReportData>();

        public static int CurrentInstanceHash = 0;
        public static Vector2i? CachedOccupiedZone = null;

        public static TelemetryContext GetContext(int instanceHashP, bool createIfMissingP = true)
        {
            bool found = ActiveSessions.TryGetValue(instanceHashP, out TelemetryContext context);
            if (found)
            {
                return context;
            }

            if (createIfMissingP)
            {
                TelemetryContext newContext = new TelemetryContext();
                ActiveSessions[instanceHashP] = newContext;
                return newContext;
            }
            return null;
        }

        public static void ReportPacketCompletion(object instanceP, bool isSuccessP)
        {
            if (isSuccessP)
            {
                SurveyMode.NotifyZonePlaced();
            }

            ZoneLocation loc = TranspiledEngineFieldCache.GetLocation(instanceP);
            if (loc == null)
            {
                return;
            }
            string prefab = loc.m_prefabName;
            Type type = instanceP.GetType();

            if (isSuccessP)
            {
                bool hasZoneField = TranspiledEngineFieldCache.ZoneIDFields.TryGetValue(type, out System.Reflection.FieldInfo zField);
                if (CachedOccupiedZone == null && hasZoneField)
                {
                    CachedOccupiedZone = (Vector2i)zField.GetValue(instanceP);
                }
            }

            int overridePlaced = -1;
            if (ModConfig.EnableInterleavedScheduling.Value)
            {
                overridePlaced = 1;
            }
            ReportData data = TranspiledStateExtractor.Analyze(instanceP, overridePlaced);

            int amountToDecrement = 1;
            if (!ModConfig.EnableInterleavedScheduling.Value)
            {
                if (!isSuccessP && data != null)
                {
                    amountToDecrement = Math.Max(1, data.Loc.m_quantity - data.Placed);
                }
            }

            GenerationProgress.IncrementProcessed(isSuccessP, amountToDecrement);

            if (data != null)
            {
                if (!ModConfig.EnableInterleavedScheduling.Value)
                {
                    AggregateReports[prefab] = data;
                }
                else
                {
                    bool hasExistingReport = AggregateReports.TryGetValue(prefab, out ReportData existingReport);
                    if (!hasExistingReport)
                    {
                        AggregateReports[prefab] = data;
                    }
                    else
                    {
                        existingReport.Merge(data);
                    }
                }
            }

            int contextHash = instanceP.GetHashCode();
            if (data != null)
            {
                contextHash = data.InstanceHash;
            }
            TelemetryContext ctx = GetContext(contextHash, false);
            if (ctx != null)
            {
                if (!ModConfig.EnableInterleavedScheduling.Value)
                {
                    AggregateSessions[prefab] = ctx;
                }
                else
                {
                    bool hasExistingSession = AggregateSessions.TryGetValue(prefab, out TelemetryContext existingSession);
                    if (!hasExistingSession)
                    {
                        existingSession = new TelemetryContext();
                        AggregateSessions[prefab] = existingSession;
                    }
                    existingSession.Merge(ctx);
                }
            }

            bool isFinished = false;
            bool hasPending = Interleaver.PendingPackets.TryGetValue(prefab, out int pendingCount);
            if (hasPending)
            {
                Interleaver.PendingPackets[prefab] = pendingCount - amountToDecrement;
                if (pendingCount - amountToDecrement <= 0)
                {
                    isFinished = true;
                }
            }
            else
            {
                isFinished = true;
            }

            if (isFinished || ModConfig.EnableInterleavedScheduling.Value)
            {
                ActiveSessions.Remove(instanceP.GetHashCode());
                CurrentInstanceHash = 0;
            }

            if (isFinished)
            {
                bool hasAggData = AggregateReports.TryGetValue(prefab, out ReportData aggData);
                if (!hasAggData)
                {
                    aggData = data;
                }

                int globalPlaced = 0;
                if (ZoneSystem.instance != null)
                {
                    foreach (LocationInstance inst in ZoneSystem.instance.m_locationInstances.Values)
                    {
                        if (inst.m_location.m_prefabName == prefab)
                        {
                            globalPlaced++;
                        }
                    }
                }

                int origQty = Interleaver.GetOriginalQuantity(prefab);

                if (aggData != null)
                {
                    aggData.Placed = globalPlaced;
                    aggData.IsComplete = globalPlaced >= origQty;
                    aggData.Loc.m_quantity = origQty;

                    bool wasRelaxed = ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefab, out int relaxCount) && relaxCount > 0;

                    if (aggData.IsComplete)
                    {
                        if (wasRelaxed)
                        {
                            RelaxationTracker.MarkRelaxationSucceeded(prefab);
                        }
                        if (!DiagnosticLog.MinimalLogging && (ModConfig.LogSuccesses.Value || wasRelaxed))
                        {
                            if (wasRelaxed)
                            {
                                DiagnosticLog.WriteTimestampedLog($"[RELAXATION SUCCESS] {prefab} placed {globalPlaced}/{origQty} after {relaxCount} relaxation(s). {ConstraintRelaxer.GetRelaxationSummary(prefab, loc)}", LogLevel.Message);
                            }
                            ReportFormatter.WriteReport(aggData, false, prefab);
                        }
                    }
                    else
                    {
                        if (!DiagnosticLog.MinimalLogging)
                        {
                            ReportFormatter.WriteReport(aggData, false, prefab);
                        }

                        if (ConstraintRelaxer.TryRelax(aggData))
                        {
                            GenerationProgress.UpdateText();
                        }
                        else
                        {
                            RelaxationTracker.CheckAndMarkFailed(prefab, globalPlaced, origQty, aggData.Loc.m_prioritized);
                        }
                    }
                }

                AggregateReports.Remove(prefab);
                AggregateSessions.Remove(prefab);
            }
        }

        public static void ReportSuccess(object instanceP)
        {
            ReportPacketCompletion(instanceP, true);
        }

        public static void ReportFailure(object instanceP)
        {
            ReportPacketCompletion(instanceP, false);
        }
    }
}