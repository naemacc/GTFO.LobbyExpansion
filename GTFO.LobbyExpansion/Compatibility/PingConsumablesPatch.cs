using BepInEx.Logging;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using System.Linq;
using System.Reflection;

namespace GTFO.LobbyExpansion.Compatibility;

/**
 * PingConsumables runs two coroutines for the entire session:
 *   - MovePickups(), every 10s, logs "setting X spawnnode and locatorbeacon position.." at Info
 *     level for every ResourcePackPickup whose course node doesn't match its spawn node.
 *   - RemoveConsumablesFromTerminal(), every 2s, logs "item X removed from terminals as it was
 *     picked up." at Info level for every picked-up consumable still pending cleanup.
 *
 * Both are legitimate diagnostic messages, but at Info level (BepInEx's default visible
 * level) they run forever for the whole play session, flooding the log with routine
 * housekeeping. Everything else this mod logs is genuinely one-shot: "all coroutines stop"
 * once per level cleanup, and a one-time per-level-load summary of each consumable's setup
 * (or why one failed) in Initialize(). Those stay visible since they're not spam - they're
 * useful for basic problem-solving and read once, not repeated forever.
 *
 * FIRST ATTEMPT (superseded): patching ManualLogSource.Log(LogLevel, object) directly, on the
 * assumption every logging path - including the interpolated-string-handler overloads
 * PingConsumables actually uses (BepInExInfoLogInterpolatedStringHandler) - funnels through
 * that one method. That didn't work: the messages kept appearing untouched despite the patch
 * applying without error, meaning that assumption about BepInEx's internal call path was
 * wrong somewhere, without direct access to BepInEx.Core's own source to confirm exactly where.
 *
 * CURRENT APPROACH: intercept much later, at BepInEx.Logging.DiskLogListener.LogEvent(object,
 * LogEventArgs) - the method that actually writes a line to LogOutput.log. Every log message,
 * regardless of which ManualLogSource method or overload produced it, has to pass through some
 * registered ILogListener before reaching the file, so this doesn't depend on guessing the
 * exact internal call path the way patching ManualLogSource itself did. This only affects the
 * log file; a ConsoleLogListener showing these lines in the live BepInEx console window would
 * need the same patch applied there too if that's also a concern.
 *
 * Scoped to PingConsumables' own logger instance specifically (compared by reference against
 * its static `Plugin.L` field via LogEventArgs.Source) so this can't affect any other mod's
 * Info-level logging that happens to contain similar text.
 */
public class PingConsumablesPatch : IModCompatibilityPatch
{
    public const string PluginGuid = "PingConsumables";

    public static PingConsumablesPatch Instance { get; } = new();

    // Substrings identifying the two specific recurring log lines to suppress. Add more here
    // if other messages turn out to be similarly spammy - everything else stays untouched.
    private static readonly string[] SpammyMessageSubstrings =
    [
        "spawnnode and locatorbeacon position",
        "removed from terminals as it was picked up",
    ];

    private FieldInfo? _loggerField;

    private PingConsumablesPatch() { }

    public bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var pluginType = pluginInstance.GetType();

        _loggerField = AccessTools.Field(pluginType, "L") ?? throw new MissingFieldException($"Unable to find {pluginType.FullName}.L; PingConsumables' internals may have changed.");

        var logEventMethod = AccessTools.Method(typeof(DiskLogListener), nameof(DiskLogListener.LogEvent)) ?? throw new MissingMethodException("Unable to find DiskLogListener.LogEvent(object, LogEventArgs).");
        var prefix = AccessTools.Method(typeof(PingConsumablesPatch), nameof(LogEvent_Prefix));
        harmony.Patch(logEventMethod, prefix: new HarmonyMethod(prefix));

        L.Debug($"Applied patches in {nameof(PingConsumablesPatch)}.");
    }

    [HarmonyWrapSafe]
    private static bool LogEvent_Prefix(LogEventArgs eventArgs)
    {
        if (eventArgs.Level != LogLevel.Info)
            return true; // only Info-level spam is the problem here

        var pingConsumablesLogger = (ManualLogSource?)Instance._loggerField!.GetValue(null);
        if (!ReferenceEquals(eventArgs.Source, pingConsumablesLogger))
            return true; // not PingConsumables' logger - leave every other mod's logging alone

        var text = eventArgs.Data?.ToString() ?? "";
        if (SpammyMessageSubstrings.Any(substring => text.Contains(substring)))
            return false; // skip writing this line to the log file

        return true; // everything else (cleanup notice, per-level setup summary) stays visible
    }
}
