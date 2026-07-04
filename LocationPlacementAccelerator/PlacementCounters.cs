// v2

// Keeping track of a single location type's placement attempts.
#nullable disable
namespace LPA
{
    internal class PlacementCounters
    {
        public int ZonesExamined;
        public int ZoneExhausted;
        public int DartsThrown;
        public int Placed;
        public int ErrOccupied;
        public int ErrDist;
        public int ErrBiome;
        public int ErrAlt;
        public int ErrSim;
        // v2: maxDistanceFromSimilar / anchor (groupMax) inclusion misses. The replaced engine never counted
        // these!!! wtf... how is it that nobody reported it.
        // it only ever ran the min exclusion check. The transpiled engine already reports them (errorNotSimilar).
        public int ErrNotSim;
        public int ErrTerrain;
        public int ErrForest;
    }
}