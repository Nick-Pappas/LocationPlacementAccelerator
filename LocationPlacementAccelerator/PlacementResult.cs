// v1
/**
* Outcome of a single successful location placement.
* Carries everything needed to commit the result to the world.
* Clarity and ridiculousness scores both 10/10
*/
#nullable disable
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    internal struct PlacementResult
    {
        public ZoneLocation Loc;
        public Vector3 Position;
        public string Group;
        public int ZoneIdx;
        public Vector2i ZoneID;
        public PlacementCounters Counters;
    }
}
