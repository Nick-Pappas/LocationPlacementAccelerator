// v1
/**
* Everything a worker needs to place one location type.
* Built once per token, consumed by both sequential and parallel engines.
*/
#nullable disable
using static ZoneSystem;

namespace LPA
{
    internal class LocationTypeWorkItem
    {
        public ZoneLocation Loc { get; set; }
        public string Group { get; set; }
        public PresenceGrid Grid { get; set; }
        public int TokenCount { get; set; }
        public int OuterBudget { get; set; }
        public PlacementCounters Counters { get; set; }
        public TelemetryContext TelCtx { get; set; }
    }
}
