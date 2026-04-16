// v1
/**
* BepInEx plugin entry point for Location Placement Accelerator.
* All configuration state lives in ModConfig. This class handles only
* Harmony patch application, config file watching, and lifecycle.
*/
#nullable disable
using BepInEx;
using HarmonyLib;
using System.IO;
using System.Reflection;

namespace LPA
{
    [BepInPlugin("nickpappas.locationplacementaccelerator", "Location Placement Accelerator", "1.0.0")]
    public class LPAPlugin : BaseUnityPlugin
    {
        private static Harmony _harmony;
        private static FileSystemWatcher _configWatcher;

        void Awake()
        {
            ModConfig.Initialize(Config, Logger);

            DiagnosticLog.Initialize(Info.Metadata.Version.ToString());

            _harmony = new Harmony("nickpappas.locationplacementaccelerator");

            if (ModConfig.EffectiveLegacy)
            {
                TranspiledEnginePatches.SkipTelemetry = ModConfig.MinimalLogging.Value;
                TranspiledEnginePatches.SkipAltTrack = ModConfig.MinimalLogging.Value;

                MethodInfo getRandomZoneMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetRandomZone));
                if (getRandomZoneMethod != null)
                {
                    _harmony.Patch(getRandomZoneMethod, prefix: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.GetRandomZonePrefix)));
                }

                _harmony.Patch(
                    AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.HaveLocationInRange), new[] { typeof(string), typeof(string), typeof(UnityEngine.Vector3), typeof(float), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.HaveLocationInRangePrefix)));

                if (!TranspiledEnginePatches.SkipTelemetry)
                {
                    _harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiome), new[] { typeof(UnityEngine.Vector3) }), postfix: new HarmonyMethod(typeof(TelemetryHelpers), nameof(TelemetryHelpers.CaptureWrongBiome)));
                    _harmony.Patch(AccessTools.Method(typeof(WorldGenerator), nameof(WorldGenerator.GetBiomeArea)), postfix: new HarmonyMethod(typeof(TelemetryHelpers), nameof(TelemetryHelpers.CaptureWrongBiomeArea)));
                }
            }

            MethodInfo logoutMethod = AccessTools.Method(typeof(Game), nameof(Game.Logout));
            if (logoutMethod != null)
            {
                _harmony.Patch(logoutMethod, prefix: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.OnGameLogout)));
                if (!ModConfig.EffectiveLegacy)
                {
                    _harmony.Patch(logoutMethod, prefix: new HarmonyMethod(typeof(ReplacedEnginePatches), nameof(ReplacedEnginePatches.Reset)));
                }
            }

            PatchCoroutines(ModConfig.EffectiveLegacy);

            MethodInfo minimapUpdate = AccessTools.Method(typeof(Minimap), "Update");
            if (minimapUpdate != null)
            {
                _harmony.Patch(minimapUpdate, prefix: new HarmonyMethod(typeof(MinimapParallelizer), nameof(MinimapParallelizer.Prefix)));
            }

            SetupConfigWatcher();

            DiagnosticLog.WriteLog($"[LPA] Initialized. Engine: {(ModConfig.EffectiveLegacy ? "Transpiled" : "Replaced")}. Mode: {ModConfig.EffectiveMode}. WorldRadius will be resolved before survey.");
        }

        void PatchCoroutines(bool legacyP)
        {
            System.Type zoneSystemType = typeof(ZoneSystem);
            System.Type[] nestedTypes = zoneSystemType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

            foreach (System.Type type in nestedTypes)
            {
                if (!type.Name.Contains("GenerateLocationsTimeSliced"))
                {
                    continue;
                }
                if (TranspiledEnginePatches.PatchedTypes.Contains(type.FullName))
                {
                    continue;
                }

                MethodInfo method = AccessTools.Method(type, "MoveNext");
                if (method == null)
                {
                    continue;
                }

                bool hasInner = TranspiledEnginePatches.ScanForInnerLoop(method);
                bool hasOuter = TranspiledEnginePatches.ScanForOuterLoop(method);

                if (!legacyP)
                {
                    if (hasOuter)
                    {
                        _harmony.Patch(method, prefix: new HarmonyMethod(typeof(ReplacedEnginePatches), nameof(ReplacedEnginePatches.OuterLoopV2Prefix)));
                        TranspiledEnginePatches.PatchedTypes.Add(type.FullName);
                    }
                }
                else
                {
                    if (hasOuter)
                    {
                        _harmony.Patch(method, prefix: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.OuterLoopPrefix)), postfix: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.OuterLoopPostfix)), transpiler: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.OuterLoopTranspiler)));
                        TranspiledEnginePatches.PatchedTypes.Add(type.FullName);
                    }
                    else if (hasInner)
                    {
                        _harmony.Patch(method, prefix: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.InnerLoopPrefix)), transpiler: new HarmonyMethod(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.InnerLoopTranspiler)));
                        TranspiledEnginePatches.PatchedTypes.Add(type.FullName);
                    }
                }
            }
        }

        private void SetupConfigWatcher()
        {
            _configWatcher = new FileSystemWatcher(Paths.ConfigPath, Path.GetFileName(Config.ConfigFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += OnConfigChanged;
        }

        private void OnConfigChanged(object senderP, FileSystemEventArgs eP)
        {
            if (eP.FullPath != Config.ConfigFilePath)
            {
                return;
            }
            Logger.LogInfo("Configuration file modified. Reloading.");
            Config.Reload();
        }

        void OnDestroy()
        {
            _configWatcher?.Dispose();
            DiagnosticLog.Dispose();
        }
    }
}
