// v1.3
/**
* Splits each location type's total quantity into individual work packets
* and interleaves them round-robin across similarity groups. This prevents
* a single high-quantity type from monopolizing spatial territory before
* competing types get a chance.
*
* When interleaving is OFF, this still acts as the authoritative source for
* PendingPackets and OriginalLocations, and handles budget calculation.
*
* 1.1: Added BuildApiWorkList + ResetApiState for the LPA public API path.
* The API needs the same packetization machinery as world-gen but must NEVER
* mutate ZoneSystem.m_locations. BuildApiWorkList clones the caller's
* PlacementRequest list, then subtracts the current m_locationInstances
* count per prefab to derive each clone's m_quantity. This matches vanilla
* UW DistributeLocations semantics exactly as I understood them:
* TargetQuantity is the WORLD target, the engine adds (target - current) more instances.
* UW's own sweep happens before the API call, so the count reflects whatever survived.
* ResetApiState mirrors RestoreLocations but skips the zsP.m_locations writeback.
*
* 1.2: My god... so logical TYPE KEY. EWD clones are entirely different location types that happen to
* share a prefab name (e.g. a Mountain variant of a normally BlackForest location), which is what Jere told me is how people would be using this usually.
* Madness..
* Anyway, keying per-type accounting on m_prefabName conflated them. A type key disambiguates: 
* the first occurrence of a prefab keeps the bare name, every subsequent clone becomes "prefab#N"
* (N by OriginalLocations order, deterministic and stable across runs). So for any world WITHOUT clones the key IS the prefab name and nothing changes anywhere.
* The key is assigned once on the snapshot (AssignTypeKeys) and inherited by every packet,  because CloneLocation stamps it
* onto the clone from the source, so propagation survives arbitrary clone depth (a relaxed packet of a packet still chains back to the origin) 
* and can never be forgotten at a call site. I hope...
* With this in place the relaxer needs no clone awareness at all: keyed on the type key, each clone is simply a separate prefab to it.
* GetOriginalQuantity gains a ZoneLocation overload that resolves per type; the legacy string overload is untouched for the transpiled engine.
* PendingPackets and SyncRelaxation now key/match on the type key. 
* NOTE to remember: the transpiled engine shares PendingPackets and stays prefab-keyed in its own reads,like identical for non-clone worlds, and the transpiled+clones
* combination was already conflated before this, so pre-existing mess... not a regression.
* 
* * 1.3: CloneLocation now mirrors EWD's per-location config onto the clone via
* Compatibility.CopyLocationExtra. EWD stores custom objects, the dungeon override, and object
* data/swaps keyed by the ZoneLocation reference; a clone is a new object EWD never registered,
* so it was silently losing all of that and reverting to vanilla content (default dungeon, no
* custom objects). Stamping it in the single clone primitive means every packet path and a
* clone-of-clone chain inherit it, same as the type-key stamp. No nothing when EWD is absent.
* Should fix what AlexRiven reported.
* 
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

        /**
        * Maps each ZoneLocation (snapshot entry OR packet clone) to its logical type key.
        * Snapshot entries are keyed by AssignTypeKeys and packets inherit their source's key inside CloneLocation. 
        * A location not in this table (e.g. a pre-existing instance) falls back to its raw prefab name via GetTypeKey, which is exactly the legacy behavior.
        */
        private static Dictionary<ZoneLocation, string> _typeKey = new Dictionary<ZoneLocation, string>();

        // Type key --> the type's original (pre-packetization) quantity. Filled by AssignTypeKeys.
        private static Dictionary<string, int> _origQtyByType = new Dictionary<string, int>(System.StringComparer.Ordinal);

        /**
        * Returns the logical type key for a location: its assigned key if present, otherwise its the raw prefab name.
        * For a world with no clones every key equals the prefab name, so callers that swap m_prefabName for GetTypeKey see no change at all in the common case.
        */
        public static string GetTypeKey(ZoneLocation locP)
        {
            if (locP == null)
            {
                return null;
            }
            bool hasKey = _typeKey.TryGetValue(locP, out string key);
            if (hasKey)
            {
                return key;
            }
            return locP.m_prefabName;
        }

        /**
        * Assigns a stable type key to every entry in a freshly built snapshot and records each type's original quantity.
        * First occurrence of a prefab keeps the bare name and subsequent duplicates stormtrooper clones become "prefab#1", "prefab#2", ... in snapshot order. 
        * Deterministic, so the same world produces the same keys every run.
        */
        private static void AssignTypeKeys(List<ZoneLocation> entriesP)
        {
            Dictionary<string, int> seenCounts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            for (int i = 0; i < entriesP.Count; i++)
            {
                ZoneLocation entry = entriesP[i];
                string prefab = entry.m_prefabName;
                bool hasSeen = seenCounts.TryGetValue(prefab, out int seen);
                string key;
                if (!hasSeen)
                {
                    key = prefab;
                    seenCounts[prefab] = 1;
                }
                else
                {
                    key = prefab + "#" + seen.ToString();
                    seenCounts[prefab] = seen + 1;
                }
                _typeKey[entry] = key;
                _origQtyByType[key] = entry.m_quantity;
            }
        }

        public static void ClearLoggedStart(string prefabNameP)
        {
            LoggedStarts.Remove(prefabNameP);
        }

        public static bool TryLogStart(string prefabNameP)
        {
            return LoggedStarts.Add(prefabNameP);
        }

        /**
        * Replaced-engine overload. Resolves the original quantity for the location's logical TYPE, so distinct clones sharing a prefab return their own quantities (E1=50, E2=20) instead of
        * the first prefab match. Packets resolve through their inherited type key to the origin's quantity.
        * Falls back to the legacy prefab scan only for un-keyed locations.
        */
        public static int GetOriginalQuantity(ZoneLocation locP)
        {
            if (locP == null)
            {
                return 1;
            }

            string key = GetTypeKey(locP);
            bool hasByType = _origQtyByType.TryGetValue(key, out int qByType);
            if (hasByType)
            {
                return qByType;
            }

            return GetOriginalQuantity(locP.m_prefabName);
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
            _typeKey.Clear();
            _origQtyByType.Clear();
            OriginalLocations = new List<ZoneLocation>(zsP.m_locations);
            AssignTypeKeys(OriginalLocations);

            if (!ModConfig.EnableInterleavedScheduling.Value)
            {
                foreach (ZoneLocation loc in OriginalLocations)
                {
                    if (loc.m_enable && loc.m_quantity > 0)
                    {
                        PendingPackets[GetTypeKey(loc)] = loc.m_quantity;
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
            return ProcessTier(tierP, baseBudgetP, true);
        }

        private static List<ZoneLocation> ProcessTier(List<ZoneLocation> tierP, int baseBudgetP, bool subtractAlreadyPlacedP)
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
                    PendingPackets[GetTypeKey(loc)] = 1;
                    continue;
                }

                int alreadyPlaced = 0;
                if (subtractAlreadyPlacedP && zs != null)
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

                PendingPackets[GetTypeKey(loc)] = totalQty;

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

            // Flatten all per-prefab queues into per-group queues, then round-robin across groups so that competing types get interleaved placement slots.
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
                PendingPackets[GetTypeKey(clone)] = quantityToPlaceP;
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
        * Retrieves all instance fields (public and non-public) for the ZoneLocation type for shallow cloning using reflection. 
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

            /**
            * Stamp the source's logical type key onto the clone. 
            * Doing it here, in the one clone primitive, means no packet-creation path can forget it, and a clone of a clone still
            * chains back to the origin type (GetTypeKey(origP) returns origP's own key or its prefab-name fallback). Jesus, this sentence...
            * This is what lets the relaxer stay completely clone-unaware.
            */
            _typeKey[clone] = GetTypeKey(origP);

            /**
            * Mirror EWD's per-location config (custom objects, dungeon override, object data/swaps) onto the clone. 
            * EWD keys that data by the ZoneLocation reference, and a clone is a new object it never registered, 
            * so without this the clone silently falls back to vanilla conten, i.e. the prefab's default dungeon instead of the configured one, and no custom objects.
            * Done here in the one clone primitive so every packet path and clone-of-clone
            * chain inherits it for free, exactly like the type-key stamp above. When EWD is
            * absent we have a no op, and harmless for vanilla locations (as they are simply not in EWD's table).
            */
            Compatibility.CopyLocationExtra(origP, clone);
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
            /**
            * Match on the logical type key, not the prefab name. Two distinct clones share a prefab
            * but have different type keys, so a Mountain-variant relaxation no longer bleeds its
            * loosened altitude band onto an unrelated BlackForest or whatever the heck-variant. 
            * Same key == this clone and its own packets, which is exactly the set that should track the relaxation.
            */
            string relaxedKey = GetTypeKey(relaxedLocP);
            foreach (ZoneLocation loc in ZoneSystem.instance.m_locations)
            {
                if (loc != relaxedLocP && GetTypeKey(loc) == relaxedKey)
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

        /**
        * The LPA public API entry.
        * 
        * Builds the per call work list from a PlacementRequest collection without ever touching zsP.m_locations.
        *
        * Sets OriginalLocations to the non packet-ized clone snapshot so the
        * parallel engine's RunParallelPath can read it as the source oftruth
        * (each entry's m_quantity == TargetQuantity, no packets). 
        * Returns the packet-ized list (when interleaving is on)
        * or a fresh copy of the snapshot (when off) for the sequential
        * engine's BuildTokenList path.
        *
        * subtractAlreadyPlaced is applied here at clone time so the non-interleaved 
        * branch sees the corrected m_quantity too. 
        * 
        * A bit of a stupid comment 
        */
        public static List<ZoneLocation> BuildApiWorkList(System.Collections.Generic.IEnumerable<PlacementRequest> requestsP, bool interleavedOverrideP)
        {
            _budgets.Clear();
            PendingPackets.Clear();
            LoggedStarts.Clear();
            _typeKey.Clear();
            _origQtyByType.Clear();
            IsGenerating = true;

            ZoneSystem zs = ZoneSystem.instance;

            // Pre-count current instances per requested prefab so we can subtract once upfront rather than per-tier.
            Dictionary<string, int> currentCounts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            HashSet<string> wantedPrefabs = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (PlacementRequest req in requestsP)
            {
                if (req.Location != null)
                {
                    wantedPrefabs.Add(req.Location.m_prefabName);
                }
            }
            if (zs != null)
            {
                foreach (LocationInstance inst in zs.m_locationInstances.Values)
                {
                    string n = inst.m_location.m_prefabName;
                    if (!wantedPrefabs.Contains(n))
                    {
                        continue;
                    }
                    currentCounts.TryGetValue(n, out int c);
                    currentCounts[n] = c + 1;
                }
            }

            List<ZoneLocation> snapshot = new List<ZoneLocation>();
            foreach (PlacementRequest req in requestsP)
            {
                if (req.Location == null)
                {
                    continue;
                }
                ZoneLocation clone = CloneLocation(req.Location);
                int target = Mathf.Max(0, req.TargetQuantity);
                currentCounts.TryGetValue(req.Location.m_prefabName, out int already);
                clone.m_quantity = Mathf.Max(0, target - already);
                snapshot.Add(clone);
            }
            OriginalLocations = snapshot;
            AssignTypeKeys(snapshot);

            if (!interleavedOverrideP)
            {
                foreach (ZoneLocation loc in snapshot)
                {
                    if (loc.m_enable && loc.m_quantity > 0)
                    {
                        PendingPackets[GetTypeKey(loc)] = loc.m_quantity;
                    }
                }
                DiagnosticLog.WriteTimestampedLog(
                    $"[Dispatcher] API run, interleaving OFF. {snapshot.Count} requests passed straight through.");
                return new List<ZoneLocation>(snapshot);
            }

            List<ZoneLocation> prio = new List<ZoneLocation>();
            List<ZoneLocation> nonPrio = new List<ZoneLocation>();
            for (int i = 0; i < snapshot.Count; i++)
            {
                ZoneLocation loc = snapshot[i];
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

            List<ZoneLocation> packetized = new List<ZoneLocation>();
            packetized.AddRange(ProcessTier(prio, 200000, false));
            packetized.AddRange(ProcessTier(nonPrio, 100000, false));
            DiagnosticLog.WriteTimestampedLog(
                $"[Dispatcher] API run, interleaved. {snapshot.Count} requests packetized to {packetized.Count} entries.");
            return packetized;
        }

        /**
        * LPA public API cleanup. Monkey sees monkeys does the state clearing parts of
        * RestoreLocations but on purpsoe skips the zsP.m_locations writeback as the API path never mutated it in the first place.
        */
        public static void ResetApiState()
        {
            _budgets.Clear();
            PendingPackets.Clear();
            LoggedStarts.Clear();
            _typeKey.Clear();
            _origQtyByType.Clear();
            OriginalLocations = null;
            IsGenerating = false;
        }

        public static void RestoreLocations(ZoneSystem zsP)
        {
            if (OriginalLocations != null && OriginalLocations.Count > 0)
            {
                zsP.m_locations = OriginalLocations;
            }
            _budgets.Clear();
            PendingPackets.Clear();
            _typeKey.Clear();
            _origQtyByType.Clear();
            OriginalLocations = null;
            IsGenerating = false;
        }
    }
}