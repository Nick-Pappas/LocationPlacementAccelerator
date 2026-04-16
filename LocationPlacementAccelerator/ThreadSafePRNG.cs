// v1
/**
* Thread-local PRNG wrapper for the replaced parallel placement engine.
* Replaces UnityEngine.Random (not thread-safe) for all off-main-thread randomness.
* Seeded per-LT via SeedForLts(worldSeed ^ prefabName.GetStableHashCode()) so each
* location type gets a deterministic, reproducible dart sequence regardless of which
* thread executes it. The lazy-init fallback (ManagedThreadId) is retained only for
* callers that skip SeedForLts (e.g. CenterFirstPlacer).
*/
#nullable disable
using System;
using System.Threading;
using UnityEngine;

namespace LPA
{
    internal static class ThreadSafePRNG
    {
        [ThreadStatic]
        private static System.Random _rng;

        // Lazy-init: seeds from world seed XOR thread ID so each thread diverges even without an explicit SeedForLts call.
        private static System.Random Rng
        {
            get
            {
                if (_rng == null)
                {
                    int worldSeed = Environment.TickCount;
                    if (WorldGenerator.instance != null)
                    {
                        worldSeed = WorldGenerator.instance.GetSeed();
                    }
                    _rng = new System.Random(worldSeed ^ Thread.CurrentThread.ManagedThreadId);
                }
                return _rng;
            }
        }

        /**
        * Seeds the thread-local RNG for a specific location type.
        * Call once per LTS work item with worldSeed ^ prefabName.GetStableHashCode()
        * so the same location type produces the same dart sequence across runs
        * regardless of which thread pool thread picks it up.
        */
        public static void SeedForLts(int ltsSeedP)
        {
            _rng = new System.Random(ltsSeedP);
        }

        /**
        * Atomically swaps the thread-local RNG and returns the previous one.
        * Used by the round-robin parallel path to preserve each prefab's
        * continuous dart sequence when the worker cycles between prefabs:
        *   save outgoing:   prefabRngs[last] = SwapRng(null)
        *   restore incoming: SwapRng(prefabRngs[current])
        */
        public static System.Random SwapRng(System.Random nextP)
        {
            System.Random prev = _rng;
            _rng = nextP;
            return prev;
        }

        public static void Reset()
        {
            _rng = null;
        }

        public static float NextFloat(float minP, float maxP)
        {
            return minP + (float)(Rng.NextDouble() * (maxP - minP));
        }

        public static int NextInt(int minP, int maxP)
        {
            return (int)(Rng.NextDouble() * (maxP - minP)) + minP;
        }

        // Polar method: exactly 2 NextDouble calls and no rejection loop.
        public static Vector2 InsideUnitCircle()
        {
            System.Random rng = Rng;
            double r = Math.Sqrt(rng.NextDouble());
            double theta = rng.NextDouble() * (2.0 * Math.PI);
            return new Vector2((float)(r * Math.Cos(theta)), (float)(r * Math.Sin(theta)));
        }

        // Uniform random world-space point within the 64m zone square.
        // Y is 0, caller resolves terrain height separately.
        public static Vector3 NextDartInZone(Vector2i zoneP)
        {
            float x = zoneP.x * 64f + NextFloat(-32f, 32f);
            float z = zoneP.y * 64f + NextFloat(-32f, 32f);
            return new Vector3(x, 0f, z);
        }
    }
}
