// v1
/**
* Spatial partitioning for the replaced parallel placement engine.
*
* So the problem:
*   Multiple worker threads place locations simultaneously. Two workers
*   placing locations from the same similarity group could violate the
*   minimum distance constraint if they place in adjacent zones. I need
*   to guarantee that workers operating on different spatial regions can
*   NEVER produce placements closer than minDistanceFromSimilar.
*
* The solution (for now):
*   I tile the world into square blocks of K×K zones, then assign each
*   block a "color" using modular arithmetic (think checkerboard, but
*   with C colors per axis instead of 2). Workers process one color
*   at a time. Two blocks of different colors on the same axis are
*   separated by at least (C-1)*K zone widths = (C-1)*K*64 metres.
*   BuildRule enforces K >= ceil(minDist / (64*(C-1))), which guarantees
*   that gap is always >= minDist. No additional safety margin needed.
*
*   C (colors per axis) = max(2, floor(sqrt(maxPartitions))).
*   Total partitions = C². For 10 threads --> C=3 --> 9 partitions.
*   C is NOT snapped to power of 2 - this allows C=3, C=5, C=6, etc.
*
* There are two tiers of arithmetic:
*   Tier 1 (BitShift): When K AND C are both powers of 2, the division
*     and modulo collapse into bit shifts and masks. No division operations.
*   Tier 2 (Modulo): General case for any K and C. Uses FloorDiv/FloorMod
*     which handle negative zone coordinates correctly 
*     (C# truncates toward zero, I need floor-toward-negative-infinity).
*     
* Note to self: Even though bitshifting is faster than modulo, I found that
* I was ending up with load balancing issues. Paradoxically modulo was faster
* in some cases. It was a nightmare to debug what was going on. I can revisit.
* 
* GetPartition is called once per zone during BuildSpatialStreams, so it
* is on a warm path (not the hottest inner loop, but called ~50k times
* per GTS group). The AggressiveInlining hints eliminate call overhead
* for the arithmetic helpers.
* 
* TODO: I should add my wedge partitioning algorithm here to compare. 
* But it may be more work to debug, to end up making 4.6s to 4.3s...
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LPA
{
    internal enum PartitionMode : byte
    {
        Single,
        BitShift,
        Modulo
    }

    internal struct PartitionRule
    {
        public PartitionMode Mode;
        public int PartitionCount;
        public int BlockSize;
        public int BlockSizeLog2;
        public int ColorsPerAxis;
        public int ColorBits;
        public int ColorMask;
    }

    internal static class SpatialPartitionAlgorithms
    {
        public static PartitionRule BuildRule(float minDistP, int maxPartitionsP)
        {
            if (minDistP <= 0f || maxPartitionsP <= 1)
            {
                return new PartitionRule { Mode = PartitionMode.Single, PartitionCount = 1 };
            }

            int colorsPerAxis = (int)Math.Sqrt(maxPartitionsP);
            if (colorsPerAxis < 2)
            {
                colorsPerAxis = 2;
            }

            /**
            * The safety invariant: two different-color blocks on the same axis are separated by (colorsPerAxis-1)*blockSize zone widths.
            * Each zone is 64m, so the minimum gap = (C-1)*K*64 metres.
            * I need that gap >= minDist, therefore K >= minDist / (64*(C-1)).
            */
            int blockSize = (int)Math.Ceiling(minDistP / (64.0 * (colorsPerAxis - 1)));
            if (blockSize < 1)
            {
                blockSize = 1;
            }

            /**
            * A number is a power of 2 if and only if it has exactly one bit set.
            * For any positive integer n, (n & (n-1)) clears the lowest set bit.
            * If the result is zero, only one bit was set, so n is a power of 2.
            */
            bool colorsIsPow2 = (colorsPerAxis & (colorsPerAxis - 1)) == 0;
            bool blockSizeIsPow2 = (blockSize & (blockSize - 1)) == 0;

            if (colorsIsPow2 && blockSizeIsPow2)
            {
                int colorBits = FindExponent(colorsPerAxis);
                return new PartitionRule
                {
                    Mode = PartitionMode.BitShift,
                    PartitionCount = colorsPerAxis * colorsPerAxis,
                    BlockSize = blockSize,
                    BlockSizeLog2 = FindExponent(blockSize),
                    ColorsPerAxis = colorsPerAxis,
                    ColorBits = colorBits,
                    ColorMask = colorsPerAxis - 1
                };
            }

            return new PartitionRule
            {
                Mode = PartitionMode.Modulo,
                PartitionCount = colorsPerAxis * colorsPerAxis,
                BlockSize = blockSize,
                ColorsPerAxis = colorsPerAxis
            };
        }

        /**
        * Finds the binary logarithm of a positive power-of-2 integer.
        * For example: 8 (binary 1000) returns 3, because 2^3 = 8.
        *
        * The method works by repeatedly right-shifting the value by 1
        * (equivalent to integer division by 2) and counting how many
        * shifts it takes to reach 1. Each shift moves the single set
        * bit one position to the right, so the count equals the bit
        * position of that bit - which is the exponent.
        *
        * Only called during BuildRule (once per GT at startup),
        * never in a hot path. Inlined because the JIT can then fold
        * the result into the caller's register allocation.
        */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindExponent(int valueP)
        {
            int exponent = 0;
            while (valueP > 1)
            {
                valueP >>= 1;
                exponent++;
            }
            return exponent;
        }

        /**
        * Maps a zone coordinate to its partition index. O(1) arithmetic.
        *
        * Called once per zone during BuildSpatialStreams (~50k zones per GT). Inlined to eliminate call overhead on that warm path.
        *
        * BitShift tier:
        *   Divide zone coordinate by block size via right-shift (since both are powers of 2), 
        *   then extract the color index via bitmask.
        *   Combine X and Z color indices into a single flat partition index.
        *   Example with C=2, K=4: zone (9,3) --> block (2,0) --> color (0,0) --> partition 0.
        *
        * Modulo tier:
        *   Same logic but uses FloorDiv and FloorMod for arbitrary K and C.
        */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPartition(Vector2i zoneP, ref PartitionRule ruleP)
        {
            switch (ruleP.Mode)
            {
                case PartitionMode.BitShift:
                {
                    int colorX = (zoneP.x >> ruleP.BlockSizeLog2) & ruleP.ColorMask;
                    int colorZ = (zoneP.y >> ruleP.BlockSizeLog2) & ruleP.ColorMask;
                    return colorX | (colorZ << ruleP.ColorBits);
                }

                case PartitionMode.Modulo:
                {
                    int colorX = FloorMod(FloorDiv(zoneP.x, ruleP.BlockSize), ruleP.ColorsPerAxis);
                    int colorZ = FloorMod(FloorDiv(zoneP.y, ruleP.BlockSize), ruleP.ColorsPerAxis);
                    return colorX * ruleP.ColorsPerAxis + colorZ;
                }

                default:
                    return 0;
            }
        }

        /**
        * Integer division that rounds toward negative infinity instead of
        * toward zero. C# truncates toward zero by default:
        *   -7 / 3 = -2  (C# default, truncates toward zero)
        *   -7 / 3 = -3  (what I need, floor toward negative infinity)
        *
        * Without this, negative zone coordinates would map to the wrong
        * block, breaking the color tiling across the world origin.
        *
        * The implementation: start with C#'s truncated division, then
        * subtract 1 if there was a remainder AND the operands had
        * different signs (XOR of the sign bits is negative).
        */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FloorDiv(int dividendP, int divisorP)
        {
            int truncated = dividendP / divisorP;
            int remainder = dividendP % divisorP;
            bool hasRemainder = remainder != 0;
            bool signsDisagree = (dividendP ^ divisorP) < 0;

            if (hasRemainder && signsDisagree)
            {
                return truncated - 1;
            }
            return truncated;
        }

        /**
        * Non-negative modulo that wraps into [0, divisor) instead of
        * preserving the sign of the dividend. C#'s % operator preserves
        * the dividend's sign:
        *   -7 % 3 = -1  (C# default)
        *   -7 mod 3 = 2 (what I need)
        *
        * Without this, negative zone coordinates would produce negative
        * color indices, which would index outside the partition array.
        * The fix: if the remainder is negative, add the divisor to wrap
        * it into the positive range.
        */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FloorMod(int dividendP, int divisorP)
        {
            int remainder = dividendP % divisorP;
            if (remainder < 0)
            {
                return remainder + divisorP;
            }
            return remainder;
        }
    

        // Named structs instead of tuples so the sort comparators and loop bodies have readable field names.
        private struct ZoneAngle
        {
            public Vector2i Zone;
            public float Angle;
        }

        private struct AngularGap
        {
            public int AfterIndex;
            public float GapSize;
        }

        private static int CompareByAngle(ZoneAngle aP, ZoneAngle bP)
        {
            if (aP.Angle < bP.Angle)
            {
                return -1;
            }
            if (aP.Angle > bP.Angle)
            {
                return 1;
            }
            return 0;
        }

        private static int CompareByGapSizeDescending(AngularGap aP, AngularGap bP)
        {
            if (bP.GapSize < aP.GapSize)
            {
                return -1;
            }
            if (bP.GapSize > aP.GapSize)
            {
                return 1;
            }
            return 0;
        }
    }
}
