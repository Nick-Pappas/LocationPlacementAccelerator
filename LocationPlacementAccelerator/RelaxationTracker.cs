// v2
/**
* Runtime tracking of which vital location types have been relaxed, rescued,
* or exhausted during placement. Thread-safe, workers mutate under lock,
* GUI thread reads snapshots via GetSnapshot().
* 
* v2: Added severity tracking. Records FailureSeverity per prefab based on
* priority and uniqueness policies, computing the highest active severity 
* during snapshot generation.
*/
#nullable disable
using System.Collections.Generic;

namespace LPA
{
    public static class RelaxationTracker
    {
        // _relaxationFailed:      vital types that placed below threshold (drives Red).
        // _relaxationSucceeded:   vital types rescued by relaxation (drives Blue on complete).
        // _relaxationExhausted:   vital types that ran out of attempts without rescue (stays Red).
        // _relaxationAttemptLog:  per-prefab ordered list of attempt descriptions (live bottom panel).
        // _placementComplete:     set once all placement work is done (enables Blue transition).
        private static HashSet<string> _relaxationFailed = new HashSet<string>();
        private static HashSet<string> _relaxationSucceeded = new HashSet<string>();
        private static HashSet<string> _relaxationExhausted = new HashSet<string>();
        private static Dictionary<string, List<string>> _relaxationAttemptLog = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        private static Dictionary<string, FailureSeverity> _failureSeverities = new Dictionary<string, FailureSeverity>(System.StringComparer.Ordinal);
        private static bool _placementComplete = false;

        private static readonly object _relaxationLock = new object();

        // Called when a vital type is attempting relaxation. Marks it as failed (drives Red) and records the attempt description for the live panel.
        public static void MarkRelaxationAttempt(string prefabNameP, string attemptDescriptionP, bool isPrioritizedP)
        {
            lock (_relaxationLock)
            {
                _relaxationFailed.Add(prefabNameP);
                _failureSeverities[prefabNameP] = PlayabilityPolicy.GetSeverity(prefabNameP, isPrioritizedP);

                bool hasLog = _relaxationAttemptLog.TryGetValue(prefabNameP, out List<string> log);
                if (!hasLog)
                {
                    log = new List<string>();
                    _relaxationAttemptLog[prefabNameP] = log;
                }
                log.Add(attemptDescriptionP);
            }
            GenerationProgress.UpdateText();
        }

        // Called when all relaxation attempts are exhausted without meeting the threshold. Type stays Red permanently.
        // Should I make it go back to main menu? Have it as a config option perhaps? Like the 340th config option?
        public static void MarkRelaxationExhausted(string prefabNameP)
        {
            lock (_relaxationLock)
            {
                _relaxationFailed.Add(prefabNameP);
                _relaxationExhausted.Add(prefabNameP);
            }
            GenerationProgress.UpdateText();
        }

        // Called when a vital type that went through relaxation ultimately meets its threshold. Moves it from failure to rescued.
        public static void MarkRelaxationSucceeded(string prefabNameP)
        {
            lock (_relaxationLock)
            {
                _relaxationSucceeded.Add(prefabNameP);
            }
            GenerationProgress.UpdateText();
        }

        public static bool IsRelaxationSucceeded(string prefabNameP)
        {
            lock (_relaxationLock)
            {
                return _relaxationSucceeded.Contains(prefabNameP);
            }
        }

        // Called from FlushLTS on any non-success. Marks the type as failed if PlayabilityPolicy says it still needs more placements.
        public static void CheckAndMarkFailed(string prefabNameP, int placedP, int requestedP, bool isPrioritizedP)
        {
            if (PlayabilityPolicy.NeedsRelaxation(prefabNameP, placedP, requestedP))
            {
                lock (_relaxationLock)
                {
                    _relaxationFailed.Add(prefabNameP);
                    _failureSeverities[prefabNameP] = PlayabilityPolicy.GetSeverity(prefabNameP, isPrioritizedP);
                }
                GenerationProgress.UpdateText();
            }
        }

        // Called just before EndGeneration(). Signals that all placement work is done. Enables the Green-->Blue transition if all failures were rescued.
        public static void MarkPlacementComplete()
        {
            _placementComplete = true;
            GenerationProgress.UpdateText();
        }

        public static int FailedCount
        {
            get
            {
                return _relaxationFailed.Count;
            }
        }

        // Thread-safe snapshot of all relaxation state for UpdateText consumption.
        public static RelaxationSnapshot GetSnapshot()
        {
            lock (_relaxationLock)
            {
                bool anyUnrescued = false;
                FailureSeverity highestSeverity = FailureSeverity.Green;

                foreach (string failed in _relaxationFailed)
                {
                    if (!_relaxationSucceeded.Contains(failed))
                    {
                        anyUnrescued = true;
                        bool hasSeverity = _failureSeverities.TryGetValue(failed, out FailureSeverity sev);
                        if (hasSeverity && sev > highestSeverity)
                        {
                            highestSeverity = sev;
                        }
                    }
                }

                List<string> active = new List<string>();
                if (!_placementComplete)
                {
                    foreach (string failed in _relaxationFailed)
                    {
                        if (!_relaxationSucceeded.Contains(failed) && !_relaxationExhausted.Contains(failed))
                        {
                            active.Add(failed);
                        }
                    }
                }

                List<string> succeeded = new List<string>(_relaxationSucceeded);
                List<string> exhausted = new List<string>(_relaxationExhausted);

                return new RelaxationSnapshot
                {
                    AnyUnrescued = anyUnrescued,
                    Active = active,
                    Succeeded = succeeded,
                    Exhausted = exhausted,
                    AttemptLog = new Dictionary<string, List<string>>(_relaxationAttemptLog, System.StringComparer.Ordinal),
                    AnyRelaxationOccurred = _relaxationFailed.Count > 0,
                    HighestSeverity = highestSeverity
                };
            }
        }

        public static void Reset()
        {
            lock (_relaxationLock)
            {
                _relaxationFailed.Clear();
                _relaxationSucceeded.Clear();
                _relaxationExhausted.Clear();
                _relaxationAttemptLog.Clear();
                _failureSeverities.Clear();
                _placementComplete = false;
            }
        }
    }
}