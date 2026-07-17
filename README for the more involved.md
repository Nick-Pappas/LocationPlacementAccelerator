# Location Placement Accelerator (LPA): System Architecture and Algorithmic Analysis

---

## Disclaimer

This document will attempt to explain what actually happens under the hood in vanilla as well as what I did myself in LPA. 

The audience of this text is basically small:

*   **Whoever decides to take over** if the mod breaks and I have basically disappeared for whatever reason. This way they will not have to reinvent the wheel by re-understanding why the code is the way it is.  

*   **My future self**, because in 4 months I may literally be that person. In particular since this was a result of me basically shaving the yak trying to make a nice epic game world for my family, and with my initial plan being for this to be added as basic Better Continents functionality as opposed to a standalone mod, I may end up continuing shaving the yak, ending up with completely forgetting about this before merging it with BC in X months if ever.

*   **Whoever wants to understand the issue and the solution** not as a simple summary but in some depth. 

It involves math, as it cannot be helped for the stated goals, but I will try to make it as understandable for all as I can. *You absolutely do not need to read this or understand it to be able to use the mod*.

So what follows may sound like an overcomplicated academic paper for a simple mod for a game, but for me this was a situation where I was more interested in the problem itself and for itself. I was not even that much interested in the implementation which I may have not done the best of jobs, as I am a theory CS person not a SE person and the problem is inherently a CS problem. Could I have done a better job avoiding all the math jargon and sounding like writing some kind of dissertation on stochastic rejection sampling intricacies especially for something so trivial? Well maybe but that would be for a different audience then. Also the math may be simple but the implementation ended up being rather messy, so me documenting stuff rigorously here is justified I think. 

Again: *You absolutely do not need to read this or understand it to be able to use the mod*.

<br>

## Primer

