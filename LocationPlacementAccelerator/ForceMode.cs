// v1
/**
* Analytical zone generator for the distance donut.
* Directly samples a uniform point in the annulus using polar coordinates
* with sqrt-weighted radius so 100% acceptance rate, no rejection loop.
* Second thing I implemented, after FilterMode, again kept for archaeology purposes.
* Also good for didactic purposes for the kids.
*/
#nullable disable
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class ForceMode
    {
        public static Vector2i GenerateDonut(float minDistanceP, float maxDistanceP)
        {
            // Pad by one zone width to stay safely inside the annulus bounds.
            float safeMin = minDistanceP + 64f;
            float safeMax = maxDistanceP - 64f;

            if (safeMax <= safeMin)
            {
                safeMax = safeMin + 1f;
            }

            float minR = safeMin / 64f;
            float maxR = safeMax / 64f;

            // Uniform distribution in annulus: r = sqrt(random(Rmin^2, Rmax^2))
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float radius = Mathf.Sqrt(UnityEngine.Random.Range(minR * minR, maxR * maxR));

            int x = Mathf.RoundToInt(radius * Mathf.Cos(angle));
            int z = Mathf.RoundToInt(radius * Mathf.Sin(angle));

            return new Vector2i(x, z);
        }
    }
}
