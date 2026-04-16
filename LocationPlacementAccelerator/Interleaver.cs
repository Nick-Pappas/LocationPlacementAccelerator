// v1
/**
* Splits each location type's total quantity into individual work packets
* and interleaves them round-robin across similarity groups. This prevents
* a single high-quantity type from monopolizing spatial territory before
* competing types get a chance.
*
* When interleaving is OFF, this still acts as the authoritative source for
* PendingPackets and OriginalLocations, and handles budget calculation.
*/
#nullable disable
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class Interleaver
    {
        private static Dictionary<ZoneLocation, int> _budgets = new Dictionary<ZoneLocation, int>();
        public static List<ZoneLocation> OriginalLocations { get; private set; } = null;
        public static bool IsGenerating = false;

        public static Dictionary<string, int> PendingPackets = new Dictionary<string, int>();
        public static HashSet<string> LoggedStarts = new HashSet<string>();

        public static void ClearLoggedStart(string prefabNameP)
        {
            LoggedStarts.Remove(prefabNameP);
        }

        public static bool TryLogStart(string prefabNameP)
        {
            return LoggedStarts.Add(prefabNameP);
        }

        public static int GetOriginalQuantity(string prefabNameP)
        {
            if (OriginalLocations != null)
            {
                for (int i = 0; i < OriginalLocations.Count; i++)
                {
                    if (OriginalLocations[i].m_prefabName == prefabNameP)
                    {
                        return OriginalLocations[i].m_quantity;
                    }
                }
            }
            else if (ZoneSystem.instance != null)
            {
                for (int i = 0; i < ZoneSystem.instance.m_locations.Count; i++)
                {
                    if (ZoneSystem.instance.m_locations[i].m_prefabName == prefabNameP)
                    {
                        return ZoneSystem.instance.m_locations[i].m_quantity;
                    }
                }
            }
            return 1;
        }

        public static void InterleaveLocations(ZoneSystem zsP)
        {
            if (OriginalLocations != null)
            {
                return;
            }

            IsGenerating = true;
            _budgets.Clear();
            PendingPackets.Clear();
            LoggedStarts.Clear();
            OriginalLocations = new List<ZoneLocation>(zsP.m_locations);

            if (!ModConfig.EnableInterleavedScheduling.Value)
            {
                foreach (ZoneLocation loc in OriginalLocations)
                {
                    if (loc.m_enable && loc.m_quantity > 0)
                    {
                        PendingPackets[loc.m_prefabName] = loc.m_quantity;
                    }
                }
                DiagnosticLog.WriteTimestampedLog($"[Dispatcher] Interleaved Scheduling is OFF. Retaining {OriginalLocations.Count} locations sequential.");
                return;
            }

            List<ZoneLocation> prio = new List<ZoneLocation>();
            List<ZoneLocation> nonPrio = new List<ZoneLocation>();
            for (int i = 0; i < OriginalLocations.Count; i++)
            {
                ZoneLocation loc = OriginalLocations[i];
                if (!loc.m_enable || loc.m_quantity <= 0)
                {
                    continue;
                }
                if (loc.m_prioritized)
                {
                    prio.Add(loc);
                }
                else
                {
                    nonPrio.Add(loc);
                }
            }

            List<ZoneLocation> newLocations = new List<ZoneLocation>();
            newLocations.AddRange(ProcessTier(prio, 200000));//the vanilla is 200k
            newLocations.AddRange(ProcessTier(nonPrio, 100000));//the vanilla is 100k

            zsP.m_locations = newLocations;
            DiagnosticLog.WriteTimestampedLog($"[Dispatcher] Interleaved {OriginalLocations.Count} prefabs into {newLocations.Count} round-robin packets.");
        }

        private static List<ZoneLocation> ProcessTier(List<ZoneLocation> tierP, int baseBudgetP)
        {
            List<ZoneLocation> result = new List<ZoneLocation>();
            Dictionary<string, Queue<ZoneLocation>> queues = new Dictionary<string, Queue<ZoneLocation>>();
            ZoneSystem zs = ZoneSystem.instance;

            float mult = ModConfig.OuterMultiplier.Value;
            int actualBaseBudget = Mathf.Max(1, Mathf.RoundToInt(baseBudgetP * mult));

            foreach (ZoneLocation loc in tierP)
            {
                if (loc.m_centerFirst || loc.m_quantity <= 1)
                {
                    ZoneLocation clone = CloneLocation(loc);
                    _budgets[clone] = actualBaseBudget;
                    Enqueue(queues, clone);
                    PendingPackets[loc.m_prefabName] = 1;
                    continue;
                }

                int alreadyPlaced = 0;
                if (zs != null)
                {
                    foreach (LocationInstance inst in zs.m_locationInstances.Values)
                    {
                        if (inst.m_location.m_prefabName == loc.m_prefabName)
                        {
                            alreadyPlaced++;
                        }
                    }
                }

                int totalQty = loc.m_quantity - alreadyPlaced;
                if (totalQty <= 0)
                {
                    continue;
                }

                int basePerChunk = actualBaseBudget / totalQty;
                int remainder = actualBaseBudget % totalQty;

                PendingPackets[loc.m_prefabName] = totalQty;

                for (int i = 0; i < totalQty; i++)
                {
                    ZoneLocation clone = CloneLocation(loc);
                    clone.m_quantity = 1;
                    int extra = 0;
                    if (i < remainder)
                    {
                        extra = 1;
                    }
                    int budget = basePerChunk + extra;
                    _budgets[clone] = Mathf.Max(1, budget);
                    Enqueue(queues, clone);
                }
            }

            // Flatten all per-prefab queues into per-group queues, then round-robin
            // across groups so that competing types get interleaved placement slots.
            Dictionary<string, List<ZoneLocation>> groupBuckets = new Dictionary<string, List<ZoneLocation>>();
            foreach (KeyValuePair<string, Queue<ZoneLocation>> kvp in queues)
            {
                foreach (ZoneLocation loc in kvp.Value)
                {
                    string groupKey = loc.m_prefabName;
                    if (!string.IsNullOrEmpty(loc.m_group))
                    {
                        groupKey = loc.m_group;
                    }
                    bool hasGroup = groupBuckets.TryGetValue(groupKey, out List<ZoneLocation> groupList);
                    if (!hasGroup)
                    {
                        groupList = new List<ZoneLocation>();
                        groupBuckets[groupKey] = groupList;
                    }
                    groupList.Add(loc);
                }
            }

            List<Queue<ZoneLocation>> groupQueues = new List<Queue<ZoneLocation>>();
            foreach (KeyValuePair<string, List<ZoneLocation>> kvp in groupBuckets)
            {
                groupQueues.Add(new Queue<ZoneLocation>(kvp.Value));
            }

            bool added = true;
            while (added)
            {
                added = false;
                for (int i = 0; i < groupQueues.Count; i++)
                {
                    if (groupQueues[i].Count > 0)
                    {
                        result.Add(groupQueues[i].Dequeue());
                        added = true;
                    }
                }
            }

            return result;
        }

        public static List<ZoneLocation> CreateRelaxedPackets(ZoneLocation relaxedLocP, int quantityToPlaceP, int fallbackBaseP)
        {
            if (!ModConfig.EnableInterleavedScheduling.Value)
            {
                ZoneLocation clone = CloneLocation(relaxedLocP);
                clone.m_quantity = quantityToPlaceP;
                PendingPackets[clone.m_prefabName] = quantityToPlaceP;
                List<ZoneLocation> singlePacket = new List<ZoneLocation>();
                singlePacket.Add(clone);
                return singlePacket;
            }

            List<ZoneLocation> tier = new List<ZoneLocation>();
            tier.Add(relaxedLocP);
            int oldQty = relaxedLocP.m_quantity;
            relaxedLocP.m_quantity = quantityToPlaceP;

            List<ZoneLocation> newPackets = ProcessTier(tier, fallbackBaseP);

            relaxedLocP.m_quantity = oldQty;
            return newPackets;
        }

        private static void Enqueue(Dictionary<string, Queue<ZoneLocation>> queuesP, ZoneLocation locP)
        {
            bool hasQueue = queuesP.TryGetValue(locP.m_prefabName, out Queue<ZoneLocation> queue);
            if (!hasQueue)
            {
                queue = new Queue<ZoneLocation>();
                queuesP[locP.m_prefabName] = queue;
            }
            queue.Enqueue(locP);
        }

        /**
        * Retrieves all instance fields (public and non-public) for the ZoneLocation type
        * for shallow cloning using reflection. 
        * Reflecting on this for a while I decided that encapsulation is for wimps :P
        * 
        * This metadata lookup should be cached to ensure O(1) retrieval 
        * during subsequent clone operations, avoiding repetitive O(N) metadata searches.
        * The cloning is still O(N), I mean what can one do.
        * 
        * Note to self: BindingFlags is quite elegant and surprising that I found something in C# that I genuinely appreciate.
        * I mean in Java I would have to do setAccessible. 
        * In C++ forget it.
        */
        private static FieldInfo[] _zoneLocationFieldCache;
        private static ZoneLocation CloneLocation(ZoneLocation origP)
        {
            ZoneLocation clone = new ZoneLocation();

            if (_zoneLocationFieldCache == null)
            {
                _zoneLocationFieldCache = typeof(ZoneLocation).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            
            for (int i = 0; i < _zoneLocationFieldCache.Length; i++)
            {
                _zoneLocationFieldCache[i].SetValue(clone, _zoneLocationFieldCache[i].GetValue(origP));
            }
            return clone;
        }

        public static int GetBudget(ZoneLocation locP, int fallbackBaseP)
        {
            if (ModConfig.EnableInterleavedScheduling.Value && locP != null)
            {
                bool hasBudget = _budgets.TryGetValue(locP, out int budget);
                if (hasBudget)
                {
                    return budget;
                }
            }
            return Mathf.Max(1, Mathf.RoundToInt(fallbackBaseP * ModConfig.OuterMultiplier.Value));
        }

        public static void SyncRelaxation(ZoneLocation relaxedLocP)
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            foreach (ZoneLocation loc in ZoneSystem.instance.m_locations)
            {
                if (loc != relaxedLocP && loc.m_prefabName == relaxedLocP.m_prefabName)
                {
                    loc.m_minAltitude = relaxedLocP.m_minAltitude;
                    loc.m_maxAltitude = relaxedLocP.m_maxAltitude;
                    loc.m_maxDistance = relaxedLocP.m_maxDistance;
                    loc.m_minDistance = relaxedLocP.m_minDistance;
                    loc.m_minTerrainDelta = relaxedLocP.m_minTerrainDelta;
                    loc.m_maxTerrainDelta = relaxedLocP.m_maxTerrainDelta;
                    loc.m_exteriorRadius = relaxedLocP.m_exteriorRadius;
                }
            }
        }

        public static void RestoreLocations(ZoneSystem zsP)
        {
            if (OriginalLocations != null && OriginalLocations.Count > 0)
            {
                zsP.m_locations = OriginalLocations;
            }
            _budgets.Clear();
            PendingPackets.Clear();
            OriginalLocations = null;
            IsGenerating = false;
        }
    }
}
