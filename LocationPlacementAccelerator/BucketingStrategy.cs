// v1
/**
* Abstract base for all zone bucketing strategies.
* Defines the contract that SurveyMode delegates through: zone retrieval,
* candidate list building, occupancy marking, and cache management.
* Concrete implementations (for now): LocationTypeBucketingStrategy.
* Biome bucketing which I think will perform better I leave for later.
* Obviously the hybrid will be done after I do the biome properly.
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public abstract class BucketingStrategy
    {
        protected static int _cachedVisitLimit = 1;
        protected static float _cachedWorldRadius = 10000f;

        public virtual void Initialize()
        {
            WorldSurveyData.Initialize();
            _cachedVisitLimit = ModConfig.SurveyVisitLimit.Value;
            _cachedWorldRadius = ModConfig.WorldRadius;
        }

        public abstract bool GetZone(ZoneLocation locationP, out Vector2i result);

        public virtual void MarkZoneOccupied(int zoneIndexP)
        {
            if (zoneIndexP < 0 || zoneIndexP >= WorldSurveyData.Grid.Length)
            {
                return;
            }
            WorldSurveyData.OccupiedZoneIndices.Add(zoneIndexP);
        }

        public abstract void PruneZone(string prefabNameP, Vector2i zoneIdP);
        public abstract void ClearCache(string prefabNameP);
        public abstract void DumpDiagnostics();
        public abstract List<Vector2i> GetOrBuildCandidateList(ZoneSystem.ZoneLocation locationP);

        
        protected static void Shuffle<T>(List<T> listP)
        {
            int n = listP.Count;
            while (n > 1)
            {
                n--;
                int k = UnityEngine.Random.Range(0, n + 1);
                T val = listP[k];
                listP[k] = listP[n];
                listP[n] = val;
            }
        }
    }
}
