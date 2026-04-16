// v1.1c
/**
* Static configuration holder for Location Placement Accelerator.
* All config entries, effective runtime values, and shared mod state live here.
* Initialized once from LPAPlugin.Awake() via Initialize().
* EASILY the most annoying and time consuming part of the mod to implement.
*/
#nullable disable
using BepInEx.Configuration;
using BepInEx.Logging;
using System.Collections.Generic;
using BepInEx;

namespace LPA
{
    public static class ModConfig
    {
        // Set once during Initialize() after the coercion chain. Use these for all behavioral decisions.
        // The ConfigEntry.Value properties reflect user intent.
        // These fields reflect what actually runs.
        public static PlacementMode EffectiveMode;
        public static bool EffectiveLegacy;

        public static ConfigEntry<PlacementMode> Mode;
        public static ConfigEntry<bool> ShowGui;

        public static ConfigEntry<bool> UseLegacyEngine;
        public static ConfigEntry<bool> EnableParallelPlacement;
        public static ConfigEntry<bool> EnableInterleavedScheduling;

        public static ConfigEntry<int> SurveyScanResolution;
        public static ConfigEntry<int> SurveyVisitLimit;

        public static ConfigEntry<float> OuterMultiplier;
        public static ConfigEntry<float> InnerMultiplier;

        public static ConfigEntry<int> MaxRelaxationAttempts;
        public static ConfigEntry<float> RelaxationMagnitude;

        public static ConfigEntry<bool> WriteToFile;
        public static ConfigEntry<bool> VerboseLogFileName;
        public static ConfigEntry<bool> LogSuccesses;
        public static ConfigEntry<bool> MinimalLogging;
        public static ConfigEntry<bool> DiagnosticMode;
        public static ConfigEntry<int> ProgressInterval;
        public static ConfigEntry<int> InnerProgressInterval;

        public static ConfigEntry<float> PresenceGridCellSize;
        public static ConfigEntry<bool> Enable3DSimilarityCheck;
        public static ConfigEntry<bool> OptimizePlacementChecks;

        public static float WorldRadius = 10000f;//the vanilla world radius which I realize I am defining every other file. 

        public static ManualLogSource Log;

