using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using MonoMod.Cil;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace GTFO.LobbyExpansion.Compatibility;

public class ForgeReplayPatch : IModCompatibilityPatch
{
    public const string PluginGuid = "NAinfini.ForgeReplay";
    private const string BUILD_PAGES = "BuildPages";
    private static readonly bool VerboseLogging = false; // Set to true temporarily for per-patch diagnostics.

    public static ForgeReplayPatch Instance { get; } = new();

    private static readonly Dictionary<FieldInfo, Array> ExpandedStaticPlayerArrays = [];
    private FieldInfo[] _rosterPlayerArrays = [];
    private bool _rosterCapacityWarningLogged;

    private ForgeReplayPatch() { }

    private static void LogVerbose(string message)
    {
        if (VerboseLogging)
            L.Info($"[{nameof(ForgeReplayPatch)}] {message}");
    }

    private static void LogWarning(string message) => L.Warning($"[{nameof(ForgeReplayPatch)}] {message}");

    public bool ShouldApply() => BepInExUtil.IsPluginLoaded(PluginGuid);

    public void Apply(Harmony harmony)
    {
        LogVerbose("BEGIN verbose patch details.");

        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        PatchStaticArrayInitializers(harmony, assembly);
        PatchInstanceConstructors(harmony, assembly);

        PatchHooks(harmony, assembly);
        var replayScreen = PatchReplayScreen(harmony, assembly);
        var puppeteer = PatchRecorders(harmony, assembly);
        var analysis = PatchAnalysis(harmony, assembly);
        PatchPresentation(harmony, assembly, replayScreen, puppeteer);
        var runReport = PatchRunReport(harmony, assembly);
        PatchDirector(harmony, assembly);
        PatchReplayMetadata(harmony, assembly, replayScreen, runReport, analysis);

        // Optional extras for players 5+. Runs last and is fully isolated: a failure here can never abort the core patch above.
        PatchOptionalExtras(harmony, assembly);

        LogVerbose("END verbose patch details.");
        L.Info($"[{nameof(ForgeReplayPatch)}] Compatibility patch applied.");
    }

