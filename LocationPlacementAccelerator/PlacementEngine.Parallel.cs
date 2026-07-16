// v1.0.9
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
* 1.0.8: Closed the min-distance race. The coloring was always sound - same-color blocks are >= minDist
* apart by construction - but the dispatcher never honored it. Non-interleaved dumped every color of a GT
* into the queue back to back, so workers pulled ADJACENT colors of the same group, and adjacent colors
* touch. Between HasConflict and CommitToGroup sits the rest of the dart's filters, a return, a quota claim
* and a zone claim, and the only atomic claim in that window is _pendingOccupancy, which is keyed by zone -
* useless for two placements thirty metres apart across a zone boundary. Both workers read clear, both
* committed. Rare, but real, and the reason two same-group locations occasionally sat far closer than their
* radius allows.
*
* The fix is one rule: a GT never has two colors in flight at once. GtsStream carries InFlightWorkUnits,
* raised as each unit is enqueued and dropped at the worker's completion point, and a stream only pushes its
* next color once its current one has drained to zero. It is per-stream, so distinct GTs never wait on each
* other and the first source of parallelism is untouched.
*
* That gate on its own would have cost the second source. One work unit per color means gating to one color
* leaves the GT a single unit, so a single thread, and a fat group would run alone while nine workers idled -
* exactly what spatial partitioning exists to prevent. So GetPartition now also hands back the block id, and
* each color is coarsened into chunkCount = worker-count chunks (unsigned mod of the block id). Same-color
* blocks are all >= minDist apart, so ANY grouping of them is safe: a chunk is one thread, and two chunks of
* one color only ever hold same-color blocks. Coarsening rather than one-unit-per-block keeps the build
* cheap - the unit count is colors * workers * subgroups, not colors * blocks.
*
* The dispatch is one round-robin that laps every stream each pass and yields only on the GUI cadence, never
* because a pass happened to enqueue nothing. A stream mid-color is skipped, not waited on, so the pool
* stays fed from the other GTs.
*
* ModConfig.ParallelExactSpacing turns the gate off: every color dumped at once, no barrier, AND the chunks
* collapsed back to one unit per color. The collapse is not cosmetic. The emit is color-major, so with
* chunks left in, the queue reads c0k0..c0kN before c1k0 and the pool drains a color before it can reach the
* next one - the ordering alone keeps it safe and the switch would be a lie. One unit per color is what puts
* ten adjacent colors of one group in front of ten workers. That is the pre-fix engine exactly, race
* included. It exists so the cost of the guarantee can be measured rather than argued about, and it defaults
* to ON because the guarantee is the point.
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
*     
*
* 1.0.9: maxDistanceFromSimilar. Three pieces here basicaly, the rule itself and why it inverts everything are in
* Core's header.
*
* HOST GATE. 
* So first of all a host or landlord type must place before its tenants.
* Vanilla does this by using the prioritized...fine. But the user should know that if they make their 
* own yamls with EWD or they make nonsense yamls. 
* A max type placing before its hosts exist throws every dart into an empty world and gets
* nothing. As I said vanilla happens to be safe (CharredFortress is prioritized, so it is down before FortressRuins
* is ever dispatched) but that is luck and I am not counting on luck. A stream carrying a querier holds its
* first color until every host prefab has drained. Only pure advertisers count as hosts (see Core's
* BuildMaxHosts), so the wait graph is depth one and cannot cycle. Two more things keep it deadlock-free:
* a prioritized stream only ever waits on prioritized hosts (waiting on a non-prioritized one would wait
* across the priority barrier), and a host in my own stream is dropped, since a
* stream cannot wait on itself. That last one is a real gap for a yaml where landlord and tenant share
* both m_group and m_groupMax which does not happen in vanilla, nothing I have seen anywhere, and I would rather leave it visible
* here than paper it with a tier sort I already decided against.
*
* One crossing deliberately even though in theory maxdistancefromsimilar would benefit from a revisit.
* A max querier walks its candidate zones exactly once, same as everything else. 
* I went a bit bananas on this one so it is worth writing down why not for the future.
*
* Vanilla's outer loop samples zones at random WITH replacement, 100k (or 200k) times, so it keeps stumbling back
* onto spots it rejected before a host landed near them. I walk each candidate once, so I only catch that
* when the zone happens to sort after the placement that made it legal. The gap is real but it is small
* and it is not worth what closing it costs: a second crossing re-throws every dart through slope, biome
* and altitude, and those rejections are deterministic as the zone was the wrong biome the first time and
* it is the wrong biome now. That is the whole per-zone cost paid again to buy back the last handful of a
* single type. Vanilla budgets for the shortfall anyway for a single prefab the what do you call it, and they want 100 of those.
* In my experience vanilla has the quantities written assuming roughly 95% density on a 10k radius world.
*
* And I get the chaining for free where it happens naturally, because the grid is live: every placement
* commits its own circle, so a dart thrown later near an earlier FortressRuins passes on the same bit read
* with no bookkeeping at all. No list of host positions, nothing to maintain, nothing to invalidate.
*
* CHUNK COLLAPSE FIX. chunkCount had two unrelated jobs living in one variable, and the OFF branch of
* ParallelExactSpacing only had business with one of them.
*
* Job 1: when a sub-group DOES have a similarity radius, its zones are graph-colored into partitions,
* and chunkCount decides whether a color is split further across threads or dispatched as one unit.
* ParallelExactSpacing OFF collapsing this to 1 is the intended tradeoff - threads may then cross a
* partition boundary within a color, which is the source of the rare ~1.4m spacing violation the flag
* exists to permit in exchange for speed. This is the ONLY thing the flag is about.
*
* Job 2: when a sub-group has NO similarity radius (minDist 0), there is no coloring at all - it is the
* Single tier, one color by construction, nothing to keep apart. Here chunkCount was only ever doing
* "how many pieces do I split this type's zone list into for the thread pool", which has no relationship
* to boundary races because there are no boundaries. The old code collapsed it to 1 anyway, purely
* because both cases ran through the same three lines not because OFF was ever supposed to reach it.
* The result was that something like 700 InfestedTrees, 500 road posts, every one of the 31 vanilla types 
* with no min rule was STUPIDLY single-threaded, regardless of what ParallelExactSpacing was set to.
* So.. f................ck that. ffs. 
*
* subGroupHasSimilarityRadius restricts the collapse to Job 1. If ParallelExactSpacing is ever removed
* from the config entirely as I am considering doing, Job 2 needs no equivalent.
* There was never a real tradeoff there, only an accidental one. See SpatialPartitionAlgorithms v3 for the caller-side half:
* the Single tier now hands back the zone coordinate instead of a constant 0, which is what lets these types spread across chunks
* once chunkCount is no longer being forced to 1 underneath them.*/
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
        *
        * OwnerStream is how a worker finds the counter to drop when it is done. The queue hands out units with
        * no memory of where they came from, and the color gate lives on the stream, so the unit has to carry
        * the way back.
        */
        private class WorkUnit
        {
            public List<TypeRegionWork> TypeWork;
            public bool IsPrioritized;
            public GtsStream OwnerStream;
        }

        /**
        * Every work unit of one color of one sub-group. These run together: the geometry guarantees that any
        * two blocks of the same color are at least minDist apart, so no two of these units can produce a
        * conflicting pair no matter how the pool interleaves them. It is only the NEXT color that has to wait.
        */
        private class ColorBatch
        {
            public List<WorkUnit> WorkUnits;
        }

        private class TypeRegionWork
        {
            public ZoneLocation Loc;
            public string Group;
            public PresenceGrid Grid;

            // Null unless this type queries maxDistanceFromSimilar. Resolved at build time so the dart
            // loop never touches the registry.
            public PresenceGrid MaxGrid;

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

            /**
            * Work units of this stream that are queued or running. The dispatcher will not open the next color
            * until this reads zero, which is what keeps two colors of one GT from ever being concurrent.
            * No volatile keyword: it triggers CS0420 when passed to Interlocked by ref. Volatile.Read on the
            * dispatcher side gives the same acquire semantics without the warning.
            */
            public int InFlightWorkUnits;

            /**
            * The _inFlightRegions counters this stream must see at zero before it opens its first color.
            * Empty for every stream on a vanilla world except FortressRuins'. Boxes rather than names so
            * the gate is a handful of reads and no hashing.
            */
            public List<StrongBox<int>> HostRegionCounters;
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

            const long YieldIntervalMs = 100;// works well in the mt case. 
            Stopwatch yieldSw = Stopwatch.StartNew();

            /**
            * One dispatch for both scheduling modes. The interleaved flag drives the sequential path's token
            * model; it has no business forking this, because the right way to feed the pool is the same either
            * way - hand distinct GTs to distinct threads, and let a GT spread across the leftovers by its own
            * chunks when it is the only one left.
            *
            * exactSpacing OFF is the pre-fix engine: every color of every stream dumped at once, no gate. Two
            * adjacent colors of one GT then run concurrently and the min-distance race is live again. It is
            * here to be measured against, not to be shipped.
            */
            bool exactSpacing = ModConfig.ParallelExactSpacing.Value;

            if (!exactSpacing)
            {
                IEnumerator dumpPrio = DumpStreams(zsP, gtsStreams, true, yieldSw, YieldIntervalMs);
                while (dumpPrio.MoveNext())
                {
                    yield return dumpPrio.Current;
                }

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

                IEnumerator dumpRest = DumpStreams(zsP, gtsStreams, false, yieldSw, YieldIntervalMs);
                while (dumpRest.MoveNext())
                {
                    yield return dumpRest.Current;
                }
            }
            else
            {
                // Phase 1: prioritized streams, color-gated.
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
                        if (PushNextColor(stream))
                        {
                            anyPrio = true;
                        }
                    }

                    /**
                    * Spin on the commit work rather than on a frame. A pass that enqueues nothing means every
                    * stream is mid-color, and yielding there costs a whole Unity frame - the workers drain a
                    * color in a millisecond or two and then the pool starves until the next frame lets me
                    * notice. So I only yield on the GUI cadence and spend the wait committing placements the
                    * workers have already produced.
                    */
                    DrainAndCommit(zsP);
                    UpdateAnnulus(zsP);
                    if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
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

                // Phase 2: non-prioritized streams, same gate.
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
                        if (PushNextColor(stream))
                        {
                            anyLeft = true;
                        }
                    }

                    DrainAndCommit(zsP);
                    UpdateAnnulus(zsP);
                    if (yieldSw.ElapsedMilliseconds >= YieldIntervalMs)
                    {
                        yieldSw.Restart();
                        yield return null;
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
                    Exception inner = t.Exception?.InnerException ?? t.Exception;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
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
        /**
        * Advances one stream by at most one color. Returns true while the stream still owes work - either it
        * has colors left or it has units in flight - which is what keeps the dispatcher lapping.
        *
        * The gate is the whole fix: I refuse to open the next color while the current one is still out. Two
        * colors of one GT are adjacent in space, so running them together is the race. Two BLOCKS of one
        * color are >= minDist apart, so running those together is free, and that is why the whole batch goes
        * in at once.
        *
        * The counter goes up BEFORE the unit is queued. A worker can pull and finish a unit between the two,
        * and if the increment trailed the enqueue the counter could read zero with work still live and I would
        * open the next color straight into the race I am trying to close.
        */
        private static bool PushNextColor(GtsStream streamP)
        {
            bool streamHasColorsLeft = streamP.CurrentSubGroup < streamP.SubGroups.Count;
            bool streamHasUnitsInFlight = Volatile.Read(ref streamP.InFlightWorkUnits) > 0;

            if (!streamHasColorsLeft && !streamHasUnitsInFlight)
            {
                return false;
            }
            if (streamHasUnitsInFlight)
            {
                return true;
            }

            /**
            * Nothing of this stream goes out while a host is still placing. Returning true without
            * enqueueing keeps the dispatcher lapping - it will come back, and meanwhile it drains
            * placements and feeds the GUI, so no frame is burned waiting.
            */
            if (StreamIsWaitingOnHosts(streamP))
            {
                return true;
            }

            SubGroupStream currentSubGroup = streamP.SubGroups[streamP.CurrentSubGroup];
            if (currentSubGroup.CurrentColorIndex >= currentSubGroup.Colors.Count)
            {
                /**
                * Sub-groups are landlord-first, and the same gate that separates colors separates them: the
                * next sub-group only opens once the previous one is fully drained, so a small-radius tenant can
                * never claim ground while its landlord is still placing.
                */
                streamP.CurrentSubGroup++;
                return true;
            }

            ColorBatch batch = currentSubGroup.Colors[currentSubGroup.CurrentColorIndex];
            for (int w = 0; w < batch.WorkUnits.Count; w++)
            {
                Interlocked.Increment(ref streamP.InFlightWorkUnits);
                _workQueue.Add(batch.WorkUnits[w]);
            }
            currentSubGroup.CurrentColorIndex++;
            return true;
        }

        private static bool StreamIsWaitingOnHosts(GtsStream streamP)
        {
            List<StrongBox<int>> hostCounters = streamP.HostRegionCounters;
            if (hostCounters == null)
            {
                return false;
            }
            for (int i = 0; i < hostCounters.Count; i++)
            {
                if (Volatile.Read(ref hostCounters[i].Value) > 0)
                {
                    return true;
                }
            }
            /**
            * Once satisfied it stays satisfied - hosts only ever finish - so drop the list and the check
            * costs one null test for the rest of the run.
            */
            streamP.HostRegionCounters = null;
            return false;
        }

        /**
        * The ungated path: everything at once, colors of a GT concurrent, race live. Kept only as the
        * baseline to measure the gate against.
        *
        * The host wait is NOT part of what this branch exists to reproduce. It is not the spacing
        * guarantee, it is the difference between a max type placing its quota and placing nothing, and
        * switching it off would not measure a cost, it would measure a bug. So this branch waits too.
        *
        * Which means it can no longer dump in one pass. Walking the list in order and blocking on a wait
        * deadlocks the moment a querier sits ahead of its host: the host's units only enter the queue when
        * the walk REACHES it, and the walk is stopped waiting for the host to finish placing them. So it
        * laps instead, exactly like the gated dispatcher - dump whatever is open, drain, come back - and a
        * stream leaves the pending list only once it has actually gone out. Everything that is not waiting
        * still goes out on the first lap, so the ungated shape is unchanged for the 148 types that have no
        * max rule.
        */
        private static IEnumerator DumpStreams(ZoneSystem zsP, List<GtsStream> streamsP, bool prioritizedP,
                                                Stopwatch yieldSwP, long yieldIntervalMsP)
        {
            List<GtsStream> pending = new List<GtsStream>();
            foreach (GtsStream stream in streamsP)
            {
                if (stream.IsPrioritized == prioritizedP)
                {
                    pending.Add(stream);
                }
            }

            while (pending.Count > 0)
            {
                // Backwards so RemoveAt does not shuffle the ground out from under the index.
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    GtsStream stream = pending[i];
                    if (StreamIsWaitingOnHosts(stream))
                    {
                        continue;
                    }

                    foreach (SubGroupStream sg in stream.SubGroups)
                    {
                        foreach (ColorBatch batch in sg.Colors)
                        {
                            for (int w = 0; w < batch.WorkUnits.Count; w++)
                            {
                                Interlocked.Increment(ref stream.InFlightWorkUnits);
                                _workQueue.Add(batch.WorkUnits[w]);
                            }
                        }
                    }
                    pending.RemoveAt(i);
                }

                if (pending.Count > 0)
                {
                    DrainAndCommit(zsP);
                    UpdateAnnulus(zsP);
                    if (yieldSwP.ElapsedMilliseconds >= yieldIntervalMsP)
                    {
                        yieldSwP.Restart();
                        yield return null;
                    }
                }
            }
        }

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

                    /**
                    * A color is split into as many chunks as I have workers, and no further. One unit per block
                    * would be the obvious read of the geometry, but it buys nothing: I only need enough units to
                    * saturate the pool, and it would explode the build (thousands of units, each with its own list
                    * and counters, all allocated single-threaded before a single dart is thrown). Coarsening is
                    * free of risk because same-color blocks are mutually >= minDist, so any partition of them into
                    * chunks is safe - a chunk runs on one thread, and two chunks of a color only ever hold
                    * same-color blocks.
                    *
                    * Ungated mode collapses that to one unit per color. It has to: the emit below is color-major,
                    * so with chunks the queue reads c0k0..c0kN, c1k0..c1kN and the pool drains a whole color before
                    * it ever reaches the next one - the ordering does the gate's job by accident and nothing races.
                    * One unit per color puts ten DIFFERENT colors of one group in front of ten workers, and
                    * adjacent colors touch. That is the pre-fix engine, and reproducing it exactly is the only
                    * reason this branch exists.
                    */
                    int chunkCount = Math.Max(1, _parallelThreadCount);
                    bool subGroupHasSimilarityRadius = sgMinDist > 0f;
                    if (!ModConfig.ParallelExactSpacing.Value && subGroupHasSimilarityRadius)
                    {
                        chunkCount = 1;
                    }
                    int bucketCount = partitionCount * chunkCount;

                    // Build per-bucket, per-type zone sublists. Candidate fetch + inline partition computation in one pass.
                    Dictionary<string, List<Vector2i>>[] partitions = new Dictionary<string, List<Vector2i>>[bucketCount];
                    for (int p = 0; p < bucketCount; p++)
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
                            SpatialPartitionAlgorithms.GetPartition(zone, ref rule, out int colorIndex, out int blockId);
                            // Unsigned so the block id's sign bit folds into the range instead of producing a negative chunk.
                            int chunk = (int)((uint)blockId % (uint)chunkCount);
                            int bucket = colorIndex * chunkCount + chunk;
                            bool hasZoneList = partitions[bucket].TryGetValue(prefabName, out List<Vector2i> zoneList);
                            if (!hasZoneList)
                            {
                                zoneList = new List<Vector2i>();
                                partitions[bucket][prefabName] = zoneList;
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
                        for (int p = 0; p < bucketCount; p++)
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
                        Colors = new List<ColorBatch>(),
                        CurrentColorIndex = 0
                    };

                    for (int c = 0; c < partitionCount; c++)
                    {
                        ColorBatch batch = new ColorBatch { WorkUnits = new List<WorkUnit>() };

                        for (int k = 0; k < chunkCount; k++)
                        {
                            int bucket = c * chunkCount + k;
                            List<TypeRegionWork> typeWork = new List<TypeRegionWork>();

                            foreach (OrderedEntry entry in sgEntries)
                            {
                                string prefabName = entry.Loc.m_prefabName;
                                bool hasZones = partitions[bucket].TryGetValue(prefabName, out List<Vector2i> zones);
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
                                    MaxGrid = ResolveMaxGrid(entry.Loc),
                                    Zones = zones,
                                    Counters = ctr,
                                    TelCtx = telCtx
                                });
                            }

                            if (typeWork.Count > 0)
                            {
                                batch.WorkUnits.Add(new WorkUnit
                                {
                                    TypeWork = typeWork,
                                    IsPrioritized = stream.IsPrioritized,
                                    OwnerStream = stream
                                });
                            }
                        }

                        if (batch.WorkUnits.Count > 0)
                        {
                            sgs.Colors.Add(batch);
                        }
                    }

                    /**
                    * A sub-group with no candidates anywhere still needs one batch to exist, because the sentinel
                    * for a zero-candidate type is attached to the first color of the first sub-group and its
                    * flush is what releases the priority barrier. Dropping the empty batch starves the sentinel
                    * and the barrier never opens.
                    */
                    if (sgs.Colors.Count == 0)
                    {
                        sgs.Colors.Add(new ColorBatch { WorkUnits = new List<WorkUnit>() });
                    }

                    stream.SubGroups.Add(sgs);
                }

                streams.Add(stream);
            }

            AttachHostGates(streams, gtsMap, gtsOrder);

            // Compute total zones for annulus denominator and per-prefab tracking.
            int totalZones = 0;
            Dictionary<string, int> prefabZones = new Dictionary<string, int>(StringComparer.Ordinal);
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
                                string prefabName = tw.Loc.m_prefabName;
                                prefabZones.TryGetValue(prefabName, out int cur);
                                prefabZones[prefabName] = cur + tw.Zones.Count;
                            }
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

                    GtsStream targetStream = null;
                    for (int i = 0; i < streams.Count; i++)
                    {
                        if (streams[i].GroupKey == grpKey)
                        {
                            targetStream = streams[i];
                            break;
                        }
                    }

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
                                MaxGrid = ResolveMaxGrid(entry.Loc),
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
                        targetStream.SubGroups[0].Colors[0].WorkUnits.Add(sentinelWu);
                    }
                }
            }

            return streams;
        }

        /**
        * Hangs each querying stream's host counters on it. Runs once over the streams; on a vanilla world
        * exactly one stream comes out with a non-null list and it has one entry.
        *
        * Two exclusions, both load-bearing and both explained in the file header: a prioritized stream
        * never waits on a non-prioritized host (that would wait across the priority barrier, and the
        * barrier is waiting on me), and a host that lives in my own stream is dropped (a stream cannot
        * wait on itself).
        */
        private static void AttachHostGates(List<GtsStream> streamsP,
                                            Dictionary<string, List<OrderedEntry>> gtsMapP,
                                            List<string> gtsOrderP)
        {
            if (_maxHostsByPrefab == null || _maxHostsByPrefab.Count == 0)
            {
                return;
            }

            // Prefab --> the group key of the stream it lives in, so I can spot a host that is one of mine.
            Dictionary<string, string> streamKeyByPrefab = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, bool> priorityByPrefab = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string grpKey in gtsOrderP)
            {
                foreach (OrderedEntry entry in gtsMapP[grpKey])
                {
                    streamKeyByPrefab[entry.Loc.m_prefabName] = grpKey;
                    priorityByPrefab[entry.Loc.m_prefabName] = entry.Loc.m_prioritized;
                }
            }

            foreach (GtsStream stream in streamsP)
            {
                List<StrongBox<int>> gateCounters = null;

                foreach (OrderedEntry entry in gtsMapP[stream.GroupKey])
                {
                    List<string> hosts = GetMaxHostPrefabs(entry.Loc.m_prefabName);
                    if (hosts == null || hosts.Count == 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < hosts.Count; i++)
                    {
                        string hostPrefab = hosts[i];

                        bool hostIsInThisStream = streamKeyByPrefab.TryGetValue(hostPrefab, out string hostStreamKey)
                            && hostStreamKey == stream.GroupKey;
                        if (hostIsInThisStream)
                        {
                            DiagnosticLog.WriteTimestampedLog(
                                $"[LPA] MAXQ {entry.Loc.m_prefabName} depends on {hostPrefab} but they share stream " +
                                $"{stream.GroupKey}; ordering not enforced.",
                                BepInEx.Logging.LogLevel.Warning);
                            continue;
                        }

                        bool hostIsPrioritized = false;
                        priorityByPrefab.TryGetValue(hostPrefab, out hostIsPrioritized);
                        if (stream.IsPrioritized && !hostIsPrioritized)
                        {
                            DiagnosticLog.WriteTimestampedLog(
                                $"[LPA] MAXQ prioritized {entry.Loc.m_prefabName} depends on non-prioritized " +
                                $"{hostPrefab}; ordering not enforced (would deadlock the priority barrier).",
                                BepInEx.Logging.LogLevel.Warning);
                            continue;
                        }

                        if (!_inFlightRegions.TryGetValue(hostPrefab, out StrongBox<int> hostCounter))
                        {
                            continue;
                        }
                        if (gateCounters == null)
                        {
                            gateCounters = new List<StrongBox<int>>();
                        }
                        if (!gateCounters.Contains(hostCounter))
                        {
                            gateCounters.Add(hostCounter);
                        }
                    }
                }

                stream.HostRegionCounters = gateCounters;
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

                /**
                * The color is only done when the last unit of it is done, so this drops after every TypeWork in
                * the unit has been walked - including the flush, which can still be reading grids the next color
                * would otherwise start writing.
                */
                if (unit.OwnerStream != null)
                {
                    Interlocked.Decrement(ref unit.OwnerStream.InFlightWorkUnits);
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
                                         ctr, twP.TelCtx, twP.MaxGrid, out Vector3 pos))
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
                    CommitMaxAdvertise(loc, pos);
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
                agg.ErrNotSim += ctr.ErrNotSim;
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
            PresenceGrid maxGrid = ResolveMaxGrid(relaxLocP);
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
                                             relaxCtr, relaxTel, maxGrid, out Vector3 pos))
                    {
                        if (!_pendingOccupancy.TryAdd(zoneID, 1))
                        {
                            relaxCtr.ErrOccupied++;
                            continue;
                        }

                        CommitToGroup(group, pos);
                        CommitMaxAdvertise(relaxLocP, pos);
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
            PresenceGrid maxGridP,
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

                /**
                * Mirror of Core's. No lock, no snapshot, no confirmation: max is monotone, so the worst a
                * stale read can do is turn down a spot that just became legal, and the next crossing picks
                * it up. This is the one similarity check that does not need the color gate behind it.
                */
                if (maxGridP != null && !maxGridP.HasConflict(p))
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