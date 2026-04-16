// v1
/**
* Per-LT telemetry accumulator. Tracks biome failures, altitude misses,
* distance violations, and shadow counters. One instance per work item,
* merged across threads after placement completes.
*/
#nullable disable
using System.Collections.Generic;
namespace LPA
{
    public class TelemetryContext
    {
        public Dictionary<Heightmap.Biome, long> BiomeFailures { get; } = new Dictionary<Heightmap.Biome, long>();
        public Dictionary<Heightmap.BiomeArea, long> BiomeAreaFailures { get; } = new Dictionary<Heightmap.BiomeArea, long>();

        public Dictionary<Heightmap.Biome, long> AltitudeTooHigh { get; } = new Dictionary<Heightmap.Biome, long>();
        public Dictionary<Heightmap.Biome, long> AltitudeTooLow_Standard { get; } = new Dictionary<Heightmap.Biome, long>();
        public Dictionary<Heightmap.Biome, long> AltitudeTooLow_Anomalous { get; } = new Dictionary<Heightmap.Biome, long>();
        public Dictionary<Heightmap.Biome, long> AltitudeTooLow_Underwater { get; } = new Dictionary<Heightmap.Biome, long>();

        public Dictionary<Heightmap.Biome, AltitudeStat> AltHighStats { get; } = new Dictionary<Heightmap.Biome, AltitudeStat>();
        public Dictionary<Heightmap.Biome, AltitudeStat> AltLowStats_Standard { get; } = new Dictionary<Heightmap.Biome, AltitudeStat>();
        public Dictionary<Heightmap.Biome, AltitudeStat> AltLowStats_Anomalous { get; } = new Dictionary<Heightmap.Biome, AltitudeStat>();
        public Dictionary<Heightmap.Biome, AltitudeStat> AltLowStats_Underwater { get; } = new Dictionary<Heightmap.Biome, AltitudeStat>();

        public long DistanceTooClose { get; set; } = 0;
        public long DistanceTooFar { get; set; } = 0;

        public Dictionary<string, long> ShadowCounters { get; } = new Dictionary<string, long>();

        public void Merge(TelemetryContext otherP)
        {
            if (otherP == null)
            {
                return;
            }
            MergeDict(this.BiomeFailures, otherP.BiomeFailures);
            MergeDict(this.BiomeAreaFailures, otherP.BiomeAreaFailures);
            MergeDict(this.AltitudeTooHigh, otherP.AltitudeTooHigh);
            MergeDict(this.AltitudeTooLow_Standard, otherP.AltitudeTooLow_Standard);
            MergeDict(this.AltitudeTooLow_Anomalous, otherP.AltitudeTooLow_Anomalous);
            MergeDict(this.AltitudeTooLow_Underwater, otherP.AltitudeTooLow_Underwater);
            MergeStats(this.AltHighStats, otherP.AltHighStats);
            MergeStats(this.AltLowStats_Standard, otherP.AltLowStats_Standard);
            MergeStats(this.AltLowStats_Anomalous, otherP.AltLowStats_Anomalous);
            MergeStats(this.AltLowStats_Underwater, otherP.AltLowStats_Underwater);
            this.DistanceTooClose += otherP.DistanceTooClose;
            this.DistanceTooFar += otherP.DistanceTooFar;
            MergeDict(this.ShadowCounters, otherP.ShadowCounters);
        }

        private void MergeDict<T>(Dictionary<T, long> targetP, Dictionary<T, long> sourceP)
        {
            foreach (KeyValuePair<T, long> kvp in sourceP)
            {
                bool exists = targetP.TryGetValue(kvp.Key, out long existing);
                if (exists)
                {
                    targetP[kvp.Key] = existing + kvp.Value;
                }
                else
                {
                    targetP[kvp.Key] = kvp.Value;
                }
            }
        }

        private void MergeStats<T>(Dictionary<T, AltitudeStat> targetP, Dictionary<T, AltitudeStat> sourceP)
        {
            foreach (KeyValuePair<T, AltitudeStat> kvp in sourceP)
            {
                bool exists = targetP.TryGetValue(kvp.Key, out AltitudeStat targetStat);
                if (!exists)
                {
                    targetStat = new AltitudeStat();
                    targetP[kvp.Key] = targetStat;
                }

                AltitudeStat sourceStat = kvp.Value;

                if (sourceStat.Min < targetStat.Min)
                {
                    targetStat.Min = sourceStat.Min;
                }
                if (sourceStat.Max > targetStat.Max)
                {
                    targetStat.Max = sourceStat.Max;
                }
                targetStat.Sum += sourceStat.Sum;
                targetStat.Count += sourceStat.Count;
            }
        }
    }
}
