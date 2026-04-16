// v1
/**
* Thread-safe terrain delta sampler. Takes random height samples within
* a radius of a point and reports the elevation spread and slope direction.
* Used by the placement engines in place of vanilla's non-thread-safe version.
*/
#nullable disable
using UnityEngine;

namespace LPA
{
    internal static class ThreadSafeTerrainDelta
    {
        private const int Samples = 10;

        public static void GetTerrainDelta(
            Vector3 centerP, float radiusP,
            out float delta, out Vector3 slopeDirection,
            int zoneGridIdxP = -1)
        {
            float maxHeight = -999999f;
            float minHeight = 999999f;
            Vector3 highPoint = centerP;
            Vector3 lowPoint = centerP;

            for (int i = 0; i < Samples; i++)
            {
                Vector2 offset = ThreadSafePRNG.InsideUnitCircle() * radiusP;
                Vector3 samplePos = centerP + new Vector3(offset.x, 0f, offset.y);
                float height = WorldGenerator.instance.GetHeight(samplePos.x, samplePos.z);

                if (height < minHeight)
                {
                    minHeight = height;
                    lowPoint = samplePos;
                }
                if (height > maxHeight)
                {
                    maxHeight = height;
                    highPoint = samplePos;
                }
            }

            delta = maxHeight - minHeight;
            slopeDirection = Vector3.Normalize(lowPoint - highPoint);
        }
    }
}