    private static void PatchHooks(Harmony harmony, Assembly assembly)
    {
        var hooks = ReflectionUtil.GetRequiredTypeByName("Hooks", assembly);
        PatchPlayerBounds(harmony, hooks, "EnemyDamage");
        PatchSpawnEnemyFinalizer(harmony);

        var presence = AccessTools.Inner(hooks, "Presence");
        if (presence is not null)
            PatchMethodSafe(harmony, presence, "Write", nameof(SlotUpperBound_Transpiler));

        PatchPlayerBounds(harmony, hooks, "NoteCharge");
        var noteCharge = AccessTools.Method(hooks, "NoteCharge");
        if (noteCharge is not null)
        {
            try
            {
                harmony.Patch(noteCharge, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StaticPlayerArrayAccess_Transpiler))));
                LogVerbose("Patched static player-array access in Hooks::NoteCharge().");
            }
            catch (Exception ex)
            {
                LogWarning($"Skipping Forge Replay static-array patch for Hooks.NoteCharge: {ex.Message}");
            }
        }
    }

    private Type PatchReplayScreen(Harmony harmony, Assembly assembly)
    {
        var replayScreen = ReflectionUtil.GetRequiredTypeByName("ReplayScreen", assembly);
        _rosterPlayerArrays = [.. new[] { "Names", "Loadouts", "NameColours", "InRecording", "Known", "CardStatus" }
            .Select(name => AccessTools.Field(replayScreen, name)
                ?? throw new MissingFieldException($"Unable to find {replayScreen.FullName}.{name}."))];
        PatchStaticPlayerArrayAccesses(harmony, replayScreen);

        foreach (var methodName in new[]
        {
            "On", "Mark", "Statistics", "BuildRating", "ClearRating",
            "RatingSelected", "ApplyRatingSelection", "BuildCompare", "RatingBody", "NameColour",
            "MarkColour", "WorldMarks", BUILD_PAGES
        })
        {
            PatchPlayerBounds(harmony, replayScreen, methodName);
        }

        PatchMethod(harmony, replayScreen, "Roster", nameof(RosterPlayerBounds_Transpiler));
        PatchMethod(harmony, replayScreen, "Mark", nameof(PlayerLocalArrays_Transpiler));
        PatchMethod(harmony, replayScreen, "Statistics", nameof(PlayerMatrix_Transpiler));
        PatchMethod(harmony, replayScreen, "RatingLayout", nameof(PlayerRatingLayout_Transpiler));

        foreach (var outerMethod in new[] { BUILD_PAGES, "BuildCompare", "BuildRating", "RatingBody", "BuildTeamTable" })
        {
            try
            {
                PatchGeneratedPlayerMethods(harmony, replayScreen, outerMethod);
            }
            catch (Exception ex)
            {
                LogWarning($"Skipping Forge Replay generated-lambda patch for ReplayScreen.{outerMethod}: {ex.Message}");
            }
        }

        var statsClock = ReflectionUtil.GetRequiredTypeByName("StatsClock", assembly);
        PatchMethod(harmony, statsClock, "Advance", nameof(NewPlayerStatsArray_Transpiler));
        var advance = AccessTools.Method(statsClock, "Advance")!;
        harmony.Patch(advance,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StatsClock_Advance_Postfix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(StatsClock_Advance_Postfix))));
        LogVerbose("Patched StatsClock::Advance() for expanded player statistics.");

        return replayScreen;
    }

    private static Type PatchRecorders(Harmony harmony, Assembly assembly)
    {
        var recorder = ReflectionUtil.GetRequiredTypeByName("PlayerRecorder", assembly);
        PatchStaticPlayerArrayAccesses(harmony, recorder);
        PatchPlayerBounds(harmony, recorder, "Tick");
        PatchPlayerBounds(harmony, recorder, "FullState");

        var puppeteer = ReflectionUtil.GetRequiredTypeByName("Puppeteer", assembly);
        PatchPlayerBounds(harmony, puppeteer, "SyncPlayers");

        return puppeteer;
    }

    private static Type PatchAnalysis(Harmony harmony, Assembly assembly)
    {
        var recorded = ReflectionUtil.GetRequiredTypeByName("Recorded", assembly);
        PatchPlayerBounds(harmony, recorded, "Read");
        PatchSlotMasks(harmony, recorded, "Holder");
        PatchSlotMasks(harmony, recorded, "LoadoutOf");

        var analysis = ReflectionUtil.GetRequiredTypeByName("Analysis", assembly);
        try
        {
            harmony.Patch(AccessTools.Method(analysis, "Run"),
                ilmanipulator: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Manipulator))));
            LogVerbose("Patched Analysis::Run() player bounds.");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Forge Replay patch for Analysis::Run(): {ex.InnerException?.Message ?? ex.Message}");
        }

        PatchPlayerBounds(harmony, analysis, "EstimateStruck");
        PatchPlayerBounds(harmony, analysis, "TakeSnapshot");
        PatchSlotMasks(harmony, analysis, "Take");
        PatchSlotMasks(harmony, analysis, "Infer");
        PatchSlotMasks(harmony, analysis, "WeaponOf");
        PatchSlotMasks(harmony, analysis, "Damaged");

        foreach (var methodName in new[]
        {
            "Tally", "Count", "PacksExact", "HurtExact", "DamageKnown", "KillsKnown", "HitsKnown"
        })
        {
            PatchSlotMasks(harmony, analysis, methodName);
        }

        try
        {
            harmony.Patch(AccessTools.Method(analysis, "Run"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(LogReportFailure_Postfix))));
            LogVerbose("Patched Analysis::Run() report-failure diagnostics.");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Analysis.Run failure logger: {ex.Message}");
        }

        return analysis;
    }

    private static void PatchPresentation(Harmony harmony, Assembly assembly, Type replayScreen, Type puppeteer)
    {
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
        InitializeCardPaging(replayer, replayScreen);
        PatchSlotMasks(harmony, replayer, "LandedAt");
    }

    private static Type PatchRunReport(Harmony harmony, Assembly assembly)
    {
        var runReport = ReflectionUtil.GetRequiredTypeByName("RunReport", assembly);
        PatchConstructorPlayerBounds(harmony, runReport);
        foreach (var constructor in AccessTools.GetDeclaredConstructors(runReport, false))
        {
            try
            {
                harmony.Patch(constructor, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerMatrixAllocations_Transpiler))));
                LogVerbose($"Patched player-matrix allocation in {constructor.DeclaringType?.FullName}..ctor().");
            }
            catch (Exception ex)
            {
                LogWarning($"Skipping Forge Replay matrix patch for {constructor.DeclaringType?.FullName}..ctor: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        PatchPlayerBounds(harmony, runReport, "WeaponsOf");
        PatchMethodSafe(harmony, runReport, "WeaponsOf", nameof(PlayerArrayAlloc_Transpiler));
        PatchPlayerBounds(harmony, runReport, "DownsOf");
        PatchMethodSafe(harmony, runReport, "DownsOf", nameof(DownsArray_Transpiler));
        PatchMethodSafe(harmony, runReport, "AddDown", nameof(SlotUpperBound_Transpiler));
        PatchSlotMasks(harmony, runReport, "Finish");
        PatchPlayerBounds(harmony, runReport, "Sample");
        PatchPlayerBounds(harmony, runReport, "TakeEvent");
        PatchSlotMasks(harmony, runReport, "TakeEvent");
        PatchPlayerBounds(harmony, runReport, "Finish");
        PatchPlayerBounds(harmony, runReport, "Nearest");

        var enemyRecorder = ReflectionUtil.GetRequiredTypeByName("EnemyRecorder", assembly);
        PatchEnemyEntityOffsets(harmony, enemyRecorder);
        return runReport;
    }

    private static void PatchDirector(Harmony harmony, Assembly assembly)
    {
        var director = ReflectionUtil.GetRequiredTypeByName("Director", assembly);
        PatchPlayerBounds(harmony, director, "Choose");
        PatchPlayerBounds(harmony, director, "EnemyScore");

        try
        {
            harmony.Patch(AccessTools.Method(director, "Choose"),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(DirectorChoose_Finalizer))));
            LogVerbose("Patched Director::Choose() finalizer.");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Forge Replay Director.Choose finalizer: {ex.Message}");
        }
    }

    private static void PatchReplayMetadata(Harmony harmony, Assembly assembly, Type replayScreen, Type runReport, Type analysis)
    {
        var replayCamera = ReflectionUtil.GetRequiredTypeByName("ReplayCamera", assembly);
        PatchPlayerBounds(harmony, replayCamera, "WatchEnemy");

        var replayProbe = ReflectionUtil.GetRequiredTypeByName("ReplayProbe", assembly);
        PatchSlotMasks(harmony, replayProbe, "Watch");

        var replayList = ReflectionUtil.GetRequiredTypeByName("ReplayList", assembly);
        PatchPlayerBounds(harmony, replayList, "Slots");
        PatchPlayerBounds(harmony, replayList, "RatingText");

        var ratingStore = ReflectionUtil.GetRequiredTypeByName("RatingStore", assembly);
        PatchMethod(harmony, ratingStore, "Record", nameof(PlayerRange_Transpiler));
        PatchPlayerBounds(harmony, ratingStore, "OfRecording");
        PatchMethod(harmony, ratingStore, "OfRecording", nameof(PlayerArrayAlloc_Transpiler));

        var session = ReflectionUtil.GetRequiredTypeByName("Session", assembly);
        PatchGeneratedIteratorIL(harmony, session, "HeaderSteps");

        var timeline = ReflectionUtil.GetRequiredTypeByName("Timeline", assembly);
        PatchSlotMasks(harmony, timeline, "Apply");

        PatchMethodSafe(harmony, analysis, "Take", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, analysis, "Placed", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, analysis, "Waked", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, runReport, "TakeEvent", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, replayScreen, "Cards", nameof(PlayerCardPage_Transpiler));
        PatchMethodSafe(harmony, replayScreen, "InfoOf", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, replayScreen, "CameraButtons", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, replayScreen, "WatchPlayer", nameof(SlotUpperBound_Transpiler));
        PatchMethodSafe(harmony, replayScreen, "BuildTeamTable", nameof(PlayerRangeAny_Transpiler));
        PatchMethodSafe(harmony, replayScreen, BUILD_PAGES, nameof(PlayerRangeAny_Transpiler));

        var summary = ReflectionUtil.GetRequiredTypeByName("RunSummary", assembly);
        PatchPlayerBounds(harmony, summary, "Decode");
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
                .Any(field => field.FieldType.IsArray && field.FieldType.GetArrayRank() <= 2 && IsPlayerArrayField(field)))
                continue;

            var constructor = type.TypeInitializer;
            if (constructor is not null)
            {
                harmony.Patch(constructor, postfix: postfix, transpiler: transpiler);
                LogVerbose($"Patched static player-array initialization in {type.FullName}.");
            }
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
                LogVerbose($"Patched player-array expansion in {constructor.DeclaringType?.FullName}..ctor().");
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
            ExpandInstanceArray(__instance, field);
    }

    private static void ExpandInstanceArray(object instance, FieldInfo field)
    {
        if (!field.FieldType.IsArray || field.FieldType.GetArrayRank() is < 1 or > 2 || !IsPlayerField(field.Name))
            return;

        if (field.GetValue(instance) is not Array array)
            return;

        var size = PlayerArrayLength(field.Name);
        if (size <= array.GetLength(0))
            return;

        var lengths = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToArray();
        lengths[0] = size;
        var expanded = Array.CreateInstance(field.FieldType.GetElementType()!, lengths);
        Array.Copy(array, expanded, array.Length);
        InitializeExpandedArrayElements(expanded);
        field.SetValue(instance, expanded);
        LogVerbose($"Expanded {instance.GetType().FullName}.{field.Name} from {array.GetLength(0)} to {size} slots.");
    }
