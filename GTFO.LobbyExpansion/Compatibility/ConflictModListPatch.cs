using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using System.Reflection;

namespace GTFO.LobbyExpansion.Compatibility;

public class ConflictModListPatch : IModCompatibilityPatch
{
    public const string PluginGuid = "hawchow.conflictmodlist";

    public static ConflictModListPatch Instance { get; } = new();

    private FieldInfo? _hudField;
    private FieldInfo? _seatsField;

    private ConflictModListPatch() { }

    public bool ShouldApply() => BepInExUtil.IsPluginLoaded(PluginGuid);

    public void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        var driverType = ReflectionUtil.GetRequiredTypeByName("TeamVersionsDriver", assembly);
        _hudField = AccessTools.Field(driverType, "hud")
            ?? throw new MissingFieldException($"Unable to find {driverType.FullName}.hud.");
        var hudType = ReflectionUtil.GetRequiredTypeByName("LobbyNetworkHud", assembly);
        _seatsField = AccessTools.Field(hudType, "seats")
            ?? throw new MissingFieldException($"Unable to find {hudType.FullName}.seats.");

        var update = AccessTools.Method(driverType, "Update", [])
            ?? throw new MissingMethodException($"Unable to find {driverType.FullName}.Update().");
        harmony.Patch(update, postfix: new HarmonyMethod(AccessTools.Method(typeof(ConflictModListPatch), nameof(Update_Postfix))));

        L.Debug($"Applied patches in {nameof(ConflictModListPatch)}.");
    }

    [HarmonyWrapSafe]
    private static void Update_Postfix(object __instance)
    {
        var self = Instance;
        var hud = self._hudField!.GetValue(__instance);
        if (hud is null)
            return;

        if (self._seatsField!.GetValue(hud) is not Array seats || seats.Length >= PluginConfig.MaxPlayers)
            return;

        var expanded = Array.CreateInstance(seats.GetType().GetElementType()!, PluginConfig.MaxPlayers);
        Array.Copy(seats, expanded, seats.Length);

        var seatType = expanded.GetType().GetElementType()!;
        for (var i = seats.Length; i < PluginConfig.MaxPlayers; i++)
            expanded.SetValue(Activator.CreateInstance(seatType, nonPublic: true), i);

        self._seatsField.SetValue(hud, expanded);
    }
}
