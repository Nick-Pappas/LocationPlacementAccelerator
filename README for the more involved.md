# Location Placement Accelerator (LPA): System Architecture and Algorithmic Analysis

## Disclaimer
This document will attempt to explain what actually happens under the hood in vanilla as well as what I did myself in LPA.
The audience of this text is basically small:

Whoever decides to take over if the mod breaks and I have basically disappearred for whatever reason. This way they will not have to reinvent the wheel by re-understanding why the code is the way it is.  

My future self, because in 4 months I may literally be that person. In particular since this was a result of me basically shaving the yak trying to make a nice epic game world for my family, and with my initial plan being for this to be added as basic Better Continents functionality as opposed to a standalone mod, I may end up continuing shaving the yak, ending up with completely forgetting about this before merging it with BC in X months if ever.

Whoever wants to understand the issue and the solution not as a simple summary but in some depth. 
It involves math, as it cannot be helped for the stated goals, but I will try to make it as understandable for all as I can. *You absolutely do not need to read this or understand it to be able to use the mod*.

So what follows may sound like an overcomplicated academic paper for a simple mod for a game, but for me this was a situation where I was more interested in the problem itself and for itself. I was not even that much interested in the implementation which I may have not done the best of jobs, as I am a theory CS person not a SE person and the problem is inherently a CS problem. Could I have done a better job avoiding all the math jargon and sounding like writing some kind of dissertation on stochastic rejection sampling intricancies especially for something so trivial? Well maybe but that would be for a different audience then. Also the math may be simple but the implementation ended up being rather messy, so me documenting stuff rigorously here is justified I think. Again:
*You absolutely do not need to read this or understand it to be able to use the mod*.


