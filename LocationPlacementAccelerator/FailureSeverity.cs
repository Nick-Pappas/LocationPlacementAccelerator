// v1
/**
* Defines the severity of a location placement failure.
* Used by the progress overlay to color code the diagnostic output.
* 
* Red    = Prioritized + Unique (Necessity)
* Orange = Prioritized + Relaxable (Secondary Goal)
* Yellow = Non-prioritized (Unique or Relaxable)
* Green  = No tracked failure / Success
*/
#nullable disable
namespace LPA
{
    public enum FailureSeverity
    {
        Green,
        Yellow,
        Orange,
        Red
    }
}