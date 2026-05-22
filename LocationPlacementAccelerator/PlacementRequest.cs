// v0.0.1c
/**
* Public API: one element of the request list passed to LPA.API.RunCustomPlacement.
*
* Location is a ZoneLocation variant (same kind that lives in ZoneSystem.m_locations).
* TargetQuantity is the caller's authoritative count for THIS request - it
* completely supersedes Location.m_quantity for this run. Callers that want
* additive semantics (e.g. UW locations_add) pre-compute the delta themselves;
* callers that want absolute semantics (e.g. UW world_reset) pass the absolute
* count. LPA does not consult m_locationInstances to adjust this number.
*/
#nullable disable

namespace LPA
{
    public struct PlacementRequest
    {
        public ZoneSystem.ZoneLocation Location;
        public int TargetQuantity;
    }
}
