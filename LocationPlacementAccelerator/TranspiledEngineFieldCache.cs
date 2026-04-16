// v1
/**
* Reflection field cache for the transpiled engine's IL-scanned coroutine fields.
* Populated by TranspiledEnginePatches during transpiler execution.
* Read by TranspiledStateExtractor, TelemetryHelpers, and TranspiledCompletionHandler.
*
* This is Transpiled infrastructure only. The replaced engine never touches it.
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using static ZoneSystem;

namespace LPA
{
    public static class TranspiledEngineFieldCache
    {
        public static Dictionary<Type, FieldInfo> LocationFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> LimitFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> CounterFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> InnerCounterFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> PlacedFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<Type, FieldInfo> ZoneIDFields = new Dictionary<Type, FieldInfo>();
        public static Dictionary<string, FieldInfo> ErrorFields = new Dictionary<string, FieldInfo>();
        public static FieldInfo IterationsPkgField = null;

        public static void SetCurrentLocation(ZoneLocation locationP)
        {
            if (GenerationProgress.CurrentLocation != locationP)
            {
                GenerationProgress.CurrentLocation = locationP;
            }
        }

        public static ZoneLocation GetLocation(object instanceP)
        {
            Type type = instanceP.GetType();
            bool found = LocationFields.TryGetValue(type, out FieldInfo field);
            if (found)
            {
                return field.GetValue(instanceP) as ZoneLocation;
            }
            return null;
        }

        public static long GetVal(object instanceP, string fieldNameP)
        {
            int instanceHash = instanceP.GetHashCode();
            bool hasSession = TranspiledCompletionHandler.ActiveSessions.TryGetValue(instanceHash, out TelemetryContext context);
            if (hasSession)
            {
                bool hasCounter = context.ShadowCounters.TryGetValue(fieldNameP, out long count);
                if (hasCounter)
                {
                    return count;
                }
            }

            bool hasField = TranspiledEngineFieldCache.ErrorFields.TryGetValue(fieldNameP, out FieldInfo field);
            if (hasField)
            {
                try
                {
                    return Convert.ToInt64(field.GetValue(instanceP));
                }
                catch { }
            }
            return 0;
        }
    }
}
