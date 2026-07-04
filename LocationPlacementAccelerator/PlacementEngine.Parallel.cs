// v1.0.12
/**
* Multi-threaded placement path for the replacement engine.
*
* 1.0.1: searchBiome in DrainWorkUnit widened to long to match the widened
* ZoneProfile.BiomeMask so custom EWD biomes beyond bit 15 participate correctly.
* 1.0.2: Sign-extension fix on the (long) cast. See my WorldSurveyData notes.
* 1.0.3: Passed location priority into RelaxationTracker.CheckAndMarkFailed
* to support accurate failure severity tracking (Red/Orange/Yellow).
* 1.0.4: Swapped the strict m_prefab.IsValid filter in RunParallelPath's 
* ordered-list build to Compatibility.IsValidLocation so EWD blueprint locations 
* make it into the work queue. Same root cause as Core's 1.0.4; see Compatibility.cs
* v1.0.2 header for the full story.
*
* 1.0.5: API gates for LocationsGenerated.
* 1.0.6: Pass ApiState.IsApiRun through to EndGeneration so the overlay
* tears down on API completion. World-gen-only cleanups stay gated inside
* EndGeneration.
* 1.0.7: Worker fault rethrow now uses ExceptionDispatchInfo.Capture(...).Throw()
* instead of `throw inner`, so the original worker stack trace survives. Before
* this, every parallel worker exception was rebranded as RunParallelPath.MoveNext
* in the log, making cross-thread races (e.g. the OccupiedZoneIndices HashSet
* race fixed in WorldSurveyData v1.0.4) effectively undiagnosable.
* That is why ashenius' report was making non sense at all.
*
* 1.0.8: Per-entry rekey and barrier fix and per-entry origQty and freaking multi-group.
* EWD v1.64 makes location clones first-class DISTINCT entries that share one prefab name.
* The per-job accounting was keyed by prefab name, so two clones E1/E2 collided:
*   - _remainingToPlace[prefab] overwritten at init so... only the last clone's quota survived
*     (E1 qty 50 + E2 qty 20 placed 20, not 70). This is what capped clone placement.
*   - _inFlightRegions[prefab] shared one counter so it hit zero ONCE, so the per-entry completion (and its _prioritizedInFlight decrement) 
*   fired once for N prioritized clones. _prioritizedInFlight never reached zero and the priority barrier hung. THE hang.
*   I mean the one Ashenius was showing me happening.
* So _inFlightRegions, _remainingToPlace, _counterLists, _telemetryLists, the renamed
* _totalZonesPerEntry, and the spatial partition map are now keyed by the ZoneLocation
* entry itself (reference identity, clones are different objects). Relaxation-created entries are new objects, so they key cleanly for free.
* The barrier decrement now reads tw.Loc.m_prioritized (the ENTRY's own flag) instead of unit.
* IsPrioritized (a per-STREAM flag). Priority and similarity groups are independent markers and a mixed-priority group would otherwise decrement a counter it never incremented.
* Would make no sense.
* origQty in DoFlushAndRelax now reads locP.m_quantity directly. The parallel path runs off OriginalLocations (un-packetized), 
* so the entry's own m_quantity IS its target whereas Interleaver.GetOriginalQuantity returns the FIRST prefab match, conflating clones.
* Defensive:
* it only diverges if clones carry different quantities, which I am thinking about documenting as unsupported.
* Cross-module handlers that are genuinely prefab-level stay prefab-keyed on purpose:
* RelaxationTracker (one user-facing row per type), _centerFirstCounts (CenterFirstPlacer caps at one center-first instance per prefab), 
* and TranspiledCompletionHandler.AggregateSessions (owned by the transpiled engine, the replaced path only writes-then-removes it, never reads).
* The relaxation-state keying in ConstraintRelaxer and the serial-fallback dedup stay prefab-keyed
* for now and move to per-origin in the separate lineage pass.
* TypeRegionWork carries the resolved membership list (set once in BuildSpatialStreams, so the per-dart hot path does no lookup).
* EvaluateZoneParallel and the commit go through the shared ConflictsWithSimilarMembers / CommitMemberships in Core.
*
* 1.0.9: Relaxation accounting routed through the logical type key (Interleaver.GetTypeKey), to match ConstraintRelaxer now writing RelaxationAttempts per type. 
* DoFlushAndRelax and RunInlineRelaxation read RelaxationAttempts and call RelaxationTracker by type key.
* the serial fallback's per-relaxation counters and its IsRelaxationSucceeded skip are per type, so one clone succeeding never suppresses a sibling clone's retry.
* _centerFirstCounts and the PlayabilityPolicy lookups stay prefab-keyed (config / per-prefab semantics), and AggregateSessions stays prefab-keyed
* (telemetry shared with the transpiled engine) so both identical for non-clone worlds.
* Also... after Jere said that clones can be different, the entire clone different quantities thing is now supported.
*
* 1.0.10: Parallel dispatcher race condition fix. The batching logic eagerly dumped multiple map 
* partitions of the same similarity group into the queue simultaneously. Because different partitions (colors) 
* are adjacent in space, concurrent workers violated the distance constraints. Furthermore, it didn't wait for 
* large-radius landlords to finish before starting small-radius tenants, which recreated the starvation bug from 
* the sequential path. Added InFlightWorkUnits tracking to GtsStream to strictly gate dispatching, ensuring 
* a group fully finishes its current spatial partition before moving to the next. Also removed the volatile 
* keyword from InFlightWorkUnits as it triggers a CS0420 warning when used with Interlocked, using 
* Volatile.Read instead. Finally, reverted an overly aggressive guard that skipped adding empty subgroups, 
* which deadlocked the priority barrier for zero-candidate prioritized groups by starving their sentinels.
*
* Architecture overview:
*   1. BuildSpatialStreams groups location types by GTS (similarity group),
*      partitions each group's candidate zones into spatial regions using
*      SpatialPartitionAlgorithms, and packages them as WorkUnits.
*   2. The main thread feeds WorkUnits into a BlockingCollection queue,
*      respecting the priority barrier (prioritized types must complete
*      before non-prioritized types begin).
*   3. N worker threads (ProcessorCount - 2) pull WorkUnits and evaluate
*      zones via EvaluateZoneParallel (thread-safe: uses ThreadSafePRNG,
*      no UnityEngine.Random). Successful placements go into _resultQueue.
*   4. The main thread polls DrainAndCommit() to call RegisterLocation()
*      (which is main-thread-only) and yields to Unity for GUI updates.
*   5. When all regions for a type are done, the last worker fires
*      DoFlushAndRelax, which can cascade into RunInlineRelaxation
*      if the type needs smart recovery.
*
* Thread safety contracts:
*   - _remainingToPlace / _inFlightRegions: per-ENTRY StrongBox<int> (keyed by ZoneLocation),
*     mutated via Interlocked. Workers decrement; zero triggers flush.
*   - _resultQueue: ConcurrentQueue, lock-free enqueue from workers,
*     dequeue on main thread only.
*   - _pendingOccupancy: ConcurrentDictionary, workers TryAdd to claim zones.
*   - PresenceGrid: lock-free CAS per cell (see PresenceGrid.cs).
*   - RegisterLocation: main thread only, called in DrainAndCommit.
*   
*   Almost made me rename the mod from LPA to PIA.
*   God class, with lots of god methods. Enjoy, me reading this a year from now.
*
* 1.0.11: Max-similarity / anchor inclusion. TypeRegionWork now carries the location's max advertise and search
* membership sets (resolved in BuildSpatialStreams), EvaluateZoneParallel enforces the search set the same way
* EvaluateZoneList commits the advertise set, and the relaxation path does likewise. ErrNotSim is aggregated in
* AggregateCounters. Empty max sets for ordinary locations, so the parallel path is unchanged for them.
* 
* 1.0.12: Placement waves on the parallel path. The dispatch loop that used to run once now runs once per anchor tier,
* lowest first, and does not open a tier until the previous one has fully drained, so a gated searcher never dispatches
* against a host grid a lower tier has not finished painting. I pulled the whole prioritized/non-prioritized dispatch body
* out verbatim into DispatchOneTier and left the graph-coloring color barriers untouched. The only new machinery is an
* inter-tier barrier (_tierRemaining / _tierDone) that every entry decrements once at its WorkerBody completion point, plus
* a per-tier reset of the priority barrier which is safe (? famours last words) because the inter-tier barrier guarantees no prior-tier worker is still
* live. Workers spawn once and survive across tiers via GetConsumingEnumerable and CompleteAdding waits for the last tier.
* The serial relaxation fallback is tier-ordered for the same reason. A world with no anchoring is a single tier, so this is  byte-for-byte the old schedule.
* 
* This is getting quite unmanageable. //TODO: get rid of all that crap and write it from scratch? *shudders*
*/
#nullable disable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    internal static partial class PlacementEngine
    {
        private static BlockingCollection<WorkUnit> _workQueue;
        private static ConcurrentQueue<PlacementResult> _resultQueue;

        private static int _prioritizedInFlight;
        private static ManualResetEventSlim _priorityBarrierDone;

        /**
        * Inter-tier barrier. Every entry in the tier currently dispatching decrements _tierRemaining once at its completion
        * point in WorkerBody; the last one sets _tierDone, which is what the dispatcher waits on before opening the next tier.
        */
        private static int _tierRemaining;
        private static ManualResetEventSlim _tierDone;

        private static ConcurrentDictionary<Vector2i, byte> _pendingOccupancy;
        private static Dictionary<Vector2i, LocationInstance> _occupancySnapshot;

        /**
         * Per-entry: how many region WorkUnits remain to be processed. Last decrement to 0 fires DoFlushAndRelax.
         * Keyed by the ZoneLocation entry (not prefab name!) so clone entries that share a prefab each get their own counter.
         */
        private static ConcurrentDictionary<ZoneLocation, StrongBox<int>> _inFlightRegions;

        // Per-entry: how many placements are still needed. Workers decrement on successful placement, stop when <= 0.
        private static ConcurrentDictionary<ZoneLocation, StrongBox<int>> _remainingToPlace;

        /**
        * Per-entry counter/telemetry lists  one entry per region that contains zones for the type.
        * Pre-allocated on main thread during BuildSpatialStreams.
        * Workers write to their own pre-assigned instances (never to the list), aggregated by one worker at flush.
        */
        private static ConcurrentDictionary<ZoneLocation, List<PlacementCounters>> _counterLists;
        private static ConcurrentDictionary<ZoneLocation, List<TelemetryContext>> _telemetryLists;

        private static ConcurrentDictionary<string, byte> _startedPrefabs;
        private static object _ltsCompletionLock;

        private static int _parallelTokensProcessed;
        private static int _parallelTotalZones;

        private static ConcurrentDictionary<ZoneLocation, int> _totalZonesPerEntry;

        private struct OrderedEntry
        {
            public ZoneLocation Loc;
            public int BaseQty;
            public int OriginalIndex;
        }

        /**
        * A spatial region of a GT. Contains per-type zone sublists.
        * Workers process TypeWork entries sequentially (sieve order),then pull the next WorkUnit from the queue.
        */
        private class WorkUnit
        {
            public List<TypeRegionWork> TypeWork;
            public bool IsPrioritized;
            public GtsStream OwnerStream;
        }

        private class TypeRegionWork
        {
            public ZoneLocation Loc;
            // Resolved once in BuildSpatialStreams so the per-dart hot path never re-resolves.One entry for single-group/ungrouped, N for a genuine multi-group location.
            public List<GroupMembership> Memberships;
            // Resolved once alongside Memberships. Advertise = groups painted on placement (GroupsMax) Search = advertise plus any search-only anchors, queried per dart. Empty for ordinary locations.
            public List<GroupMembership> MaxAdvertise;
            public List<GroupMembership> MaxSearch;
            public List<Vector2i> Zones;
            public PlacementCounters Counters;
            public TelemetryContext TelCtx;
        }

        private class ColorBatch
        {
            public List<WorkUnit> WorkUnits;
        }

        /**
        * A GT stream groups all location types that share a similarity group.
        * SubGroups partition by minDistFromSimilar (landlord-first: descending distance order so the type with the
        * largest exclusion radius places first and claims territory before smaller-radius types).
        */
        private class GtsStream
        {
            public string GroupKey;
            public List<SubGroupStream> SubGroups;
            public int CurrentSubGroup;
            public bool IsPrioritized;
            public int InFlightWorkUnits; // Mutated via Interlocked. No volatile keyword to avoid CS0420. ""a reference to a volatile field will not be treated as volatile". I do not remember what was the deal here. Let it be.
        }

        private class SubGroupStream
        {
            public float MinDistFromSimilar;
            public List<ColorBatch> Colors;
            public int CurrentColorIndex;
        }

        private static IEnumerator RunParallelPath(ZoneSystem zsP, int locListSnapshotP)
        {
            DiagnosticLog.WriteTimestampedLog(
                $"[LPA] Parallel placement ENABLED.  Workers: {_parallelThreadCount}." +
                $"  BC: {Compatibility.IsBetterContinentsActive}");

            HashSet<string> centerFirstNames = new HashSet<string>();
            for (int i = 0; i < zsP.m_locations.Count; i++)
            {
                if (zsP.m_locations[i].m_centerFirst)
                {
                    centerFirstNames.Add(zsP.m_locations[i].m_prefabName);
                }
            }

            List<ZoneLocation> srcLocations = zsP.m_locations;
            if (Interleaver.OriginalLocations != null)
            {
                srcLocations = Interleaver.OriginalLocations;
            }

            // Build eligible list, then sort: prioritized first, modded types pushed back.
            List<OrderedEntry> ordered = new List<OrderedEntry>();
            for (int i = 0; i < srcLocations.Count; i++)
            {
                ZoneLocation loc = srcLocations[i];
                // EWD-mirror: blueprint locations have an empty AssetID + name-only SoftReference. The old m_prefab.IsValid check rejected them before
                // they ever hit the work queue. IsValidLocation matches EWD's own IdManager.IsValid so blueprints now survive into RunParallelPath.
                if (!loc.m_enable || !Compatibility.IsValidLocation(loc) || loc.m_quantity <= 0)
                {
                    continue;
                }
                int baseQty = loc.m_quantity;
                if (centerFirstNames.Contains(loc.m_prefabName))
                {
                    baseQty = loc.m_quantity - 1;
                }
                if (baseQty <= 0)
                {
                    continue;
                }
                OrderedEntry entry = new OrderedEntry();
                entry.Loc = loc;
                entry.BaseQty = baseQty;
                entry.OriginalIndex = i;
                ordered.Add(entry);
            }

            ordered.Sort(CompareOrderedEntries);

            _workQueue = new BlockingCollection<WorkUnit>();
            _resultQueue = new ConcurrentQueue<PlacementResult>();
            _pendingOccupancy = new ConcurrentDictionary<Vector2i, byte>();
            _occupancySnapshot = new Dictionary<Vector2i, LocationInstance>(zsP.m_locationInstances);
            _ltsCompletionLock = new object();
            _startedPrefabs = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _priorityBarrierDone = new ManualResetEventSlim(false);
            _tierDone = new ManualResetEventSlim(false);
            _parallelTokensProcessed = 0;

            _inFlightRegions = new ConcurrentDictionary<ZoneLocation, StrongBox<int>>();
            _remainingToPlace = new ConcurrentDictionary<ZoneLocation, StrongBox<int>>();
            _counterLists = new ConcurrentDictionary<ZoneLocation, List<PlacementCounters>>();
            _telemetryLists = new ConcurrentDictionary<ZoneLocation, List<TelemetryContext>>();
            _totalZonesPerEntry = new ConcurrentDictionary<ZoneLocation, int>();

            _parallelTotalZones = 0;
            foreach (OrderedEntry entry in ordered)
            {
                _remainingToPlace[entry.Loc] = new StrongBox<int>(entry.BaseQty);
                _inFlightRegions[entry.Loc] = new StrongBox<int>(0);
                _counterLists[entry.Loc] = new List<PlacementCounters>();
                _telemetryLists[entry.Loc] = new List<TelemetryContext>();
            }


            GenerationProgress.InitThreadSlots(_parallelThreadCount);
            Task[] workerTasks = new Task[_parallelThreadCount];
            for (int w = 0; w < _parallelThreadCount; w++)
            {
                int idx = w;
                workerTasks[w] = Task.Run(() => WorkerBody(zsP, idx));
            }

            const long YieldIntervalMs = 100;// works well in the mt case. 
            Stopwatch yieldSw = Stopwatch.StartNew();

            /**
            * Partition the sorted entries into tiers, keeping the existing sort within each tier, then dispatch one tier to
            * completion before opening the next. That is what guarantees an advertiser's footprint exists before any gated
            * searcher in a later tier reads its grid. With no anchoring every type is tier 0, so this is one pass, unchanged.
            */
            List<List<OrderedEntry>> orderedByTier = new List<List<OrderedEntry>>();
            for (int e = 0; e < ordered.Count; e++)
            {
                int tier = TierOf(ordered[e].Loc);
                while (orderedByTier.Count <= tier)
                {
                    orderedByTier.Add(new List<OrderedEntry>());
                }
                orderedByTier[tier].Add(ordered[e]);
            }

            for (int tier = 0; tier < orderedByTier.Count; tier++)
            {
                List<OrderedEntry> tierEntries = orderedByTier[tier];
                if (tierEntries.Count == 0)
                {
                    continue;
                }
                IEnumerator tierIter = DispatchOneTier(zsP, tierEntries, yieldSw);
                while (tierIter.MoveNext())
                {
                    yield return tierIter.Current;
                }
            }

            _workQueue.CompleteAdding();

            Task allDone = Task.WhenAll(workerTasks);
            while (!allDone.IsCompleted)
            {
                DrainAndCommit(zsP);
                UpdateAnnulus(zsP);
                if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                {
                    yieldSw.Restart();
                    yield return null;
                }
            }

            foreach (Task t in workerTasks)
            {
                if (t.IsFaulted)
                {
                    Exception inner = t.Exception?.InnerException ?? t.Exception;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                }
            }

            // Final drain... exhaust everything remaining in the queue.
            while (_resultQueue.TryDequeue(out PlacementResult finalResult))
            {
                zsP.RegisterLocation(finalResult.Loc, finalResult.Position, false);
                if (finalResult.ZoneIdx >= 0)
                {
                    SurveyMode.MarkZoneOccupied(finalResult.ZoneIdx);
                }
            }
            yield return null;

            // Serial relaxation fallback for types that failed inline relaxation.
            for (int rp = 0; rp < _maxRelaxationAttempts; rp++)
            {
                if (zsP.m_locations.Count <= locListSnapshotP)
                {
                    break;
                }
                int newCount = zsP.m_locations.Count - locListSnapshotP;
                List<ZoneLocation> relaxLocs = zsP.m_locations.GetRange(locListSnapshotP, newCount);
                locListSnapshotP = zsP.m_locations.Count;

                /**
                 * Tier-order the fallback exactly as the main pass is tier-ordered: a host that only recovers in this serial
                 * pass still has to precede its satellites, or a satellite retries against a grid the host has not painted.
                 * I bucket by tier and walk low tiers first, preserving append order within a tier so the pass is deterministic. 
                 * Used the word tier here 45 times. 
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

                Dictionary<string, PlacementCounters> rCtrs = new Dictionary<string, PlacementCounters>(StringComparer.Ordinal);
                Dictionary<string, ZoneLocation> rRep = new Dictionary<string, ZoneLocation>(StringComparer.Ordinal);
                foreach (ZoneLocation rx in relaxLocs)
                {
                    if (!rx.m_enable || rx.m_centerFirst)
                    {
                        continue;
                    }
                    // Per logical type: distinct clones relax independently, and a relaxation packet inherits its origin's type key so a single relaxation's packets group together.
                    string typeKey = Interleaver.GetTypeKey(rx);
                    // Skip if relaxation already succeeded inline on a worker thread keyed per type so one clone succeeding does not suppress a sibling clone's serial retry.
                    if (RelaxationTracker.IsRelaxationSucceeded(typeKey))
                    {
                        continue;
                    }
                    bool hasRCtr = rCtrs.ContainsKey(typeKey);
                    if (!hasRCtr)
                    {
                        rCtrs[typeKey] = new PlacementCounters();
                        rRep[typeKey] = rx;
                    }
                    IEnumerator it = RunLocSerial(zsP, rx, rCtrs[typeKey], suppressFlushP: true);
                    while (it.MoveNext())
                    {
                        yield return it.Current;
                    }
                }
                foreach (KeyValuePair<string, ZoneLocation> k in rRep)
                {
                    FlushLTS(zsP, k.Value, rCtrs[k.Key]);
                    // AggregateSessions is prefab-keyed (telemetry, shared with the transpiled path), so remove by the representative's prefab name rather than the type-key map key.
                    TranspiledCompletionHandler.AggregateSessions.Remove(k.Value.m_prefabName);
                }
            }

            GenerationProgress.ClearThreadSlots();
            if (!ApiState.IsApiRun)
            {
                if (_locationsGeneratedProp != null)
                {
                    _locationsGeneratedProp.SetValue(zsP, true);
                }
                else
                {
                    DiagnosticLog.WriteLog(
                        "[LPA] WARNING: Could not set LocationsGenerated via reflection.",
                        BepInEx.Logging.LogLevel.Error);
                }
            }

            SurveyMode.DumpDiagnostics();
            DiagnosticLog.DumpPlacementsToFile();
            GenerationProgress.CurrentLocation = null;
            RelaxationTracker.MarkPlacementComplete();
            GenerationProgress.EndGeneration(ApiState.IsApiRun);

            _workQueue?.Dispose();
            _workQueue = null;
            _priorityBarrierDone?.Dispose();
            _priorityBarrierDone = null;
            _tierDone?.Dispose();
            _tierDone = null;
        }

        /**
        * Builds the GT stream hierarchy for the parallel path.
        *
        * For each GT (similarity group), entries are sub-grouped by their
        * minDistFromSimilar value. Each sub-group gets spatially partitioned
        * into N regions using SpatialPartitionAlgorithms.BuildRule(), producing
        * one WorkUnit per region. Each WorkUnit contains one TypeRegionWork
        * per location type that has zones in that region.
        *
        * The spatial partition guarantees that two workers processing different
        * regions of the same sub-group will never place locations close enough
        * to violate the similarity distance constraint - see the safety proof
        * in SpatialPartitionAlgorithms.
        * 
        */
        private static List<GtsStream> BuildSpatialStreams(List<OrderedEntry> orderedP)
        {
            // Group entries by GT, preserving list order.
            Dictionary<string, List<OrderedEntry>> gtsMap = new Dictionary<string, List<OrderedEntry>>(StringComparer.Ordinal);
            List<string> gtsOrder = new List<string>();
            Dictionary<string, bool> gtsPriority = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (OrderedEntry entry in orderedP)
            {
                string grp = entry.Loc.m_prefabName;
                if (!string.IsNullOrEmpty(entry.Loc.m_group))
                {
                    grp = entry.Loc.m_group;
                }
                bool hasGroup = gtsMap.TryGetValue(grp, out List<OrderedEntry> list);
                if (!hasGroup)
                {
                    list = new List<OrderedEntry>();
                    gtsMap[grp] = list;
                    gtsOrder.Add(grp);
                    gtsPriority[grp] = entry.Loc.m_prioritized;
                }
                list.Add(entry);
            }

            List<GtsStream> streams = new List<GtsStream>(gtsOrder.Count);

            foreach (string grpKey in gtsOrder)
            {
                List<OrderedEntry> entries = gtsMap[grpKey];

                // Sub-group entries by minDistFromSimilar (rounded to 2 decimal places to merge floating point noise from different LTS definitions).
                Dictionary<float, List<OrderedEntry>> subGroupMap = new Dictionary<float, List<OrderedEntry>>();
                List<float> subGroupDists = new List<float>();

                foreach (OrderedEntry entry in entries)
                {
                    float dist = entry.Loc.m_minDistanceFromSimilar;
                    float key = Mathf.Round(dist * 100f) / 100f;
                    bool hasSubGroup = subGroupMap.TryGetValue(key, out List<OrderedEntry> sgList);
                    if (!hasSubGroup)
                    {
                        sgList = new List<OrderedEntry>();
                        subGroupMap[key] = sgList;
                        subGroupDists.Add(key);
                    }
                    sgList.Add(entry);
                }

                // Landlord-first ordering: descending by minDist so the type with the largest exclusion radius (the "landlord") places first and claims territory.
                subGroupDists.Sort((float aP, float bP) => bP.CompareTo(aP));

                GtsStream stream = new GtsStream
                {
                    GroupKey = grpKey,
                    SubGroups = new List<SubGroupStream>(),
                    CurrentSubGroup = 0,
                    IsPrioritized = gtsPriority[grpKey],
                    InFlightWorkUnits = 0
                };

                foreach (float sgMinDist in subGroupDists)
                {
                    List<OrderedEntry> sgEntries = subGroupMap[sgMinDist];

                    PartitionRule rule = SpatialPartitionAlgorithms.BuildRule(sgMinDist, _parallelThreadCount);
                    int partitionCount = rule.PartitionCount;

                    // Build per-partition, per-BLOCK, per-ENTRY zone sublists. Keyed by the ZoneLocation entry so clone entries that share a prefab get their own distinct candidate lists.
                    Dictionary<int, Dictionary<ZoneLocation, List<Vector2i>>>[] colorBlocks = new Dictionary<int, Dictionary<ZoneLocation, List<Vector2i>>>[partitionCount];//btw all these 2is will become 2ss in PTB.
                    for (int p = 0; p < partitionCount; p++)
                    {
                        colorBlocks[p] = new Dictionary<int, Dictionary<ZoneLocation, List<Vector2i>>>();
                    }

                    int totalCandidateZones = 0;
                    foreach (OrderedEntry entry in sgEntries)
                    {
                        List<Vector2i> candidates = SurveyMode.GetOrBuildCandidateList(entry.Loc);
                        totalCandidateZones += candidates.Count;

                        foreach (Vector2i zone in candidates)
                        {
                            SpatialPartitionAlgorithms.GetPartition(zone, ref rule, out int colorIndex, out int blockId);

                            bool hasBlockDict = colorBlocks[colorIndex].TryGetValue(blockId, out Dictionary<ZoneLocation, List<Vector2i>> locDict);
                            if (!hasBlockDict)
                            {
                                locDict = new Dictionary<ZoneLocation, List<Vector2i>>();
                                colorBlocks[colorIndex][blockId] = locDict;
                            }

                            bool hasZoneList = locDict.TryGetValue(entry.Loc, out List<Vector2i> zoneList);
                            if (!hasZoneList)
                            {
                                zoneList = new List<Vector2i>();
                                locDict[entry.Loc] = zoneList;
                            }
                            zoneList.Add(zone);
                        }
                    }

                    if (ModConfig.DiagnosticMode.Value)
                    {
                        DiagnosticLog.WriteTimestampedLog(
                            $"[LPA] GTS={grpKey} minDist={sgMinDist:F0} " +
                            $"types={sgEntries.Count} zones={totalCandidateZones} " +
                            $"partitions={partitionCount} mode={rule.Mode}");
                    }

                    // Track how many regions each entry appears in.
                    foreach (OrderedEntry entry in sgEntries)
                    {
                        int regionCount = 0;
                        for (int p = 0; p < partitionCount; p++)
                        {
                            foreach (KeyValuePair<int, Dictionary<ZoneLocation, List<Vector2i>>> blockKvp in colorBlocks[p])
                            {
                                bool hasZones = blockKvp.Value.TryGetValue(entry.Loc, out List<Vector2i> zoneList);
                                if (hasZones && zoneList.Count > 0)
                                {
                                    regionCount++;
                                }
                            }
                        }
                        Interlocked.Add(ref _inFlightRegions[entry.Loc].Value, regionCount);
                    }

                    SubGroupStream sgs = new SubGroupStream
                    {
                        MinDistFromSimilar = sgMinDist,
                        Colors = new List<ColorBatch>(),
                        CurrentColorIndex = 0
                    };

                    for (int p = 0; p < partitionCount; p++)
                    {
                        ColorBatch colorBatch = new ColorBatch();
                        colorBatch.WorkUnits = new List<WorkUnit>();

                        foreach (KeyValuePair<int, Dictionary<ZoneLocation, List<Vector2i>>> blockKvp in colorBlocks[p])
                        {
                            List<TypeRegionWork> typeWork = new List<TypeRegionWork>();

                            foreach (OrderedEntry entry in sgEntries)
                            {
                                bool hasZones = blockKvp.Value.TryGetValue(entry.Loc, out List<Vector2i> zones);
                                if (!hasZones || zones.Count == 0)
                                {
                                    continue;
                                }

                                PlacementCounters ctr = new PlacementCounters();
                                TelemetryContext telCtx = new TelemetryContext();
                                _counterLists[entry.Loc].Add(ctr);
                                _telemetryLists[entry.Loc].Add(telCtx);

                                typeWork.Add(new TypeRegionWork
                                {
                                    Loc = entry.Loc,
                                    Memberships = ResolveSimilarityMemberships(entry.Loc),
                                    MaxAdvertise = ResolveMaxAdvertiseMemberships(entry.Loc),
                                    MaxSearch = ResolveMaxSearchMemberships(entry.Loc),
                                    Zones = zones,
                                    Counters = ctr,
                                    TelCtx = telCtx
                                });
                            }

                            if (typeWork.Count > 0)
                            {
                                colorBatch.WorkUnits.Add(new WorkUnit
                                {
                                    TypeWork = typeWork,
                                    IsPrioritized = stream.IsPrioritized,
                                    OwnerStream = stream
                                });
                            }
                        }

                        if (colorBatch.WorkUnits.Count > 0)
                        {
                            sgs.Colors.Add(colorBatch);
                        }
                    }

                    stream.SubGroups.Add(sgs);
                }

                streams.Add(stream);
            }

            // Compute total zones for annulus denominator and per-entry tracking.
            int totalZones = 0;
            Dictionary<ZoneLocation, int> entryZones = new Dictionary<ZoneLocation, int>();
            foreach (GtsStream s in streams)
            {
                foreach (SubGroupStream sg in s.SubGroups)
                {
                    foreach (ColorBatch batch in sg.Colors)
                    {
                        foreach (WorkUnit wu in batch.WorkUnits)
                        {
                            foreach (TypeRegionWork tw in wu.TypeWork)
                            {
                                totalZones += tw.Zones.Count;
                                entryZones.TryGetValue(tw.Loc, out int cur);
                                entryZones[tw.Loc] = cur + tw.Zones.Count;
                            }
                        }
                    }
                }
            }
            _parallelTotalZones = Math.Max(1, totalZones);
            foreach (KeyValuePair<ZoneLocation, int> kvp in entryZones)
            {
                _totalZonesPerEntry[kvp.Key] = kvp.Value;
            }

            /**
            * Sentinel WorkUnits for zero-candidate types. These types have no
            * zones anywhere (biome doesn't exist, all occupied, etc).
            * Enqueuing them through the normal pipeline ensures DoFlushAndRelax
            * fires via the standard lifecycle - no special-case main-thread flush.
            * */
            foreach (string grpKey in gtsOrder)
            {
                List<OrderedEntry> entries = gtsMap[grpKey];
                foreach (OrderedEntry entry in entries)
                {
                    if (_inFlightRegions[entry.Loc].Value > 0)
                    {
                        continue;
                    }

                    _inFlightRegions[entry.Loc] = new StrongBox<int>(1);

                    PlacementCounters sentinelCtr = new PlacementCounters();
                    TelemetryContext sentinelTel = new TelemetryContext();
                    _counterLists[entry.Loc].Add(sentinelCtr);
                    _telemetryLists[entry.Loc].Add(sentinelTel);

                    //I have to say this looks horrible...
                    GtsStream targetStream = null;
                    for (int i = 0; i < streams.Count; i++)
                    {
                        if (streams[i].GroupKey == grpKey)
                        {
                            targetStream = streams[i];
                            break;
                        }
                    }

                    WorkUnit sentinelWu = new WorkUnit
                    {
                        TypeWork = new List<TypeRegionWork>
                        {
                            new TypeRegionWork
                            {
                                Loc = entry.Loc,
                                Memberships = ResolveSimilarityMemberships(entry.Loc),
                                MaxAdvertise = ResolveMaxAdvertiseMemberships(entry.Loc),
                                MaxSearch = ResolveMaxSearchMemberships(entry.Loc),
                                Zones = new List<Vector2i>(),
                                Counters = sentinelCtr,
                                TelCtx = sentinelTel
                            }
                        },
                        IsPrioritized = entry.Loc.m_prioritized,
                        OwnerStream = targetStream
                    };

                    if (targetStream != null && targetStream.SubGroups.Count > 0)
                    {
                        if (targetStream.SubGroups[0].Colors.Count > 0)
                        {
                            targetStream.SubGroups[0].Colors[0].WorkUnits.Add(sentinelWu);
                        }
                        else
                        {
                            ColorBatch dummyBatch = new ColorBatch();
                            dummyBatch.WorkUnits = new List<WorkUnit>();
                            dummyBatch.WorkUnits.Add(sentinelWu);
                            targetStream.SubGroups[0].Colors.Add(dummyBatch);
                        }
                    }
                }
            }

            return streams;
        }

        /**
        * Dispatches exactly one anchor tier and does not return until every entry in it has fully completed, inline
        * relaxation included (that runs synchronously on the finishing worker inside DoFlushAndRelax). RunParallelPath calls
        * this once per tier in ascending order, so a lower tier's advertise footprints are all committed before any searcher
        * in a higher tier reads a grid.
        *
        * Within a tier the schedule is exactly what it always was: prioritized streams drain first, the priority barrier
        * opens, then the rest, with the graph-coloring color barriers untouched. I reset the priority barrier per tier, which
        * is safe only because the inter-tier barrier below guarantees no worker from the previous tier is still running.
        *
        * The inter-tier barrier is _tierDone: WorkerBody decrements _tierRemaining once per entry at its single completion
        * point, and the last entry sets _tierDone. Workers stay alive across tiers because RunParallelPath withholds
        * CompleteAdding until the final tier - between tiers they simply block in GetConsumingEnumerable.
        */
        private static IEnumerator DispatchOneTier(ZoneSystem zsP, List<OrderedEntry> tierEntriesP, Stopwatch yieldSwP)
        {
            _prioritizedInFlight = 0;
            for (int i = 0; i < tierEntriesP.Count; i++)
            {
                if (tierEntriesP[i].Loc.m_prioritized)
                {
                    _prioritizedInFlight++;
                }
            }
            _priorityBarrierDone.Reset();
            if (_prioritizedInFlight == 0)
            {
                _priorityBarrierDone.Set();
            }

            _tierRemaining = tierEntriesP.Count;
            _tierDone.Reset();

            List<GtsStream> gtsStreams = BuildSpatialStreams(tierEntriesP);

            // yieldSw is threaded through from RunParallelPath so frame pacing stays continuous across tiers. I alias it to the name the extracted dispatch body already uses.
            Stopwatch yieldSw = yieldSwP;
            bool crossedPriority = false;
            const long YieldIntervalMs = 100;

            if (_interleavedScheduling)
            {
                // Phase 1: round-robin prioritized streams until exhausted.
                bool anyPrio = true;
                while (anyPrio)
                {
                    anyPrio = false;
                    bool enqueuedAnything = false;
                    foreach (GtsStream stream in gtsStreams)
                    {
                        if (!stream.IsPrioritized)
                        {
                            continue;
                        }

                        // Wait for this stream's current color to finish completely before pushing the next color.
                        if (Volatile.Read(ref stream.InFlightWorkUnits) > 0)
                        {
                            anyPrio = true;
                            continue;
                        }

                        if (stream.CurrentSubGroup >= stream.SubGroups.Count)
                        {
                            continue;
                        }

                        SubGroupStream csg = stream.SubGroups[stream.CurrentSubGroup];
                        if (csg.CurrentColorIndex >= csg.Colors.Count)
                        {
                            stream.CurrentSubGroup++;
                            if (stream.CurrentSubGroup < stream.SubGroups.Count)
                            {
                                anyPrio = true;
                            }
                            continue;
                        }

                        // Enqueue ALL blocks (WorkUnits) for this specific Color!
                        // Since they share a color, they are spatially isolated and safe for concurrent processing.
                        ColorBatch batch = csg.Colors[csg.CurrentColorIndex];
                        for (int w = 0; w < batch.WorkUnits.Count; w++)
                        {
                            _workQueue.Add(batch.WorkUnits[w]);
                            Interlocked.Increment(ref stream.InFlightWorkUnits);
                        }

                        csg.CurrentColorIndex++;
                        anyPrio = true;
                        enqueuedAnything = true;
                    }

                    DrainAndCommit(zsP);
                    UpdateAnnulus(zsP);
                    if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs || (!enqueuedAnything && anyPrio))
                    {
                        yieldSw.Restart();
                        yield return null;
                    }
                }

                // Wait for the priority barrier before feeding non-prioritized work.
                while (!_priorityBarrierDone.IsSet)
                {
                    DrainAndCommit(zsP);
                    UpdateAnnulus(zsP);
                    if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                    {
                        yieldSw.Restart();
                        yield return null;
                    }
                }

                // Phase 2: round-robin non-prioritized streams.
                bool anyLeft = true;
                while (anyLeft)
                {
                    anyLeft = false;
                    bool enqueuedAnything = false;
                    foreach (GtsStream stream in gtsStreams)
                    {
                        if (stream.IsPrioritized)
                        {
                            continue;
                        }

                        if (Volatile.Read(ref stream.InFlightWorkUnits) > 0)
                        {
                            anyLeft = true;
                            continue;
                        }

                        if (stream.CurrentSubGroup >= stream.SubGroups.Count)
                        {
                            continue;
                        }

                        SubGroupStream csg = stream.SubGroups[stream.CurrentSubGroup];
                        if (csg.CurrentColorIndex >= csg.Colors.Count)
                        {
                            stream.CurrentSubGroup++;
                            if (stream.CurrentSubGroup < stream.SubGroups.Count)
                            {
                                anyLeft = true;
                            }
                            continue;
                        }

                        ColorBatch batch = csg.Colors[csg.CurrentColorIndex];
                        for (int w = 0; w < batch.WorkUnits.Count; w++)
                        {
                            _workQueue.Add(batch.WorkUnits[w]);
                            Interlocked.Increment(ref stream.InFlightWorkUnits);
                        }

                        csg.CurrentColorIndex++;
                        anyLeft = true;
                        enqueuedAnything = true;
                    }

                    DrainAndCommit(zsP);
                    UpdateAnnulus(zsP);
                    if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs || (!enqueuedAnything && anyLeft))
                    {
                        yieldSw.Restart();
                        yield return null;
                    }
                }
            }
            else
            {
                // Non-interleaved: exhaust each stream completely before moving on.
                foreach (GtsStream stream in gtsStreams)
                {
                    if (!crossedPriority && !stream.IsPrioritized)
                    {
                        crossedPriority = true;
                        while (!_priorityBarrierDone.IsSet)
                        {
                            DrainAndCommit(zsP);
                            UpdateAnnulus(zsP);
                            if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                            {
                                yieldSw.Restart();
                                yield return null;
                            }
                        }
                    }
                    foreach (SubGroupStream sg in stream.SubGroups)
                    {
                        for (int c = 0; c < sg.Colors.Count; c++)
                        {
                            ColorBatch batch = sg.Colors[c];
                            for (int w = 0; w < batch.WorkUnits.Count; w++)
                            {
                                _workQueue.Add(batch.WorkUnits[w]);
                                Interlocked.Increment(ref stream.InFlightWorkUnits);
                            }

                            // Wait for this specific color batch to finish before pushing the next color.
                            while (Volatile.Read(ref stream.InFlightWorkUnits) > 0)
                            {
                                DrainAndCommit(zsP);
                                UpdateAnnulus(zsP);
                                if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                                {
                                    yieldSw.Restart();
                                    yield return null;
                                }
                            }
                        }
                    }
                }

                // Final wait for any straggler streams.
                foreach (GtsStream stream in gtsStreams)
                {
                    while (Volatile.Read(ref stream.InFlightWorkUnits) > 0)
                    {
                        DrainAndCommit(zsP);
                        UpdateAnnulus(zsP);
                        if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                        {
                            yieldSw.Restart();
                            yield return null;
                        }
                    }
                }
            }

            // Inter-tier barrier: hold here until every entry in this tier has completed (main pass plus any inline relaxation),
            // so the next tier's searchers see a fully painted set of this tier's advertisers.
            while (!_tierDone.IsSet)
            {
                DrainAndCommit(zsP);
                UpdateAnnulus(zsP);
                if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                {
                    yieldSw.Restart();
                    yield return null;
                }
            }
        }

        private static void WorkerBody(ZoneSystem zsP, int workerIdxP)
        {
            foreach (WorkUnit unit in _workQueue.GetConsumingEnumerable())
            {
                foreach (TypeRegionWork tw in unit.TypeWork)
                {
                    string prefab = tw.Loc.m_prefabName;
                    GenerationProgress.SetThreadSlot(workerIdxP, prefab);

                    // First encounter across all workers - log the start banner.
                    if (_startedPrefabs.TryAdd(prefab, 0))
                    {
                        if (_logSuccesses || ModConfig.DiagnosticMode.Value)
                        {
                            TelemetryHelpers.LogLocationStart(tw.Loc, _mode);
                        }
                    }

                    // Seed PRNG with a region-specific salt so different regions of the same type get different dart sequences.
                    int regionSalt = 0;
                    if (tw.Zones.Count > 0)
                    {
                        regionSalt = tw.Zones[0].GetHashCode();
                    }
                    ThreadSafePRNG.SeedForLts(
                        WorldGenerator.instance.GetSeed()
                        + prefab.GetStableHashCode()
                        + regionSalt);

                    if (Volatile.Read(ref _remainingToPlace[tw.Loc].Value) > 0)
                    {
                        EvaluateZoneList(tw);
                    }

                    int regionsLeft = Interlocked.Decrement(ref _inFlightRegions[tw.Loc].Value);

                    if (regionsLeft == 0)
                    {
                        /**
                        * Pass and gate on the ENTRY's own priority flag, not unit.IsPrioritized (the stream's flag).
                        * Priority and similarity groups are independent: a prioritized entry sharing a stream with a non-prioritized one would otherwise decrement
                        * _prioritizedInFlight against a count it never contributed to, and vice versa. *sigh*
                        */
                        DoFlushAndRelax(zsP, tw.Loc, tw.Loc.m_prioritized, workerIdxP);

                        /**
                        * Priority barrier first: this entry releases its slot in _prioritizedInFlight (and opens the priority
                        * barrier if it was the last prioritized entry) BEFORE it signals tier completion below. Order matters -
                        * the dispatcher resets _prioritizedInFlight and _priorityBarrierDone for the next tier the instant it
                        * observes _tierDone, so every touch of those two by this tier's workers has to happen first.
                        */  
                        if (tw.Loc.m_prioritized)
                        {
                            if (Interlocked.Decrement(ref _prioritizedInFlight) == 0)
                            {
                                _priorityBarrierDone.Set();
                            }
                        }

                        /**
                        * Inter-tier barrier LAST: every entry, prioritized or not, decrements the tier counter exactly once
                        * here at its single completion point (inline relaxation already ran synchronously inside
                        * DoFlushAndRelax, so a tier is not done until its relaxation is done too). The last entry to complete
                        * sets _tierDone and releases the dispatcher, by which point all priority-state writes above are done.
                        */
                        if (Interlocked.Decrement(ref _tierRemaining) == 0)
                        {
                            _tierDone.Set();
                        }
                    }
                }

                if (unit.OwnerStream != null)
                {
                    Interlocked.Decrement(ref unit.OwnerStream.InFlightWorkUnits);
                }

                GenerationProgress.SetThreadSlot(workerIdxP, null);
            }
        }

        private static void EvaluateZoneList(TypeRegionWork twP)
        {
            ZoneLocation loc = twP.Loc;
            PlacementCounters ctr = twP.Counters;
            List<GroupMembership> memberships = twP.Memberships;
            List<GroupMembership> maxAdvertise = twP.MaxAdvertise;
            List<GroupMembership> maxSearch = twP.MaxSearch;
            int baseBudget = 100000;
            if (loc.m_prioritized)
            {
                baseBudget = 200000;
            }
            int budget = Interleaver.GetBudget(loc, baseBudget);

            /**
            * Must mirror ScanWorldForCandidates' BoilingOcean augmentation:
            * candidate lists include BoilingOcean zones for AshLands types
            * whose altitude range extends below -4m, so the biome mask must match.
            * NOTE (1.0.1): literal AshLands reference retained. This is geometry-specific
            * (below-sea reclassification of vanilla AshLands zones) not a generic lava-biome check. 
            * Flagged for a future pass to generalize across EWD custom lava biomes. I had this comment somewhere else.
            * I do not remember what lava biomes I was thinking of... gee zus..
            */
            long searchBiome = (long)(uint)(int)loc.m_biome;
            bool isAshLands = (searchBiome & (long)Heightmap.Biome.AshLands) != 0L;
            if (isAshLands && loc.m_minAltitude < -4.0f)
            {
                if (loc.m_maxAltitude < -4.0f)
                {
                    searchBiome = WorldSurveyData.BiomeBoilingOcean;
                }
                else
                {
                    searchBiome |= WorldSurveyData.BiomeBoilingOcean;
                }
            }

            int zonesChecked = 0;
            foreach (Vector2i zoneID in twP.Zones)
            {
                if (zonesChecked >= budget)
                {
                    break;
                }
                if (Volatile.Read(ref _remainingToPlace[twP.Loc].Value) <= 0)
                {
                    break;
                }

                zonesChecked++;
                ctr.ZonesExamined++;
                Interlocked.Increment(ref _parallelTokensProcessed);

                if (_occupancySnapshot.ContainsKey(zoneID) ||
                    _pendingOccupancy.ContainsKey(zoneID))
                {
                    ctr.ErrOccupied++;
                    continue;
                }

                int zoneGridIdx = -1;
                if (WorldSurveyData.ZoneToIndex.TryGetValue(zoneID, out int si))
                {
                    zoneGridIdx = si;
                }

                if (EvaluateZoneParallel(loc, zoneID, zoneGridIdx, memberships, maxSearch,
                                         ctr, twP.TelCtx, out Vector3 pos))
                {
                    // Atomically claim a placement slot. If another worker beat us to filling the quota, undo and stop.
                    if (Interlocked.Decrement(ref _remainingToPlace[twP.Loc].Value) < 0)
                    {
                        Interlocked.Increment(ref _remainingToPlace[twP.Loc].Value);
                        break;
                    }

                    // Atomically claim the zone. If another worker already placed here, undo the slot claim and continue to next zone.
                    if (!_pendingOccupancy.TryAdd(zoneID, 1))
                    {
                        Interlocked.Increment(ref _remainingToPlace[twP.Loc].Value);
                        ctr.ErrOccupied++;
                        continue;
                    }

                    CommitMemberships(memberships, pos);
                    CommitMaxAdvertise(maxAdvertise, pos);
                    _resultQueue.Enqueue(new PlacementResult
                    {
                        Loc = loc,
                        Position = pos,
                        Group = loc.m_group,
                        ZoneIdx = zoneGridIdx,
                        ZoneID = zoneID,
                        Counters = ctr
                    });
                    ctr.Placed++;
                    GenerationProgress.IncrementAttempted(1);
                    GenerationProgress.IncrementPlaced(1);
                }
            }
        }

        private static PlacementCounters AggregateCounters(ZoneLocation entryP)
        {
            PlacementCounters agg = new PlacementCounters();
            bool hasList = _counterLists.TryGetValue(entryP, out List<PlacementCounters> list);
            if (!hasList)
            {
                return agg;
            }
            foreach (PlacementCounters ctr in list)
            {
                agg.ZonesExamined += ctr.ZonesExamined;
                agg.ZoneExhausted += ctr.ZoneExhausted;
                agg.DartsThrown += ctr.DartsThrown;
                agg.Placed += ctr.Placed;
                agg.ErrOccupied += ctr.ErrOccupied;
                agg.ErrDist += ctr.ErrDist;
                agg.ErrBiome += ctr.ErrBiome;
                agg.ErrAlt += ctr.ErrAlt;
                agg.ErrSim += ctr.ErrSim;
                agg.ErrNotSim += ctr.ErrNotSim;
                agg.ErrTerrain += ctr.ErrTerrain;
                agg.ErrForest += ctr.ErrForest;
            }
            return agg;
        }

        private static TelemetryContext AggregateTelemetry(ZoneLocation entryP)
        {
            TelemetryContext merged = new TelemetryContext();
            bool hasList = _telemetryLists.TryGetValue(entryP, out List<TelemetryContext> list);
            if (hasList)
            {
                foreach (TelemetryContext tc in list)
                {
                    merged.Merge(tc);
                }
            }
            return merged;
        }

        private static void DoFlushAndRelax(
            ZoneSystem zsP, ZoneLocation locP, bool isPrioritizedP, int workerIdxP)
        {
            string prefab = locP.m_prefabName;
            // Relaxation state is keyed per logical type (clones are distinct types), prefab stays for the center-first count, the PlayabilityPolicy config lookup, and AggregateSessions.
            string typeKey = Interleaver.GetTypeKey(locP);

            PlacementCounters ctr = AggregateCounters(locP);
            TelemetryContext telCtx = AggregateTelemetry(locP);

            int cfCount = 0;
            if (_centerFirstCounts.TryGetValue(prefab, out int cfc))
            {
                cfCount = cfc;
            }
            int globalPlaced = ctr.Placed + cfCount;
            // The entry's own m_quantity IS its target here. i.e. the parallel path runs off OriginalLocations (un-packetized), and relaxation never mutates m_quantity on the original entry.
            // GetOriginalQuantity(prefab) would return the FIRST prefab match, conflating clones, so it is wrong for any clone past the first.
            int origQty = locP.m_quantity;
            bool isComplete = globalPlaced >= origQty;
            int minNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefab, origQty);
            bool wasRelaxed = ConstraintRelaxer.RelaxationAttempts.TryGetValue(typeKey, out int relaxCount) && relaxCount > 0;
            bool isSuccess = isComplete || (wasRelaxed && globalPlaced >= minNeeded);

            // Credit unexamined zones to the annulus progress so it stays smooth when a type fills its quota early and leaves zones unvisited.
            int totalZonesForType = 0;
            if (_totalZonesPerEntry.TryGetValue(locP, out int tz))
            {
                totalZonesForType = tz;
            }
            int unexamined = Math.Max(0, totalZonesForType - ctr.ZonesExamined);
            if (unexamined > 0)
            {
                Interlocked.Add(ref _parallelTokensProcessed, unexamined);
            }

            lock (TranspiledCompletionHandler.AggregateSessions)
            {
                TranspiledCompletionHandler.AggregateSessions[prefab] = telCtx;
            }

            int displayQty = origQty;
            if (wasRelaxed && isSuccess && !isComplete)
            {
                displayQty = minNeeded;
            }
            ReportData data = BuildReportData(locP, ctr, globalPlaced, displayQty, isComplete);

            int failedTokens = Math.Max(0, origQty - globalPlaced);
            if (failedTokens > 0)
            {
                GenerationProgress.IncrementAttempted(failedTokens);
            }

            if (isSuccess && wasRelaxed)
            {
                RelaxationTracker.MarkRelaxationSucceeded(typeKey);
            }

            if (!_minimalLogging)
            {
                if (isSuccess)
                {
                    if (_logSuccesses || wasRelaxed)
                    {
                        if (wasRelaxed)
                        {
                            DiagnosticLog.WriteTimestampedLog(
                                $"[RELAXATION SUCCESS] {prefab} placed {globalPlaced}/{displayQty} " +
                                $"after {relaxCount} relaxation(s). " +
                                ConstraintRelaxer.GetRelaxationSummary(prefab, locP),
                                BepInEx.Logging.LogLevel.Message);
                        }
                        ReportFormatter.WriteReport(data, false, prefab);
                    }
                }
                else
                {
                    ReportFormatter.WriteReport(data, false, prefab);
                }
            }

            if (!isSuccess)
            {
                ZoneLocation relaxLoc = null;
                lock (_ltsCompletionLock)
                {
                    int snap = zsP.m_locations.Count;
                    if (!ConstraintRelaxer.TryRelax(data))
                    {
                        RelaxationTracker.CheckAndMarkFailed(typeKey, globalPlaced, origQty, locP.m_prioritized);
                    }
                    else if (zsP.m_locations.Count > snap)
                    {
                        relaxLoc = zsP.m_locations[snap];
                        if (isPrioritizedP)
                        {
                            Interlocked.Increment(ref _prioritizedInFlight);
                        }
                    }
                }

                if (relaxLoc != null)
                {
                    RunInlineRelaxation(zsP, relaxLoc, isPrioritizedP, workerIdxP,
                        globalPlaced, origQty, minNeeded, cfCount);

                    if (isPrioritizedP)
                    {
                        if (Interlocked.Decrement(ref _prioritizedInFlight) == 0)
                        {
                            _priorityBarrierDone.Set();
                        }
                    }
                }
            }

            lock (TranspiledCompletionHandler.AggregateSessions)
            {
                TranspiledCompletionHandler.AggregateSessions.Remove(prefab);
            }
        }

        /**
        * Inline relaxation on a worker thread. Uses GetZone for zone iteration  since relaxation is single-threaded (one worker owns the failed type, basically the one who happened to realize the failure)
        * and the original candidate cache is untouched (parallel path used copies). Can cascade recursively if further relaxation attempts are needed.
        */
        private static void RunInlineRelaxation(
            ZoneSystem zsP, ZoneLocation relaxLocP, bool isPrioritizedP, int workerIdxP,
            int priorPlacedP, int origQtyP, int minNeededP, int cfCountP)
        {
            string prefab = relaxLocP.m_prefabName;
            string typeKey = Interleaver.GetTypeKey(relaxLocP);
            int attemptNum = 1;
            if (ConstraintRelaxer.RelaxationAttempts.TryGetValue(typeKey, out int ac))
            {
                attemptNum = ac;
            }
            GenerationProgress.SetThreadSlot(workerIdxP,
                $"{prefab}  (Relaxation attempt {attemptNum})");

            List<GroupMembership> memberships = ResolveSimilarityMemberships(relaxLocP);
            List<GroupMembership> maxAdvertise = ResolveMaxAdvertiseMemberships(relaxLocP);
            List<GroupMembership> maxSearch = ResolveMaxSearchMemberships(relaxLocP);
            int budget = _outerBudgetBase;
            if (relaxLocP.m_prioritized)
            {
                budget = _outerBudgetPrioritized;
            }
            int qty = relaxLocP.m_quantity;

            ThreadSafePRNG.SeedForLts(
                WorldGenerator.instance.GetSeed() + prefab.GetStableHashCode());

            PlacementCounters relaxCtr = new PlacementCounters();
            TelemetryContext relaxTel = new TelemetryContext();

            for (int ri = 0; ri < qty; ri++)
            {
                bool placed = false;
                for (int outer = 0; outer < budget && !placed; outer++)
                {
                    if (!SurveyMode.GetZone(relaxLocP, out Vector2i zoneID))
                    {
                        relaxCtr.ZoneExhausted++;
                        break;
                    }

                    relaxCtr.ZonesExamined++;

                    if (_occupancySnapshot.ContainsKey(zoneID) ||
                        _pendingOccupancy.ContainsKey(zoneID))
                    {
                        relaxCtr.ErrOccupied++;
                        continue;
                    }

                    int relaxZoneGridIdx = -1;
                    if (WorldSurveyData.ZoneToIndex.TryGetValue(zoneID, out int rsi))
                    {
                        relaxZoneGridIdx = rsi;
                    }

                    if (EvaluateZoneParallel(relaxLocP, zoneID, relaxZoneGridIdx, memberships, maxSearch,
                                             relaxCtr, relaxTel, out Vector3 pos))
                    {
                        if (!_pendingOccupancy.TryAdd(zoneID, 1))
                        {
                            relaxCtr.ErrOccupied++;
                            continue;
                        }

                        CommitMemberships(memberships, pos);
                        CommitMaxAdvertise(maxAdvertise, pos);
                        _resultQueue.Enqueue(new PlacementResult
                        {
                            Loc = relaxLocP,
                            Position = pos,
                            Group = relaxLocP.m_group,
                            ZoneIdx = relaxZoneGridIdx,
                            ZoneID = zoneID,
                            Counters = relaxCtr
                        });

                        relaxCtr.Placed++;
                        placed = true;
                        GenerationProgress.IncrementAttempted(1);
                        GenerationProgress.IncrementPlaced(1);
                        Interlocked.Increment(ref _parallelTokensProcessed);
                    }
                }
                if (!placed)
                {
                    GenerationProgress.IncrementAttempted(1);
                    Interlocked.Increment(ref _parallelTokensProcessed);
                }
            }

            int relaxGlobalPlaced = priorPlacedP + relaxCtr.Placed;

            /**
            * Under the per-entry rekey there is no longer a "subsequent DoFlushAndRelax for this prefab" to feed as each entry owns its _inFlightRegions counter and flushes exactly once,
            * and this relaxation's outcome is reported inline below. relaxCtr therefore does not need to be parked back into _counterLists (the parent entry already flushed), so the old
            * prefab-keyed registration is gone. Keeping it would also have mis-credited a clone that happens to share this prefab. I will not remember any of this pre EWD 1.66 by September anyway.
            */

            lock (TranspiledCompletionHandler.AggregateSessions)
            {
                TranspiledCompletionHandler.AggregateSessions[prefab] = relaxTel;
            }

            bool relaxIsSuccess = relaxGlobalPlaced >= origQtyP
                || (ConstraintRelaxer.RelaxationAttempts.TryGetValue(typeKey, out int rc2) && rc2 > 0
                    && relaxGlobalPlaced >= minNeededP);

            int relaxDisplayQty = origQtyP;
            if (relaxIsSuccess && relaxGlobalPlaced < origQtyP)
            {
                relaxDisplayQty = minNeededP;
            }
            ReportData relaxData = BuildReportData(relaxLocP, relaxCtr, relaxGlobalPlaced, relaxDisplayQty,
                relaxGlobalPlaced >= origQtyP);

            if (relaxIsSuccess)
            {
                RelaxationTracker.MarkRelaxationSucceeded(typeKey);
                if (!_minimalLogging)
                {
                    int rc3 = 0;
                    if (ConstraintRelaxer.RelaxationAttempts.TryGetValue(typeKey, out int r3))
                    {
                        rc3 = r3;
                    }
                    DiagnosticLog.WriteTimestampedLog(
                        $"[RELAXATION SUCCESS] {prefab} placed {relaxGlobalPlaced}/{relaxDisplayQty} " +
                        $"after {rc3} relaxation(s). " +
                        ConstraintRelaxer.GetRelaxationSummary(prefab, relaxLocP),
                        BepInEx.Logging.LogLevel.Message);
                    ReportFormatter.WriteReport(relaxData, false, prefab);
                }
            }
            else
            {
                if (!_minimalLogging)
                {
                    ReportFormatter.WriteReport(relaxData, false, prefab);
                }

                // Cascade: attempt further relaxation.
                lock (_ltsCompletionLock)
                {
                    int snap2 = zsP.m_locations.Count;
                    if (!ConstraintRelaxer.TryRelax(relaxData))
                    {
                        RelaxationTracker.CheckAndMarkFailed(typeKey, relaxGlobalPlaced, origQtyP, relaxLocP.m_prioritized);
                    }
                    else if (zsP.m_locations.Count > snap2)
                    {
                        ZoneLocation nextRelaxLoc = zsP.m_locations[snap2];
                        if (isPrioritizedP)
                        {
                            Interlocked.Increment(ref _prioritizedInFlight);
                        }

                        RunInlineRelaxation(zsP, nextRelaxLoc, isPrioritizedP,
                            workerIdxP, relaxGlobalPlaced, origQtyP, minNeededP, cfCountP);

                        if (isPrioritizedP)
                        {
                            if (Interlocked.Decrement(ref _prioritizedInFlight) == 0)
                            {
                                _priorityBarrierDone.Set();
                            }
                        }
                    }
                }
            }

            lock (TranspiledCompletionHandler.AggregateSessions)
            {
                TranspiledCompletionHandler.AggregateSessions.Remove(prefab);
            }
        }

        /**
        * Thread-safe dart evaluation with identical filter chain to EvaluateZone but uses ThreadSafePRNG instead of UnityEngine.Random.
        * Does NOT call RegisterLocation (main-thread-only). Instead, returns the position via out parameter for the caller to enqueue into _resultQueue.
        * PIA god method. Could not be helped. Maybe it could. 
        */
        private static bool EvaluateZoneParallel(
            ZoneLocation locP, Vector2i zoneIDP, int zoneGridIdxP,
            List<GroupMembership> membershipsP, List<GroupMembership> maxSearchP,
            PlacementCounters ctrP, TelemetryContext telCtxP,
            out Vector3 position)
        {
            position = Vector3.zero;
            Vector3 zonePos = ZoneSystem.GetZonePos(zoneIDP);

            for (int di = 0; di < _dartsPerZone; di++)
            {
                ctrP.DartsThrown++;
                float rx = ThreadSafePRNG.NextFloat(-32f + locP.m_exteriorRadius, 32f - locP.m_exteriorRadius);
                float rz = ThreadSafePRNG.NextFloat(-32f + locP.m_exteriorRadius, 32f - locP.m_exteriorRadius);
                Vector3 p = zonePos + new Vector3(rx, 0f, rz);

                float dist = new Vector2(p.x, p.z).magnitude;
                if (locP.m_minDistance > 0f && dist < locP.m_minDistance)
                {
                    ctrP.ErrDist++;
                    TelemetryHelpers.TrackDistanceFailureCtx(telCtxP, dist, locP.m_minDistance, locP.m_maxDistance);
                    continue;
                }
                if (locP.m_maxDistance > 0f && dist > locP.m_maxDistance)
                {
                    ctrP.ErrDist++;
                    TelemetryHelpers.TrackDistanceFailureCtx(telCtxP, dist, locP.m_minDistance, locP.m_maxDistance);
                    continue;
                }

                Heightmap.Biome dartBiome = WorldGenerator.instance.GetBiome(p);
                if ((dartBiome & locP.m_biome) == 0)
                {
                    ctrP.ErrBiome++;
                    TelemetryHelpers.CaptureWrongBiomeCtx(telCtxP, dartBiome);
                    continue;
                }

                float rawAlt = WorldGenerator.instance.GetHeight(p.x, p.z);
                p.y = rawAlt;
                float alt = rawAlt - 30.0f;

                if (alt < locP.m_minAltitude || alt > locP.m_maxAltitude)
                {
                    ctrP.ErrAlt++;
                    TelemetryHelpers.TrackAltitudeFailureCtx(telCtxP, alt, locP.m_minAltitude, locP.m_maxAltitude, p);
                    continue;
                }

                if (ConflictsWithSimilarMembers(p, membershipsP, dartBiome, _occupancySnapshot))
                {
                    ctrP.ErrSim++;
                    continue;
                }

                if (!SatisfiesMaxSearch(p, maxSearchP, dartBiome, _occupancySnapshot))
                {
                    ctrP.ErrNotSim++;
                    continue;
                }

                if (locP.m_maxTerrainDelta > 0f || locP.m_minTerrainDelta > 0f)
                {
                    ThreadSafeTerrainDelta.GetTerrainDelta(p, locP.m_exteriorRadius, out float delta, out _, zoneGridIdxP);
                    if (delta > locP.m_maxTerrainDelta || delta < locP.m_minTerrainDelta)
                    {
                        ctrP.ErrTerrain++;
                        continue;
                    }
                }

                if (locP.m_inForest)
                {
                    float forestFactor = WorldGenerator.GetForestFactor(p);
                    if (forestFactor < locP.m_forestTresholdMin || forestFactor > locP.m_forestTresholdMax)
                    {
                        ctrP.ErrForest++;
                        continue;
                    }
                }

                position = p;
                return true;
            }
            return false;
        }

        // Main thread only. Drains all pending results and commits them to the world.
        // No cap instead drain everything available. Workers are on ThreadPool and never blocked by main-thread timing.
        private static void DrainAndCommit(ZoneSystem zsP)
        {
            while (_resultQueue.TryDequeue(out PlacementResult result))
            {
                zsP.RegisterLocation(result.Loc, result.Position, false);
                if (result.ZoneIdx >= 0)
                {
                    SurveyMode.MarkZoneOccupied(result.ZoneIdx);
                }
            }
        }

        /**
         * Prioritized locations sort first ( again vanilla behaviour and what I was doing).
         * Then as I said somewhere previously, within the same priority tier, modded locations (MWL_ prefix and later others I should add) sort after vanilla types so vanilla fills its quotas first.
         * I then sort by descending exclusion radius so Landlords (huge radius) place before Tenants (small radius) for the crazy yamls that set everything prioritized breaking my elegant assumption that
         * landlords are prioritized and would be placed first. *angry dome visit*
         * Finally, I use the original list index to guarantee the sort is stable which I should be doing anyway.
         */
        private static int CompareOrderedEntries(OrderedEntry aP, OrderedEntry bP)
        {
            if (aP.Loc.m_prioritized != bP.Loc.m_prioritized)
            {
                if (aP.Loc.m_prioritized)
                {
                    return -1;
                }
                return 1;
            }

            bool aIsModded = aP.Loc.m_prefabName.StartsWith("MWL_", StringComparison.OrdinalIgnoreCase);
            bool bIsModded = bP.Loc.m_prefabName.StartsWith("MWL_", StringComparison.OrdinalIgnoreCase);
            if (aIsModded != bIsModded)
            {
                if (aIsModded)
                {
                    return 1;
                }
                return -1;
            }

            if (aP.Loc.m_minDistanceFromSimilar != bP.Loc.m_minDistanceFromSimilar)
            {
                return bP.Loc.m_minDistanceFromSimilar.CompareTo(aP.Loc.m_minDistanceFromSimilar);
            }

            return aP.OriginalIndex.CompareTo(bP.OriginalIndex);
        }

        private static void UpdateAnnulus(ZoneSystem zsP)
        {
            if (_generateLocationsProgressField != null && _parallelTotalZones > 0)
            {
                _generateLocationsProgressField.SetValue(zsP,
                    Mathf.Clamp01((float)Volatile.Read(ref _parallelTokensProcessed) / _parallelTotalZones));
            }
        }
    }
}