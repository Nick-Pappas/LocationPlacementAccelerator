// v1.3c
/**
* Harmony transpiler patches for the transpiled engine path.
*
* How it all started:
*
* Valheim's location placement lives in ZoneSystem.GenerateLocationsTimeSliced(),
* which is a Unity coroutine. The C# compiler turns coroutines into hidden state
* machine classes. These are not visible in the C# source. They only exist in IL.
*
* Step 1 : Discovery by reflection. I enumerated ZoneSystem's nested types:
*     typeof(ZoneSystem).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public | ...)
*   dumping a list including two compiler-generated types:
*     <GenerateLocationsTimeSliced>d__46
*     <GenerateLocationsTimeSliced>d__48
*   The "d__" prefix is how the C# compiler names coroutine state machines.
*
* Step 2 : IL Sniffer. Before I thought to simply use dnSpy, I wrote a Harmony transpiler
*   that hooked both d__46.MoveNext() and d__48.MoveNext(), dumped every IL instruction
*   to a couple text files (IL_Dump_ZoneSystem_...d__46.txt and d__48.txt), and scanned for the
*   budget constants 100000 and 200000 to confirm which state machine was which.
*   I knew about the 100k and 200k budgets from googling it.
*      
*   This was... not efficient, basicaly ridiculous but it is how I first learned the IL
*   structure of the placement coroutine. I wrote the mod twice over on IL dumps before
*   I had the bright idea that dnSpy exists and I could just decompile the damn thing.
*
* Step 3 : dnSpy decompilation. Opening assembly_valheim.dll in dnSpy gave me readable C#, 
*   which made the coroutine structure obvious:
*
*   d__46 -> the OUTER loop. Iterates over m_locations, sets up each location
*           type (seeds RNG, computes budget), then delegates to d__48 for the actual
*           inner dart-throwing. Contains the fields: m_estimatedGenerateLocationsCompletionTime
*           (set on state 0), the ordered location list, the per-type iteration counter,
*           and the budget limit (100000 for non-prioritized, 200000 for prioritized 
*           verifying that google was not lying).
*           Identified d__46 by scanning for a Stfld to
*           m_estimatedGenerateLocationsCompletionTime (ScanForOuterLoop).
*
*   d__48 -> the INNER placement loop. Receives one ZoneLocation, throws darts
*           (GetRandomPointInZone), checks biome/altitude/terrain/similarity/forest,
*           and calls RegisterLocation on success. Contains all the error counter
*           fields (errorAlt, errorBiome, errorCenterDistance, errorSimilar,
*           errorNotSimilar, etc.) and the placed counter. Identified d__48 by
*           scanning for a Call to GetRandomPointInZone (ScanForInnerLoop).
*
* IL Primer for the uninitiated and for me in say 5 months looking at my code and say wtf:
*
* Intermediate Language is the bytecode that C# compiles to. It is a stack machine: 
* operations push values onto a stack and pop them off. Each IL instruction has an opcode 
* (the verb that tells you what to do) and optionally an operand (the object i.e.* what to do it to).
* OpCodes is a .NET enumeration that defines every possible IL verb.
*
* Key opcodes used in this file:
*   Ldc_I4      — "Load Constant, Integer 4-byte." Pushes an int onto the stack.
*                 Ldc_I4 100000 means "push the number 100000."
*   Ldc_I4_S    — Same but for small constants (fits in a signed byte). Ldc_I4_S 20.
*   Ldc_I4_1    — Shorthand for Ldc_I4 1. Pushes the number 1.
*   Ldc_R8      — "Load Constant, Real 8-byte." Pushes a double. Ldc_R8 30.0 is
*                 vanilla's water plane altitude (ZoneSystem.m_waterLevel = 30f).
*   Ldfld       — "Load Field." Pops an object, pushes the value of one of its fields.
*   Stfld       — "Store Field." Pops a value and an object, writes the value into a field.
*   Ldflda      — "Load Field Address." Like Ldfld but pushes the address (ref/out).
*   Ldarg_0     — Pushes 'this' (the state machine instance, since MoveNext is instance).
*   Ldloc_S     — "Load Local (Short)." Pushes a local variable by index.
*   Stloc_S     — "Store Local (Short)." Pops into a local variable.
*   Ldstr       — "Load String." Pushes a string constant. Vanilla uses Ldstr "error..."
*                 for its error counter logging, which is how we identify error fields.
*   Call        — Calls a method. The operand is a MethodInfo.
*   Callvirt    — Calls a virtual method.
*   Br / Br_S   — Unconditional branch (goto). Operand is a Label.
*   Brtrue      — Branch if the top of stack is true/nonzero.
*   Add         — Pops two values, pushes their sum. Used in "counter++" patterns.
*   Dup         — Duplicates the top of stack (push a copy).
*   Pop         — Discards the top of stack.
*   Ret         — Return from method.
*
* C# pattern matching in IL scanning, the chain idiom...
*
* So throughout this file there are conditions like this beautiful thing:
*   if (codes[i].opcode == OpCodes.Ldc_I4 && codes[i].operand is int val && (val == 100000 || val == 200000))
*
* This is C# pattern matching with lazy evaluation, and it packs a LOT of operations into one line. Reading left to right:
*   1. codes[i].opcode == OpCodes.Ldc_I4  <-- Is this instruction a "push integer"?
*   2. codes[i].operand is int val        <--  Is the operand actually an int? If yes,
*      simultaneously DECLARE a variable named val and ASSIGN the operand's value to it.
*      This is both a type check and an extraction in one expression. Fun times.
*   3. (val == 100000 || val == 200000)     Is it one of vanilla's budget constants?
*
* So all languages that respect themselves have lazy evaluation. If you have "if (P&&Q)" and you
* evaluate P to be false you do not also need to evaluate Q. You call it a day. And if P and Q 
* are independent you could have done "if (Q&&P)" and it would work just as nice and dandy. But...!!
* The short-circuit evaluation is STRUCTURALLY IMPORTANT here, not just an optimization.
* Step 3 uses the variable 'val' which only exists because step 2 succeeded.
* If step 1 fails, step 2 never executes, val is never declared, and step 3 is never
* reached. If you rearrange the clauses, the code breaks. This idiom appears dozens of
* times here. 
*
* The transpilers:
*
*   OuterLoopTranspiler patches d__46 to:
*     1 Replace the hardcoded 100000/200000 budget with Interleaver.GetBudget()
*     2 Inject GenerationProgress.MarkActualStart() after the start timestamp
*     3 Inject ResetAndPrepareForNewLocation() when the current ZoneLocation changes
*     4 Suppress the inner coroutine yield when survey exhaustion cuts it short
*
*   InnerLoopTranspiler patches d__48 to:
*     1 Cache field references (location, counter, limit, error fields) into
*       TranspiledEngineFieldCache for runtime reflection access
*     2 Reorder the filter chain: move similarity checks before terrain checks
*       (similarity is cheaper to reject on, saves terrain delta computation)
*     3 Inject telemetry hooks after altitude errors, distance errors, and
*       biome area mismatches
*     4 Inject progress logging hooks after counter increments
*     5 Inject completion callbacks after RegisterLocation and "Failed to place"
*     6 Scale the inner loop budget (vanilla 20 darts) by InnerMultiplier
*
* WARNING: This is IL manipulation. The opcode patterns, field name matches,
* and label management are extremely fragile. One Valheim update that changes
* the coroutine structure, renames a field, or reorders the filter chain will
* break these transpilers silently. Basically it is half an update from not working. :P
*
* __instance, __result, triple-underscore field names and so on are framework-mandated
* Harmony conventions. I use them where I must.
*/
#nullable disable
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using static ZoneSystem;

