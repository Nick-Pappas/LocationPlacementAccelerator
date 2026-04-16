// v1
/**
* Per-zone spatial fingerprint used by the survey and bucketing systems.
* Packed bitmasks for biome, area, and distance classification.
* Clarity and ridiculousness scores both 10/10
*/
#nullable disable
using UnityEngine;

namespace LPA
{
    public struct ZoneProfile
    {
        public Vector2i ID;
        public int BiomeMask;
        public int AreaMask;
        public ushort DistanceMask;
    }
}
