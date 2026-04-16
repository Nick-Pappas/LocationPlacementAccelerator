// v1

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
        public int ErrTerrain;
        public int ErrForest;
    }
}
