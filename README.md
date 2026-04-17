
# Location Placement Accelerator (LPA)

  NOT COMPATIBLE with warp's mod World Gen Accelerator. Use one or the other! 

LPA is a complete overhaul of Valheim's location placement engine, originally built to solve the massive generation times and broken unplayable worlds caused by using mods like **Better Continents** and **Expand World Size** combined with mods that add locations. Better Continents (BC hereafter) can generate incredible geology and topology and Expand World Size (EWS) can generate vast worlds, but often you cannot play in those worlds because vital locations fail to be placed during generation, and it takes forever to generate those worlds too.
  

When you use custom terrain noise, massive map radii, and hundreds of modded points of interest, Valheim's vanilla "guess-and-check" placement algorithm breaks down. A heavily modded game can take 30 minutes to generate a world, only to leave you with an unplayable map missing half its bosses or having no Hildir and so on.
 

LPA can fix this by giving the placement algorithm "eyes" (pre-scanning the world topology) and optionally (although on by default) utilizing all your CPU cores. 

**Note: LPA is only needed during world generation. Once your map is generated, the mod can be safely disabled or removed from your server/game. You do not need it unless you want to speed up the generation of another world.**

Although the original intention was for this to be a BC feature, I realized that it can help people who do not want to use BC.



## Key Features
 **Performance Gains:**
There are two kinds of performance gain. Speed and success rate. 
In a heavily modded setup (details later) :
 Total Requested:  17,391 location tokens, same seed (aabbxx)
 
*Vanilla (deterministic)*
  Total Time:       **27m 39.7s**
  Total Placed:     16,849  (96.88%)
  Total Failed:     **542**
  
*Survey single threaded (deterministic)*
  Total Time:       **3m 45.0s**
  Total Placed:     16,935  (97.38%)
  Total Failed:     **456**

*Survey multi threaded (NON deterministic)*
  Total Time:       **0m 36.6s**
  Total Placed:     16,986  (97.67%)
  Total Failed:     **405**

**27m 39.7s** --> **3m 45.0s** --> **0m 36.6s**

**542** --> **456** --> **405**

**The Diagnostic Logger :** 
You can run LPA purely as a diagnostic tool in any mode. For example you could leave it in Vanilla, which would leave the vanilla placement logic intact but inject telemetry, outputting an exhaustive log of exactly what happened, and precisely *why* (Altitude, Distance, Biome, etc.) failed locations were rejected.


**Smart Recovery (Constraint Relaxation):** 
If a vital location (e.g., bosses, quest locations, traders) fails to spawn because the map is too crowded or the terrain is rough or whatever the reason, LPA detects the bottleneck, slightly (or as much as you want and if you want) relaxes the rules (like altitude or distance constraints) , and retries until the world is playable or fails at the relaxed cases too. You can decide how many times you want to keep relaxing. If a location that requires enough of it placed for the game to be playable (e.g. you need enough crypts for iron) then the relaxation happens (if you have it on) until it places at least 50% of the amount. 
The relaxation never attempts to put more than the min required. So for vital stuff it tries to place at most one, and for locations that require enough it would relax to place at most 50% of them.

**Interleaved Placement:**
This is basically a fairness mode. Vanilla places locations one type at a time. So if you have three kinds of huts, A, B and C, all wanting the same biome, it will first place all As, then all Bs, and then all Cs. This means that Cs are getting the short end of the stick, if A and B huts have captured all available spots. This mode instead will place an A, then a B, then a C, then an A, then a B, and so on. Thus it prevents a single location type from monopolizing all the good terrain before other locations get a chance to spawn.

**Parallel Minimap Generation:** Generates the world map textures across multiple threads. A process that normally freezes the game for 6-10 seconds in vanilla now finishes in under ~2 seconds.

## Compatibility

  

LPA should be compatible with everything and anything unless it messes with location placement.

* **Better Continents** 
* **Expand World Size (EWS)** 
* **Expand World Data (EWD)** 

Fully compatible. In fact I was writing this to add it as functionality to Better Continents especially when it is used with EWS and larger radii. It is compatible with Jere's stuff by its very inception.


