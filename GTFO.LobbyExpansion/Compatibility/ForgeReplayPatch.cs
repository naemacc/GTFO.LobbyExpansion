using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace GTFO.LobbyExpansion.Compatibility;

public class ForgeReplayPatch : IModCompatibilityPatch
{
    public const string PluginGuid = "NAinfini.ForgeReplay";

    public static ForgeReplayPatch Instance { get; } = new();

    private static readonly Dictionary<FieldInfo, Array> ExpandedStaticPlayerArrays = [];
    private FieldInfo[] _rosterPlayerArrays = [];
    private bool _rosterCapacityWarningLogged;

    private ForgeReplayPatch() { }

    public bool ShouldApply() => BepInExUtil.IsPluginLoaded(PluginGuid);

    public void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        PatchStaticArrayInitializers(harmony, assembly);
        PatchInstanceConstructors(harmony, assembly);

        var replayScreen = ReflectionUtil.GetRequiredTypeByName("ReplayScreen", assembly);
        _rosterPlayerArrays = [.. new[] { "Names", "Loadouts", "NameColours", "InRecording", "Known", "CardStatus" }
            .Select(name => AccessTools.Field(replayScreen, name)
                ?? throw new MissingFieldException($"Unable to find {replayScreen.FullName}.{name}."))];
        PatchStaticPlayerArrayAccesses(harmony, replayScreen);

        foreach (var methodName in new[]
        {
            "On", "Mark", "Statistics", "Cards", "BuildRating", "ClearRating",
            "RatingSelected", "ApplyRatingSelection", "BuildCompare", "RatingBody", "NameColour",
            "MarkColour", "WorldMarks", "BuildPages"
        })
        {
            PatchPlayerBounds(harmony, replayScreen, methodName);
        }
        PatchMethod(harmony, replayScreen, "Roster", nameof(RosterPlayerBounds_Transpiler));
        PatchMethod(harmony, replayScreen, "Mark", nameof(PlayerLocalArrays_Transpiler));
        PatchMethod(harmony, replayScreen, "Statistics", nameof(PlayerMatrix_Transpiler));
        PatchMethod(harmony, replayScreen, "RatingLayout", nameof(PlayerRatingLayout_Transpiler));
        PatchGeneratedPlayerMethods(harmony, replayScreen, "BuildPages");

