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

        /**
        * maxDistanceFromSimilar rejections - the dart was legal everywhere else but had no
        * member of its groupMax within range. Kept apart from ErrSim because the two are
        * opposite failures: ErrSim means something similar was too close, ErrNotSim means
        * nothing similar was close enough. ReportData/ReportFormatter/ConstraintRelaxer were
        * already carrying ErrNotSim before the engine ever produced one, so nothing downstream
        * needed changing here.
        */
        public int ErrNotSim;
        public int ErrTerrain;
        public int ErrForest;
    }
}