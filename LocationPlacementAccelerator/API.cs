// v0.0.4
/**
* LPA public API. The only file external callers should reflect against.
* Reflection targets: LPA.API.RunCustomPlacement, LPA.API.IsAvailable.
*
* Contract:
*   - LPA owns spatial allocation only. The caller owns CLI parsing,
*     player-base detection, ZDO destruction, terrain reset, and any
*     post-call materialization sweep. LPA is a pure function from
*     (requests, allowedZones, options) -> mutations on m_locationInstances.
*   - The caller has already swept any prior unplaced reservations for the
*     prefabs it cares about, and computed AllowedZones from its own policy.
*     LPA never relaxes AllowedZones; even under MaxRelaxationAttempts > 0
*     the zone allow-list stays absolute.
*   - PlacementRequest.TargetQuantity is the WORLD target for that prefab.
*     LPA subtracts the current m_locationInstances count and tries to add
*     the difference, matching vanilla UW DistributeLocations semantics.
*   - WorldSurveyData lazy-inits on the first API call after a session
*     start. Subsequent calls reuse the survey. Terrain is immutable
*     post-gen so this is safe.
*   - LPA does not flip ZoneSystem.LocationsGenerated; the game is already
*     fully generated at API call time.
*
* Lifecycle: RunCustomPlacement is an IEnumerator. Callers MUST consume
* it via Unity's coroutine machinery (yield return LPA.API.RunCustomPlacement(...))
* or an explicit using(...) block - the try/finally that resets the
* internal ApiState fields runs on iterator Dispose. Manual MoveNext loops
* that don't dispose will leak per-call state and corrupt the next call.
*/
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LPA
{
    public static class API
    {
        /**
        * Static probe for callers that want to detect whether LPA is
        * loaded before reflecting onto RunCustomPlacement. Assembly
        * present == API usable, so just returns true.
        */
        public static bool IsAvailable()
        {
            return true;
        }

        /**
        * Reflection-friendly overload. Callers that want to invoke this via
        * reflection (no compile-time reference to LPA) can build a plain
        * Dictionary<ZoneLocation, int> using only BCL + Valheim types, then
        * find this method by exact parameter signature. Internally this
        * just maps to a List<PlacementRequest> and forwards.
        *
        * The dictionary uses reference equality on ZoneLocation, so each
        * variant the caller wants placed must appear under a distinct
        * ZoneLocation reference. UW's zs.m_locations.Where(...).ToArray()
        * pattern yields distinct references, so this is a non-issue in
        * practice.
        *
        * Validation fires synchronously here rather than being deferred to
        * the first MoveNext on the iterator, so a null dictionary throws
        * at the Invoke call site where the stack trace is useful.
        */
        public static IEnumerator RunCustomPlacement(
            Dictionary<ZoneSystem.ZoneLocation, int> requestsP,
            HashSet<Vector2i> allowedZonesP,
            LpaApiOptions optionsP = null)
        {
            if (requestsP == null)
            {
                throw new ArgumentNullException(nameof(requestsP));
            }

            List<PlacementRequest> mapped = new List<PlacementRequest>(requestsP.Count);
            foreach (KeyValuePair<ZoneSystem.ZoneLocation, int> kvp in requestsP)
            {
                if (kvp.Key == null)
                {
                    continue;
                }
                mapped.Add(new PlacementRequest
                {
                    Location = kvp.Key,
                    TargetQuantity = kvp.Value
                });
            }
            return RunCustomPlacement(mapped, allowedZonesP, optionsP);
        }

        /**
        * Primary entry. The other overload forwards to this one.
        *
        * Coroutine entry. The caller's StartCoroutine wraps this and
        * yields its IEnumerator until completion. PlacementEngine.Run
        * drives the actual work; the try/finally here owns the per-call
        * state lifecycle (set on entry, cleared on natural completion or
        * iterator disposal).
        *
        * requestsP: one entry per ZoneLocation variant the caller wants
        *            placed. TargetQuantity is the WORLD target for that
        *            prefab; LPA subtracts the current m_locationInstances
        *            count and tries to add the difference.
        *
        * allowedZonesP: zones outside this set are dropped from candidate
        *                lists before any relaxation logic runs. Null means
        *                "no constraint" - candidates filtered only by the
        *                location's biome/area/distance.
        *
        * optionsP: per-call behavioral overrides. Null is acceptable
        *           (everything inherits ModConfig defaults).
        */
        public static IEnumerator RunCustomPlacement(
            IEnumerable<PlacementRequest> requestsP,
            HashSet<Vector2i> allowedZonesP,
            LpaApiOptions optionsP = null)
        {
            if (requestsP == null)
            {
                throw new ArgumentNullException(nameof(requestsP));
            }
            if (ZoneSystem.instance == null)
            {
                throw new InvalidOperationException(
                    "[LPA] RunCustomPlacement called before ZoneSystem.instance is available.");
            }

            List<PlacementRequest> materialized = new List<PlacementRequest>(requestsP);
            if (materialized.Count == 0)
            {
                DiagnosticLog.WriteTimestampedLog("[LPA.API] RunCustomPlacement called with 0 requests, exiting.");
                yield break;
            }

            ApiState.IsApiRun = true;
            ApiState.Requests = materialized;
            ApiState.AllowedZones = allowedZonesP;
            ApiState.Options = optionsP ?? new LpaApiOptions();
            ApiState.WorkList = null;

            // Reset per-call tracker state. World-gen resets these in
            // GenerationProgress.StartGeneration -> ConstraintRelaxer.Reset();
            // API mode bypasses StartGeneration's initialized-guard on the
            // second-and-later call, so the reset has to happen here. The
            // ZoneLocation stat rollback itself is done in the previous call's
            // finally via ConstraintRelaxer.RestoreAllStats, so by the time
            // we reach this point any prior relaxation has already been
            // undone in zsP.m_locations - the Reset call just clears the
            // bookkeeping dictionaries.
            ConstraintRelaxer.Reset();

            // Per-call candidate cache wipe. The AllowedZones filter bakes
            // into the cache; a follow-up call with a different mask would
            // otherwise see stale entries.
            SurveyMode.ClearAllCaches();

            string tag = optionsP?.CallerTag;
            if (string.IsNullOrEmpty(tag))
            {
                tag = "API";
            }
            DiagnosticLog.WriteTimestampedLog(
                $"[LPA.API] RunCustomPlacement begin. Caller={tag}, requests={materialized.Count}, " +
                $"allowedZones={(allowedZonesP == null ? "ALL" : allowedZonesP.Count.ToString())}, " +
                $"parallel={(ApiState.Options.Parallel ?? ModConfig.EnableParallelPlacement.Value)}, " +
                $"interleaved={(ApiState.Options.Interleaved ?? ModConfig.EnableInterleavedScheduling.Value)}, " +
                $"maxRelax={(ApiState.Options.MaxRelaxationAttempts ?? ModConfig.MaxRelaxationAttempts.Value)}");

            try
            {
                IEnumerator inner = PlacementEngine.Run(ZoneSystem.instance);
                while (inner.MoveNext())
                {
                    yield return inner.Current;
                }
            }
            finally
            {
                DiagnosticLog.WriteTimestampedLog($"[LPA.API] RunCustomPlacement end. Caller={tag}.");

                // Order matters slightly: restore stats before clearing
                // relaxer state, so the snapshot is still available when we
                // walk ZoneSystem.m_locations.
                ConstraintRelaxer.RestoreAllStats();
                Interleaver.ResetApiState();
                SurveyMode.ClearAllCaches();

                // Defensive: if the engine threw mid-run, EndGeneration was
                // skipped and GenerationProgress._initialized stays true. The
                // next StartGeneration call then early-returns, leaving stale
                // counters in the overlay (the user sees "X of Y placed" where
                // X is the previous run's tail). ForceCleanup unconditionally
                // resets the overlay state machine. On the happy path EndGeneration
                // already cleaned up, so this is a no-op.
                GenerationProgress.ForceCleanup();

                ApiState.IsApiRun = false;
                ApiState.Requests = null;
                ApiState.AllowedZones = null;
                ApiState.Options = null;
                ApiState.WorkList = null;
            }
        }
    }
}