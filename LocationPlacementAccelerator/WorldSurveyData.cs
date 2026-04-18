// v1.0.2
/**
* Pre-scans the entire world grid in parallel, building a flat ZoneProfile[]
* array with packed biome/area/distance bitmasks per zone. Also performs
* coastal tagging (zones adjacent to ocean get CoastalBit set) and logs
* the biome distribution summary.
*
* Grid[] is the authoritative zone data source for LT bucketing.
* ZoneToIndex maps Vector2i --> flat array index.
* OccupiedZoneIndices tracks zones that already have a location placed.
* Here is where the magic happens. 
*
* 1.0.1: Biome masks widened to long. EWD custom biomes can occupy bits 0..31 in
* the Heightmap.Biome enum. LPA's synthetic flags (BiomeBoilingOcean, CoastalBit)
* moved to bits 40/41 so they can never collide with a legitimate biome value.
* LandBiomeMask now covers all 32 biome bits minus ocean flags. Biome distribution
* logging uses the EWD BiomeManager (via reflection in Compatibility) for names
* when available so custom biomes show as their user-defined names instead of
* bare numeric values.
*
* 1.0.2: Sign-extension bug fix in GetBiomeMask. EWD's custom biome values can
* set bit 31 (e.g. 0x80000000 from NextBiome wraparound). Casting (long)(int)Biome
* on such a value sign-extends and corrupts bits 32..63 in the resulting long,
* colliding with the synthetic flags.
*/
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LPA
{
    public static class WorldSurveyData
    {
        // Synthetic flags live above the biome enum range. Heightmap.Biome can
        // produce values up to bit 31 (EWD's NextBiome doubles from 0x200 to 0x80000000
        // then wraps to 0x80). Bits 32..63 are ours to use.
        public const long BiomeBoilingOcean = 1L << 40;
        public const long CoastalBit = 1L << 41;

        // Real biome bits occupy 0..31. Ocean is bit 8 (0x100). Why are we restricting the biomes so much though. Anyway.
        private const long AllBiomeBits = 0xFFFFFFFFL;
        public const long OceanFlags = (long)Heightmap.Biome.Ocean | BiomeBoilingOcean;
        public const long LandBiomeMask = AllBiomeBits & ~OceanFlags;

        public static ZoneProfile[] Grid { get; private set; }
        public static Dictionary<Vector2i, int> ZoneToIndex { get; private set; } = new Dictionary<Vector2i, int>();
        public static HashSet<int> OccupiedZoneIndices { get; private set; } = new HashSet<int>();

        private static bool _initialized = false;
        private static int _scanRowsDone = 0;
        private static int _scanTotalRows = 0;

        public static float SurveyProgress
        {
            get
            {
                if (_scanTotalRows <= 0)
                {
                    return 0f;
                }
                return Math.Min(1f, (float)_scanRowsDone / _scanTotalRows);
            }
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            ScanEntireWorld();
            _initialized = true;
        }

        public static void Reset()
        {
            _initialized = false;
            _scanRowsDone = 0;
            _scanTotalRows = 0;
        }

        private static void ScanEntireWorld()
        {
            int gridSize = ModConfig.SurveyScanResolution.Value;
            float worldRadius = ModConfig.WorldRadius;
            DiagnosticLog.WriteTimestampedLog($"Phase A: Starting Survey (Resolution {gridSize}x{gridSize}, Parallel Init)...");

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int radiusZones = (int)(worldRadius / 64f);
            int diameter = radiusZones * 2 + 1;

            List<ZoneProfile>[] localProfiles = new List<ZoneProfile>[diameter];
            float[] localMinAlt = new float[diameter];
            float[] localMaxAlt = new float[diameter];

            float[] offsets = GetSampleOffsets(gridSize);

            int workerCount = Math.Max(1, Environment.ProcessorCount - 2);
            DiagnosticLog.WriteTimestampedLog(
                $"[Survey] Using {workerCount} worker threads (logical CPUs: {Environment.ProcessorCount}).");

            _scanRowsDone = 0;
            _scanTotalRows = diameter;
            ParallelOptions pOpts = new ParallelOptions { MaxDegreeOfParallelism = workerCount };

            Parallel.For(0, diameter, pOpts, (int iP) =>
            {
                int y = iP - radiusZones;
                localProfiles[iP] = new List<ZoneProfile>();

                localMinAlt[iP] = float.MaxValue;
                localMaxAlt[iP] = float.MinValue;

                for (int x = -radiusZones; x <= radiusZones; x++)
                {
                    if (x * x + y * y > radiusZones * radiusZones)
                    {
                        continue;
                    }

                    Vector2i id = new Vector2i(x, y);
                    Vector3 center = ZoneSystem.GetZonePos(id);
                    float rawH = WorldGenerator.instance.GetHeight(center.x, center.z);
                    float normH = rawH - 30.0f;

                    if (normH < localMinAlt[iP])
                    {
                        localMinAlt[iP] = normH;
                    }
                    if (normH > localMaxAlt[iP])
                    {
                        localMaxAlt[iP] = normH;
                    }

                    long bMask = GetBiomeMask(center, gridSize, offsets);

                    // AshLands zones below sea level are reclassified as BiomeBoilingOcean so they match
                    // AshLands underwater location types during candidate scan.
                    // NOTE: this remains a vanilla-geometry-specific hack. A custom "lava" biome whose
                    // terrain dips below sea level would NOT be reclassified here. Acceptable for now
                    // because this only matters for the handful of AshLands sub-sea location types.
                    bool isAshLands = (bMask & (long)Heightmap.Biome.AshLands) != 0L;
                    if (isAshLands && normH < -4.0f)
                    {
                        bMask &= ~(long)Heightmap.Biome.AshLands;
                        bMask |= BiomeBoilingOcean;
                    }

                    int aMask = GetAreaMask(center, gridSize, offsets);
                    ushort distMask = GetDistanceMask(center, worldRadius);

                    localProfiles[iP].Add(new ZoneProfile { ID = id, BiomeMask = bMask, AreaMask = aMask, DistanceMask = distMask });
                }
                Interlocked.Increment(ref _scanRowsDone);
            });

            TelemetryHelpers.GlobalMinAltitudeSeen = float.MaxValue;
            TelemetryHelpers.GlobalMaxAltitudeSeen = float.MinValue;

            List<ZoneProfile> tempList = new List<ZoneProfile>();
            ZoneToIndex.Clear();
            OccupiedZoneIndices.Clear();

            int indexCounter = 0;

            for (int i = 0; i < diameter; i++)
            {
                if (localMinAlt[i] < TelemetryHelpers.GlobalMinAltitudeSeen)
                {
                    TelemetryHelpers.GlobalMinAltitudeSeen = localMinAlt[i];
                }
                if (localMaxAlt[i] > TelemetryHelpers.GlobalMaxAltitudeSeen)
                {
                    TelemetryHelpers.GlobalMaxAltitudeSeen = localMaxAlt[i];
                }

                if (localProfiles[i] != null)
                {
                    for (int j = 0; j < localProfiles[i].Count; j++)
                    {
                        ZoneProfile profile = localProfiles[i][j];
                        tempList.Add(profile);
                        ZoneToIndex[profile.ID] = indexCounter++;
                    }
                }
            }

            Grid = tempList.ToArray();

            // Coastal Tagging: tag any zone adjacent to an ocean/land boundary.
            // coastMap is now long[] to carry the full biome mask per zone.
            {
                long[] coastMap = new long[diameter * diameter];
                for (int gi = 0; gi < Grid.Length; gi++)
                {
                    ZoneProfile z = Grid[gi];
                    coastMap[(z.ID.y + radiusZones) * diameter + (z.ID.x + radiusZones)] = z.BiomeMask;
                }

                int coastalTagged = 0;
                for (int gi = 0; gi < Grid.Length; gi++)
                {
                    long bm = Grid[gi].BiomeMask;
                    bool isOcean = (bm & OceanFlags) != 0L;
                    bool isLand = (bm & LandBiomeMask) != 0L;
                    if (!isOcean && !isLand)
                    {
                        continue;
                    }

                    int cx = Grid[gi].ID.x + radiusZones;
                    int cy = Grid[gi].ID.y + radiusZones;
                    bool tagged = false;

                    for (int dx = -1; dx <= 1 && !tagged; dx++)
                    {
                        for (int dz = -1; dz <= 1 && !tagged; dz++)
                        {
                            if (dx == 0 && dz == 0)
                            {
                                continue;
                            }
                            int nx = cx + dx;
                            int ny = cy + dz;
                            if ((uint)nx >= (uint)diameter || (uint)ny >= (uint)diameter)
                            {
                                continue;
                            }
                            long nb = coastMap[ny * diameter + nx];
                            if (nb == 0L)
                            {
                                continue;
                            }

                            if (isOcean && (nb & LandBiomeMask) != 0L)
                            {
                                tagged = true;
                            }
                            else if (isLand && (nb & (long)Heightmap.Biome.Ocean) != 0L)
                            {
                                tagged = true;
                            }
                        }
                    }

                    if (tagged)
                    {
                        Grid[gi].BiomeMask |= CoastalBit;
                        coastalTagged++;
                    }
                }

                DiagnosticLog.WriteTimestampedLog(
                    $"[Survey] Coastal tagging: {coastalTagged:N0} of {Grid.Length:N0} zones tagged.");
            }

            stopwatch.Stop();
            LogSurveySummary(stopwatch.ElapsedMilliseconds);
        }

        private static float[] GetSampleOffsets(int gridSizeP)
        {
            if (gridSizeP <= 1)
            {
                return new[] { 0f };
            }
            float[] offsets = new float[gridSizeP];
            float safeExtent = 30f;
            float step = (safeExtent * 2f) / (gridSizeP - 1);
            for (int i = 0; i < gridSizeP; i++)
            {
                offsets[i] = -safeExtent + (i * step);
            }
            return offsets;
        }

        private static long GetBiomeMask(Vector3 zoneCenterP, int gridSizeP, float[] offsetsP)
        {
            long mask = 0L;
            for (int ox = 0; ox < gridSizeP; ox++)
            {
                for (int oz = 0; oz < gridSizeP; oz++)
                {
                    // (uint) cast first to prevent sign extension when biome bit 31 is set
                    // (EWD's NextBiome can produce 0x80000000). (long)(int) would sign-extend
                    // and corrupt bits 32..63 in the mask, colliding with our synthetic flags.
                    mask |= (long)(uint)(int)WorldGenerator.instance.GetBiome(new Vector3(zoneCenterP.x + offsetsP[ox], 0, zoneCenterP.z + offsetsP[oz]));
                }
            }
            return mask;
        }

        private static int GetAreaMask(Vector3 zoneCenterP, int gridSizeP, float[] offsetsP)
        {
            int mask = 0;
            for (int ox = 0; ox < gridSizeP; ox++)
            {
                for (int oz = 0; oz < gridSizeP; oz++)
                {
                    mask |= (int)WorldGenerator.instance.GetBiomeArea(new Vector3(zoneCenterP.x + offsetsP[ox], 0, zoneCenterP.z + offsetsP[oz]));
                }
            }
            return mask;
        }

        /**
        * Packs the zone's distance from origin into a 10-bit mask where each bit
        * represents a 10% shell of the world radius. A zone whose center is at
        * distance d gets bits set for [floor((d-halfDiag)/wr*10), ceil((d+halfDiag)/wr*10)]
        * so that distance-range filtering can be done with a single bitwise AND.
        * I used 10 bits because most locations work in 0.1*k increments.
        * i.e. they say e.g. "from 0.3 to 0.7 of world radius" (it is normalized). 
        * So 10 bits is enough.
        */
        private static ushort GetDistanceMask(Vector3 zoneCenterP, float worldRadiusP)
        {
            float dist = zoneCenterP.magnitude;
            const float halfDiag = 45.25f;
            int minBit = (int)Mathf.Max(0, (dist - halfDiag) / worldRadiusP * 10f);
            int maxBit = (int)Mathf.Min(9, (dist + halfDiag) / worldRadiusP * 10f);
            ushort mask = 0;
            for (int i = minBit; i <= maxBit; i++)
            {
                mask |= (ushort)(1 << i);
            }
            return mask;
        }

        /**
        * Builds a biome-value -> display-name map. When EWD is active we pull
        * BiomeManager.BiomeToDisplayName via reflection so custom biomes log with
        * their user-defined names ("Summit", "Deep Meadows"). When EWD is absent
        * or reflection fails, vanilla enum names are used.
        */
        private static Dictionary<long, string> BuildBiomeNameMap()
        {
            Dictionary<long, string> map = new Dictionary<long, string>();

            if (Compatibility.IsExpandWorldDataActive)
            {
                try
                {
                    Assembly ewdAssembly = null;
                    foreach (BepInEx.PluginInfo info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                    {
                        if (info.Metadata.GUID == "expand_world_data")
                        {
                            ewdAssembly = info.Instance.GetType().Assembly;
                            break;
                        }
                    }
                    if (ewdAssembly != null)
                    {
                        Type bmType = ewdAssembly.GetType("ExpandWorldData.BiomeManager");
                        if (bmType != null)
                        {
                            FieldInfo nameField = bmType.GetField("BiomeToDisplayName",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            if (nameField != null)
                            {
                                object dictObj = nameField.GetValue(null);
                                if (dictObj is IDictionary dict)
                                {
                                    foreach (DictionaryEntry entry in dict)
                                    {
                                        if (entry.Key == null)
                                        {
                                            continue;
                                        }
                                        long biomeVal = (long)(int)(Heightmap.Biome)entry.Key;
                                        string displayName = entry.Value as string;
                                        if (!string.IsNullOrEmpty(displayName) && !map.ContainsKey(biomeVal))
                                        {
                                            map[biomeVal] = displayName;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fall through to vanilla name map.
                }
            }

            // Ensure vanilla names are always present as a fallback.
            AddIfAbsent(map, (long)Heightmap.Biome.Meadows, "Meadows");
            AddIfAbsent(map, (long)Heightmap.Biome.Swamp, "Swamp");
            AddIfAbsent(map, (long)Heightmap.Biome.Mountain, "Mountain");
            AddIfAbsent(map, (long)Heightmap.Biome.BlackForest, "BlackForest");
            AddIfAbsent(map, (long)Heightmap.Biome.Plains, "Plains");
            AddIfAbsent(map, (long)Heightmap.Biome.AshLands, "AshLands");
            AddIfAbsent(map, (long)Heightmap.Biome.DeepNorth, "DeepNorth");
            AddIfAbsent(map, (long)Heightmap.Biome.Ocean, "Ocean");
            AddIfAbsent(map, (long)Heightmap.Biome.Mistlands, "Mistlands");

            return map;
        }

        private static void AddIfAbsent(Dictionary<long, string> mapP, long keyP, string valueP)
        {
            if (!mapP.ContainsKey(keyP))
            {
                mapP[keyP] = valueP;
            }
        }

        private static void LogSurveySummary(long msP)
        {
            DiagnosticLog.WriteTimestampedLog($"Survey initialization completed in: {msP}ms.");

            Dictionary<long, int> biomeCounts = new Dictionary<long, int>();

            for (int gi = 0; gi < Grid.Length; gi++)
            {
                long bm = Grid[gi].BiomeMask;

                // Iterate all 32 real biome bits so EWD custom biomes are counted.
                for (int bit = 0; bit < 32; bit++)
                {
                    long flag = 1L << bit;
                    if ((bm & flag) != 0L)
                    {
                        bool hasCount = biomeCounts.TryGetValue(flag, out int count);
                        if (!hasCount)
                        {
                            count = 0;
                        }
                        biomeCounts[flag] = count + 1;
                    }
                }
                if ((bm & BiomeBoilingOcean) != 0L)
                {
                    bool hasCount = biomeCounts.TryGetValue(BiomeBoilingOcean, out int count);
                    if (!hasCount)
                    {
                        count = 0;
                    }
                    biomeCounts[BiomeBoilingOcean] = count + 1;
                }
            }

            Dictionary<long, string> nameMap = BuildBiomeNameMap();

            DiagnosticLog.WriteLog("Biome Distribution:");
            List<KeyValuePair<long, int>> sorted = new List<KeyValuePair<long, int>>(biomeCounts);
            sorted.Sort((KeyValuePair<long, int> aP, KeyValuePair<long, int> bP) => bP.Value.CompareTo(aP.Value));

            foreach (KeyValuePair<long, int> kvp in sorted)
            {
                if (kvp.Value == 0)
                {
                    continue;
                }
                string name;
                if (kvp.Key == BiomeBoilingOcean)
                {
                    name = "AshLands (Boiling Ocean)";
                }
                else if (kvp.Key == (long)Heightmap.Biome.AshLands)
                {
                    name = "AshLands (Land)";
                }
                else
                {
                    bool hasName = nameMap.TryGetValue(kvp.Key, out name);
                    if (!hasName || string.IsNullOrEmpty(name))
                    {
                        name = $"Biome 0x{kvp.Key:X}";
                    }
                }
                DiagnosticLog.WriteLog($"   - {name,-25}: {kvp.Value,7:N0} zones");
            }
            DiagnosticLog.WriteLog("");

            if (TelemetryHelpers.GlobalMaxAltitudeSeen > float.MinValue)
            {
                DiagnosticLog.WriteLog($"World Altitude Profile: Min {TelemetryHelpers.GlobalMinAltitudeSeen:F1}m, Max {TelemetryHelpers.GlobalMaxAltitudeSeen:F1}m");
                DiagnosticLog.WriteLog("");
            }

            DiagnosticLog.WriteTimestampedLog($"Placement Starts! Total locations to be placed: {GenerationProgress.TotalRequested}");
            DiagnosticLog.WriteLog("");
        }
    }
}
