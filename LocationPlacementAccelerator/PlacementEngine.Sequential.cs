// v1.6
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
*
* 1.1: API gates at the bottom for LocationsGenerated.
* 1.2: Pass ApiState.IsApiRun straight through to EndGeneration. The
* world-gen-only cleanups (m_locations restore, full surveyor reset,
* relax-quantity restore) are  inside EndGeneration itself.
* The * overlay teardown and summary log run in both paths now and 
* I am not left with the GUI staring at me.
*
* 1.3: Multi-group similarity as per 1.64 EWD. Each of the three EvaluateZone sites used to build a
* single (group, radius) grid off m_group-or-prefab and hand it in. They now resolve the location's full membership list 
* via ResolveSimilarityMemberships (Core), so a multi-group location is checked against, and commits into, 
* every real group it belongs to. Single-group and ungrouped locations resolve to a one-element list, so this path is unchanged for them.
*
* 1.4: Per-token accounting keyed on the logical type key (Interleaver.GetTypeKey). 
* Distinct  EWD clones share a prefab but are different types, so nativeCounters, the per-type RNG isolation (ltsRngStates + the InitState seed), 
* the pendingPackets bookkeeping, and the relaxation pass's relaxCtrs/relaxRepLoc (and its seed) all key per type, otherwise a second
* clone's packet would read the first's pending count and share its counter. 
* The seed deriving from the type key decorrelates clone dart sequences while leaving non-clone worlds bit-identical
* (the type key equals the prefab name there). AggregateSessions stays prefab-keyed (for the telemetry).
*
* 1.5: Each of the three EvaluateZone sites now also resolves the location's max advertise and search membership
* sets (Core.ResolveMaxAdvertise/SearchMemberships) and hands them in, so the sequential path enforces
* maxDistanceFromSimilar / anchors identically to Core. Ordinary and single-group locations resolve to empty max
* sets, so this path is unchanged for them.
* 
* 1.6: The relaxation pass now runs tier by tier. A host that only succeeds during relaxation still has to be placed
* before its satellites retry, otherwise a satellite relaxes against a grid the host has not painted yet and fails for
* no real reason. I bucket the appended relaxation entries by anchor tier and process low tiers first, keeping append
* order within a tier so the pass stays deterministic. Tier ordering of the main pass itself lives in the sequential
* sort (Core.CompareSequentialSortEntries), this covers only the relaxation tail.
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
                /**
                 * Sp per logical type: distinct clones sharing a prefab get their own counter, RNG isolation, and packet bookkeeping. 
                 * A packet inherits its origin's type key, so all packets of one type group together. 
                 * AggregateSessions again stays prefab-keyed for the telemetry.
                */
                string typeKey = Interleaver.GetTypeKey(loc);

                bool hasCounter = nativeCounters.TryGetValue(typeKey, out PlacementCounters ctr);
                if (!hasCounter)
                {
                    ctr = new PlacementCounters();
                    nativeCounters[typeKey] = ctr;
                }

                bool isFirstTokenForThisType = !_rngIsolationActive;
                if (_interleavedScheduling)
                {
                    isFirstTokenForThisType = !ltsRngStates.ContainsKey(typeKey);
                }

                if (_interleavedScheduling)
                {
                    if (isFirstTokenForThisType)
                    {
                        _outsideRngState = UnityEngine.Random.state;
                        int ltsSeed = WorldGenerator.instance.GetSeed() + typeKey.GetStableHashCode();
                        UnityEngine.Random.InitState(ltsSeed);
                    }
                    else
                    {
                        ltsRngStates.TryGetValue(typeKey, out UnityEngine.Random.State savedState);
                        UnityEngine.Random.state = savedState;
                    }
                    _rngIsolationActive = true;
                }
                else if (isFirstTokenForThisType)
                {
                    _outsideRngState = UnityEngine.Random.state;
                    int ltsSeed = WorldGenerator.instance.GetSeed() + typeKey.GetStableHashCode();
                    UnityEngine.Random.InitState(ltsSeed);
                    _rngIsolationActive = true;
                }

                if (isFirstTokenForThisType)
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

                List<GroupMembership> memberships = ResolveSimilarityMemberships(loc);
                List<GroupMembership> maxAdvertise = ResolveMaxAdvertiseMemberships(loc);
                List<GroupMembership> maxSearch = ResolveMaxSearchMemberships(loc);
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

                    placed = EvaluateZone(zsP, loc, zoneID, memberships, maxAdvertise, maxSearch, ctr, telCtx);

                    if (++yieldCounter >= YieldEvery)
                    {
                        yieldCounter = 0;
                        if (_generateLocationsProgressField != null && tokens.Count > 0)
                        {
                            _generateLocationsProgressField.SetValue(zsP, (float)(ti + 1) / tokens.Count);//ti is my for loop counter, 3k lines above.
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
                    ltsRngStates[typeKey] = UnityEngine.Random.state;
                }

                bool hasPending = pendingPackets.TryGetValue(typeKey, out int remaining);
                if (hasPending)
                {
                    remaining--;
                    pendingPackets[typeKey] = remaining;
                    if (remaining <= 0)
                    {
                        pendingPackets.Remove(typeKey);
                        if (_rngIsolationActive)
                        {
                            UnityEngine.Random.state = _outsideRngState;
                            _rngIsolationActive = false;
                        }
                        ltsRngStates.Remove(typeKey);
                        FlushLTS(zsP, loc, ctr);
                        nativeCounters.Remove(typeKey);
                    }
                }
            }

            // Flush any remaining counters (should be empty if pendingPackets was well-formed).
            foreach (KeyValuePair<string, PlacementCounters> kvp in nativeCounters)
            {
                ZoneLocation remainingLoc = null;
                for (int i = 0; i < zsP.m_locations.Count; i++)
                {
                    if (Interleaver.GetTypeKey(zsP.m_locations[i]) == kvp.Key)
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

                /**
                * Relaxation must respect tiers as well: a host that only recovers here has to be placed before its satellites
                * retry, or the satellites relax against a grid the host has not painted. I bucket the appended entries by tier
                * and walk low tiers first, preserving append order within a tier so the pass is deterministic.
                */ 
                int maxRelaxTier = 0;
                for (int ri = 0; ri < relaxLocs.Count; ri++)
                {
                    int rt = TierOf(relaxLocs[ri]);
                    if (rt > maxRelaxTier)
                    {
                        maxRelaxTier = rt;
                    }
                }
                if (maxRelaxTier > 0)
                {
                    List<ZoneLocation> tierOrdered = new List<ZoneLocation>(relaxLocs.Count);
                    for (int rtier = 0; rtier <= maxRelaxTier; rtier++)
                    {
                        for (int ri = 0; ri < relaxLocs.Count; ri++)
                        {
                            if (TierOf(relaxLocs[ri]) == rtier)
                            {
                                tierOrdered.Add(relaxLocs[ri]);
                            }
                        }
                    }
                    relaxLocs = tierOrdered;
                }

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
                    // Relaxation counters/representative and the RNG seed are per logical type now.
                    // AggregateSessions (the telemetry) stays prefab-keyed. A relaxation packet inherits its origin's type key, so a single relaxation's packets share one counter.
                    string typeKey = Interleaver.GetTypeKey(relaxLoc);
                    bool hasRelaxCtr = relaxCtrs.ContainsKey(typeKey);
                    if (!hasRelaxCtr)
                    {
                        relaxCtrs[typeKey] = new PlacementCounters();
                        relaxRepLoc[typeKey] = relaxLoc;
                        bool hasSession = TranspiledCompletionHandler.AggregateSessions.ContainsKey(prefabName);
                        if (!hasSession)
                        {
                            TranspiledCompletionHandler.AggregateSessions[prefabName] = new TelemetryContext();
                        }
                    }
                    PlacementCounters relaxCtr = relaxCtrs[typeKey];

                    List<GroupMembership> relaxMemberships = ResolveSimilarityMemberships(relaxLoc);
                    List<GroupMembership> relaxMaxAdvertise = ResolveMaxAdvertiseMemberships(relaxLoc);
                    List<GroupMembership> relaxMaxSearch = ResolveMaxSearchMemberships(relaxLoc);
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
                    int relaxSeed = WorldGenerator.instance.GetSeed() + typeKey.GetStableHashCode();
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

                            placed = EvaluateZone(zsP, relaxLoc, zoneID, relaxMemberships, relaxMaxAdvertise, relaxMaxSearch, relaxCtr, relaxTelCtx);

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

            if (!ApiState.IsApiRun)
            {
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
            }

            SurveyMode.DumpDiagnostics();
            DiagnosticLog.DumpPlacementsToFile();
            GenerationProgress.CurrentLocation = null;
            RelaxationTracker.MarkPlacementComplete();
            GenerationProgress.EndGeneration(ApiState.IsApiRun);
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

            List<GroupMembership> memberships = ResolveSimilarityMemberships(locP);
            List<GroupMembership> maxAdvertise = ResolveMaxAdvertiseMemberships(locP);
            List<GroupMembership> maxSearch = ResolveMaxSearchMemberships(locP);
            int baseBudget = 100000;//ffs I still have these things hardcoded everywhere.
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
            int ltsSeed = WorldGenerator.instance.GetSeed() + Interleaver.GetTypeKey(locP).GetStableHashCode();
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

                    placed = EvaluateZone(zsP, locP, zoneID, memberships, maxAdvertise, maxSearch, ctrP, telCtx);

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