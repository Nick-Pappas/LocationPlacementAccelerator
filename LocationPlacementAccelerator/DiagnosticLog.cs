// v1.1c
/**
* Log file management and writing infrastructure.
* WriteLog/WriteTimestampedLog are the primary logging entry points used
* throughout the codebase. OpenLogFile creates the per-run log file.
* WriteConfigHeader writes the run configuration block at file open time.
*/
#nullable disable
using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LPA
{
    public static class DiagnosticLog
    {
        private static StreamWriter _logWriter;
        private static readonly object _logLock = new object();

        public static bool MinimalLogging = false;

        private static string _runVersion = "000";
        private static string _runFingerprint = "";

        public static void Initialize(string versionP)
        {
            Compatibility.Initialize(ModConfig.Log);
            _runVersion = versionP.Replace(".", "");
        }

        public static string BuildFingerprint(string versionP = null)
        {
            if (versionP != null)
            {
                _runVersion = versionP.Replace(".", "");
            }

            PlacementMode mode = ModConfig.EffectiveMode;
            bool legacy = ModConfig.EffectiveLegacy;
            string enginePrefix = legacy ? "Transpiled" : "Replaced";

            string modeName;
            switch (mode)
            {
                case PlacementMode.Survey: modeName = "Survey"; break;
                case PlacementMode.Filter: modeName = "Filter"; break;
                case PlacementMode.Force: modeName = "Force"; break;
                default: modeName = "Vanilla"; break;
            }

            System.Text.StringBuilder parts = new System.Text.StringBuilder();
            parts.Append($"LPA_{enginePrefix}_{modeName}");

            if (!legacy && ModConfig.EnableParallelPlacement.Value)
            {
                parts.Append("_MT");
            }
            if (!legacy && ModConfig.EnableInterleavedScheduling.Value)
            {
                parts.Append("_Interleaved");
            }

            if (mode == PlacementMode.Survey)
            {
                int res = ModConfig.SurveyScanResolution.Value;
                parts.Append($"_{res}x");
            }

            float outer = ModConfig.OuterMultiplier.Value;
            float inner = ModConfig.InnerMultiplier.Value;
            if (Mathf.Abs(outer - 1f) > 0.001f)
            {
                parts.Append($"_Outer{outer:G}x");
            }
            if (Mathf.Abs(inner - 1f) > 0.001f)
            {
                parts.Append($"_Inner{inner:G}x");
            }
            int relax = ModConfig.MaxRelaxationAttempts.Value;
            if (relax > 0)
            {
                parts.Append($"_Relax{relax}");
            }

            string ts = System.DateTime.Now.ToString("HHmm");
            parts.Append($"_{ts}");

            _runFingerprint = parts.ToString();
            return _runFingerprint;
        }

        public static string RunFingerprint
        {
            get
            {
                return _runFingerprint;
            }
        }

        public static void OpenLogFile()
        {
            MinimalLogging = ModConfig.MinimalLogging.Value;

            if (!ModConfig.WriteToFile.Value)
            {
                return;
            }
            try
            {
                _logWriter?.Close();
                _logWriter = null;

                string logPath;
                if (ModConfig.VerboseLogFileName.Value)
                {
                    string fingerprint = BuildFingerprint(_runVersion);
                    logPath = Path.Combine(Paths.BepInExRootPath, fingerprint + ".log");
                }
                else
                {
                    BuildFingerprint(_runVersion);
                    logPath = Path.Combine(Paths.BepInExRootPath, "LocationPlacementAccelerator.log");
                }

                _logWriter = new StreamWriter(logPath, false) { AutoFlush = true };
                WriteConfigHeader(_runVersion);
            }
            catch { }
        }

        private static void WriteConfigHeader(string versionP)
        {
            if (_logWriter == null)
            {
                return;
            }

            string engineLabel = ModConfig.EffectiveLegacy ? "Transpiled" : "Replaced";

            bool isSurvey = ModConfig.EffectiveMode == PlacementMode.Survey;

            _logWriter.WriteLine($"=== Location Placement Accelerator v{versionP} ===");
            _logWriter.WriteLine($"=== Run Configuration ===");
            _logWriter.WriteLine($"  Engine:               {engineLabel}");
            _logWriter.WriteLine($"  Mode:                 {ModConfig.EffectiveMode}");
            _logWriter.WriteLine($"  World Radius:         {Compatibility.DetectedWorldRadius:F0}m [{Compatibility.WorldRadiusSource}]");

            if (isSurvey)
            {
                _logWriter.WriteLine($"  Scan Resolution:      {ModConfig.SurveyScanResolution.Value}x{ModConfig.SurveyScanResolution.Value}");
                _logWriter.WriteLine($"  Visit Limit:          {ModConfig.SurveyVisitLimit.Value}");
            }

            if (!ModConfig.EffectiveLegacy)
            {
                _logWriter.WriteLine($"  Multithreaded:        {(ModConfig.EnableParallelPlacement.Value ? "ON" : "OFF")}");
                _logWriter.WriteLine($"  Interleaved:          {(ModConfig.EnableInterleavedScheduling.Value ? "ON" : "OFF")}");
                _logWriter.WriteLine($"  PresenceGrid Cell:    {ModConfig.PresenceGridCellSize.Value}m");
            }

            _logWriter.WriteLine($"  Outer Multiplier:     {ModConfig.OuterMultiplier.Value}x");
            _logWriter.WriteLine($"  Inner Multiplier:     {ModConfig.InnerMultiplier.Value}x");
            _logWriter.WriteLine($"  Relaxation:           {ModConfig.MaxRelaxationAttempts.Value} attempts @ {ModConfig.RelaxationMagnitude.Value * 100f:F0}%/step");

            _logWriter.WriteLine($"  Diagnostic Mode:      {(ModConfig.DiagnosticMode.Value ? "ON" : "OFF")}");
            _logWriter.WriteLine($"  Better Continents:    {(Compatibility.IsBetterContinentsActive ? "Detected" : "Not present")}");
            _logWriter.WriteLine($"  Expand World Size:    {(Compatibility.IsExpandWorldSizeActive ? "Detected" : "Not present")}");
            _logWriter.WriteLine($"  Expand World Data:    {(Compatibility.IsExpandWorldDataActive ? "Detected" : "Not present")}");
            _logWriter.WriteLine($"==========================================");
        }

        public static void OnWorldRadiusResolved()
        {
            float radius = Compatibility.RefreshWorldRadius(ModConfig.Log);
            ModConfig.WorldRadius = radius;
            WriteLog($"[LPA] World radius resolved: {radius:F0}m [{Compatibility.WorldRadiusSource}]");
            if (Compatibility.IsExpandWorldSizeActive || Compatibility.IsExpandWorldDataActive)
            {
                WriteLog("[LPA] Modded world size detected. Location quantities reflect any external multipliers.");
            }
        }

        public static void Dispose()
        {
            _logWriter?.Close();
        }

        public static void WriteBlankLine()
        {
            lock (_logLock)
            {
                _logWriter?.WriteLine("");
            }
        }

        public static void DumpPlacementsToFile()
        {
            if (ZoneSystem.instance == null)
            {
                return;
            }
            if (!ModConfig.WriteToFile.Value || !ModConfig.DiagnosticMode.Value)
            {
                return;
            }

            try
            {
                string path = Path.Combine(Paths.BepInExRootPath, _runFingerprint + ".placements");
                using (StreamWriter writer = new StreamWriter(path, false))
                {
                    writer.WriteLine($"=== Final Location Placements - {_runFingerprint} ===");
                    Dictionary<string, int> counters = new Dictionary<string, int>();

                    //List<ZoneSystem.LocationInstance> sorted = new List<ZoneSystem.LocationInstance>(ZoneSystem.instance.m_locationInstances.Values);
                    //sorted.Sort((ZoneSystem.LocationInstance aP, ZoneSystem.LocationInstance bP) =>
                    //    string.Compare(aP.m_location.m_prefabName, bP.m_location.m_prefabName, StringComparison.Ordinal)); //reading this a week later has me scratching my head, so I need to rewrite it. I ll do it later in the refactor.


                    List<ZoneSystem.LocationInstance> sorted = new List<ZoneSystem.LocationInstance>(ZoneSystem.instance.m_locationInstances.Values);
                    sorted.Sort(CompareLocationInstancesByName);

                    for (int i = 0; i < sorted.Count; i++)
                    {
                        ZoneSystem.LocationInstance inst = sorted[i];
                        string name = inst.m_location.m_prefabName;
                        bool hasCount = counters.TryGetValue(name, out int count);
                        if (!hasCount)
                        {
                            count = 0;
                        }
                        count++;
                        counters[name] = count;
                        Vector3 pos = inst.m_position;
                        writer.WriteLine($"{name}_{count} ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                    }
                }
                WriteLog($"[LPA] Dumped {ZoneSystem.instance.m_locationInstances.Count} placements to {_runFingerprint}.placements");
            }
            catch (Exception exP)
            {
                WriteLog($"[LPA] Failed to dump placements: {exP.Message}", LogLevel.Error);
            }
        }

        // Alphabetical sort by prefab name for the placement dump file.
        private static int CompareLocationInstancesByName(ZoneSystem.LocationInstance aP, ZoneSystem.LocationInstance bP)
        {
            return string.Compare(aP.m_location.m_prefabName, bP.m_location.m_prefabName, StringComparison.Ordinal);
        }

        public static void WriteLog(string messageP, LogLevel levelP = LogLevel.Info)
        {
            lock (_logLock)
            {
                ModConfig.Log.Log(levelP, messageP);
                _logWriter?.WriteLine($"[{levelP}] {messageP}");
            }
        }

        public static void WriteTimestampedLog(string messageP, LogLevel levelP = LogLevel.Info)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string msg = $"[{ts}] {messageP}";
            lock (_logLock)
            {
                ModConfig.Log.Log(levelP, msg);
                _logWriter?.WriteLine($"[{levelP}]{msg}");
            }
        }
    }
}