// v1.0.5
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
*
* 1.0.5  the clean follow-up to 1.04: the diagnostic 
* file-writer machinery is gone (DiagWrite, the dedicated LPA_MinimapDiagnostic.log file, 
* the per-call counters, the LoadImage and EncodeToPNG reflection probes, 
* the path FieldRefs that only existed for * diagnostic logging). 
* The packing fix stays is here to stay.The removal of the duplicate SaveMapTextureDataToDisk 
* invocation stays. If the EWS-regen bug ever needs re-investigation, I can pull the diagnostic 
* from a 1.0.4-tagged git revision; reinventing it from scratch would be a PIA. 
* I do not remember if I fixed the EWS regen... I need to look. 
*/
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private static System.Reflection.MethodInfo _tryLoadMethod;
        private static System.Reflection.MethodInfo _loadMapDataMethod;
        private static System.Reflection.MethodInfo _saveMethod;

        static MinimapParallelizer()
        {
            _tryLoadMethod = AccessTools.Method(typeof(Minimap), "TryLoadMinimapTextureData");
            _loadMapDataMethod = AccessTools.Method(typeof(Minimap), "LoadMapData");
            _saveMethod = AccessTools.Method(typeof(Minimap), "SaveMapTextureDataToDisk");
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
        }

        public static bool Prefix(Minimap __instance)
        {
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

                        // 1.0.7c - match vanilla's GenerateWorldMap line 1392-1395 
                        // exactly. Earlier code used *65.535 with clamp at height=1000; 
                        // vanilla expects *127.5 with clamp at packed=65025 (i.e. 
                        // height ~510m). Vanilla's TryLoadMinimapTextureData unpacks 
                        // with /127.5, so any mismatch in the multiplier corrupts the 
                        // height on the next saved-world load. See header docblock 
                        // for full rationale.
                        int packed = Mathf.Clamp((int)(biomeHeight * 127.5f), 0, 65025);
                        _heightPacked[idx] = new Color32((byte)(packed >> 8), (byte)(packed & 255), 0, byte.MaxValue);
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
                if (_saveMethod != null)
                {
                    _saveMethod.Invoke(instanceP, new object[] { _forestMaskTexture(instanceP), _mapTexture(instanceP), packedTex });
                }
            }
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