// v2
/**
* Sequential placer for m_centerFirst locations (e.g. StartTemple / spawning altar).
*
* Vanilla algorithm (from d__48 IL):
*   maxRange = m_minDistance          (not WorldRadius.  Starts at the center ring)
*   outer loop per zone:
*     zoneID = GetRandomZone(maxRange)   picks a random zone within maxRange metres of origin
*     maxRange += 1                      spiral outward by 1m per miss
*     if zone occupied              --> skip (errorLocationInZone)
*     if biomeArea mismatch         --> skip (errorBiomeArea)
*     inner loop: 20 darts per zone
*       point = GetRandomPointInZone(zoneID)
*       distance checks (minDistance / maxDistance from origin)
*       biome check
*       altitude check
*       terrain delta check
*       similarity checks
*       --> RegisterLocation on first success
*
* I replicate this exactly, using only main-thread-safe calls.
* The outer iteration budget matches vanilla's inner-loop budget (20k × OuterMultiplier).
* This runs synchronously on the main thread before the parallel batch.  
* It's 1-2 locations budget exhaustion is not a concern.
*
* v2: Dedup check now fires for ALL center-first prefabs not just m_unique ones.
* Vanilla only places 1 center-first instance per type (m_centerFirst implies the 
* spiral-out semantic which is meaningless beyond the first placement). Without 
* this, genloc-on-saved-world re-placed StartTemple even though one already 
* existed because StartTemple has m_unique=false. The PreEx + (quantity-1) 
* interleave math in PlacementEngine_Core relies on at most 1 center-first 
* instance per prefab being created here per run.
*/
#nullable disable
using System.Collections.Generic;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    internal static class CenterFirstPlacer
    {
        private const int DartsPerZone = 20;
        private const int MaxOuterIter = 200000;

        public static List<string> PlaceAll(ZoneSystem zsP)
        {
            List<string> placed = new List<string>();

            foreach (ZoneLocation loc in zsP.m_locations)
            {
                if (!loc.m_enable)
                {
                    continue;
                }
                if (!loc.m_centerFirst)
                {
                    continue;
                }
                if (loc.m_prefab == null || !loc.m_prefab.IsValid)
                {
                    continue;
                }

                /**
                * If any instance of this prefab already exists in m_locationInstances,
                * the center-first placement is already done. The non-placed sweep at the 
                * top of PlacementEngine.Run guarantees that surviving entries are real
                * spawned structures, so seeing one here means "the player already has 
                * this thing in their world" and we must not double up on it. This 
                * applies regardless of m_unique because m_centerFirst itself implies 
                * a one-shot spiral-from-center semantic.
                */
                bool alreadyExists = false;
                foreach (LocationInstance inst in zsP.m_locationInstances.Values)
                {
                    if (inst.m_location.m_prefabName == loc.m_prefabName)
                    {
                        alreadyExists = true;
                        break;
                    }
                }
                if (alreadyExists)
                {
                    placed.Add(loc.m_prefabName);
                    DiagnosticLog.WriteTimestampedLog(
                        $"[CenterFirstPlacer] {loc.m_prefabName}: already present in world, skipping center-first placement.");
                    continue;
                }

                /**
                 * Place exactly 1 instance near origin is the center-first semantic.
                 * For quantity > 1, the remaining (quantity - 1) instances are handed
                 * to the parallel engine via BuildTokenList, which uses the full Survey
                 * pool-based system at normal speed.
                 */
                if (TryPlace(zsP, loc))
                {
                    placed.Add(loc.m_prefabName);
                    DiagnosticLog.WriteTimestampedLog(
                        $"[CenterFirstPlacer] {loc.m_prefabName}: placed 1/{loc.m_quantity}.");
                }
                else
                {
                    DiagnosticLog.WriteTimestampedLog(
                        $"[CenterFirstPlacer] {loc.m_prefabName}: failed to place center-first instance. The replaced engine will handle all {loc.m_quantity}.",
                        BepInEx.Logging.LogLevel.Warning);
                }
            }

            return placed;
        }

        private static bool TryPlace(ZoneSystem zsP, ZoneLocation locP)
        {
            // maxRange spirals outward from m_minDistance, exactly as vanilla does.
            float maxRange = locP.m_minDistance;
            int outerBudget = Mathf.Max(1, Mathf.RoundToInt(MaxOuterIter * ModConfig.OuterMultiplier.Value));

            for (int iter = 0; iter < outerBudget; iter++)
            {
                /**
                 * Replicate GetRandomZone(maxRange) inline using ThreadSafePRNG.
                 * GetRandomZone calls UnityEngine.Random.Range twice per outer iteration.
                 * Since the spiral length varies per run (StartTemple takes a non-deterministic
                 * number of outer attempts), using the vanilla call would shift
                 * UnityEngine.Random state before the main loop's Shuffle calls.
                 * Inlining with ThreadSafePRNG fully decouples us.
                 */
                int zr = Mathf.FloorToInt(maxRange / 64f) + 1;
                Vector2i zoneID = new Vector2i(
                    ThreadSafePRNG.NextInt(-zr, zr),
                    ThreadSafePRNG.NextInt(-zr, zr));
                maxRange += 1f;

                if (zsP.m_locationInstances.ContainsKey(zoneID))
                {
                    continue;
                }

                Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
                Heightmap.BiomeArea area = WorldGenerator.instance.GetBiomeArea(zonePos);
                if ((locP.m_biomeArea & area) == 0)
                {
                    continue;
                }

                for (int di = 0; di < DartsPerZone; di++)
                {
                    float rx = ThreadSafePRNG.NextFloat(-32f + locP.m_exteriorRadius, 32f - locP.m_exteriorRadius);
                    float rz = ThreadSafePRNG.NextFloat(-32f + locP.m_exteriorRadius, 32f - locP.m_exteriorRadius);
                    Vector3 p = zonePos + new Vector3(rx, 0f, rz);

                    float dist = p.magnitude;

                    if (locP.m_minDistance > 0f && dist < locP.m_minDistance)
                    {
                        continue;
                    }
                    if (locP.m_maxDistance > 0f && dist > locP.m_maxDistance)
                    {
                        continue;
                    }

                    if ((WorldGenerator.instance.GetBiome(p) & locP.m_biome) == 0)
                    {
                        continue;
                    }

                    /**
                     * GetHeight returns raw world-Y. m_minAltitude/m_maxAltitude are
                     * sea-level-relative. ZoneSystem.m_waterLevel is the fixed water
                     * plane (world-Y 30m). Subtract before comparing.
                     */
                    float rawHeight = WorldGenerator.instance.GetHeight(p.x, p.z);
                    p.y = rawHeight;
                    float alt = rawHeight - ZoneSystem.instance.m_waterLevel;
                    if (alt < locP.m_minAltitude || alt > locP.m_maxAltitude)
                    {
                        continue;
                    }

                    if (locP.m_maxTerrainDelta > 0f || locP.m_minTerrainDelta > 0f)
                    {
                        ThreadSafeTerrainDelta.GetTerrainDelta(p, locP.m_exteriorRadius, out float delta, out _);
                        if (delta > locP.m_maxTerrainDelta || delta < locP.m_minTerrainDelta)
                        {
                            continue;
                        }
                    }

                    if (locP.m_minDistanceFromSimilar > 0f &&
                        zsP.HaveLocationInRange(locP.m_prefabName, locP.m_group, p, locP.m_minDistanceFromSimilar, false))
                    {
                        continue;
                    }

                    if (locP.m_maxDistanceFromSimilar > 0f &&
                        !zsP.HaveLocationInRange(locP.m_prefabName, locP.m_group, p, locP.m_maxDistanceFromSimilar, true))
                    {
                        continue;
                    }

                    zsP.RegisterLocation(locP, p, false);

                    bool hasZoneIndex = WorldSurveyData.ZoneToIndex.TryGetValue(zoneID, out int zIdx);
                    if (hasZoneIndex)
                    {
                        WorldSurveyData.OccupiedZoneIndices.Add(zIdx);
                        SurveyMode.MarkZoneOccupied(zIdx);
                    }

                    DiagnosticLog.WriteTimestampedLog(
                        $"[CenterFirstPlacer] Placed {locP.m_prefabName}.");

                    return true;
                }
            }

            return false;
        }
    }
}