namespace LPA
{
    public static class TranspiledEnginePatches
    {
        public static HashSet<string> PatchedTypes = new HashSet<string>();
        public static bool SkipTelemetry = false;
        public static bool SkipAltTrack = false;
        private static bool _insideGetRandomZone = false;
        private static readonly HashSet<string> _reportedViaExhaustion = new HashSet<string>();

        /**
        * Identifies d__48 by checking if its MoveNext IL contains a Call to GetRandomPointInZone.
        * PatchProcessor.GetOriginalInstructions() returns the raw IL instruction list for a method.
        * Each CodeInstruction has an .opcode (the verb) and an .operand (the object).
        * Here: opcode == OpCodes.Call means "this instruction calls a method", and
        * operand is MethodInfo mi means "the operand is a method reference — extract it into mi",
        * then mi.Name == "GetRandomPointInZone" checks if it's the method we are looking for.
        */
        public static bool ScanForInnerLoop(MethodInfo methodP)
        {
            try
            {
                IList<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(methodP);
                for (int i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].opcode == OpCodes.Call && instructions[i].operand is MethodInfo mi && mi.Name == "GetRandomPointInZone")
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // Same scanning pattern as above. Identifies d__46 by looking for a Stfld (store field) targeting m_estimatedGenerateLocationsCompletionTime, a field only d__46 touches.
        public static bool ScanForOuterLoop(MethodInfo methodP)
        {
            try
            {
                IList<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(methodP);
                for (int i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].opcode == OpCodes.Stfld && instructions[i].operand is FieldInfo fi && fi.Name == "m_estimatedGenerateLocationsCompletionTime")
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void OnGameLogout()
        {
            GenerationProgress.ForceCleanup();
        }

        private static object _outerLoopInstance = null;
        private static object _currentInnerLoopInstance = null;

        public static Dictionary<int, int> ActualIterationsByInstance = new Dictionary<int, int>();

        public static int ProxyCountNrOfLocation(ZoneSystem zsP, ZoneLocation locP)
        {
            if (Interleaver.IsGenerating)
            {
                return 0;
            }
            int num = 0;
            foreach (LocationInstance value in zsP.m_locationInstances.Values)
            {
                if (value.m_location.m_prefabName == locP.m_prefabName)
                {
                    num++;
                }
            }
            return num;
        }


        public static void OuterLoopPrefix(object __instance)
        {
            _outerLoopInstance = __instance;

            // MoveNext() executes every frame the coroutine is active.
            // I MUST only clear the memory if I am at the very beginning of map generation.
            if (!Interleaver.IsGenerating)
            {
                _reportedViaExhaustion.Clear();
            }

            if (ZoneSystem.instance != null)
            {
                Interleaver.InterleaveLocations(ZoneSystem.instance);
                GenerationProgress.StartGeneration(ZoneSystem.instance);
            }

            ConstraintRelaxer.CaptureStateMachine(__instance);
        }

        public static void OuterLoopPostfix(ref bool __result)
        {
            if (!__result)
            {
                if (ModConfig.EffectiveMode == PlacementMode.Survey)
                {
                    SurveyMode.DumpDiagnostics();
                }
                GenerationProgress.EndGeneration();
            }
        }

        public static void ResetAndPrepareForNewLocation()
        {
            SurveyMode.SurveyExhausted = false;
            TelemetryHelpers.ResetInnerLoopCounter();
        }

        public static void ResetLocationLog()
        {
        }

        public static void ResetExhaustionReport()
        {
            _reportedViaExhaustion.Clear();
        }

        /**
        * Replaces vanilla's HaveLocationInRange which iterates ALL m_locationInstances.Values
        * (O(N) over every placed location in the world, which is ridiculous) with a 5x5 zone neighborhood lookup.
        * Since m_locationInstances is keyed by zone Vector2i, we compute which zones fall within the similarity radius, 
        * check only those, and do a 3D Euclidean distance test.
        * This is O(K) where K is the number of zones in the radius, typically 9-25.
        */
        public static bool HaveLocationInRangePrefix(ZoneSystem __instance, ref bool __result, string prefabName, string group, Vector3 p, float radius, bool maxGroup)
        {
            if (ModConfig.EffectiveMode == PlacementMode.Vanilla && !ModConfig.OptimizePlacementChecks.Value)
            {
                return true;
            }

            float radiusSqr = radius * radius;
            int zoneRadius = Mathf.CeilToInt(radius / 64f);

            int cx = Mathf.FloorToInt((p.x + 32f) / 64f);
            int cz = Mathf.FloorToInt((p.z + 32f) / 64f);

            for (int z = cz - zoneRadius; z <= cz + zoneRadius; z++)
            {
                for (int x = cx - zoneRadius; x <= cx + zoneRadius; x++)
                {
                    Vector2i zoneId = new Vector2i(x, z);

                    if (__instance.m_locationInstances.TryGetValue(zoneId, out LocationInstance instance))
                    {
                        float dx = instance.m_position.x - p.x;
                        float dy = instance.m_position.y - p.y;
                        float dz2 = instance.m_position.z - p.z;

                        if (dx * dx + dy * dy + dz2 * dz2 < radiusSqr)
                        {
                            if (instance.m_location.m_prefab.Name == prefabName ||
                                (!maxGroup && group != null && group.Length > 0 && group == instance.m_location.m_group) ||
                                (maxGroup && group != null && group.Length > 0 && group == instance.m_location.m_groupMax))
                            {
                                __result = true;
                                return false;
                            }
                        }
                    }
                }
            }

            __result = false;
            return false;
        }

        /**
        * Prefix on d__48.MoveNext(). When survey mode has exhausted all candidate zones
        * for a location type, we need to make vanilla's inner loop exit immediately.
        * The ZPackage trick: vanilla reads its iteration count from a ZPackage (a serialized int).
        * We clear the package and write 0, which makes vanilla think it has 0 iterations left, 
        * so it exits cleanly on its own without us having to patch the loop condition. 
        * This is kind of fragile but avoids a transpiler dependency.
        */
        public static bool InnerLoopPrefix(object __instance, ref bool __result)
        {
            _currentInnerLoopInstance = __instance;
            TranspiledCompletionHandler.CurrentInstanceHash = __instance.GetHashCode();

            if (SurveyMode.SurveyExhausted)
            {
                if (TranspiledEngineFieldCache.IterationsPkgField != null)
                {
                    try
                    {
                        ZPackage pkg = TranspiledEngineFieldCache.IterationsPkgField.GetValue(__instance) as ZPackage;
                        if (pkg != null)
                        {
                            pkg.Clear();
                            pkg.Write(0);
                            pkg.SetPos(0);
                        }
                    }
                    catch { }
                }

                ZoneLocation loc = TranspiledEngineFieldCache.GetLocation(__instance);
                if (loc != null)
                {
                    if (_reportedViaExhaustion.Add(loc.m_prefabName))
                    {
                        TranspiledCompletionHandler.ReportFailure(__instance);
                    }
                }

                __result = false;
                return false;
            }
            return true;
        }

        /**
        * Intercepts vanilla's GetRandomZone() to redirect zone selection to the active
        * placement mode (Filter, Force, or Survey). In Vanilla mode, we let it through
        * unmodified. In Survey mode, if all candidates are exhausted, we force-exhaust
        * the inner loop by setting the counter to the limit (so vanilla's own loop
        * condition terminates nice and clean), saving the actual count for our telemetry.
        */
        public static bool GetRandomZonePrefix(ref Vector2i __result, float range)
        {
            TelemetryHelpers.FilterTotalCalls++;
            if (_insideGetRandomZone)
            {
                return true;
            }

            PlacementMode mode = ModConfig.EffectiveMode;
            ZoneLocation currentLoc = GenerationProgress.CurrentLocation;

            if (currentLoc == null)
            {
                return true;
            }
            if (currentLoc.m_centerFirst)
            {
                return true;
            }

            if (Interleaver.TryLogStart(currentLoc.m_prefabName))
            {
                if (ModConfig.LogSuccesses.Value || ModConfig.DiagnosticMode.Value)
                {
                    TelemetryHelpers.LogLocationStart(currentLoc, mode);
                }
            }

            if (mode == PlacementMode.Vanilla)
            {
                return true;
            }

            try
            {
                _insideGetRandomZone = true;
                float min = currentLoc.m_minDistance;
                float max = ModConfig.WorldRadius;
                if (currentLoc.m_maxDistance > 0.1f)
                {
                    max = currentLoc.m_maxDistance;
                }

                if (mode == PlacementMode.Force)
                {
                    __result = ForceMode.GenerateDonut(min, max);
                    TelemetryHelpers.FilterAcceptedZones++;
                    return false;
                }
                if (mode == PlacementMode.Filter)
                {
                    __result = FilterMode.GenerateSieve(min, max, ModConfig.WorldRadius);
                    TelemetryHelpers.FilterAcceptedZones++;
                    return false;
                }

                if (mode == PlacementMode.Survey)
                {
                    if (SurveyMode.GetZone(currentLoc, out Vector2i surveyResult))
                    {
                        __result = surveyResult;
                        TelemetryHelpers.FilterAcceptedZones++;
                        return false;
                    }

                    if (_currentInnerLoopInstance != null)
                    {
                        Type innerType = _currentInnerLoopInstance.GetType();
                        bool hasLimit = TranspiledEngineFieldCache.LimitFields.TryGetValue(innerType, out FieldInfo limitField);
                        bool hasCounter = TranspiledEngineFieldCache.CounterFields.TryGetValue(innerType, out FieldInfo counterField);

                        if (hasLimit && hasCounter)
                        {
                            try
                            {
                                int limit = (int)limitField.GetValue(_currentInnerLoopInstance);
                                int actual = (int)counterField.GetValue(_currentInnerLoopInstance);
                                ActualIterationsByInstance[_currentInnerLoopInstance.GetHashCode()] = actual;
                                counterField.SetValue(_currentInnerLoopInstance, limit);
                            }
                            catch { }
                        }
                    }

                    __result = Vector2i.zero;
                    return false;
                }

                return true;
            }
            finally
            {
                _insideGetRandomZone = false;
            }
        }

        /**
        * Backwards IL scan that finds the instructions needed to push the current
        * ZoneLocation onto the evaluation stack. We need this because Interleaver.GetBudget()
        * takes (ZoneLocation, int) as arguments, so before we can Call it we must have both
        * the location and the base budget on the stack.
        *
        * We anchor on m_prioritized because vanilla always loads the ZoneLocation right before
        * checking loc.m_prioritized to decide between the 100k and 200k budgets. So we scan
        * backwards from the budget constant until we find a Ldfld m_prioritized, then grab
        * the 1-2 instructions immediately before it (which load the ZoneLocation).
        *
        * Two patterns exist in the IL depending on whether vanilla stored the location in a
        * local variable or accessed it through a field chain:
        *   Pattern A: Ldarg_0 (this), Ldfld (location field), Ldfld m_prioritized
        *   Pattern B: Ldloc_S (local var holding location), Ldfld m_prioritized
        * We return the instructions that reproduce that load sequence.
        */
        private static IEnumerable<CodeInstruction> GetLocationLoadInstructions(List<CodeInstruction> codesP, int startIndexP)
        {
            for (int j = startIndexP; j >= 2; j--)
            {
                if (codesP[j].opcode == OpCodes.Ldfld && codesP[j].operand is FieldInfo f && f.Name == "m_prioritized")
                {
                    CodeInstruction inst1 = codesP[j - 1];
                    CodeInstruction inst2 = codesP[j - 2];

                    if (inst1.opcode == OpCodes.Ldfld && (inst2.opcode == OpCodes.Ldarg_0 || inst2.opcode == OpCodes.Ldloc_S || inst2.opcode == OpCodes.Ldloc_0 || inst2.opcode == OpCodes.Ldloc_1))
                    {
                        List<CodeInstruction> result = new List<CodeInstruction>();
                        result.Add(new CodeInstruction(inst2.opcode, inst2.operand));
                        result.Add(new CodeInstruction(inst1.opcode, inst1.operand));
                        return result;
                    }
                    else if (inst1.opcode == OpCodes.Ldloc_S || inst1.opcode == OpCodes.Ldloc_0 || inst1.opcode == OpCodes.Ldloc_1 || inst1.opcode == OpCodes.Ldloc_2 || inst1.opcode == OpCodes.Ldloc_3)
                    {
                        List<CodeInstruction> result = new List<CodeInstruction>();
                        result.Add(new CodeInstruction(inst1.opcode, inst1.operand));
                        return result;
                    }
                }
            }
            List<CodeInstruction> fallback = new List<CodeInstruction>();
            fallback.Add(new CodeInstruction(OpCodes.Ldnull));
            return fallback;
        }

        /**
        * Transpiler for d__46 (the outer coordinator loop).
        * Two passes: first discovers the limit/counter fields, then rewrites the IL.
        */
        public static IEnumerable<CodeInstruction> OuterLoopTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            Type currentType = original.DeclaringType;

            /**
            * Discovery pass: find the budget limit field and the iteration counter field.
            * Vanilla IL pattern: Ldc_I4 100000 ... Stfld <some_compiler_generated_field>
            * The field that receives the 100000/200000 constant IS the budget limit field.
            * We cannot match it by name (it is mangled and nonsensical), so we find it by 
            * what gets stored into it.
            */
            FieldInfo limitFieldFound = null;
            for (int i = 0; i < codes.Count; i++)
            {
                // "Push integer 100000 or 200000" — this is vanilla's budget constant.
                if (codes[i].opcode == OpCodes.Ldc_I4 && codes[i].operand is int v && (v == 100000 || v == 200000))
                {
                    // Look ahead up to 5 instructions for a Stfld — the field being written to.
                    for (int k = 1; k <= 5 && i + k < codes.Count; k++)
                    {
                        if (codes[i + k].opcode == OpCodes.Stfld)
                        {
                            limitFieldFound = codes[i + k].operand as FieldInfo;
                            TranspiledEngineFieldCache.LimitFields[currentType] = limitFieldFound;
                            break;
                        }
                    }
                }
                /**
                * Now, once we know the limit field, we find the counter field. Vanilla compares them:
                *   Ldfld <counter> ... Ldfld <limit> ... Blt (branch if less than)
                * So the field loaded 2 instructions before a Ldfld of the limit field is the counter.
                */
                if (limitFieldFound != null && codes[i].opcode == OpCodes.Ldfld && (codes[i].operand as FieldInfo) == limitFieldFound && i >= 2 && codes[i - 2].opcode == OpCodes.Ldfld)
                {
                    TranspiledEngineFieldCache.CounterFields[currentType] = codes[i - 2].operand as FieldInfo;
                }
            }

            // Emission pass: yield each instruction, replacing or augmenting at specific patterns.
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instruction = codes[i];

                // Budget replacement: where vanilla pushes 100000 or 200000, we instead push
                // the ZoneLocation and the base budget, then call Interleaver.GetBudget(loc, base)
                // which returns the actual budget (may be multiplied by OuterMultiplier).
                if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int v && (v == 100000 || v == 200000))
                {
                    List<CodeInstruction> loadInsts = new List<CodeInstruction>(GetLocationLoadInstructions(codes, i));
                    loadInsts[0].labels = instruction.labels;

                    for (int li = 0; li < loadInsts.Count; li++)
                    {
                        yield return loadInsts[li];
                    }

                    yield return new CodeInstruction(OpCodes.Ldc_I4, v);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Interleaver), nameof(Interleaver.GetBudget)));
                }
                /**
                * Yield suppression: when survey mode exhausts all candidate zones, the inner loop (d__48) has nothing to do. 
                * Vanilla would still yield back to Unity and wait a frame. We intercept: after the Call to GenerateLocationsTimeSliced,
                * we Dup the enumerator, call MoveNext on it, and if it returns false (exhausted), we Pop the dead enumerator and branch past the yield to save a frame of latency.
                */
                else if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo miGen && miGen.Name == "GenerateLocationsTimeSliced")
                {
                    yield return instruction;

                    Label skipYieldLabel = generator.DefineLabel();
                    Label yieldItLabel = generator.DefineLabel();

                    int retIdx = codes.FindIndex(i, (CodeInstruction cP) => cP.opcode == OpCodes.Ret);
                    if (retIdx != -1 && retIdx + 1 < codes.Count)
                    {
                        codes[retIdx + 1].labels.Add(skipYieldLabel);
                    }

                    if (i + 1 < codes.Count)
                    {
                        codes[i + 1].labels.Add(yieldItLabel);
                    }

                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(System.Collections.IEnumerator), "MoveNext"));
                    yield return new CodeInstruction(OpCodes.Brtrue, yieldItLabel);
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Br, skipYieldLabel);
                }
                else
                {
                    yield return instruction;
                }

                // Timestamp hook: after vanilla stores its start time, inject our own start marker.
                if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo stf
                    && stf.FieldType == typeof(DateTime) && stf.Name.Contains("startTime"))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GenerationProgress), nameof(GenerationProgress.MarkActualStart)));
                }

                // New location hook: when vanilla stores a new ZoneLocation into its field,we inject a call to reset our per-location state (survey exhaustion flag, counters).
                if (codes[i].opcode == OpCodes.Stfld && codes[i].operand is FieldInfo fi && fi.FieldType == typeof(ZoneLocation))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.ResetAndPrepareForNewLocation)));
                }
            }
        }

        /**
        * Transpiler for d__48 (the inner placement loop).
        *
        * This method does two things in sequence:
        *
        * Pass 1 (Field Discovery): Walks the entire IL instruction list once to find and cache
        *   the compiler-generated field references that vanilla uses internally. These fields
        *   have mangled names (e.g. <>7__wrap3) that change between Valheim versions, so I
        *   identify them by TYPE and CONTEXT, not by name:
        *     - ZPackage field         --> IterationsPkgField   (used to force-exhaust the loop)
        *     - ZoneLocation field     --> LocationFields       (the current location being placed)
        *     - Vector2i "zoneID" field --> ZoneIDFields        (the zone that got a placement)
        *     - int field after Ldc_I4 100000/200000 --> LimitFields  (the outer budget cap)
        *     - int field loaded 2 instructions before the limit field comparison --> CounterFields (the iteration counter)
        *     - int field before Ldc_I4_S 20 --> InnerCounterFields (the inner dart counter)
        *     - fields referenced right after Ldstr "error*" --> ErrorFields (errorAlt, errorBiome, etc.)
        *   All discovered fields are stored in TranspiledEngineFieldCache for runtime access
        *   by TranspiledStateExtractor and TelemetryHelpers.
        *
        * Pass 1.5 (Filter Reorder): After field discovery, locates the terrain delta check and
        *   similarity check blocks in the IL, then swaps their order. Vanilla checks terrain
        *   before similarity; I swap them because similarity (PresenceGrid bit read) is cheaper
        *   to reject on than terrain delta (10 random height samples). The swap is done by
        *   extracting the two IL blocks, relabeling their branch targets, and reinserting them
        *   in reversed order.
        *
        * Pass 2 (Injection): Walks the IL again, yielding each instruction while injecting
        *   hooks at specific patterns:
        *     - Ldc_I4_S 20 (inner budget)     --> scale by InnerMultiplier
        *     - Ldfld ZoneLocation              --> inject SetCurrentLocation call
        *     - Ldc_R8 30.0 (water level sub)   --> inject altitude tracking after the store
        *     - Ldstr "Failed to place all"     --> inject ReportFailure callback
        *     - Call LogWarning                  --> suppress (replace with Pop)
        *     - Stfld on counter field           --> inject progress logging
        *     - Call GetRandomPointInZone        --> inject inner loop progress logging
        *     - Call RegisterLocation            --> inject ReportSuccess callback
        *     - Ldfld errorAlt + Ldc_I4_1 + Add --> inject altitude failure telemetry
        *       (backwards IL scan for the nearest float local = altitude, nearest Vector3 local = dart position)
        *     - Ldfld errorCenterDistance + Ldc_I4_1 + Add --> inject distance failure telemetry
        *       (backwards IL scan for the nearest float local after get_magnitude = distance value)
        */
        public static IEnumerable<CodeInstruction> InnerLoopTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            Type currentType = original.DeclaringType;

            float innerMult = ModConfig.InnerMultiplier.Value;

            string lastLogString = "";
            FieldInfo limitFieldFound = null;

            // PASS 1 — Walk the IL once to discover and cache compiler-generated field references.
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instruction = codes[i];

                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo fiPkg && fiPkg.FieldType == typeof(ZPackage))
                {
                    TranspiledEngineFieldCache.IterationsPkgField = fiPkg;
                }
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo fi)
                {
                    if (fi.FieldType == typeof(ZoneLocation))
                    {
                        TranspiledEngineFieldCache.LocationFields[currentType] = fi;
                    }
                    if (fi.FieldType == typeof(Vector2i) && fi.Name.Contains("zoneID"))
                    {
                        TranspiledEngineFieldCache.ZoneIDFields[currentType] = fi;
                    }
                }
                if (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int val && (val == 100000 || val == 200000))
                {
                    for (int k = 1; k <= 5 && i + k < codes.Count; k++)
                    {
                        if (codes[i + k].opcode == OpCodes.Stfld)
                        {
                            limitFieldFound = codes[i + k].operand as FieldInfo;
                            TranspiledEngineFieldCache.LimitFields[currentType] = limitFieldFound;
                            break;
                        }
                    }
                }
                if (instruction.opcode == OpCodes.Ldfld && limitFieldFound != null && (instruction.operand as FieldInfo) == limitFieldFound && i >= 2 && codes[i - 2].opcode == OpCodes.Ldfld)
                {
                    TranspiledEngineFieldCache.CounterFields[currentType] = codes[i - 2].operand as FieldInfo;
                }

                if (instruction.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(instruction.operand) == 20)
                {
                    if (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld)
                    {
                        TranspiledEngineFieldCache.InnerCounterFields[currentType] = codes[i - 1].operand as FieldInfo;
                    }
                }
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string s && s.StartsWith("error"))
                {
                    lastLogString = s.Trim();
                }
                if (instruction.opcode == OpCodes.Ldflda && !string.IsNullOrEmpty(lastLogString) && lastLogString.StartsWith("error"))
                {
                    if (instruction.operand is FieldInfo f)
                    {
                        TranspiledEngineFieldCache.ErrorFields[lastLogString] = f;
                    }
                    lastLogString = "";
                }
            }

            /**
            * PASS 1.5 — Filter chain reorder.
            * Vanilla's d__48 checks terrain delta BEFORE similarity. I swap them so
            * similarity (a single PresenceGrid bit read when using the replaced engine,
            * or a zone-neighborhood scan when using the transpiled engine) runs first.
            * Terrain delta requires 10 random height samples, so rejecting on similarity
            * first saves that computation for darts that would have failed similarity anyway.
            *
            * The swap works by locating the terrain and similarity IL blocks via their
            * anchor calls (GetTerrainDelta and HaveLocationInRange), extracting them,
            * relabeling branch targets so control flow still works after the swap,
            * then reinserting them in reversed order. If either anchor is missing
            * (modded Valheim, different coroutine structure), the swap is silently skipped.
            */
            if (ModConfig.EffectiveMode != PlacementMode.Vanilla || ModConfig.OptimizePlacementChecks.Value)
            {
                try
                {
                    MethodInfo getTerrainDeltaMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.GetTerrainDelta));
                    MethodInfo haveLocationInRangeMethod = AccessTools.Method(typeof(ZoneSystem), nameof(ZoneSystem.HaveLocationInRange), new[] { typeof(string), typeof(string), typeof(Vector3), typeof(float), typeof(bool) });

                    int terrainCall = codes.FindIndex((CodeInstruction cP) => cP.Calls(getTerrainDeltaMethod));
                    int firstSimCall = codes.FindIndex((CodeInstruction cP) => cP.Calls(haveLocationInRangeMethod));

                    if (terrainCall != -1 && firstSimCall != -1 && terrainCall < firstSimCall)
                    {
                        MethodInfo getInstanceMethod = AccessTools.PropertyGetter(typeof(ZoneSystem), "instance");
                        int terrainStart = codes.FindLastIndex(terrainCall, (CodeInstruction cP) => cP.Calls(getInstanceMethod));

                        FieldInfo minSimilarField = AccessTools.Field(typeof(ZoneLocation), "m_minDistanceFromSimilar");
                        int simFieldLoad = codes.FindLastIndex(firstSimCall, (CodeInstruction cP) => cP.LoadsField(minSimilarField));
                        int simStart = simFieldLoad - 2;

                        int errorNotSimilarStfld = codes.FindIndex(firstSimCall, (CodeInstruction cP) => cP.opcode == OpCodes.Stfld && cP.operand is FieldInfo f && f.Name.Contains("errorNotSimilar"));
                        int simFailBranch = codes.FindIndex(errorNotSimilarStfld, (CodeInstruction cP) => cP.opcode == OpCodes.Br || cP.opcode == OpCodes.Br_S);
                        int vegStart = simFailBranch + 1;

                        int terrainEnd = simStart - 1;
                        int simEnd = vegStart - 1;

                        if (terrainStart > -1 && simStart > terrainStart && vegStart > simStart)
                        {
                            List<CodeInstruction> terrainBlock = codes.GetRange(terrainStart, terrainEnd - terrainStart + 1);
                            List<CodeInstruction> simBlock = codes.GetRange(simStart, simEnd - simStart + 1);

                            Label lNewTerrStart = generator.DefineLabel();
                            terrainBlock[0].labels.Add(lNewTerrStart);

                            List<Label> vegLabels = new List<Label>(codes[vegStart].labels);

                            foreach (CodeInstruction inst in simBlock)
                            {
                                if (inst.operand is Label targetLabel && vegLabels.Contains(targetLabel))
                                {
                                    inst.operand = lNewTerrStart;
                                }
                            }

                            // Preserve original labels, swapping them between the reordered blocks.
                            List<Label> lTerrOld = new List<Label>();
                            for (int li = 0; li < terrainBlock[0].labels.Count; li++)
                            {
                                if (!terrainBlock[0].labels[li].Equals(lNewTerrStart))
                                {
                                    lTerrOld.Add(terrainBlock[0].labels[li]);
                                }
                            }
                            List<Label> lSimOld = new List<Label>(simBlock[0].labels);

                            terrainBlock[0].labels.Clear();
                            terrainBlock[0].labels.Add(lNewTerrStart);

                            simBlock[0].labels.Clear();
                            simBlock[0].labels.AddRange(lTerrOld);

                            codes[vegStart].labels.AddRange(lSimOld);

                            codes.RemoveRange(simStart, simBlock.Count);
                            codes.RemoveRange(terrainStart, terrainBlock.Count);

                            codes.InsertRange(terrainStart, simBlock);
                            codes.InsertRange(terrainStart + simBlock.Count, terrainBlock);

                            ModConfig.Log.LogInfo("[LPA] Similarity checks reordered before Terrain Delta.");
                        }
                    }
                }
                catch (Exception eP)
                {
                    ModConfig.Log.LogWarning($"[LPA] Failed to reorder checks, proceeding with original order. Error: {eP.Message}");
                }
            }

            int getRandomZoneIndex = codes.FindIndex((CodeInstruction cP) => cP.opcode == OpCodes.Call && cP.operand is MethodInfo mi && mi.Name == "GetRandomZone");

            // Pass 2: Yield each IL instruction, injecting hooks at the patterns described in the method header.
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction instruction = codes[i];
                OpCode opcode = instruction.opcode;
                object operand = instruction.operand;

                // Placed count proxy: replace vanilla's CountNrOfLocation with our version
                // that returns 0 during interleaved scheduling (we track counts ourselves).
                // Also discover the "placed" field — the field that receives the count.
                if (opcode == OpCodes.Call && operand is MethodInfo miCount && miCount.Name == "CountNrOfLocation")
                {
                    if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Stfld)
                    {
                        TranspiledEngineFieldCache.PlacedFields[currentType] = codes[i + 1].operand as FieldInfo;
                    }

                    CodeInstruction newInst = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TranspiledEnginePatches), nameof(TranspiledEnginePatches.ProxyCountNrOfLocation)));
                    newInst.labels = instruction.labels;
                    yield return newInst;
                    continue;
                }

                // Budget replacement, (same as in OuterLoopTranspiler — replace 100k/200k with GetBudget).
                if (opcode == OpCodes.Ldc_I4 && operand is int val && (val == 100000 || val == 200000))
                {
                    CodeInstruction newInst = new CodeInstruction(OpCodes.Ldarg_0);
                    newInst.labels = instruction.labels;
                    yield return newInst;
                    yield return new CodeInstruction(OpCodes.Ldfld, TranspiledEngineFieldCache.LocationFields[currentType]);
                    yield return new CodeInstruction(OpCodes.Ldc_I4, val);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Interleaver), nameof(Interleaver.GetBudget)));
                    continue;
                }

                // Inner budget scaling: vanilla pushes 20 darts per zone. We scale it by InnerMultiplier.
                // The Ldc_I4_S 20 appears right after a Ldfld (the inner counter comparison).
                if ((opcode == OpCodes.Ldc_I4_S || opcode == OpCodes.Ldc_I4) && Convert.ToInt32(operand) == 20)
                {
                    if (i > 0 && codes[i - 1].opcode == OpCodes.Ldfld)
                    {
                        int newVal = 0;
                        if (innerMult > 0f)
                        {
                            newVal = Mathf.Max(1, Mathf.RoundToInt(20 * innerMult));
                        }
                        CodeInstruction newInst = new CodeInstruction(OpCodes.Ldc_I4, newVal);
                        newInst.labels = instruction.labels;
                        yield return newInst;
                        continue;
                    }
                }

                // Current location: right before GetRandomZone is called, inject a
                // SetCurrentLocation so our telemetry hooks know which type is being placed.
                if (i == getRandomZoneIndex)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, TranspiledEngineFieldCache.LocationFields[currentType]);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TranspiledEngineFieldCache), nameof(TranspiledEngineFieldCache.SetCurrentLocation)));
                }

                /**
                * Altitude tracking: Ldc_R8 30.0 is vanilla subtracting the water plane altitude (ZoneSystem.m_waterLevel = 30m)
                * from raw height to get sea-level-relative altitude.
                * We find the float local that receives the result and inject TrackGlobalAltitude.
                * Forward scan: find the Stloc that stores the computed altitude.
                * Backward scan: find the Vector3 local that holds the dart position.
                * Maybe I should rewrite this as it gives me a headache everytime I look at it.
                */
                if (opcode == OpCodes.Ldc_R8 && operand is double dval && dval == 30.0 && !SkipAltTrack)
                {
                    for (int j = i + 1; j < i + 10 && j < codes.Count; j++)
                    {
                        if ((codes[j].opcode == OpCodes.Stloc || codes[j].opcode == OpCodes.Stloc_S) && codes[j].operand is LocalBuilder altLocal && altLocal.LocalType == typeof(float))
                        {
                            LocalBuilder pointLocal = null;
                            for (int b = i - 1; b >= 0 && b > i - 15; b--)
                            {
                                if ((codes[b].opcode == OpCodes.Ldloc || codes[b].opcode == OpCodes.Ldloc_S) && codes[b].operand is LocalBuilder plb && plb.LocalType == typeof(Vector3))
                                {
                                    pointLocal = plb;
                                    break;
                                }
                            }

                            yield return instruction;
                            int k = i + 1;
                            while (k <= j)
                            {
                                yield return codes[k];
                                k++;
                            }

                            if (pointLocal != null)
                            {
                                yield return new CodeInstruction(OpCodes.Ldloc_S, altLocal);
                                yield return new CodeInstruction(OpCodes.Ldloc_S, pointLocal);
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TelemetryHelpers), nameof(TelemetryHelpers.TrackGlobalAltitude), new[] { typeof(float), typeof(Vector3) }));
                            }
                            else
                            {
                                yield return new CodeInstruction(OpCodes.Ldloc_S, altLocal);
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TelemetryHelpers), nameof(TelemetryHelpers.TrackGlobalAltitude), new[] { typeof(float) }));
                            }

                            // Advance past the instructions we already yielded inline (the altitude store sequence). Without this skip, those instructions would be yielded twice!
                            i = j;
                            goto next_instr; //who does not love a good old fashioned goto. Was using in in Basic in the 80s I will use it here too.
                        }
                    }
                }

                // Failure callback: vanilla pushes "Failed to place all ..." before logging a warning.
                // We inject ReportFailure right before that string so our completion handler fires.
                if (opcode == OpCodes.Ldstr && operand is string str && str.Contains("Failed to place all"))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TranspiledCompletionHandler), nameof(TranspiledCompletionHandler.ReportFailure)));
                }

                // Suppress vanilla warning: replace the LogWarning call with a Pop (discard the string argument that was already pushed). We handle all logging ourselves.
                if (opcode == OpCodes.Call && operand is MethodInfo miLog && miLog.Name == "LogWarning")
                {
                    CodeInstruction popInst = new CodeInstruction(OpCodes.Pop);
                    popInst.labels = instruction.labels;
                    yield return popInst;
                    continue;
                }

                yield return instruction;

                // Progress hook: after vanilla increments its outer iteration counter (Stfld on the counter field), inject a call to LogProgress for heartbeat diagnostics.
                if (opcode == OpCodes.Stfld)
                {
                    bool hasCounterField = TranspiledEngineFieldCache.CounterFields.TryGetValue(currentType, out FieldInfo cf);
                    if (hasCounterField && (operand as FieldInfo) == cf)
                    {
                        if (ModConfig.ProgressInterval.Value > 0)
                        {
                            yield return new CodeInstruction(OpCodes.Ldarg_0);
                            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TelemetryHelpers), nameof(TelemetryHelpers.LogProgress)));
                        }
                    }
                }

                // Inner loop progress: after each Call to GetRandomPointInZone (one dart thrown), inject inner loop progress logging for heartbeat diagnostics.
                if (opcode == OpCodes.Call && operand is MethodInfo miRP && miRP.Name == "GetRandomPointInZone")
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TelemetryHelpers), nameof(TelemetryHelpers.LogInnerLoopProgress)));
                }

                // Sucess callback: after vanilla calls RegisterLocation (a dart hit), inject ReportSuccess.
                if (opcode == OpCodes.Call && operand is MethodInfo miReg && miReg.Name == "RegisterLocation")
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TranspiledCompletionHandler), nameof(TranspiledCompletionHandler.ReportSuccess)));
                }

                // Error counter telemetry: vanilla increments error counters with a Ldfld <errorField>, Ldc_I4_1, Add pattern (load counter, push 1, add). lol. 
                // After each such increment, we inject telemetry that records WHY the dart failed.
                if (opcode == OpCodes.Ldfld && operand is FieldInfo errField)
                {
                    if (i + 3 < codes.Count && codes[i + 1].opcode == OpCodes.Ldc_I4_1 && codes[i + 2].opcode == OpCodes.Add)
                    {
                        if (errField.Name.Contains("errorAlt") && !SkipTelemetry)
                        {
                            /**
                            * Backwards IL scan: find the most recent float local (= computed altitude)
                            * and Vector3 local (= dart position) preceding the errorAlt increment.
                            * These locals were set by vanilla's altitude computation a few instructions
                            * earlier. We need their LocalBuilder references to emit Ldloc_S instructions
                            * that pass their values to our telemetry hook.
                            */
                            LocalBuilder altLocal = null;
                            LocalBuilder pointLocal = null;
                            for (int x = 1; x <= 40 && i - x >= 0; x++)
                            {
                                if (codes[i - x].operand is LocalBuilder lb)
                                {
                                    if (lb.LocalType == typeof(float) && altLocal == null)
                                    {
                                        altLocal = lb;
                                    }
                                    if (lb.LocalType == typeof(Vector3) && pointLocal == null)
                                    {
                                        pointLocal = lb;
                                    }
                                    if (altLocal != null && pointLocal != null)
                                    {
                                        break;
                                    }
                                }
                            }

                            bool hasLocField = TranspiledEngineFieldCache.LocationFields.ContainsKey(currentType);
                            if (altLocal != null && pointLocal != null && hasLocField)
                            {
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldloc_S, altLocal);
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, TranspiledEngineFieldCache.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_minAltitude"));
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, TranspiledEngineFieldCache.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_maxAltitude"));
                                yield return new CodeInstruction(OpCodes.Ldloc_S, pointLocal);
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TelemetryHelpers), nameof(TelemetryHelpers.TrackAltitudeFailure)));
                            }
                        }

                        if (errField.Name.Contains("errorCenterDistance") && !SkipTelemetry)
                        {
                            /**
                            * Again backwards IL scan: find the most recent float local that was stored
                            * immediately after a Call to get_magnitude. That local holds the
                            * computed distance-from-origin value that vanilla just compared against
                            * m_minDistance/m_maxDistance. We need its LocalBuilder to pass it
                            * to our distance failure telemetry hook.
                            */
                            LocalBuilder distLocal = null;
                            for (int x = 1; x <= 30 && i - x >= 0; x++)
                            {
                                if (codes[i - x].opcode == OpCodes.Stloc_S && codes[i - x].operand is LocalBuilder lb && lb.LocalType == typeof(float))
                                {
                                    if (i - x - 1 >= 0 && codes[i - x - 1].opcode == OpCodes.Call && codes[i - x - 1].operand is MethodInfo miMag && miMag.Name == "get_magnitude")
                                    {
                                        distLocal = lb;
                                        break;
                                    }
                                }
                            }

                            bool hasLocField = TranspiledEngineFieldCache.LocationFields.ContainsKey(currentType);
                            if (distLocal != null && hasLocField)
                            {
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldloc_S, distLocal);
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, TranspiledEngineFieldCache.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_minDistance"));
                                yield return new CodeInstruction(OpCodes.Ldarg_0);
                                yield return new CodeInstruction(OpCodes.Ldfld, TranspiledEngineFieldCache.LocationFields[currentType]);
                                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(ZoneLocation), "m_maxDistance"));
                                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TelemetryHelpers), nameof(TelemetryHelpers.TrackDistanceFailure)));
                            }
                        }
                    }
                }

            next_instr:;
            }
        }
    }
}