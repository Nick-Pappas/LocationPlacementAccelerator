// v1.0.6
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
* 1.0.6: maxDistanceFromSimilar. Vanilla has carried this rule since the AshLands patch and I have
* never implemented it, so FortressRuins has been placing 100/100 wherever it liked instead of
* huddling round the CharredFortresses.
*
* The thing that took me longest to see is that vanilla has TWO group systems, not one. HaveLocationInRange
* takes a maxGroup flag: false compares my m_group against THEIR m_group, true compares my m_groupMax
* against THEIR m_groupMax. m_group is never tested against m_groupMax in either direction. So every
* location carries two independent memberships and the whole engine - streams, coloring, sub-group
* ordering, the grids - only ever modelled the first one. That is the gap. Not "a rule I skipped": a
* second membership system I never built.
*
* The proof they are separate is sitting in the vanilla yaml: CharredRuins1's m_group is "zigg" and
* FortressRuins' m_groupMax is "zigg". Same string, zero interaction, because one is only read by the
* false-flag call and the other only by the true-flag call. That is exactly why every grid key below is
* prefixed MAX: - the day someone writes a maxDistanceFromSimilar of 256 the two would collide on
* "zigg:256" and CharredRuins1's exclusion footprint would silently start advertising itself as a host.
*
* The rule inverts everything min assumes. Min gets HARDER as a run proceeds - every placement carves
* out territory, and a spot rejected early is only more rejected later. Max gets EASIER - at the start
* nowhere is legal because nothing is down yet, and every placement opens new ground. Three things fall
* out of that and all three are handled here or in Parallel/Sequential:
*
*   1. Bootstrapping. A max type cannot place anything until its hosts exist. In vanilla this is free
*      (CharredFortress is prioritized) but I will not rely on luck, so BuildMaxHosts records who each
*      querier depends on and the parallel dispatcher gates on it.
*   2. Revisiting. Vanilla re-samples zones with replacement 100k times, so it stumbles back onto spots
*      it rejected before a host landed nearby. I walk each candidate once. For min that is free; for max
*      it is fatal, because the chain (FortressRuins is a member of zigg, so each one hosts the next) only
*      forms if the child zone happens to sort after its parent. Both paths now re-walk for max types only.
*   3. No race. Max is monotone - a concurrent commit only makes the answer MORE true, so a worker reading
*      a stale clear bit loses a placement, never correctness. The color gate does not extend over max
*      and must not: there is nothing to keep apart.
*
* Cost when nothing uses max: _maxQueries.Length == 0, so CommitMaxAdvertise returns on a length check
* and EvaluateZone's max branch is a null test on a local. Vanilla has exactly one querier.
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
        // Cached config - read once at Run() entry, never touched in hot paths.
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
        * The m_groupMax system. One entry per distinct (key, radius) that some enabled type actually
        * QUERIES; a type that only advertises (maxDistanceFromSimilar 0, like CharredFortress) never
        * produces an entry, it just paints into other people's.
        *
        * Key mirrors the min side's group-or-prefab collapse: m_groupMax when set, prefab name otherwise.
        * That collapse is exact here rather than approximate. Vanilla's max arm is an unconditional OR -
        * "their prefab == my prefab, OR their m_groupMax == my m_groupMax" - and the prefab arm can only
        * ever match instances of the querier's OWN prefab, which necessarily carry the querier's own
        * m_groupMax. So whenever m_groupMax is set the prefab arm is a strict subset of the group arm and
        * folding it away changes nothing. When m_groupMax is empty the prefab arm is all there is, and
        * the key becomes the prefab name.
        *
        * Array, not a dictionary: it is walked per placement and it has one element on a vanilla world.
        */
        private struct MaxQuery
        {
            public string PrefabName;
            public string GroupMax;
            public float Radius;
            public PresenceGrid Grid;
        }
        private static MaxQuery[] _maxQueries = new MaxQuery[0];

        // Querying prefab --> the grid it reads. Resolved once per LTS, never in the dart loop.
        private static Dictionary<string, PresenceGrid> _maxGridByPrefab;

        /**
        * Querying prefab --> the prefabs whose placements it needs to already exist. Only pure
        * advertisers land here (types that are in the group but ask for nothing themselves), which is
        * what makes the dependency acyclic by construction: a pure advertiser can never be waiting on
        * anyone, so nothing can wait in a circle. A querier that is a member of its own group - which
        * FortressRuins is, and has to be, or 20 hosts times 4 lattice slots caps it at 80 of the 100 the
        * designer asked for - is deliberately NOT a dependency of itself. That chain is the re-walk's job.
        */
        private static Dictionary<string, List<string>> _maxHostsByPrefab;

        // Prefab name --> count of instances placed by CenterFirstPlacer.PlaceAll().
        // Used by the parallel path's DoFlushAndRelax to compute globalPlaced
        // without iterating m_locationInstances (which is being mutated on main thread).
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
            _dartsPerZone = Mathf.Max(1, Mathf.RoundToInt(20 * innerMult)); //vanilla inner loop is 20 darts per zone. I feel I keep repeating these numbers in every other file... I should throw them in some constants. This is ridiculous.
            _maxRelaxationAttempts = ApiState.Options?.MaxRelaxationAttempts ?? ModConfig.MaxRelaxationAttempts.Value;
            _interleavedScheduling = ApiState.Options?.Interleaved ?? ModConfig.EnableInterleavedScheduling.Value;
            _minimalLogging = ApiState.Options?.MinimalLogging ?? ModConfig.MinimalLogging.Value;
            _logSuccesses = ApiState.Options?.LogSuccesses ?? ModConfig.LogSuccesses.Value;
            _mode = ModConfig.EffectiveMode;
            _enable3DSimilarity = ApiState.Options?.Enable3DSimilarityCheck ?? ModConfig.Enable3DSimilarityCheck.Value;
            _highReliefBiomeMask = Compatibility.GetHighReliefBiomeMask();

            // API mode always uses the Survey-pipeline (only path that supports
            // parallel placement and the bucketing/candidate cache).
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
            * which keeps every m_placed=true entry (real spawned structures the player 
            * has visited) and discards every m_placed=false entry (stale reservations
            * from prior generations that never materialized). I bypass the entire 
            * vanilla outer coroutine via the ReplacedEnginePatches prefix, so I have
            * to do this myself or genloc-on-saved-world inherits every old reservation
            * as a hard occupancy claim and starves modded locations in scarce biomes.
            *
            * API mode skips this. UW already swept its own targets selectively (only
            * the prefabs it is acting on, preserving everyone else's unplaced
            * reservations). Pruning here would destroy state UW kept on purpose.
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

            // Interleaver path. World-gen mutates zsP.m_locations directly; API
            // mode goes through BuildApiWorkList which returns a private list
            // and never touches zsP.m_locations.
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

            // Run survey off the main thread so OnGUI can render the progress overlay.
            // API mode also needs the survey, even if the world-gen mode was Vanilla
            // (the replaced engine's parallel/sequential paths both require it).
            // Lazy-init: if a prior world-gen or API call already ran the survey,
            // we skip the rescan and proceed straight to SurveyMode init.
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

            _groupPartitions = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
            foreach (ZoneLocation loc in zsP.m_locations)
            {
                // EWD-mirror check: blueprint locations arrive with an empty AssetID
                // and only a name on the SoftReference. Compatibility.IsValidLocation
                // accepts IsValid OR m_name != null, matching EWD's own IdManager.IsValid.
                // Without this I was silently dropping every EWD blueprint location here.
                if (!loc.m_enable || !Compatibility.IsValidLocation(loc))
                {
                    continue;
                }
                string grp = loc.m_prefabName;
                if (!string.IsNullOrEmpty(loc.m_group))
                {
                    grp = loc.m_group;
                }
                bool hasPartitionSet = _groupPartitions.TryGetValue(grp, out HashSet<float> pset);
                if (!hasPartitionSet)
                {
                    pset = new HashSet<float>();
                    _groupPartitions[grp] = pset;
                }
                if (loc.m_minDistanceFromSimilar > 0f)
                {
                    pset.Add(loc.m_minDistanceFromSimilar);
                }
            }

            BuildMaxSystem(zsP);

            List<string> centerFirstPlaced = CenterFirstPlacer.PlaceAll(zsP);

            _centerFirstCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string name in centerFirstPlaced)
            {
                _centerFirstCounts.TryGetValue(name, out int count);
                _centerFirstCounts[name] = count + 1;
            }

            // Rasterize centerFirst placements into their group PresenceGrids so the main batch sees them as exclusion footprints.
            foreach (LocationInstance instance in zsP.m_locationInstances.Values)
            {
                string grp = instance.m_location.m_prefabName;
                if (!string.IsNullOrEmpty(instance.m_location.m_group))
                {
                    grp = instance.m_location.m_group;
                }
                CommitToGroup(grp, instance.m_position);
                CommitMaxAdvertise(instance.m_location, instance.m_position);
            }

            yield return null;

            int locListSnapshot = zsP.m_locations.Count;

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
                                          PresenceGrid groupGridP, string groupP, PlacementCounters ctrP,
                                          TelemetryContext telCtxP, PresenceGrid maxGridP)
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

                if (locP.m_minDistanceFromSimilar > 0f && groupGridP.HasConflict(p))
                {
                    if (!_enable3DSimilarity || !IsHighRelief(dartBiome) ||
                        Confirm3DSimilarityConflict(p, locP.m_minDistanceFromSimilar, groupP, zsP.m_locationInstances))
                    {
                        ctrP.ErrSim++;
                        continue;
                    }
                }

                /**
                * The mirror image of the check above, and the same single-bit read. The grid is painted at
                * the querier's own radius by everything that advertises to its groupMax, so "a bit is set
                * here" already means "a host is within range" - the radius does not appear again. Min
                * rejects when the bit is SET, max rejects when it is CLEAR. Same machinery, same
                * quantization, opposite sign. Vanilla runs min first then max and so do I; the ordering is
                * free either way but there is no reason to diverge.
                */
                if (maxGridP != null && !maxGridP.HasConflict(p))
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
                CommitToGroup(groupP, p);
                CommitMaxAdvertise(locP, p);
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

        private static bool Confirm3DSimilarityConflict(
            Vector3 pP, float radiusP, string groupP,
            Dictionary<Vector2i, ZoneSystem.LocationInstance> instancesP) //Vector3 pP... slap the like button davie504
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

                    string instGroup = instance.m_location.m_prefabName;
                    if (!string.IsNullOrEmpty(instance.m_location.m_group))
                    {
                        instGroup = instance.m_location.m_group;
                    }

                    if (instGroup != groupP)
                    {
                        continue;
                    }

                    float dx = instance.m_position.x - pP.x;
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

        /**
        * Builds the m_groupMax system: who queries, at what radius, and who they depend on.
        * Runs once, over ~150 entries, before a single dart is thrown.
        */
        private static void BuildMaxSystem(ZoneSystem zsP)
        {
            List<MaxQuery> queries = new List<MaxQuery>();
            _maxGridByPrefab = new Dictionary<string, PresenceGrid>(StringComparer.Ordinal);
            _maxHostsByPrefab = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            Dictionary<string, int> queryIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (ZoneLocation loc in zsP.m_locations)
            {
                if (!loc.m_enable || !Compatibility.IsValidLocation(loc))
                {
                    continue;
                }
                if (loc.m_maxDistanceFromSimilar <= 0f)
                {
                    continue;
                }

                string groupMax = loc.m_groupMax;
                if (groupMax == null)
                {
                    groupMax = string.Empty;
                }

                string key = loc.m_prefabName;
                if (groupMax.Length > 0)
                {
                    key = groupMax;
                }

                string gridKey = $"MAX:{key}:{loc.m_maxDistanceFromSimilar:F0}";

                bool hasQuery = queryIndexByKey.TryGetValue(gridKey, out int existingIndex);
                if (!hasQuery)
                {
                    MaxQuery query = new MaxQuery
                    {
                        PrefabName = loc.m_prefabName,
                        GroupMax = groupMax,
                        Radius = loc.m_maxDistanceFromSimilar,
                        Grid = PresenceGrid.GetOrCreate(gridKey)
                    };
                    existingIndex = queries.Count;
                    queries.Add(query);
                    queryIndexByKey[gridKey] = existingIndex;
                }

                _maxGridByPrefab[loc.m_prefabName] = queries[existingIndex].Grid;
            }

            _maxQueries = queries.ToArray();

            if (_maxQueries.Length == 0)
            {
                return;
            }

            BuildMaxHosts(zsP);

            if (ModConfig.DiagnosticMode.Value)
            {
                for (int i = 0; i < _maxQueries.Length; i++)
                {
                    string hostList = "none";
                    if (_maxHostsByPrefab.TryGetValue(_maxQueries[i].PrefabName, out List<string> hosts) && hosts.Count > 0)
                    {
                        hostList = string.Join(",", hosts.ToArray());
                    }
                    DiagnosticLog.WriteTimestampedLog(
                        $"[LPA] MAXQ prefab={_maxQueries[i].PrefabName} groupMax={_maxQueries[i].GroupMax} " +
                        $"radius={_maxQueries[i].Radius:F0} hosts={hostList}");
                }
            }
        }

        /**
        * For every querier, the pure advertisers it needs on the ground first. "Pure" is doing real work:
        * a type that both advertises and queries is a chain link, not a host, and treating it as a
        * dependency would let two queriers wait on each other. Excluding them means the wait graph has
        * depth one and cannot cycle, which is why this needs no topological sort - which is also the
        * whole reason I refused the tier graph the last time round.
        */
        private static void BuildMaxHosts(ZoneSystem zsP)
        {
            for (int i = 0; i < _maxQueries.Length; i++)
            {
                MaxQuery query = _maxQueries[i];
                List<string> hosts = new List<string>();

                foreach (ZoneLocation candidate in zsP.m_locations)
                {
                    if (!candidate.m_enable || !Compatibility.IsValidLocation(candidate) || candidate.m_quantity <= 0)
                    {
                        continue;
                    }
                    if (candidate.m_maxDistanceFromSimilar > 0f)
                    {
                        continue;
                    }
                    if (candidate.m_prefabName == query.PrefabName)
                    {
                        continue;
                    }
                    if (!AdvertisesTo(candidate, ref query))
                    {
                        continue;
                    }
                    if (hosts.Contains(candidate.m_prefabName))
                    {
                        continue;
                    }
                    hosts.Add(candidate.m_prefabName);
                }

                _maxHostsByPrefab[query.PrefabName] = hosts;
            }
        }

        /**
        * Vanilla's max arm, verbatim: their prefab == my prefab, OR their m_groupMax == my m_groupMax.
        * The OR is unconditional in the original and stays unconditional here.
        */
        private static bool AdvertisesTo(ZoneLocation locP, ref MaxQuery queryP)
        {
            if (locP.m_prefabName == queryP.PrefabName)
            {
                return true;
            }
            if (queryP.GroupMax.Length > 0 && locP.m_groupMax == queryP.GroupMax)
            {
                return true;
            }
            return false;
        }

        // Null when this type has no max rule, which is 148 of 149 vanilla types.
        private static PresenceGrid ResolveMaxGrid(ZoneLocation locP)
        {
            if (_maxQueries.Length == 0)
            {
                return null;
            }
            if (locP.m_maxDistanceFromSimilar <= 0f)
            {
                return null;
            }
            _maxGridByPrefab.TryGetValue(locP.m_prefabName, out PresenceGrid grid);
            return grid;
        }

        public static List<string> GetMaxHostPrefabs(string prefabNameP)
        {
            if (_maxHostsByPrefab == null)
            {
                return null;
            }
            _maxHostsByPrefab.TryGetValue(prefabNameP, out List<string> hosts);
            return hosts;
        }

        /**
        * Paint a placement into every max grid it advertises to. Sits next to CommitToGroup at every
        * commit site rather than inside it, because CommitToGroup is handed the resolved group STRING and
        * this needs the location - m_groupMax and m_prefabName both.
        *
        * Length check first so a world with no max types pays one compare per placement.
        */
        private static void CommitMaxAdvertise(ZoneLocation locP, Vector3 pP)
        {
            MaxQuery[] queries = _maxQueries;
            if (queries.Length == 0)
            {
                return;
            }
            for (int i = 0; i < queries.Length; i++)
            {
                if (AdvertisesTo(locP, ref queries[i]))
                {
                    queries[i].Grid.Commit(pP, queries[i].Radius);
                }
            }
        }

        // Rasterize a placement into all radius sub-grids for the group.
        // Heterogeneous groups have multiple sub-grids (one per distinct minDistanceFromSimilar value).
        // Every sub-grid must see every placement so HasConflict works regardless of which LTS is querying.
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

        // Parallel path overload: caller provides globalPlaced directly (ctr.Placed + _centerFirstCounts) because
        // RegisterLocation isdeferred until the post-worker commit wave on the main thread.
        private static void FlushLTS(ZoneSystem zsP, ZoneLocation locP, PlacementCounters ctrP, int globalPlacedOverrideP)
        {
            FlushLTSCore(zsP, locP, ctrP, globalPlacedOverrideP);
        }

        private static void FlushLTSCore(ZoneSystem zsP, ZoneLocation locP, PlacementCounters ctrP, int globalPlacedP)
        {
            string prefab = locP.m_prefabName;

            int origQty = Interleaver.GetOriginalQuantity(prefab);
            bool isComplete = globalPlacedP >= origQty;

            int minNeeded = PlayabilityPolicy.GetMinimumNeededCount(prefab, origQty);
            bool isNecessitySatisfied = globalPlacedP >= minNeeded;

            bool wasRelaxed = ConstraintRelaxer.RelaxationAttempts.TryGetValue(prefab, out int relaxCount) && relaxCount > 0;
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
                    RelaxationTracker.MarkRelaxationSucceeded(prefab);
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
                    RelaxationTracker.CheckAndMarkFailed(prefab, globalPlacedP, origQty, locP.m_prioritized);
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
            long inTerr = inSim - ctrP.ErrSim;
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
        * Sorted: prioritized first (vanilla's OrderByDescending(m_prioritized)),
        * then modded locations ("MWL_" prefix) pushed to the back so vanilla
        * types fill their quotas before mod-added types compete for the same biomes.
        * I should do it for ALL though, like Therzie's, RtD's and all the other mods
        * that would go before vanilla, but this is a ton of research that I need to do.
        * Never mind the testing... MWL_ though is an easy one as it adds a ton.
        *
        * CenterFirst types get (quantity - 1) tokens since CenterFirstPlacer
        * already placed the first instance. If interleaving is on, each token
        * represents exactly 1 placement attempt; otherwise all quantity is packed
        * into a single token.
        */
        private static List<PlacementToken> BuildTokenList(ZoneSystem zsP)
        {
            List<PlacementToken> tokens = new List<PlacementToken>();

            // API mode iterates the per-call work list (clones with TargetQuantity
            // already stamped over m_quantity, optionally packetized). World-gen
            // iterates zsP.m_locations as before.
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
            // Priority sort must be stable - vanilla uses OrderByDescending(m_prioritized).
            List<ZoneLocation> eligible = new List<ZoneLocation>();
            for (int i = 0; i < source.Count; i++)
            {
                ZoneLocation loc = source[i];
                // EWD-mirror: accept blueprints (empty AssetID, name-only SoftReference)
                // so they actually make it into the token list. Quantity check stays inline.
                if (loc.m_enable && Compatibility.IsValidLocation(loc) && loc.m_quantity > 0)
                {
                    eligible.Add(loc);
                }
            }

            eligible.Sort(CompareLocationsByPriorityThenOrigin);

            foreach (ZoneLocation loc in eligible)
            {
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

        // Prioritized locations sort first (vanilla behaviour).
        // Within the same priority tier, modded locations (MWL_ prefix) sort after vanilla types so vanilla fills its quotas first.
        private static int CompareLocationsByPriorityThenOrigin(ZoneLocation aP, ZoneLocation bP)
        {
            if (aP.m_prioritized != bP.m_prioritized)
            {
                if (aP.m_prioritized)
                {
                    return -1;
                }
                return 1;
            }

            bool aIsModded = aP.m_prefabName.StartsWith("MWL_", StringComparison.OrdinalIgnoreCase);
            bool bIsModded = bP.m_prefabName.StartsWith("MWL_", StringComparison.OrdinalIgnoreCase);
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
    }
}