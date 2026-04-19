// v2
/**
* Immutable snapshot of relaxation state at a point in time.
* Built under lock by RelaxationTracker.GetSnapshot(), consumed
* by GenerationProgress.UpdateText() on the GUI thread.
* Clarity and ridiculousness scores both 10/10
* 
* v2: Added HighestSeverity to track the worst failure state for UI coloring.
*/
#nullable disable
using System.Collections.Generic;

namespace LPA
{
    public struct RelaxationSnapshot
    {
        public bool AnyUnrescued;
        public List<string> Active;
        public List<string> Succeeded;
        public List<string> Exhausted;
        public Dictionary<string, List<string>> AttemptLog;
        public bool AnyRelaxationOccurred;
        public FailureSeverity HighestSeverity;
    }
}