## Example
 The setup in the specific example was as follows:
 Mods that add locations:
-   Therzie-Warfare-1.8.9
-   Therzie-Monstrum-1.5.1
-   Therzie-WarfareFireAndIce-2.0.8
-   Therzie-MonstrumDeepNorth-2.0.6
-    Therzie-Wizardry-1.1.8
-   Therzie-Armory-1.3.1
-   warpalicious-More_World_Locations_AIO-4.5.0    
-   Soloredis-RtDOcean-2.1.1
-   Soloredis-RtDOceanFoods-0.2.0
-   Soloredis-RtDMonsters-2.3.2
-   Soloredis-RtDMonstrum-0.9.5
-   Soloredis-RtDHorrors-0.5.2
-   Soloredis-RtDDungeons-1.0.3
-   Marlthon-TheFisher-0.3.5

And also using Better Continents and Expand World Size, setting the radius to 17500.

* *Vanilla Engine:* 
=================================================
===      WORLD GENERATION SUMMARY             ===
=================================================

      Total Time:       **27m 39.7s**
      Total Requested:  17,391
      Total Placed:     16,849  (**96.88%**)
      Total Failed:     **542**
           ----------------
            Complete failures:
            **-Hildir_cave : 0/3** *<--this makes the world unplayable*
            -MWL_MeadowsWall1 : 0/10
            -MWL_SwampCourtyard1 : 0/5
            -MWL_SwampBrokenTower1 : 0/15
            -MWL_SwampBrokenTower3 : 0/10
           ----------------
            Partial failures:
            -Grave1 : 135/200
            -SwampRuin2 : 28/30
            -SwampHut2 : 26/50
            -SwampHut3 : 21/50
            -StoneTowerRuins04 : 36/50
            -StoneTowerRuins05 : 5/50
            -AshlandsCave_01 : 5/7
            -AshlandsCave_02 : 4/7
            -Vegvisir_location_Vrykolathas_TW : 2/12
            -DemonPortal_TW : 1/3
            -DeepNorth_SurtrCamp_TW : 6/25
            -FortressRuins : 34/100
            -DevWallAsh : 3/5
            -VegvisirSwamp_RtD : 15/24
            -MountainAltar_RtD : 69/80
            -MistlandsAltar_RtD : 74/80
            -MWL_Ruins3 : 1/25
            -MWL_RuinsArena1 : 10/25
            -MWL_RuinsArena3 : 6/25
            -MWL_RuinsChurch1 : 9/25
            -MWL_MeadowsHouse2 : 14/20
            -MWL_MeadowsTower1 : 12/15
            -MWL_OakHut1 : 12/15
            -MWL_MeadowsLighthouse1 : 9/10
            -MWL_MeadowsSawmill1 : 1/10
            -MWL_MeadowsTavern1 : 3/10
            -MWL_RuinsTower3 : 3/15
            -MWL_RuinsTower8 : 1/10
            -MWL_ForestTower2 : 9/20
            -MWL_MassGrave1 : 9/15
            -MWL_RootRuins1 : 1/15
            -MWL_ForestTower5 : 7/15
            -MWL_SwampRuin1 : 17/25
            -MWL_AbandonedHouse1 : 9/15
            -MWL_Belmont1 : 4/5
            -MWL_StoneCircle1 : 7/10
            -MWL_SwampTemple1 : 1/10
      **Playability:      UNPLAYABLE**

=================================================


