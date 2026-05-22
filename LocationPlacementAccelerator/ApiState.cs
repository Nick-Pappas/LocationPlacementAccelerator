// v0.0.1c
/**
* Per-call state for LPA.API.RunCustomPlacement. Owned by API.cs:
* set at call entry, cleared in the finally block. Read by Run() and its
* downstream collaborators (CenterFirstPlacer, LocationTypeBucketingStrategy,
* the placement engine partial files).
*
* Kept as a separate type so the placement engine's partial class graph
* does not accumulate cross-cutting public fields - the API is a thin
* layer on top of the engine, not part of it.
*/
#nullable disable
using System.Collections.Generic;
using UnityEngine;

namespace LPA
{
    internal static class ApiState
    {
        public static bool IsApiRun;
        public static List<PlacementRequest> Requests;
        public static HashSet<Vector2i> AllowedZones;
        public static LpaApiOptions Options;
        public static List<ZoneSystem.ZoneLocation> WorkList;
    }
}
