using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Player;
using SNetwork;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Player.PlayerBotActionBase.Descriptor;

namespace GTFO.LobbyExpansion.Compatibility;

/**
 * Zombified Initiative caches each bot's PlayerAgent once in ZombieComp.Initialize()
 * (the "myself" field) and never re-validates it afterward. If that PlayerAgent is
 * ever destroyed out from under the component - e.g. by another mod's player-removal
 * patch throwing partway through (observed with LocaliaCore.Patch_RemovePlayerFromSession
 * / Patch_AddToPlayersInGame throwing IndexOutOfRangeException in a 5+ player lobby) -
 * ZombieComp.Update() dereferences the now-destroyed "myself" every frame forever,
 * flooding the log with:
 *
 *   [Error :Il2CppInterop] Exception in IL2CPP-to-Managed trampoline, not passing it to il2cpp:
 *   System.NullReferenceException: Object reference not set to an instance of an object.
 *      at Zombified_Initiative.ZombieComp.Update()
 *
 * This patch prefixes ZombieComp.Update() to check whether "myself" is still a live
 * Unity object before letting the original method run. If it's been destroyed, the
 * orphaned ZombieComp is destroyed too instead of ticking forever.
 *
 * NOTE: this only guards the symptom. The actual root cause (whatever is corrupting
 * player registration/removal for 5th+ players) lives in whichever mod's SNet_SessionHub /
 * SNet_PlayerSlotManager patch is throwing, and should be fixed there separately.
 *
 * SEPARATE FIX, same file: ZombifiedInitiative v0.9.5 never networks the "sentry mode" bot
 * command (ZombieComp.allowedmove) to the host when issued by a non-host client - every other
 * command ("attack my target", pickup/share permission, clear command queue, pickup/share
 * resource) correctly round-trips through the mod's own ZINetInfo/NetworkAPI protocol via
 * ExecuteBotAction() or an inline `if (!SNet.IsMaster) NetworkAPI.InvokeEvent(...)` check.
 * Sentry mode has neither - it just mutates the local ZombieComp.allowedmove field directly,
 * which only has real effect if you happen to be the host, since ZombieComp's own per-frame
 * Update() (host-only) is what actually enforces sentry mode by repeatedly forcing
 * followaction/travelaction's DescBase.Status to a blocked state while allowedmove is false.
 * Nothing un-blocks that automatically when allowedmove flips back to true - the original
 * code's own DescBase.Status = 0 reset is what does that - so replicating just the flag flip
 * without also replicating that reset would leave a bot's movement stuck even once
 * un-sentried. This is a bug in Zombified Initiative itself (confirmed: not a documented or
 * intentional host-only limitation), unrelated to player count, that happens to matter more
 * here since expanded lobbies have more non-host players relying on bot commands.
 *
 * The fix: snapshot every bot's allowedmove immediately before CommunicationMenu.
 * PlayConfirmSound() runs, diff it against a snapshot taken immediately after. Any bot whose
 * flag changed just had sentry mode toggled locally; if we're not the host, forward that to
 * the host using a ZINetInfo FUNC code ZombifiedInitiative itself doesn't use (0-5 are taken,
 * this uses 6), then postfix ZombieController.ReceiveZINetInfo() so the host applies the same
 * toggle - and, when re-enabling movement, the same status reset - that the original inline
 * code already does for a host-issued command.
 *
 * This assumes ZombieComp.allowedmove/followaction/travelaction and
 * ZombifiedInitiative.BotTable/ZINetInfo keep their current shapes, and that GTFO.API
 * (a hard dependency of ZombifiedInitiative) is loaded somewhere in the AppDomain. The
 * InvokeEvent<T>(string, T, SNet_ChannelType) signature and RegisterEvent<T> semantics were
 * confirmed directly against GTFO.API's own NetworkAPI.cs source rather than inferred from
 * ZombifiedInitiative's call sites alone - in particular, NetworkAPI has three same-named
 * generic InvokeEvent<T> overloads (broadcast / single-target / multi-target), so the lookup
 * below matches the exact (string, T, SNet_ChannelType) parameter shape rather than the first
 * method named InvokeEvent, since GetMethods() enumeration order isn't something to rely on
 * with three same-named candidates. If any of the ZombifiedInitiative-side assumptions break in
 * a future version, this logs an error and sentry mode simply goes back to being host-only
 * again - it does not affect the orphaned-ZombieComp fix above, which is applied separately.
 */
