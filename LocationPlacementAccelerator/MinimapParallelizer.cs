// v1.0.4
/**
* Replaces vanilla's single-threaded Minimap.GenerateWorldMap() with a
* parallel implementation. Intercepts the Minimap.Update() loop via a
* Harmony prefix, launches a background Task with Parallel.For over
* texture rows, then uploads the computed textures back on the main thread
* once the task completes.
* 
* For vanilla map of 10k radius on a 6 core machine we go from 7s to sub 2s. 
* TODO: use the minimap info that I am ignoring in place of or in addition to the survey. 
*
* 1.0.3: Added Reset() called from TranspiledEnginePatches.OnGameLogout. Without 
* it, _cacheChecked and GenerationComplete stayed true across Game.Logout, so on 
* the second saved-world load of a session the cache gate was skipped, 
* LaunchGeneration was called anyway, and it then nuked the perfectly good map 
* cache on disk via DeleteMapTextureData before regenerating. Single-world-load 
* path worked correctly before this fix; the multi-world lifecycle was the hole.
*
* 1.0.4 diagnostic blocks left in place, commented out. Wild goose chase, 
* hair pulling. Every test scenario I could think of - fresh world, vanilla-
* generated world loaded with LPA, LPA-generated world reloaded, logout-and-
* reload without exiting, with and without MWL, with and without EWD - hit 
* TryLoadMinimapTextureData correctly and returned true. Cannot reproduce the 
* saved-world regeneration bug.
* Either 1.0.3's Reset()-on-Logout fix silently addressed it too, or there's 
* some state I haven't stumbled into yet (maybe EWS? something else?). Leaving 
* the [LPA-DIAG] stuff in commented-out form so it can be re-enabled in 
* 5 minutes if the bug reappears, rather than reinvented from scratch. 
* I bet it will happen the second I compile this.
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace LPA
{
    internal static class MinimapParallelizer
    {
        private static bool _started;
        private static bool _cacheChecked;
        private static Task _task;
        private static Stopwatch _stopwatch;

        private static Color32[] _mapColors;
        private static Color32[] _maskColors;
        private static Color[] _heights;
        private static Color32[] _heightPacked;

        private static volatile int _rowsDone;
        private static int _totalRows;

        // [LPA-DIAG] - per-session Prefix call counter. Reset() zeroes it so each 
        // world load starts at call #1 again. Used to rate-limit the verbose logs, the first 10 Prefix calls log everything,
        // after that we log only state transitions so we don't spam the log file with all those "waiting on task" lines.
        // private static int _diagPrefixCallCount;

        // [LPA-DIAG] - remembers whether the last Prefix call entered the cache block. Used to emit one "cache outcome" summary log line
        // after the decision is made so we have a single find target for "what did we decide about the cache on this world load".
        // private static bool _diagCacheOutcomeLogged;

        public static bool IsGenerating
        {
            get
            {
                return _started && _task != null && !_task.IsCompleted;
            }
        }

        public static volatile bool GenerationComplete;
        public static string DeferredTimingMessage { get; private set; }

        public static float Progress
        {
            get
            {
                if (_totalRows <= 0)
                {
                    return 0f;
                }
                return Math.Min(1f, (float)_rowsDone / _totalRows);
            }
        }

        private static readonly AccessTools.FieldRef<Minimap, bool> _hasGenerated =
            AccessTools.FieldRefAccess<Minimap, bool>("m_hasGenerated");

        private static readonly AccessTools.FieldRef<Minimap, Texture2D> _mapTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_mapTexture");

        private static readonly AccessTools.FieldRef<Minimap, Texture2D> _forestMaskTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");

        private static readonly AccessTools.FieldRef<Minimap, Texture2D> _heightTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");

        private static readonly AccessTools.FieldRef<Minimap, Color> _mistlandsColor =
            AccessTools.FieldRefAccess<Minimap, Color>("m_mistlandsColor");

        // [LPA-DIAG] - path-string FieldRefs so I can read m_forestMaskTexturePath 
        // etc. out of the Minimap instance at the exact moment my Prefix runs. 
        // Vanilla populates these in Start() from ZNet.World.GetRootPath, so if my Prefix fires before those are populated I'll see empty strings here 
        // and TryLoadMinimapTextureData will fail its first guard (string.IsNullOrEmpty check at line 336 of the vanilla source).
        // private static readonly AccessTools.FieldRef<Minimap, string> _forestMaskTexturePath =
        //     AccessTools.FieldRefAccess<Minimap, string>("m_forestMaskTexturePath");
        //
        // private static readonly AccessTools.FieldRef<Minimap, string> _mapTexturePath =
        //     AccessTools.FieldRefAccess<Minimap, string>("m_mapTexturePath");
        //
        // private static readonly AccessTools.FieldRef<Minimap, string> _heightTexturePath =
        //     AccessTools.FieldRefAccess<Minimap, string>("m_heightTexturePath");

        private static System.Reflection.MethodInfo _tryLoadMethod;
        private static System.Reflection.MethodInfo _loadMapDataMethod;

        static MinimapParallelizer()
        {
            _tryLoadMethod = AccessTools.Method(typeof(Minimap), "TryLoadMinimapTextureData");
            _loadMapDataMethod = AccessTools.Method(typeof(Minimap), "LoadMapData");
        }

        /**
        * Clears all static state. Called from TranspiledEnginePatches.OnGameLogout
        * via the Game.Logout Harmony prefix. Without this, the second saved-world 
        * load of a session sees _cacheChecked = true from the previous world, 
        * skips the TryLoadMinimapTextureData path, enters LaunchGeneration, and 
        * then DeleteMapTextureData nukes the good cache on disk before regenerating. 
        * Not catastrophic but a couple seconds of burned CPU and pointless disk 
        * churn every time someone reloads a saved world.
        *
        * If the task is still in flight at logout time (we exited to menu during 
        * the initial 2-second generation), we drop our reference but the Parallel.For
        * keeps running on the background threads until it finishes. Its closure still
        * owns the buffer arrays, so nulling our fields here doesn't free them 
        * immediately I think.
        */
        public static void Reset()
        {
            _started = false;
            _cacheChecked = false;
            _task = null;
            _stopwatch = null;

            _mapColors = null;
            _maskColors = null;
            _heights = null;
            _heightPacked = null;

            _rowsDone = 0;
            _totalRows = 0;

            GenerationComplete = false;
            DeferredTimingMessage = null;

            // [LPA-DIAG]
            // _diagPrefixCallCount = 0;
            // _diagCacheOutcomeLogged = false;
        }

        public static bool Prefix(Minimap __instance)
        {
            // [LPA-DIAG] - entry snapshot. Only the first 10 Prefix calls log 
            // this in a verbose manner.
            // _diagPrefixCallCount++;
            // bool diagVerbose = _diagPrefixCallCount <= 10;
            // if (diagVerbose)
            // {
            //     bool hasGeneratedAtEntry = _hasGenerated(__instance);
            //     bool wgNull = WorldGenerator.instance == null;
            //     bool bcActive = Compatibility.IsBetterContinentsActive;
            //     ModConfig.Log.LogInfo(
            //         $"[LPA-DIAG] Prefix #{_diagPrefixCallCount} entry: " +
            //         $"m_hasGenerated={hasGeneratedAtEntry} " +
            //         $"_cacheChecked={_cacheChecked} " +
            //         $"_started={_started} " +
            //         $"WorldGenerator.instance==null: {wgNull} " +
            //         $"BC active: {bcActive}");
            // }

            if (_hasGenerated(__instance))
            {
                GenerationComplete = true;
                return true;
            }
            if (WorldGenerator.instance == null)
            {
                return true;
            }
            if (Compatibility.IsBetterContinentsActive)
            {
                GenerationComplete = true;
                return true;
            }

            if (!_cacheChecked)
            {
                _cacheChecked = true;

                // [LPA-DIAG] - this is hit this block once per world load (_cacheChecked). Capture everything I might want to know about why TryLoadMinimapTextureData succeeded or failed.
                // DiagLogCacheAttempt(__instance);
                //
                // object rawReturn = null;
                // Exception invokeException = null;
                // try
                // {
                //     rawReturn = _tryLoadMethod.Invoke(__instance, null);
                // }
                // catch (Exception exP)
                // {
                //     // [LPA-DIAG] - vanilla's control log shows TryLoad succeeds on that vaaaaaaa world I made. If I am seeing an exception here that Harmony would otherwise swallow, THAT is the bug.
                //     invokeException = exP;
                // }
                //
                // [LPA-DIAG] - raw return value before the bool cast. If this is null, the `(bool)null` cast below would throw NullReferenceException and I 'd fall through to LaunchGeneration.
                // That is the #1 suspect.
                // ModConfig.Log.LogInfo(
                //     $"[LPA-DIAG] TryLoadMinimapTextureData.Invoke returned: " +
                //     $"{(rawReturn == null ? "NULL" : rawReturn.ToString())} " +
                //     $"(type: {(rawReturn == null ? "<null>" : rawReturn.GetType().FullName)}) " +
                //     $"exception: {(invokeException == null ? "<none>" : invokeException.GetType().Name + ": " + invokeException.Message)}");
                // if (invokeException != null && invokeException.InnerException != null)
                // {
                //     ModConfig.Log.LogInfo(
                //         $"[LPA-DIAG] TryLoad inner exception: " +
                //         $"{invokeException.InnerException.GetType().Name}: {invokeException.InnerException.Message}");
                // }
                //
                // bool cacheHit = false;
                // if (invokeException == null && rawReturn is bool b)
                // {
                //     cacheHit = b;
                // }
                //
                // _diagCacheOutcomeLogged = true;
                // ModConfig.Log.LogInfo(
                //     $"[LPA-DIAG] Cache outcome: {(cacheHit ? "HIT (will load and suppress Update)" : "MISS (will fall through to LaunchGeneration)")}");

                if ((bool)_tryLoadMethod.Invoke(__instance, null))
                {
                    _loadMapDataMethod.Invoke(__instance, null);
                    _hasGenerated(__instance) = true;
                    GenerationComplete = true;
                    return false;
                }
            }

            if (!_started)
            {
                // [LPA-DIAG]  about to regenerate. One more sanity line so the log explicitly shows "yes, entering LaunchGeneration now".
                // ModConfig.Log.LogInfo(
                //     $"[LPA-DIAG] Prefix #{_diagPrefixCallCount}: entering LaunchGeneration " +
                //     $"(_cacheOutcomeLogged={_diagCacheOutcomeLogged})");
                LaunchGeneration(__instance);
                _started = true;
                return false;
            }

            if (!_task.IsCompleted)
            {
                return false;
            }

            if (_task.IsFaulted)
            {
                ModConfig.Log.LogError($"[LPA] Minimap generation failed: {_task.Exception?.InnerException?.Message}");
                _started = false;
                _cacheChecked = false;
                return true;
            }

            UploadTextures(__instance);
            _loadMapDataMethod.Invoke(__instance, null);
            _hasGenerated(__instance) = true;
            GenerationComplete = true;

            _stopwatch.Stop();
            int workers = Math.Max(1, Environment.ProcessorCount - 2);
            DeferredTimingMessage =
                $"[LPA] Minimap generated: {_stopwatch.ElapsedMilliseconds}ms " +
                $"(parallel, {workers} workers, {__instance.m_textureSize}x{__instance.m_textureSize} @ {__instance.m_pixelSize:F1}m/px)";

            _started = false;
            _cacheChecked = false;
            _task = null;
            _mapColors = null;
            _maskColors = null;
            _heights = null;
            _heightPacked = null;

            return false;
        }

        // [LPA-DIAG] - helper that pulls out everything I might care about at the moment of the cache-check. Vanilla's TryLoadMinimapTextureData gates on 
        // (empty path OR missing file OR wrong worldVersion). I log each of the inputs to that gate so the log file tells us which one tripped when I get a MISS.
        // private static void DiagLogCacheAttempt(Minimap instanceP)
        // {
        //     try
        //     {
        //         bool tryLoadMethodNull = _tryLoadMethod == null;
        //
        //         string forestPath = _forestMaskTexturePath(instanceP);
        //         string mapPath = _mapTexturePath(instanceP);
        //         string heightPath = _heightTexturePath(instanceP);
        //
        //         bool forestExists = !string.IsNullOrEmpty(forestPath) && File.Exists(forestPath);
        //         bool mapExists = !string.IsNullOrEmpty(mapPath) && File.Exists(mapPath);
        //         bool heightExists = !string.IsNullOrEmpty(heightPath) && File.Exists(heightPath);
        //
        //         int worldVersion = ZNet.World != null ? ZNet.World.m_worldVersion : -1;
        //         string worldName = ZNet.World != null ? ZNet.World.m_name : "<ZNet.World null>";
        //
        //         ModConfig.Log.LogInfo(
        //             $"[LPA-DIAG] Cache attempt state: " +
        //             $"world='{worldName}' worldVersion={worldVersion} " +
        //             $"_tryLoadMethod==null: {tryLoadMethodNull}");
        //         ModConfig.Log.LogInfo(
        //             $"[LPA-DIAG]   forestPath='{forestPath}' exists={forestExists}");
        //         ModConfig.Log.LogInfo(
        //             $"[LPA-DIAG]   mapPath='{mapPath}' exists={mapExists}");
        //         ModConfig.Log.LogInfo(
        //             $"[LPA-DIAG]   heightPath='{heightPath}' exists={heightExists}");
        //     }
        //     catch (Exception exP)
        //     {
        //         // [LPA-DIAG] - even the diagnostic can fail (reflection on path fields, ZNet.World null at the wrong moment, etc).
        //         // Don't let it take down the real path. lol
        //         ModConfig.Log.LogWarning(
        //             $"[LPA-DIAG] DiagLogCacheAttempt itself threw: " +
        //             $"{exP.GetType().Name}: {exP.Message}");
        //     }
        // }

        private static void LaunchGeneration(Minimap instanceP)
        {
            if (ModConfig.ShowGui.Value)
            {
                ProgressOverlay.EnsureInstance();
            }

            Minimap.DeleteMapTextureData(ZNet.World.m_name);

            int texSize = instanceP.m_textureSize;
            float pixSize = instanceP.m_pixelSize;
            int half = texSize / 2;
            float halfPix = pixSize / 2f;
            int totalPixels = texSize * texSize;

            _mapColors = new Color32[totalPixels];
            _maskColors = new Color32[totalPixels];
            _heights = new Color[totalPixels];
            _heightPacked = new Color32[totalPixels];

            Dictionary<Heightmap.Biome, Color32> biomeColorMap = BuildBiomeColorMap(instanceP);

            Dictionary<Heightmap.Biome, Heightmap.Biome> terrainMap = null;
            if (Compatibility.IsExpandWorldDataActive)
            {
                terrainMap = Compatibility.GetEwdBiomeToTerrainMap();
            }

            _rowsDone = 0;
            _totalRows = texSize;
            _stopwatch = Stopwatch.StartNew();

            int workers = Math.Max(1, Environment.ProcessorCount - 2);// Leave a couple cores free for windows and the gui, everybody else must be ben-huring at ramming speed. 
            ParallelOptions pOpts = new ParallelOptions { MaxDegreeOfParallelism = workers };

            _task = Task.Run(() =>
            {
                Parallel.For(0, texSize, pOpts, (int iP) =>
                {
                    for (int j = 0; j < texSize; j++)
                    {
                        float wx = (j - half) * pixSize + halfPix;
                        float wy = (iP - half) * pixSize + halfPix;

                        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(wx, wy, 0.02f, false);
                        Color color;
                        float biomeHeight = WorldGenerator.instance.GetBiomeHeight(biome, wx, wy, out color, false);

                        int idx = iP * texSize + j;

                        bool hasBiomeColor = biomeColorMap.TryGetValue(biome, out Color32 c);
                        Color32 mapColor = White32;
                        if (hasBiomeColor)
                        {
                            mapColor = c;
                        }
                        _mapColors[idx] = mapColor;

                        Heightmap.Biome terrainBiome = biome;
                        if (terrainMap != null)
                        {
                            bool hasTerrain = terrainMap.TryGetValue(biome, out Heightmap.Biome tb);
                            if (hasTerrain)
                            {
                                terrainBiome = tb;
                            }
                        }

                        _maskColors[idx] = ComputeMaskColor(wx, wy, biomeHeight, terrainBiome);
                        _heights[idx].r = biomeHeight;

                        float clampedHeight = Mathf.Clamp(biomeHeight, 0f, 1000f);
                        int packed = (int)(clampedHeight * 65.535f);
                        _heightPacked[idx] = new Color32((byte)(packed >> 8), (byte)(packed & 255), 0, 255);
                    }
                    Interlocked.Increment(ref _rowsDone);
                });
            });
        }

        private static void UploadTextures(Minimap instanceP)
        {
            _forestMaskTexture(instanceP).SetPixels32(_maskColors);
            _forestMaskTexture(instanceP).Apply();
            _mapTexture(instanceP).SetPixels32(_mapColors);
            _mapTexture(instanceP).Apply();
            _heightTexture(instanceP).SetPixels(_heights);
            _heightTexture(instanceP).Apply();

            Texture2D packedTex = new Texture2D(instanceP.m_textureSize, instanceP.m_textureSize);
            packedTex.SetPixels32(_heightPacked);
            packedTex.Apply();

            if (FileHelpers.LocalStorageSupport == LocalStorageSupport.Supported)
            {
                // [LPA-DIAG] - if the bug turns out to be "we never actually save the cache (wtf), so every load sees no file and regenerates", 
                // then these logs tell us whether SaveMapTextureDataToDisk was resolved, whether the invocation threw, and whether the files 
                // are actually present on disk when the method returns.
                // System.Reflection.MethodInfo saveMethodDiag = AccessTools.Method(typeof(Minimap), "SaveMapTextureDataToDisk");
                // bool saveMethodResolved = saveMethodDiag != null;
                // ModConfig.Log.LogInfo(
                //     $"[LPA-DIAG] UploadTextures: SaveMapTextureDataToDisk resolved={saveMethodResolved}");
                //
                // Exception saveException = null;
                // Stopwatch saveWatch = Stopwatch.StartNew();
                // try
                // {
                //     if (saveMethodResolved)
                //     {
                //         saveMethodDiag.Invoke(instanceP, new object[] { _forestMaskTexture(instanceP), _mapTexture(instanceP), packedTex });
                //     }
                // }
                // catch (Exception exP)
                // {
                //     saveException = exP;
                // }
                // saveWatch.Stop();
                //
                // if (saveException == null)
                // {
                //     // [LPA-DIAG] - post-save file existence check. If these say  "false" after an apparently-successful save, SaveMapTextureDataToDisk 
                //     // hit its own early-return guard (empty path string) and silently did nothing. Cannot be but, pure sanity check.
                //     string forestPath = _forestMaskTexturePath(instanceP);
                //     string mapPath = _mapTexturePath(instanceP);
                //     string heightPath = _heightTexturePath(instanceP);
                //     bool forestOnDisk = !string.IsNullOrEmpty(forestPath) && File.Exists(forestPath);
                //     bool mapOnDisk = !string.IsNullOrEmpty(mapPath) && File.Exists(mapPath);
                //     bool heightOnDisk = !string.IsNullOrEmpty(heightPath) && File.Exists(heightPath);
                //     ModConfig.Log.LogInfo(
                //         $"[LPA-DIAG] UploadTextures: save took {saveWatch.ElapsedMilliseconds}ms. " +
                //         $"forest-on-disk={forestOnDisk} map-on-disk={mapOnDisk} height-on-disk={heightOnDisk}");
                // }
                // else
                // {
                //     ModConfig.Log.LogError(
                //         $"[LPA-DIAG] UploadTextures: SaveMapTextureDataToDisk threw: " +
                //         $"{saveException.GetType().Name}: {saveException.Message}");
                //     if (saveException.InnerException != null)
                //     {
                //         ModConfig.Log.LogError(
                //             $"[LPA-DIAG] Inner: {saveException.InnerException.GetType().Name}: {saveException.InnerException.Message}");
                //     }
                // }

                System.Reflection.MethodInfo saveMethod = AccessTools.Method(typeof(Minimap), "SaveMapTextureDataToDisk");
                saveMethod?.Invoke(instanceP, new object[] { _forestMaskTexture(instanceP), _mapTexture(instanceP), packedTex });
            }
            // [LPA-DIAG] - if we're in a build where LocalStorageSupport isn't supported we'd never save.Irrelevant for a pc but again everything and the kitchen sink
            // else
            // {
            //     ModConfig.Log.LogInfo(
            //         $"[LPA-DIAG] UploadTextures: FileHelpers.LocalStorageSupport={FileHelpers.LocalStorageSupport}, skipping disk save.");
            // }
        }

        private static readonly Color32 White32 = new Color32(255, 255, 255, 255);

        private static Dictionary<Heightmap.Biome, Color32> BuildBiomeColorMap(Minimap instanceP)
        {
            Dictionary<Heightmap.Biome, Color32> map = new Dictionary<Heightmap.Biome, Color32>();
            map[Heightmap.Biome.Meadows] = instanceP.m_meadowsColor;
            map[Heightmap.Biome.Swamp] = instanceP.m_swampColor;
            map[Heightmap.Biome.Mountain] = instanceP.m_mountainColor;
            map[Heightmap.Biome.BlackForest] = instanceP.m_blackforestColor;
            map[Heightmap.Biome.Plains] = instanceP.m_heathColor;
            map[Heightmap.Biome.AshLands] = instanceP.m_ashlandsColor;
            map[Heightmap.Biome.DeepNorth] = instanceP.m_deepnorthColor;
            map[Heightmap.Biome.Ocean] = (Color32)Color.white;
            map[Heightmap.Biome.Mistlands] = _mistlandsColor(instanceP);

            if (Compatibility.IsExpandWorldDataActive)
            {
                Dictionary<Heightmap.Biome, Heightmap.Biome> terrainMap = Compatibility.GetEwdBiomeToTerrainMap();
                if (terrainMap != null)
                {
                    foreach (KeyValuePair<Heightmap.Biome, Heightmap.Biome> kvp in terrainMap)
                    {
                        Color pixelColor = instanceP.GetPixelColor(kvp.Key);
                        map[kvp.Key] = (Color32)pixelColor;
                    }
                }
            }

            return map;
        }


        /**
        * This method replicates the exact logic from Minimap.GenerateWorldMap() that determines the RGB values of the forest mask texture.
        * The mask texture encodes biome-specific features that the minimap shader uses to draw trees, mist, and lava/ash terrain.
        * Valheim's minimap shader (and the better map shader) reads this mask texture to draw biome features:
        * Red channel: Discrete trees (Meadows, Plains, Black Forest)
        * Green channel: Soft mist/terrain gradients (Mistlands)
        * Blue channel: Lava/Ash terrain (Ashlands)
        */
        private static Color32 ComputeMaskColor(float wxP, float wyP, float heightP, Heightmap.Biome biomeP)
        {
            if (heightP < 30f)
            {
                float ashlandsOcean = Mathf.Clamp01(WorldGenerator.GetAshlandsOceanGradient(wxP, wyP));
                return new Color32(0, 0, (byte)(ashlandsOcean * 255f), 0);
            }


            float redChannel = 0f;
            float greenChannel = 0f;
            float blueChannel = 0f;

            // I m gonna leave the ternary operators here even though they suck
            // or I will be scrolling up and down for 10 minutes if I revisit this code.
            if (biomeP == Heightmap.Biome.Meadows)
            {
                redChannel = WorldGenerator.InForest(new Vector3(wxP, 0f, wyP)) ? 1f : 0f;
            }
            else if (biomeP == Heightmap.Biome.Plains)
            {
                redChannel = WorldGenerator.GetForestFactor(new Vector3(wxP, 0f, wyP)) < 0.8f ? 1f : 0f;
            }
            else if (biomeP == Heightmap.Biome.BlackForest)
            {
                redChannel = 1f;
            }
            else if (biomeP == Heightmap.Biome.Mistlands)
            {
                /**
                * Note: This mirrors vanilla logic exactly as I saw it in dnSpy to maintain map visual parity.
                * Mistlands uses the Green channel for its fog overlay. 
                * The S-curve between 1.1 and 1.3 creates the soft 
                * feathered edge of the Mistlands biome on the map. These are vanilla magic numbers.
                */
                float forestFactor = WorldGenerator.GetForestFactor(new Vector3(wxP, 0f, wyP));
                greenChannel = 1f - SmoothStep(1.1f, 1.3f, forestFactor);
            }
            else if (biomeP == Heightmap.Biome.AshLands)
            {
                WorldGenerator.instance.GetAshlandsHeight(wxP, wyP, out Color ashColor, true);
                blueChannel = ashColor.a;
            }

            return new Color32(
                (byte)(redChannel * 255f),
                (byte)(greenChannel * 255f),
                (byte)(blueChannel * 255f),
                0
            );
        }

        /**
         * Vanilla's cubic Hermite interpolation to create a smooth S-curve transition, replicated here.
         * This is used for the Mistlands biome edge gradient in the mask texture.
         * Without this, the biome edges look pixelated or hard. 
         * IG uses this to create a soft, natural gradient where the forest density
         * fades in/out, preventing visual "popping" at the biome borders.
         */
        private static float SmoothStep(float edge0P, float edge1P, float xP)
        {
            float t = Mathf.Clamp01((xP - edge0P) / (edge1P - edge0P));
            return t * t * (3f - 2f * t);
        }
    }
}