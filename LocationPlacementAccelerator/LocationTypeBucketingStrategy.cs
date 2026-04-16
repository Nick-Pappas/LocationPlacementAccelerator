// v1
/**
* LT (Location Type) bucketing strategy!
* I wrote this second, after my original biome bucketing strategy to compare experimentally.
* 
* Scans Grid[] once per location type to build a shuffled candidate list
* filtered by biome, area, distance, and coastal classification.
* GetZone walks the list with a forward cursor, swap-removing occupied
* zones on the fly. When the cursor wraps, the list is reshuffled and
* the visit pass counter advances toward the configured limit.
* 
* It is the only one surviving currently just so that I can publish something good enough.
* It is an eager approach as opposed to my original lazy biome bucketing so unlike
* biome, I cannot be using the learning stuff per token attempted. 
* Note that this can get slow if the set of location types is large. 
* For vanilla, and for the heavily modded stuff I tried where L is ~400something LTs it is fine.
* 
* TODO: Freaking fix my biome bucketing strategy. It should be running circles
* around this one in terms of performance due to learning especially on 
* difficult worlds generated with Better Continents, or any other non vanilla noise generator 
* and especially if we have so many mods adding locations where 
* the O(L) starts to matter.
* Currently biome works identically to location performance wise so it is clearly buggy.
* Lazy should be beating eager as a matter of principle. In life too?:D
* I need to remove the TODO comment once I fix it.
*/
#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public class LocationTypeBucketingStrategy : BucketingStrategy
    {
        private ConcurrentDictionary<string, List<Vector2i>> _candidateCache = new ConcurrentDictionary<string, List<Vector2i>>(StringComparer.Ordinal);
        private ConcurrentDictionary<string, int> _explorationIndex = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private ConcurrentDictionary<string, int> _visitPass = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private ConcurrentDictionary<string, byte> _exhaustedLocations = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly object _cacheLock = new object();

        public override void Initialize()
        {
            base.Initialize();
        }

        public override void ClearCache(string prefabNameP)
        {
            _candidateCache.TryRemove(prefabNameP, out _);
            _explorationIndex.TryRemove(prefabNameP, out _);
            _visitPass.TryRemove(prefabNameP, out _);
        }

        public override void DumpDiagnostics() { }

        public override bool GetZone(ZoneLocation locationP, out Vector2i result)
        {
            result = Vector2i.zero;
            string prefabName = locationP.m_prefabName;

            if (!_candidateCache.TryGetValue(prefabName, out List<Vector2i> candidates))
            {
                lock (_cacheLock)
                {
                    if (!_candidateCache.TryGetValue(prefabName, out candidates))
                    {
                        candidates = ScanWorldForCandidates(locationP, prefabName);
                        _candidateCache.TryAdd(prefabName, candidates);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                SurveyMode.SurveyExhausted = true;
                result = Vector2i.zero;
                SurveyMode.CurrentActiveZoneIndex = -1;
                return false;
            }

            int limit = _cachedVisitLimit;

            while (true)
            {
                if (candidates.Count == 0)
                {
                    HandleExhaustion(prefabName, 0, limit);
                    return false;
                }

                int pass = 0;
                if (_visitPass.TryGetValue(prefabName, out int p))
                {
                    pass = p;
                }
                if (pass >= limit)
                {
                    HandleExhaustion(prefabName, candidates.Count, limit);
                    return false;
                }

                int idx = 0;
                if (_explorationIndex.TryGetValue(prefabName, out int ei))
                {
                    idx = ei;
                }
                if (idx >= candidates.Count)
                {
                    _visitPass[prefabName] = pass + 1;
                    if (pass + 1 >= limit)
                    {
                        HandleExhaustion(prefabName, candidates.Count, limit);
                        return false;
                    }
                    Shuffle(candidates);
                    idx = 0;
                    _explorationIndex[prefabName] = 0;
                }

                result = candidates[idx];
                bool hasIdx = WorldSurveyData.ZoneToIndex.TryGetValue(result, out int zoneIndex);

                if (hasIdx && WorldSurveyData.OccupiedZoneIndices.Contains(zoneIndex))
                {
                    // I do not care about order one iota here.Thus a great opportunity for a bada boom bada bam O(1) by swapping with last and popping.
                    int last = candidates.Count - 1;
                    candidates[idx] = candidates[last];
                    candidates.RemoveAt(last);
                    continue;
                }

                SurveyMode.CurrentActiveZoneIndex = -1;
                if (hasIdx)
                {
                    SurveyMode.CurrentActiveZoneIndex = zoneIndex;
                }
                _explorationIndex[prefabName] = idx + 1;
                return true;
            }
        }

        public override void MarkZoneOccupied(int zoneIndexP)
        {
            if (zoneIndexP >= 0)
            {
                WorldSurveyData.OccupiedZoneIndices.Add(zoneIndexP);
            }
        }

        public override void PruneZone(string prefabNameP, Vector2i zoneIdP) { }

        public override List<Vector2i> GetOrBuildCandidateList(ZoneLocation locationP)
        {
            string prefabName = locationP.m_prefabName;
            if (!_candidateCache.TryGetValue(prefabName, out List<Vector2i> candidates))
            {
                lock (_cacheLock)
                {
                    if (!_candidateCache.TryGetValue(prefabName, out candidates))
                    {
                        candidates = ScanWorldForCandidates(locationP, prefabName);
                        _candidateCache.TryAdd(prefabName, candidates);
                    }
                }
            }
            return new List<Vector2i>(candidates);
        }

        private void HandleExhaustion(string prefabNameP, int candidateCountP, int limitP)
        {
            _exhaustedLocations.TryAdd(prefabNameP, 0);
            SurveyMode.SurveyExhausted = true;
            SurveyMode.CurrentActiveZoneIndex = -1;
        }

        private List<Vector2i> ScanWorldForCandidates(ZoneLocation locationP, string prefabNameP)
        {
            List<Vector2i> results = new List<Vector2i>();
            int requiredArea = (int)locationP.m_biomeArea;
            int searchBiome = (int)locationP.m_biome;

            // AshLands locations with sub-sea-level altitude ranges need to match
            // my homebrewed BiomeBoilingOcean flag set during survey.
            bool isAshLands = (searchBiome & (int)Heightmap.Biome.AshLands) != 0;
            if (isAshLands && locationP.m_minAltitude < -4.0f)
            {
                if (locationP.m_maxAltitude < -4.0f)
                {
                    searchBiome = WorldSurveyData.BiomeBoilingOcean;
                }
                else
                {
                    searchBiome |= WorldSurveyData.BiomeBoilingOcean;
                }
            }

            bool coastalOnly = (searchBiome & WorldSurveyData.OceanFlags) != 0
                            && (searchBiome & WorldSurveyData.LandBiomeMask) != 0;

            float minD = locationP.m_minDistance;
            float maxD = _cachedWorldRadius;
            if (locationP.m_maxDistance > 0.1f)
            {
                maxD = locationP.m_maxDistance;
            }

            for (int i = 0; i < WorldSurveyData.Grid.Length; i++)
            {
                ZoneProfile zone = WorldSurveyData.Grid[i];

                bool biomeMatch = (zone.BiomeMask & searchBiome) != 0;
                bool areaMatch = (zone.AreaMask & requiredArea) != 0;
                if (!biomeMatch || !areaMatch)
                {
                    continue;
                }

                if (coastalOnly && ((zone.AreaMask & (int)Heightmap.BiomeArea.Edge) == 0 || (zone.BiomeMask & WorldSurveyData.CoastalBit) == 0)) //a zone cannot be coastal if it was Median
                {
                    continue;
                }

                Vector3 center = ZoneSystem.GetZonePos(zone.ID);
                float dist = center.magnitude;
                if (dist < minD || dist > maxD)
                {
                    continue;
                }

                results.Add(zone.ID);
            }

            Shuffle(results);
            return results;
        }
    }
}
