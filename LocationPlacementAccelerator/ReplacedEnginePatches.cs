// v2
/**
* Single Harmony patch class for the replaced engine.
* Intercepts d__46 (GenerateLocationsTimeSliced outer coordinator) on its
* very first MoveNext() call, launches PlacementEngine.Run as a replacement
* coroutine, then permanently suppresses all subsequent d__46 MoveNext() calls.
*
* This is the entire replaced engine patch surface. All V1 patches (transpilers, inner loop
* prefix, GetRandomZone prefix, HaveLocationInRange prefix) are not registered
* when the replaced engine is active. (see LPAPlugin.PatchCoroutines().)
*
* v2: Reset() is now also called from GenerationProgress.EndGeneration so a 
* second `genloc` console command in the same session actually re-runs LPA. 
* Previously the latches stayed set across runs and the prefix silently
* suppressed every call after the first, making subsequent genlocs no-ops.
* Game.Logout still calls Reset for the load-different-world case.
*/
#nullable disable
using HarmonyLib;
using UnityEngine;

namespace LPA
{
    public static class ReplacedEnginePatches
    {
        private static volatile bool _firstCallDone = false;
        private static volatile bool _v2Started = false;

        /**
        * Harmony prefix on d__46.MoveNext().
        *
        * d__46 state machine structure:
        *   State 0 (first call): vanilla sets m_estimatedGenerateLocationsCompletionTime
        *                         and does a bare "yield return null". No real work.
        *                         The engine parks d__46 here while the minimap generates (~6s).
        *   State 1 (second call): vanilla logs "Generating locations" and begins placement.
        *
        * I must let state 0 execute so the engine parks the coroutine and the minimap
        * finishes before I touch anything. If I intercept on state 0, the replaced engine runs concurrently
        * with minimap generation, WorldSurveyData.ScanEntireWorld's Parallel.For races
        * the minimap's world-geometry calls, perturbing UnityEngine.Random non-deterministically creating
        * the mess I was debugging for a while.
        *
        * Fix: pass state 0 through (return true). Intercept from state 1 onward.
        */
        public static bool OuterLoopV2Prefix()
        {
            if (!_firstCallDone)
            {
                _firstCallDone = true;
                return true;
            }

            if (!_v2Started)
            {
                _v2Started = true;

                if (ZoneSystem.instance != null)
                {
                    ZoneSystem.instance.StartCoroutine(
                        PlacementEngine.Run(ZoneSystem.instance));
                }
            }

            return false;
        }

        // Called on Game.Logout (fresh-world case) and on EndGeneration (re-arm 
        // for a follow-up genloc in the same session).
        public static void Reset()
        {
            _firstCallDone = false;
            _v2Started = false;
        }
    }
}