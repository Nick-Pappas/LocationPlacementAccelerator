// v1.0.4
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
*   - _remainingToPlace / _inFlightRegions: per-prefab StrongBox<int>,
*     mutated via Interlocked. Workers decrement; zero triggers flush.
*   - _resultQueue: ConcurrentQueue, lock-free enqueue from workers,
*     dequeue on main thread only.
*   - _pendingOccupancy: ConcurrentDictionary, workers TryAdd to claim zones.
*   - PresenceGrid: lock-free CAS per cell (see PresenceGrid.cs).
*   - RegisterLocation: main thread only, called in DrainAndCommit.
*   
*   Almost made me rename the mod from LPA to PIA.
*   God class, with lots of god methods. Enjoy, me reading this a year from now.
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

        private static ConcurrentDictionary<Vector2i, byte> _pendingOccupancy;
        private static Dictionary<Vector2i, LocationInstance> _occupancySnapshot;

        // Per-prefab: how many region WorkUnits remain to be processed. Last decrement to 0 fires DoFlushAndRelax.
        private static ConcurrentDictionary<string, StrongBox<int>> _inFlightRegions;

        // Per-prefab: how many placements are still needed. Workers decrement on successful placement, stop when <= 0.
        private static ConcurrentDictionary<string, StrongBox<int>> _remainingToPlace;

        /**
        * Per-prefab counter/telemetry lists - one entry per region that contains zones for the type.
        * Pre-allocated on main thread during BuildSpatialStreams.
        * Workers write to their own pre-assigned instances (never to the list), aggregated by one worker at flush.
        */
        private static ConcurrentDictionary<string, List<PlacementCounters>> _counterLists;
        private static ConcurrentDictionary<string, List<TelemetryContext>> _telemetryLists;

        private static ConcurrentDictionary<string, byte> _startedPrefabs;
        private static object _ltsCompletionLock;

        private static int _parallelTokensProcessed;
        private static int _parallelTotalZones;

        private static ConcurrentDictionary<string, int> _totalZonesPerPrefab;

        private struct OrderedEntry
        {
            public ZoneLocation Loc;
            public int BaseQty;
        }

        /**
        * A spatial region of a GT. Contains per-type zone sublists.
        * Workers process TypeWork entries sequentially (sieve order),then pull the next WorkUnit from the queue.
        */
        private class WorkUnit
        {
            public List<TypeRegionWork> TypeWork;
            public bool IsPrioritized;
        }

        private class TypeRegionWork
        {
            public ZoneLocation Loc;
            public string Group;
            public PresenceGrid Grid;
            public List<Vector2i> Zones;
            public PlacementCounters Counters;
            public TelemetryContext TelCtx;
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
        }

        private class SubGroupStream
        {
            public float MinDistFromSimilar;
            public Queue<WorkUnit> WorkUnits;
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
                // EWD-mirror: blueprint locations have an empty AssetID + name-only
                // SoftReference. The old m_prefab.IsValid check rejected them before
                // they ever hit the work queue. IsValidLocation matches EWD's own
                // IdManager.IsValid so blueprints now survive into RunParallelPath.
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
                ordered.Add(new OrderedEntry { Loc = loc, BaseQty = baseQty });
            }

            ordered.Sort(CompareOrderedEntries);

            _workQueue = new BlockingCollection<WorkUnit>();
            _resultQueue = new ConcurrentQueue<PlacementResult>();
            _pendingOccupancy = new ConcurrentDictionary<Vector2i, byte>();
            _occupancySnapshot = new Dictionary<Vector2i, LocationInstance>(zsP.m_locationInstances);
            _ltsCompletionLock = new object();
            _startedPrefabs = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _priorityBarrierDone = new ManualResetEventSlim(false);
            _parallelTokensProcessed = 0;

            _inFlightRegions = new ConcurrentDictionary<string, StrongBox<int>>(StringComparer.Ordinal);
            _remainingToPlace = new ConcurrentDictionary<string, StrongBox<int>>(StringComparer.Ordinal);
            _counterLists = new ConcurrentDictionary<string, List<PlacementCounters>>(StringComparer.Ordinal);
            _telemetryLists = new ConcurrentDictionary<string, List<TelemetryContext>>(StringComparer.Ordinal);
            _totalZonesPerPrefab = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

            _parallelTotalZones = 0;
            _prioritizedInFlight = 0;
            foreach (OrderedEntry entry in ordered)
            {
                string prefabName = entry.Loc.m_prefabName;
                _remainingToPlace[prefabName] = new StrongBox<int>(entry.BaseQty);
                _inFlightRegions[prefabName] = new StrongBox<int>(0);
                _counterLists[prefabName] = new List<PlacementCounters>();
                _telemetryLists[prefabName] = new List<TelemetryContext>();
                if (entry.Loc.m_prioritized)
                {
                    _prioritizedInFlight++;
                }
            }
            if (_prioritizedInFlight == 0)
            {
                _priorityBarrierDone.Set();
            }

            List<GtsStream> gtsStreams = BuildSpatialStreams(ordered);

            GenerationProgress.InitThreadSlots(_parallelThreadCount);
            Task[] workerTasks = new Task[_parallelThreadCount];
            for (int w = 0; w < _parallelThreadCount; w++)
            {
                int idx = w;
                workerTasks[w] = Task.Run(() => WorkerBody(zsP, idx));
            }

            bool crossedPriority = false;
            const long YieldIntervalMs = 100;// works well in the mt case. 
            Stopwatch yieldSw = Stopwatch.StartNew();

            if (_interleavedScheduling)
            {
                // Phase 1: round-robin prioritized streams until exhausted.
                bool anyPrio = true;
                while (anyPrio)
                {
                    anyPrio = false;
                    foreach (GtsStream stream in gtsStreams)
                    {
                        if (!stream.IsPrioritized)
                        {
                            continue;
                        }
                        if (stream.CurrentSubGroup >= stream.SubGroups.Count)
                        {
                            continue;
                        }
                        SubGroupStream csg = stream.SubGroups[stream.CurrentSubGroup];
                        if (csg.WorkUnits.Count == 0)
                        {
                            stream.CurrentSubGroup++;
                            if (stream.CurrentSubGroup < stream.SubGroups.Count)
                            {
                                anyPrio = true;
                            }
                            continue;
                        }

                        int batch = 1;
                        if (stream.SubGroups.Count > 1)
                        {
                            batch = Math.Min(_parallelThreadCount, csg.WorkUnits.Count);
                        }
                        for (int b = 0; b < batch && csg.WorkUnits.Count > 0; b++)
                        {
                            _workQueue.Add(csg.WorkUnits.Dequeue());
                        }
                        anyPrio = true;
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
                    foreach (GtsStream stream in gtsStreams)
                    {
                        if (stream.IsPrioritized)
                        {
                            continue;
                        }
                        if (stream.CurrentSubGroup >= stream.SubGroups.Count)
                        {
                            continue;
                        }
                        SubGroupStream csg = stream.SubGroups[stream.CurrentSubGroup];
                        if (csg.WorkUnits.Count == 0)
                        {
                            stream.CurrentSubGroup++;
                            if (stream.CurrentSubGroup < stream.SubGroups.Count)
                            {
                                anyLeft = true;
                            }
                            continue;
                        }

                        int batch = 1;
                        if (stream.SubGroups.Count > 1)
                        {
                            batch = Math.Min(_parallelThreadCount, csg.WorkUnits.Count);
                        }
                        for (int b = 0; b < batch && csg.WorkUnits.Count > 0; b++)
                        {
                            _workQueue.Add(csg.WorkUnits.Dequeue());
                        }
                        anyLeft = true;
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
                        while (sg.WorkUnits.Count > 0)
                        {
                            _workQueue.Add(sg.WorkUnits.Dequeue());
                        }
                    }
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
                    Exception inner = t.Exception.InnerException;
                    if (inner != null)
                    {
                        throw inner;
                    }
                    throw t.Exception;
                }
            }

            // Final drain - exhaust everything remaining in the queue.
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

                Dictionary<string, PlacementCounters> rCtrs = new Dictionary<string, PlacementCounters>(StringComparer.Ordinal);
                Dictionary<string, ZoneLocation> rRep = new Dictionary<string, ZoneLocation>(StringComparer.Ordinal);
                foreach (ZoneLocation rx in relaxLocs)
                {
                    if (!rx.m_enable || rx.m_centerFirst)
                    {
                        continue;
                    }
                    string prefabName = rx.m_prefabName;
                    // Skip if relaxation already succeeded inline on a worker thread.
                    if (RelaxationTracker.IsRelaxationSucceeded(prefabName))
                    {
                        continue;
                    }
                    bool hasRCtr = rCtrs.ContainsKey(prefabName);
                    if (!hasRCtr)
                    {
                        rCtrs[prefabName] = new PlacementCounters();
                        rRep[prefabName] = rx;
                    }
                    IEnumerator it = RunLocSerial(zsP, rx, rCtrs[prefabName], suppressFlushP: true);
                    while (it.MoveNext())
                    {
                        yield return it.Current;
                    }
                }
                foreach (KeyValuePair<string, ZoneLocation> k in rRep)
                {
                    FlushLTS(zsP, k.Value, rCtrs[k.Key]);
                    TranspiledCompletionHandler.AggregateSessions.Remove(k.Key);
                }
            }

            GenerationProgress.ClearThreadSlots();
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

            SurveyMode.DumpDiagnostics();
            DiagnosticLog.DumpPlacementsToFile();
            GenerationProgress.CurrentLocation = null;
            RelaxationTracker.MarkPlacementComplete();
            GenerationProgress.EndGeneration();

            _workQueue?.Dispose();
            _workQueue = null;
            _priorityBarrierDone?.Dispose();
            _priorityBarrierDone = null;
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
                    IsPrioritized = gtsPriority[grpKey]
                };

                foreach (float sgMinDist in subGroupDists)
                {
                    List<OrderedEntry> sgEntries = subGroupMap[sgMinDist];

                    PartitionRule rule = SpatialPartitionAlgorithms.BuildRule(sgMinDist, _parallelThreadCount);
                    int partitionCount = rule.PartitionCount;

                    // Build per-partition, per-type zone sublists. Candidate fetch + inline partition computation in one pass.
                    Dictionary<string, List<Vector2i>>[] partitions = new Dictionary<string, List<Vector2i>>[partitionCount];
                    for (int p = 0; p < partitionCount; p++)
                    {
                        partitions[p] = new Dictionary<string, List<Vector2i>>(StringComparer.Ordinal);
                    }

                    int totalCandidateZones = 0;
                    foreach (OrderedEntry entry in sgEntries)
                    {
                        string prefabName = entry.Loc.m_prefabName;
                        List<Vector2i> candidates = SurveyMode.GetOrBuildCandidateList(entry.Loc);
                        totalCandidateZones += candidates.Count;

                        foreach (Vector2i zone in candidates)
                        {
                            int partition = SpatialPartitionAlgorithms.GetPartition(zone, ref rule);
                            bool hasZoneList = partitions[partition].TryGetValue(prefabName, out List<Vector2i> zoneList);
                            if (!hasZoneList)
                            {
                                zoneList = new List<Vector2i>();
                                partitions[partition][prefabName] = zoneList;
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

                    // Track how many regions each prefab appears in.
                    foreach (OrderedEntry entry in sgEntries)
                    {
                        string prefabName = entry.Loc.m_prefabName;
                        int regionCount = 0;
                        for (int p = 0; p < partitionCount; p++)
                        {
                            bool hasZones = partitions[p].TryGetValue(prefabName, out List<Vector2i> zoneList);
                            if (hasZones && zoneList.Count > 0)
                            {
                                regionCount++;
                            }
                        }
                        Interlocked.Add(ref _inFlightRegions[prefabName].Value, regionCount);
                    }

                    PresenceGrid grid = PresenceGrid.GetOrCreate($"{grpKey}:{sgMinDist:F0}");

                    SubGroupStream sgs = new SubGroupStream
                    {
                        MinDistFromSimilar = sgMinDist,
                        WorkUnits = new Queue<WorkUnit>()
                    };

                    for (int p = 0; p < partitionCount; p++)
                    {
                        List<TypeRegionWork> typeWork = new List<TypeRegionWork>();

                        foreach (OrderedEntry entry in sgEntries)
                        {
                            string prefabName = entry.Loc.m_prefabName;
                            bool hasZones = partitions[p].TryGetValue(prefabName, out List<Vector2i> zones);
                            if (!hasZones || zones.Count == 0)
                            {
                                continue;
                            }

                            PlacementCounters ctr = new PlacementCounters();
                            TelemetryContext telCtx = new TelemetryContext();
                            _counterLists[prefabName].Add(ctr);
                            _telemetryLists[prefabName].Add(telCtx);

                            typeWork.Add(new TypeRegionWork
                            {
                                Loc = entry.Loc,
                                Group = grpKey,
                                Grid = grid,
                                Zones = zones,
                                Counters = ctr,
                                TelCtx = telCtx
                            });
                        }

                        if (typeWork.Count > 0)
                        {
                            sgs.WorkUnits.Enqueue(new WorkUnit
                            {
                                TypeWork = typeWork,
                                IsPrioritized = stream.IsPrioritized
                            });
                        }
                    }

                    stream.SubGroups.Add(sgs);
                }

                streams.Add(stream);
            }

            // Compute total zones for annulus denominator and per-prefab tracking.
            int totalZones = 0;
            Dictionary<string, int> prefabZones = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (GtsStream s in streams)
            {
                foreach (SubGroupStream sg in s.SubGroups)
                {
                    foreach (WorkUnit wu in sg.WorkUnits)
                    {
                        foreach (TypeRegionWork tw in wu.TypeWork)
                        {
                            totalZones += tw.Zones.Count;
                            string prefabName = tw.Loc.m_prefabName;
                            prefabZones.TryGetValue(prefabName, out int cur);
                            prefabZones[prefabName] = cur + tw.Zones.Count;
                        }
                    }
                }
            }
            _parallelTotalZones = Math.Max(1, totalZones);
            foreach (KeyValuePair<string, int> kvp in prefabZones)
            {
                _totalZonesPerPrefab[kvp.Key] = kvp.Value;
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
                    string prefabName = entry.Loc.m_prefabName;
                    if (_inFlightRegions[prefabName].Value > 0)
                    {
                        continue;
                    }

                    _inFlightRegions[prefabName] = new StrongBox<int>(1);

                    PlacementCounters sentinelCtr = new PlacementCounters();
                    TelemetryContext sentinelTel = new TelemetryContext();
                    _counterLists[prefabName].Add(sentinelCtr);
                    _telemetryLists[prefabName].Add(sentinelTel);

                    string grp = entry.Loc.m_prefabName;
                    if (!string.IsNullOrEmpty(entry.Loc.m_group))
                    {
                        grp = entry.Loc.m_group;
                    }
                    PresenceGrid grid = PresenceGrid.GetOrCreate(
                        $"{grp}:{entry.Loc.m_minDistanceFromSimilar:F0}");

                    //I have to say this looks horrible...
                    WorkUnit sentinelWu = new WorkUnit
                    {
                        TypeWork = new List<TypeRegionWork>
                        {
                            new TypeRegionWork
                            {
                                Loc = entry.Loc,
                                Group = grp,
                                Grid = grid,
                                Zones = new List<Vector2i>(),
                                Counters = sentinelCtr,
                                TelCtx = sentinelTel
                            }
                        },
                        IsPrioritized = entry.Loc.m_prioritized
                    };

                    GtsStream targetStream = null;
                    for (int i = 0; i < streams.Count; i++)
                    {
                        if (streams[i].GroupKey == grpKey)
                        {
                            targetStream = streams[i];
                            break;
                        }
                    }
                    if (targetStream != null && targetStream.SubGroups.Count > 0)
                    {
                        targetStream.SubGroups[0].WorkUnits.Enqueue(sentinelWu);
                    }
                }
            }

            return streams;
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

                    if (Volatile.Read(ref _remainingToPlace[prefab].Value) > 0)
                    {
                        EvaluateZoneList(tw, prefab);
                    }

                    int regionsLeft = Interlocked.Decrement(ref _inFlightRegions[prefab].Value);

                    if (regionsLeft == 0)
                    {
                        DoFlushAndRelax(zsP, tw.Loc, unit.IsPrioritized, workerIdxP);

                        if (unit.IsPrioritized)
                        {
                            if (Interlocked.Decrement(ref _prioritizedInFlight) == 0)
                            {
                                _priorityBarrierDone.Set();
                            }
                        }
                    }
                }

                GenerationProgress.SetThreadSlot(workerIdxP, null);
            }
        }

        private static void EvaluateZoneList(TypeRegionWork twP, string prefabP)
        {
            ZoneLocation loc = twP.Loc;
            PlacementCounters ctr = twP.Counters;
            string group = twP.Group;
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
            * (below-sea reclassification of vanilla AshLands zones) not a generic lava-biome
            * check. Flagged for a future pass to generalize across EWD custom lava biomes.
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
                if (Volatile.Read(ref _remainingToPlace[prefabP].Value) <= 0)
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

                if (EvaluateZoneParallel(loc, zoneID, zoneGridIdx, twP.Grid, group,
                                         ctr, twP.TelCtx, out Vector3 pos))
                {
                    // Atomically claim a placement slot. If another worker beat us to filling the quota, undo and stop.
                    if (Interlocked.Decrement(ref _remainingToPlace[prefabP].Value) < 0)
                    {
                        Interlocked.Increment(ref _remainingToPlace[prefabP].Value);
                        break;
                    }

                    // Atomically claim the zone. If another worker already placed here, undo the slot claim and continue to next zone.
                    if (!_pendingOccupancy.TryAdd(zoneID, 1))
                    {
                        Interlocked.Increment(ref _remainingToPlace[prefabP].Value);
                        ctr.ErrOccupied++;
                        continue;
                    }

                    CommitToGroup(group, pos);
                    _resultQueue.Enqueue(new PlacementResult
                    {
                        Loc = loc,
                        Position = pos,
                        Group = group,
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

        private static PlacementCounters AggregateCounters(string prefabP)
        {
            PlacementCounters agg = new PlacementCounters();
            bool hasList = _counterLists.TryGetValue(prefabP, out List<PlacementCounters> list);
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
                agg.ErrTerrain += ctr.ErrTerrain;
                agg.ErrForest += ctr.ErrForest;
            }
            return agg;
        }

        private static TelemetryContext AggregateTelemetry(string prefabP)
        {
            TelemetryContext merged = new TelemetryContext();
            bool hasList = _telemetryLists.TryGetValue(prefabP, out List<TelemetryContext> list);
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

            PlacementCounters ctr = AggregateCounters(prefab);
            TelemetryContext telCtx = AggregateTelemetry(prefab);

            int cfCount = 0;
            if (_centerFirstCounts.TryGetValue(prefab, out int cfc))
            {
                cfCount = cfc;
            }
            int globalPlaced = ctr.Placed + cfCount;
            int origQty = Interleaver.GetOriginalQuantity(prefab);
            bool isComplete = globalPlaced >= origQty;
            int minNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefab, origQty);
            bool wasRelaxed = ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefab, out int relaxCount) && relaxCount > 0;
            bool isSuccess = isComplete || (wasRelaxed && globalPlaced >= minNeeded);

            // Credit unexamined zones to the annulus progress so it stays smooth when a type fills its quota early and leaves zones unvisited.
            int totalZonesForType = 0;
            if (_totalZonesPerPrefab.TryGetValue(prefab, out int tz))
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
                RelaxationTracker.MarkRelaxationSucceeded(prefab);
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
                        RelaxationTracker.CheckAndMarkFailed(prefab, globalPlaced, origQty, locP.m_prioritized);
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
        * Inline relaxation on a worker thread. Uses GetZone for zone iteration
        * since relaxation is single-threaded (one worker owns the failed type, basically the one who happened to realize the failure)
        * and the original candidate cache is untouched (parallel path used copies). Can cascade recursively if further relaxation attempts are needed.
        */
        private static void RunInlineRelaxation(
            ZoneSystem zsP, ZoneLocation relaxLocP, bool isPrioritizedP, int workerIdxP,
            int priorPlacedP, int origQtyP, int minNeededP, int cfCountP)
        {
            string prefab = relaxLocP.m_prefabName;
            int attemptNum = 1;
            if (ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefab, out int ac))
            {
                attemptNum = ac;
            }
            GenerationProgress.SetThreadSlot(workerIdxP,
                $"{prefab}  (Relaxation attempt {attemptNum})");

            string group = relaxLocP.m_prefabName;
            if (!string.IsNullOrEmpty(relaxLocP.m_group))
            {
                group = relaxLocP.m_group;
            }
            PresenceGrid grid = PresenceGrid.GetOrCreate(
                $"{group}:{relaxLocP.m_minDistanceFromSimilar:F0}");
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

                    if (EvaluateZoneParallel(relaxLocP, zoneID, relaxZoneGridIdx, grid, group,
                                             relaxCtr, relaxTel, out Vector3 pos))
                    {
                        if (!_pendingOccupancy.TryAdd(zoneID, 1))
                        {
                            relaxCtr.ErrOccupied++;
                            continue;
                        }

                        CommitToGroup(group, pos);
                        _resultQueue.Enqueue(new PlacementResult
                        {
                            Loc = relaxLocP,
                            Position = pos,
                            Group = group,
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
            * Register relaxCtr so any subsequent DoFlushAndRelax call for this prefab
            * (from a later-finishing work unit) sees the correct globalPlaced and doesn't re-trigger TryRelax.
            */
            bool hasCounterList = _counterLists.TryGetValue(prefab, out List<PlacementCounters> ctrList);
            if (hasCounterList)
            {
                ctrList.Add(relaxCtr);
            }

            lock (TranspiledCompletionHandler.AggregateSessions)
            {
                TranspiledCompletionHandler.AggregateSessions[prefab] = relaxTel;
            }

            bool relaxIsSuccess = relaxGlobalPlaced >= origQtyP
                || (ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefab, out int rc2) && rc2 > 0
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
                RelaxationTracker.MarkRelaxationSucceeded(prefab);
                if (!_minimalLogging)
                {
                    int rc3 = 0;
                    if (ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefab, out int r3))
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
                        RelaxationTracker.CheckAndMarkFailed(prefab, relaxGlobalPlaced, origQtyP, relaxLocP.m_prioritized);
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
        * Thread-safe dart evaluation - identical filter chain to EvaluateZone
        * but uses ThreadSafePRNG instead of UnityEngine.Random.
        * Does NOT call RegisterLocation (main-thread-only). Instead, returns
        * the position via out parameter for the caller to enqueue into _resultQueue.
        * PIA god method. Could not be helped. 
        */
        private static bool EvaluateZoneParallel(
            ZoneLocation locP, Vector2i zoneIDP, int zoneGridIdxP,
            PresenceGrid groupGridP, string groupP,
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

                if (locP.m_minDistanceFromSimilar > 0f && groupGridP.HasConflict(p))
                {
                    if (!_enable3DSimilarity || !IsHighRelief(dartBiome) ||
                        Confirm3DSimilarityConflict(p, locP.m_minDistanceFromSimilar, groupP, _occupancySnapshot))
                    {
                        ctrP.ErrSim++;
                        continue;
                    }
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

            return 0;
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