// v2
/**
* Defines which location types are vital for a playable world and their
* minimum placement thresholds. Pure data + pure functions - no state.
* Necessities (bosses, vendors, quest camps) must place at least once.
* Secondary goals (dungeons, tarpits) have a fractional fill-rate floor.
* 
* I really should make this ewd configurable. Read the yaml and config there
* both thresholds and necessity vs secondary classification. 
* For now hardcoded is fine for my purposes and I want to get this out.
* 
* v2: Added YAML-driven relaxation overrides. Reads expand_locations*.yaml 
* from EWD's config folder. Overrides merge over hardcoded defaults using a 
* last-write-wins (alphabetical file sort) strategy. Added severity tracking
* to color-code UI failures based on priority and uniqueness.
*/
#nullable disable
using System.Collections.Generic;
using UnityEngine;
using YamlDotNet.Serialization;
using BepInEx;

namespace LPA
{
    public static class PlayabilityPolicy
    {
        public class LocationYamlOverride
        {
            public string prefab { get; set; }
            public bool? relaxable { get; set; }
            public bool? relaxableunique { get; set; }
            public float? relaxableamount { get; set; }
        }

        private struct EffectivePolicy
        {
            public bool IsDisabled;
            public bool IsUnique;
            public bool IsRelaxable;
            public float Amount;
        }

        private static readonly HashSet<string> _necessities = new HashSet<string>
        {
            "Eikthyrnir", "GDKing", "Bonemass", "Dragonqueen", "GoblinKing",
            "Mistlands_DvergrBossEntrance1", "FaderLocation", "Vendor_BlackForest",
            "Hildir_camp", "BogWitch_Camp", "Hildir_crypt", "Hildir_cave", "Hildir_plainsfortress"
        };

        private static readonly Dictionary<string, float> _secondaryGoals = new Dictionary<string, float>
        {
            { "Crypt", 0.5f }, { "SunkenCrypt", 0.5f }, { "MountainCave", 0.5f },
            { "InfestedMine", 0.5f }, { "TarPit", 0.5f }, { "CharredFortress", 0.5f }
        };// Making everything 50% sounds good. The problem is that I need to play the game more to know what is reasonable. 
        // Asking the kids, is useless as they start a new world every couple of days. I should ask on discord perhaps. The solution is EWD yaml really. 

        private static Dictionary<string, LocationYamlOverride> _yamlOverrides = new Dictionary<string, LocationYamlOverride>(System.StringComparer.Ordinal);

        public static void Initialize()
        {
            _yamlOverrides.Clear();

            try
            {
                string dir = System.IO.Path.Combine(Paths.ConfigPath, "expand_world");
                if (!System.IO.Directory.Exists(dir))
                {
                    return;
                }

                string[] files = System.IO.Directory.GetFiles(dir, "expand_locations*.yaml");
                System.Array.Sort(files);

                IDeserializer deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .Build();

                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    string yaml = System.IO.File.ReadAllText(file);
                    List<LocationYamlOverride> parsed = deserializer.Deserialize<List<LocationYamlOverride>>(yaml);

                    if (parsed != null)
                    {
                        for (int j = 0; j < parsed.Count; j++)
                        {
                            LocationYamlOverride loc = parsed[j];
                            if (!string.IsNullOrEmpty(loc.prefab))
                            {
                                _yamlOverrides[loc.prefab] = loc;
                            }
                        }
                    }
                }

                DiagnosticLog.WriteLog($"[PlayabilityPolicy] Loaded {_yamlOverrides.Count} location overrides from EWD YAMLs.");
            }
            catch (System.Exception exP)
            {
                DiagnosticLog.WriteLog($"[PlayabilityPolicy] Failed to load EWD YAMLs: {exP.Message}", BepInEx.Logging.LogLevel.Warning);
            }
        }

        private static EffectivePolicy GetEffectivePolicy(string prefabNameP)
        {
            bool hasOverride = _yamlOverrides.TryGetValue(prefabNameP, out LocationYamlOverride yaml);
            if (hasOverride)
            {
                if (yaml.relaxable.HasValue && !yaml.relaxable.Value)
                {
                    return new EffectivePolicy
                    {
                        IsDisabled = true,
                        IsUnique = false,
                        IsRelaxable = false,
                        Amount = 0f
                    };
                }

                bool isUnique = _necessities.Contains(prefabNameP);
                if (yaml.relaxableunique.HasValue)
                {
                    isUnique = yaml.relaxableunique.Value;
                }

                bool isRelaxable = isUnique || _secondaryGoals.ContainsKey(prefabNameP);
                if (yaml.relaxable.HasValue)
                {
                    isRelaxable = yaml.relaxable.Value;
                }

                float amount = 0.5f;
                bool hasSecondary = _secondaryGoals.TryGetValue(prefabNameP, out float secondaryAmount);
                if (hasSecondary)
                {
                    amount = secondaryAmount;
                }
                if (yaml.relaxableamount.HasValue)
                {
                    amount = yaml.relaxableamount.Value;
                }

                return new EffectivePolicy
                {
                    IsDisabled = false,
                    IsUnique = isUnique,
                    IsRelaxable = isRelaxable,
                    Amount = amount
                };
            }

            bool baseUnique = _necessities.Contains(prefabNameP);
            bool baseRelaxable = baseUnique || _secondaryGoals.ContainsKey(prefabNameP);
            float baseAmount = 0.5f;
            bool hasBaseSecondary = _secondaryGoals.TryGetValue(prefabNameP, out float baseSecondaryAmount);

            if (hasBaseSecondary)
            {
                baseAmount = baseSecondaryAmount;
            }

            return new EffectivePolicy
            {
                IsDisabled = false,
                IsUnique = baseUnique,
                IsRelaxable = baseRelaxable,
                Amount = baseAmount
            };
        }

        public static bool IsNecessity(string prefabNameP)
        {
            EffectivePolicy policy = GetEffectivePolicy(prefabNameP);
            if (policy.IsDisabled)
            {
                return false;
            }
            return policy.IsUnique;
        }

        public static bool NeedsRelaxation(string prefabNameP, int placedCountP, int requestedCountP)
        {
            EffectivePolicy policy = GetEffectivePolicy(prefabNameP);

            if (policy.IsDisabled)
            {
                return false;
            }

            if (policy.IsUnique)
            {
                return placedCountP == 0;
            }

            if (policy.IsRelaxable)
            {
                return (float)placedCountP / requestedCountP < policy.Amount;
            }

            return false;
        }

        public static int GetMinimumNeededCount(string prefabNameP, int requestedCountP)
        {
            EffectivePolicy policy = GetEffectivePolicy(prefabNameP);

            if (policy.IsDisabled)
            {
                return 0;
            }

            if (policy.IsUnique)
            {
                return 1;
            }

            if (policy.IsRelaxable)
            {
                return Mathf.Max(1, Mathf.CeilToInt(requestedCountP * policy.Amount));
            }

            return requestedCountP;
        }

        public static FailureSeverity GetSeverity(string prefabNameP, bool isPrioritizedP)
        {
            EffectivePolicy policy = GetEffectivePolicy(prefabNameP);

            if (policy.IsDisabled)
            {
                return FailureSeverity.Green;
            }

            if (policy.IsUnique)
            {
                if (isPrioritizedP)
                {
                    return FailureSeverity.Red;
                }
                return FailureSeverity.Yellow;
            }

            if (policy.IsRelaxable)
            {
                if (isPrioritizedP)
                {
                    return FailureSeverity.Orange;
                }
                return FailureSeverity.Yellow;
            }

            return FailureSeverity.Green;
        }
    }
}