* *LPA Survey Mode (Single-Threaded):* 
=================================================
===      WORLD GENERATION SUMMARY             ===
=================================================

 

     Total Time:       **3m 45.0s**
      Total Requested:  17,391
      Total Placed:     16,935  (**97.38%**)
      Total Failed:     **456**
           ----------------
            Complete failures:
            -MWL_Ruins1 : 0/5
            -MWL_MeadowsRuin1 : 0/5
            -MWL_SwampGrave1 : 0/25
            -MWL_SwampCourtyard1 : 0/5
            -MWL_SwampBrokenTower1 : 0/15
           ----------------
            Partial failures:
            -Grave1 : 67/200
            -StoneTowerRuins04 : 9/50
            -StoneTowerRuins05 : 49/50
            -ShipWreck01 : 2/25
            -ShipWreck02 : 11/25
            -Vegvisir_location_Vrykolathas_TW : 10/12
            -AltarStormHerald_TW : 1/2
            -Vegvisir_location_Gorr_TW : 2/3
            -DeepNorth_SurtrCamp_TW : 6/25
            -Hildir_cave : 1/3
            -FaderLocation : 3/5
            -MWL_Ruins2 : 4/10
            -MWL_Ruins3 : 5/25
            -MWL_Ruins7 : 1/2
            -MWL_RuinsArena1 : 13/25
            -MWL_RuinsArena3 : 12/25
            -MWL_RuinsChurch1 : 18/25
            -MWL_MeadowsHouse2 : 4/20
            -MWL_MeadowsTower1 : 8/15
            -MWL_SmallHouse1 : 13/20
            -MWL_MeadowsFarm1 : 2/10
            -MWL_MeadowsLighthouse1 : 1/10
            -MWL_MeadowsSawmill1 : 2/10
            -MWL_RuinsCastle1 : 1/15
            -MWL_ForestTower2 : 9/20
            -MWL_MassGrave1 : 9/15
            -MWL_ForestTower5 : 8/15
            -MWL_SwampBrokenTower3 : 1/10
            -MWL_StoneCircle1 : 9/10
      **Playability:      Playable**
    **-------------------------------------------------**
      Relaxations Applied:
     **- Hildir_cave (Relaxed 1x: MinAlt: 200->190)**

**=================================================**


* *LPA Survey Mode (Multithreaded):* 
=================================================
===      WORLD GENERATION SUMMARY             ===
=================================================
   

       Total Time:       **0m 36.6s**
          Total Requested:  17,391
          Total Placed:     16,986  (**97.67%**)
          Total Failed:     **405**
               ----------------
                Complete failures:
                -MWL_Ruins1 : 0/5
                -MWL_RuinsCastle1 : 0/15
               ----------------
                Partial failures:
                -Grave1 : 87/200
                -SwampHut2 : 43/50
                -StoneTowerRuins04 : 9/50
                -StoneTowerRuins05 : 48/50
                -ShipWreck01 : 3/25
                -ShipWreck02 : 6/25
                -Vegvisir_location_Vrykolathas_TW : 10/12
                -DeepNorth_SurtrCamp_TW : 8/25
                -Hildir_cave : 1/3
                -MWL_Ruins2 : 1/10
                -MWL_Ruins3 : 9/25
                -MWL_Ruins7 : 1/2
                -MWL_RuinsArena1 : 6/25
                -MWL_RuinsArena3 : 10/25
                -MWL_RuinsChurch1 : 14/25
                -MWL_MeadowsHouse2 : 11/20
                -MWL_MeadowsFarm1 : 3/10
                -MWL_MeadowsLighthouse1 : 1/10
                -MWL_MeadowsSawmill1 : 3/10
                -MWL_MeadowsWall1 : 8/10
                -MWL_RuinsTower3 : 13/15
                -MWL_ForestTower2 : 10/20
                -MWL_MassGrave1 : 14/15
                -MWL_SwampGrave1 : 3/25
                -MWL_Belmont1 : 2/5
                -MWL_SwampBrokenTower1 : 4/15
                -MWL_SwampBrokenTower3 : 4/10
          Playability:      Playable
        -------------------------------------------------
          Relaxations Applied:
        - Hildir_cave (Relaxed 1x: MinAlt: 200->190)
   =================================================




