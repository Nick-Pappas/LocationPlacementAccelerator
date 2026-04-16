// v1
/**
* Rejection-sampling zone generator for the distance donut.
* Generates random square coordinates and retries until one falls
* within [minDistance, maxDistance]. Burns CPU here so the outer
* placement loop never sees an out-of-range zone.
* Kept it for archaeology purposes. Was the first thing I tried, and you know...
* Every line is a child of mine.
*/
#nullable disable
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class FilterMode
    {
        public static Vector2i GenerateSieve(float minDistanceP, float maxDistanceP, float worldRadiusP)
        {
            int maxZoneRadius = (int)(worldRadiusP / 64f);
            Vector2i zone;
            Vector3 center;
            float dist;
            int attempts = 0;

            do
            {
                zone = new Vector2i(
                    UnityEngine.Random.Range(-maxZoneRadius, maxZoneRadius),
                    UnityEngine.Random.Range(-maxZoneRadius, maxZoneRadius)
                );

                center = ZoneSystem.GetZonePos(zone);
                dist = center.magnitude;
                attempts++;

                if (attempts > 1000) //1000 sounds reasonable.
                {
                    break;
                }

            } while (dist < minDistanceP || dist > maxDistanceP);

            return zone;
        }
    }
}