        var statsClock = ReflectionUtil.GetRequiredTypeByName("StatsClock", assembly);
        PatchMethod(harmony, statsClock, "Advance", nameof(NewPlayerStatsArray_Transpiler));
        var advance = AccessTools.Method(statsClock, "Advance")!;
        harmony.Patch(advance, postfix: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StatsClock_Advance_Postfix))));

        var recorder = ReflectionUtil.GetRequiredTypeByName("PlayerRecorder", assembly);
        PatchStaticPlayerArrayAccesses(harmony, recorder);
        PatchPlayerBounds(harmony, recorder, "Tick");
        PatchPlayerBounds(harmony, recorder, "FullState");
        PatchOptionalPlayerBounds(harmony, recorder, "NoteCharge");

        var puppeteer = ReflectionUtil.GetRequiredTypeByName("Puppeteer", assembly);
        PatchPlayerBounds(harmony, puppeteer, "SyncPlayers");

        var recorded = ReflectionUtil.GetRequiredTypeByName("Recorded", assembly);
        PatchPlayerBounds(harmony, recorded, "Read");
        PatchSlotMasks(harmony, recorded, "Holder");
        PatchSlotMasks(harmony, recorded, "LoadoutOf");

        var analysis = ReflectionUtil.GetRequiredTypeByName("Analysis", assembly);
        PatchPlayerBounds(harmony, analysis, "Run");
        PatchPlayerBounds(harmony, analysis, "EstimateStruck");
        PatchPlayerBounds(harmony, analysis, "HostTotalsOf");
        PatchPlayerBounds(harmony, analysis, "TakeSnapshot");
        PatchSlotMasks(harmony, analysis, "Take");
        PatchSlotMasks(harmony, analysis, "Infer");
        PatchSlotMasks(harmony, analysis, "WeaponOf");
        PatchSlotMasks(harmony, analysis, "Damaged");
        foreach (var methodName in new[]
        {
            "Tally", "Count", "PacksExact", "HurtExact", "DamageKnown", "KillsKnown",
            "HitsKnown"
        })
        {
            PatchSlotMasks(harmony, analysis, methodName);
        }

        var inference = ReflectionUtil.GetRequiredTypeByName("ClientInference", assembly);
        PatchSlotMasks(harmony, inference, "HealthSeen");
        PatchSlotMasks(harmony, inference, "TakenFraction");
        PatchPlayerBounds(harmony, inference, "Sample");

        var highlights = ReflectionUtil.GetRequiredTypeByName("Highlights", assembly);
        PatchPlayerBounds(harmony, highlights, "Pick");
        PatchSlotMasks(harmony, highlights, "Pick");
        PatchMethod(harmony, highlights, "Pick", nameof(PlayerArrayAlloc_Transpiler));

        var hitMatch = ReflectionUtil.GetRequiredTypeByName("HitMatch", assembly);
        PatchSlotMasks(harmony, hitMatch, "Hit");
        PatchSlotMasks(harmony, hitMatch, "Shot");

        foreach (var methodName in new[] { "WieldedOf", "StandInOf" })
            PatchSlotMasks(harmony, puppeteer, methodName);

        var noiseRebuild = ReflectionUtil.GetRequiredTypeByName("NoiseRebuild", assembly);
        PatchSlotMasks(harmony, noiseRebuild, "Locomotion");
        PatchSlotMasks(harmony, noiseRebuild, "Take");
        PatchPlayerBounds(harmony, noiseRebuild, "Nearest");

        var firstPerson = ReflectionUtil.GetRequiredTypeByName("FirstPerson", assembly);
        PatchSlotMasks(harmony, firstPerson, "Muzzle");

        var replayer = ReflectionUtil.GetRequiredTypeByName("Replayer", assembly);
        PatchSlotMasks(harmony, replayer, "LandedAt");

        var runReport = ReflectionUtil.GetRequiredTypeByName("RunReport", assembly);
        PatchConstructorPlayerBounds(harmony, runReport);
        PatchPlayerBounds(harmony, runReport, "Sample");
        PatchPlayerBounds(harmony, runReport, "TakeEvent");
        PatchSlotMasks(harmony, runReport, "TakeEvent");

        var enemyRecorder = ReflectionUtil.GetRequiredTypeByName("EnemyRecorder", assembly);
        PatchEnemyEntityOffsets(harmony, enemyRecorder);

        var director = ReflectionUtil.GetRequiredTypeByName("Director", assembly);
        PatchPlayerBounds(harmony, director, "Choose");
        PatchPlayerBounds(harmony, director, "EnemyScore");

        var replayCamera = ReflectionUtil.GetRequiredTypeByName("ReplayCamera", assembly);
        PatchPlayerBounds(harmony, replayCamera, "WatchEnemy");

        var replayVersion = ReflectionUtil.GetRequiredTypeByName("Version", assembly);
        PatchSlotMasks(harmony, replayVersion, "Watch");

        var replayList = ReflectionUtil.GetRequiredTypeByName("ReplayList", assembly);
        PatchPlayerBounds(harmony, replayList, "Slots");
        PatchPlayerBounds(harmony, replayList, "RatingText");

        var ratingStore = ReflectionUtil.GetRequiredTypeByName("RatingStore", assembly);
        PatchMethod(harmony, ratingStore, "Record", nameof(PlayerRange_Transpiler));
        PatchPlayerBounds(harmony, ratingStore, "OfRecording");

        var session = ReflectionUtil.GetRequiredTypeByName("Session", assembly);
        PatchGeneratedIterator(harmony, session, "HeaderSteps");

        var timeline = ReflectionUtil.GetRequiredTypeByName("Timeline", assembly);
        PatchSlotMasks(harmony, timeline, "Apply");
        PatchMethod(harmony, replayScreen, "Cards", nameof(PlayerCardLayout_Transpiler));

        var summary = ReflectionUtil.GetRequiredTypeByName("RunSummary", assembly);
        PatchPlayerBounds(harmony, summary, "Decode");

        L.Debug($"Applied patches in {nameof(ForgeReplayPatch)}.");
    }

    // Resize static player arrays at allocation time; this avoids early type initialization and static-readonly field writes.
    // Reflection only inspects third-party arrays matching the player-state name filter; it does not read or write their values here.