public class ZombifiedInitiativePatch : ModCompatibilityPatch
{
    public const string PluginGuid = "com.hirnukuono.zombified_initiative";

    public static ZombifiedInitiativePatch Instance { get; } = new();

    private Type? _zombieCompType;

    private FieldInfo? _myselfField;

    // --- Sentry mode networking fix ---
    private const int SentryModeFunc = 6; // ZombifiedInitiative's own ZINetInfo.FUNC uses 0-5; 6 is unused.
    private const int AllBotsSlotSentinel = 8; // matches ZombifiedInitiative's own "AllBots" convention.

    private Type? _zombifiedInitiativeType;
    private Type? _zinetInfoType;
    private ConstructorInfo? _zinetInfoCtor;
    private FieldInfo? _botTableField;
    private FieldInfo? _allowedMoveField;
    private FieldInfo? _followActionField;
    private FieldInfo? _travelActionField;
    private Type? _networkApiType;
    private MethodInfo? _invokeEventMethod;

    private static Dictionary<string, bool>? _allowedMoveSnapshot;

    private ZombifiedInitiativePatch() { }

    public override bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid);
    }

    public override void Apply(Harmony harmony)
    {
        var pluginInstance = BepInExUtil.GetPluginInstance(PluginGuid);
        var assembly = pluginInstance.GetType().Assembly;

        _zombieCompType = ReflectionUtil.GetRequiredTypeByName("ZombieComp", assembly);

        _myselfField = AccessTools.Field(_zombieCompType, "myself");
        L.Assert(_myselfField is not null, $"{nameof(_myselfField)} is null.");

        ApplyPatches(harmony);

        try
        {
            ApplySentryModeNetworkingFix(harmony, assembly);
            L.Debug($"{nameof(ZombifiedInitiativePatch)}: applied sentry mode networking fix.");
        }
        catch (Exception ex)
        {
            // Non-fatal: the orphaned-ZombieComp fix above still works either way. Sentry mode
            // just goes back to being host-only if this fails.
            L.Error($"{nameof(ZombifiedInitiativePatch)}: sentry mode networking fix failed, sentry mode will remain host-only: {ex}");
        }
    }

    protected void ApplyPatches(Harmony harmony)
    {
        // Patched manually since we don't have a reference to the ZombieComp type at compile time.
        var original = AccessTools.Method(_zombieCompType, "Update") ?? throw new Exception($"Unable to find {nameof(_zombieCompType)}::Update().");
        var prefix = AccessTools.Method(typeof(ZombifiedInitiativePatch), nameof(ZombieComp__Update__Prefix));
        harmony.Patch(original, prefix: new HarmonyMethod(prefix));

        L.Debug($"Applied patches in {nameof(ZombifiedInitiativePatch)}.");
    }

    [HarmonyWrapSafe]
    private static bool ZombieComp__Update__Prefix(object __instance)
    {
        if (Instance._zombieCompType is null)
        {
            L.Error($"Update prefix can't run; {nameof(_zombieCompType)} is null.");

            return true; // let the original run; nothing else we can safely do
        }

        if (!Instance._zombieCompType.IsInstanceOfType(__instance))
        {
            L.Error(
                $"Update prefix was passed an instance that is NOT an instance of {nameof(_zombieCompType)} type? It was of type {__instance.GetType().FullName}");

            return true;
        }

        if (Instance._myselfField is null)
        {
            L.Error($"Update prefix can't run; {nameof(_myselfField)} is null.");

            return true;
        }

        var myself = Instance._myselfField.GetValue(__instance);

        // UnityEngine.Object overloads == to report true for destroyed native objects even
        // when the managed reference isn't C# null, so we have to go through that operator
        // (the same way ZombieComp's own Initialize() does its null check) rather than a plain
        // `myself is null` check.
        var isDestroyed = myself is null || (UnityEngine.Object)myself == null;

        if (!isDestroyed)
            return true; // myself is still a live PlayerAgent, let the original Update() run

        // The PlayerAgent this bot's ZombieComp was attached to no longer exists (torn down
        // elsewhere - e.g. another mod's buggy player-removal patch). Destroy the orphaned
        // component instead of letting it NullReferenceException every frame forever.
        L.Warning("Destroying orphaned ZombieComp whose PlayerAgent no longer exists.");
        UnityEngine.Object.Destroy((UnityEngine.Object)__instance);

        return false; // skip the original Update()
    }

    // Patched manually since ZombieComp/ZombifiedInitiative/ZINetInfo aren't referenced at
    // compile time. GTFO.API.NetworkAPI lives in a separate shared library ZombifiedInitiative
    // depends on, not its own assembly, so it's located across the whole AppDomain instead.
    private void ApplySentryModeNetworkingFix(Harmony harmony, Assembly zombifiedAssembly)
    {
        _zombifiedInitiativeType = ReflectionUtil.GetRequiredTypeByName("ZombifiedInitiative", zombifiedAssembly);

        _botTableField = AccessTools.Field(_zombifiedInitiativeType, "BotTable");
        RequireMember(_botTableField, nameof(_botTableField));

        _zinetInfoType = _zombifiedInitiativeType.GetNestedType("ZINetInfo", BindingFlags.Public | BindingFlags.NonPublic);
        if (_zinetInfoType is null)
            throw new Exception("Unable to find ZombifiedInitiative.ZINetInfo.");

        _zinetInfoCtor = _zinetInfoType.GetConstructor([typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)]);
        if (_zinetInfoCtor is null)
            throw new Exception("Unable to find ZINetInfo(int,int,int,int,int) constructor.");

        _allowedMoveField = AccessTools.Field(_zombieCompType, "allowedmove");
        RequireMember(_allowedMoveField, nameof(_allowedMoveField));

        _followActionField = AccessTools.Field(_zombieCompType, "followaction");
        RequireMember(_followActionField, nameof(_followActionField));

        _travelActionField = AccessTools.Field(_zombieCompType, "travelaction");
        RequireMember(_travelActionField, nameof(_travelActionField));

        _networkApiType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("GTFO.API.NetworkAPI"))
            .FirstOrDefault(t => t is not null);
        if (_networkApiType is null)
            throw new Exception("Unable to find GTFO.API.NetworkAPI in any loaded assembly.");

        var invokeEventGeneric = _networkApiType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "InvokeEvent" && m.IsGenericMethodDefinition)
            .FirstOrDefault(m =>
            {
                // NetworkAPI has 3 overloads of InvokeEvent<T> (broadcast, single-target,
                // multi-target SNet_Player list) - all generic, all named the same. Match the
                // broadcast one specifically: (string, T, SNet_ChannelType), 3 parameters,
                // last one SNet_ChannelType. Matching by name alone risks reflection picking
                // whichever overload GetMethods() happens to enumerate first.
                var parameters = m.GetParameters();
                return parameters.Length == 3
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[2].ParameterType == typeof(SNet_ChannelType);
            }) ?? throw new Exception("Unable to find GTFO.API.NetworkAPI.InvokeEvent<T>(string, T, SNet_ChannelType).");
        _invokeEventMethod = invokeEventGeneric.MakeGenericMethod(_zinetInfoType);

        var zombieControllerType = ReflectionUtil.GetRequiredTypeByName("ZombieController", zombifiedAssembly);
        var receiveZinetInfo = AccessTools.Method(zombieControllerType, "ReceiveZINetInfo");
        RequireMember(receiveZinetInfo, nameof(receiveZinetInfo));

        var playConfirmSound = AccessTools.Method(typeof(CommunicationMenu), "PlayConfirmSound");
        RequireMember(playConfirmSound, nameof(playConfirmSound));

        harmony.Patch(playConfirmSound,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ZombifiedInitiativePatch), nameof(PlayConfirmSound_Prefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ZombifiedInitiativePatch), nameof(PlayConfirmSound_Postfix))));

        harmony.Patch(receiveZinetInfo,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ZombifiedInitiativePatch), nameof(ReceiveZINetInfo_Postfix))));
    }

    private static void RequireMember(object? member, string memberName)
    {
        if (member is null)
            throw new Exception($"Unable to find {memberName}; ZombifiedInitiative's internals may have changed.");
    }

    // Component.GetComponent(Il2CppSystem.Type) returns a wrapper whose C# reflection type is
    // UnityEngine.Component - not ZombieComp - even though the underlying native object really
    // is one. FieldInfo.GetValue/SetValue check the object's actual C# type, so calling them
    // directly on that wrapper throws "Field 'X' ... is not a field on the target object which
    // is of type 'UnityEngine.Component'". Every other mod's decompiled source we've looked at
    // handles this by calling .Cast<T>()/.TryCast<T>() right after GetComponent - this does the
    // same thing reflectively, producing a wrapper of the actual ZombieComp type that field
    // reflection can work with.
    private static object? GetZombieComp(PlayerAIBot bot)
    {
        var component = ((UnityEngine.Component)bot).GetComponent(Il2CppType.From(Instance._zombieCompType!));
        if (component is null)
            return null;

        var tryCast = typeof(Il2CppObjectBase)
            .GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance)
            ?.MakeGenericMethod(Instance._zombieCompType!);

        return tryCast?.Invoke(component, null);
    }

    private static Dictionary<string, bool> SnapshotAllowedMove()
    {
        var result = new Dictionary<string, bool>();
        var botTable = (Dictionary<string, PlayerAIBot>)Instance._botTableField!.GetValue(null)!;

        foreach (var (name, bot) in botTable)
        {
            var zombieComp = GetZombieComp(bot);
            if (zombieComp is null)
                continue;

            result[name] = (bool)Instance._allowedMoveField!.GetValue(zombieComp)!;
        }

        return result;
    }

    [HarmonyWrapSafe]
    private static void PlayConfirmSound_Prefix()
    {
        _allowedMoveSnapshot = SnapshotAllowedMove();
    }

    // Diffs allowedmove before/after the original ran. Any bot whose flag changed just had
    // sentry mode toggled locally by the original code - if we're the host that's already
    // authoritative and nothing more is needed; if not, forward it so the host actually acts on it.
    [HarmonyWrapSafe]
    private static void PlayConfirmSound_Postfix()
    {
        if (SNet.IsMaster || _allowedMoveSnapshot is null)
            return;

        var before = _allowedMoveSnapshot;
        _allowedMoveSnapshot = null;
        var after = SnapshotAllowedMove();
        var botTable = (Dictionary<string, PlayerAIBot>)Instance._botTableField!.GetValue(null)!;

        foreach (var (botName, wasAllowed) in before)
        {
            if (!after.TryGetValue(botName, out var isAllowedNow) || isAllowedNow == wasAllowed)
                continue; // this bot's sentry state wasn't touched by whatever command just ran

            if (!botTable.TryGetValue(botName, out var bot))
                continue;

            var zinetInfo = Instance._zinetInfoCtor!.Invoke([SentryModeFunc, bot.m_playerAgent.PlayerSlotIndex, 0, 0, 0]);
            Instance._invokeEventMethod!.Invoke(null, ["ZINetInfo", zinetInfo, (SNet_ChannelType)2]);
        }
    }

    // `sender` is unused but must keep this exact name: Harmony matches postfix parameters to
    // the original method's parameters by name, and the original is
    // ReceiveZINetInfo(ulong sender, ZombifiedInitiative.ZINetInfo netInfo). Renaming it (even
    // to the usual `_`/`_1` unused-parameter convention) would make Harmony unable to bind it
    // and fail to apply this patch.
