// v1
/**
* Simple min/max/sum/count accumulator for altitude telemetry.
* Made way more sense when I had the biome with learning on, but I still need it for the relaxation perhaps.
* Also, good to know what we have encountered in general, especially if better continents is involved. 
*/
#nullable disable
namespace LPA
{
    public class AltitudeStat
    {
        public float Min = float.MaxValue;
        public float Max = float.MinValue;
        public double Sum = 0;
        public long Count = 0;

        public void Add(float valueP)
        {
            if (valueP < Min)
            {
                Min = valueP;
            }
            if (valueP > Max)
            {
                Max = valueP;
            }
            Sum += valueP;
            Count++;
        }

        public string GetString()
        {
            if (Count == 0)
            {
                return "";
            }
            return $"[Observed: Min {Min:F1}m, Avg {(Sum / Count):F1}m, Max {Max:F1}m]";
        }
    }
}