#pragma warning disable S3011
    private static void PatchStaticArrayInitializers(Harmony harmony, Assembly assembly)
    {
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StaticPlayerArrays_Transpiler)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(InitializeStaticPlayerArrays_Postfix)));
        foreach (var type in GetTypes(assembly))
        {
            if (type.ContainsGenericParameters)
                continue;

            if (!type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => field.FieldType.IsArray && field.FieldType.GetArrayRank() <= 2 && IsPlayerField(field.Name)))
                continue;

            var constructor = type.TypeInitializer;
            if (constructor is not null)
                harmony.Patch(constructor, postfix: postfix, transpiler: transpiler);
        }
    }
#pragma warning restore S3011

    // Forge Replay static-array reflection is safe because it only initializes labels within its own player-slot array.
    // The private Numbers field is read after its declaring type's initializer and only its expanded player labels are set.
#pragma warning disable S3011
    [HarmonyWrapSafe]
    private static void InitializeStaticPlayerArrays_Postfix(MethodBase __originalMethod)
    {
        if (__originalMethod.DeclaringType?.GetField("Numbers", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) is not string[] numbers)
            return;

        for (var slot = 0; slot < numbers.Length; slot++)
            numbers[slot] = (slot + 1).ToString();
    }
#pragma warning restore S3011

    // Forge Replay constructor reflection is safe because it inspects only array fields whose names match player-slot state.
    // It only installs postfixes on constructors of those types; no unrelated private members are invoked or changed.
#pragma warning disable S3011
    private static void PatchInstanceConstructors(Harmony harmony, Assembly assembly)
    {
        foreach (var type in GetTypes(assembly))
        {
            if (type.ContainsGenericParameters)
                continue;

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field.FieldType.IsArray
                    && field.FieldType.GetArrayRank() <= 2
                    && IsPlayerField(field.Name));

            if (!fields.Any())
                continue;

            foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                harmony.Patch(constructor, postfix: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(ExpandInstanceArrays_Postfix))));
            }
        }
    }
#pragma warning restore S3011

    // Forge Replay postfix reflection is safe because it copies existing values and enlarges only player-slot array fields.
    // Fields are selected by rank and player-state name; the replacement preserves existing contents and only increases capacity.
#pragma warning disable S3011
    [HarmonyWrapSafe]
    private static void ExpandInstanceArrays_Postfix(object __instance)
    {
        if (__instance is null)
            return;

        var type = __instance.GetType();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!field.FieldType.IsArray || field.FieldType.GetArrayRank() is < 1 or > 2 || !IsPlayerField(field.Name))
                continue;

            if (field.GetValue(__instance) is not Array array)
                continue;

            var size = PlayerArrayLength(field.Name);
            if (size <= array.GetLength(0))
                continue;

            var lengths = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray();
            lengths[0] = size;
            var expanded = Array.CreateInstance(field.FieldType.GetElementType()!, lengths);
            Array.Copy(array, expanded, array.Length);
            InitializeExpandedArrayElements(expanded);
            field.SetValue(__instance, expanded);
        }
    }
