// v1.0.6
/**
* Detects and talks to companion mods (Better Continents, Expand World Size, Expand World Data).
* Pulls in the world radius from whichever size-authority mod is present (EWS only as of 1.0.1).
* Also publishes the high-relief biome mask for the replaced engine's 3D similarity fallback.
*
* EWD integration in 1.0.1:
*  - Detection now looks at the actual field names on ExpandWorldData.WorldInfo
*    (Radius / TotalRadius / Stretch / BiomeStretch), which was the long-standing bug that I kept putting off for 2 weeks... 
*    previously I was probing for a "WorldRadius" member that does not exist.
*  - Detection is diagnostic only. EWS is the sole size authority per project policy.
*  - GetHighReliefBiomeMask reflects into ExpandWorldData.BiomeManager's BiomeToTerrain
*    dictionary so custom biomes whose terrain algorithm is Mountain or Mistlands
*    participate in the 3D similarity fallback the same way vanilla Mountain/Mistlands do.
*
* 1.0.2:
*  - Added IsValidLocation(loc) mirroring EWD's own IsValid helper from IdManager.cs.
*    This replaces the five places in the replaced engine where I was doing a strict
*    "m_enable && m_prefab != null && m_prefab.IsValid" check. That check was silently
*    filtering out every EWD blueprint-based location, because EWD builds those with
*    an empty AssetID SoftReference (see SetupBlueprint in LocationLoading.cs.EWD:
*    "location.m_prefab = new(new()) { m_name = name };"). SoftReference.IsValid
*    checks the asset id, not the name, so blueprints come back as invalid. Vanilla
*    doesn't care because its outer loop only filters on m_enable && m_quantity, and
*    EWD patches PokeCanSpawnLocation to force-accept anything BlueprintManager knows
*    about. My replaced engine bypasses both of those, so the safety check was
*    gatekeeping the exact locations EWD was trying to add. EWD-mirror semantic below
*    (IsValid OR name present) is what EWD itself uses for its own validity test and
*    is the least-surprising, least-coupled fix. Jere does it, I do it too and call it
*    a day.
*
* 1.0.3 (BC fail-safe):
*    - The MinimapGenerationComplete event hook only exists on my BC fork; stock 
*      upstream BC has no such event. Previous logic in the three error branches 
*      (bcType missing, event missing, exception during subscription) just logged 
*      a warning and returned, leaving BCMinimapDone permanently false. Combined 
*      with the gate in PlacementEngine.Run that yields until BCMinimapDone is 
*      true, this meant any stock-BC + LPA user would deadlock the placement 
*      coroutine on world load - LPA's survey would never start, no locations 
*      would be placed, world would appear empty. Silent because LPA's outer 
*      prefix returns false from call #2 onward, so vanilla's location 
*      generation also doesn't run. So in each error branch, set 
*      BCMinimapDone = true so the wait short-circuits. Of course the trade-off is
*      that stock-BC users get LPA workers (N-2) running concurrently with BC's hardcoded 
*      4 minimap threads instead of cleanly serialized as on my fork. Mild 
*      oversubscription for ~2s on a 6+ core machine. Worth it! ANY behavior 
*      beats hung locations. Push the BC PR upstream when I get a chance to 
*      restore the clean serialized path. I need to remember to talk to Jere about this.
*      
* 1.0.4: Added the GetLocationGroups handle for multi-group similarity. EWD's v1.64 maps location data per-entry, 
* which means a real similarity group like "fortress" gets virtualized onto m_group as a unique per-entry handle whenever a location
* belongs to MORE THAN ONE group (single-group locations keep their real name after the EWD single-group fix which I have to remember immediately after my comments to do a PR.).
* My replaced engine reads m_group raw everywhere, so for a genuine multi-group location it was seeing an opaque handle and losing the real group
* identity, clones and group-mates stopped repelling each other. EWD now exposes Api.GetLocationGroups(ZoneLocation, bool) as a promised-stable contract (:D) that hands back
* the real (group, distance) pairs regardless of the virtual handle. I bind to it here (gated on IsExpandWorldDataActive, same reflection pattern as the BiomeToTerrain handle)
* so the engine can resolve a location's true similarity memberships. I deliberately bind to Api, not the internal LocationExtra.GetGroups, so I am coupled to a surface Jere has
* agreed to hold stable rather than an unversioned internal thingamajig. 
*
* 1.0.5: Added CopyLocationExtra. EWD keys custom objects, the dungeon override, and object
* data/swaps in LocationExtra.ExtraInfo by ZoneLocation REFERENCE. LPA produces clone
* ZoneLocations (interleave packets, relaxation packets, the API work list stuff) that EWD never
* registered, so all of that config was silently lost, swallowed by the black hole... configured dungeons reverted to the
* prefab default and custom objects never spawned (AlexRiven's Crypt4:Void report). Binding
* to LocationExtra.ExtraInfo directly (no Api surface exists for this), no big deal as it is non-fatal if it moves.
*
* 1.0.6: Added GetAnchorGroups, the search-only counterpart to GetLocationGroups. It binds to EWD's directed-anchor
* contract when present and returns null otherwise, so the replaced engine's max-similarity SEARCH set falls back to
* the advertise set (GroupsMax) and worlds using only symmetric groupMax are unaffected.
*/
#nullable disable
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace LPA
{
    public static class Compatibility
    {
        public static bool IsBetterContinentsActive { get; private set; } = false;
        public static bool IsExpandWorldSizeActive { get; private set; } = false;
        public static bool IsExpandWorldDataActive { get; private set; } = false;

        public static float DetectedWorldRadius { get; private set; } = 10000f;
        public static string WorldRadiusSource { get; private set; } = "Vanilla default";

        /**
        * Written by the BC MinimapGenerationComplete event handler
        * (in my BC fork; needs to be pushed to Jere's upstream).
        * The placement coroutine waits on this before starting workers.
        * Volatile for cross-frame visibility even though both reads and writes
        * currently happen on the main thread.
        */
        public static volatile bool BCMinimapDone = false;

        // EWS reflection state
        private static PropertyInfo _ewsWorldRadiusProp;
        private static FieldInfo _ewsWorldRadiusField;

        // EWD reflection state. All four are public static fields on ExpandWorldData.WorldInfo; we read them for the diagnostic logging only.
        private static FieldInfo _ewdRadiusField;
        private static FieldInfo _ewdTotalRadiusField;
        private static FieldInfo _ewdStretchField;
        private static FieldInfo _ewdBiomeStretchField;

        // EWD high-relief support. Populated once on first call after EWD is detected.
        // Maps custom biome values to their vanilla terrain classification (Mountain, Mistlands, etc.).
        // I need to think what I will be doing with my better map and also  my multilevel mountains. meh... later. 
        private static Type _ewdBiomeManagerType;
        private static FieldInfo _ewdBiomeToTerrainField;
        private static Heightmap.Biome _cachedHighReliefMask = Heightmap.Biome.Mountain | Heightmap.Biome.Mistlands;
        private static bool _highReliefMaskComputed = false;

        // EWD multi-group support. Handle to ExpandWorldData.Api.GetLocationGroups(ZoneLocation, bool),
        // the read-side contract that returns a location's real (group, distance) similarity pairs even when m_group is a virtual per-entry handle (the multi-group case after EWD v1.64).
        // Null when EWD is absent or the contract is missing; callers fall back to raw m_group as was previously the case.
        private static MethodInfo _ewdGetLocationGroupsMethod;

        // EWD directed-anchor support. Handle to ExpandWorldData.Api.GetAnchorGroups(ZoneLocation), the search-only
        // companion to GetLocationGroups: the groups a location must be placed NEAR without advertising into them.
        // Null when EWD lacks the contract, in which case the max-similarity search set falls back to the advertise
        // set (GroupsMax), i.e. plain symmetric groupMax behaviour.
        private static MethodInfo _ewdGetAnchorGroupsMethod;

        // EWD per-location config. Handle to LocationExtra.ExtraInfo, keyed by ZoneLocation reference.
        // Null when EWD is absent or the field moves; CopyLocationExtra becomes a no-op in that case.
        private static FieldInfo _ewdExtraInfoField;

        /**
        * Returns true if the location has a realasset (IsValid) OR at least a name on the SoftReference (the shape EWD
        * gives blueprint locations: empty AssetID + m_name set). This is exactly the condition EWD's own 
        * IdManager.IsValid uses. I didn't want to couple to BlueprintManager directly because (a) that requires reflection across
        * a soft-referenced assembly, and (b) any other mod that follows EWD's "empty-AssetID + name" pattern for runtime-built locations will also
        * benefit from this, which is the right and noble behavior.
        *
        * Call this anywhere the replaced engine was using "loc.m_enable && loc.m_prefab != null && loc.m_prefab.IsValid"
        * and fold m_enable / m_quantity into the surrounding check (this helper does not gate on either because the call sites have 
        * different needs around enable/quantity filtering).
        * 
        * I may have to rewrite the comment here. I found myself reading the above thing thrice in what... a week later?
        * no chance I follow or remember by July.
        */
        public static bool IsValidLocation(ZoneSystem.ZoneLocation locP)
        {
            if (locP == null)
            {
                return false;
            }
            if (locP.m_prefab == null)
            {
                return false;
            }
            // EWD's IdManager.IsValid uses exactly this disjunction. Matching it keeps us in lockstep with EWD's definition of "a placeable location" rather than inventing my own. 
            if (locP.m_prefab.IsValid)
            {
                return true;
            }
            if (locP.m_prefab.m_name != null)
            {
                return true;
            }
            return false;
        }

        public static void Initialize(ManualLogSource loggerP)
        {
            DetectBetterContinents(loggerP);
            DetectExpandWorldSize(loggerP);
            DetectExpandWorldData(loggerP);

            RefreshWorldRadius(loggerP);

            loggerP.LogInfo($"[LPACompatibility] Init complete. " +
                            $"BC={IsBetterContinentsActive}, " +
                            $"EWS={IsExpandWorldSizeActive}," +
                            $" EWD={IsExpandWorldDataActive}");

            if (IsExpandWorldDataActive)
            {
                LogEWDWorldInfoSnapshot(loggerP);
            }
        }

        /**
        * Resolves the effective world radius. EWS is the sole size authority.
        * EWD presence is diagnostic only: EWD mirrors whatever radius EWS (or BC although BC yields to EWS) pushes
        * via its own WorldInfo.Set, so reading EWD's radius here would double-count
        * or conflict. Vanilla default of 10000m applies when EWS is not present. 
        */
        public static float RefreshWorldRadius(ManualLogSource loggerP)
        {
            float radius = 10000f;
            string source = "Vanilla default";

            if (IsExpandWorldSizeActive)
            {
                float? ewsRadius = ReadEWSRadius();
                bool ewsRadiusIsUsable = ewsRadius.HasValue && ewsRadius.Value > 100f;

                if (ewsRadiusIsUsable)
                {
                    radius = ewsRadius.Value;
                    source = "Expand World Size";
                }
                else
                {
                    loggerP.LogWarning("[LPACompatibility] EWS detected but radius read failed - using 10000m.");
                }
            }

            DetectedWorldRadius = radius;
            WorldRadiusSource = source;

            return radius;
        }

        /**
        * Retrieves the full dictionary mapping custom biomes to their underlying terrain biome.
        * Used by the minimap parallelizer to resolve colors and mask overlays correctly for EWD.
        */
        public static Dictionary<Heightmap.Biome, Heightmap.Biome> GetEwdBiomeToTerrainMap()
        {
            if (!IsExpandWorldDataActive || _ewdBiomeToTerrainField == null)
            {
                return null;
            }

            try
            {
                object dictObj = _ewdBiomeToTerrainField.GetValue(null);
                if (dictObj is IDictionary dict)
                {
                    Dictionary<Heightmap.Biome, Heightmap.Biome> map = new Dictionary<Heightmap.Biome, Heightmap.Biome>();
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Key == null || entry.Value == null)
                        {
                            continue;
                        }
                        map[(Heightmap.Biome)entry.Key] = (Heightmap.Biome)entry.Value;
                    }
                    return map;
                }
            }
            catch (Exception exP)
            {
                DiagnosticLog.WriteTimestampedLog(
                    $"[LPACompatibility] Failed to extract EWD BiomeToTerrain map: {exP.Message}",
                    BepInEx.Logging.LogLevel.Warning);
            }

            return null;
        }

        /**
        * Resolves a location's real similarity groups through EWD's Api.GetLocationGroups contract.
        * Returns the list of (groupName, distance) pairs, or null when EWD is absent, the handle did
        * not resolve, or the location declares no groups. The replaced engine uses this to honor
        * multi-group locations, whose m_group is a virtual handle that hides the real group names quite unhelpfully. :D 
        *
        * The contract returns System.Collections.Generic.List of System.Tuple both framework types shared across the assembly boundary, so
        * the boxed reflection result casts directly with no per-element reflection.
        * maxGroupP selects the min-similarity groups (false) or max-similarity groups (true). The replaced engine only checks the min set.
        */
        public static List<Tuple<string, float>> GetLocationGroups(ZoneSystem.ZoneLocation locP, bool maxGroupP)
        {
            if (_ewdGetLocationGroupsMethod == null || locP == null)
            {
                return null;
            }
            try
            {
                object result = _ewdGetLocationGroupsMethod.Invoke(null, new object[] { locP, maxGroupP });
                return result as List<Tuple<string, float>>;
            }
            catch (Exception exP)
            {
                DiagnosticLog.WriteTimestampedLog(
                    $"[LPACompatibility] GetLocationGroups failed: {exP.Message}. Falling back to raw m_group.",
                    BepInEx.Logging.LogLevel.Warning);
                return null;
            }
        }

        /**
        * Resolves a location's search-only anchor groups through EWD's Api.GetAnchorGroups contract: the (group,
        * distance) pairs this location must be placed NEAR without itself advertising into them. Returns null when
        * EWD is absent, the handle did not resolve (older EWD, or LPA shipped ahead of the EWD feature), or the
        * location declares no anchors. A null result collapses the max-similarity search set onto the advertise set,
        * so a world with no directed anchors behaves exactly as symmetric groupMax did.
        */
        public static List<Tuple<string, float>> GetAnchorGroups(ZoneSystem.ZoneLocation locP)
        {
            if (_ewdGetAnchorGroupsMethod == null || locP == null)
            {
                return null;
            }
            try
            {
                object result = _ewdGetAnchorGroupsMethod.Invoke(null, new object[] { locP });
                return result as List<Tuple<string, float>>;
            }
            catch (Exception exP)
            {
                DiagnosticLog.WriteTimestampedLog(
                    $"[LPACompatibility] GetAnchorGroups failed: {exP.Message}. Treating location as having no anchors.",
                    BepInEx.Logging.LogLevel.Warning);
                return null;
            }
        }

        /**
        * Mirrors EWD's per-location config association from an origin ZoneLocation onto a clone.
        * EWD keys LocationExtra.ExtraInfo by ZoneLocation REFERENCE, so a clone - a brand-new object
        * LPA produces for interleaving, relaxation, and the API work list - is absent from that table
        * and silently loses its custom objects, dungeon override, and object data/swaps, falling back
        * to vanilla content (a default dungeon, missing objects). Sharing the same LocationExtraInfo
        * reference is correct: per-location config is immutable, every clone of one origin resolves
        * identically. No-op when EWD is absent or the handle failed to bind.
        */
        public static void CopyLocationExtra(ZoneSystem.ZoneLocation fromP, ZoneSystem.ZoneLocation toP)
        {
            if (_ewdExtraInfoField == null || fromP == null || toP == null)
            {
                return;
            }
            try
            {
                IDictionary extraInfo = _ewdExtraInfoField.GetValue(null) as IDictionary;
                if (extraInfo == null)
                {
                    return;
                }
                if (extraInfo.Contains(fromP))
                {
                    extraInfo[toP] = extraInfo[fromP];
                }
            }
            catch (Exception exP)
            {
                DiagnosticLog.WriteTimestampedLog(
                    $"[LPACompatibility] CopyLocationExtra failed: {exP.Message}. Clone may lose its EWD config.",
                    BepInEx.Logging.LogLevel.Warning);
            }
        }

        /**
        * Returns the set of biomes (as an ORed Heightmap.Biome bitmask) that should trigger
        * the 3D similarity fallback. Vanilla baseline is Mountain | Mistlands.
        * When EWD is present, any custom biome whose terrain algorithm maps to Mountain
        * or Mistlands is also included (Summit, High Peak Mountain, Deep Mistlands, etc.
        * from Zeus-style configurations).
        *
        * Computed lazily on first call because EWD's biome data may not be loaded yet
        * at Initialize() time. Cached thereafter.
        */
        public static Heightmap.Biome GetHighReliefBiomeMask()
        {
            if (_highReliefMaskComputed)
            {
                return _cachedHighReliefMask;
            }

            Heightmap.Biome mask = Heightmap.Biome.Mountain | Heightmap.Biome.Mistlands;

            if (IsExpandWorldDataActive && _ewdBiomeToTerrainField != null)
            {
                try
                {
                    object dictObj = _ewdBiomeToTerrainField.GetValue(null);
                    if (dictObj is IDictionary dict)
                    {
                        int extraCount = 0;
                        foreach (DictionaryEntry entry in dict)
                        {
                            if (entry.Key == null || entry.Value == null)
                            {
                                continue;
                            }
                            Heightmap.Biome customBiome = (Heightmap.Biome)entry.Key;
                            Heightmap.Biome terrainBiome = (Heightmap.Biome)entry.Value;

                            // Skip identity mappings (vanilla biomes map to themselves in BiomeToTerrain).
                            // Only add custom biomes whose terrain algorithm is Mountain or Mistlands.
                            if (customBiome == terrainBiome)
                            {
                                continue;
                            }
                            if (terrainBiome == Heightmap.Biome.Mountain || terrainBiome == Heightmap.Biome.Mistlands)
                            {
                                if ((mask & customBiome) == 0)
                                {
                                    mask |= customBiome;
                                    extraCount++;
                                }
                            }
                        }
                        if (extraCount > 0)
                        {
                            DiagnosticLog.WriteTimestampedLog(
                                $"[LPACompatibility] EWD high-relief: {extraCount} custom biome(s) mapped to Mountain/Mistlands terrain. Added to 3D similarity mask.");
                        }
                    }
                }
                catch (Exception exP)
                {
                    DiagnosticLog.WriteTimestampedLog(
                        $"[LPACompatibility] High-relief discovery failed: {exP.Message}. Falling back to vanilla Mountain|Mistlands.",
                        BepInEx.Logging.LogLevel.Warning);
                }
            }

            _cachedHighReliefMask = mask;
            _highReliefMaskComputed = true;
            return mask;
        }

        /**
        * Logs EWD's current world info fields. Purely diagnostic. Helps with noticing
        * if EWS and EWD disagree about world size at runtime (which would indicate a config mistake on the user's side). Basically this should not happen. 
        */
        private static void LogEWDWorldInfoSnapshot(ManualLogSource loggerP)
        {
            try
            {
                float radius = ReadEWDFloatField(_ewdRadiusField);
                float totalRadius = ReadEWDFloatField(_ewdTotalRadiusField);
                float stretch = ReadEWDFloatField(_ewdStretchField);
                float biomeStretch = ReadEWDFloatField(_ewdBiomeStretchField);
                loggerP.LogInfo(
                    $"[LPACompatibility] EWD WorldInfo snapshot: " +
                    $"Radius={radius:F0} TotalRadius={totalRadius:F0} " +
                    $"Stretch={stretch:F3} BiomeStretch={biomeStretch:F3}");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[LPACompatibility] EWD WorldInfo snapshot failed: {exP.Message}");
            }
        }

        private static float ReadEWDFloatField(FieldInfo fieldP)
        {
            if (fieldP == null)
            {
                return 0f;
            }
            return Convert.ToSingle(fieldP.GetValue(null));
        }

        private static void DetectBetterContinents(ManualLogSource loggerP)
        {
            bool bcPluginFound = Chainloader.PluginInfos.TryGetValue("BetterContinents", out BepInEx.PluginInfo bcPluginInfo);
            if (!bcPluginFound)
            {
                return;
            }

            IsBetterContinentsActive = true;
            loggerP.LogInfo("[LPACompatibility] Better Continents detected.");

            try
            {
                Assembly bcAssembly = bcPluginInfo.Instance.GetType().Assembly;
                Type bcType = bcAssembly.GetType("BetterContinents.BetterContinents");

                if (bcType == null)
                {
                    loggerP.LogWarning("[LPACompatibility] BC: BetterContinents type not found - minimap wait disabled.");
                    BCMinimapDone = true;
                    return;
                }

                EventInfo minimapCompleteEvent = bcType.GetEvent("MinimapGenerationComplete",
                    BindingFlags.Public | BindingFlags.Static);

                if (minimapCompleteEvent == null)
                {
                    loggerP.LogWarning("[LPACompatibility] BC: MinimapGenerationComplete event not found - minimap wait disabled.");
                    BCMinimapDone = true;
                    return;
                }

                minimapCompleteEvent.AddEventHandler(null, (Action)(() => BCMinimapDone = true));
                loggerP.LogInfo("[LPACompatibility] BC: Subscribed to MinimapGenerationComplete. Placement will wait for minimap.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[LPACompatibility] BC: Event subscription failed - minimap wait disabled. {exP.Message}");
                BCMinimapDone = true;
            }
        }

        private static void DetectExpandWorldSize(ManualLogSource loggerP)
        {
            bool ewsPluginFound = Chainloader.PluginInfos.TryGetValue("expand_world_size", out BepInEx.PluginInfo ewsPluginInfo);
            if (!ewsPluginFound)
            {
                return;
            }

            try
            {
                Assembly ewsAssembly = ewsPluginInfo.Instance.GetType().Assembly;
                Type ewsConfigType = ewsAssembly.GetType("ExpandWorldSize.Configuration");

                if (ewsConfigType == null)
                {
                    loggerP.LogWarning("[LPACompatibility] EWS: Configuration type not found.");
                    return;
                }

                _ewsWorldRadiusProp = AccessTools.Property(ewsConfigType, "WorldRadius");
                if (_ewsWorldRadiusProp == null)
                {
                    _ewsWorldRadiusField = AccessTools.Field(ewsConfigType, "WorldRadius");
                }

                if (_ewsWorldRadiusProp == null && _ewsWorldRadiusField == null)
                {
                    loggerP.LogWarning("[LPACompatibility] EWS: WorldRadius member not found.");
                    return;
                }

                IsExpandWorldSizeActive = true;
                loggerP.LogInfo("[LPACompatibility] Expand World Size detected.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[LPACompatibility] EWS error: {exP.Message}");
            }
        }

        /**
        * Detects EWD and caches field handles for the four public static fields on
        * ExpandWorldData.WorldInfo (Radius, TotalRadius, Stretch, BiomeStretch).
        * Also caches the handle to ExpandWorldData.BiomeManager's BiomeToTerrain
        * dictionary for high-relief discovery later.
        *
        * 1.0.1 bug fix: I  used to look for a field named "WorldRadius" which has
        * never existed on EWD's WorldInfo. Detection has therefore been silently
        * failing since the day EWD support was added. The real field is "Radius".
        * Either Jere changed it, or I was blind. 
        */
        private static void DetectExpandWorldData(ManualLogSource loggerP)
        {
            bool ewdPluginFound = Chainloader.PluginInfos.TryGetValue("expand_world_data", out BepInEx.PluginInfo ewdPluginInfo);
            if (!ewdPluginFound)
            {
                return;
            }

            try
            {
                Assembly ewdAssembly = ewdPluginInfo.Instance.GetType().Assembly;
                Type ewdWorldInfoType = ewdAssembly.GetType("ExpandWorldData.WorldInfo");

                if (ewdWorldInfoType == null)
                {
                    loggerP.LogWarning("[LPACompatibility] EWD: ExpandWorldData.WorldInfo type not found.");
                    return;
                }

                _ewdRadiusField = AccessTools.Field(ewdWorldInfoType, "Radius");
                _ewdTotalRadiusField = AccessTools.Field(ewdWorldInfoType, "TotalRadius");
                _ewdStretchField = AccessTools.Field(ewdWorldInfoType, "Stretch");
                _ewdBiomeStretchField = AccessTools.Field(ewdWorldInfoType, "BiomeStretch");

                if (_ewdRadiusField == null)
                {
                    loggerP.LogWarning("[LPACompatibility] EWD: Radius field not found on WorldInfo. EWD integration will remain inactive.");
                    return;
                }

                _ewdBiomeManagerType = ewdAssembly.GetType("ExpandWorldData.BiomeManager");
                if (_ewdBiomeManagerType != null)
                {
                    _ewdBiomeToTerrainField = AccessTools.Field(_ewdBiomeManagerType, "BiomeToTerrain");
                    if (_ewdBiomeToTerrainField == null)
                    {
                        loggerP.LogWarning("[LPACompatibility] EWD: BiomeToTerrain field not found. Custom biomes will not participate in 3D similarity mask (vanilla fallback used).");
                    }
                }
                else
                {
                    loggerP.LogWarning("[LPACompatibility] EWD: BiomeManager type not found. Custom biome terrain classification unavailable.");
                }

                Type ewdApiType = ewdAssembly.GetType("ExpandWorldData.Api");
                if (ewdApiType != null)
                {
                    _ewdGetLocationGroupsMethod = AccessTools.Method(ewdApiType, "GetLocationGroups",
                        new Type[] { typeof(ZoneSystem.ZoneLocation), typeof(bool) });
                    if (_ewdGetLocationGroupsMethod == null)
                    {
                        loggerP.LogWarning("[LPACompatibility] EWD: Api.GetLocationGroups not found. Multi-group similarity will fall back to raw m_group.");
                    }

                    _ewdGetAnchorGroupsMethod = AccessTools.Method(ewdApiType, "GetAnchorGroups",
                        new Type[] { typeof(ZoneSystem.ZoneLocation) });
                    if (_ewdGetAnchorGroupsMethod == null)
                    {
                        loggerP.LogInfo("[LPACompatibility] EWD: Api.GetAnchorGroups not present (older EWD, or LPA ahead of the EWD directed-anchor feature). Max-similarity search set defaults to the advertise set.");
                    }
                }
                else
                {
                    loggerP.LogWarning("[LPACompatibility] EWD: Api type not found. Multi-group similarity will fall back to raw m_group.");
                }

                Type ewdLocationExtraType = ewdAssembly.GetType("ExpandWorldData.LocationExtra");
                if (ewdLocationExtraType != null)
                {
                    _ewdExtraInfoField = AccessTools.Field(ewdLocationExtraType, "ExtraInfo");
                    if (_ewdExtraInfoField == null)
                    {
                        loggerP.LogWarning("[LPACompatibility] EWD: LocationExtra.ExtraInfo not found. Clone locations will lose custom objects and dungeon overrides.");
                    }
                }
                else
                {
                    loggerP.LogWarning("[LPACompatibility] EWD: LocationExtra type not found. Clone locations will lose custom objects and dungeon overrides.");
                }

                IsExpandWorldDataActive = true;
                loggerP.LogInfo("[LPACompatibility] Expand World Data detected.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[LPACompatibility] EWD reflection error: {exP.Message}");
            }
        }

        private static float? ReadEWSRadius()
        {
            try
            {
                if (_ewsWorldRadiusProp != null)
                {
                    return Convert.ToSingle(_ewsWorldRadiusProp.GetValue(null));
                }
                if (_ewsWorldRadiusField != null)
                {
                    return Convert.ToSingle(_ewsWorldRadiusField.GetValue(null));
                }
            }
            catch { }
            return null;
        }
    }
}