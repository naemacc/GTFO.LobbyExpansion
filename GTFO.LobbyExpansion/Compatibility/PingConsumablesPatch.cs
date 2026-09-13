using System.Reflection;
using BepInEx.Logging;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;

namespace GTFO.LobbyExpansion.Compatibility;

public class PingConsumablesPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "PingConsumables";

    public static PingConsumablesPatch Instance { get; } = new();

    // Substrings identifying the two specific recurring log lines to downgrade. Add more here
    // if other messages turn out to be similarly spammy - everything else stays untouched.
    private static readonly string[] SpammyMessageSubstrings =
    {
        "spawnnode and locatorbeacon position",
        "removed from terminals as it was picked up",
    };

    private FieldInfo? _loggerField;

    private PingConsumablesPatch() { }

    #region Overrides of ModCompatibilityPatch

    public override bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public override void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var pluginType = pluginInstance.GetType();

        _loggerField = AccessTools.Field(pluginType, "L");
        if (_loggerField is null)
            throw new Exception($"Unable to find {pluginType.FullName}.L; PingConsumables' internals may have changed.");

        var logMethod = AccessTools.Method(typeof(ManualLogSource), nameof(ManualLogSource.Log), new[] { typeof(LogLevel), typeof(object) });
        if (logMethod is null)
            throw new Exception("Unable to find ManualLogSource.Log(LogLevel, object).");

        var prefix = AccessTools.Method(typeof(PingConsumablesPatch), nameof(Log_Prefix));
        harmony.Patch(logMethod, prefix: new HarmonyMethod(prefix));

        L.Debug($"Applied patches in {nameof(PingConsumablesPatch)}.");
    }

    #endregion

    [HarmonyWrapSafe]
    private static bool Log_Prefix(ManualLogSource __instance, LogLevel level, object data)
    {
        if (level != LogLevel.Info)
            return true; 

        var pingConsumablesLogger = (ManualLogSource?)Instance._loggerField!.GetValue(null);
        if (!ReferenceEquals(__instance, pingConsumablesLogger))
            return true; 

        var text = data?.ToString() ?? "";
        foreach (var substring in SpammyMessageSubstrings)
        {
            if (!text.Contains(substring))
                continue;

            __instance.Log(LogLevel.Debug, data); 
            return false; 
        }

        return true; 
    }
}