#pragma warning restore S3011

    private static bool IsPlayerField(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "gearslots" or "loadoutslots" or "itemslots" or "flashnames" or "blockslots" or "hintnames"
            or "_loadout" or "_on" or "_asked")
            return false;

        return lower is "inrecording" or "noaimtold" or "topweapon" or "topenemy" or "tagsplaced" or "numbers" or "chatlabels" or "_hosted" or "_guessed"
            or "_matchcounts" or "_lastusems" or "_gave" or "_lastdropms" or "_grabbedms"
            or "healedshare" or "foamfired" or "boxesby" or "levelitemheld" or "_in"
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
            .Where(field => field.FieldType.IsArray && field.FieldType.GetArrayRank() is 1 or 2 && IsPlayerArrayField(field))];
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

        if (IsBodyStateField(field) && instruction.opcode == OpCodes.Ldc_I4_S
            && Convert.ToInt32(instruction.operand) == 36)
        {
            getter = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetBodyStateLength));
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

            var length = PlayerArrayLength(field);
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

            if (expanded is string[] strings)
            {
                for (var i = 0; i < strings.Length; i++)
                    strings[i] ??= field.Name == "Numbers"
                        ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "";
            }

            ExpandedStaticPlayerArrays.Add(field, expanded);
            LogVerbose($"Expanded static player array {field.DeclaringType?.FullName}.{field.Name} from {array.GetLength(0)} to {length} slots.");
            return expanded;
        }
    }

    private static void ReplaceIntegerWithCall(CodeInstruction instruction, MethodInfo getter)
    {
        instruction.opcode = OpCodes.Call;
        instruction.operand = getter;
    }

    private static void PlayerBounds_Manipulator(ILContext il)
    {
        var getMax = il.Import(AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers)));
        foreach (var instr in il.Instrs)
        {
            if (instr.OpCode != Mono.Cecil.Cil.OpCodes.Ldc_I4_4)
                continue;

            var next = instr.Next;
            while (next is not null && next.OpCode.Code is Mono.Cecil.Cil.Code.Conv_I8 or Mono.Cecil.Cil.Code.Conv_U8 or Mono.Cecil.Cil.Code.Conv_I or Mono.Cecil.Cil.Code.Conv_U)
                next = next.Next;

            if (next is null || next.OpCode.Code is not (
                Mono.Cecil.Cil.Code.Clt or Mono.Cecil.Cil.Code.Clt_Un or Mono.Cecil.Cil.Code.Cgt or Mono.Cecil.Cil.Code.Cgt_Un
                or Mono.Cecil.Cil.Code.Blt or Mono.Cecil.Cil.Code.Blt_S or Mono.Cecil.Cil.Code.Blt_Un or Mono.Cecil.Cil.Code.Blt_Un_S
                or Mono.Cecil.Cil.Code.Bge or Mono.Cecil.Cil.Code.Bge_S or Mono.Cecil.Cil.Code.Bge_Un or Mono.Cecil.Cil.Code.Bge_Un_S
                or Mono.Cecil.Cil.Code.Ble or Mono.Cecil.Cil.Code.Ble_S or Mono.Cecil.Cil.Code.Bgt or Mono.Cecil.Cil.Code.Bgt_S))
                continue;

            instr.OpCode = Mono.Cecil.Cil.OpCodes.Call;
            instr.Operand = getMax;
        }
    }

    private static void PatchPlayerBounds(Harmony harmony, Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method is null)
        {
            LogWarning($"Skipping Forge Replay PlayerBounds patch for {type.FullName}::{methodName}(): method not found.");
            return;
        }
        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Transpiler))));
            LogVerbose($"Patched player bounds for {type.FullName}::{methodName}().");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Forge Replay patch for {type.FullName}::{methodName}(): {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void PatchSlotMasks(Harmony harmony, Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method is null)
        {
            LogWarning($"Skipping Forge Replay slot-mask patch for {type.FullName}::{methodName}(): method not found.");
            return;
        }

        try
        {
            harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(SlotMask_Transpiler))));
            LogVerbose($"Patched slot masks for {type.FullName}::{methodName}().");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Forge Replay slot-mask patch for {type.FullName}::{methodName}(): {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
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
        {
            harmony.Patch(method, transpiler: transpiler);
            LogVerbose($"Patched static player-array access in {method.DeclaringType?.FullName}::{method.Name}().");
        }
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
                || !IsPlayerArrayField(field))
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
        {
            harmony.Patch(constructor, transpiler: transpiler);
            LogVerbose($"Patched constructor player bounds in {constructor.DeclaringType?.FullName}..ctor().");
        }
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
        {
            harmony.Patch(method, transpiler: transpiler);
            LogVerbose($"Patched enemy entity offsets in {method.DeclaringType?.FullName}::{method.Name}().");
        }
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
            LogVerbose($"Patched generated player method {method.DeclaringType?.FullName}::{method.Name}().");
        }
    }