## Primer
Valheim’s native location placement system relies on stochastic rejection sampling operating within a continuous, bounded space (with the bounded being important as we 'll soon see). 
In simple terms this is a close your eyes, throw a dart, open them and see if you like what you got, reject it if you don't. 

While this is sufficient for the vanilla world dimensions with the vanilla amount of locations, this architecture scales really poorly. When people start making mods that add locations or alter the map's geography and topology and especially with expanded radii of heavily modded worlds (e.g., *Better Continents*, *Expand World Size*, and a bunch of location adding mods), the system suffers algorithmic collapse.

Iron Gate implemented it this way because for their goals it was good enough; fast enough and maintenable, you have your player wait maybe 1.5 minutes once and in the vast majority of cases you end up with something playable. 
This is due to the fact of the world being small (10k radius) and the nature of vanilla's Perlin noise and the geographic reality it generates (basically a quite homogeneous archipelago) and ofcourse the very design of what the location types are looking for. However throw stuff in there there like BC and/or EWS and suddenly you can change both the geography and topology in addition to changing the size.

With BC+EWS you can move from an archipelago on a 10k radius world, to a world of 20k radius (making the area 4x larger) that has, say, 3 large continents, with a few islands and vast oceans separating them. That would be the geography changed.

Moreover a meadows in vanilla would look different than a meadows in in BC simply because BC is using much more complex noise to generate the mesh of the ground. That would be the topology changed.

In the above scenario, vanilla trying to place a meadows point of interest would throw a ton of darts into ocean, saying "nope, let me throw another one" and when it would actually get meadows the nature of the landscape on it would be such that would render a ton of it non suitable. 

Iron Gate did NOT make a mistake here, I cannot stress this enough. They were not designing something to accomodate the amazing mods that people subsequently created. Good enough was the indisputably right philosophy for their needs. Nobody will complain for waiting 1.4m ONCE to generate a world, when they could have waited 4 seconds (again this happens once at world generation).


In any case the Location Placement Accelerator (LPA) replaces this stochastic brute-force approach with a few alternatives, that improve the performance, quite dramatically. This document details the mathematical realities of discrete grids, the architectural limits of the vanilla engine, and the methods utilized by LPA to solve them.


## The Problem Domain and Algorithmic Limits

To understand the optimizations and heuristic data structures employed by LPA, I must first define the environment and the constraints of the vanilla placement system.

### World Geometry and Bounded Problem Space
The Valheim world is mathematically modeled as a Gauss disk composed of 64x64 regions or zones or grids. 
In vanilla configurations, this disk possesses a fixed radius of 10km. Under modded setups, this radius can be in theory really anything but in practice I decided to arbitrarily consider an upper limit of 50km. This is because it is around there that Unity's floating point precision would start to create problems anyway. 
This is important because it reframes the problem we are trying to solve. The finite (and small) radius defines a bounded problem space. Unlike an infinite procedural voxel plane or a finite but completely unbounded case where the radius could be anything, Valheim's finite and small by necessity due to Unity's shortcomings map, ensures that pre-computation, memory-caching, and full-world traversal are mathematically finite and structurally viable approaches for a modern non potato PC.



### Grid Infrastructure
The continuous world disk is partitioned into a discrete Cartesian grid. The fundamental spatial unit is the "Zone," a strictly defined $64\text{m} \times 64\text{m}$ area. I also refer to those as "Grid." This disk is our problem space.

To find the exact size of the problem space, we must count integer lattice points $(i, j)$ where $i^2 + j^2 \le r^2$. 

This is the **Gauss circle problem**. 

In the context of Valheim's zone system, the variables are defined as follows:

* **$R$ (World Radius):** The physical radius of the world in meters (Vanilla $R = 10,000\text{m}$).
* **$G$ (Grid Unit):** The side length of a single discrete zone in meters ($G = 64\text{m}$).
* **$r$ (Normalized Radius):** The radius expressed in discrete grid units ($r = R / G$).
* **$N(R, G)$ (Zone Count):** The total number of discrete $64\text{m} \times 64\text{m}$ zones whose geometric center falls within the world radius $R$.
* **$i$ (X-Coordinate):** The integer index of a zone along the world's X-axis.
* **$j$ (Z-Coordinate):** The integer index of a zone along the world's Z-axis.

The formula for the total number of valid zones is:

$$
N(R, G) = 1 + 4\lfloor r \rfloor + 4 \sum_{i=1}^{\lfloor r \rfloor} \lfloor \sqrt{r^2 - i^2} \rfloor
$$

#### Explanation:
**The $1$ :** Represents the origin zone, programmatically `Vector2i(0,0)`, centered at world coordinates $(0,0,0)$.

**The $4\lfloor r \rfloor$ :** Counts the zones lying strictly along the four cardinal axes (North, South, East, West), excluding the origin. $\lfloor r \rfloor$ is the maximum number of full $64\text{m}$ units that fit within $R$ in a straight line.

**The Summation ($\sum$):** Counts the zones located within the four quadrants (the "in-fill" zones between the axes).
- For every integer step $i$ from $1$ to the edge of the disk, the formula calculates the maximum possible integer height $j$ that stays within the circle using the Pythagorean theorem ($j = \sqrt{r^2 - i^2}$).
- The result is multiplied by $4$ to account for all four quadrants of the circular map.

Applying this to the standard Valheim parameters:
* $R = 10,000\text{m}$
* $G = 64\text{m}$
* $r = 10,000 / 64 = 156.25$

$$
N(10000, 64) = 1 + 4(156) + 4 \sum_{i=1}^{156} \lfloor \sqrt{156.25^2 - i^2} \rfloor = \mathbf{77,413}
$$



This reveals that the placement budgets of **100,000** and **200,000** are precisely calibrated to this discrete reality. 100k provides a **$1.29\times$** coverage of every possible valid zone center on the map, while 200k provides a **$2.58\times$** coverage for prioritized locations.

Vanilla Valheim places locations inside this grid infrastructure using a nested loop "guess-and-check" methodology (`ZoneSystem.GenerateLocationsTimeSliced`). 

### The Outer Loop:  100k (or 200k) Budget and Grid Coverage

This first loop (from hereafter the Outer loop) sole task is to find a candidate zone (Vector2i) using the budgets we just talked about.
Crucially, these numbers are hardcoded and constant, regardless of the target quantity of a location type or the geometric scale of the world (as observed in `ZoneSystem.cs`).

The placement process evaluates the following condition for every candidate zone:

$$\text{Continue} = (i < \text{Budget}) \land (\text{PlacedCount} < \text{TargetQuantity})$$ 

Where the Budget is fixed to either 100k or 200k and the TargetQuantity does not affect it.

If you need to place 10 huts (TargetQuantity = 10) or use EWS multiplier to attempt to place 100 huts, you still have 100k darts to use (or 200k if prioritized). 

So the outer loop does the following for each randomly generated zoneID:

  **Performs an occupancy check:**  
    Is there already a location placed in this zone? If yes, reject. This is a very fast dictionary lookup.
        
  **Checks if a zone is "generated":** 
    This one is never an issue in a freshly generated world, as any and all locations are not generated. (They have nothing in them at all, as no player has ever visited them yet).
        
  **Performs a BiomeArea check:**  
    Does the macro-topology of this zone (is it deep inside a biome or on an edge?) match what the location requires? This is a crucial check that I should briefly explain. The biome area concept (BiomeAre in Heightmap.cs) is a bitmask. Edge means the zone is on the border of two or more different biomes. Median means the zone is solidly inside a single biome. A location can specify it wants one, the other, or it does not care. 

The determination of whether a coordinate resides in a `Median` (solid) or `Edge` (boundary) state is using a "Neighborhood Consensus" check. This implementation performs a $3 \times 3$ kernel sampling centered on the candidate coordinate $(i, j)$. The logic looks like so:

The system executes the following validation for every random coordinate candidate:

 **Origin Sampling:** Retrieve $S_0$ (the Biome type at the candidate zone's exact center).
 **Neighbor Sampling:** Retrieve $S_1 \dots S_8$ (the Biome types for the 8 adjacent zones' centers).
 **Boolean Aggregation:**
    $$\text{State} = \bigwedge_{i=1}^{8} (S_0 == S_i) ? \text{Median} : \text{Edge}$$

In essence, if any neighbor grid's center coordinate's biome differs from the original grid's one, the zone is classified as an `Edge`.

Now that we understand the role of the Outer loop, we can see how the fixed budget may become a problem if we increase the radius of a world.
When a mod like *Expand World Size* increases the radius to $50,000m$, the area scales quadratically ($R^2$). 
To find the new problem space, we apply the same discrete geometry formula:

* **$R$ (World Radius):** $50,000\text{m}$
* **$G$ (Grid Unit):** $64\text{m}$
* **$r$ (Normalized Radius):** $50,000 / 64 = 781.25$


$$
N(50000, 64) = 1 + 4(781) + 4 \sum_{i=1}^{781} \lfloor \sqrt{781.25^2 - i^2} \rfloor = \mathbf{1,917,485}
$$


The map now contains approximately **1.92 million discrete zones**. 

In this situation the fixed 100k or 200k budget—which previously covered the vanilla map more than enough, now would cover barely **$5.2\%$** (in the 100k case) of the available search space. For locations with strict biome or terrain requirements, the probability of the stochastic sampler "stumbling" upon a valid zone within this $5.2\%$ window drops significantly. In large even vanilla worlds, certain locations can simply fail to spawn entirely despite a ton of available space: the "darts" are being thrown at a board that is now $25\times$ larger (the zone count is to be pedantic 24.77x), but the number of darts hasn't changed.

When I first started LPA, I was mostly concerned with the outer loop because I was using EWS and the simplest thing one can do is increase this budget for these larger radii worlds. In fact I had named the mod Location Budget Booster as all it was doing was giving more darts to this external loop.

In any case, the outer loop is relatively simple and efficiently structured to fail fast. The real and expensive work is really happening in the inner loop. Because I was also using BC I immediately realized that simply adopting the philosophy of "and damn the expenses" like Bender in Futurama for the external budget was not enough. 


### Inner Loop Amplification and $O(N)$ Degradation
When the outer loop finds a valid Grid G, it sends it to the inner loop which it is tasked then with finding an exact $Vector3$ coordinate in that grid to place the location. The inner loop then throws `20` random darts in that grid G. 

It does so in a strict "fail-fast" (well, attempted fail fast) algorithmic waterfall:

it executes computationally cheap mathematical validations first, deferring expensive heightmap queries and array traversals until later.

For each of the 20 attempts, the engine generates a random coordinate within the zone (padding the boundaries by the location's maximum radius to prevent grid-bleeding) and subjects it to the following sequence of filters:

#### 1. Global Distance and Coordinate Validation (ZoneSystem.cs)
The system first calculates the magnitude (so the $L_2$ norm !!) of the coordinate from the world origin $(0,0,0)$. 
If the location requires a specific spawning annulus (e.g., $m\_minDistance < \text{magnitude} < m\_maxDistance$), points falling outside this ring are immediately discarded. This is a trivial $O(1)$ floating-point check assymptotically, however this is kind of expensive in practice, and it is not clear to me why they did not use sqrMagnitude. 
Anyway, the point here is, that 
$$\||\mathbf{v}\||_2 = \sqrt{x^2 + y^2 + z^2}$$
is a bit expensive and perhaps they could be doing instead:
$minDistance^2 < mag^2 < maxDistance^2$


(to be continued)
