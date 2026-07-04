// v1.0.10
/**
* Core of the replaced placement engine. This partial class contains:
*  Run(): the entry-point coroutine called by ReplacedEnginePatches
*  EvaluateZone(): the dart-throwing inner loop (the hot path)
*  FlushLTS / BuildReportData: per-type completion and telemetry
*  BuildTokenList: token generation for the sequential path
*  CommitToGroup: PresenceGrid rasterization for heterogeneous groups
*  3D similarity fallback for Mountain/Mistlands high-relief terrain
*
* Even now it contains everything and the kitchen sink
* but there are limits to how much I can decouple stuff.
* I have already overthought this. 
*
* The other two partial files are:
*  PlacementEngine.Sequential.cs : single-threaded token loop
*  PlacementEngine.Parallel.cs   : multi-threaded spatial partition pipeline
*
* 1.0.1: high-relief biome mask moved from a compile-time const to a runtime field
* that includes EWD custom biomes whose terrain algorithm is Mountain or Mistlands
* (Zeus's Summit, High Peak Mountain, Deep Mistlands, etc.). Populated in Run()
* via Compatibility.GetHighReliefBiomeMask(). Vanilla fallback is Mountain|Mistlands.
* 
* 1.0.2: Passed location priority into RelaxationTracker.CheckAndMarkFailed
* to support accurate failure severity tracking (Red/Orange/Yellow).
*
* 1.0.3: Mirror vanilla's ClearNonPlacedLocations at the top of Run. Without this,
* genloc on a saved world inherited every reservation vanilla had made during 
* original generation as a hard occupancy claim. Modded location types competing 
* for scarce biomes (Swamp, AshLands) had no zones left to land in. This re-aligns 
* LPA with vanilla's "non-placed reservations are disposable" semantic - real 
* m_placed=true structures are preserved, stale m_placed=false reservations are 
* swept so the placement pass starts from the same baseline as a fresh run.
*
* 1.0.4: Swapped the two strict m_prefab.IsValid pre-filters (partitions build 
* around line 215, token-list build around line 619) to Compatibility.IsValidLocation.
* EWD blueprint-based locations (Loki, Dhakhar's etc.) arrive with an empty AssetID and a 
* name-only SoftReference, so the old IsValid check was silently dropping them 
* before they ever got a token. See Compatibility.cs v1.0.2 header for the full 
* story. The m_enable / m_quantity portions of each filter are kept inline because
* the two sites have slightly different needs (partitions build doesn't gate on
* quantity, token-list build does).
*
* 1.0.5: Public API plumbing.
*
* 1.0.6: Multi-group similarity thing... Until now I resolved a location's similarity
* identity as a single (group, radius) pair off m_group-or-prefab. That is correct for  single-group and ungrouped types, but EWD's v1.64 virtualizes m_group into an opaque
* per-entry handle whenever a location belongs to more than one group, so genuine multi-group locations were reading as their own private group and silently stopped
* repelling their real group-mates. Jere says I have to thank Dhakhar for this one lol. 
* I now resolve each location once into a LIST of (group, radius) memberships via Compatibility.GetLocationGroups
* (memoized in _groupMemberships), roll every membership's radius into _groupPartitions, commit into
* every membership's group on placement, and test every membership in the similarity check and the 3D fallback. 
* A single-membership list is the old path exactly, so the common case pays nothing, is as it was.
* Note this stays NON-retroactive: each placement only tests its own radius against the current grid state, never the other way round (landlord
* places at 1000, a later tenant at 250 from it is valid the landlord's radius does not reach back. The landlord has no say). That is by design and documented, not a gap.
*
* 1.0.7: FlushLTSCore (the sequential/serial flush) routes original-quantity and relaxation state through the logical type key. origQty now comes from GetOriginalQuantity(locP) 
* the ZoneLocation overload that resolves per type, so a packet maps to its origin's quantity instead of the first prefab match.
* RelaxationAttempts and RelaxationTracker are read/called by type key to match the relaxer's type-keyed writes. 
* GetMinimumNeededCount and AggregateSessions stay prefab-keyed (config, telemetry). No change for non-clone worlds, where the type key is the prefab name.
*
* 1.0.8: Sequential sort stability fix and waterfall fix. I noticed the waterfall telemetry math was 
* subtracting the wrong error counter (ErrForest instead of ErrTerrain) in BuildReportData, making my diagnostic 
* logs slightly confusing. Corrected it. Also, the sequential sort was using List<T>.Sort which is fundamentally 
* unstable. Ties (e.g. all prioritized locations) were arbitrarily shuffled, allowing small-radius tenants to place 
* before large-radius landlords of the same group. This polluted the map with exclusion zones and starved the large 
* locations entirely. I replaced this with a total order encompassing priority, modded status, exclusion radius 
* (descending), and original snapshot index. 
*
* 1.0.9: The replaced engine now honors maxDistanceFromSimilar (max-similarity / anchor inclusion), which it
* silently ignored....(angry dome stuff).  Only the min exclusion set was ever checked. Each location resolves two more membership sets:
* an advertise set (GroupsMax, painted on placement) and a search set (advertise plus EWD search-only anchors,
* queried per dart via SatisfiesMaxSearch). Max grids live in a separate key namespace so a group reused across
* group and groupMax can never alias. Empty sets for ordinary locations, so the path costs nothing there.
* 
* 1.0.10: Placement waves (anchor tiers). The max/anchor rule only holds if a type's advertiser is committed before
* the searcher reads its grid. Vanilla gets that from strict in-order placement; my parallel path reorders freely
* across streams, so a gated satellite could otherwise search a host grid nobody had painted yet. I derive a tier per
* type from the advertise->search(radius>0) dependency in ComputeTiers (Kahn's longest-path; residual cycles, which
* are seedless symmetric groups, lump one wave above the acyclic part). The sequential path only needs tier-first
* sorting because its main loop walks the sorted list in order; the parallel path dispatches one tier at a time behind
* a completion barrier. A world with no anchoring collapses to a single tier, so ordinary generation is unchanged.
* 
* Should I remove all the archaeology from the files? I mean at this point I may end up with more comments than code.
* No no... if I return in September on 1.0 release I will want to not have to reinvent all the context from scratch.
*/
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    internal static partial class PlacementEngine
    {
        // Cached config with read once at Run() entry, never touched in hot paths.
        private static int _outerBudgetBase;
        private static int _outerBudgetPrioritized;
        private static int _dartsPerZone;
        private static int _maxRelaxationAttempts;
        private static bool _interleavedScheduling;
        private static bool _minimalLogging;
        private static bool _logSuccesses;
        private static PlacementMode _mode;
        private static bool _enable3DSimilarity;

        /**
        * Biomes that trigger the 3D similarity fallback. Vanilla default is
        * Mountain | Mistlands. When EWD is active, Compatibility.GetHighReliefBiomeMask()
        * adds any custom biome whose terrain algorithm maps to Mountain or Mistlands
        * (Summit, High Peak Mountain, Deep Mistlands, etc.). Set once in Run().
        * Should I have this configurable actually?
        */
        private static Heightmap.Biome _highReliefBiomeMask = Heightmap.Biome.Mountain | Heightmap.Biome.Mistlands;

        /**
        * Group --> set of distinct minDistanceFromSimilar values.
        * Built once at Run() time. Supports heterogeneous groups (17 known vanilla groups have exactly 2 radius partitions).
        * CommitToGroup rasterizes into ALL sub-grids so each reader's HasConflict(p) sees placements from every same-group LTS
        * regardless of that LTS's own spacing radius.
        */
        private static Dictionary<string, HashSet<float>> _groupPartitions;

        /**
        * One similarity membership of a location: a real group name, the spacing radius this
        * location uses against that group, and the matching radius sub-grid (cached so the hot path never re-resolves it). 
        * A single-group or ungrouped location has exactly one of these.
        */
        private struct GroupMembership
        {
            public string Group;
            public float Radius;
            public PresenceGrid Grid;
        }

        /**
        * Per-location resolved similarity memberships, memoized. Concurrent because worker threads
        * resolve a placed instance's memberships on the 3D similarity fallback, and BuildSpatialStreams
        * resolves entries while the main thread is elsewhere. Cleared and rebuilt each Run().
        */
        private static System.Collections.Concurrent.ConcurrentDictionary<ZoneLocation, List<GroupMembership>> _groupMemberships;

        /**
        * Max-similarity (anchor / groupMax) memberships, resolved once per location and memoized like _groupMemberships.
        * Two sets because the max relationship is directional:
        *   advertise = the groups this location makes itself findable in (EWD GroupsMax); painted on placement.
        *   search    = the groups this location must be NEAR to be allowed (advertise plus any search-only anchors); queried per dart.
        * For symmetric groupMax both sets are identical, so behaviour matches vanilla. For a directed satellite the
        * search set carries the anchor group while its advertise set stays empty, so satellites never anchor each other.
        */
        private static System.Collections.Concurrent.ConcurrentDictionary<ZoneLocation, List<GroupMembership>> _maxAdvertiseMemberships;
        private static System.Collections.Concurrent.ConcurrentDictionary<ZoneLocation, List<GroupMembership>> _maxSearchMemberships;

        /**
        * Radii committed into each max group, kept in a SEPARATE map (and grid namespace) from _groupPartitions so a
        * group name reused across group (min) and groupMax (max) can never alias one PresenceGrid into meaning both
        * "reject if near" (min exclusion) and "reject if far" (max inclusion). 
        */
        private static Dictionary<string, HashSet<float>> _groupPartitionsMax;

        /**
        * Placement tier per location type, from the anchor dependency graph (see ComputeTiers). Read by the parallel
        * dispatcher to gate one tier behind the previous, and by the sequential sort to order tiers front to back.
        */
        private static Dictionary<ZoneLocation, int> _typeTier;

        /**
        * Grid-key namespace prefix for max grids. \u001D (group separator) cannot occur in a YAML group key, so a max
        * grid key can never collide with a min grid key regardless of what a user names their groups. 
        * Seemed like a good idea at the moment. We will see. 
        */
        private const string MaxGridNamespace = "\u001Dmax\u001D";

        private static string MaxGridKey(string groupP, float radiusP)
        {
            return $"{MaxGridNamespace}{groupP}:{radiusP:F0}";
        }

        /**
        * Wrapper struct used by the sequential sort to preserve original snapshot ordering.
        * This allows me to force a stable sort out of List.Sort().
        */
        private struct SequentialSortEntry
        {
            public ZoneLocation Loc;
            public int OriginalIndex;
        }

        /**
        * Resolves (and memoizes) a location's real similarity memberships.
        * Single-group / ungrouped / no-EWD: one membership on the raw m_group-or-prefab key, whichreproduces the pre-crazyness behavior exactly. 
        * Multi-group: EWD's Api.GetLocationGroups hands back the real (group, distance) pairs that the virtual m_group handle was hiding, one membership each.
        */
        private static List<GroupMembership> ResolveSimilarityMemberships(ZoneLocation locP)
        {
            return _groupMemberships.GetOrAdd(locP, BuildMemberships);
        }

        private static List<GroupMembership> BuildMemberships(ZoneLocation locP)
        {
            List<Tuple<string, float>> groups = Compatibility.GetLocationGroups(locP, false);
            if (groups != null && groups.Count > 0)
            {
                List<GroupMembership> result = new List<GroupMembership>(groups.Count);
                for (int i = 0; i < groups.Count; i++)
                {
                    string groupName = groups[i].Item1;
                    float radius = groups[i].Item2;
                    PresenceGrid grid = null;
                    if (radius > 0f)
                    {
                        grid = PresenceGrid.GetOrCreate($"{groupName}:{radius:F0}");
                    }
                    result.Add(new GroupMembership { Group = groupName, Radius = radius, Grid = grid });
                }
                return result;
            }

            // Fallback path: vanilla, EWD absent, or the location declares no groups. One membership keyed on m_group-or-prefab at the location's own spacing radius, the legacy single grid.
            string grp = locP.m_prefabName;
            if (!string.IsNullOrEmpty(locP.m_group))
            {
                grp = locP.m_group;
            }
            float fallbackRadius = locP.m_minDistanceFromSimilar;
            PresenceGrid fallbackGrid = null;
            if (fallbackRadius > 0f)
            {
                fallbackGrid = PresenceGrid.GetOrCreate($"{grp}:{fallbackRadius:F0}");
            }
            List<GroupMembership> single = new List<GroupMembership>(1);
            single.Add(new GroupMembership { Group = grp, Radius = fallbackRadius, Grid = fallbackGrid });
            return single;
        }

        // Prefab name --> count of instances placed by CenterFirstPlacer.PlaceAll().
        // Used by the parallel path's DoFlushAndRelax to compute globalPlaced without iterating m_locationInstances (which is being mutated on main thread).
        private static Dictionary<string, int> _centerFirstCounts;

        // Private setter for ZoneSystem.LocationsGenerated - must be set to true at the end of placement or the game hangs on a black screen. :s
        private static PropertyInfo _locationsGeneratedProp;

        // ZoneSystem.m_generateLocationsProgress - drives the vanilla LoadingIndicator annulus so it tracks the placement progress. 
        private static FieldInfo _generateLocationsProgressField;

        /**
        * RNG isolation for the sequential / main-thread path.
        * Vanilla seeds UnityEngine.Random per-LTS via InitState(worldSeed + hash),
        * then restores the global state when yielding back to Unity.
        * I mirror this exactly so the dart sequence is deterministic.
        *   _outsideRngState: the global UnityEngine.Random state between frames.
        *   _insideRngState: the seeded dart sequence mid-LTS, preserved across yields.
        *   _rngIsolationActive: true while inside an LTS's dart sequence.
        */
        private static UnityEngine.Random.State _outsideRngState;
        private static UnityEngine.Random.State _insideRngState;
        private static bool _rngIsolationActive;

        private static bool _parallelPlacement;
        private static int _parallelThreadCount;

        public static IEnumerator Run(ZoneSystem zsP)
        {
            if (!MinimapParallelizer.GenerationComplete)
            {
                while (!MinimapParallelizer.GenerationComplete)
                {
                    yield return null;
                }
            }

            if (Compatibility.IsBetterContinentsActive && !Compatibility.BCMinimapDone)
            {
                while (!Compatibility.BCMinimapDone)
                {
                    yield return null;
                }
            }

            float outerMult = ApiState.Options?.OuterMultiplier ?? ModConfig.OuterMultiplier.Value;
            float innerMult = ApiState.Options?.InnerMultiplier ?? ModConfig.InnerMultiplier.Value;
            _outerBudgetBase = Mathf.Max(1, Mathf.RoundToInt(100000 * outerMult));//100k is the vanilla for non prioritized
            _outerBudgetPrioritized = Mathf.Max(1, Mathf.RoundToInt(200000 * outerMult));//200k for the prioritized.
            _dartsPerZone = Mathf.Max(1, Mathf.RoundToInt(20 * innerMult)); //vanilla inner loop is 20 darts per zone. I feel I keep repeating these numbers in every other file... I should throw them in some constants. This is ridiculous. June... I read this AGAIN...ffs... my god...July and I see this fing comment again.
            _maxRelaxationAttempts = ApiState.Options?.MaxRelaxationAttempts ?? ModConfig.MaxRelaxationAttempts.Value;
            _interleavedScheduling = ApiState.Options?.Interleaved ?? ModConfig.EnableInterleavedScheduling.Value;
            _minimalLogging = ApiState.Options?.MinimalLogging ?? ModConfig.MinimalLogging.Value;
            _logSuccesses = ApiState.Options?.LogSuccesses ?? ModConfig.LogSuccesses.Value;
            _mode = ModConfig.EffectiveMode;
            _enable3DSimilarity = ApiState.Options?.Enable3DSimilarityCheck ?? ModConfig.Enable3DSimilarityCheck.Value;
            _highReliefBiomeMask = Compatibility.GetHighReliefBiomeMask();

            // API mode always uses the Survey-pipeline (only path that supports parallel placement and the bucketing/candidate cache).
            bool parallelDefault = ApiState.Options?.Parallel ?? ModConfig.EnableParallelPlacement.Value;
            _parallelPlacement = parallelDefault && (ApiState.IsApiRun || _mode == PlacementMode.Survey);
            if (_parallelPlacement)
            {
                int raw = System.Environment.ProcessorCount - 2;
                _parallelThreadCount = Math.Max(1, raw);
            }

            _locationsGeneratedProp = typeof(ZoneSystem).GetProperty(
                nameof(ZoneSystem.LocationsGenerated),
                BindingFlags.Public | BindingFlags.Instance);

            _generateLocationsProgressField = typeof(ZoneSystem).GetField(
                "m_generateLocationsProgress",
                BindingFlags.NonPublic | BindingFlags.Instance);

            /**
            * Vanilla GenerateLocationsTimeSliced opens with ClearNonPlacedLocations(),
            * which keeps every m_placed=true entry (real spawned structures the player has visited) and discards every m_placed=false entry 
            * (stale reservations from prior generations that never materialized). I bypass the entire 
            * vanilla outer coroutine via the ReplacedEnginePatches prefix, so I have to do this myself or genloc-on-saved-world inherits every old reservation
            * as a hard occupancy claim and starves modded locations in scarce biomes.
            *
            * API mode skips this. UW already swept its own targets selectively (only the prefabs it is acting on, preserving everyone else's unplaced reservations). 
            * Pruning here would destroy state UW kept on purpose.
            */
            if (!ApiState.IsApiRun)
            {
                int beforeCount = zsP.m_locationInstances.Count;
                Dictionary<Vector2i, LocationInstance> retained = new Dictionary<Vector2i, LocationInstance>();
                foreach (KeyValuePair<Vector2i, LocationInstance> kvp in zsP.m_locationInstances)
                {
                    if (kvp.Value.m_placed)
                    {
                        retained.Add(kvp.Key, kvp.Value);
                    }
                }
                zsP.m_locationInstances = retained;
                int sweptCount = beforeCount - retained.Count;
                if (sweptCount > 0)
                {
                    DiagnosticLog.WriteTimestampedLog(
                        $"[LPA] Cleared {sweptCount} non-placed location reservations. Kept {retained.Count} placed instances.");
                }
            }

            // Interleaver path. World-gen mutates zsP.m_locations directly.
            // API mode goes through BuildApiWorkList which returns a private list and never touches zsP.m_locations. I feel I re-mentioned that elsewhere.
            if (ApiState.IsApiRun)
            {
                ApiState.WorkList = Interleaver.BuildApiWorkList(ApiState.Requests, _interleavedScheduling);
            }
            else
            {
                Interleaver.InterleaveLocations(zsP);
            }
            GenerationProgress.StartGeneration(zsP);

            if (MinimapParallelizer.DeferredTimingMessage != null)
            {
                DiagnosticLog.WriteTimestampedLog(MinimapParallelizer.DeferredTimingMessage);
            }

            GenerationProgress.MarkActualStartNoSurvey();

            /**
            * Run survey off the main thread so OnGUI can render the progress overlay.
            * API mode also needs the survey, even if the world-gen mode was Vanilla
            * (the replaced engine's parallel/sequential paths both require it).
            * Lazy-init: if a prior world-gen or API call already ran the survey,
            * I wisely skip the rescan and proceed straight to SurveyMode init.
            */
            bool needsSurvey = ApiState.IsApiRun || ModConfig.EffectiveMode == PlacementMode.Survey;
            if (needsSurvey)
            {
                if (!WorldSurveyData.IsInitialized)
                {
                    GenerationProgress.BeginSurvey();
                    Task surveyTask = Task.Run(() => WorldSurveyData.Initialize());
                    while (!surveyTask.IsCompleted)
                    {
                        yield return null;
                    }
                    if (surveyTask.IsFaulted)
                    {
                        Exception inner = surveyTask.Exception.InnerException;
                        if (inner != null)
                        {
                            throw inner;
                        }
                        throw surveyTask.Exception;
                    }
                    GenerationProgress.EndSurvey();
                    yield return null;
                }
                SurveyMode.Initialize();
                yield return null;
            }

            PresenceGrid.Initialize(ApiState.Options?.PresenceGridCellSize ?? ModConfig.PresenceGridCellSize.Value);

            _groupMemberships = new System.Collections.Concurrent.ConcurrentDictionary<ZoneLocation, List<GroupMembership>>();
            _groupPartitions = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
            _maxAdvertiseMemberships = new System.Collections.Concurrent.ConcurrentDictionary<ZoneLocation, List<GroupMembership>>();
            _maxSearchMemberships = new System.Collections.Concurrent.ConcurrentDictionary<ZoneLocation, List<GroupMembership>>();
            _groupPartitionsMax = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
            foreach (ZoneLocation loc in zsP.m_locations)
            {
                /**
                * EWD-mirror check: blueprint locations arrive with an empty AssetID and only a name on the SoftReference.
                * Compatibility.IsValidLocation accepts IsValid OR m_name != null, matching EWD's own IdManager.IsValid.
                * Without this I was silently dropping every EWD blueprint location here. Also angry dome.
                * */
                if (!loc.m_enable || !Compatibility.IsValidLocation(loc))
                {
                    continue;
                }
                /**
                * Resolve real memberships (one for single/ungrouped, N for multi-group) and roll every (group, radius) into the global partition map, so CommitToGroup later paints
                * every radius sub-grid each group needs, including sub-grids a multi-group location contributes to that no single-group member would have registered on its own.
                * I should split the above into two paragraphs ffs. 
                */
                List<GroupMembership> memberships = ResolveSimilarityMemberships(loc);
                for (int i = 0; i < memberships.Count; i++)
                {
                    GroupMembership m = memberships[i];
                    if (m.Radius <= 0f)
                    {
                        continue;
                    }
                    bool hasPartitionSet = _groupPartitions.TryGetValue(m.Group, out HashSet<float> pset);
                    if (!hasPartitionSet)
                    {
                        pset = new HashSet<float>();
                        _groupPartitions[m.Group] = pset;
                    }
                    pset.Add(m.Radius);
                }

                /**
                 * Roll the max memberships into the separate max partition map. Search radii are what searchers query,
                * so they must be present for advertisers to paint them; advertise radii (usually 0 for advertise-only
                * hosts) are rolled too for the symmetric case. Empty for ordinary locations, so nothing is added.
                * */
                RollMaxPartitions(ResolveMaxAdvertiseMemberships(loc));
                RollMaxPartitions(ResolveMaxSearchMemberships(loc));
            }

            List<string> centerFirstPlaced = CenterFirstPlacer.PlaceAll(zsP);

            _centerFirstCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string name in centerFirstPlaced)
            {
                _centerFirstCounts.TryGetValue(name, out int count);
                _centerFirstCounts[name] = count + 1;
            }

            // Rasterize centerFirst placements into their group PresenceGrids so the main batch sees them as exclusion footprints.
            // Multi-group instances commit a presence record into every real group they belong to, not just their virtual m_group thing.
            foreach (LocationInstance instance in zsP.m_locationInstances.Values)
            {
                CommitMemberships(ResolveSimilarityMemberships(instance.m_location), instance.m_position);
                CommitMaxAdvertise(ResolveMaxAdvertiseMemberships(instance.m_location), instance.m_position);
            }

            yield return null;

            int locListSnapshot = zsP.m_locations.Count;

            // Assign every eligible type its anchor tier before either path runs. Advertise/search sets are already resolved
            // and memoized above, so this is cheap; it exists so a gated searcher never dispatches ahead of its advertiser.
            ComputeTiers(zsP);

            if (_parallelPlacement)
            {
                IEnumerator parallelIter = RunParallelPath(zsP, locListSnapshot);
                while (parallelIter.MoveNext())
                {
                    yield return parallelIter.Current;
                }
                yield break;
            }

            IEnumerator seqIter = RunSequentialPath(zsP, locListSnapshot);
            while (seqIter.MoveNext())
            {
                yield return seqIter.Current;
            }
        }

        /**
        * Throw darts inside one zone, attempt placement.
        * This is the hot path, called once per zone per outer iteration.
        * Returns true on first successful placement, false if all darts miss.
        *
        * Filter chain mirrors vanilla's d__48 order:
        *   distance --> biome --> altitude --> similarity --> terrain --> forest --> place
        */
        private static bool EvaluateZone(ZoneSystem zsP, ZoneLocation locP, Vector2i zoneIDP,
                                          List<GroupMembership> membershipsP, List<GroupMembership> maxAdvertiseP, List<GroupMembership> maxSearchP, PlacementCounters ctrP,
                                          TelemetryContext telCtxP)
        {
            Vector3 zonePos = ZoneSystem.GetZonePos(zoneIDP);
            int zoneGridIdx = -1;
            if (WorldSurveyData.ZoneToIndex.TryGetValue(zoneIDP, out int zi))
            {
                zoneGridIdx = zi;
            }

            for (int di = 0; di < _dartsPerZone; di++)
            {
                ctrP.DartsThrown++;

                float rx = UnityEngine.Random.Range(-32f + locP.m_exteriorRadius, 32f - locP.m_exteriorRadius);
                float rz = UnityEngine.Random.Range(-32f + locP.m_exteriorRadius, 32f - locP.m_exteriorRadius);
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
                TelemetryHelpers.TrackGlobalAltitude(alt);

                if (alt < locP.m_minAltitude || alt > locP.m_maxAltitude)
                {
                    ctrP.ErrAlt++;
                    TelemetryHelpers.TrackAltitudeFailureCtx(telCtxP, alt, locP.m_minAltitude, locP.m_maxAltitude, p);
                    continue;
                }

                if (ConflictsWithSimilarMembers(p, membershipsP, dartBiome, zsP.m_locationInstances))
                {
                    ctrP.ErrSim++;
                    continue;
                }

                if (!SatisfiesMaxSearch(p, maxSearchP, dartBiome, zsP.m_locationInstances))
                {
                    ctrP.ErrNotSim++;
                    continue;
                }

                if (locP.m_maxTerrainDelta > 0f || locP.m_minTerrainDelta > 0f)
                {
                    ThreadSafeTerrainDelta.GetTerrainDelta(p, locP.m_exteriorRadius, out float delta, out _, zoneGridIdx);
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

                zsP.RegisterLocation(locP, p, false);
                if (zoneGridIdx >= 0)
                {
                    SurveyMode.MarkZoneOccupied(zoneGridIdx);
                }
                CommitMemberships(membershipsP, p);
                CommitMaxAdvertise(maxAdvertiseP, p);
                ctrP.Placed++;
                return true;
            }

            return false;
        }

        /**
        * 3D similarity fallback for high-relief biomes.
        * PresenceGrid is 2D (x,z plane). In Mountain and Mistlands, altitude
        * differences of 200-400m make 2D exclusion circles overly conservative, in fact ridiculous as
        * two locations at vastly different elevations read as "conflicting" when they're actually far apart in 3D space.
        * When the 2D bit is set AND the biome is high-relief, this method verifies with actual 3D Euclidean
        * distance against placed instances. Only fires on the rare path (bit=1 AND high-relief biome), so cost is negligible.
        */
        private static bool IsHighRelief(Heightmap.Biome biomeP)
        {
            return (biomeP & _highReliefBiomeMask) != 0;
        }

        private static bool Confirm3DSimilarityConflict(Vector3 pP, float radiusP, string groupP, Dictionary<Vector2i, ZoneSystem.LocationInstance> instancesP) //Vector3 pP... slap the like button davie504
        {
            float radiusSqr = radiusP * radiusP;
            int zoneRadius = Mathf.CeilToInt(radiusP / 64f);
            int cx = Mathf.FloorToInt((pP.x + 32f) / 64f);
            int cz = Mathf.FloorToInt((pP.z + 32f) / 64f);

            for (int z = cz - zoneRadius; z <= cz + zoneRadius; z++)
            {
                for (int x = cx - zoneRadius; x <= cx + zoneRadius; x++)
                {
                    bool found = instancesP.TryGetValue(new Vector2i(x, z), out LocationInstance instance);
                    if (!found)
                    {
                        continue;
                    }

                    // A placed instance belongs to the querying group only if one of its REAL memberships matches.
                    // Reading m_group raw would miss multi-group instances, whose m_group is a virtual handle that never equals the real group name.
                    if (!InstanceBelongsToGroup(instance.m_location, groupP))
                    {
                        continue;
                    }

                    float dx = instance.m_position.x - pP.x;
                    float dy = instance.m_position.y - pP.y;
                    float dz = instance.m_position.z - pP.z; //lots of pps...

                    if (dx * dx + dy * dy + dz * dz < radiusSqr)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // True if the placed instance shares the queried group through any of its real memberships.
        private static bool InstanceBelongsToGroup(ZoneLocation instLocP, string groupP)
        {
            List<GroupMembership> memberships = ResolveSimilarityMemberships(instLocP);
            for (int i = 0; i < memberships.Count; i++)
            {
                if (memberships[i].Group == groupP)
                {
                    return true;
                }
            }
            return false;
        }

        /**
        * Shared similarity check for both engine paths. A candidate conflicts if it falls inside any
        * placed member's exclusion circle in ANY group the location belongs to, each tested at that
        * group's own radius sub-grid. High-relief biomes get the 3D confirmation per group, because a
        * 2D circle is far too conservative across 200-400m of Mountain/Mistlands elevation.
        */
        private static bool ConflictsWithSimilarMembers(
            Vector3 pP, List<GroupMembership> membershipsP, Heightmap.Biome dartBiomeP,
            Dictionary<Vector2i, ZoneSystem.LocationInstance> instancesP)
        {
            for (int i = 0; i < membershipsP.Count; i++)
            {
                GroupMembership m = membershipsP[i];
                if (m.Radius <= 0f || m.Grid == null)
                {
                    continue;
                }
                if (!m.Grid.HasConflict(pP))
                {
                    continue;
                }
                // 2D bit is set. In high-relief terrain confirm in true 3D against this group, a negative 3D result means elevation made the flat circle a false positive, so allow.
                if (!_enable3DSimilarity || !IsHighRelief(dartBiomeP) ||
                    Confirm3DSimilarityConflict(pP, m.Radius, m.Group, instancesP))
                {
                    return true;
                }
            }
            return false;
        }

        /**
        * Rasterize a placement into all radius sub-grids for the group.
        * Heterogeneous groups have multiple sub-grids (one per distinct minDistanceFromSimilar value).
        * Every sub-grid must see every placement so HasConflict works regardless of which LTS is querying.
        */
        private static void CommitToGroup(string groupP, Vector3 pP)
        {
            bool hasPartitions = _groupPartitions.TryGetValue(groupP, out HashSet<float> partitions);
            if (!hasPartitions)
            {
                return;
            }
            foreach (float radius in partitions)
            {
                PresenceGrid.GetOrCreate($"{groupP}:{radius:F0}").Commit(pP, radius);
            }
        }

        // Rasterize a placement into every group the location belongs to. For a single-group or ungrouped location this is one CommitToGroup call, the one identical to the old single-group path.
        private static void CommitMemberships(List<GroupMembership> membershipsP, Vector3 pP)
        {
            for (int i = 0; i < membershipsP.Count; i++)
            {
                CommitToGroup(membershipsP[i].Group, pP);
            }
        }

        private static List<GroupMembership> ResolveMaxAdvertiseMemberships(ZoneLocation locP)
        {
            return _maxAdvertiseMemberships.GetOrAdd(locP, BuildMaxAdvertiseMemberships);
        }

        private static List<GroupMembership> ResolveMaxSearchMemberships(ZoneLocation locP)
        {
            return _maxSearchMemberships.GetOrAdd(locP, BuildMaxSearchMemberships);
        }

        /**
        * Advertise set: the max groups this location paints itself into on placement (EWD GroupsMax). A host declaring
        * groupMax: X with maxDistanceFromSimilar 0 resolves to (X, 0), which paints X but never queries - exactly the
        * advertise-only behaviour vanilla gives it. Fallback (no EWD contract) mirrors BuildMemberships: m_groupMax-or-
        * prefab at the location's own max spacing.
        */
        private static List<GroupMembership> BuildMaxAdvertiseMemberships(ZoneLocation locP)
        {
            List<Tuple<string, float>> groups = Compatibility.GetLocationGroups(locP, true);
            return BuildMaxMembershipList(groups, locP.m_groupMax, locP.m_maxDistanceFromSimilar, locP.m_prefabName);
        }

        /**
        * Search set: advertise groups PLUS any search-only anchor groups (EWD's directed-anchor contract). This is what a dart must fall within range of. 
        * For symmetric groupMax the anchor set is empty, so search equals advertise and behaviour is vanilla. 
        * A directed satellite carries its anchor group only here while its advertise set stays
        * empty, so it must sit near a host without becoming one (no chaining). Fallback is identical to advertise.
        */
        private static List<GroupMembership> BuildMaxSearchMemberships(ZoneLocation locP)
        {
            List<Tuple<string, float>> advertise = Compatibility.GetLocationGroups(locP, true);
            List<Tuple<string, float>> anchors = Compatibility.GetAnchorGroups(locP);

            if (anchors == null || anchors.Count == 0)
            {
                return BuildMaxMembershipList(advertise, locP.m_groupMax, locP.m_maxDistanceFromSimilar, locP.m_prefabName);
            }

            List<Tuple<string, float>> combined = new List<Tuple<string, float>>();
            if (advertise != null)
            {
                for (int i = 0; i < advertise.Count; i++)
                {
                    combined.Add(advertise[i]);
                }
            }
            for (int i = 0; i < anchors.Count; i++)
            {
                /**
                * A group already advertised keeps its advertise distance; a location declaring both groupMax: X and
                * anchors: X is contradictory config, so first-wins (advertise) is fine and documented here. 
                * However now that I re-read this my "documented here" seemed to me like wtf am I talking about. Concerning because I did this like 4-5 days ago. 
                */
                bool already = false;
                for (int j = 0; j < combined.Count; j++)
                {
                    if (combined[j].Item1 == anchors[i].Item1)
                    {
                        already = true;
                        break;
                    }
                }
                if (!already)
                {
                    combined.Add(anchors[i]);
                }
            }
            return BuildMaxMembershipList(combined, locP.m_groupMax, locP.m_maxDistanceFromSimilar, locP.m_prefabName);
        }

        /**
        * Shared max-membership builder, mirror of BuildMemberships. A radius-0 entry (an advertise-only host) gets a null
        * grid but still carries its group, so CommitMaxAdvertise can paint it at whatever radius a searcher registered.
        * When the EWD contract is unavailable the list is null, so I fall back to m_groupMax-or-prefab at the location's
        * own max distance - the same degraded-but-consistent path the min side uses, and it keeps vanilla's same-prefab
        * max matching for an ungrouped location that sets maxDistanceFromSimilar without a groupMax.
        */
        private static List<GroupMembership> BuildMaxMembershipList(List<Tuple<string, float>> groupsP, string rawGroupP, float rawRadiusP, string rawPrefabP)
        {
            if (groupsP != null && groupsP.Count > 0)
            {
                List<GroupMembership> result = new List<GroupMembership>(groupsP.Count);
                for (int i = 0; i < groupsP.Count; i++)
                {
                    string groupName = groupsP[i].Item1;
                    float radius = groupsP[i].Item2;
                    PresenceGrid grid = null;
                    if (radius > 0f)
                    {
                        grid = PresenceGrid.GetOrCreate(MaxGridKey(groupName, radius));
                    }
                    result.Add(new GroupMembership { Group = groupName, Radius = radius, Grid = grid });
                }
                return result;
            }

            string grp = rawPrefabP;
            if (!string.IsNullOrEmpty(rawGroupP))
            {
                grp = rawGroupP;
            }
            PresenceGrid fallbackGrid = null;
            if (rawRadiusP > 0f)
            {
                fallbackGrid = PresenceGrid.GetOrCreate(MaxGridKey(grp, rawRadiusP));
            }
            List<GroupMembership> single = new List<GroupMembership>(1);
            single.Add(new GroupMembership { Group = grp, Radius = rawRadiusP, Grid = fallbackGrid });
            return single;
        }

        /**
        * Roll a max membership list's radii into the max partition map, so advertisers later paint every sub-grid a
        * searcher needs. Mirror of the min partition roll in Run(). Radius-0 entries add nothing.
        */
        private static void RollMaxPartitions(List<GroupMembership> membershipsP)
        {
            for (int i = 0; i < membershipsP.Count; i++)
            {
                GroupMembership m = membershipsP[i];
                if (m.Radius <= 0f)
                {
                    continue;
                }
                bool hasSet = _groupPartitionsMax.TryGetValue(m.Group, out HashSet<float> pset);
                if (!hasSet)
                {
                    pset = new HashSet<float>();
                    _groupPartitionsMax[m.Group] = pset;
                }
                pset.Add(m.Radius);
            }
        }

        /**
        * Max/anchor inclusion check, the mirror of ConflictsWithSimilarMembers. A candidate is allowed only if it falls
        * within range of at least one placed member of ANY of its search groups (vanilla's OR over the max groups). If
        * the location has no max-search constraint the check is a no-op (returns true), so ordinary locations pay nothing.
        * High-relief terrain gets the inverted 3D confirmation: a 2D grid hit that turns out to be 3D-distant is a false
        * positive, so that group does not count as an anchor and I keep checking the others.
        */
        private static bool SatisfiesMaxSearch(
            Vector3 pP, List<GroupMembership> searchP, Heightmap.Biome dartBiomeP,
            Dictionary<Vector2i, ZoneSystem.LocationInstance> instancesP)
        {
            bool gated = false;
            for (int i = 0; i < searchP.Count; i++)
            {
                GroupMembership m = searchP[i];
                if (m.Radius <= 0f || m.Grid == null)
                {
                    continue;
                }
                gated = true;
                if (!m.Grid.HasConflict(pP))
                {
                    continue;
                }
                // 2D bit set: an advertiser is in range on the flat plane. On high-relief confirm in true 3D, else a 200-400m elevation gap would let a vertically distant advertiser wrongly count as an anchor.
                if (!_enable3DSimilarity || !IsHighRelief(dartBiomeP) ||
                    ConfirmMaxAnchorInRange(pP, m.Radius, m.Group, instancesP))
                {
                    return true;
                }
            }
            // Not gated: no max-search constraint, always allowed. Gated with nothing in range: this dart has no anchor.
            return !gated;
        }

        /**
        * 3D confirmation for the max check: true if some instance ADVERTISING the group sits within 3D radius. Advertise
        * membership (not min) is what makes an instance a valid anchor, so I resolve the placed instance's advertise set,
        * not its raw m_groupMax (a virtual handle for the multi-group case).
        */
        private static bool ConfirmMaxAnchorInRange(Vector3 pP, float radiusP, string groupP, Dictionary<Vector2i, ZoneSystem.LocationInstance> instancesP)
        {
            float radiusSqr = radiusP * radiusP;
            int zoneRadius = Mathf.CeilToInt(radiusP / 64f);
            int cx = Mathf.FloorToInt((pP.x + 32f) / 64f);
            int cz = Mathf.FloorToInt((pP.z + 32f) / 64f);

            for (int z = cz - zoneRadius; z <= cz + zoneRadius; z++)
            {
                for (int x = cx - zoneRadius; x <= cx + zoneRadius; x++)
                {
                    bool found = instancesP.TryGetValue(new Vector2i(x, z), out LocationInstance instance);
                    if (!found)
                    {
                        continue;
                    }
                    if (!InstanceAdvertisesMaxGroup(instance.m_location, groupP))
                    {
                        continue;
                    }
                    float dx = instance.m_position.x - pP.x; //here we go with pps again.
                    float dy = instance.m_position.y - pP.y;
                    float dz = instance.m_position.z - pP.z;
                    if (dx * dx + dy * dy + dz * dz < radiusSqr)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // True if the placed instance advertises the queried max group through any of its advertise memberships.
        private static bool InstanceAdvertisesMaxGroup(ZoneLocation instLocP, string groupP)
        {
            List<GroupMembership> advertise = ResolveMaxAdvertiseMemberships(instLocP);
            for (int i = 0; i < advertise.Count; i++)
            {
                if (advertise[i].Group == groupP)
                {
                    return true;
                }
            }
            return false;
        }

        /**
        * Rasterize a placement into every max sub-grid for the group (max namespace). Mirror of CommitToGroup but reads
        * _groupPartitionsMax, so an advertise-only host (its own maxDistanceFromSimilar 0) still paints the radius a
        * searcher registered - that radius is what lands in the partition set, not the host's own.
        */
        private static void CommitToGroupMax(string groupP, Vector3 pP)
        {
            bool hasPartitions = _groupPartitionsMax.TryGetValue(groupP, out HashSet<float> partitions);
            if (!hasPartitions)
            {
                return;
            }
            foreach (float radius in partitions)
            {
                PresenceGrid.GetOrCreate(MaxGridKey(groupP, radius)).Commit(pP, radius);
            }
        }

        // Paint a placement into every max group it advertises. Empty for ordinary locations, so it costs nothing there.
        private static void CommitMaxAdvertise(List<GroupMembership> advertiseP, Vector3 pP)
        {
            for (int i = 0; i < advertiseP.Count; i++)
            {
                CommitToGroupMax(advertiseP[i].Group, pP);
            }
        }

        /**
        * Assigns each location type a placement tier from the anchor dependency graph. An edge U -> T means U advertises a
        * group that T searches at radius > 0, so T cannot be evaluated until U's advertise footprint exists and must land in
        * a strictly later tier. Tier is the longest such chain per type, which I get from Kahn's algorithm.
        *
        * The reason this is necessary: the max-similarity (anchor) rule assumes the advertiser is already committed when the
        * searcher reads the grid. Strict in-order placement gives that for free, but my parallel dispatcher reorders freely
        * across streams, so without an explicit tier the satellite races its host and fails against an unpainted grid. Tiers
        * reinstate exactly the ordering the max rule depends on, and they do it independent of the priority flag - an anchor
        * dependency is a data dependency, not an importance knob.
        *
        * Cycles are the seedless-symmetric case: two types that both advertise and search one group at radius > 0 depend on
        * each other, so no strict order exists. Kahn's leaves them unprocessed and I lump the whole residual set one tier
        * above the acyclic part - conservative but never wrong, and it mirrors what vanilla does with a symmetric group that
        * has no maxDistance-0 seed (nothing anchors, so little places). A symmetric group WITH a seed is not a cycle: the
        * seed advertises with its own search radius 0, so it is a source at tier 0 and the gated members fall to tier 1,
        * which is the seed-first ordering the config intends.
        */
        private static void ComputeTiers(ZoneSystem zsP)
        {
            _typeTier = new Dictionary<ZoneLocation, int>();

            /**
             * A tier is intrinsic to a type's own advertise/search sets, so I evaluate the union of every list a path might
             * walk. That way a clone, a packet, or an API work-list entry each resolve to a tier even though they are distinct
             * objects from the entries in zsP.m_locations.
             * */
            List<ZoneLocation> all = new List<ZoneLocation>();
            HashSet<ZoneLocation> seen = new HashSet<ZoneLocation>();
            AddTierCandidates(zsP.m_locations, all, seen);
            if (Interleaver.OriginalLocations != null)
            {
                AddTierCandidates(Interleaver.OriginalLocations, all, seen);
            }
            if (ApiState.WorkList != null)
            {
                AddTierCandidates(ApiState.WorkList, all, seen);
            }

            // Advertisers keyed by group name. Advertising at ANY radius (including 0, the advertise-only seed) makes a type an anchor target, so I do not filter radius here, only the searcher side cares about radius > 0.
            Dictionary<string, List<ZoneLocation>> advertisersByGroup = new Dictionary<string, List<ZoneLocation>>(StringComparer.Ordinal);
            for (int i = 0; i < all.Count; i++)
            {
                ZoneLocation loc = all[i];
                _typeTier[loc] = 0;
                List<GroupMembership> advertise = ResolveMaxAdvertiseMemberships(loc);
                for (int a = 0; a < advertise.Count; a++)
                {
                    string grp = advertise[a].Group;
                    bool hasList = advertisersByGroup.TryGetValue(grp, out List<ZoneLocation> list);
                    if (!hasList)
                    {
                        list = new List<ZoneLocation>();
                        advertisersByGroup[grp] = list;
                    }
                    list.Add(loc);
                }
            }

            /**
            * Dependency edges and in-degrees. adj[U] holds every T that must wait for U; inDegree[T] counts the distinct
            * advertisers T waits on. I dedupe predecessors per T so a group advertised by several hosts, or searched via
            * several memberships, still contributes one edge per (host, satellite) pair.
            */
            Dictionary<ZoneLocation, List<ZoneLocation>> adj = new Dictionary<ZoneLocation, List<ZoneLocation>>();
            Dictionary<ZoneLocation, int> inDegree = new Dictionary<ZoneLocation, int>();
            for (int i = 0; i < all.Count; i++)
            {
                adj[all[i]] = new List<ZoneLocation>();
                inDegree[all[i]] = 0;
            }
            for (int i = 0; i < all.Count; i++)
            {
                ZoneLocation t = all[i];
                List<GroupMembership> search = ResolveMaxSearchMemberships(t);
                HashSet<ZoneLocation> preds = new HashSet<ZoneLocation>();
                for (int s = 0; s < search.Count; s++)
                {
                    GroupMembership m = search[s];
                    if (m.Radius <= 0f)
                    {
                        continue;
                    }
                    bool hasHosts = advertisersByGroup.TryGetValue(m.Group, out List<ZoneLocation> hosts);
                    if (!hasHosts)
                    {
                        continue;
                    }
                    for (int h = 0; h < hosts.Count; h++)
                    {
                        if (!object.ReferenceEquals(hosts[h], t))
                        {
                            preds.Add(hosts[h]);
                        }
                    }
                }
                foreach (ZoneLocation u in preds)
                {
                    adj[u].Add(t);
                    inDegree[t] = inDegree[t] + 1;
                }
            }

            /**
             * Kahn's: sources (in-degree 0) start at tier 0; each relaxed edge raises the successor to at least predecessor tier + 1. 
             * Counting processed nodes lets me detect the residual cycle afterward.
             * KAAAAAAAAAHHN! *insert captain kirk voice*
             * Let me add a link here: https://en.wikipedia.org/wiki/Topological_sorting#Kahn's_algorithm
             * as I should not be assuming everyone knows what Kahn's is. Although if they do not, wth are they doing reading this code anyway...
             */
            Queue<ZoneLocation> ready = new Queue<ZoneLocation>();
            for (int i = 0; i < all.Count; i++)
            {
                if (inDegree[all[i]] == 0)
                {
                    ready.Enqueue(all[i]);
                }
            }
            int processed = 0;
            while (ready.Count > 0)
            {
                ZoneLocation u = ready.Dequeue();
                processed++;
                List<ZoneLocation> succ = adj[u];
                for (int i = 0; i < succ.Count; i++)
                {
                    ZoneLocation t = succ[i];
                    if (_typeTier[u] + 1 > _typeTier[t])
                    {
                        _typeTier[t] = _typeTier[u] + 1;
                    }
                    inDegree[t] = inDegree[t] - 1;
                    if (inDegree[t] == 0)
                    {
                        ready.Enqueue(t);
                    }
                }
            }

            /**
             * So... residual cycle handling. Any node still carrying in-degree sits in a dependency cycle (a seedless symmetric group).
             * I raise the whole residual set to one tier above the highest tier the acyclic part reached, so a cyclic
             * searcher can never precede a genuine advertiser that lives in the DAG below it. 
             */
            if (processed < all.Count)
            {
                int floorPlus1 = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    if (inDegree[all[i]] == 0 && _typeTier[all[i]] + 1 > floorPlus1)
                    {
                        floorPlus1 = _typeTier[all[i]] + 1;
                    }
                }
                for (int i = 0; i < all.Count; i++)
                {
                    if (inDegree[all[i]] > 0 && floorPlus1 > _typeTier[all[i]])
                    {
                        _typeTier[all[i]] = floorPlus1;
                    }
                }
            }
        }

        /**
        * Adds the enabled, valid entries of one source list into the tier candidate set, deduped by object reference. The
        * same enable/validity gate the placement paths use, so I never assign a tier to a type neither path would place.
        */
        private static void AddTierCandidates(List<ZoneLocation> sourceP, List<ZoneLocation> intoP, HashSet<ZoneLocation> seenP)
        {
            for (int i = 0; i < sourceP.Count; i++)
            {
                ZoneLocation loc = sourceP[i];
                if (!loc.m_enable || !Compatibility.IsValidLocation(loc))
                {
                    continue;
                }
                if (seenP.Add(loc))
                {
                    intoP.Add(loc);
                }
            }
        }

        /**
        * Tier lookup with a tier-0 default. The default keeps every caller safe if a type was never scored (an edge path that
        * bypassed ComputeTiers, or an entry outside the candidate union): tier 0 is the no-dependency case, i.e. today's order.
        */
        private static int TierOf(ZoneLocation locP)
        {
            if (_typeTier != null && _typeTier.TryGetValue(locP, out int tier))
            {
                return tier;
            }
            return 0;
        }

        // Default path: counts placed from m_locationInstances (accurate for the sequential path where RegisterLocation is called inline).
        private static void FlushLTS(ZoneSystem zsP, ZoneLocation locP, PlacementCounters ctrP)
        {
            string prefab = locP.m_prefabName;

            int globalPlaced = 0;
            foreach (LocationInstance inst in zsP.m_locationInstances.Values)
            {
                if (inst.m_location.m_prefabName == prefab)
                {
                    globalPlaced++;
                }
            }

            FlushLTSCore(zsP, locP, ctrP, globalPlaced);
        }

        // Parallel path overload: caller provides globalPlaced directly (ctr.Placed and _centerFirstCounts) because RegisterLocation isdeferred until the post-worker commit wave on the main thread.
        private static void FlushLTS(ZoneSystem zsP, ZoneLocation locP, PlacementCounters ctrP, int globalPlacedOverrideP)
        {
            FlushLTSCore(zsP, locP, ctrP, globalPlacedOverrideP);
        }

        private static void FlushLTSCore(ZoneSystem zsP, ZoneLocation locP, PlacementCounters ctrP, int globalPlacedP)
        {
            string prefab = locP.m_prefabName;
            // Relaxation state and original-quantity resolve per logical type (clones are distinct types. I have to keep remembering the decision) prefab stays for the PlayabilityPolicy config lookup and AggregateSessions.
            string typeKey = Interleaver.GetTypeKey(locP);

            int origQty = Interleaver.GetOriginalQuantity(locP);
            bool isComplete = globalPlacedP >= origQty;

            int minNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefab, origQty);
            bool isNecessitySatisfied = globalPlacedP >= minNeeded;

            bool wasRelaxed = ConstraintRelaxer.RelaxationAttempts.TryGetValue(typeKey, out int relaxCount) && relaxCount > 0;
            bool isSuccess = isComplete || (wasRelaxed && isNecessitySatisfied);

            int displayQty = origQty;
            if (wasRelaxed && isSuccess && !isComplete)
            {
                displayQty = minNeeded;
            }
            ReportData data = BuildReportData(locP, ctrP, globalPlacedP, displayQty, isComplete);

            if (isSuccess)
            {
                if (wasRelaxed)
                {
                    RelaxationTracker.MarkRelaxationSucceeded(typeKey);
                }
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
                                $"[RELAXATION SUCCESS] {prefab} placed {globalPlacedP}/{displayQty} after {relaxCount} relaxation(s). {ConstraintRelaxer.GetRelaxationSummary(prefab, locP)}",
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
                if (!ConstraintRelaxer.TryRelax(data))
                {
                    RelaxationTracker.CheckAndMarkFailed(typeKey, globalPlacedP, origQty, locP.m_prioritized);
                }
            }

            TranspiledCompletionHandler.AggregateSessions.Remove(prefab);
        }

        private static ReportData BuildReportData(
            ZoneLocation locP, PlacementCounters ctrP,
            int globalPlacedP, int origQtyP, bool isCompleteP)
        {
            // Reconstruct the placement funnel waterfall. Each stage = darts that survived all previous stages.
            long inDist = ctrP.DartsThrown;
            long inBiome = inDist - ctrP.ErrDist;
            long inAlt = inBiome - ctrP.ErrBiome;
            long inSim = inAlt - ctrP.ErrAlt;
            long inTerr = inSim - ctrP.ErrSim - ctrP.ErrNotSim;
            long inForest = inTerr - ctrP.ErrTerrain;
            long inVeg = inForest - ctrP.ErrForest;

            int baseBudget = 100000;
            if (locP.m_prioritized)
            {
                baseBudget = 200000;
            }

            return new ReportData
            {
                Loc = locP,
                PrefabName = locP.m_prefabName,
                CurrentOuter = ctrP.ZonesExamined,
                LimitOuter = Interleaver.GetBudget(locP, baseBudget),
                Placed = globalPlacedP,
                OriginalQuantity = origQtyP,
                IsComplete = isCompleteP,

                ErrZone = ctrP.ErrOccupied,
                ValidZones = ctrP.ZonesExamined - ctrP.ErrOccupied,

                InDist = inDist,
                ErrDist = ctrP.ErrDist,
                InBiome = inBiome,
                ErrBiome = ctrP.ErrBiome,
                InSim = inSim,
                ErrSim = ctrP.ErrSim,
                ErrNotSim = ctrP.ErrNotSim,
                InAlt = inAlt,
                ErrAlt = ctrP.ErrAlt,
                InTerr = inTerr,
                ErrTerrain = ctrP.ErrTerrain,
                InForest = inForest,
                ErrForest = ctrP.ErrForest,
                InVeg = inVeg,
                ErrVeg = 0L,
            };
        }

        /**
        * Builds one PlacementToken per placement-unit for the sequential path.
        * Sorted: prioritized first, then vanilla before modded ("MWL_" prefix),
        * then descending exclusion radius so Landlords grab territory before Tenants,
        * and finally by the original snapshot index to guarantee stability.
        *
        * CenterFirst types get (quantity - 1) tokens since CenterFirstPlacer
        * already placed the first instance. If interleaving is on, each token
        * represents exactly 1 placement attempt; otherwise all quantity is packed
        * into a single token.
        */
        private static List<PlacementToken> BuildTokenList(ZoneSystem zsP)
        {
            List<PlacementToken> tokens = new List<PlacementToken>();

            // API mode iterates the per-call work list (clones with TargetQuantity already stamped over m_quantity, optionally packetized).
            // World-gen iterates zsP.m_locations as before.
            List<ZoneLocation> source = zsP.m_locations;
            if (ApiState.IsApiRun && ApiState.WorkList != null)
            {
                source = ApiState.WorkList;
            }

            HashSet<string> centerFirstNames = new HashSet<string>();
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].m_centerFirst)
                {
                    centerFirstNames.Add(source[i].m_prefabName);
                }
            }

            // Build and sort the eligible location list.
            // Wrap the location with its original index to guarantee a stable sort against List.Sort().
            List<SequentialSortEntry> eligible = new List<SequentialSortEntry>();
            for (int i = 0; i < source.Count; i++)
            {
                ZoneLocation loc = source[i];
                // EWD-mirror: accept blueprints (empty AssetID, name-only SoftReference) so they actually make it into the token list. Quantity check stays inline.
                if (loc.m_enable && Compatibility.IsValidLocation(loc) && loc.m_quantity > 0)
                {
                    SequentialSortEntry entry = new SequentialSortEntry();
                    entry.Loc = loc;
                    entry.OriginalIndex = i;
                    eligible.Add(entry);
                }
            }

            eligible.Sort(CompareSequentialSortEntries);

            foreach (SequentialSortEntry entry in eligible)
            {
                ZoneLocation loc = entry.Loc;
                int baseQty = loc.m_quantity;
                if (centerFirstNames.Contains(loc.m_prefabName))
                {
                    baseQty = loc.m_quantity - 1;
                }

                if (baseQty <= 0)
                {
                    continue;
                }

                int tokenCount = baseQty;
                if (_interleavedScheduling)
                {
                    tokenCount = 1;
                }
                for (int i = 0; i < tokenCount; i++)
                {
                    tokens.Add(new PlacementToken { Location = loc });
                }
            }

            return tokens;
        }

        /**
        * So, prioritized locations sort first (vanilla behaviour and also what I was doing pre 1.64 EWD madness).
        * Now within the same priority tier, modded locations (MWL_ prefix for the moment) sort after vanilla types so vanilla fills its quotas first.
        * I then sort by descending exclusion radius so Landlords (huge radius) place before Tenants (small radius), this is because JP was doing the crazy
        * stuff with setting everything prioritized breaking my elegant "if prioritized surely it is a landlord" assumption. *sigh*
        * A legitimate question is whether I should be acommodating everything crazy, but since it is not a big deal, no biggy.
        * Finally, I use the original list index to guarantee the sort is stable.
        */
        private static int CompareSequentialSortEntries(SequentialSortEntry aP, SequentialSortEntry bP)
        {
            /**
            * Tier is the outermost key. A type gated on an anchor group (searches it at radius > 0) must place after every
            * type that advertises that group, or it searches a grid nobody has painted. This path's main loop walks the sorted
            * list strictly in order, so ordering all lower-tier tokens ahead of higher-tier ones IS the entire guarantee - no
            * runtime barrier is needed here. Priority sits below tier on purpose: a prioritized satellite still yields to its
            * unprioritized host, because the dependency is about data, not importance.
            */
            int aTier = TierOf(aP.Loc);
            int bTier = TierOf(bP.Loc);
            if (aTier != bTier)
            {
                return aTier.CompareTo(bTier);
            }

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
    }
}