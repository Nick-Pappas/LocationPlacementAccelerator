// v1
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
*/
#nullable disable
using System.Collections.Generic;
using UnityEngine;

namespace LPA
{
    public static class PlayabilityPolicy
    {
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


        public static bool IsNecessity(string prefabNameP)
        {
            return _necessities.Contains(prefabNameP);
        }

        public static bool NeedsRelaxation(string prefabNameP, int placedCountP, int requestedCountP)
        {
            if (_necessities.Contains(prefabNameP))
            {
                return placedCountP == 0;
            }

            bool isSecondaryGoal = _secondaryGoals.TryGetValue(prefabNameP, out float requiredRate);
            if (isSecondaryGoal)
            {
                return (float)placedCountP / requestedCountP < requiredRate;
            }

            return false;
        }

        public static int GetMinimumNeededCount(string prefabNameP, int requestedCountP)
        {
            if (_necessities.Contains(prefabNameP))
            {
                return 1;
            }

            bool isSecondaryGoal = _secondaryGoals.TryGetValue(prefabNameP, out float requiredRate);
            if (isSecondaryGoal)
            {
                return Mathf.Max(1, Mathf.CeilToInt(requestedCountP * requiredRate));
            }

            return requestedCountP;
        }
    }
}