#pragma warning restore S3011

    private static bool IsPlayerField(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "gearslots" or "loadoutslots" or "itemslots" or "flashnames" or "blockslots" or "hintnames"
            or "_loadout" or "_on" or "_asked")
            return false;

        return lower is "inrecording" or "noaimtold" or "topweapon" or "topenemy" or "tagsplaced" or "numbers" or "chatlabels"
            or "ally" or "seen" or "lastinteract" or "lastthrow" or "blocks" or "gave" or "got"
            || lower.Contains("slot") || lower.Contains("player") || lower.Contains("mate")
            || lower.Contains("holder") || lower.Contains("health") || lower.Contains("infection")
            || lower.Contains("damage")
            || lower.Contains("stamina") || lower.Contains("down") || lower.Contains("aim")
            || lower.Contains("wield") || lower.Contains("kit") || lower.Contains("gear")
            || lower.Contains("boost") || lower.Contains("shot") || lower.Contains("hurt")
            || lower.Contains("rescue") || lower.Contains("pack") || lower.Contains("sentry")
            || lower.Contains("mine") || lower.Contains("ammo") || lower.Contains("present")
            || lower.Contains("status") || lower.Contains("name") || lower.Contains("loadout")
            || lower.Contains("card") || lower.Contains("radar") || lower.Contains("grade")
            || lower.Contains("rating") || lower.Contains("carry") || lower.Contains("everheld")
            || lower.Contains("known") || lower.Contains("parts") || lower.Contains("motion")
            || lower.Contains("place") || lower.Contains("hands") || lower.Contains("hosted")
            || lower.Contains("guessed") || lower.Contains("anchor") || lower.Contains("swing")
            || lower.Contains("charg") || lower.Contains("kindof") || lower.Contains("retry")
            || lower.Contains("asked") || lower.Contains("agents") || lower.Contains("bodies")
            || lower.Contains("totals")
            || lower.Contains("remote");
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
    }

    private static IEnumerable<CodeInstruction> StaticPlayerArrays_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var declaringType = __originalMethod.DeclaringType
            ?? throw new InvalidOperationException("Forge Replay static constructor has no declaring type.");
        var playerFields = GetStaticPlayerArrayFields(declaringType);
        var codes = new List<CodeInstruction>(instructions);

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i + 1].opcode != OpCodes.Newarr
                || !TryGetStoredPlayerField(codes, i + 2, playerFields, out var field, out var storeIndex)
                || !TryGetArraySizeGetter(codes[i], field, out var getter))
                continue;

            var initializeArrayIndex = FindInitializeArrayCall(codes, i + 2, storeIndex);
            if (initializeArrayIndex >= 0)
            {
                var elementType = field.FieldType.GetElementType()!;
                var expand = AccessTools.Method(typeof(ForgeReplayPatch), nameof(ExpandInitializedArray))!
                    .MakeGenericMethod(elementType);
                codes.Insert(initializeArrayIndex + 1, new CodeInstruction(OpCodes.Call, getter));
                codes.Insert(initializeArrayIndex + 2, new CodeInstruction(OpCodes.Call, expand));
            }
            else
            {
                ReplaceIntegerWithCall(codes[i], getter);
            }
        }

        for (var i = 0; i < codes.Count; i++)
        {
            if (!TryGetTwoDimensionalPlayerField(codes, i, playerFields, out var field))
                continue;

            var dimension = FindPlayerDimension(codes, i, field);
            ReplaceIntegerWithCall(codes[dimension], AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers)));
        }

        return codes;
    }

    // Private-field metadata is read only from Forge Replay and filtered to player-array fields before it guides allocation rewrites.
#pragma warning disable S3011
    private static HashSet<FieldInfo> GetStaticPlayerArrayFields(Type type) =>
        [.. type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType.IsArray && field.FieldType.GetArrayRank() is 1 or 2 && IsPlayerField(field.Name))];
