// v1
/**
* Facade over the active BucketingStrategy.
* All survey and candidate-list access from the placement engines
* goes through here so the strategy implementation can be swapped
* without touching callers.
*/
#nullable disable
using System;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class SurveyMode
    {
        private static BucketingStrategy _activeStrategy;
        private static bool _initialized = false;
        public static bool SurveyExhausted = false;
        public static int CurrentActiveZoneIndex = -1;

        public static bool GetZone(ZoneLocation locationP, out Vector2i result)
        {
            return _activeStrategy.GetZone(locationP, out result);
        }

        public static System.Collections.Generic.List<Vector2i> GetOrBuildCandidateList(ZoneLocation locationP)
        {
            return _activeStrategy.GetOrBuildCandidateList(locationP);
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _activeStrategy = new LocationTypeBucketingStrategy();
            _activeStrategy.Initialize();
            _initialized = true;
        }

        public static void DumpDiagnostics()
        {
            _activeStrategy?.DumpDiagnostics();
        }

        public static void ClearCache(string prefabNameP)
        {
            _activeStrategy?.ClearCache(prefabNameP);
            SurveyExhausted = false;
        }

        public static void Reset()
        {
            _activeStrategy = null;
            _initialized = false;
            SurveyExhausted = false;
            CurrentActiveZoneIndex = -1;
        }

        public static void NotifyZonePlaced()
        {
            if (!_initialized || CurrentActiveZoneIndex < 0)
            {
                return;
            }
            _activeStrategy.MarkZoneOccupied(CurrentActiveZoneIndex);
        }

        public static void MarkZoneOccupied(int zoneIndexP)
        {
            if (!_initialized || _activeStrategy == null)
            {
                return;
            }
            _activeStrategy.MarkZoneOccupied(zoneIndexP);
        }
    }
}