What you would see in the log if you have logging on:

    [Warning][21:12:52.778] [FAILURE] Hildir_cave: 0/3. Cost: 4,113/200,000 outer loop budget and 82,260 inner loop iterations.
    (World Altitude Profile: Min -68.0m, Max 199.6m)
    ────────────────────────────────────────────────────────
    PHASE 1 (Zone Search): 4,113 Checks
    [!] Valid Zones: 4,113
        └─ Median
    
    PHASE 2 (Placement): 82,260 Points Sampled in the 4,113 Median zones
    1. DISTANCE FILTER (Min: 1750, Max: 14000)
    [x] Failed: 7
    Above Max: 7
    [!] Passed: 82,253
        └─ Range 1750-14000
           |
           └─ 2. BIOME MATCH (Required: Mountain): 82,253 points checked
              [x] Failed: 35
                  └─ BlackForest: 15
                  └─ Plains: 10
                  └─ Meadows: 7
                  └─ Mistlands: 3
              [!] Passed: 82,218
                  └─ Mountain
                  |
                  └─ 3. ALTITUDE CHECK (Min: 200, Max: 5000): 82,218 points checked
                     [x] Failed: 82,218
                         └─ Too Low: 82,218
                            └─ Mountain:
                               ├─ Underwater (<0m): 12 [Observed: Min -3.0m, Avg -1.8m, Max 0.0m]
                               ├─ Anomalous (0m to 50m): 109 [Observed: Min 0.0m, Avg 24.0m, Max 49.2m]
                               └─ Standard Failures: 82,097 [Observed: Min 52.5m, Avg 99.2m, Max 199.6m]


  
So in that specific instance because of the specific settings the max altitude on the map was not 200, (which is what Hildir needed) and thus all 82,218 attempts failed. However, by relaxing the requirements the world is saved:

    [Info][21:14:58.763] [LPA] Relaxation pass 1: processing 1 relaxed packet(s).
    [Message][21:14:59.385] [RELAXATION SUCCESS] Hildir_cave placed 1/1 after 1 relaxation(s). (Relaxed 1x: MinAlt: 200->190)
    [Message][21:14:59.386] [RELAXED] Hildir_cave: 1/1. Cost: 763/200,000 outer loop budget and 15,243 inner loop iterations.
               (Relaxed 1x: MinAlt: 200->190)
    (World Altitude Profile: Min -68.0m, Max 199.6m)
    ────────────────────────────────────────────────────────
    PHASE 1 (Zone Search): 763 Checks
    [!] Valid Zones: 763
        └─ Median
    
    PHASE 2 (Placement): 15,243 Points Sampled in the 763 Median zones
    1. DISTANCE FILTER (Min: 1750, Max: 14000)
    [!] Passed: 15,243
        └─ Range 1750-14000
           |
           └─ 2. BIOME MATCH (Required: Mountain): 15,243 points checked
              [x] Failed: 7
                  └─ Meadows: 5
                  └─ Plains: 2
              [!] Passed: 15,236
                  └─ Mountain
                  |
                  └─ 3. ALTITUDE CHECK (Min: 190, Max: 5250): 15,236 points checked
                     [x] Failed: 15,235
                         └─ Too Low: 15,235
                            └─ Mountain:
                               ├─ Anomalous (0m to 50m): 57 [Observed: Min 0.9m, Avg 29.7m, Max 49.3m]
                               └─ Standard Failures: 15,178 [Observed: Min 50.3m, Avg 95.2m, Max 182.2m]
                     [!] Passed: 1
                         └─ Alt 190 to 5250
                         └─ PASSED REMAINING CHECKS (SIMILARITY -> TERRAIN DELTA -> VEGETATION DENSITY): 1

Since it was detected than altitude was the problem the altitude was relaxed by 5% (so 200 became 190) and we looked again, successfully placing the location.
  

## Installation

  

1. Install via your mod manager or drop the `.dll` into your `BepInEx/plugins` folder.

2. Launch the game and generate your world. By default, **Parallel Placement** and **Smart Recovery** are enabled.

3. Check the `LocationPlacementAccelerator.log` in your BepInEx folder to verify your world is playable.

4. (Optional) Disable or remove the mod. You only need it when creating a new world or forcing new location generation. It simply does not do anything otherwise.


You can find me on Discord at the Valheim World Editing server: https://discord.gg/uqY4V8Aw 