#pragma warning restore S3011

    private static bool TryGetStoredPlayerField(List<CodeInstruction> codes, int start, HashSet<FieldInfo> playerFields, out FieldInfo field, out int storeIndex)
    {
        for (var i = start; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Stsfld)
                continue;

            if (codes[i].operand is FieldInfo storedField && playerFields.Contains(storedField))
            {
                field = storedField;
                storeIndex = i;
                return true;
            }

            break;
        }

        field = null!;
        storeIndex = -1;
        return false;
    }

    private static int FindInitializeArrayCall(List<CodeInstruction> codes, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (codes[i].opcode == OpCodes.Call
                && codes[i].operand is MethodInfo method
                && method.DeclaringType == typeof(System.Runtime.CompilerServices.RuntimeHelpers)
                && method.Name == nameof(System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray))
                return i;
        }

        return -1;
    }

    private static bool TryGetArraySizeGetter(CodeInstruction instruction, FieldInfo field, out MethodInfo getter)
    {
        if (field.Name == "LastStatus" && instruction.opcode == OpCodes.Ldc_I4_S
            && Convert.ToInt32(instruction.operand) == 28)
        {
            getter = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetPlayerStatusLength));
            return true;
        }

        if (instruction.opcode == OpCodes.Ldc_I4_4)
        {
            getter = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
            return true;
        }

        getter = null!;
        return false;
    }

    private static bool TryGetTwoDimensionalPlayerField(List<CodeInstruction> codes, int index, HashSet<FieldInfo> playerFields, out FieldInfo field)
    {
        if (codes[index].opcode == OpCodes.Newobj
            && codes[index].operand is ConstructorInfo constructor
            && constructor.DeclaringType?.IsArray == true
            && constructor.DeclaringType.GetArrayRank() == 2
            && TryGetStoredPlayerField(codes, index + 1, playerFields, out field, out _))
            return true;

        field = null!;
        return false;
    }

    private static int FindPlayerDimension(List<CodeInstruction> codes, int arrayConstructor, FieldInfo field)
    {
        var firstDimension = arrayConstructor - 2;
        if (firstDimension >= 0 && codes[firstDimension].opcode == OpCodes.Ldc_I4_4)
            return firstDimension;

        throw new InvalidOperationException($"Unable to find the first player dimension for {field.DeclaringType?.FullName}.{field.Name}.");
    }

    private static T[] ExpandInitializedArray<T>(T[] array, int length)
    {
        if (array.Length >= length)
            return array;

        var expanded = new T[length];
        Array.Copy(array, expanded, array.Length);
        return expanded;
    }

    private static Array GetExpandedStaticPlayerArray(RuntimeFieldHandle fieldHandle)
    {
        var field = FieldInfo.GetFieldFromHandle(fieldHandle);
        return ExpandStaticPlayerArray(field);
    }

    private static Array ExpandStaticPlayerArray(FieldInfo field)
    {
        lock (ExpandedStaticPlayerArrays)
        {
            if (ExpandedStaticPlayerArrays.TryGetValue(field, out var cached))
                return cached;

            if (field.GetValue(null) is not Array array)
                throw new InvalidOperationException($"Forge Replay player field {field.DeclaringType?.FullName}.{field.Name} is not initialized as an array.");

            var length = PlayerArrayLength(field.Name);
            if (array.GetLength(0) >= length)
            {
                ExpandedStaticPlayerArrays.Add(field, array);
                return array;
            }

            var lengths = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray();
            lengths[0] = length;
            var expanded = Array.CreateInstance(field.FieldType.GetElementType()!, lengths);
            Array.Copy(array, expanded, array.Length);
            InitializeExpandedArrayElements(expanded);
            ExpandedStaticPlayerArrays.Add(field, expanded);
            return expanded;
        }
    }

    private static void ReplaceIntegerWithCall(CodeInstruction instruction, MethodInfo getter)
    {
        instruction.opcode = OpCodes.Call;
        instruction.operand = getter;
    }

    private static void PatchPlayerBounds(Harmony harmony, Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName)
            ?? throw new MissingMethodException($"Unable to find {type.FullName}::{methodName}().");
        harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Transpiler))));
    }

    private static void PatchOptionalPlayerBounds(Harmony harmony, Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method is null)
        {
            L.Debug($"Skipping optional Forge Replay method {type.FullName}::{methodName}(); it is not present in this version.");
            return;
        }

        harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Transpiler))));
    }

    private static void PatchSlotMasks(Harmony harmony, Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName)
            ?? throw new MissingMethodException($"Unable to find {type.FullName}::{methodName}().");
        harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(SlotMask_Transpiler))));
    }

#pragma warning disable S3011
    private static void PatchStaticPlayerArrayAccesses(Harmony harmony, Type type)
    {
        if (GetStaticPlayerArrayFields(type).Count == 0)
            return;

        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StaticPlayerArrayAccess_Transpiler)));
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => !method.ContainsGenericParameters && method.GetMethodBody() is not null);
        foreach (var method in methods)
            harmony.Patch(method, transpiler: transpiler);
    }
