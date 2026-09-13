using System.Reflection;
using BepInEx.Logging;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;

namespace GTFO.LobbyExpansion.Compatibility;

public class PingConsumablesPatch : ModCompatibilityPatch
{
    public const string PluginGuid = "PingConsumables";

    public static PingConsumablesPatch Instance { get; } = new();

    // Substrings identifying the two specific recurring log lines to suppress. Add more here
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

        var logEventMethod = AccessTools.Method(typeof(DiskLogListener), nameof(DiskLogListener.LogEvent));
        if (logEventMethod is null)
            throw new Exception("Unable to find DiskLogListener.LogEvent(object, LogEventArgs).");

        var prefix = AccessTools.Method(typeof(PingConsumablesPatch), nameof(LogEvent_Prefix));
        harmony.Patch(logEventMethod, prefix: new HarmonyMethod(prefix));

        L.Debug($"Applied patches in {nameof(PingConsumablesPatch)}.");
    }

    #endregion

    [HarmonyWrapSafe]
    private static bool LogEvent_Prefix(LogEventArgs eventArgs)
    {
        if (eventArgs.Level != LogLevel.Info)
            return true; // only Info-level spam is the problem here

        var pingConsumablesLogger = (ManualLogSource?)Instance._loggerField!.GetValue(null);
        if (!ReferenceEquals(eventArgs.Source, pingConsumablesLogger))
            return true; // not PingConsumables' logger - leave every other mod's logging alone

        var text = eventArgs.Data?.ToString() ?? "";
        foreach (var substring in SpammyMessageSubstrings)
        {
            if (text.Contains(substring))
                return false; // skip writing this line to the log file
        }

        return true; // everything else (cleanup notice, per-level setup summary) stays visible
    }
}