Valheim’s native location placement system relies on stochastic rejection sampling operating within a continuous, bounded space (with the bounded being important as we'll soon see). 

In simple terms this is a *close your eyes, throw a dart, open them and see if you like what you got, reject it if you don't*. 

While this is sufficient for the vanilla world dimensions with the vanilla amount of locations, this architecture scales really poorly. When people start making mods that add locations or alter the map's geography and topology and especially with expanded radii of heavily modded worlds (e.g., *Better Continents*, *Expand World Size*, and a bunch of location adding mods), the system suffers algorithmic collapse.

Iron Gate implemented it this way because for their goals it was good enough; fast enough and maintainable, you have your player wait maybe 1.5 minutes once and in the vast majority of cases you end up with something playable. 

This is due to the fact of the world being small (10k radius) and the nature of vanilla's Perlin noise and the geographic reality it generates (basically a quite homogeneous archipelago) and of course the very design of what the location types are looking for. However throw stuff in there like BC and/or EWS and suddenly you can change both the geography and topology in addition to changing the size.

*   With BC+EWS you can move from an archipelago on a 10k radius world, to a world of 20k radius (making the area 4x larger) that has, say, 3 large continents, with a few islands and vast oceans separating them. That would be the **geography** changed.
*   Moreover a meadows in vanilla would look different than a meadows in BC simply because BC is using much more complex noise to generate the mesh of the ground. That would be the **topology** changed.

In the above scenario, vanilla trying to place a meadows point of interest would throw a ton of darts into ocean, saying "nope, let me throw another one" and when it would actually get meadows the nature of the landscape on it would be such that would render a ton of it non suitable. 

Iron Gate did NOT make a mistake here, I cannot stress this enough. They were not designing something to accommodate the amazing mods that people subsequently created. Good enough was the indisputably right philosophy for their needs. Nobody will complain for waiting 1.4m ONCE to generate a world, when they could have waited 4 seconds (again this happens once at world generation).

In any case the Location Placement Accelerator (LPA) replaces this stochastic brute-force approach with a few alternatives that improve the performance quite dramatically. This document details the mathematical realities of discrete grids, the architectural limits of the vanilla engine, and the methods utilized by LPA to solve them.

<br>

---

## The Problem Domain and Algorithmic Limits

To understand the optimizations and heuristic data structures employed by LPA, I must first define the environment and the constraints of the vanilla placement system.

### World Geometry and Bounded Problem Space

The Valheim world is mathematically modeled as a Gauss disk composed of 64x64 regions or zones or grids. 

In vanilla configurations, this disk possesses a fixed radius of 10km. Under modded setups, this radius can be in theory really anything but in practice I decided to arbitrarily consider an upper limit of 50km. This is because it is around there that Unity's floating point precision would start to create problems anyway. 

This is important because it reframes the problem we are trying to solve. The finite (and small) radius defines a bounded problem space. Unlike an infinite procedural voxel plane, Valheim's map is finite and relatively small - a necessity dictated by Unity's floating-point precision limits. This ensures that pre-computation, memory-caching, and full-world traversal are mathematically finite and structurally viable approaches for a modern non potato PC.

### Grid Infrastructure

The continuous world disk is partitioned into a discrete Cartesian grid. The fundamental spatial unit is the "Zone," a strictly defined $64\text{m} \times 64\text{m}$ area. I also refer to those as "Grid." This disk is our problem space.

To find the exact size of the problem space, we must count integer lattice points $(i, j)$ where $i^2 + j^2 \le r^2$. 

This is the **Gauss circle problem**. 

In the context of Valheim's zone system, the variables are defined as follows:

*   **$R$ (World Radius):** The physical radius of the world in meters (Vanilla $R = 10,000\text{m}$).
*   **$G$ (Grid Unit):** The side length of a single discrete zone in meters ($G = 64\text{m}$).
*   **$r$ (Normalized Radius):** The radius expressed in discrete grid units ($r = R / G$).
*   **$N(R, G)$ (Zone Count):** The total number of discrete $64\text{m} \times 64\text{m}$ zones whose geometric center falls within the world radius $R$.
*   **$i$ (X-Coordinate):** The integer index of a zone along the world's X-axis.
*   **$j$ (Z-Coordinate):** The integer index of a zone along the world's Z-axis.

The formula for the total number of valid zones is:

$$
N(R, G) = 1 + 4\lfloor r \rfloor + 4 \sum_{i=1}^{\lfloor r \rfloor} \lfloor \sqrt{r^2 - i^2} \rfloor
$$

#### Explanation:
*   **The $1$ :** Represents the origin zone, programmatically `Vector2i(0,0)`, centered at world coordinates $(0,0,0)$.
*   **The $4\lfloor r \rfloor$ :** Counts the zones lying strictly along the four cardinal axes (North, South, East, West), excluding the origin. $\lfloor r \rfloor$ is the maximum number of full $64\text{m}$ units that fit within $R$ in a straight line.
*   **The Summation ($\sum$):** Counts the zones located within the four quadrants (the "in-fill" zones between the axes).
    *   For every integer step $i$ from $1$ to the edge of the disk, the formula calculates the maximum possible integer height $j$ that stays within the circle using the Pythagorean theorem ($j = \sqrt{r^2 - i^2}$).
    *   The result is multiplied by $4$ to account for all four quadrants of the circular map.

Applying this to the standard Valheim parameters ($R = 10,000\text{m}$, $G = 64\text{m}$, $r = 156.25$):

$$
N(10000, 64) = 1 + 4(156) + 4 \sum_{i=1}^{156} \lfloor \sqrt{156.25^2 - i^2} \rfloor = \mathbf{77,413}
$$

This reveals that the placement budgets of **100,000** and **200,000** are precisely calibrated to this discrete reality. 100k provides a **$1.29\times$** coverage of every possible valid zone center on the map, while 200k provides a **$2.58\times$** coverage for prioritized locations.

Vanilla Valheim places locations inside this grid infrastructure using a nested loop "guess-and-check" methodology (`ZoneSystem.GenerateLocationsTimeSliced`). 

<br>

---

## The Outer Loop: 100k (or 200k) Budget and Grid Coverage

This first loop (from hereafter the Outer loop) sole task is to find a candidate zone (Vector2i) using the budgets we just talked about.
Crucially, these numbers are hardcoded and constant, regardless of the target quantity of a location type or the geometric scale of the world (as observed in `ZoneSystem.cs`).

The placement process evaluates the following condition for every candidate zone:

$$
\text{Continue} = (i < \text{Budget}) \land (\text{PlacedCount} < \text{TargetQuantity})
$$

Where the Budget is fixed to either 100k or 200k and the TargetQuantity does not affect it.

If you need to place 10 huts (TargetQuantity = 10) or use EWS multiplier to attempt to place 100 huts, you still have 100k darts to use (or 200k if prioritized). 

So the outer loop does the following for each randomly generated zoneID:

1.  **Performs an occupancy check:**  
    Is there already a location placed in this zone? If yes, reject. This is a very fast dictionary lookup.
        
2.  **Checks if a zone is "generated":**  
    This one is never an issue in a freshly generated world, as any and all locations are not generated. (They have nothing in them at all, as no player has ever visited them yet).
        
3.  **Performs a BiomeArea check:**  
    Does the macro-topology of this zone (is it deep inside a biome or on an edge?) match what the location requires? 

This is a crucial check that I should briefly explain. The biome area concept (`BiomeArea` in `Heightmap.cs`) is an enum: `Median`, `Edge`, or `Everything` (where the `Everything = Median | Edge`). Edge means the zone is on the border of two or more different biomes. Median means the zone is solidly inside a single biome. A location can specify it wants one, the other, or it does not care.

The biome types themselves are encoded as individual bits within a 32-bit integer bitfield. Because each unique biome is allocated a single bit via binary bit-shifting (1 << n), the underlying $\text{Int32}$ container imposes a hard architectural limit of exactly 32 distinct biomes across the entire engine.

The determination of whether a coordinate resides in a `Median` (solid) or `Edge` (boundary) state is using a "Neighborhood Consensus" check. This implementation performs a $3 \times 3$ kernel sampling centered on the candidate coordinate $(i, j)$. The logic looks like so:

The system executes the following validation for every random coordinate candidate:

*   **Origin Sampling:** Retrieve $S_0$ (the Biome type at the candidate zone's exact center).
*   **Neighbor Sampling:** Retrieve $S_1 \dots S_8$ (the Biome types for the 8 adjacent zones' centers).
*   **Boolean Aggregation:**

$$
\text{State} = \bigwedge_{i=1}^{8} (S_0 == S_i) ? \text{Median} : \text{Edge}
$$

In essence, if any neighbor grid's center coordinate's biome differs from the original grid's one, the zone is classified as an `Edge`.

When a location evaluates whether a zone's biome matches its placement parameters, it performs a highly efficient bitwise AND operation: 

$$
\text{Valid} = (\text{SampledBiome} \land \text{LocationMask}) \neq 0
$$

While this bitwise mask comparison is a blazing-fast $O(1)$ operation, calculating the `SampledBiome` noise state at runtime is significantly more expensive which will bring us directly to the bottlenecks of the inner loop shortly.

### The Scaling Problem

Now that we understand the role of the Outer loop, we can see how the fixed budget may become a problem if we increase the radius of a world.
When a mod like *Expand World Size* increases the radius to $50,000m$, the area scales quadratically ($R^2$). 
To find the new problem space, we apply the same discrete geometry formula:

*   **$R$ (World Radius):** $50,000\text{m}$
*   **$G$ (Grid Unit):** $64\text{m}$
*   **$r$ (Normalized Radius):** $50,000 / 64 = 781.25$

$$
N(50000, 64) = 1 + 4(781) + 4 \sum_{i=1}^{781} \lfloor \sqrt{781.25^2 - i^2} \rfloor = \mathbf{1,917,485}
$$

The map now contains approximately **1.92 million discrete zones**. 

In this situation the fixed 100k or 200k budget - which previously covered the vanilla map more than enough, now would cover barely **$5.2\%$** (in the 100k case) of the available search space. For locations with strict biome or terrain requirements, the probability of the stochastic sampler "stumbling" upon a valid zone within this $5.2\%$ window drops significantly. In large even vanilla worlds, certain locations can simply fail to spawn entirely despite a ton of available space: the "darts" are being thrown at a board that is now $25\times$ larger (the zone count is to be pedantic 24.77x), but the number of darts hasn't changed.

When I first started LPA, I was mostly concerned with the outer loop because I was using EWS and the simplest thing one can do is increase this budget for these larger radii worlds. In fact I had named the mod Location Budget Booster as all it was doing was giving more darts to this external loop.

In any case, the outer loop is relatively simple and efficiently structured to fail fast. The real and expensive work is really happening in the inner loop. Because I was also using BC I immediately realized that simply adopting the philosophy of "and damn the expenses" like Bender in Futurama for the external budget was not enough. 

<br>

---

## Inner Loop Amplification and $O(N)$ Degradation

When the outer loop finds a valid Grid G, it sends it to the inner loop which it is tasked then with finding an exact $Vector3$ coordinate in that grid to place the location. The inner loop then throws `20` random darts in that grid G. 

It does so in a strict "fail-fast" (well, attempted fail fast) algorithmic waterfall: it executes computationally cheap mathematical validations first, deferring expensive heightmap queries and array traversals until later.

For each of the 20 attempts, the engine generates a random coordinate within the zone (padding the boundaries by the location's maximum radius to prevent grid-bleeding) and subjects it to the following sequence of filters:

### 1. Global Distance and Coordinate Validation
The system first calculates the magnitude (so the $L_2$ norm !!) of the coordinate from the world origin $(0,0,0)$. 
If the location requires a specific spawning annulus (e.g., $m\_minDistance < \text{magnitude} < m\_maxDistance$), points falling outside this ring are immediately discarded. 

This is a trivial $O(1)$ floating-point check asymptotically, however this is kind of expensive in practice, and it is not clear to me why they did not use sqrMagnitude. 
Anyway, the point here is, that 

$$
\||\mathbf{v}\||_2 = \sqrt{x^2 + y^2 + z^2}
$$

is a bit expensive and perhaps they could be doing instead: $minDistance^2 < mag^2 < maxDistance^2$.

Note however that because this first check is a check for a zone (the center of a zone) they do not care about the relief of the zone, they treat it purely as a 2d square by setting y=0. So at this point the Euclidean distance is the 2D one. 

### 2. Point Biome Check
The biome at the candidate point is sampled directly from the world generator's noise. This is a **different check than the outer loop's biome area check**. The outer loop's `GetBiomeArea` used a 3×3 neighborhood consensus at zone resolution to classify the zone as `Median` or `Edge`. This check asks a more direct question: what biome is this specific point actually in?

If the result does not match the location's `m_biome` bitmask, the point is rejected. This is a moderate-cost noise evaluation (several layers of Perlin and cellular noise), but it is the cheapest of the terrain queries, and it lives here appropriately.

### 3. Altitude Check and the First Hidden Tax
The world generator is asked to compute the terrain height at the candidate point. This is where things start to get expensive.

The height computation is not a lookup. It is a full noise evaluation: the biome at the point is derived first (from the same noise as Filter 2 meaning the biome is now computed **twice in direct sequence** for any point that passes Filter 2, with no caching between the two calls), and then a biome-specific height function is evaluated. These height functions are multi-octave Perlin stacks with river influence and various correction terms layered on top. The exact arithmetic cost varies by biome but none of them are cheap.

I do not think that IG was stupid here, and the lack of caching is actually probably defensible in context, and here is why: `GetHeight` is a general-purpose function used all over the engine, not just in the inner loop. Designing it to accept a pre-computed biome would mean every caller needs to have already computed the biome, which couples the API and pushes responsibility onto callers. Just weird design. The clean design is for `GetHeight` to be self-contained. 

The cost of that cleanliness is only visible when you call it immediately after an explicit `GetBiome` call on the same point, which is the inner loop's specific sin. It is a reasonable API design decision that happens to interact poorly with one specific call sequence.

The altitude check itself compares the computed height against `m_minAltitude` and `m_maxAltitude` after subtracting the water surface level (which is at $y = 30$ in world units):

$$
\text{altitude} = h(x, z) - 30
$$
$$
m\_minAltitude \leq \text{altitude} \leq m\_maxAltitude
$$

Two important side effects occur here that persist for the rest of the attempt:
1.  First, the candidate point's $y$ coordinate is updated to the computed terrain height. The world is a relief disk not a flat plane and from this filter onward the candidate is a full 3D point with a real altitude. This matters for Filter 7 as we will see.
2.  Second, the vegetation mask for this point comes out for free as a byproduct of the height computation (returned as a color mask channel). This means Filter 9's vegetation check will cost nothing. One of the few things the ordering gets right by accident (?) or by genius (?). Lets go with genius.

**Cost:** 1× biome classification (redundant with Filter 2) + 1× biome-specific multi-octave noise stack. This is the largest, dominant individual cost we have endured up to this point.

### 4. Forest Factor Check (Conditional)
If the location type has `m_inForest = true`, the forest noise value at the candidate point is sampled and compared against `m_forestTresholdMin` and `m_forestTresholdMax`. This is gated, so the vast majority of location types skip it entirely at negligible cost.

**Cost:** 0 for most locations. 1× Perlin evaluation for forest-sensitive ones.

### 5. Distance From Center Check
A second radial distance gate, distinct from Filter 1. This one uses only the $XZ$ plane distance (explicitly ignoring $y$, which now has a real terrain height value that is irrelevant to the question of how far from the world center this point is):

$$
d_{XZ} = \sqrt{x^2 + z^2}
$$
$$
m\_minDistanceFromCenter \leq d_{XZ} \leq m\_maxDistanceFromCenter
$$

This is an $O(1)$ sqrt. It has no dependency on $y$ whatsoever, which means it could have run before Filter 3 with identical correctness. It did not need to wait for the expensive height computation. Now granted... this is gated. The distance-from-center check only runs if at least one of the two distance constraints is nonzero. For location types that do not use these parameters, the entire sqrt is skipped with a single branch. So if most of the locations do not use them then... fine. The practical impact would be small. I will come back to this.

The point here is that this distance is once again a 2D check, a point P on sea level 3km from 0,0,0 and a point Q on a 3km tall mountain also 3km away from 0,0,0 will both pass the check. Once again I am not sure why we are doing the expensive square rooting here. 

**Cost:** 1× sqrt. Trivial if we ignore that we could have avoided the square root. 

### 6. Terrain Delta Check — The Most Expensive Step
The terrain slope within the location's footprint is estimated by sampling the height at 10 (!!) randomly placed points within the exterior radius, then computing the difference between the maximum and minimum sampled heights:

$$
\Delta = \max_i\,h_i - \min_i\,h_i, \quad i \in \{1, \ldots, 10\}
$$

This is compared against `m_minTerrainDelta` and `m_maxTerrainDelta`. Locations that need flat ground (structures with foundations) set a tight upper bound while locations that tolerate uneven terrain set it high. The slope direction (from lowest to highest sample) is returned as a byproduct and used later for slope-aligned rotation.

The hidden cost here is I would say significant: each of the 10 samples is a **full height computation** of the same kind as Filter 3 meaning another `GetBiome` call plus another biome-specific noise stack, ten times over. A candidate that reaches Filter 6 after passing all preceding filters has therefore accumulated a minimum of **11 full height evaluations** in this single inner loop attempt (1 from Filter 3, 10 from here), and at least **12 biome classifications** (1 explicit in Filter 2, 1 implicit in Filter 3, 10 implicit here).

I do not know why they did it this way, but let's say charitably that when the vanilla location list was small and M stayed small throughout generation, HaveLocationInRange was always cheap, and the ordering was invisible in practice. It was probably set during development and never needed to change because it never caused a problem. It only becomes wrong at scale, and that scale was never the design target. Still, as you will see a simple swap in order of checks can dramatically speed up things. More on that later.

**Cost:** $10\times$ full height evaluation = $10\times$ (biome classification + biome-specific noise). Dominant cost of the entire waterfall by a significant margin.

### 7 & 8. Proximity to Similar Locations — the $O(N)$ Problem
Filter 7 checks that the candidate does not fall within a minimum exclusion distance of an existing location of the same name or group (`m_minDistanceFromSimilar`). Filter 8 checks the inverse - that the candidate *is* within a maximum clustering distance of a required neighbor type (`m_maxDistanceFromSimilar`). Both are evaluated only when their respective parameters are nonzero.

Both work the same way: a full linear scan of `m_locationInstances`, the dictionary of all currently registered placements across all location types. For each entry, a name or group string is compared, and if it matches, the euclidean distance to the candidate is computed:

$$
d = \sqrt{(x_1-x_2)^2 + (y_1-y_2)^2 + (z_1-z_2)^2}
$$

Note that this is the **full 3D distance**, using the $y$ coordinate that Filter 3 set to the actual terrain height. Contrast with Filter 1, which used a 2D distance with $y = 0$ because zones are spatially indexed on the XZ plane and altitude is irrelevant at that resolution. Both choices are defensible in isolation, but they create an internal inconsistency: the two "distance from origin" checks in this waterfall use different distance metrics for no explicit reason. 

In high-relief terrain like Mountains and Mistlands, this matters: two locations that are horizontally close but separated by substantial vertical relief will have a larger 3D distance than 2D distance, meaning the 3D check is more permissive - it allows placements that a purely 2D check would reject. Whether this is the right behavior depends on your goals. In LPA I handle this explicitly (more on that later). Basically for most biomes since the world is so small and so flat relatively speaking, I decided that the 2D can be safely assumed to be adequate. It is not adequate obviously in the high relief terrains. 

The more immediate problem is the $O(N)$ cost. Let $M$ be the number of entries currently in `m_locationInstances`. This scan costs $O(M)$, and $M$ grows monotonically as placement proceeds. It starts at zero and ends at the total number of successfully placed locations across the entire world (potentially many thousands in a heavily modded setup). The rejection rate of Filter 7 also grows with $M$: as territory is claimed, the probability of landing inside an exclusion zone increases.

This gives us the critical inversion: **Filter 6 has constant cost. Filter 7 has growing cost and growing rejection rate.** The rational order in a rejection waterfall is to put the filter with the highest cost-per-rejection ratio last. Early in generation Filter 6 is clearly more expensive than Filter 7. Late in generation Filter 7 catches up and exceeds it in both cost and effectiveness as a gate. The vanilla order is therefore wrong at scale, and increasingly wrong as the world fills up.

**Cost:** $O(M) \times$ (string comparison + sqrt). Cost grows with placement progress.

### 9. Vegetation Density
The vegetation mask retrieved during Filter 3 is compared against `m_minimumVegetation` and `m_maximumVegetation`. As noted earlier, this data was obtained for free as a byproduct of the height computation.

**Cost:** Zero. Already in hand from Filter 3.

### 10. Surround Vegetation Check (Conditional, Expensive)
Some location types do not merely gate on a vegetation density range. Rather they actively seek the most vegetated point within the zone. This is implemented by sampling vegetation at points arranged on concentric rings around the candidate (6 points per ring, with configurable ring count `m_surroundCheckLayers` and radius `m_surroundCheckDistance`), computing a weighted sum, and comparing the candidate's weighted sum against the running average of the last several candidates. A candidate is only accepted if its vegetation quality exceeds a configured fraction of the gap between the average and the best seen so far.

Each sample requires a full height computation (for the vegetation mask), so with the default 2 layers this adds $6 \times 2 = 12$ more height evaluations. The comparison against recent candidates also means this check accumulates a sample buffer across multiple inner loop attempts - up to 10 samples - before it can produce a reliable result, which creates an interesting interaction with the 20-attempt budget.

Very few location types that I 've seen use this check. For those that do, the per-attempt cost is substantial.

**Cost (conditional):** $6 \times \text{layers} \times$ full height evaluation.

<br>

---

## The Ordering Problem, Summarized

The full cost of a maximally expensive inner loop attempt (all filters active, all passing, location registered) is at minimum 11 height evaluations, 12 biome classifications, and two $O(M)$ linear scans. The ordering of filters 5, 6, and 7 represents the core inefficiency:

- **Filter 5** (trivial sqrt, no dependency on $y$) runs after the expensive Filter 3 for no reason.
- **Filter 6** ($10\times$ height evaluation, constant cost) runs before Filter 7.
- **Filter 7** ($O(M)$ scan, growing cost and growing rejection rate) runs after Filter 6.

The inversion of 6 and 7 means that as $M$ grows, an increasing fraction of inner loop attempts pay the full cost of Filter 6 for a point that Filter 7 would have rejected at lower cost. The larger $M$ gets, the more this hurts.

In a vanilla 10k world with a small total location count, this is, let's say, invisible. In a modded 50k world with dozens of location types and thousands of total instances, it compounds into real time. This is what LPA's `OptimizePlacementChecks` config parameter addresses.
I could have done an inversion of 3 and 5 but I did not and I do not remember why. Maybe in the future.

### The Asymptotic Argument

The vanilla ordering is wrong in the asymptotic sense. The formal argument is:

Let $C$ be the constant cost of terrain delta, $r_\delta$ its rejection rate, and $r_s$ the similarity rejection rate. Vanilla's ordering is better than swapped only when:

$$
O(M) < C \cdot \frac{r_s}{r_\delta}
$$

Since $C$, $r_s$, and $r_\delta$ are all constants for a given location type, the right-hand side is a constant $M^\ast$. Once $M > M^\ast$, the swap is permanently superior, and since $M$ grows monotonically and never decreases, the vanilla order is wrong for all placement after that threshold. That is precisely the mathematical "almost all"; everything except a constant-length initial prefix.

For any fixed terrain delta cost and any fixed rejection rate profile, there exists a constant threshold $M^\ast$ beyond which the $O(M)$ scan dominates and moving it before terrain delta is always the better choice. Since $M$ grows monotonically and generation finishes at $M_{\text{final}} \gg M^\ast$ for any non-trivial world, the vanilla order is suboptimal for almost all placement - the sole exception being the constant-length initial phase where $M < M^\ast$. 

If one were to super-hyper-duper optimize would have to do 6->7 up to the indifference point and then swap to 7->6 ordering. 

In any case in LPA I do not merely reorder these two checks. In the transpiled engine, the similarity check becomes $O(K)$ where $K$ is bounded by the similarity radius in zone units typically 9 to 25, regardless of $M$. In the replaced engine it becomes a single bit read: $O(1)$. Either way, the check is unconditionally cheaper than 10 `GetHeight` evaluations at any $M$ including $M = 0$, making the asymptotic argument moot. Similarity before terrain delta is not just eventually correct, it is correct for every single dart thrown.


(to be continued)