#pragma warning restore S3011

    // Iterators whose MoveNext holds try/finally blocks (foreach over another iterator) fail Harmony's transpiler
    // pipeline with "IL Compile Error"; the IL manipulator path keeps exception blocks intact.
    private static void PatchGeneratedIteratorIL(Harmony harmony, Type ownerType, string methodName)
    {
        var stateMachine = GetTypes(ownerType.Assembly)
            .FirstOrDefault(type => type.Name.Contains($"<{methodName}>", StringComparison.Ordinal))
            ?? throw new MissingMethodException($"Unable to find the generated {ownerType.FullName}.{methodName}() iterator.");
        var iteratorMethod = AccessTools.Method(stateMachine, "MoveNext")
            ?? throw new MissingMethodException($"Unable to find MoveNext() for the generated {ownerType.FullName}.{methodName}() iterator.");

        try
        {
            harmony.Patch(iteratorMethod, ilmanipulator: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Manipulator))));
            LogVerbose($"Patched {ownerType.FullName}.{methodName}() through the IL manipulator.");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Forge Replay iterator patch for {ownerType.FullName}.{methodName}(): {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string transpilerName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method is null)
            return;

        harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), transpilerName)));
        LogVerbose($"Applied {transpilerName} to {type.FullName}::{methodName}().");
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
            LogVerbose("No hardcoded four-player comparison found in ReplayScreen.Roster().");

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
            LogWarning($"Forge Replay roster arrays are only expanded to {capacity} of {PluginConfig.MaxPlayers} configured player slots.");
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
            LogVerbose($"No hardcoded four-player comparison found in {__originalMethod.DeclaringType?.FullName}::{__originalMethod.Name}().");

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
    private static IEnumerable<CodeInstruction> PlayerRangeAny_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode == OpCodes.Ldc_I4_4
                && codes[i + 1].operand is MethodInfo { Name: "Range" } method
                && method.DeclaringType == typeof(Enumerable))
            {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = getMax;
            }
        }

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
        var getGaps = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetRatingCardGaps));
        var codes = new List<CodeInstruction>(instructions);
        var cardCount = new List<int>();
        var gaps = new List<int>();

        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_R4 || codes[i].operand is not float value)
                continue;

            if (value is > 3.99f and < 4.01f)
                cardCount.Add(i);
            else if (value is > 41.99f and < 42.01f)
                gaps.Add(i);
        }

        if (cardCount.Count != 1 || gaps.Count != 1)
            throw new InvalidOperationException($"Unexpected ReplayScreen.RatingLayout() shape: {cardCount.Count} card counts, {gaps.Count} gap totals (expected 1/1).");

        codes[gaps[0]].opcode = OpCodes.Call;
        codes[gaps[0]].operand = getGaps;

        codes[cardCount[0]].opcode = OpCodes.Call;
        codes[cardCount[0]].operand = getMax;
        codes.Insert(cardCount[0] + 1, new CodeInstruction(OpCodes.Conv_R4));
        return codes;
    }

    private static float GetRatingCardGaps() => (PluginConfig.MaxPlayers - 1) * 14f;

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
    private static IEnumerable<CodeInstruction> PlayerMatrixAllocations_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);

        for (var i = 0; i < codes.Count - 2; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4
                || codes[i + 2].opcode != OpCodes.Newobj
                || codes[i + 2].operand is not ConstructorInfo { DeclaringType: { IsArray: true } arrayType }
                || arrayType.GetArrayRank() != 2)
                continue;

            // first dimension is always the player slot
            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getMax;

            // square player x player matrices (FriendlyDamage[4, 4])
            if (codes[i + 1].opcode == OpCodes.Ldc_I4_4)
            {
                codes[i + 1].opcode = OpCodes.Call;
                codes[i + 1].operand = getMax;
            }
        }

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

            // "Fields.Position + 4" is a byte offset to the Y component, not a player count.
            if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Add)
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

        var fill = AccessTools.Method(typeof(ForgeReplayPatch), nameof(StatsClock_Advance_Postfix));
        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i].operand is MethodInfo { Name: "set_Players" })
            {
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
                codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, fill));
                break;
            }
        }

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

    private static bool IsBodyStateField(FieldInfo field) =>
    field.DeclaringType?.Name == "BodyRecorder" && field.Name is "Last" or "LastTime";

    private static bool IsPlayerArrayField(FieldInfo field) =>
        IsPlayerField(field.Name) || IsBodyStateField(field);

    private static int GetBodyStateLength() => PluginConfig.MaxPlayers * 9;
    private static int PlayerArrayLength(FieldInfo field) =>
        IsBodyStateField(field) ? PluginConfig.MaxPlayers * 9 : PlayerArrayLength(field.Name);

    private static int PlayerArrayLength(string fieldName) => fieldName switch
    {
        "LastStatus" => PluginConfig.MaxPlayers * 7,
        "ChatLabels" => PluginConfig.MaxPlayers + 1,
        _ => PluginConfig.MaxPlayers
    };

    private static int GetPlayerStatusLength() => PluginConfig.MaxPlayers * 7;

    private static Exception? DirectorChoose_Finalizer(Exception? __exception, int current, ref int __result)
    {
        if (__exception is null)
            return null;

        __result = current;
        return null;
    }

    private static void InitializeExpandedArrayElements(Array array)
    {
        var elementType = array.GetType().GetElementType();
        if (elementType?.Name != "PlayerStats"
            && elementType?.Name != "Series"
            && elementType?.Name != "RadarChart"
            && !(elementType?.IsGenericType == true
                && elementType.GetGenericTypeDefinition().Name is "Shown`1" or "List`1"))
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

    private const int CardsPerPage = 8;
    private static MethodInfo? _followGetter;
    private static FieldInfo? _inRecordingField;
    private static int _cardPageFrame = -1;
    private static int _cardPageStart;
    private static int _cardPageEnd;

    private static void InitializeCardPaging(Type replayer, Type replayScreen)
    {
        _followGetter = AccessTools.PropertyGetter(replayer, "Follow");
        _inRecordingField = AccessTools.Field(replayScreen, "InRecording");
    }

    private static void PatchMethodSafe(Harmony harmony, Type type, string methodName, string transpilerName)
    {
        try
        {
            PatchMethod(harmony, type, methodName, transpilerName);
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping Forge Replay patch for {type.FullName}::{methodName}(): {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void RefreshCardPage()
    {
        var frame = UnityEngine.Time.frameCount;
        if (frame == _cardPageFrame)
            return;

        _cardPageFrame = frame;

        var used = 4;
        if (_inRecordingField is not null && ExpandStaticPlayerArray(_inRecordingField) is bool[] inRecording)
        {
            for (var i = inRecording.Length - 1; i >= 0; i--)
            {
                if (!inRecording[i])
                    continue;

                used = Math.Max(used, i + 1);
                break;
            }
        }

        used = Math.Max(1, Math.Min(used, PluginConfig.MaxPlayers));

        var follow = _followGetter?.Invoke(null, null) is int value ? value : 0;
        follow = Math.Clamp(follow, 0, used - 1);

        _cardPageStart = follow / CardsPerPage * CardsPerPage;
        _cardPageEnd = Math.Min(_cardPageStart + CardsPerPage, used);
    }

    private static int GetCardPageStart() { RefreshCardPage(); return _cardPageStart; }
    private static int GetCardPageEnd() { RefreshCardPage(); return _cardPageEnd; }
    private static int GetCardPageCapacity() { RefreshCardPage(); return Math.Max(1, _cardPageEnd - _cardPageStart); }
    private static int GetLastPlayerSlot() => PluginConfig.MaxPlayers - 1;

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> PlayerCardPage_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var pageStart = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetCardPageStart));
        var pageEnd = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetCardPageEnd));
        var capacity = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetCardPageCapacity));
        var codes = new List<CodeInstruction>(instructions);
        int loops = 0, bounds = 0, layouts = 0;

        for (var i = 0; i < codes.Count - 2; i++)
        {
            var op = codes[i].opcode;
            if (op == OpCodes.Ldc_I4_0
                && codes[i + 1].opcode.Name!.StartsWith("stloc", StringComparison.Ordinal)
                && (codes[i + 2].opcode == OpCodes.Br || codes[i + 2].opcode == OpCodes.Br_S))
            {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = pageStart;
                loops++;
            }
            else if (op == OpCodes.Ldc_I4_4)
            {
                if (IsCompareOrBranch(codes[i + 1].opcode))
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = pageEnd;
                    bounds++;
                }
                else if (codes[i + 1].opcode.Name!.StartsWith("ldloc", StringComparison.Ordinal)
                    && codes[i + 2].opcode == OpCodes.Sub)
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = capacity;
                    layouts++;
                }
            }
        }

        if (loops != 2 || bounds != 2 || layouts != 1)
            throw new InvalidOperationException($"Unexpected ReplayScreen.Cards() shape: {loops} loop starts, {bounds} loop bounds, {layouts} layout terms (expected 2/2/1).");

        return codes;
    }

    // WatchPlayer only accepts slots 0..3 ("slot <= 3").
    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> SlotUpperBound_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getLast = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetLastPlayerSlot));
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_3)
                continue;

            var next = i + 1;
            while (next < codes.Count && IsConv(codes[next].opcode))
                next++;

            if (next >= codes.Count)
                break;

            var name = codes[next].opcode.Name ?? "";
            if (!(name.StartsWith("ble") || name.StartsWith("bgt") || name.StartsWith("cgt")))
                continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getLast;
        }

        return codes;
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> DownsArray_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var fill = AccessTools.Method(typeof(ForgeReplayPatch), nameof(FillWithMinusOne));
        var codes = new List<CodeInstruction>(instructions);
        for (var i = 0; i < codes.Count - 1; i++)
        {
            if (codes[i].opcode != OpCodes.Ldc_I4_4
                || codes[i + 1].opcode != OpCodes.Newarr
                || codes[i + 1].operand is not Type elementType
                || elementType != typeof(long))
                continue;

            codes[i].opcode = OpCodes.Call;
            codes[i].operand = getMax;
            codes.Insert(i + 2, new CodeInstruction(OpCodes.Dup));
            codes.Insert(i + 3, new CodeInstruction(OpCodes.Call, fill));

            // The compiler's constant-blob initializer only fits the original 4 elements; the fill above replaces it.
            for (var j = i + 4; j < codes.Count; j++)
            {
                if (codes[j].operand is MethodInfo { Name: "InitializeArray" })
                {
                    codes[j].opcode = OpCodes.Pop;   // consumes the field handle
                    codes[j].operand = null;
                    codes.Insert(j + 1, new CodeInstruction(OpCodes.Pop));   // consumes the array
                    return codes;
                }
            }

            throw new InvalidOperationException("RunReport.DownsOf() InitializeArray call not found after the long[4] allocation.");
        }

        throw new InvalidOperationException("RunReport.DownsOf() long[4] allocation not found.");
    }

    // Diagnostic: Analysis.Run swallows report failures into ReportFailure; surface them.
    // The known third-party result property is read only to report a string; no unrelated members are invoked or changed.
