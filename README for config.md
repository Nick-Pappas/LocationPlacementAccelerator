
# LPA Configuration Guide

Location Placement Accelerator (LPA) is quite configurable. Because the mod can operate as a pure diagnostic logger, a mathematical tweak to the vanilla code, or a complete engine replacement, the configuration file (`nickpappas.locationplacementaccelerator.cfg`) enforces certain rules to prevent nonsensical or contradictory settings.

---

## 1. The Coercion Chain (Overrides Explained)

You might set `PlacementMode` to `Vanilla`, but if you also set `EnableParallelPlacement` to `true`, LPA will ignore your request for `Vanilla` and run `Survey` mode instead. 

This is the **Coercion Chain**. It prevents impossible states (like trying to run multithreading on the legacy single-threaded vanilla engine). The engine evaluates your settings from top to bottom, applying these overrides:

1. **Parallel Placement is King:** If `EnableParallelPlacement` is `true`, LPA absolutely requires the Replaced Engine and `Survey` mode. It will force both to be active, ignoring any other engine/mode settings.
2. **Replaced Engine requires Survey:** If you set `UseLegacyEngine` to `false` (meaning you want the new Replaced Engine), LPA forces `PlacementMode` to `Survey`. The Replaced Engine does not understand "Vanilla" or "Filter" modes.
3. **Legacy Modes require the Legacy Engine:** If you explicitly choose `Filter`, `Force`, or `Vanilla` as your `PlacementMode`, LPA forces `UseLegacyEngine` to `true`. It will use the vanilla engine.

If you are ever confused about what is actually running, look at the top of your LPA log file (saved right next to your bepinex log). The header explicitly prints the "Effective" settings the engine used.

---

## 2. Engine & Architecture (The Big Switches)

**EnableParallelPlacement** (Default: `true`)
Moves location placement off the main thread and utilizes your CPU cores (specifically, however many you have but two, to leave room for the OS and Unity). 
* **Trade-off:** You get speed increase, but this eliminates strict placement determinism. If you generate the exact same seed twice, the no of locations will spawn slightly, and their exact XYZ coordinates will differ.

**UseLegacyEngine** (Default: `false`)
* `false` (Replaced Engine): Uses LPA's magic. Highly recommended.
* `true` (Transpiled Engine): Hooks directly into Valheim's original Monte Carlo-ish coroutine. Use this only if you want a pure vanilla placement run with LPA's diagnostic logging attached.

**PlacementMode** (Default: `Survey`)
* **Survey:** Pre-scans the world map, classifying every 64m zone by biome, altitude, and distance *before* throwing any placement darts. 
* **Filter:** Vanilla algorithm, but instantly rejects darts that fall outside a location's Min/Max distance ring. Think of it as Vanilla+ (gaining a bit of speed)
* **Force:** Vanilla algorithm, but mathematically forces darts to only land inside the required distance ring. Think of it as Vanilla++ (gaining perhaps a bit of success rate)
* **Vanilla:** Pure vanilla placement. Throw darts and hope for the best.

**EnableInterleavedScheduling** (Default: `false`)
Valheim normally places locations sequentially: it places *all* instances of Location A, then *all* instances of Location B then  *all* instances of Location B. If A and B and C want the same biome, Location A will greedily consume all the prime real estate. 
* Setting this to `true` breaks the location quotas into single "packets" (with some exceptions) and deals them out round-robin (A, then B, then C, then A, then B...). 
* This prevents spatial monopolies and ensures fairer distribution of locations, though it significantly alters the layout of known seeds. 

Example:
Suppose you have 5A, 5B and 5 C, and you have enough space for 7 of them. 
You would see:
AAAAABB
3 Bs will fail and all 5 Cs will fail,  8 tokens total very unfairly distributed. 

With interleaved you would get:
ABCABCA

having 2 As, 3 Bs, and 3Cs failing, which is still 8 but much fairer since every location got a chance.

Note that interleaving handles prioritized first. All prioritized locations are being placed first interleaved and then all non prioritized first interleaved. Furthermore note that for non homogeneous  group types the interleaving will not be exactly 1 token at a time. For more on that you should read the documentation here. 

---

## 3. Spatial Tuning & Survey Options

*These settings only apply if `PlacementMode` is `Survey`.*

**SurveyScanResolution** (Default: `1`)
During the initial map pre-scan, this dictates how many points inside each 64m x 64m zone are checked to determine its biome and altitude.
* `1`: Checks only the exact center (0,0) of the zone. Fastest.
* `3`: Throws a 3x3 grid (9 darts) across the zone. 
* `5`: Throws a 5x5 grid (25 darts).
* *Note:* Must be an odd number. Higher values reduce "false negatives" where a zone is discarded because its center is slightly underwater, even though the edges are dry land. However, it increases the initial survey time. 

This currently is marginally useful (was designed for a smarter lazy candidate selection method that is coming and which learns as it goes improving its vision). You can safely leave it to 1 for now. Increasing it will increase the survey time but it will also increase the success rate as you 'll get a better vision of the map. 