        public static void Initialize(ConfigFile configP, ManualLogSource loggerP)
        {
            Log = loggerP;


            Mode = configP.Bind("1 - General", "PlacementMode", PlacementMode.Survey,
                "Controls the overall placement strategy.\n" +
                "\n" +
                "  Vanilla  - Unmodified Logic. The mod adds placement logging and smart\n" +
                "             recovery only. World generation is identical to vanilla.\n" +
                "  Filter   - Vanilla Logic + Spatial Constraints. Rejects candidate zones\n" +
                "             that fall outside each location's Min/Max distance ring.\n" +
                "             Requires the transpiled engine (see section 2).\n" +
                "  Force    - Vanilla Logic + Spatial Constraints. Mathematically generates\n" +
                "             candidate zones inside the distance ring rather than filtering.\n" +
                "             Requires the transpiled engine (see section 2).\n" +
                "  Survey   - Full engine. Pre-scans the world, builds sorted candidate lists\n" +
                "             per biome and altitude. Required for parallel placement.\n" +
                "             Recommended for all modded worlds.\n" +
                "\n" +
                "OVERRIDE: Parallel Placement (section 2) forces Survey regardless of this setting.\n" +
                "OVERRIDE: Filter and Force force the transpiled engine regardless of section 2 settings.\n" +
                "OVERRIDE: Vanilla forces the transpiled engine.");

            ShowGui = configP.Bind("1 - General", "ShowGui", true,
                "Show the on-screen placement progress overlay during world generation.\n" +
                "Set to false to disable the overlay entirely.");

            /**
            * Override chain (applied in order, top wins):
            *   1. Parallel Placement ON    --> forces Survey mode + replaced engine.
            *   2. Replaced engine selected --> forces Survey mode if not already Survey.
            *   3. Filter or Force mode     --> forces transpiled engine.
            *   4. Vanilla mode             --> forces transpiled engine.
            *
            * The effective values are written to ModConfig.EffectiveMode and ModConfig.EffectiveLegacy at startup.
            * The log file header shows what actually ran.
            * 
            * Now... TODO:
            * EVERY time I see this in my config it seems confusing even to me who wrote it.
            * This means that it is ridiculous and I should change it. 
            * My problem is that I cannot make the config dynamic, and so I have to explain the coercion rules
            * in the description, making everything a total noise mess. 
            * Fix when I resurect biome bucketing with the learning stuff.
            * Another thing for the perpetual refactoring pile. 
            */

            UseLegacyEngine = configP.Bind("2 - Engine", "UseLegacyEngine", false,
                "false - Replaced engine. Uses pre-built candidate lists and spatial exclusion\n" +
                "        grids for faster, higher-quality placement. Recommended.\n" +
                "true  - Transpiled engine. Full original patch surface. Required for Filter\n" +
                "        and Force modes.\n" +
                "\n" +
                "OVERRIDE: Parallel Placement ON forces the replaced engine regardless of this setting.\n" +
                "OVERRIDE: Filter, Force, or Vanilla mode forces the transpiled engine regardless of this setting.\n" +
                "Changing this requires a full game restart.");

            EnableParallelPlacement = configP.Bind("2 - Engine", "EnableParallelPlacement", true,
                "Enables multi-threaded placement. Multiple location types are placed\n" +
                "simultaneously, dramatically reducing world generation time on multi-core\n" +
                "systems.\n" +
                "\n" +
                "ON by default. When ON:\n" +
                "  - Forces Survey mode regardless of the PlacementMode setting.\n" +
                "  - Forces the replaced engine regardless of the UseLegacyEngine setting.\n" +
                "  - Eliminates determinism: each generation run of the same seed produces a different placement\n" +
                "    layout (placement order is no longer fixed).\n" +
                "\n" +
                "Changing this requires a full game restart.");

            EnableInterleavedScheduling = configP.Bind("2 - Engine", "EnableInterleavedScheduling", false,
                "Splits each location type's total quantity into individual work packets\n" +
                "and interleaves them across all types, rather than placing all of one type\n" +
                "before moving to the next.\n" +
                "\n" +
                "Can reduce spatial clustering when many of the same type compete for the\n" +
                "same regions. OFF by default.\n" +
                "\n" +
                "WARNING: Changes the deterministic layout of the world.");


            SurveyScanResolution = configP.Bind("3 - Placement", "ScanResolution", 1,
                "Number of sample points per side of each zone during the pre-scan.\n" +
                "Only applies in Survey mode.\n" +
                "\n" +
                "  1 - One dart at the zone center. Fastest. Default.\n" +
                "  3 - 3x3 = 9 darts spread across the zone.\n" +
                "  5 - 5x5 = 25 darts.\n" +
                "\n" +
                "Must be an odd number (sampling is balanced around the center).\n" +
                "Even values are rounded up to the nearest odd number automatically.\n" +
                "Higher values improve altitude and biome accuracy at the cost of scan time.");

            SurveyVisitLimit = configP.Bind("3 - Placement", "VisitLimit", 1,
                "Number of passes through the candidate zone list per placement attempt.\n" +
                "Only applies in Survey mode.\n" +
                "\n" +
                "At 1, each candidate zone is examined at most once per location type.\n" +
                "Higher values allow re-examination of zones already visited, which can\n" +
                "improve placement success in constrained worlds at the cost of additional\n" +
                "time. Works together with the Iteration Budget settings (section 4).");

            OuterMultiplier = configP.Bind("4 - Iteration Budgets", "OuterLoopMultiplier", 1.0f,
                "Multiplies the maximum number of candidate zones examined before giving up\n" +
                "on a single placement attempt. Applies in all modes.\n" +
                "\n" +
                "1.0 = vanilla budget. 2.0 = twice as many zones examined.\n" +
                "0.5 = half the budget. 0.0 = disables placement entirely.\n" +
                "Accepts decimals.");

            InnerMultiplier = configP.Bind("4 - Iteration Budgets", "InnerLoopMultiplier", 1.0f,
                "Multiplies the maximum number of placement attempts within a single zone.\n" +
                "Applies in all modes.\n" +
                "\n" +
                "1.0 = vanilla budget. 0.0 = disables placement entirely.\n" +
                "Accepts decimals.\n" +
                "\n" +
                "Note: VisitLimit and these budgets interact. A high VisitLimit with a high\n" +
                "OuterLoopMultiplier can significantly extend placement time.");


            MaxRelaxationAttempts = configP.Bind("5 - Smart Recovery", "MaxRelaxationAttempts", 4,
                "When a critical location type cannot be placed, the engine automatically\n" +
                "relaxes its placement constraints and retries. This setting controls how\n" +
                "many retry attempts are made before giving up.\n" +
                "\n" +
                "0 = disables smart recovery entirely.\n" +
                "\n" +
                "Critical types covered (must place at least 1):\n" +
                "  Eikthyrnir, GDKing, Bonemass, Dragonqueen, GoblinKing,\n" +
                "  Mistlands_DvergrBossEntrance1, FaderLocation, Vendor_BlackForest,\n" +
                "  Hildir_camp, BogWitch_Camp, Hildir_crypt, Hildir_cave,\n" +
                "  Hildir_plainsfortress\n" +
                "\n" +
                "Important types covered (must place at least 50%):\n" +
                "  Crypt, SunkenCrypt, MountainCave, InfestedMine, TarPit, CharredFortress");

            RelaxationMagnitude = configP.Bind("5 - Smart Recovery", "RelaxationMagnitude", 0.05f,
                "Fraction of each original placement constraint to relax per retry attempt.\n" +
                "0.05 = 5% per attempt. At 4 attempts, up to 20% total relaxation.\n" +
                "Increase if smart recovery is failing to find placements.");


            WriteToFile = configP.Bind("6 - Logging", "WriteToFile", true,
                "Write the generation log to a file in the BepInEx folder.\n" +
                "The file contains the full run configuration and per-location placement\n" +
                "results. Useful for diagnosing placement failures.");

            VerboseLogFileName = configP.Bind("6 - Logging", "VerboseLogFileName", false,
                "Controls the name of the log file written each run.\n" +
                "\n" +
                "false - Log file is always named LocationPlacementAccelerator.log.\n" +
                "        Overwrites the previous run's file each time. Default.\n" +
                "true  - Log file name includes a fingerprint of the run configuration\n" +
                "        and a timestamp. Multiple runs produce separate files.");

            LogSuccesses = configP.Bind("6 - Logging", "LogSuccess", false,
                "Log each successfully placed location type to the console.\n" +
                "OFF by default. Failures and smart recovery events are always logged.");

            MinimalLogging = configP.Bind("6 - Logging", "MinimalLogging", false,
                "When ON, suppresses per-location placement detail in the log file.\n" +
                "The run configuration header and final world summary are always written.\n" +
                "\n" +
                "NOTE: Changing this requires a full game restart.");

            DiagnosticMode = configP.Bind("6 - Logging", "DiagnosticMode", false,
                "Enables verbose diagnostic output. Intended for mod development.\n" +
                "Leave OFF unless investigating a specific problem.");

            ProgressInterval = configP.Bind("6 - Logging", "ProgressInterval", 0,
                "Log a heartbeat every N zone candidates examined (outer loop).\n" +
                "0 = disabled. Only meaningful when DiagnosticMode is ON.");

            InnerProgressInterval = configP.Bind("6 - Logging", "InnerProgressInterval", 0,
                "Log a heartbeat every N placement attempts within a zone (inner loop).\n" +
                "0 = disabled. Only meaningful when DiagnosticMode is ON.");


            PresenceGridCellSize = configP.Bind("7 - Advanced", "PresenceGridCellSize", 16f,
                "Only applies when using the replaced engine.\n" +
                "\n" +
                "Cell size in metres for the spatial exclusion grid used to enforce minimum\n" +
                "distances between similar location types.\n" +
                "\n" +
                "Smaller values = finer resolution = higher memory use.\n" +
                "Minimum: 4m. Default 16m uses approximately 4.9 MB per location group\n" +
                "at maximum world radius.");

            Enable3DSimilarityCheck = configP.Bind("7 - Advanced", "Enable3DSimilarityCheck", false,
                "Only applies when using the replaced engine.\n" +
                "\n" +
                "When ON, Mountain and Mistlands similarity checks use 3D distance\n" +
                "verification when the 2D exclusion grid reports a conflict. This reduces\n" +
                "false positives in high-relief terrain at the cost of additional lookups.\n" +
                "\n" +
                "OFF by default. The 2D grid is conservative (never misses real conflicts)\n" +
                "so this only affects placement success rates in mountainous terrain.");

            OptimizePlacementChecks = configP.Bind("7 - Advanced", "OptimizePlacementChecks", true,
                "Only applies in Vanilla mode.\n" +
                "\n" +
                "When ON (default), two performance optimizations remain active even in Vanilla mode:\n" +
                "  - HaveLocationInRange uses a zone-neighborhood lookup (O(K)) instead of a\n" +
                "    full world scan (O(N)).\n" +
                "  - The inner placement loop checks similarity before terrain delta, saving\n" +
                "    terrain height samples on darts that would fail similarity anyway.\n" +
                "\n" +
                "Both optimizations are correctness-neutral: placement results are identical to\n" +
                "unmodified Valheim. Leave ON unless benchmarking against true vanilla.");

            /**
            * Purge obsolete config keys from previous versions so the .cfg file
            * stays clean. ConfigFile.Remove is no op for keys that don't exist.
            * Do not worry guys I will be bringing you back.
            */
            List<ConfigDefinition> configKeys = new List<ConfigDefinition>(configP.Keys);
            foreach (ConfigDefinition key in configKeys)
            {
                if (key.Section == "DistanceFilter" || key.Section == "Performance" || key.Section == "2 - Survey Strategy" ||
                    key.Section == "4 - Performance" || key.Section == "5 - Logging" || (key.Section == "Strategy" && key.Key == "TargetLocation") ||
                    key.Key == "WorldRadius" || key.Key == "PruningKnowledgeThreshold" || key.Key == "ActivePreset" ||
                    key.Key == "EnableAltitudeMapping" || key.Key == "AltitudeMappingAllBiomes" ||
                    key.Key == "EnableRuntimeAltitudeEnrichment" || key.Key == "EnableRuntimeBiomeEnrichment" ||
                    key.Key == "MaxAttempts" ||
                    key.Key == "BucketingMode" || key.Key == "AltitudeMappingScope" ||
                    key.Key == "ExplorationRate" || key.Key == "AltitudeInferenceGap" ||
                    key.Key == "RuntimeEnrichment" || key.Section == "8 - Learning" ||
                    key.Key == "EnableAdvancedPruning" ||
                    key.Key == "BiomeConfidenceThreshold" || key.Section == "8 - Survey Tuning" ||
                    key.Key == "SubBiomeFiltering")
                {
                    configP.Remove(key);
                }
            }

            {
                int raw = SurveyScanResolution.Value;
                int clamped = raw;
                if (raw < 1)
                {
                    clamped = 1;
                }
                else if (raw % 2 == 0)
                {
                    clamped = raw + 1;
                }
                if (clamped != raw)
                {
                    SurveyScanResolution.Value = clamped;
                    Log.LogWarning($"[LPA] ScanResolution {raw} is not a valid odd integer. Corrected to {clamped}.");
                }
            }

            /**
            * Applied top-down. Higher rules override lower ones with no conflicts possible.
            * Results written to EffectiveMode and EffectiveLegacy.
            * The log file configuration header shows what actually ran.
            */
            EffectiveMode = Mode.Value;
            EffectiveLegacy = UseLegacyEngine.Value;

            // Rule 1: Parallel placement forces Survey + replaced engine (highest priority).
            if (EnableParallelPlacement.Value)
            {
                EffectiveMode = PlacementMode.Survey;
                EffectiveLegacy = false;
            }

            // Rule 2: Replaced engine requires Survey mode.
            if (!EffectiveLegacy && EffectiveMode != PlacementMode.Survey)
            {
                EffectiveMode = PlacementMode.Survey;
            }

            // Rule 3: Filter and Force require the transpiled engine.
            if (EffectiveMode == PlacementMode.Filter || EffectiveMode == PlacementMode.Force)
            {
                EffectiveLegacy = true;
            }

            // Rule 4: Vanilla requires the transpiled engine.
            if (EffectiveMode == PlacementMode.Vanilla)
            {
                EffectiveLegacy = true;
            }
        }
    }
}