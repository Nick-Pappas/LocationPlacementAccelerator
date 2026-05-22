// v0.0.1c
/**
* Public API: per-call behavioral overrides for LPA.API.RunCustomPlacement.
*
* Every field is nullable. A null field means "inherit ModConfig". A set
* field overrides the corresponding ModConfig entry for this call only,
* without mutating ModConfig itself.
*
* Field semantics mirror their ModConfig counterparts. Defaults shown
* are the suggested behavior for callers that want "fast and reasonable":
*   Parallel = true, Interleaved = false, MaxRelaxationAttempts = 4.
*
* CallerTag is informational - drives the diagnostic log filename pattern
* once that plumbing lands. Today it is captured but not yet routed.
*/
#nullable disable

namespace LPA
{
    public class LpaApiOptions
    {
        public bool? Parallel;
        public bool? Interleaved;
        public int? MaxRelaxationAttempts;
        public float? OuterMultiplier;
        public float? InnerMultiplier;
        public float? PresenceGridCellSize;
        public bool? Enable3DSimilarityCheck;
        public bool? MinimalLogging;
        public bool? LogSuccesses;

        public string CallerTag;
    }
}
