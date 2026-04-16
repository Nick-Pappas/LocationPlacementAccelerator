// v1
/**
* Reads the transpiled engine's coroutine state machine fields via
* reflection and assembles a ReportData snapshot. The waterfall pass
* counts (InDist --> InBiome --> InAlt --> ... --> Placed) are reconstructed
* from error counters so the report shows the placement funnel.
*/
#nullable disable
using System;
using System.Collections.Generic;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class TranspiledStateExtractor
    {
        public static ReportData Analyze(object instanceP, int overridePlacedP = -1)
        {
            ZoneLocation loc = TranspiledEngineFieldCache.GetLocation(instanceP);
            if (loc == null)
            {
                return null;
            }

            Type instType = instanceP.GetType();
            long rawOuter = Convert.ToInt64(TranspiledEngineFieldCache.CounterFields[instType].GetValue(instanceP));
            long limitOuter = Convert.ToInt64(TranspiledEngineFieldCache.LimitFields[instType].GetValue(instanceP));

            int instKey = instanceP.GetHashCode();
            bool hasSavedActual = TranspiledEnginePatches.ActualIterationsByInstance.TryGetValue(instKey, out int savedActual);
            if (rawOuter >= limitOuter && hasSavedActual)
            {
                rawOuter = savedActual;
                TranspiledEnginePatches.ActualIterationsByInstance.Remove(instKey);
            }

            int placedCount = (int)TranspiledEngineFieldCache.PlacedFields[instType].GetValue(instanceP);
            if (overridePlacedP > -1)
            {
                placedCount = overridePlacedP;
            }

            ReportData data = new ReportData
            {
                Loc = loc,
                Instance = instanceP,
                InstanceHash = instKey,
                PrefabName = loc.m_prefabName,
                CurrentOuter = rawOuter,
                LimitOuter = limitOuter,
                Placed = placedCount,
                OriginalQuantity = loc.m_quantity
            };

            data.IsComplete = data.Placed >= loc.m_quantity;

            data.ErrZone = TranspiledEngineFieldCache.GetVal(instanceP, "errorLocationInZone");
            data.ErrArea = TranspiledEngineFieldCache.GetVal(instanceP, "errorBiomeArea");
            data.ErrDist = TranspiledEngineFieldCache.GetVal(instanceP, "errorCenterDistance");
            data.ErrBiome = TranspiledEngineFieldCache.GetVal(instanceP, "errorBiome");
            data.ErrAlt = TranspiledEngineFieldCache.GetVal(instanceP, "errorAlt");
            data.ErrForest = TranspiledEngineFieldCache.GetVal(instanceP, "errorForest");
            data.ErrTerrain = TranspiledEngineFieldCache.GetVal(instanceP, "errorTerrainDelta");
            data.ErrSim = TranspiledEngineFieldCache.GetVal(instanceP, "errorSimilar");
            data.ErrNotSim = TranspiledEngineFieldCache.GetVal(instanceP, "errorNotSimilar");
            data.ErrVeg = TranspiledEngineFieldCache.GetVal(instanceP, "errorVegetation");

            // Reconstruct the placement funnel: each stage = survivors of previous + its own errors.
            long currentPassed = data.Placed;

            data.InVeg = currentPassed + data.ErrVeg;
            currentPassed = data.InVeg;

            data.InTerr = currentPassed + data.ErrTerrain;
            currentPassed = data.InTerr;
            data.InSim = currentPassed + data.ErrSim + data.ErrNotSim;
            currentPassed = data.InSim;

            data.InForest = currentPassed;
            if (loc.m_inForest)
            {
                data.InForest = currentPassed + data.ErrForest;
                currentPassed = data.InForest;
            }

            data.InAlt = currentPassed + data.ErrAlt;
            currentPassed = data.InAlt;

            data.InBiome = currentPassed + data.ErrBiome;
            currentPassed = data.InBiome;

            data.InDist = currentPassed + data.ErrDist;

            data.ValidZones = data.CurrentOuter - data.ErrZone - data.ErrArea;

            return data;
        }
    }
}
