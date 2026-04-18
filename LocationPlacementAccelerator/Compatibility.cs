// v1.0.2
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

        // EWD reflection state. All four are public static fields on
        // ExpandWorldData.WorldInfo; we read them for diagnostic logging only.
        private static FieldInfo _ewdRadiusField;
        private static FieldInfo _ewdTotalRadiusField;
        private static FieldInfo _ewdStretchField;
        private static FieldInfo _ewdBiomeStretchField;

        // EWD high-relief support. Populated once on first call after EWD is detected.
        // Maps custom biome values to their vanilla terrain classification (Mountain, Mistlands, etc.).
        private static Type _ewdBiomeManagerType;
        private static FieldInfo _ewdBiomeToTerrainField;
        private static Heightmap.Biome _cachedHighReliefMask = Heightmap.Biome.Mountain | Heightmap.Biome.Mistlands;
        private static bool _highReliefMaskComputed = false;

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
        * EWD presence is diagnostic only: EWD mirrors whatever radius EWS (or BC) pushes
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
        * Logs EWD's current world info fields. Purely diagnostic. Helps us notice
        * if EWS and EWD disagree about world size at runtime (which would indicate
        * a config mistake on the user's side).
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
                    return;
                }

                EventInfo minimapCompleteEvent = bcType.GetEvent("MinimapGenerationComplete",
                    BindingFlags.Public | BindingFlags.Static);

                if (minimapCompleteEvent == null)
                {
                    loggerP.LogWarning("[LPACompatibility] BC: MinimapGenerationComplete event not found - minimap wait disabled.");
                    return;
                }

                minimapCompleteEvent.AddEventHandler(null, (Action)(() => BCMinimapDone = true));
                loggerP.LogInfo("[LPACompatibility] BC: Subscribed to MinimapGenerationComplete. Placement will wait for minimap.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[LPACompatibility] BC: Event subscription failed - minimap wait disabled. {exP.Message}");
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
        * 1.0.1 bug fix: we used to look for a field named "WorldRadius" which has
        * never existed on EWD's WorldInfo. Detection has therefore been silently
        * failing since the day EWD support was added. The real field is "Radius".
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

                // BiomeToTerrain is a private static dictionary; AccessTools.Field handles non-public.
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