**SurveyVisitLimit** (Default: `1`)
How many passes the engine is allowed to make through the pre-computed candidate zone list per location type. `1` means each candidate zone is looked at exactly once. In heavily constrained maps, increasing this allows the engine to re-evaluate zones it skipped earlier.

**PresenceGridCellSize** (Default: `16f`)
*(Replaced Engine only)* The cell size (in meters) of the 2D spatial exclusion grid. LPA uses this grid to quickly check if a location is spawning too close to a similar location (the `minDistanceFromSimilar` rule). 
* `16f` is the sweet spot. Smaller values (e.g., `4f`) are hyper-precise but consume significantly more RAM (especially on 50,000m radius maps, but at those radii you may have issue with floating point precision anyway).
* Setting this to 4 will increase your success rate.



**Enable3DSimilarityCheck** (Default: `false`)
*(Replaced Engine only)* The spatial exclusion grid is 2D (X and Z axis). This works well in most biomes because the altitude range is not enough to make a difference. The 2D distance and the 3D Euclidean distance are basically equivalent. However in high-relief biomes like the Mountains or Mistlands, two locations might be overlapping on the 2D map, but separated by 300 meters of vertical cliff. 
* If `true`, LPA will perform a rigorous 3D Euclidean distance check if the 2D grid flags a conflict in a high-relief biome. This helps spawn locations in dense, vertical terrain.

---

## 4. Budgets & Brute Force

**OuterLoopMultiplier** (Default: `1.0`)
Scales the vanilla budget for how many 64m zones are examined before the engine gives up on placing a location. Vanilla is normally 100,000 (or 200,000 for prioritized locations like bosses). 
* `2.0` doubles the budget. `0.5` halves it. 

These are basically darts that you throw on the map to pick a single one of those grids. To understand why IG chose these numbers read here. I recommend you leave it at 1.0

**InnerLoopMultiplier** (Default: `1.0`)
Scales the vanilla budget for how many exact XYZ coordinates are sampled *within* a valid zone. Vanilla normally throws 20 darts per zone. 
* A value of `2.0` means 40 darts per zone. 

Once a grid is selected by the outer loop dart (one of those 100k or 200k darts) we throw a bunch of darts inside that grid trying to place a location. This is a way more meaningful parameter to increase if you want to increase success rate than increasing the outer loop one especially in a vanilla radius world. For more on this you can read here. I recommend you leave it at 1.  

---

## 5. Smart Recovery (Constraint Relaxation)

When and if the engine exhausts its budgets and a vital location fails to place, Smart Recovery kicks in. It identifies the exact bottleneck (e.g., "The map has no mountains above 200m") and loosens the rule(s) that needs to relax to attempt the placement again. 
There are two types of locations at the moment considered:
Vital to have **at least one** for the world to be playable. 
Vital to have **enough** for the world to be playable.
At the moment I have hardcoded both what is vital (bosses, traders, quests) and what is necessary (e.g. crypts for iron) and I have hardcoded the threshold to 50% for the latter and to 1 for the former. 
So if you fail to place any Hildir camp, the relaxation will relax as needed but it will do so with a goal of placing one.
If  you fail to place at least 50% of the amount of crypts it will relax enough with the goal of placing 50% and no more. 
Eventually I was thinking of making these EWD configurable through the locations yaml.


**MaxRelaxationAttempts** (Default: `4`)
How many times LPA is allowed to loosen the rules and retry before permanently giving up.
* Set to `0` to completely disable Smart Recovery.

Example:
You try to place A, which needs an altitude of at least 200, but it did not find one, will relax the altitude by the amount defined below e.g. 5% would make the altitude 190. Suppose it fails again. It will relax this time the 190 by 5% setting the altitude to 180.5. Suppose that it finds a location, but it now fails always midDistance from similar which say was 2000m.  It will now relax for a third time this time the min distance from similar again by 5%, trying now 1900m. 
And so on. It will keep attempting relaxations up to however many times you have set it here. 
Note that if you have the GUI on, failure to relax will show everything red. Successful relaxation will render everything blue. 



**RelaxationMagnitude** (Default: `0.05`)
The percentage by which the failing constraint is loosened per step. 
* At `0.05` (5%), four attempts will loosen the constraint by 5% each of what the previous attempt was at (if they keep fail at the same constraint)

---

## 6. Diagnostics & Logging

**WriteToFile** (Default: `true`)
Outputs the diagnostic logs to a file in your BepInEx directory.

**VerboseLogFileName** (Default: `false`)
* `false`: The log is always named `LocationPlacementAccelerator.log` and overwrites itself each run.
* `true`: Generates a unique log file name based on a fingerprint of your configuration and a timestamp (e.g., `LPA_Replaced_Survey_MT_1430.log`). Useful for A/B testing different config setups.

**MinimalLogging** (Default: `false`)
Suppresses the detailed "Funnel Reports" (the breakdown of exactly which filters failed) for every location. The log will only output the configuration header and the final summary.

**DiagnosticMode** (Default: `false`)
Outputs verbose heartbeat logs to track the engine's real-time progress. You wont be needing this in Survey mode. 
