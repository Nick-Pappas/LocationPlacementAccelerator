// v1
/**
* Accumulator for per-location-type placement statistics.
* One instance per LTS, populated during placement, merged across
* threads, then consumed by ReportFormatter for the diagnostic log.
*/
#nullable disable
using static ZoneSystem;

namespace LPA
{
    public class ReportData
    {
        public ZoneLocation Loc;
        public object Instance;
        public int InstanceHash;
        public string PrefabName;

        public long CurrentOuter;
        public long LimitOuter;
        public int Placed;
        public int OriginalQuantity;
        public bool IsComplete;

        public long ErrZone;
        public long ErrArea;
        public long ValidZones;

        public long InDist;
        public long InBiome;
        public long InAlt;
        public long InForest;
        public long InTerr;
        public long InSim;
        public long InVeg;

        public long ErrDist;
        public long ErrBiome;
        public long ErrAlt;
        public long ErrForest;
        public long ErrTerrain;
        public long ErrSim;
        public long ErrNotSim;
        public long ErrVeg;

        public void Merge(ReportData otherP)
        {
            if (otherP == null)
            {
                return;
            }
            CurrentOuter += otherP.CurrentOuter;
            LimitOuter += otherP.LimitOuter;
            ErrZone += otherP.ErrZone;
            ErrArea += otherP.ErrArea;
            ValidZones += otherP.ValidZones;
            InDist += otherP.InDist;
            InBiome += otherP.InBiome;
            InAlt += otherP.InAlt;
            InForest += otherP.InForest;
            InTerr += otherP.InTerr;
            InSim += otherP.InSim;
            InVeg += otherP.InVeg;
            ErrDist += otherP.ErrDist;
            ErrBiome += otherP.ErrBiome;
            ErrAlt += otherP.ErrAlt;
            ErrForest += otherP.ErrForest;
            ErrTerrain += otherP.ErrTerrain;
            ErrSim += otherP.ErrSim;
            ErrNotSim += otherP.ErrNotSim;
            ErrVeg += otherP.ErrVeg;
        }
    }
}
