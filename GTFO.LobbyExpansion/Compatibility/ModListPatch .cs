using BepInEx.Unity.IL2CPP;
using CellMenu;
using GTFO.LobbyExpansion.Util;
using HarmonyLib;
using SNetwork;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using TMPro;
using UnityEngine;

namespace GTFO.LobbyExpansion.Compatibility;

#pragma warning disable S3011 // Reflection on the third-party ModList/LocaliaCore internals is the whole point of this patch.

/**
 * ModList (Localia.ModList) draws a "MOD: ..." label on every lobby bar and a popup with the mod
 * list of a player. This patch does two jobs:
 *
 * 1) LOBBY EXPANSION (unchanged from before)
 *    UI_ModButtons / GenerateLobbyButtons / ClearButtonText are hardcoded to 4 players, so the
 *    array is resized and the loop bound is swapped for MaxPlayers via transpiler.
 *
 * 2) HOST COMPARE + LIVE LATENCY (replaces the old ConflictModListPatch / ConflictModList)
 *    - LocaliaCore only syncs plugin GUIDs (no versions). We piggy-back the versions on the same
 *      list as extra entries: "~v~<guid>|<version>". They are appended to the outgoing buffers
 *      in API.onLoadedModList() and stripped again in API.onGotModList(), so:
 *        * LocaliaCore's own API (has_Install_Plugin, Get_ModList, ...) never sees them,
 *        * peers WITHOUT this patch stay fully compatible (they just see a few extra grey rows
 *          in their popup and cannot do version compares).
 *    - Every player is compared against the HOST's list:
 *        * different version -> flagged
 *        * host has it, player doesn't -> MISSING
 *        * player has it, host doesn't -> EXTRA
 *      A flagged player's label becomes red "MOD: UNSYNCED (n)". In their popup the flagged
 *      rows are red and sorted on top, followed by the normal rows (now with versions).
 *      If either side didn't send versions (unpatched peer), only GUID presence is compared.
 *    - The label (incl. latency) is refreshed every RefreshIntervalSeconds while the loadout page
 *      is open. Vanilla ModList only refreshed it when the lobby bars happened to update, and
 *      additionally read the ping with the lobby BAR index instead of the player SLOT index.
 */
public class ModListPatch : IModCompatibilityPatch
{
    public const string PluginGuid = "Localia.ModList";
    private const string CorePluginGuid = "Localia.LocaliaCore";

    // Prefix for the version entries piggy-backed on LocaliaCore's mod list. No ';' and no '@' (see above).
    private const string MarkerPrefix = "~v~";

    private const float RefreshIntervalSeconds = 0.5f;

    // Popup sizing. Vanilla was 600 / 700; widened a bit since rows now also carry versions.
    private const float WindowWidth = 800f;
    private const float RowTextWidth = 900f;

    private const string ColorSynced = "#6ecc72";
    private const string ColorBad = "#ff4d4d";
    private const string ColorMine = "#0f4f94";
    private const string ColorOther = "#b3b3b3";
    private const string ColorVersion = "#808080";

    public static ModListPatch Instance { get; } = new();

    // slot index -> (plugin guid -> version). Cleared together with LocaliaCore's own slot state.
    private static readonly Dictionary<int, Dictionary<string, string>> SlotVersions = [];

    private static float _nextRefresh;
    private static string? _lastError;

    // ModList
    private Type? _modListManagerType;
    private FieldInfo? _pageLoadoutField;
    private FieldInfo? _modListWindowField;
    private FieldInfo? _myModDicField;
    private FieldInfo? _playerLobbyBarsField;
    private FieldInfo? _uiModButtonsField;

    // LocaliaCore
    private Func<int, bool>? _hasInstallCore;
    private Func<int, bool>? _hasGotModList;
    private Func<int, int>? _getModListCount;
    private Func<int, List<string>?>? _getModList;
    private Func<int, int>? _getSlotPing;

    private FieldInfo? _coreMyModListField;
    private FieldInfo? _coreMyModListBufferField;
    private FieldInfo? _coreMyModListBufferRawField;
    private MethodInfo? _coreMakeHeader;
    private MethodInfo? _coreChangeToDicBuffer;
    private MethodInfo? _coreGZipCompress;
    private object? _coreModListDataType;