#pragma warning restore S3011

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> StaticPlayerArrayAccess_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var expand = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetExpandedStaticPlayerArray));
        var i = 0;
        while (i < codes.Count)
        {
            if (codes[i].opcode != OpCodes.Ldsfld
                || codes[i].operand is not FieldInfo field
                || !field.FieldType.IsArray
                || field.FieldType.GetArrayRank() is < 1 or > 2
                || !IsPlayerField(field.Name))
            {
                i++;
                continue;
            }

            codes[i].opcode = OpCodes.Ldtoken;
            codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, expand));
            codes.Insert(i + 2, new CodeInstruction(OpCodes.Castclass, field.FieldType));
            i += 3;
        }

        return codes;
    }

    // Non-public constructors are patched only to expand the instance's player arrays after initialization.
#pragma warning disable S3011
    private static void PatchConstructorPlayerBounds(Harmony harmony, Type type)
    {
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Transpiler)));
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (constructors.Length == 0)
            throw new MissingMethodException($"Unable to find a constructor for {type.FullName}.");

        foreach (var constructor in constructors)
            harmony.Patch(constructor, transpiler: transpiler);
    }
#pragma warning restore S3011

    // Forge Replay's player entities occupy [0, MaxPlayers); shift enemy entities so their IDs remain disjoint.
    // Private method discovery is limited to this fixed list of internal entity-ID helpers.
#pragma warning disable S3011
    private static void PatchEnemyEntityOffsets(Harmony harmony, Type type)
    {
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(EnemyEntityOffset_Transpiler)));
        var methods = new[] { "KeyOf", "Hot", "Mind", "Track", "End", "WriteSpawn", "WriteLook", "WriteMind", "SampleOne" };
        var candidates = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => methods.Contains(method.Name))
            .Where(method => method.Name != "Hot"
                || (method.GetParameters() is [{ ParameterType: var parameterType }] && parameterType == typeof(int)));

        foreach (var method in candidates)
            harmony.Patch(method, transpiler: transpiler);
    }
#pragma warning restore S3011

    // Generated lambdas can live on private compiler-created nested types, so scan the mod assembly for this owner's generated methods.
    // Patching is restricted to methods named for the requested owner method; the reflection does not invoke private game logic.
#pragma warning disable S3011
    private static void PatchGeneratedPlayerMethods(Harmony harmony, Type ownerType, string outerMethodName)
    {
        var boundsTranspiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(GeneratedPlayerBounds_Transpiler)));
        var arraysTranspiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(GeneratedPlayerArrays_Transpiler)));
        var staticArraysTranspiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StaticPlayerArrayAccess_Transpiler)));
        var generatedMethods = GetTypes(ownerType.Assembly)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(method => method.Name.Contains($"<{outerMethodName}>", StringComparison.Ordinal));

        foreach (var method in generatedMethods)
        {
            harmony.Patch(method, transpiler: boundsTranspiler);
            harmony.Patch(method, transpiler: arraysTranspiler);
            harmony.Patch(method, transpiler: staticArraysTranspiler);
        }
    }
