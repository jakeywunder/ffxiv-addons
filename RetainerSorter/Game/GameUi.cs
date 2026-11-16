using System;
using System.Collections.Generic;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace RetainerSorter.Game;

/// <summary>
/// The few game windows the sorter drives: the retainer list, the retainer's dialogue and menu,
/// and the retainer's inventory. Each call does one small thing; the executor decides the order.
/// </summary>
internal static unsafe class GameUi
{
    public const string RetainerList = "RetainerList";
    public const string SelectString = "SelectString";
    public const string Talk = "Talk";

    // Text of the retainer menu's entries, from the game's "Addon" sheet.
    private const uint EntrustItemsText = 2378; // "Entrust or withdraw items. (Slots filled: <n>)"
    private const uint QuitText = 2383;         // "Quit."

    // The retainer list hands its rows to the window as plain values: ten per retainer, starting
    // at the fourth value, with the name first and whether the row can be chosen ninth.
    private const int ListFirstRow = 3;
    private const int ListValuesPerRow = 10;
    private const int ListRowActiveOffset = 8;

    private static readonly string[] RetainerInventoryNames = ["InventoryRetainer", "InventoryRetainerLarge"];

    /// <summary> The window if it is on screen and finished loading, otherwise null. </summary>
    public static AtkUnitBase* Ready(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    public static bool IsVisible(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
        return addon != null && addon->IsVisible;
    }

    public static AtkUnitBase* RetainerInventory()
    {
        foreach (var name in RetainerInventoryNames)
        {
            var addon = Ready(name);
            if (addon != null)
                return addon;
        }

        return null;
    }

    public static bool RetainerInventoryVisible()
    {
        foreach (var name in RetainerInventoryNames)
        {
            if (IsVisible(name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a retainer's row in the open retainer list by name. -1 if it is missing or greyed out.
    /// If the window's names cannot be read at all (a game patch moved them), the retainer's
    /// position is used instead; the executor checks which retainer actually opened before it
    /// moves anything.
    /// </summary>
    public static int FindRetainerRow(AtkUnitBase* list, string name, int position)
    {
        var readable = 0;
        for (var row = 0; row < Sorting.Layout.MaxRetainers; row++)
        {
            var index = ListFirstRow + row * ListValuesPerRow;
            if (index + ListRowActiveOffset >= list->AtkValuesCount)
                break;

            var shown = ReadString(list->AtkValues[index]);
            if (shown.Length > 0)
                readable++;

            if (!string.Equals(shown, name, StringComparison.Ordinal))
                continue;

            var active = list->AtkValues[index + ListRowActiveOffset];
            return active.Type == AtkValueType.Bool && active.Byte == 0 ? -1 : row;
        }

        return readable == 0 ? position : -1;
    }

    /// <summary> Chooses a retainer in the retainer list, the same as clicking its row. </summary>
    public static void SelectRetainer(AtkUnitBase* list, int row)
    {
        var values = stackalloc AtkValue[4];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
        values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = (uint)row };
        values[2] = default;
        values[3] = default;
        list->FireCallback(4, values, true);
    }

    /// <summary> Advances the retainer's greeting or farewell line. </summary>
    public static void ClickTalk(AtkUnitBase* talk)
    {
        var atkEvent = stackalloc AtkEvent[1];
        *atkEvent = default;
        atkEvent->Listener = (AtkEventListener*)talk;
        atkEvent->Target = &AtkStage.Instance()->AtkEventTarget;
        atkEvent->State.StateFlags = (AtkEventStateFlags)132;

        var data = stackalloc AtkEventData[1];
        *data = default;

        talk->ReceiveEvent(AtkEventType.MouseDown, 0, atkEvent, data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, atkEvent, data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, atkEvent, data);
    }

    public static List<string> MenuEntries(AtkUnitBase* menu)
    {
        var entries = new List<string>();
        ref var popup = ref ((AddonSelectString*)menu)->PopupMenu.PopupMenu;
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var text = popup.EntryNames[i].Value;
            entries.Add(text == null ? string.Empty : MemoryHelper.ReadSeStringNullTerminated((nint)text).TextValue);
        }

        return entries;
    }

    public static void SelectMenuEntry(AtkUnitBase* menu, int index)
    {
        var values = stackalloc AtkValue[1];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = index };
        menu->FireCallback(1, values, true);
    }

    /// <summary> "Entrust or withdraw items", which is also the first entry of a retainer's menu. </summary>
    public static int FindEntrustEntry(List<string> entries)
    {
        var prefix = LeadingText(EntrustItemsText);
        if (prefix.Length >= 4)
        {
            var index = entries.FindIndex(entry => entry.StartsWith(prefix, StringComparison.Ordinal));
            if (index >= 0)
                return index;
        }

        return entries.Count >= 2 ? 0 : -1;
    }

    /// <summary> "Quit", which is also the last entry of a retainer's menu. </summary>
    public static int FindQuitEntry(List<string> entries)
    {
        var text = LeadingText(QuitText);
        var index = text.Length == 0 ? -1 : entries.FindIndex(entry => entry.StartsWith(text, StringComparison.Ordinal));
        return index >= 0 ? index : entries.Count - 1;
    }

    /// <summary>
    /// Sends the open retainer away from its inventory, back to its menu. Normally the retainer
    /// screen is asked to close itself; <paramref name="force"/> closes the inventory window directly.
    /// </summary>
    public static void CloseRetainerInventory(bool force)
    {
        if (force)
        {
            foreach (var name in RetainerInventoryNames)
            {
                var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
                if (addon != null && addon->IsVisible)
                    addon->Close(true);
            }

            return;
        }

        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (agent != null && agent->IsAgentActive())
            agent->Hide();
    }

    public static ulong ActiveRetainerId()
    {
        var manager = RetainerManager.Instance();
        return manager == null ? 0 : manager->LastSelectedRetainerId;
    }

    /// <summary> A sheet text up to its first placeholder, e.g. the part before the slot count. </summary>
    private static string LeadingText(uint addonRow)
    {
        if (!Plugin.DataManager.GetExcelSheet<Addon>().TryGetRow(addonRow, out var row))
            return string.Empty;

        var text = string.Empty;
        foreach (var payload in row.Text)
        {
            if (payload.Type != ReadOnlySePayloadType.Text)
                break;

            text += payload.ToString();
        }

        return text;
    }

    private static string ReadString(AtkValue value)
    {
        if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString))
            return string.Empty;

        var text = value.String.Value;
        return text == null ? string.Empty : MemoryHelper.ReadSeStringNullTerminated((nint)text).TextValue;
    }
}