    private enum DiffKind
    {
        VersionMismatch,
        Missing,
        Extra,
    }

    private sealed record ModDiff(string Guid, DiffKind Kind, string? HostVersion, string? PlayerVersion);

    private ModListPatch() { }

    public bool ShouldApply()
    {
        return BepInExUtil.IsPluginLoaded(PluginGuid) && BepInExUtil.IsPluginLoaded(CorePluginGuid);
    }

    public void Apply(Harmony harmony)
    {
        try
        {
            var modListAssembly = BepInExUtil.GetPluginInstance(PluginGuid).GetType().Assembly;
            var coreAssembly = BepInExUtil.GetPluginInstance(CorePluginGuid).GetType().Assembly;

            _modListManagerType = ReflectionUtil.GetRequiredTypeByName("ModList_Manager", modListAssembly);

            ResolveModListMembers();
            var (apiType, networkManagerType) = ResolveCoreMembers(coreAssembly);

            // --- lobby expansion ---
            ExpandUiModButtons();
            PatchTranspiler(harmony, _modListManagerType, "GenerateLobbyButtons");
            PatchTranspiler(harmony, _modListManagerType, "ClearButtonText");

            // --- compare + latency UI ---
            PatchMethod(harmony, _modListManagerType, "checkSlotState", nameof(CheckSlotState_Prefix));
            PatchMethod(harmony, _modListManagerType, "ShowListWindow", nameof(ShowListWindow_Prefix));

            var pageUpdate = AccessTools.Method(typeof(CM_PageLoadout), nameof(CM_PageLoadout.Update))
                ?? throw new MissingMethodException("Unable to find CM_PageLoadout.Update().");
            harmony.Patch(pageUpdate, postfix: new HarmonyMethod(AccessTools.Method(typeof(ModListPatch), nameof(PageLoadout_Update_Postfix))));

            // --- version transport on top of LocaliaCore ---
            PatchMethod(harmony, apiType, "onLoadedModList", nameof(OnLoadedModList_Prefix), prefix: true);
            PatchMethod(harmony, apiType, "onGotModList", nameof(OnGotModList_Prefix), prefix: true, parameterTypes: [typeof(int), typeof(List<string>)]);
            PatchMethod(harmony, networkManagerType, "clearSlot", nameof(ClearSlot_Postfix), prefix: false, parameterTypes: [typeof(int)]);

            // Startup may already have built the buffers if we got here late.
            if (_coreMyModListBufferField!.GetValue(null) is IDictionary { Count: > 0 })
                RebuildOutgoingBuffers();

            L.Debug($"Applied patches in {nameof(ModListPatch)}.");
        }
        catch (Exception ex)
        {
            L.Error($"{nameof(ModListPatch)}.Apply() failed: {ex}");
            throw;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Reflection setup
    // ------------------------------------------------------------------------------------------

    private void ResolveModListMembers()
    {
        _pageLoadoutField = RequireField(_modListManagerType, "PageLoadout");
        _modListWindowField = RequireField(_modListManagerType, "ModListWindow");
        _myModDicField = RequireField(_modListManagerType, "myModDic");
        _playerLobbyBarsField = RequireField(_modListManagerType, "PlayerLobbyBars");
        _uiModButtonsField = RequireField(_modListManagerType, "UI_ModButtons");
    }

    private (Type Api, Type NetworkManager) ResolveCoreMembers(System.Reflection.Assembly coreAssembly)
    {
        var apiType = ReflectionUtil.GetRequiredTypeByName("API", coreAssembly);
        var networkManagerType = ReflectionUtil.GetRequiredTypeByName("Network_Manager", coreAssembly);
        var cmpType = ReflectionUtil.GetRequiredTypeByName("Cmp", coreAssembly);

        _hasInstallCore = BindStatic<Func<int, bool>>(apiType, "has_Install_Core");
        _hasGotModList = BindStatic<Func<int, bool>>(apiType, "has_got_modlist");
        _getModListCount = BindStatic<Func<int, int>>(apiType, "Get_ModList_Count");
        _getModList = BindStatic<Func<int, List<string>?>>(apiType, "Get_ModList");
        _getSlotPing = BindStatic<Func<int, int>>(apiType, "Get_Slot_Ping");

        _coreMyModListField = RequireField(networkManagerType, "myModList");
        _coreMyModListBufferField = RequireField(networkManagerType, "myModListBuffer");
        _coreMyModListBufferRawField = RequireField(networkManagerType, "myModListBuffer_raw");

        var dataTypeEnum = networkManagerType.GetNestedType("dataType", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new TypeLoadException("Unable to find Network_Manager.dataType.");
        _coreModListDataType = Enum.Parse(dataTypeEnum, "MODLIST_DATA");

        _coreMakeHeader = AccessTools.Method(networkManagerType, "MakeHeader", [dataTypeEnum, typeof(bool), typeof(bool)])
            ?? throw new MissingMethodException("Unable to find Network_Manager.MakeHeader(dataType, bool, bool).");
        _coreChangeToDicBuffer = AccessTools.Method(networkManagerType, "ChangtoDicBuffer", [typeof(string), typeof(string), typeof(int)])
            ?? throw new MissingMethodException("Unable to find Network_Manager.ChangtoDicBuffer(string, string, int).");
        _coreGZipCompress = AccessTools.Method(cmpType, "GZipCompressString", [typeof(string)])
            ?? throw new MissingMethodException("Unable to find Cmp.GZipCompressString(string).");

        return (apiType, networkManagerType);
    }

    private static FieldInfo RequireField(Type? type, string name)
    {
        return AccessTools.Field(type, name) ?? throw new MissingFieldException($"Unable to find {type?.FullName}.{name}.");
    }

    private static T BindStatic<T>(Type type, string methodName) where T : Delegate
    {
        var parameterTypes = typeof(T).GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray();
        var method = AccessTools.Method(type, methodName, parameterTypes)
            ?? throw new MissingMethodException($"Unable to find {type.FullName}::{methodName}().");
        return (T)Delegate.CreateDelegate(typeof(T), method);
    }

    private void ExpandUiModButtons()
    {
        var field = _uiModButtonsField!;
        var oldArray = (Array)field.GetValue(null)!;

        if (oldArray.Length >= PluginConfig.MaxPlayers)
            return; // already big enough (e.g. Apply() ran twice)

        var elementType = field.FieldType.GetElementType()!;
        var newArray = Array.CreateInstance(elementType, PluginConfig.MaxPlayers);
        Array.Copy(oldArray, newArray, oldArray.Length);
        field.SetValue(null, newArray);
    }

    // Patched manually since we don't have compile-time references to the third-party types.
    private static void PatchTranspiler(Harmony harmony, Type? targetType, string methodName)
    {
        var original = AccessTools.Method(targetType, methodName) ?? throw new MissingMethodException($"Unable to find {targetType?.FullName}::{methodName}().");
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(ModListPatch), nameof(HardcodedFour_Transpiler)));
        harmony.Patch(original, transpiler: transpiler);
    }

    private static void PatchMethod(Harmony harmony, Type? targetType, string methodName, string patchName, bool prefix = true, Type[]? parameterTypes = null)
    {
        var original = AccessTools.Method(targetType, methodName, parameterTypes)
            ?? throw new MissingMethodException($"Unable to find {targetType?.FullName}::{methodName}().");
        var patch = new HarmonyMethod(AccessTools.Method(typeof(ModListPatch), patchName)
            ?? throw new MissingMethodException($"Unable to find patch method {patchName}."));

        if (prefix)
            harmony.Patch(original, prefix: patch);
        else
            harmony.Patch(original, postfix: patch);
    }

    // ------------------------------------------------------------------------------------------
    // Version transport (LocaliaCore side)
    // ------------------------------------------------------------------------------------------

    private static string SanitizeVersion(string version)
    {
        // LocaliaCore splits its payload on ';' and '@'; '|' separates guid and version in our entries.
        return version.Replace(';', ' ').Replace('@', '_').Replace('|', '_');
    }

    private static Dictionary<string, string> BuildLocalVersions()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, info) in IL2CPPChainloader.Instance.Plugins)
            result[key.Replace(';', ' ')] = SanitizeVersion(info.Metadata.Version.ToString());

        return result;
    }

    /// Rebuilds LocaliaCore's outgoing (compressed + raw) mod list buffers with our version entries
    /// appended, using LocaliaCore's own helpers so the wire format stays identical.
    private void RebuildOutgoingBuffers()
    {
        var guids = (List<string>)_coreMyModListField!.GetValue(null)!;
        var parts = new List<string>(guids);
        parts.AddRange(BuildLocalVersions().Select(kv => $"{MarkerPrefix}{kv.Key}|{kv.Value}"));

        var text = string.Join(";", parts);
        var header = (string)_coreMakeHeader!.Invoke(null, [_coreModListDataType, false, false])!;

        var raw = _coreChangeToDicBuffer!.Invoke(null, [header, text, 50]);
        var compressed = (string)_coreGZipCompress!.Invoke(null, [text])!;
        var buffer = _coreChangeToDicBuffer.Invoke(null, [header, compressed, 50]);

        _coreMyModListBufferRawField!.SetValue(null, raw);
        _coreMyModListBufferField!.SetValue(null, buffer);

        L.Debug($"{nameof(ModListPatch)}: appended {parts.Count - guids.Count} version entries to the outgoing mod list.");
    }

    [HarmonyWrapSafe]
    private static void OnLoadedModList_Prefix()
    {
        Instance.RebuildOutgoingBuffers();
    }

    // Parameter names must match the original: Harmony binds by name (original is onGotModList(int slot, List<string> ModList)).
    [HarmonyWrapSafe]
    private static void OnGotModList_Prefix(int slot, List<string> ModList)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = ModList.Count - 1; i >= 0; i--)
        {
            var entry = ModList[i];
            if (entry is null || !entry.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                continue;

            ModList.RemoveAt(i); // keep LocaliaCore's API clean of our entries

            var body = entry[MarkerPrefix.Length..];
            var separator = body.LastIndexOf('|');
            if (separator <= 0)
                continue;

            versions[body[..separator]] = body[(separator + 1)..];
        }

        // The local list is built from LocaliaCore's own GUID list (no marker entries), so use our own data.
        if (SNet.LocalPlayer is { } local && local.PlayerSlotIndex() == slot)
            versions = BuildLocalVersions();

        if (versions.Count > 0)
            SlotVersions[slot] = versions;
        else
            SlotVersions.Remove(slot);
    }

    private static void ClearSlot_Postfix(int slot)
    {
        SlotVersions.Remove(slot);
    }

    // ------------------------------------------------------------------------------------------
    // Compare
    // ------------------------------------------------------------------------------------------

    private static string? Lookup(Dictionary<string, string>? versions, string guid)
    {
        return versions is not null && versions.TryGetValue(guid, out var version) ? version : null;
    }

    /// Returns null when a comparison isn't possible (no host yet, host/player list missing, or the
    /// player IS the host); otherwise the (possibly empty) list of differences vs. the host.
    private List<ModDiff>? Compare(int slot)
    {
        var host = SNet.Master;
        if (host is null)
            return null;

        var hostSlot = host.PlayerSlotIndex();
        if (hostSlot < 0 || hostSlot == slot)
            return null;

        var hostList = _hasGotModList!(hostSlot) ? _getModList!(hostSlot) : null;
        var playerList = _hasGotModList(slot) ? _getModList!(slot) : null;
        if (hostList is null || playerList is null || hostList.Count == 0)
            return null;

        SlotVersions.TryGetValue(hostSlot, out var hostVersions);
        SlotVersions.TryGetValue(slot, out var playerVersions);

        var hostSet = new HashSet<string>(hostList.Where(g => !string.IsNullOrWhiteSpace(g)), StringComparer.Ordinal);
        var playerSet = new HashSet<string>(playerList.Where(g => !string.IsNullOrWhiteSpace(g)), StringComparer.Ordinal);
        var diffs = new List<ModDiff>();

        foreach (var guid in hostSet)
        {
            var hostVersion = Lookup(hostVersions, guid);

            if (!playerSet.Contains(guid))
            {
                diffs.Add(new ModDiff(guid, DiffKind.Missing, hostVersion, null));
                continue;
            }

            // Only flag a version mismatch when BOTH sides actually reported a version.
            var playerVersion = Lookup(playerVersions, guid);
            if (hostVersion is not null && playerVersion is not null
                && !string.Equals(hostVersion, playerVersion, StringComparison.Ordinal))
            {
                diffs.Add(new ModDiff(guid, DiffKind.VersionMismatch, hostVersion, playerVersion));
            }
        }

        diffs.AddRange(playerSet
            .Where(guid => !hostSet.Contains(guid))
            .Select(guid => new ModDiff(guid, DiffKind.Extra, null, Lookup(playerVersions, guid))));

        return [.. diffs.OrderBy(d => d.Kind).ThenBy(d => d.Guid, StringComparer.OrdinalIgnoreCase)];
    }

    private static string FormatDiff(ModDiff diff)
    {
        return diff.Kind switch
        {
            DiffKind.VersionMismatch => $"<{ColorBad}>{diff.Guid}  {diff.PlayerVersion}  (host {diff.HostVersion})",
            DiffKind.Missing => $"<{ColorBad}>{diff.Guid}  MISSING  (host {diff.HostVersion ?? "?"})",
            _ => $"<{ColorBad}>{diff.Guid}  EXTRA  {diff.PlayerVersion}",
        };
    }

    // ------------------------------------------------------------------------------------------
    // Label (status + latency)
    // ------------------------------------------------------------------------------------------

    private string BuildStatusText(CM_PlayerLobbyBar bar)
    {
        const string unknown = "<#FFF>MOD: UNKNOWN";

        if (!bar.HasPlayer())
            return unknown;

        var slot = bar.m_player.PlayerSlotIndex();
        if (slot < 0 || !_hasInstallCore!(slot))
            return unknown;

        if (!_hasGotModList!(slot) || _getModListCount!(slot) <= 0)
            return "<#FFF>MOD: GETTING...";

        var ping = _getSlotPing!(slot);
        var pingText = ping != -1 ? $"  {ping}ms" : "";

        var diffs = Compare(slot);
        return diffs is { Count: > 0 }
            ? $"<{ColorBad}>MOD: UNSYNCED ({diffs.Count}){pingText}"
            : $"<{ColorSynced}>MOD: SYNCED{pingText}";
    }

    private void RefreshStatusTexts()
    {
        var page = (CM_PageLoadout?)_pageLoadoutField!.GetValue(null);
        if (page == null)
            return;

        var bars = (CM_PlayerLobbyBar[]?)_playerLobbyBarsField!.GetValue(null);
        var buttons = (CM_Item[]?)_uiModButtonsField!.GetValue(null);
        if (bars is null || buttons is null)
            return;

        var count = Math.Min(PluginConfig.MaxPlayers, Math.Min(bars.Length, buttons.Length));
        for (var i = 0; i < count; i++)
        {
            var bar = bars[i];
            var button = buttons[i];
            if (bar == null || button == null)
                continue;

            button.SetText(BuildStatusText(bar));
        }
    }

    private static void SafeRefresh()
    {
        try
        {
            Instance.RefreshStatusTexts();
            _lastError = null;
        }
        catch (Exception ex)
        {
            var message = ex.ToString();
            if (message != _lastError) // don't spam every tick
                L.Error($"{nameof(ModListPatch)}: refreshing mod status failed: {message}");
            _lastError = message;
        }
    }

    // Replaces ModList_Manager.checkSlotState() (hardcoded to 4 + no compare + wrong ping index).
    private static bool CheckSlotState_Prefix()
    {
        SafeRefresh();
        return false; // skip the original
    }

    // Keeps the label (and latency) live instead of only when the lobby bars happen to update.
    [HarmonyWrapSafe]
    private static void PageLoadout_Update_Postfix()
    {
        var now = UnityEngine.Time.unscaledTime;
        if (now < _nextRefresh)
            return;

        _nextRefresh = now + RefreshIntervalSeconds;
        SafeRefresh();
    }

    // ------------------------------------------------------------------------------------------
    // Popup
    // ------------------------------------------------------------------------------------------

    // Replaces ModList_Manager.ShowListWindow(int). Parameter name must match the original.
    private static bool ShowListWindow_Prefix(int ori_slot)
    {
        try
        {
            Instance.ShowListWindow(ori_slot);
        }
        catch (Exception ex)
        {
            L.Error($"{nameof(ModListPatch)}: showing the mod list popup failed: {ex}");
        }

        return false; // skip the original
    }

    private void ShowListWindow(int oriSlot)
    {
        if (!TryGetPopupContext(oriSlot, out var page, out var window, out var bar, out var button))
            return;

        var slot = bar.m_player.PlayerSlotIndex();
        if (!_hasGotModList!(slot))
            return;

        var modList = _getModList!(slot);
        if (modList is null)
            return;

        var rows = BuildPopupRows(slot, modList);
        var contentItems = BuildPopupContentItems(page, rows);
        var height = Math.Clamp(contentItems.Count * 60f, 300f, 1000f);

        window.transform.localScale = bar.m_player.IsLocal ? Vector3.one : new Vector3(1.2f, 1.2f, 1.2f);
        window.SetContentItems(contentItems, 10f);
        window.SetSize(new Vector2(WindowWidth, height));
        window.transform.position = button.transform.position;
        window.SetVisible(visible: true);
    }

    private bool TryGetPopupContext(
        int oriSlot,
        out CM_PageLoadout page,
        out CM_ScrollWindow window,
        out CM_PlayerLobbyBar bar,
        out CM_Item button)
    {
        page = null!;
        window = null!;
        bar = null!;
        button = null!;

        var reflectedPage = (CM_PageLoadout?)_pageLoadoutField!.GetValue(null);
        var reflectedWindow = (CM_ScrollWindow?)_modListWindowField!.GetValue(null);
        var bars = (CM_PlayerLobbyBar[]?)_playerLobbyBarsField!.GetValue(null);
        var buttons = (CM_Item[]?)_uiModButtonsField!.GetValue(null);

        if (reflectedPage == null || reflectedWindow == null || bars is null || buttons is null
            || oriSlot < 0 || oriSlot >= bars.Length || oriSlot >= buttons.Length)
        {
            return false;
        }

        bar = bars[oriSlot];
        button = buttons[oriSlot];

        if (bar == null || !bar.HasPlayer())
            return false;

        page = reflectedPage;
        window = reflectedWindow;
        return true;
    }

    private List<string> BuildPopupRows(int slot, List<string> modList)
    {
        var myMods = (Dictionary<string, string>?)_myModDicField!.GetValue(null);
        var diffs = Compare(slot) ?? [];
        SlotVersions.TryGetValue(slot, out var versions);

        // Differences first (red), then everything else as vanilla ModList did, now with versions.
        var rows = new List<string>(diffs.Select(FormatDiff));
        var flagged = new HashSet<string>(diffs.Select(d => d.Guid), StringComparer.Ordinal);

        foreach (var guid in modList)
        {
            if (string.IsNullOrWhiteSpace(guid) || flagged.Contains(guid))
                continue;

            var mine = myMods is not null && myMods.ContainsKey(guid);
            var row = $"<{(mine ? ColorMine : ColorOther)}>{guid}";

            var version = Lookup(versions, guid);
            if (version is not null)
                row += $"  <{ColorVersion}>{version}";

            rows.Add(row);
        }

        return rows;
    }

    private static Il2CppSystem.Collections.Generic.List<iScrollWindowContent> BuildPopupContentItems(
        CM_PageLoadout page,
        IReadOnlyList<string> rows)
    {
        var contentItems = new Il2CppSystem.Collections.Generic.List<iScrollWindowContent>();

        foreach (var row in rows)
        {
            var content = GOUtil.SpawnChildAndGetComp<iScrollWindowContent>(page.m_inviteWindowContentPrefab);
            content.SetText(row);
            contentItems.Add(content);

            ConfigurePopupContent(page, content);
        }

        return contentItems;
    }

    private static void ConfigurePopupContent(CM_PageLoadout page, iScrollWindowContent content)
    {
        var timedButton = content.transform.GetComponent<CM_TimedButton>();
        if (timedButton != null)
        {
            timedButton.TextMeshRoot = page.transform;
            timedButton.m_holdButtonDuration = 0f;
            timedButton.ForcePopupLayer();
        }

        var arrow = content.transform.Find("Arrow");
        arrow?.gameObject.SetActive(false);

        var mainText = content.transform.Find("MainText");
        if (mainText == null)
            return;

        var text = mainText.GetComponent<TextMeshPro>();
        text.fontStyle = FontStyles.Normal;
        text.m_fontStyle = FontStyles.Normal;
        text.m_FontStyleInternal = FontStyles.Normal;

        var rect = mainText.GetComponent<RectTransform>();
        rect.offsetMax = new Vector2(RowTextWidth, rect.offsetMax.y);
    }

    // ------------------------------------------------------------------------------------------
    // Lobby expansion transpiler (unchanged)
    // ------------------------------------------------------------------------------------------

    /// Replaces ONLY the `ldc.i4.4` that is part of the loop's own condition check - matched by
    /// requiring it to sit between a load of the loop counter and a comparison/branch opcode
    /// (the standard shape the C# compiler emits for `for (int i = 0; i < 4; i++)`, including
    /// the extra `clt`/`stloc`/`ldloc`/`brtrue` steps Debug builds add - ModList.dll is a Debug
    /// build per its AssemblyInfo). Any other `ldc.i4.4` in the method (e.g. the GuiAnchor.MidLeft
    /// enum constant passed to AddRectComp) is left completely alone. Throws if the pattern isn't
    /// found exactly once rather than silently guessing wrong.
    [HarmonyWrapSafe]
    private static IEnumerable<CodeInstruction> HardcodedFour_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getLoopBound = AccessTools.Method(typeof(ModListPatch), nameof(GetLoopBound));
        var codes = new List<CodeInstruction>(instructions);
        var matchIndices = new List<int>();

        for (var i = 0; i < codes.Count - 2; i++)
        {
            if (IsLdloc(codes[i].opcode)
                && codes[i + 1].opcode == OpCodes.Ldc_I4_4
                && IsCompareOrBranch(codes[i + 2].opcode))
            {
                matchIndices.Add(i + 1);
            }
        }

        if (matchIndices.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly 1 loop-bound pattern (ldloc; ldc.i4.4; compare/branch), found {matchIndices.Count}. " +
                "ModList's compiled IL may have changed - refusing to guess which constant is the loop bound.");
        }

        // Mutate in place rather than replace the object - preserves the instruction's identity,
        // which HarmonyX's bake step tracks jump targets by ("Bad label content in ILGenerator").
        var target = codes[matchIndices[0]];
        target.opcode = OpCodes.Call;
        target.operand = getLoopBound;

        return codes;
    }

    private static bool IsLdloc(OpCode opcode)
    {
        return opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1
            || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3
            || opcode == OpCodes.Ldloc_S || opcode == OpCodes.Ldloc;
    }

    private static bool IsCompareOrBranch(OpCode opcode)
    {
        return opcode == OpCodes.Clt || opcode == OpCodes.Clt_Un
            || opcode == OpCodes.Cgt || opcode == OpCodes.Cgt_Un
            || opcode == OpCodes.Blt || opcode == OpCodes.Blt_S || opcode == OpCodes.Blt_Un || opcode == OpCodes.Blt_Un_S
            || opcode == OpCodes.Bge || opcode == OpCodes.Bge_S || opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S
            || opcode == OpCodes.Ble || opcode == OpCodes.Ble_S || opcode == OpCodes.Ble_Un || opcode == OpCodes.Ble_Un_S
            || opcode == OpCodes.Bgt || opcode == OpCodes.Bgt_S || opcode == OpCodes.Bgt_Un || opcode == OpCodes.Bgt_Un_S;
    }

    private static int GetLoopBound()
    {
        var playerLobbyBars = (Array?)Instance._playerLobbyBarsField?.GetValue(null);

        return playerLobbyBars is null
            ? PluginConfig.MaxPlayers
            : Math.Min(PluginConfig.MaxPlayers, playerLobbyBars.Length);
    }
}
