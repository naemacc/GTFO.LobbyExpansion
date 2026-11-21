using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace GTFO.LobbyExpansion.Compatibility;

public class ForgeCorePatch : IModCompatibilityPatch
{
    public const string PluginGuid = "NAinfini.ForgeCore";

    public static ForgeCorePatch Instance { get; } = new();

    private ForgeCorePatch() { }

    public bool ShouldApply() => BepInExUtil.IsPluginLoaded(PluginGuid);

    public void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        ExpandStaticPlayerArrays(assembly);
        PatchInstanceConstructors(harmony, assembly);

        PatchNamedTypeMethod(harmony, assembly, "GameCompass", "Lookup", "HardcodedFourLoop_Transpiler");
        PatchNamedTypeMethod(harmony, assembly, "GameCompass", "AddMate", "HardcodedFourLoop_Transpiler");
        PatchNamedTypeMethod(harmony, assembly, "Counted", "Counted", "HardcodedFourLoop_Transpiler");

        L.Debug($"Applied patches in {nameof(ForgeCorePatch)}.");
    }

    // Forge Core private reflection is safe because it only replaces known player-array fields with larger arrays.
#pragma warning disable S3011
    private static void ExpandStaticPlayerArrays(Assembly assembly)
    {
        foreach (var type in GetTypes(assembly))
        {
            if (type.ContainsGenericParameters)
                continue;

            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!field.FieldType.IsArray || field.FieldType.GetArrayRank() != 1 || field.IsInitOnly || !IsPlayerField(field.Name))
                    continue;

                if (field.GetValue(null) is not Array array || array.Length >= PluginConfig.MaxPlayers)
                    continue;

                var expanded = Array.CreateInstance(field.FieldType.GetElementType()!, PluginConfig.MaxPlayers);
                Array.Copy(array, expanded, array.Length);
                field.SetValue(null, expanded);
            }
        }
    }
#pragma warning restore S3011

    // Forge Core constructor reflection is safe because it inspects only array fields whose names match player-slot state.
#pragma warning disable S3011
    private static void PatchInstanceConstructors(Harmony harmony, Assembly assembly)
    {
        foreach (var type in GetTypes(assembly))
        {
            if (type.ContainsGenericParameters || type.IsValueType)
                continue;

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => field.FieldType.IsArray
                    && field.FieldType.GetArrayRank() == 1
                    && IsPlayerField(field.Name));

            if (!fields.Any())
                continue;

            foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(constructor => !constructor.ContainsGenericParameters))
            {
                try
                {
                    harmony.Patch(constructor, postfix: new HarmonyMethod(AccessTools.Method(typeof(ForgeCorePatch), nameof(ExpandInstanceArrays_Postfix))));
                }
                catch (HarmonyException ex)
                {
                    L.Warning($"Skipping Forge Core constructor patch for {constructor.DeclaringType?.FullName}: {ex.Message}");
                }
            }
        }
    }
#pragma warning restore S3011

    // Forge Core postfix reflection is safe because it copies existing values and enlarges only player-slot array fields.
#pragma warning disable S3011
    [HarmonyWrapSafe]
    private static void ExpandInstanceArrays_Postfix(object __instance)
    {
        if (__instance is null)
            return;

        var type = __instance.GetType();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!field.FieldType.IsArray || field.FieldType.GetArrayRank() != 1 || !IsPlayerField(field.Name))
                continue;

            if (field.GetValue(__instance) is not Array array || array.Length >= PluginConfig.MaxPlayers)
                continue;

            var expanded = Array.CreateInstance(field.FieldType.GetElementType()!, PluginConfig.MaxPlayers);
            Array.Copy(array, expanded, array.Length);
            field.SetValue(__instance, expanded);
        }
    }
#pragma warning restore S3011

    private static bool IsPlayerField(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("slot") || lower.Contains("player") || lower.Contains("mate")
            || lower.Contains("holder") || lower.Contains("health") || lower.Contains("infection")
            || lower.Contains("stamina") || lower.Contains("down") || lower.Contains("aim")
            || lower.Contains("wield") || lower.Contains("kit") || lower.Contains("gear")
            || lower.Contains("boost") || lower.Contains("shot") || lower.Contains("hurt")
            || lower.Contains("rescue") || lower.Contains("pack") || lower.Contains("sentry")
            || lower.Contains("mine") || lower.Contains("ammo") || lower.Contains("present")
            || lower.Contains("status") || lower.Contains("loadout")
            || lower.Contains("part") || lower.Contains("motion") || lower.Contains("place")
            || lower.Contains("hand") || lower.Contains("tag") || lower.Contains("ring")
            || lower.Contains("matecolour") || lower.Contains("mateslot");
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type is not null)!; }
    }

    private static void PatchNamedTypeMethod(Harmony harmony, Assembly assembly, string typeName, string methodName, string transpilerName)
    {
        var type = assembly.GetTypes().FirstOrDefault(candidate => candidate.Name == typeName);
        if (type is null)
            return;

        var method = AccessTools.Method(type, methodName);
        if (method is null)
            return;

        harmony.Patch(method, transpiler: new HarmonyMethod(AccessTools.Method(typeof(ForgeCorePatch), transpilerName)));
    }

    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> HardcodedFourLoop_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMax = AccessTools.Method(typeof(ForgeCorePatch), nameof(GetMaxPlayers));
        var codes = new List<CodeInstruction>(instructions);
        var matches = new List<int>();

        for (var i = 0; i < codes.Count - 2; i++)
        {
            if (IsLdloc(codes[i].opcode)
                && codes[i + 1].opcode == OpCodes.Ldc_I4_4
                && IsCompareOrBranch(codes[i + 2].opcode))
                matches.Add(i + 1);

            if (codes[i].opcode == OpCodes.Ldc_I4_4
                && IsCompareOrBranch(codes[i + 1].opcode))
                matches.Add(i);
        }

        matches = [.. matches.Distinct()];
        if (matches.Count == 0)
            return codes;

        foreach (var index in matches)
        {
            var target = codes[index];
            target.opcode = OpCodes.Call;
            target.operand = getMax;
        }

        return codes;
    }

    private static bool IsLdloc(OpCode opcode) => opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1
        || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3 || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc;

    private static bool IsCompareOrBranch(OpCode opcode) => opcode == OpCodes.Clt || opcode == OpCodes.Clt_Un
        || opcode == OpCodes.Blt || opcode == OpCodes.Blt_S || opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S
        || opcode == OpCodes.Bge || opcode == OpCodes.Bge_S || opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S;

    private static int GetMaxPlayers() => PluginConfig.MaxPlayers;
}
