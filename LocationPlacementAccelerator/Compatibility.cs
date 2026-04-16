// v0.9 
// I have a slight issue with detecting EWD, which is not important now, but I need to fix it. 
#nullable disable
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using BepInEx.Bootstrap;

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
        * Written by the BC MinimapGenerationComplete event handler. 
        * i.e. my version of BC. I need to remember to push this change to Jere's fork. 
        * The placement coroutine waits on this before starting workers.
        * Volatile for cross-frame visibility even though both reads and writes
        * currently happen on the main thread.
        */
        public static volatile bool BCMinimapDone = false;

        private static PropertyInfo _ewsWorldRadiusProp;
        private static FieldInfo _ewsWorldRadiusField;

        private static PropertyInfo _ewdWorldRadiusProp;
        private static FieldInfo _ewdWorldRadiusField;

        public static void Initialize(ManualLogSource loggerP)
        {
            DetectBetterContinents(loggerP);
            DetectExpandWorldSize(loggerP);

            if (!IsExpandWorldSizeActive)
            {
                DetectExpandWorldData(loggerP);
            }

            RefreshWorldRadius(loggerP);

            loggerP.LogInfo($"[BoosterCompatibility] Init complete. " +
                            $"BC={IsBetterContinentsActive}, " +
                            $"EWS={IsExpandWorldSizeActive}," +
                            $" EWD={IsExpandWorldDataActive}");
        }

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
                    loggerP.LogWarning("[BoosterCompatibility] EWS detected but radius read failed - using 10000m.");
                }
            }
            else if (IsExpandWorldDataActive)
            {
                float? ewdRadius = ReadEWDRadius();
                bool ewdRadiusIsUsable = ewdRadius.HasValue && ewdRadius.Value > 100f;

                if (ewdRadiusIsUsable)
                {
                    radius = ewdRadius.Value;
                    source = "Expand World Data";
                }
                else
                {
                    loggerP.LogWarning("[BoosterCompatibility] EWD detected but radius read failed - using 10000m.");
                }
            }

            DetectedWorldRadius = radius;
            WorldRadiusSource = source;

            return radius;
        }

        private static void DetectBetterContinents(ManualLogSource loggerP)
        {
            bool bcPluginFound = Chainloader.PluginInfos.TryGetValue("BetterContinents", out BepInEx.PluginInfo bcPluginInfo);
            if (!bcPluginFound)
            {
                return;
            }

            IsBetterContinentsActive = true;
            loggerP.LogInfo("[BoosterCompatibility] Better Continents detected.");

            try
            {
                Assembly bcAssembly = bcPluginInfo.Instance.GetType().Assembly;
                Type bcType = bcAssembly.GetType("BetterContinents.BetterContinents");

                if (bcType == null)
                {
                    loggerP.LogWarning("[BoosterCompatibility] BC: BetterContinents type not found - minimap wait disabled.");
                    return;
                }

                EventInfo minimapCompleteEvent = bcType.GetEvent("MinimapGenerationComplete",
                    BindingFlags.Public | BindingFlags.Static);

                if (minimapCompleteEvent == null)
                {
                    loggerP.LogWarning("[BoosterCompatibility] BC: MinimapGenerationComplete event not found - minimap wait disabled.");
                    return;
                }

                minimapCompleteEvent.AddEventHandler(null, (Action)(() => BCMinimapDone = true));
                loggerP.LogInfo("[BoosterCompatibility] BC: Subscribed to MinimapGenerationComplete. Placement will wait for minimap.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[BoosterCompatibility] BC: Event subscription failed - minimap wait disabled. {exP.Message}");
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
                    loggerP.LogWarning("[BoosterCompatibility] EWS: Configuration type not found.");
                    return;
                }

                _ewsWorldRadiusProp = AccessTools.Property(ewsConfigType, "WorldRadius");
                _ewsWorldRadiusField = _ewsWorldRadiusProp == null ? AccessTools.Field(ewsConfigType, "WorldRadius") : null;

                if (_ewsWorldRadiusProp == null && _ewsWorldRadiusField == null)
                {
                    loggerP.LogWarning("[BoosterCompatibility] EWS: WorldRadius member not found.");
                    return;
                }

                IsExpandWorldSizeActive = true;
                loggerP.LogInfo("[BoosterCompatibility] Expand World Size detected.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[BoosterCompatibility] EWS error: {exP.Message}");
            }
        }

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
                    loggerP.LogWarning("[BoosterCompatibility] EWD: ExpandWorldData.WorldInfo type not found.");
                    return;
                }

                _ewdWorldRadiusProp = AccessTools.Property(ewdWorldInfoType, "WorldRadius");
                _ewdWorldRadiusField = AccessTools.Field(ewdWorldInfoType, "WorldRadius");

                if (_ewdWorldRadiusProp == null && _ewdWorldRadiusField == null)
                {
                    loggerP.LogWarning("[BoosterCompatibility] EWD: WorldRadius property/field not found on WorldInfo.");
                    return;
                }

                IsExpandWorldDataActive = true;
                loggerP.LogInfo("[BoosterCompatibility] Expand World Data detected.");
            }
            catch (Exception exP)
            {
                loggerP.LogWarning($"[BoosterCompatibility] EWD reflection error: {exP.Message}");
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

        private static float? ReadEWDRadius()
        {
            try
            {
                if (_ewdWorldRadiusProp != null)
                {
                    return Convert.ToSingle(_ewdWorldRadiusProp.GetValue(null));
                }
                if (_ewdWorldRadiusField != null)
                {
                    return Convert.ToSingle(_ewdWorldRadiusField.GetValue(null));
                }
            }
            catch { }
            return null;
        }
    }
}