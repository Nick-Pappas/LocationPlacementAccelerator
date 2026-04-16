// v1
/**
* Per-group flat ulong[] presence grid for replaced parallel placement engine.
*
* Two distinct structures live here:
*
*   PresenceGrid  : one per (group:radius) sub-grid (lazy-allocated).
*                   Tracks the exclusion footprint of placed group members so
*                   that HasConflict(p) (a single-bit read) tells the caller
*                   whether candidate p falls inside any placed member's circle.
*                   Heterogeneous groups (different minDistFromSimilar values)
*                   get one sub-grid per distinct radius. CommitToGroup in
*                   PlacementEngine rasterizes into all of them on each commit.
*
*   OccupancyGrid : one global instance (PresenceGrid.Occupancy).
*                   Prevents two concurrent workers from committing a placement
*                   to the same 16m cell regardless of location group.
*                   NOT used in the single-threaded path; kept for parallel prep.
*
* Cell size is configurable (default 16m, hard floor 4m as RAM prices are through the roof nowdays).
* Cached at Initialize(), never read from config in hot paths.
*
* Memory: ~(WorldRadius*2 / CellSize)^2 / 8 bytes per allocated sub-grid.
* At default 16m / 50k radius: ~4.88 MB per sub-grid. Lazy allocation, my favorite.
*
* Thread safety:
*   Commit: lock-free CAS per cell via Interlocked.CompareExchange(ref long,...).
*   Grid is stored as long[] internally; ulong bit logic applied via unchecked casts. The semantics are identical, no precision loss.
*   HasConflict(pos): single Volatile.Read; O(1).
*   HasConflict(pos,radius): circle scan; kept for Occupancy / debug use.
*   TrySet: retained for OccupancyGrid (single-cell CAS, parallel workers).
*   
*/
#nullable disable
using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace LPA
{
    internal sealed class PresenceGrid
    {
        public static float CellSize { get; private set; } = 16f;

        private static float _gridExtent;
        private static int _cellsPerAxis;
        private static int _arrayLength;

        private static readonly ConcurrentDictionary<string, PresenceGrid> _registry
            = new ConcurrentDictionary<string, PresenceGrid>(StringComparer.Ordinal);

        // Global fine-cell occupancy grid - prevents two workers placing at the same physical cell regardless of location group.
        public static readonly PresenceGrid Occupancy = new PresenceGrid();

        // Stored as long[] so Interlocked.CompareExchange(ref long,...) compiles cleanly
        private volatile long[] _grid;

        public static void Initialize(float cellSizeP = 0f)
        {
            float cs = ModConfig.PresenceGridCellSize.Value;
            if (cellSizeP > 0f)
            {
                cs = cellSizeP;
            }
            CellSize = Math.Max(4f, cs);
            _gridExtent = ModConfig.WorldRadius;
            _cellsPerAxis = (int)(_gridExtent * 2f / CellSize);
            int total = _cellsPerAxis * _cellsPerAxis;
            _arrayLength = (total + 63) / 64;

            ClearAll();
        }

        public static PresenceGrid GetOrCreate(string groupP)
        {
            return _registry.GetOrAdd(groupP, (string keyP) => new PresenceGrid());
        }

        // Atomically claims the single cell at worldPos.
        // Returns true --> this thread won the CAS. False --> already occupied.
        public bool TrySet(Vector3 worldPosP)
        {
            EnsureAllocated();
            WorldToCell(worldPosP, out int cx, out int cz);
            int bitIndex = cz * _cellsPerAxis + cx;
            int arrayIdx = bitIndex >> 6;
            ulong mask = 1UL << (bitIndex & 63);

            if (arrayIdx >= _arrayLength)
            {
                return false;
            }

            ulong current, updated;
            do
            {
                current = unchecked((ulong)Volatile.Read(ref _grid[arrayIdx]));
                if ((current & mask) != 0)
                {
                    return false;
                }
                updated = current | mask;
            }
            while (Interlocked.CompareExchange(
                       ref _grid[arrayIdx],
                       unchecked((long)updated),
                       unchecked((long)current)) != unchecked((long)current));

            return true;
        }

        // Returns true if any bit within 'radius' metres of worldPos is set.
        // Optimistic read - safe but may miss a simultaneous TrySet at the boundary.
        public bool HasConflict(Vector3 worldPosP, float radiusP)
        {
            if (_grid == null)
            {
                return false;
            }

            WorldToCell(worldPosP, out int cx, out int cz);
            int rCells = (int)Math.Ceiling(radiusP / CellSize);

            for (int dz = -rCells; dz <= rCells; dz++)
            {
                int rowZ = cz + dz;
                if (rowZ < 0 || rowZ >= _cellsPerAxis)
                {
                    continue;
                }

                int halfSpan = (int)Math.Sqrt((double)(rCells * rCells - dz * dz));
                int colMin = Math.Max(0, cx - halfSpan);
                int colMax = Math.Min(_cellsPerAxis - 1, cx + halfSpan);

                int bitStart = rowZ * _cellsPerAxis + colMin;
                int bitEnd = rowZ * _cellsPerAxis + colMax;
                int idxStart = bitStart >> 6;
                int idxEnd = bitEnd >> 6;

                for (int idx = idxStart; idx <= idxEnd; idx++)
                {
                    ulong word = unchecked((ulong)Volatile.Read(ref _grid[idx]));
                    if (word == 0UL)
                    {
                        continue;
                    }

                    int lo = 0;
                    if (idx == idxStart)
                    {
                        lo = bitStart & 63;
                    }
                    int hi = 63;
                    if (idx == idxEnd)
                    {
                        hi = bitEnd & 63;
                    }

                    if ((word & RangeMask(lo, hi)) != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /**
        * Rasterizes a filled circle of 'radius' metres around worldPos into this grid.
        * Every cell whose centre falls within the circle is atomically set via CAS.
        * Called at placement commit time; the circle IS the exclusion footprint that
        * future HasConflict(p) single-bit reads will test against.
        */
        public void Commit(Vector3 worldPosP, float radiusP)
        {
            EnsureAllocated();
            WorldToCell(worldPosP, out int cx, out int cz);
            int rCells = (int)Math.Ceiling(radiusP / CellSize);

            for (int dz = -rCells; dz <= rCells; dz++)
            {
                int rowZ = cz + dz;
                if (rowZ < 0 || rowZ >= _cellsPerAxis)
                {
                    continue;
                }

                int halfSpan = (int)Math.Sqrt((double)(rCells * rCells - dz * dz));
                int colMin = Math.Max(0, cx - halfSpan);
                int colMax = Math.Min(_cellsPerAxis - 1, cx + halfSpan);

                for (int col = colMin; col <= colMax; col++)
                {
                    int bitIndex = rowZ * _cellsPerAxis + col;
                    int arrayIdx = bitIndex >> 6;
                    if (arrayIdx >= _arrayLength)
                    {
                        continue;
                    }
                    ulong mask = 1UL << (bitIndex & 63);

                    ulong current, updated;
                    do
                    {
                        current = unchecked((ulong)Volatile.Read(ref _grid[arrayIdx]));
                        if ((current & mask) != 0)
                        {
                            break;
                        }
                        updated = current | mask;
                    }
                    while (Interlocked.CompareExchange(
                               ref _grid[arrayIdx],
                               unchecked((long)updated),
                               unchecked((long)current)) != unchecked((long)current));
                }
            }
        }

        /**
        * O(1) conflict check - reads the single bit at worldPos.
        * Returns true if that cell was previously rasterized by a Commit call,
        * meaning a placed group member's exclusion circle covers this point.
        * This is the hot-path overload used in EvaluateZone.
        */
        public bool HasConflict(Vector3 worldPosP)
        {
            if (_grid == null)
            {
                return false;
            }
            WorldToCell(worldPosP, out int cx, out int cz);
            int bitIndex = cz * _cellsPerAxis + cx;
            int arrayIdx = bitIndex >> 6;
            if (arrayIdx >= _arrayLength)
            {
                return false;
            }
            ulong word = unchecked((ulong)Volatile.Read(ref _grid[arrayIdx]));
            ulong mask = 1UL << (bitIndex & 63);
            return (word & mask) != 0;
        }

        public void Clear()
        {
            long[] g = _grid;
            if (g != null)
            {
                Array.Clear(g, 0, g.Length);
            }
        }

        public static void ClearAll()
        {
            foreach (PresenceGrid pg in _registry.Values)
            {
                pg.Clear();
            }
            _registry.Clear();
            Occupancy._grid = new long[_arrayLength];
        }

        private void EnsureAllocated()
        {
            if (_grid != null)
            {
                return;
            }
            Interlocked.CompareExchange(ref _grid, new long[_arrayLength], null);
        }

        private static void WorldToCell(Vector3 pP, out int cx, out int cz)
        {
            cx = (int)((pP.x + _gridExtent) / CellSize);
            cz = (int)((pP.z + _gridExtent) / CellSize);

            if (cx < 0)
            {
                cx = 0;
            }
            else if (cx >= _cellsPerAxis)
            {
                cx = _cellsPerAxis - 1;
            }

            if (cz < 0)
            {
                cz = 0;
            }
            else if (cz >= _cellsPerAxis)
            {
                cz = _cellsPerAxis - 1;
            }
        }

        /**
        * Closed-form bitmask with bits [lo..hi] inclusive set.
        *   hi==63  -->  ~0UL << lo               (avoids UB: 1UL<<64 is undefined)
        *   else    -->  ((1UL << (hi-lo+1)) - 1) << lo
        */
        private static ulong RangeMask(int loP, int hiP)
        {
            if (hiP == 63)
            {
                return ~0UL << loP;
            }
            return (((1UL << (hiP - loP + 1)) - 1UL) << loP); //maybe I should rewrite this return, kind of ridiculously dense if I see it again in 2028, or basically in a week. 
        }
    }
}
