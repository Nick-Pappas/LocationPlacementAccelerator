// v1.0.1
/**
* Per-zone spatial fingerprint used by the survey and bucketing systems.
* Packed bitmasks for biome, area, and distance classification.
* Clarity and ridiculousness scores both 10/10
*
* 1.0.1: BiomeMask widened from int to long because EWD custom biomes can occupy
* any of bits 0..31 (Mistlands is bit 9, EWD doubles from there up to bit 31, then
* wraps to bit 7). LPA's synthetic flags (BiomeBoilingOcean, CoastalBit) must live
* at bits that EWD can never produce, so they moved to bit 40 and bit 41. int was
* no longer wide enough to hold both the full 32-bit biome range and our flags.
*/
#nullable disable
using UnityEngine;

namespace LPA
{
    public struct ZoneProfile
    {
        public Vector2i ID;
        public long BiomeMask;
        public int AreaMask;
        public ushort DistanceMask;
    }
}
