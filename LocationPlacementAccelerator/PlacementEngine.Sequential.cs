// v1
/**
* Sequential (single-threaded) placement path for the replaced engine.
*
* Walks the token list one at a time on the main thread, yielding back
* to Unity periodically so the game doesn't freeze. Each token represents
* one placement attempt for one location type.
*
* RNG isolation: vanilla seeds UnityEngine.Random per-LT then restores
* global state on every yield. I mirror this exactly. The save/restore
* dance around every `yield return null` is not paranoia as without it,
* Unity's frame-to-frame Random calls (particles, weather, etc.) would
* consume numbers from the LT's dart sequence, breaking determinism.
*
* Relaxation: after the main token loop, any location types that failed
* and were re-queued by ConstraintRelaxer get a dedicated relaxation pass.
* Up to _maxRelaxationAttempts passes, each processing only the newly
* appended packets.
* 
* The third part of the god class in a row, but I need those picoseconds.
*/
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    internal static partial class PlacementEngine
    {
        private static IEnumerator RunSequentialPath(ZoneSystem zsP, int locListSnapshotP)
        {
            List<PlacementToken> tokens = BuildTokenList(zsP);

            DiagnosticLog.WriteTimestampedLog(
                $"[LPA] Token list built: {tokens.Count} tokens across {zsP.m_locations.Count} location entries.");

            int yieldCounter = 0;
            const int YieldEvery = 512; // I found this works best. Gives me smooth GUI and max performance.

            Dictionary<string, PlacementCounters> nativeCounters = new Dictionary<string, PlacementCounters>();
            Dictionary<string, int> pendingPackets = new Dictionary<string, int>(Interleaver.PendingPackets);

            /**
            * Per-prefab RNG state for interleaved mode. When interleaving,
            * each prefab's dart sequence must survive across non-contiguous
            * tokens, so I save/restore the Random.State per prefab name.
            */
            Dictionary<string, UnityEngine.Random.State> ltsRngStates = new Dictionary<string, UnityEngine.Random.State>(StringComparer.Ordinal);

            for (int ti = 0; ti < tokens.Count; ti++)
            {
                PlacementToken token = tokens[ti];
                ZoneLocation loc = token.Location;

                bool hasCounter = nativeCounters.TryGetValue(loc.m_prefabName, out PlacementCounters ctr);
                if (!hasCounter)
                {
                    ctr = new PlacementCounters();
                    nativeCounters[loc.m_prefabName] = ctr;
                }

                bool isFirstTokenForThisPrefab = !_rngIsolationActive;
                if (_interleavedScheduling)
                {
                    isFirstTokenForThisPrefab = !ltsRngStates.ContainsKey(loc.m_prefabName);
                }

                if (_interleavedScheduling)
                {
                    if (isFirstTokenForThisPrefab)
                    {
                        _outsideRngState = UnityEngine.Random.state;
                        int ltsSeed = WorldGenerator.instance.GetSeed() + loc.m_prefabName.GetStableHashCode();
                        UnityEngine.Random.InitState(ltsSeed);
                    }
                    else
                    {
                        ltsRngStates.TryGetValue(loc.m_prefabName, out UnityEngine.Random.State savedState);
                        UnityEngine.Random.state = savedState;
                    }
                    _rngIsolationActive = true;
                }
                else if (isFirstTokenForThisPrefab)
                {
                    _outsideRngState = UnityEngine.Random.state;
                    int ltsSeed = WorldGenerator.instance.GetSeed() + loc.m_prefabName.GetStableHashCode();
                    UnityEngine.Random.InitState(ltsSeed);
                    _rngIsolationActive = true;
                }

                if (isFirstTokenForThisPrefab)
                {
                    if (_logSuccesses || ModConfig.DiagnosticMode.Value)
                    {
                        TelemetryHelpers.LogLocationStart(loc, _mode);
                    }
                    bool hasSession = TranspiledCompletionHandler.AggregateSessions.ContainsKey(loc.m_prefabName);
                    if (!hasSession)
                    {
                        TranspiledCompletionHandler.AggregateSessions[loc.m_prefabName] = new TelemetryContext();
                    }
                }

                GenerationProgress.CurrentLocation = loc;

                string group = loc.m_prefabName;
                if (!string.IsNullOrEmpty(loc.m_group))
                {
                    group = loc.m_group;
                }
                PresenceGrid groupGrid = PresenceGrid.GetOrCreate($"{group}:{loc.m_minDistanceFromSimilar:F0}");
                int baseBudget = 100000; 
                if (loc.m_prioritized)
                {
                    baseBudget = 200000; 
                }
                int outerBudget = Interleaver.GetBudget(loc, baseBudget);

                bool placed = false;

                TelemetryContext telCtx = null;
                TranspiledCompletionHandler.AggregateSessions.TryGetValue(loc.m_prefabName, out telCtx);

                for (int outer = 0; outer < outerBudget && !placed; outer++)
                {
                    if (!SurveyMode.GetZone(loc, out Vector2i zoneID))
                    {
                        ctr.ZoneExhausted++;
                        break;
                    }

                    ctr.ZonesExamined++;

                    if (zsP.m_locationInstances.ContainsKey(zoneID))
                    {
                        ctr.ErrOccupied++;
                        if (++yieldCounter >= YieldEvery)
                        {
                            yieldCounter = 0;
                            // Save LTS RNG, restore global so Unity's frame doesn't consume dart numbers.
                            if (_rngIsolationActive)
                            {
                                _insideRngState = UnityEngine.Random.state;
                                UnityEngine.Random.state = _outsideRngState;
                            }
                            yield return null;
                            if (_rngIsolationActive)
                            {
                                _outsideRngState = UnityEngine.Random.state;
                                UnityEngine.Random.state = _insideRngState;
                            }
                        }
                        continue;
                    }

                    placed = EvaluateZone(zsP, loc, zoneID, groupGrid, group, ctr, telCtx);

                    if (++yieldCounter >= YieldEvery)
                    {
                        yieldCounter = 0;
                        if (_generateLocationsProgressField != null && tokens.Count > 0)
                        {
                            _generateLocationsProgressField.SetValue(zsP, (float)(ti + 1) / tokens.Count);//ti is my for loop counter, 3km above.
                        }
                        if (_rngIsolationActive)
                        {
                            _insideRngState = UnityEngine.Random.state;
                            UnityEngine.Random.state = _outsideRngState;
                        }
                        yield return null;
                        if (_rngIsolationActive)
                        {
                            _outsideRngState = UnityEngine.Random.state;
                            UnityEngine.Random.state = _insideRngState;
                        }
                    }
                } //for loop for the ages.

                if (placed)
                {
                    GenerationProgress.IncrementProcessed(true, 1);
                }
                else
                {
                    GenerationProgress.IncrementProcessed(false, 1);
                }

                if (_interleavedScheduling && _rngIsolationActive)
                {
                    ltsRngStates[loc.m_prefabName] = UnityEngine.Random.state;
                }

                bool hasPending = pendingPackets.TryGetValue(loc.m_prefabName, out int remaining);
                if (hasPending)
                {
                    remaining--;
                    pendingPackets[loc.m_prefabName] = remaining;
                    if (remaining <= 0)
                    {
                        pendingPackets.Remove(loc.m_prefabName);
                        if (_rngIsolationActive)
                        {
                            UnityEngine.Random.state = _outsideRngState;
                            _rngIsolationActive = false;
                        }
                        ltsRngStates.Remove(loc.m_prefabName);
                        FlushLTS(zsP, loc, ctr);
                        nativeCounters.Remove(loc.m_prefabName);
                    }
                }
            }

            // Flush any remaining counters (should be empty if pendingPackets was well-formed).
            foreach (KeyValuePair<string, PlacementCounters> kvp in nativeCounters)
            {
                ZoneLocation remainingLoc = null;
                for (int i = 0; i < zsP.m_locations.Count; i++)
                {
                    if (zsP.m_locations[i].m_prefabName == kvp.Key)
                    {
                        remainingLoc = zsP.m_locations[i];
                        break;
                    }
                }
                if (remainingLoc != null)
                {
                    FlushLTS(zsP, remainingLoc, kvp.Value);
                }
            }

            // Relaxation passes: process any packets that ConstraintRelaxer appended to zs.m_locations beyond the original snapshot boundary.
            // Another for loop for the ages.
            for (int relaxPass = 0; relaxPass < _maxRelaxationAttempts; relaxPass++)
            {
                if (zsP.m_locations.Count <= locListSnapshotP)
                {
                    break;
                }

                int newCount = zsP.m_locations.Count - locListSnapshotP;
                List<ZoneLocation> relaxLocs = zsP.m_locations.GetRange(locListSnapshotP, newCount);
                locListSnapshotP = zsP.m_locations.Count;

                DiagnosticLog.WriteTimestampedLog(
                    $"[LPA] Relaxation pass {relaxPass + 1}: processing {newCount} relaxed packet(s).");

                Dictionary<string, PlacementCounters> relaxCtrs = new Dictionary<string, PlacementCounters>(StringComparer.Ordinal);
                Dictionary<string, ZoneLocation> relaxRepLoc = new Dictionary<string, ZoneLocation>(StringComparer.Ordinal);

                foreach (ZoneLocation relaxLoc in relaxLocs) //doh!
                {
                    if (!relaxLoc.m_enable || relaxLoc.m_centerFirst)
                    {
                        continue;
                    }
                    string prefabName = relaxLoc.m_prefabName;
                    bool hasRelaxCtr = relaxCtrs.ContainsKey(prefabName);
                    if (!hasRelaxCtr)
                    {
                        relaxCtrs[prefabName] = new PlacementCounters();
                        relaxRepLoc[prefabName] = relaxLoc;
                        bool hasSession = TranspiledCompletionHandler.AggregateSessions.ContainsKey(prefabName);
                        if (!hasSession)
                        {
                            TranspiledCompletionHandler.AggregateSessions[prefabName] = new TelemetryContext();
                        }
                    }
                    PlacementCounters relaxCtr = relaxCtrs[prefabName];

                    string relaxGroup = prefabName;
                    if (!string.IsNullOrEmpty(relaxLoc.m_group))
                    {
                        relaxGroup = relaxLoc.m_group;
                    }
                    PresenceGrid relaxGrid = PresenceGrid.GetOrCreate($"{relaxGroup}:{relaxLoc.m_minDistanceFromSimilar:F0}");
                    int relaxOuterBudget = _outerBudgetBase;
                    if (relaxLoc.m_prioritized)
                    {
                        relaxOuterBudget = _outerBudgetPrioritized;
                    }

                    int relaxQty = relaxLoc.m_quantity;
                    if (_interleavedScheduling)
                    {
                        relaxQty = 1;
                    }

                    _outsideRngState = UnityEngine.Random.state;
                    int relaxSeed = WorldGenerator.instance.GetSeed() + prefabName.GetStableHashCode();
                    UnityEngine.Random.InitState(relaxSeed);
                    _rngIsolationActive = true;

                    for (int ri = 0; ri < relaxQty; ri++) //doh!
                    {
                        bool placed = false;

                        TelemetryContext relaxTelCtx = null;
                        TranspiledCompletionHandler.AggregateSessions.TryGetValue(prefabName, out relaxTelCtx);

                        for (int outer = 0; outer < relaxOuterBudget && !placed; outer++) //doh!
                        {
                            if (!SurveyMode.GetZone(relaxLoc, out Vector2i zoneID))
                            {
                                relaxCtr.ZoneExhausted++;
                                break;
                            }

                            relaxCtr.ZonesExamined++;

                            if (zsP.m_locationInstances.ContainsKey(zoneID))
                            {
                                relaxCtr.ErrOccupied++;
                                if (++yieldCounter >= YieldEvery)
                                {
                                    yieldCounter = 0;
                                    if (_rngIsolationActive)
                                    {
                                        _insideRngState = UnityEngine.Random.state;
                                        UnityEngine.Random.state = _outsideRngState;
                                    }
                                    yield return null;
                                    if (_rngIsolationActive)
                                    {
                                        _outsideRngState = UnityEngine.Random.state;
                                        UnityEngine.Random.state = _insideRngState;
                                    }
                                }
                                continue;
                            }

                            placed = EvaluateZone(zsP, relaxLoc, zoneID, relaxGrid, relaxGroup, relaxCtr, relaxTelCtx);

                            if (++yieldCounter >= YieldEvery)
                            {
                                yieldCounter = 0;
                                if (_rngIsolationActive)
                                {
                                    _insideRngState = UnityEngine.Random.state;
                                    UnityEngine.Random.state = _outsideRngState;
                                }
                                yield return null;
                                if (_rngIsolationActive)
                                {
                                    _outsideRngState = UnityEngine.Random.state;
                                    UnityEngine.Random.state = _insideRngState;
                                }
                            }
                        }

                        GenerationProgress.IncrementProcessed(placed, 1);
                    }

                    if (_rngIsolationActive)
                    {
                        UnityEngine.Random.state = _outsideRngState;
                        _rngIsolationActive = false;
                    }
                }

                foreach (KeyValuePair<string, PlacementCounters> kvp in relaxCtrs)
                {
                    FlushLTS(zsP, relaxRepLoc[kvp.Key], kvp.Value);
                }
            }

            if (_locationsGeneratedProp != null)
            {
                _locationsGeneratedProp.SetValue(zsP, true);
            }
            else
            {
                DiagnosticLog.WriteLog(
                    "[LPA] WARNING: Could not set LocationsGenerated via reflection. Black screen likely.",
                    BepInEx.Logging.LogLevel.Error);
            }

            SurveyMode.DumpDiagnostics();
            DiagnosticLog.DumpPlacementsToFile();
            GenerationProgress.CurrentLocation = null;
            RelaxationTracker.MarkPlacementComplete();
            GenerationProgress.EndGeneration();
        } // Probably beaten the max quasi bicliques record in absurd method length here ffs. Maybe I should rethink these colossal methods. 

        /**
        * Single-threaded placement of one ZoneLocation.
        * Used by the parallel path's relaxation fallback (RunLocSerial runs on the main thread when inline relaxation can't handle it) and as a general-purpose serial placement utility.
        */
        private static IEnumerator RunLocSerial(ZoneSystem zsP, ZoneLocation locP, PlacementCounters ctrP, int overrideQtyP = -1, bool suppressFlushP = false)
        {
            GenerationProgress.CurrentLocation = locP;

            bool isFirst = !TranspiledCompletionHandler.AggregateSessions.ContainsKey(locP.m_prefabName);
            if (isFirst)
            {
                if (_logSuccesses || ModConfig.DiagnosticMode.Value)
                {
                    TelemetryHelpers.LogLocationStart(locP, _mode);
                }
                TranspiledCompletionHandler.AggregateSessions[locP.m_prefabName] = new TelemetryContext();
            }

            string group = locP.m_prefabName;
            if (!string.IsNullOrEmpty(locP.m_group))
            {
                group = locP.m_group;
            }
            PresenceGrid groupGrid = PresenceGrid.GetOrCreate($"{group}:{locP.m_minDistanceFromSimilar:F0}");
            int baseBudget = 100000;
            if (locP.m_prioritized)
            {
                baseBudget = 200000;
            }
            int outerBudget = Interleaver.GetBudget(locP, baseBudget);

            int tokenCount = locP.m_quantity;
            if (overrideQtyP > 0)
            {
                tokenCount = overrideQtyP;
            }
            if (_interleavedScheduling)
            {
                tokenCount = 1;
            }

            TelemetryContext telCtx = null;
            TranspiledCompletionHandler.AggregateSessions.TryGetValue(locP.m_prefabName, out telCtx);

            int yieldCounter = 0;
            const int YieldEvery = 512;

            _outsideRngState = UnityEngine.Random.state;
            int ltsSeed = WorldGenerator.instance.GetSeed() + locP.m_prefabName.GetStableHashCode();
            UnityEngine.Random.InitState(ltsSeed);
            _rngIsolationActive = true;

            for (int ti = 0; ti < tokenCount; ti++)
            {
                bool placed = false;

                for (int outer = 0; outer < outerBudget && !placed; outer++)
                {
                    if (!SurveyMode.GetZone(locP, out Vector2i zoneID))
                    {
                        ctrP.ZoneExhausted++;
                        break;
                    }

                    ctrP.ZonesExamined++;

                    if (zsP.m_locationInstances.ContainsKey(zoneID))
                    {
                        ctrP.ErrOccupied++;
                        if (++yieldCounter >= YieldEvery)
                        {
                            yieldCounter = 0;
                            if (_rngIsolationActive)
                            {
                                _insideRngState = UnityEngine.Random.state;
                                UnityEngine.Random.state = _outsideRngState;
                            }
                            yield return null;
                            if (_rngIsolationActive)
                            {
                                _outsideRngState = UnityEngine.Random.state;
                                UnityEngine.Random.state = _insideRngState;
                            }
                        }
                        continue;
                    }

                    placed = EvaluateZone(zsP, locP, zoneID, groupGrid, group, ctrP, telCtx);

                    if (++yieldCounter >= YieldEvery)
                    {
                        yieldCounter = 0;
                        if (_rngIsolationActive)
                        {
                            _insideRngState = UnityEngine.Random.state;
                            UnityEngine.Random.state = _outsideRngState;
                        }
                        yield return null;
                        if (_rngIsolationActive)
                        {
                            _outsideRngState = UnityEngine.Random.state;
                            UnityEngine.Random.state = _insideRngState;
                        }
                    }
                }

                GenerationProgress.IncrementProcessed(placed, 1);
            }

            if (_rngIsolationActive)
            {
                UnityEngine.Random.state = _outsideRngState;
                _rngIsolationActive = false;
            }
            if (!suppressFlushP)
            {
                FlushLTS(zsP, locP, ctrP);
                TranspiledCompletionHandler.AggregateSessions.Remove(locP.m_prefabName);
            }
        }
    }
}