#pragma warning disable S3011
    private static void LogReportFailure_Postfix(object __result)
    {
        var failure = __result?.GetType()
            .GetProperty("ReportFailure", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(__result) as string;

        if (!string.IsNullOrEmpty(failure))
            LogWarning($"Forge Replay run report failed: {failure}");
    }
#pragma warning restore S3011

    private static void FillWithMinusOne(long[] array) => Array.Fill(array, -1L);

    private static void PatchSpawnEnemyFinalizer(Harmony harmony)
    {
        var allocator = AccessTools.TypeByName("Enemies.EnemyAllocator");
        var spawn = allocator is null ? null : AccessTools.GetDeclaredMethods(allocator).FirstOrDefault(method => method.Name == "SpawnEnemy");
        if (spawn is null)
            return;

        try
        {
            harmony.Patch(spawn, finalizer: new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(SpawnEnemy_Finalizer))));
            LogVerbose("Patched EnemyAllocator::SpawnEnemy() finalizer.");
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping SpawnEnemy finalizer: {ex.Message}");
        }
    }

    private static Exception? SpawnEnemy_Finalizer(Exception? __exception)
        => __exception is not null && __exception.ToString().Contains("TumorShadowFix", StringComparison.Ordinal) ? null : __exception;

    // ------------------------------------------------------------------------------------------------
    // Optional extras (Forge Replay 1.0.3). Every step is wrapped, so a failure only skips that step.
    // ------------------------------------------------------------------------------------------------
    private static void PatchOptionalExtras(Harmony harmony, Assembly assembly)
    {
        Optional("stackalloc player spans", () =>
        {
            PatchMethodSafe(harmony, ReflectionUtil.GetRequiredTypeByName("Director", assembly), "Choose", nameof(StackallocPlayerSpan_Transpiler));
            PatchMethodSafe(harmony, ReflectionUtil.GetRequiredTypeByName("HitMatch", assembly), "Match", nameof(StackallocPlayerSpan_Transpiler));
        });

        Optional("Analysis loops / slot guards", () =>
        {
            var analysis = ReflectionUtil.GetRequiredTypeByName("Analysis", assembly);
            foreach (var name in new[] { "Take", "Wiped", "Rescued", "NearestPlayer" })
                PatchPlayerBounds(harmony, analysis, name);
            PatchMethodSafe(harmony, analysis, "EstimateStruck", nameof(SlotUpperBound_Transpiler));
            PatchSlotMasks(harmony, analysis, "EstimateShot");
            // NOTE: Analysis.Run intentionally NOT given SlotUpperBound - it caused "IL Compile Error" in testing.
        });

        Optional("ClientInference loops / slot guards", () =>
        {
            var inference = ReflectionUtil.GetRequiredTypeByName("ClientInference", assembly);
            foreach (var name in new[] { "Woke", "Reviver", "Retracted" })
                PatchPlayerBounds(harmony, inference, name);
            PatchPlayerBoundsAllOverloads(harmony, inference, "Settle"); // Settle(Drop) / Settle(Wakeup) are overloaded
            PatchSlotMasks(harmony, inference, "Take");
            PatchMethodSafe(harmony, inference, "Take", nameof(SlotUpperBound_Transpiler));
            PatchMethodSafe(harmony, inference, "Retracted", nameof(SlotUpperBound_Transpiler));
        });

        Optional("director result >= 4", () =>
            PatchPlayerBounds(harmony, ReflectionUtil.GetRequiredTypeByName("ReplayScreen", assembly), "Tick"));

        Optional("spectate / first-person slot masks", () =>
        {
            PatchSlotMasks(harmony, ReflectionUtil.GetRequiredTypeByName("Puppeteer", assembly), "SyncPlayers");
            var firstPerson = ReflectionUtil.GetRequiredTypeByName("FirstPerson", assembly);
            foreach (var name in new[] { "Frame", "Sway", "Keep", "Event", "Charge" })
                PatchSlotMasks(harmony, firstPerson, name);
        });
    }

    private static void Optional(string label, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            LogWarning($"Skipping optional patch '{label}': {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static void PatchPlayerBoundsAllOverloads(Harmony harmony, Type type, string methodName)
    {
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ForgeReplayPatch), nameof(PlayerBounds_Transpiler)));
        var found = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == methodName))
        {
            harmony.Patch(method, transpiler: transpiler);
            found++;
        }

        if (found == 0)
            LogWarning($"Skipping Forge Replay PlayerBounds patch for {type.FullName}::{methodName}(): method not found.");
    }

    // Replaces stackalloc T[4] sizing and the Span<T> length with the configured player capacity.
    // stackalloc of a 4-byte element x4 compiles to: ldc.i4.s 16 ; conv.u ; localloc ; ldc.i4.4 ; newobj Span<T>(void*, int)
    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> StackallocPlayerSpan_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeReplayPatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var found = 0;

        for (var i = 0; i < codes.Count - 4; i++)
        {
            if (!codes[i].LoadsConstant(16)
                || codes[i + 1].opcode != OpCodes.Conv_U
                || codes[i + 2].opcode != OpCodes.Localloc
                || codes[i + 3].opcode != OpCodes.Ldc_I4_4
                || codes[i + 4].opcode != OpCodes.Newobj
                || codes[i + 4].operand is not ConstructorInfo { DeclaringType: { IsGenericType: true } spanType }
                || spanType.GetGenericTypeDefinition() != typeof(Span<>))
                continue;

            codes[i + 3].opcode = OpCodes.Call;   // span length = MaxPlayers
            codes[i + 3].operand = getMax;
            codes[i].opcode = OpCodes.Call;       // byte size = MaxPlayers * 4
            codes[i].operand = getMax;
            codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldc_I4_4));
            codes.Insert(i + 2, new CodeInstruction(OpCodes.Mul));
            found++;
            break;
        }

        if (found != 1)
            throw new InvalidOperationException($"Expected one stackalloc player span, found {found}.");

        return codes;
    }

    private static int GetMaxPlayers() => PluginConfig.MaxPlayers;

}