#pragma warning disable IDE0060
    [HarmonyWrapSafe]
    private static void ReceiveZINetInfo_Postfix(ulong sender, object netInfo)
#pragma warning restore IDE0060
    {
        if (!SNet.IsMaster)
            return;

        var netInfoType = netInfo.GetType();
        var func = (int)netInfoType.GetField("FUNC")!.GetValue(netInfo)!;
        if (func != SentryModeFunc)
            return;

        var slot = (int)netInfoType.GetField("SLOT")!.GetValue(netInfo)!;
        var botTable = (Dictionary<string, PlayerAIBot>)Instance._botTableField!.GetValue(null)!;

        foreach (var bot in botTable.Values)
        {
            if (slot != AllBotsSlotSentinel && bot.m_playerAgent.PlayerSlotIndex != slot)
                continue;

            ToggleSentryMode(bot);
        }
    }

    private static void ToggleSentryMode(PlayerAIBot bot)
    {
        var zombieComp = GetZombieComp(bot);
        if (zombieComp is null)
            return;

        var newValue = !(bool)Instance._allowedMoveField!.GetValue(zombieComp)!;
        Instance._allowedMoveField.SetValue(zombieComp, newValue);

        if (!newValue)
            return; // sentry mode just turned ON - nothing further to reset

        // Mirrors the original code's own DescBase.Status = 0 reset: without this, a bot's
        // follow/travel action stays stuck at the "blocked" status ZombieComp's per-frame host
        // loop forces while allowedmove is false, and never un-sticks on its own.
        var followAction = (PlayerBotActionBase?)Instance._followActionField!.GetValue(zombieComp);
        var travelAction = (PlayerBotActionBase?)Instance._travelActionField!.GetValue(zombieComp);

        if (followAction is not null)
            followAction.DescBase.Status = (StatusType)0;

        if (travelAction is not null)
            travelAction.DescBase.Status = (StatusType)0;
    }
}
