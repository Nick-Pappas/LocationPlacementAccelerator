// v5
/**
* Generation lifecycle management, placement counters, and UI text building.
* Coordinates between placement engines, diagnostics, and the progress overlay.
*
* Counter threading model:
*   _currentProcessed / _currentPlaced: written by worker threads via
*   Interlocked.Add, read by GUI thread each OnGUI frame. Volatile ensures
*   the GUI sees the latest value without needing a lock.
*   
* Could I have split this into multiple classes? Should I? Maybe. 
* But there's a lot of shared state and I don't want to risk synchronization bugs
* by scattering it across multiple classes. 
* One has to pick their battles.
* 
* v2: Added PlayabilityPolicy.Initialize() to dynamically load EWD YAML
* configurations at the start of generation. Updated progress overlay colors
* to respect FailureSeverity (Red/Orange/Yellow).
*
* v3: Snapshot pre-existing instance counts in StartGeneration so the EndGeneration
* tally only credits LPA for what LPA actually added. Without this the summary
* counts retained m_placed=true instances from prior generations as new placements,
* which produced ridiculous percentages (140%+) on genloc-on-saved-world runs.
* The snapshot is taken AFTER PlacementEngine.Run sweeps non-placed reservations,
* so it represents only the real player-visited structures we are preserving.
*
* v4: Swapped the _validLocations filter from strict m_prefab.IsValid to 
* Compatibility.IsValidLocation. EWD blueprint locations weren't counted in the
* progress overlay's denominator (m_totalRequested), so the overlay reported a
* lower "expected" count than what the engine was trying to place. This was 
* mostly cosmetic because the engine itself was also dropping them (see Core
* v1.0.4 / Parallel v1.0.4), but now that the engine accepts them the overlay
* needs to agree on the total.
*
* v5: Fixed the genloc-on-saved-world reporting lie. "Requested" was using 
* loc.m_quantity (world target) while "placed" was using GetActualPlacedCount 
* (this run's deltas) - apples-to-oranges. For a quota that was already fully 
* met (StartTemple, or any location that was placed and explored in a prior run), 
* CenterFirstPlacer/Interleaver correctly skipped it, but then the summary paired 
* requested=1 with placed=0 and printed "StartTemple: 0/1 Complete failure". 
* Now both numbers are on the same scale: requested-this-run = m_quantity minus
* preExisting, and locations whose quotas are already met are silently dropped
* from the summary tables (they would contribute 0/0 otherwise, which is noise).
* Also moved the _preExistingCounts snapshot above the _totalRequested sum so
* the overlay denominator reflects "what this run still needs to do".
* At this rate I will be v3googolplexes soon. 
*/
#nullable disable
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class GenerationProgress
    {
        private static bool _initialized = false;
        private static int _totalRequested = 0;
        private static volatile int _currentProcessed = 0;
        private static volatile int _currentPlaced = 0;
        private static string _modeName = "Vanilla";
        private static DateTime _startTime;

        private static bool _isSurveying = false;

        /**
        * Per-prefab snapshot of instance counts as they exist at the very start of
        * placement (after the non-placed sweep). The EndGeneration tally subtracts
        * these so the percentages and "placed N/M" strings reflect what LPA placed
        * this run, not what was already in the world from prior generations.
        */
        private static Dictionary<string, int> _preExistingCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        public static bool IsSurveying
        {
            get
            {
                return _isSurveying;
            }
        }

        public static ZoneSystem.ZoneLocation CurrentLocation = null;

        public static string StaticTopText = "";
        public static string StaticBottomText = "";

        private static volatile string[] _threadSlots = null;

        public static string[] ThreadSlots
        {
            get
            {
                return _threadSlots;
            }
        }

        public static int ThreadSlotCount
        {
            get
            {
                string[] slots = _threadSlots;
                if (slots == null)
                {
                    return 0;
                }
                return slots.Length;
            }
        }

        public static int TotalRequested
        {
            get
            {
                return _totalRequested;
            }
        }

        public static int CurrentProcessed
        {
            get
            {
                return _currentProcessed;
            }
        }

        public static int CurrentPlaced
        {
            get
            {
                return _currentPlaced;
            }
        }

        private static List<ZoneLocation> _validLocations = new List<ZoneLocation>();

        public static void InitThreadSlots(int countP)
        {
            _threadSlots = new string[countP];
        }

        // Workers call this to announce which prefab they're currently placing.
        // null = slot is idle. Each slot is owned by exactly one thread at a time.
        public static void SetThreadSlot(int slotIndexP, string prefabNameP)
        {
            string[] slots = _threadSlots;
            if (slots == null || slotIndexP < 0 || slotIndexP >= slots.Length)
            {
                return;
            }
            System.Threading.Volatile.Write(ref slots[slotIndexP], prefabNameP);
        }

        public static void ClearThreadSlots()
        {
            _threadSlots = null;
        }

        private static string BuildModeName()
        {
            bool legacy = ModConfig.EffectiveLegacy;
            PlacementMode mode = ModConfig.EffectiveMode;

            string modeStr;
            switch (mode)
            {
                case PlacementMode.Survey: modeStr = "Survey"; break;
                case PlacementMode.Filter: modeStr = "Filter"; break;
                case PlacementMode.Force: modeStr = "Force"; break;
                default: modeStr = "Vanilla"; break;
            }

            string engineLabel = legacy ? "Transpiled" : "Replaced";
            bool parallel = !legacy && ModConfig.EnableParallelPlacement.Value;
            if (parallel)
            {
                return $"{engineLabel} - {modeStr} - Parallel";
            }
            return $"{engineLabel} - {modeStr}";
        }

        public static void StartGeneration(ZoneSystem zsP)
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

            PlayabilityPolicy.Initialize();

            if (ModConfig.ShowGui.Value)
            {
                ProgressOverlay.EnsureInstance();
            }
            ConstraintRelaxer.Reset();

            DiagnosticLog.OpenLogFile();
            DiagnosticLog.OnWorldRadiusResolved();

            _modeName = BuildModeName();

            _validLocations.Clear();

            List<ZoneLocation> sourceList = zsP.m_locations;
            if (Interleaver.OriginalLocations != null)
            {
                sourceList = Interleaver.OriginalLocations;
            }
            foreach (ZoneLocation loc in sourceList)
            {
                // EWD-mirror: count blueprint locations in the "valid" set too. They
                // arrive with an empty AssetID + name-only SoftReference, which the
                // old m_prefab.IsValid check rejected - leaving them out of the 
                // progress overlay's denominator even when the engine actually tried
                // to place them. IsValidLocation matches EWD's own IdManager.IsValid.
                if (loc.m_enable && Compatibility.IsValidLocation(loc))
                {
                    _validLocations.Add(loc);
                }
            }

            /**
            * Snapshot the per-prefab counts that already exist in m_locationInstances
            * right now. PlacementEngine.Run has already swept non-placed reservations
            * so these are the genuine pre-existing placed structures from prior
            * generations. Both the overlay denominator (_totalRequested) and the
            * summary's per-location ratios subtract these so the numbers reflect
            * "what this run still needs to do" rather than "world target vs this 
            * run's deltas", which was reporting already-met quotas as 0/N failures.
            */
            _preExistingCounts.Clear();
            foreach (KeyValuePair<Vector2i, LocationInstance> kvp in zsP.m_locationInstances)
            {
                string prefabName = kvp.Value.m_location.m_prefabName;
                _preExistingCounts.TryGetValue(prefabName, out int existing);
                _preExistingCounts[prefabName] = existing + 1;
            }

            int total = 0;
            for (int i = 0; i < _validLocations.Count; i++)
            {
                ZoneLocation loc = _validLocations[i];
                _preExistingCounts.TryGetValue(loc.m_prefabName, out int preExisting);
                int needed = loc.m_quantity - preExisting;
                if (needed > 0)
                {
                    total += needed;
                }
            }
            _totalRequested = total;
            _currentProcessed = 0;
            _currentPlaced = 0;
            RelaxationTracker.Reset();

            UpdateText();
        }

        public static void MarkActualStart()
        {
            _startTime = DateTime.Now;
            DiagnosticLog.WriteTimestampedLog($"=== GLOBAL START: Generating Locations ({_modeName}) ===");

            if (ModConfig.EffectiveMode == PlacementMode.Survey)
            {
                WorldSurveyData.Initialize();
                SurveyMode.Initialize();
            }
        }

        /**
        * Replaced-engine variant: logs the start timestamp but does NOT run the survey.
        * The replaced engine drives WorldSurveyData.Initialize() itself as a background
        * Task so the main thread stays free to render the survey progress overlay.
        */
        public static void MarkActualStartNoSurvey()
        {
            _startTime = DateTime.Now;
            bool parallel = ModConfig.EnableParallelPlacement.Value
                         && !ModConfig.EffectiveLegacy;
            DiagnosticLog.WriteTimestampedLog($"=== GLOBAL START: Generating Locations ({_modeName}) ===");
            DiagnosticLog.WriteTimestampedLog($"  Multithreaded:        {(parallel ? "ON" : "OFF")}");
        }

        public static void BeginSurvey()
        {
            _isSurveying = true;
        }

        public static void EndSurvey()
        {
            _isSurveying = false;
        }

        public static void IncrementProcessed(bool successfullyPlacedP, int countP = 1)
        {
            _currentProcessed += countP;
            if (successfullyPlacedP)
            {
                _currentPlaced += countP;
            }
            UpdateText();
        }

        public static void IncrementAttempted(int countP)
        {
            System.Threading.Interlocked.Add(ref _currentProcessed, countP);
        }

        public static void IncrementPlaced(int countP)
        {
            System.Threading.Interlocked.Add(ref _currentPlaced, countP);
        }

        /**
        * Returns the number of instances of this prefab placed BY LPA THIS RUN.
        * Subtracts the pre-existing snapshot count taken at StartGeneration so
        * retained m_placed=true instances from previous generations are not
        * counted as new placements.
        */
        private static int GetActualPlacedCount(string prefabNameP)
        {
            if (ZoneSystem.instance == null)
            {
                return 0;
            }
            int total = 0;
            foreach (KeyValuePair<Vector2i, LocationInstance> kvp in ZoneSystem.instance.m_locationInstances)
            {
                if (kvp.Value.m_location.m_prefabName == prefabNameP)
                {
                    total++;
                }
            }
            _preExistingCounts.TryGetValue(prefabNameP, out int preExisting);
            int placedThisRun = total - preExisting;
            if (placedThisRun < 0)
            {
                placedThisRun = 0;
            }
            return placedThisRun;
        }

        public static void UpdateText()
        {
            if (ProgressOverlay.instance == null)
            {
                return;
            }

            RelaxationSnapshot snap = RelaxationTracker.GetSnapshot();

            /**
            * Text color scheme:
            * 
            * Green = no known failures, all placements successful so far.
            * Red = at least one known failure that has not yet been rescued.
            * Blue = all known failures have been rescued, but placement is still ongoing so more failures may be discovered.
            *
            * Blue fires as soon as all known failures are rescued - doesn't wait for
            * placement to complete so the full GUI turns blue the moment relaxation succeeds.
            */
            string color;
            if (snap.AnyRelaxationOccurred && !snap.AnyUnrescued)
            {
                color = "#55AAFF";
            }
            else if (snap.AnyUnrescued)
            {
                if (snap.HighestSeverity == FailureSeverity.Red)
                {
                    color = "#FF4444";
                }
                else if (snap.HighestSeverity == FailureSeverity.Orange)
                {
                    color = "#FFAA00";
                }
                else
                {
                    color = "#FFFF55";
                }
            }
            else
            {
                color = "#55FF55";
            }

            StringBuilder sbTop = new StringBuilder();
            sbTop.AppendLine($"<color={color}>");
            sbTop.AppendLine($"<size=28><b>Placing locations using {_modeName}</b></size>");
            StaticTopText = sbTop.ToString();

            StringBuilder sbBot = new StringBuilder();
            sbBot.Append("</color>");

            if (snap.Active.Count > 0)
            {
                sbBot.AppendLine("\n<color=#FF4444><size=22><b>FAILED - ATTEMPTING RELAXATION:</b></size>");
                foreach (string name in snap.Active)
                {
                    bool hasAttempts = snap.AttemptLog.TryGetValue(name, out List<string> attempts);
                    if (hasAttempts)
                    {
                        for (int i = 0; i < attempts.Count; i++)
                        {
                            sbBot.AppendLine($"<size=20>  {name}  {attempts[i]}</size>");
                        }
                    }
                    else
                    {
                        sbBot.AppendLine($"<size=20>  {name}</size>");
                    }
                }
                sbBot.Append("</color>");
            }

            if (snap.Succeeded.Count > 0)
            {
                sbBot.AppendLine("\n<color=#55AAFF><size=22><b>RELAXED CONSTRAINTS:</b></size>");
                foreach (string name in snap.Succeeded)
                {
                    ZoneLocation locData = null;
                    if (ZoneSystem.instance != null)
                    {
                        for (int i = 0; i < ZoneSystem.instance.m_locations.Count; i++)
                        {
                            if (ZoneSystem.instance.m_locations[i].m_prefabName == name)
                            {
                                locData = ZoneSystem.instance.m_locations[i];
                                break;
                            }
                        }
                    }
                    if (locData != null)
                    {
                        sbBot.AppendLine($"<size=20>  {name}  {ConstraintRelaxer.GetRelaxationSummary(name, locData)}</size>");
                    }
                }
                sbBot.Append("</color>");
            }

            if (snap.Exhausted.Count > 0)
            {
                sbBot.AppendLine("\n<color=#FF4444><size=22><b>FAILED - COULD NOT PLACE:</b></size>");
                foreach (string name in snap.Exhausted)
                {
                    bool hasCount = ConstraintRelaxer.RelaxationAttempts.TryGetValue(name, out int cnt);
                    int n = 0;
                    if (hasCount)
                    {
                        n = cnt;
                    }
                    sbBot.AppendLine($"<size=20>  {name}  (exhausted {n} relaxation attempts)</size>");
                }
                sbBot.Append("</color>");
            }

            StaticBottomText = sbBot.ToString();
        }

        public static void EndGeneration()
        {
            if (!_initialized)
            {
                return;
            }

            DateTime endTime = DateTime.Now;
            TimeSpan elapsedTime = endTime - _startTime;
            string timeString = $"{(int)elapsedTime.TotalMinutes}m {elapsedTime.Seconds}.{elapsedTime.Milliseconds / 100}s";

            DiagnosticLog.WriteBlankLine();
            DiagnosticLog.WriteTimestampedLog($"=== GLOBAL END: Generating Locations ({_modeName}) ===");
            DiagnosticLog.WriteBlankLine();

            if (ZoneSystem.instance != null)
            {
                Interleaver.RestoreLocations(ZoneSystem.instance);

                // Deduplicate m_locations in case relaxation packets created duplicates.
                HashSet<ZoneLocation> seen = new HashSet<ZoneLocation>();
                List<ZoneLocation> distinctList = new List<ZoneLocation>();
                for (int i = 0; i < ZoneSystem.instance.m_locations.Count; i++)
                {
                    if (seen.Add(ZoneSystem.instance.m_locations[i]))
                    {
                        distinctList.Add(ZoneSystem.instance.m_locations[i]);
                    }
                }
                ZoneSystem.instance.m_locations.Clear();
                ZoneSystem.instance.m_locations.AddRange(distinctList);
            }

            ConstraintRelaxer.RestoreQuantities();

            int totalActualPlaced = 0;
            Dictionary<string, int> finalCounts = new Dictionary<string, int>();
            foreach (ZoneLocation loc in _validLocations)
            {
                bool alreadyCounted = finalCounts.TryGetValue(loc.m_prefabName, out int existingCount);
                if (!alreadyCounted)
                {
                    int count = GetActualPlacedCount(loc.m_prefabName);
                    finalCounts[loc.m_prefabName] = count;
                    totalActualPlaced += count;
                }
            }

            List<string> completeFailures = new List<string>();
            List<string> partialFailures = new List<string>();
            List<string> missedNecessities = new List<string>();

            // Track which prefab names I've already processed to skip duplicates.
            HashSet<string> processedPrefabs = new HashSet<string>();
            foreach (ZoneLocation loc in _validLocations)
            {
                if (!processedPrefabs.Add(loc.m_prefabName))
                {
                    continue;
                }

                /**
                * Genloc fix: "requested" is what this run still needs to place, not 
                * the world target. If a quota was already met in a prior run (e.g. 
                * StartTemple sitting under the player's feet), CenterFirstPlacer / 
                * Interleaver correctly skip it, and the engine has nothing to do - 
                * that's not a failure. Without this subtraction the summary paired 
                * an un-deducted requested (N) with GetActualPlacedCount's deducted 
                * placed (0) and reported "0/N Complete failure" for healthy quotas.
                */
                _preExistingCounts.TryGetValue(loc.m_prefabName, out int preExisting);
                int requested = loc.m_quantity - preExisting;
                if (requested <= 0)
                {
                    continue;
                }

                bool hasPlaced = finalCounts.TryGetValue(loc.m_prefabName, out int placed);
                if (hasPlaced)
                {
                    if (placed == 0)
                    {
                        completeFailures.Add($"-{loc.m_prefabName} : {placed}/{requested}");
                        if (PlayabilityPolicy.IsNecessity(loc.m_prefabName))
                        {
                            missedNecessities.Add(loc.m_prefabName);
                        }
                    }
                    else if (placed < requested)
                    {
                        partialFailures.Add($"-{loc.m_prefabName} : {placed}/{requested}");
                    }
                }
            }

            string playabilityVerdict;
            LogLevel logLevel;

            List<KeyValuePair<string, int>> relaxedItems = new List<KeyValuePair<string, int>>();
            foreach (KeyValuePair<string, int> kvp in ConstraintRelaxer.RelaxationAttempts)
            {
                if (kvp.Value > 0)
                {
                    relaxedItems.Add(kvp);
                }
            }

            if (missedNecessities.Count > 0)
            {
                playabilityVerdict = "UNPLAYABLE";
                logLevel = LogLevel.Error;
            }
            else
            {
                playabilityVerdict = "Playable";
                logLevel = LogLevel.Info;
                if (relaxedItems.Count > 0)
                {
                    logLevel = LogLevel.Warning;
                }
            }

            int totalFailed = _totalRequested - totalActualPlaced;
            if (totalFailed < 0)
            {
                totalFailed = 0;
            }
            float successRate = 100f;
            if (_totalRequested > 0)
            {
                successRate = totalActualPlaced * 100f / _totalRequested;
            }

            StringBuilder summary = new StringBuilder();
            summary.AppendLine("=================================================");
            summary.AppendLine("===      WORLD GENERATION SUMMARY             ===");
            summary.AppendLine("=================================================");
            summary.AppendLine($"  Total Time:       {timeString}");
            summary.AppendLine($"  Total Requested:  {_totalRequested:N0}");
            summary.AppendLine($"  Total Placed:     {totalActualPlaced:N0}  ({successRate:F2}%)");
            summary.AppendLine($"  Total Failed:     {totalFailed:N0}");

            if (completeFailures.Count > 0)
            {
                summary.AppendLine("       ----------------");
                summary.AppendLine("        Complete failures:");
                for (int i = 0; i < completeFailures.Count; i++)
                {
                    summary.AppendLine($"        {completeFailures[i]}");
                }
            }
            if (partialFailures.Count > 0)
            {
                summary.AppendLine("       ----------------");
                summary.AppendLine("        Partial failures:");
                for (int i = 0; i < partialFailures.Count; i++)
                {
                    summary.AppendLine($"        {partialFailures[i]}");
                }
            }

            summary.AppendLine($"  Playability:      {playabilityVerdict}");

            if (relaxedItems.Count > 0)
            {
                summary.AppendLine("-------------------------------------------------");
                summary.AppendLine("  Relaxations Applied:");
                foreach (KeyValuePair<string, int> kvp in relaxedItems)
                {
                    ZoneLocation locData = null;
                    if (ZoneSystem.instance != null)
                    {
                        for (int i = 0; i < ZoneSystem.instance.m_locations.Count; i++)
                        {
                            if (ZoneSystem.instance.m_locations[i].m_prefabName == kvp.Key)
                            {
                                locData = ZoneSystem.instance.m_locations[i];
                                break;
                            }
                        }
                    }
                    if (locData != null)
                    {
                        summary.AppendLine($"  - {kvp.Key} {ConstraintRelaxer.GetRelaxationSummary(kvp.Key, locData)}");
                    }
                }
            }

            summary.AppendLine("=================================================");

            DiagnosticLog.WriteLog("\n" + summary.ToString().TrimEnd(), logLevel);

            ProgressOverlay.DestroyInstance();
            ThreadSafePRNG.Reset();
            WorldSurveyData.Reset();
            SurveyMode.Reset();

            /**
            * Re-arm the engine prefix so a second genloc in the same session actually 
            * runs again. Without this the first call latched _firstCallDone/_v2Started
            * and every subsequent call just got suppressed silently.
            */
            ReplacedEnginePatches.Reset();

            _initialized = false;
        }

        public static void ForceCleanup()
        {
            ProgressOverlay.DestroyInstance();
            _initialized = false;
        }
    }
}