#pragma warning restore S3011

    private static void PatchGeneratedIterator(Harmony harmony, Type ownerType, string methodName)
    {
        var stateMachine = GetTypes(ownerType.Assembly)
            .FirstOrDefault(type => type.Name.Contains($"<{methodName}>", StringComparison.Ordinal))
            ?? throw new MissingMethodException($"Unable to find the generated {ownerType.FullName}.{methodName}() iterator.");
        var iteratorMethod = AccessTools.Method(stateMachine, "MoveNext")
            ?? throw new MissingMethodException($"Unable to find MoveNext() for the generated {ownerType.FullName}.{methodName}() iterator.");

        harmony.Patch(iteratorMethod, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Transpiler))));
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string transpilerName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method is null)
            return;

        harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), transpilerName)));
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> RosterPlayerBounds_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getCapacity = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetRosterPlayerCount));
        var codes = new List<CodeInstruction>(instructions);
        var replacements = 0;

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4 || !IsCompareOrBranch(codes[i + 1].opcode))
                continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getCapacity;
            replacements++;
        }

        if (replacements == 0)
            L.Debug("No hardcoded four-player comparison found in Forge Replay ReplayScreen.Roster().");

        return codes;
    }

    private static int GetRosterPlayerCount()
    {
        var instance = Instance;
        var capacity = (int)PluginConfig.MaxPlayers;
        foreach (var field in instance._rosterPlayerArrays)
        {
            var array = ExpandStaticPlayerArray(field);
            capacity = Math.Min(capacity, array.GetLength(0));
        }

        if (capacity < PluginConfig.MaxPlayers && !instance._rosterCapacityWarningLogged)
        {
            L.Warning($"Forge Replay roster arrays are only expanded to {capacity} of {PluginConfig.MaxPlayers} configured player slots.");
            instance._rosterCapacityWarningLogged = true;
        }

        return capacity;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerBounds_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var replacements = 0;

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4)
                continue;

            // skip conv.i8/conv.u8 etc. (ulong comparisons like `num2 > 4` in RunSummary.Decode)
            var next = i + 1;
            while (next < codes.Count && IsConv(codes[next].opcode))
                next++;

            if (next >= codes.Count || !IsCompareOrBranch(codes[next].opcode))
                continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getMax;
            replacements++;
        }

        if (replacements == 0)
            L.Debug($"No hardcoded four-player comparison found in Forge Replay method {__originalMethod.DeclaringType?.FullName}::{__originalMethod.Name}().");

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> GeneratedPlayerBounds_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4 && IsCompareOrBranch(codes[i + 1].opcode))
            {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = getMax;
            }
        }

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> GeneratedPlayerArrays_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4
                && codes[i + 1].opcode == OpCodes.Newarr
                && codes[i + 1].operand is Type elementType
                && elementType.Name == "StatCell")
            {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = getMax;
            }
        }

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerRange_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4
                && codes[i + 1].operand is MethodInfo method
                && method.Name == "Range"
                && method.DeclaringType == typeof(Enumerable))
                matches.Add(i);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one four-slot Enumerable.Range() in Forge Replay rating cache, found {matches.Count}.");

        codes[matches[0]].opcode = OpCodes.Call;
        codes[matches[0]].operand = getMax;
        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerLocalArrays_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var replacements = 0;

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4 || codes[i + 1].opcode != OpCodes.Newarr)
                continue;

            if (codes[i + 1].operand is not Type elementType
                || (elementType != typeof(bool) && elementType != typeof(uint)))
                continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getMax;
            replacements++;
        }

        if (replacements != 2)
            throw new InvalidOperationException($"Expected two player-indexed local arrays in Forge Replay.Mark(), found {replacements}.");

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerRatingLayout_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();

        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_R4
                && codes[i].operand is float value
                && value is > 3.99f and < 4.01f)
                matches.Add(i);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one four-player card-width calculation in Forge Replay.ReplayScreen.RatingLayout(), found {matches.Count}.");

        var index = matches[0];
        codes[index].opcode = OpCodes.Call;
        codes[index].operand = getMax;
        codes.Insert(index + 1, new CodeInstruction(OpCodes.Conv_R4));
        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerCardLayout_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4 || IsCompareOrBranch(codes[i + 1].opcode))
                continue;

            for (var j = i + 1; j < codes.Count && j <= i + 4; j++)
            {
                if (codes[j].opcode == OpCodes.Sub)
                {
                    matches.Add(i);
                    break;
                }

                if (codes[j].opcode == OpCodes.Br || codes[j].opcode == OpCodes.Br_S)
                    break;
            }
        }

        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one four-player layout calculation in Forge Replay.ReplayScreen.Cards(), found {matches.Count}.");

        codes[matches[0]].opcode = OpCodes.Call;
        codes[matches[0]].operand = getMax;
        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerArrayAlloc_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4 && codes[i + 1].opcode == OpCodes.Newarr)
                matches.Add(i);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one four-slot player array in Forge Replay.Highlights.Pick(), found {matches.Count}.");

        codes[matches[0]].opcode = OpCodes.Call;
        codes[matches[0]].operand = getMax;
        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerMatrix_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4
                && codes[i + 1].opcode == OpCodes.Newobj
                && codes[i + 1].operand is ConstructorInfo constructor
                && constructor.DeclaringType?.IsArray == true
                && constructor.DeclaringType.GetArrayRank() == 2
                && constructor.DeclaringType.GetElementType()?.Name == "StatCell")
                matches.Add(i);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected one player-sized StatCell matrix in Forge Replay.Statistics(), found {matches.Count}.");

        codes[matches[0]].opcode = OpCodes.Call;
        codes[matches[0]].operand = getMax;
        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> EnemyEntityOffset_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4)
                continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getMax;
        }

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> SlotMask_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_3 || codes[i + 1].opcode != OpCodes.And)
                continue;

            codes[i].opcode = OpCodes.Nop;
            codes[i].operand = null;
            codes[i + 1].opcode = OpCodes.Nop;
            codes[i + 1].operand = null;
        }

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> NewPlayerStatsArray_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();

        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4 && codes[i + 1].opcode == OpCodes.Newarr)
                matches.Add(i);
        }

        if (matches.Count != 1)
            throw new InvalidOperationException($"Expected exactly 1 PlayerStats[4] allocation in StatsClock.Advance(), found {matches.Count}.");

        var target = codes[matches[0]];
        target.opcode = OpCodes.Call;
        target.operand = getMax;
        return codes;
    }

    [HarmonyWrapSafe]
    private static void StatsClock_Advance_Postfix(object __instance)
    {
        var playersProperty = AccessTools.Property(__instance.GetType(), "Players");
        if (playersProperty?.GetValue(__instance) is not Array players)
            return;

        var playerStatsType = players.GetType().GetElementType()!;
        for (var slot = 0; slot < players.Length; slot++)
        {
            if (players.GetValue(slot) is null)
                players.SetValue(Activator.CreateInstance(playerStatsType, nonPublic: true), slot);
        }
    }

    private static int PlayerArrayLength(string fieldName) => fieldName switch
    {
        "LastStatus" => PluginConfig.MaxPlayers * 7,
        "ChatLabels" => PluginConfig.MaxPlayers + 1,
        _ => PluginConfig.MaxPlayers
    };

    private static int GetPlayerStatusLength() => PluginConfig.MaxPlayers * 7;

    private static void InitializeExpandedArrayElements(Array array)
    {
        var elementType = array.GetType().GetElementType();
        if (elementType?.Name != "PlayerStats"
            && !(elementType?.IsGenericType == true && elementType.GetGenericTypeDefinition().Name == "Shown`1"))
            return;

        for (var slot = 0; slot < array.Length; slot++)
        {
            if (array.GetValue(slot) is null)
                array.SetValue(Activator.CreateInstance(elementType, nonPublic: true), slot);
        }
    }

    private static bool IsConv(OpCode op) =>
        op == OpCodes.Conv_I8 || op == OpCodes.Conv_U8 || op == OpCodes.Conv_I ||
        op == OpCodes.Conv_U || op == OpCodes.Conv_I4 || op == OpCodes.Conv_U4;

    private static bool IsCompareOrBranch(OpCode opcode) => opcode == OpCodes.Clt || opcode == OpCodes.Clt_Un
        || opcode == OpCodes.Cgt || opcode == OpCodes.Cgt_Un
        || opcode == OpCodes.Blt || opcode == OpCodes.Blt_S || opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S
        || opcode == OpCodes.Bge || opcode == OpCodes.Bge_S || opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S
        || opcode == OpCodes.Ble || opcode == OpCodes.Ble_S || opcode == OpCodes.Ble_Un || opcode == OpCodes.Ble_Un_S
        || opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S || opcode == OpCodes.Bgt_Un || opcode == OpCodes.Bgt_Un_S;

    private static int GetMaxPlayers() => PluginConfig.MaxPlayers;
}
