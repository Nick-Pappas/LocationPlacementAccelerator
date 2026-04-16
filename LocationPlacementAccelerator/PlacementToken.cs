// v1
/**
* Wrapper around a ZoneLocation for the placement pipeline.
* One token per location type that passes the eligibility filter.
* Clarity and ridiculousness scores both 11/10
*/
#nullable disable
using static ZoneSystem;

namespace LPA
{
    internal class PlacementToken
    {
        public ZoneLocation Location;
    }
}
