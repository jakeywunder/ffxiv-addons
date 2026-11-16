using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HuntingLogPlus.Data;
using HuntingLogPlus.Game;
using KamiToolKit.Classes;
using KamiToolKit.Controllers;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;

namespace HuntingLogPlus.Ui;

/// <summary>
/// Lives inside the game's Hunting Log window ("MonsterNote").
///
/// Normal view: the game's page is untouched, and clicking a monster's icon flags it on the map.
/// "All remaining" view: the game's right-hand page is moved out of sight and replaced by
/// a native list of unfinished targets, with sorting controls. The list follows the class or
/// Grand Company icon selected on the left, or covers every class at once.
///
/// Node ids and positions come from the window's layout file, ui/uld/MonsterNoteBook.uld.
/// </summary>
internal sealed unsafe class HuntingLogController : IDisposable
{
    private const string AddonName = "MonsterNote";

    // Top-level nodes of the game's right-hand page.
    private const uint HeaderNodeId = 18;
    private const uint FilterDropDownNodeId = 38;
    private const uint EmptyTextNodeId = 39;
    private const uint RequirementsNodeId = 40;
    private const uint TreeListNodeId = 46;
    private static readonly uint[] ReplacedNodeIds = [HeaderNodeId, FilterDropDownNodeId, EmptyTextNodeId, RequirementsNodeId, TreeListNodeId];

    // The icons on the left: nine class buttons (nodes 4 to 12), then the Grand Company's
    // (node 13), which is its own kind of button.
    private const uint FirstClassTabNodeId = 4;
    private const uint GrandCompanyTabNodeId = 13;
    private const ushort ClassTabComponentId = 1013;
    private const ushort GrandCompanyTabComponentId = 1021;

    // The classes those nine buttons stand for, in the order the window shows them: Gladiator, Pugilist,
    // Marauder, Lancer / Archer, Rogue, Conjurer, Thaumaturge / Arcanist.
    private static readonly uint[] ClassTabOrder = [1, 2, 3, 4, 5, 29, 6, 7, 26];

    // How the game numbers Hunting Log entries: class * 10000 + ..., Grand Company * 1000000 + ...
    private const uint ClassBaseId = 10000;
    private const uint GrandCompanyBaseId = 1000000;

    // A monster row inside the game's list: component 1017, icon node 3, name node 4.
    private const ushort MonsterRowComponentId = 1017;
    private const uint RowIconNodeId = 3;
    private const uint RowNameNodeId = 4;

    // The free strip left of the game's filter drop-down.
    private static readonly Vector2 TogglePosition = new(166.0f, 122.0f);

    // The game's right-hand page: header at y=38, list down to y=562.
    private static readonly Vector2 PanelPosition = new(164.0f, 38.0f);
    private static readonly Vector2 PanelSize = new(380.0f, 528.0f);

    private const float OffscreenOffset = 10000.0f;
    private const long RefreshIntervalMs = 500;
    private const long SelectionSettleMs = 400;
    private const long ClickDebounceMs = 300;

    private const string NothingLeftMessage = "Nothing left to hunt here.";
    private const string ClassLockedMessage = "You have not unlocked this class.";

    private readonly HuntData data;
    private readonly TargetService targets;
    private readonly AddonController controller;
    private readonly Dictionary<uint, Vector2> nativePositions = new();

    private CheckboxNode? viewToggle;
    private CheckboxNode? allClassesCheckbox;
    private ResNode? panel;
    private TextNode? countText;
    private DropDownNode<SortMode>? sortDropDown;
    private ListNode<TargetRow, TargetRowNode>? list;

    // Our own click listener on the game's list, in case the window itself does not listen for row clicks.
    private IAddonEventHandle? nativeListClick;
    private long lastNativeActivation;

    private List<TargetRow> shownRows = [];
    private bool combined;
    private bool suppressToggle;
    private bool refreshRequested;
    private bool resetScrollRequested;
    private long nextRefresh;

    // The class or Grand Company whose icon is selected on the left; 0 when it cannot be told.
    private uint followedJob;

    // The rank the game's page shows for that selection. Right after the window opens or an icon
    // is clicked the game is still settling on its page, so changes are followed quietly for a moment.
    private byte followedRank;
    private bool selectionSettled;
    private long selectionSettledAt;

    private string emptyMessage = NothingLeftMessage;
    private bool warnedUnknownSelection;

    private static Configuration Config => Plugin.Config;

    public HuntingLogController(HuntData data)
    {
        this.data = data;
        targets = new TargetService(data);

        controller = new AddonController
        {
            AddonName = AddonName,
            OnSetup = OnSetup,
            OnFinalize = OnFinalize,
            OnUpdate = OnUpdate,
        };
    }

    /// <summary> Must be called on the game's main thread. </summary>
    public void Enable()
    {
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, AddonName, OnReceiveEvent);
        controller.Enable();
    }

    /// <summary> Must be called on the game's main thread. </summary>
    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, AddonName, OnReceiveEvent);
        controller.Dispose();
    }

    private void OnSetup(AtkUnitBase* addon)
    {
        try
        {
            DisposeNodes();
            nativePositions.Clear();

            foreach (var id in ReplacedNodeIds)
            {
                var node = addon->GetNodeById(id);
                if (node == null)
                {
                    // A game patch changed the window. Do nothing rather than guess.
                    Plugin.Log.Warning($"Hunting Log node {id} was not found; the window layout has changed, so Hunting Log Plus is staying out of it.");
                    nativePositions.Clear();
                    return;
                }

                nativePositions[id] = new Vector2(node->X, node->Y);
            }

            BuildNodes(addon);
            combined = false;
            ApplyView(addon, Config.ShowAllRemaining);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to set up the Hunting Log additions.");
        }
    }

    private void OnFinalize(AtkUnitBase* addon)
    {
        try
        {
            if (combined)
                MoveNativePage(addon, false);

            combined = false;
            DisposeNodes();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to clean up the Hunting Log additions.");
        }
    }

    private void OnUpdate(AtkUnitBase* addon)
    {
        if (panel == null || !combined)
            return;

        try
        {
            var now = Environment.TickCount64;
            var job = SelectedJob(addon);
            var rank = CurrentRank();

            if (!selectionSettled)
            {
                if (job != followedJob)
                {
                    followedJob = job;
                    refreshRequested = true;
                }

                followedRank = rank;
                if (now >= selectionSettledAt)
                {
                    selectionSettled = true;
                    LogSelection(addon, "settled");
                    WarnIfSelectionUnknown();
                }
            }
            else if (job != followedJob)
            {
                // An icon on the left was clicked: the list follows it.
                followedJob = job;
                OnClassIconClicked(now);
                LogSelection(addon, "icon clicked");
            }
            else if (rank != followedRank)
            {
                // The rank list on the left still drives the game's own page.
                // Using it means the player wants that page back.
                LeaveCombinedView(addon);
                return;
            }

            MoveNativePage(addon, true);

            if (refreshRequested || now >= nextRefresh)
            {
                nextRefresh = now + RefreshIntervalMs;
                RefreshList(refreshRequested);
                refreshRequested = false;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to update the Hunting Log additions.");
        }
    }

    private void BuildNodes(AtkUnitBase* addon)
    {
        viewToggle = MakeCheckbox(
            "All remaining",
            Config.ShowAllRemaining,
            TogglePosition,
            150.0f,
            "List the targets you still need in one sortable list. Click a class or Grand Company icon on the left to choose whose.",
            OnViewToggled);
        viewToggle.AttachNode(addon);

        panel = new ResNode
        {
            Position = PanelPosition,
            Size = PanelSize,
        };
        panel.AttachNode(addon);

        var title = new TextNode
        {
            Position = new Vector2(4.0f, 2.0f),
            Size = new Vector2(250.0f, 30.0f),
            FontType = FontType.Jupiter,
            FontSize = 23,
            AlignmentType = AlignmentType.Left,
            TextColor = ColorHelper.GetColor(22),
            String = "Remaining Targets",
        };
        title.AttachNode(panel);

        countText = new TextNode
        {
            Position = new Vector2(258.0f, 8.0f),
            Size = new Vector2(112.0f, 22.0f),
            FontSize = 14,
            AlignmentType = AlignmentType.Right,
            TextColor = ColorHelper.GetColor(2),
        };
        countText.AttachNode(panel);

        var hint = new TextNode
        {
            Position = new Vector2(6.0f, 32.0f),
            Size = new Vector2(366.0f, 16.0f),
            FontSize = 12,
            AlignmentType = AlignmentType.Left,
            TextColor = ColorHelper.GetColor(3),
            String = "Click a target to flag it on the map. Shift+Click also teleports.",
        };
        hint.AttachNode(panel);

        allClassesCheckbox = MakeCheckbox(
            "All classes",
            Config.AllClasses,
            new Vector2(4.0f, 52.0f),
            116.0f,
            "Show every class you have unlocked and your Grand Company together, instead of only the icon selected on the left. Faded targets belong to a class you are not playing right now.",
            value =>
            {
                Config.AllClasses = value;
                OnListOptionChanged();
            });
        allClassesCheckbox.AttachNode(panel);

        var nearest = MakeCheckbox(
            "Nearest first",
            Config.NearestFirst,
            new Vector2(124.0f, 52.0f),
            120.0f,
            "Put targets in your current zone on top, closest first. Other zones follow from the nearest to the farthest, then dungeons.",
            value =>
            {
                Config.NearestFirst = value;
                OnListOptionChanged();
            });
        nearest.AttachNode(panel);

        var autoFlag = MakeCheckbox(
            "Auto-flag next",
            Config.AutoFlagNext,
            new Vector2(248.0f, 52.0f),
            126.0f,
            "When you finish a target, flag the next nearest one on the map automatically.",
            value =>
            {
                Config.AutoFlagNext = value;
                Config.Save();
            });
        autoFlag.AttachNode(panel);

        // Sits exactly where the game's own filter drop-down is.
        sortDropDown = new DropDownNode<SortMode>
        {
            Position = new Vector2(164.0f, 82.0f),
            Size = new Vector2(210.0f, 24.0f),
            GetLabelFunction = SortLabel,
            MaxListOptions = 2,
            Options = [SortMode.Zone, SortMode.Rank],
            SelectedOption = Config.Sort,
            OnOptionSelected = mode =>
            {
                Config.Sort = mode;
                OnListOptionChanged();
            },
        };
        sortDropDown.AttachNode(panel);

        // Same place and size as the game's list.
        list = new ListNode<TargetRow, TargetRowNode>
        {
            Position = new Vector2(0.0f, 108.0f),
            Size = new Vector2(380.0f, 416.0f),
            ItemSpacing = 2.0f,
            AutoResetScroll = false,
            OptionsList = [],
            OnItemSelected = OnRowClicked,
        };
        list.NoResultsTextNode.TextId = 0;
        list.NoResultsTextNode.String = NothingLeftMessage;
        emptyMessage = NothingLeftMessage;
        list.AttachNode(panel);

        shownRows = [];

        var nativeList = addon->GetNodeById(TreeListNodeId);
        if (nativeList != null)
            nativeListClick = Plugin.AddonEventManager.AddEvent((nint)addon, (nint)nativeList, AddonEventType.ListItemClick, OnNativeListClick);
    }

    private static CheckboxNode MakeCheckbox(string label, bool isChecked, Vector2 position, float width, string tooltip, Action<bool> onClick)
    {
        var checkbox = new CheckboxNode
        {
            Position = position,
        };

        // Matches the 12pt labels of the game's own controls in this window.
        // Set before the text so the label measures itself at the right size.
        checkbox.Label.FontSize = 12;
        checkbox.String = label;

        // Setting the text auto-fits the width; a fixed width keeps the layout predictable.
        checkbox.Size = new Vector2(width, 20.0f);
        checkbox.IsChecked = isChecked;
        checkbox.CollisionNode.TextTooltip = tooltip;
        checkbox.OnClick = onClick;
        return checkbox;
    }

    private static ReadOnlySeString SortLabel(SortMode mode) => mode switch
    {
        SortMode.Rank => "Sort by rank",
        _ => "Sort by zone",
    };

    private void DisposeNodes()
    {
        if (nativeListClick != null)
        {
            Plugin.AddonEventManager.RemoveEvent(nativeListClick);
            nativeListClick = null;
        }

        sortDropDown?.Collapse(false);

        viewToggle?.Dispose();
        viewToggle = null;

        // Disposing the panel disposes everything attached to it.
        panel?.Dispose();
        panel = null;

        allClassesCheckbox = null;
        countText = null;
        sortDropDown = null;
        list = null;
        shownRows = [];
    }

    private void OnViewToggled(bool value)
    {
        if (suppressToggle)
            return;

        var addon = GetAddon();
        if (addon == null || panel == null)
            return;

        Config.ShowAllRemaining = value;
        Config.Save();
        ApplyView(addon, value);
    }

    private void LeaveCombinedView(AtkUnitBase* addon)
    {
        Config.ShowAllRemaining = false;
        Config.Save();

        if (viewToggle != null)
        {
            suppressToggle = true;
            viewToggle.IsChecked = false;
            suppressToggle = false;
        }

        ApplyView(addon, false);
    }

    private void ApplyView(AtkUnitBase* addon, bool showCombined)
    {
        if (panel == null)
            return;

        combined = showCombined;
        panel.IsVisible = showCombined;
        MoveNativePage(addon, showCombined);

        if (showCombined)
        {
            followedJob = SelectedJob(addon);
            followedRank = CurrentRank();
            selectionSettled = false;
            selectionSettledAt = Environment.TickCount64 + SelectionSettleMs;
            refreshRequested = true;
            resetScrollRequested = true;
        }

        // Shown and hidden nodes need to be picked up for drawing and mouse input.
        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);
    }

    /// <summary>
    /// Slides the game's right-hand page far outside the window, or back. The game keeps running
    /// that page as usual, so nothing about its own state has to be tracked or restored.
    /// </summary>
    private void MoveNativePage(AtkUnitBase* addon, bool offscreen)
    {
        foreach (var (id, original) in nativePositions)
        {
            var node = addon->GetNodeById(id);
            if (node == null)
                continue;

            var targetX = offscreen ? original.X + OffscreenOffset : original.X;
            if (MathF.Abs(node->X - targetX) > 0.5f || MathF.Abs(node->Y - original.Y) > 0.5f)
                node->SetPositionFloat(targetX, original.Y);
        }
    }

    private void OnListOptionChanged()
    {
        Config.Save();
        refreshRequested = true;
        resetScrollRequested = true;
    }

    /// <summary>
    /// A class or Grand Company icon on the left was clicked. The list narrows to it, which
    /// also means leaving "All classes": otherwise the click would appear to do nothing.
    /// </summary>
    private void OnClassIconClicked(long now)
    {
        if (Config.AllClasses)
        {
            Config.AllClasses = false;
            Config.Save();

            if (allClassesCheckbox != null)
                allClassesCheckbox.IsChecked = false;
        }

        // The game picks the rank to show for the new class; let that settle before watching the rank list again.
        followedRank = CurrentRank();
        selectionSettled = false;
        selectionSettledAt = now + SelectionSettleMs;

        refreshRequested = true;
        resetScrollRequested = true;
    }

    /// <summary> The logs the list covers: every class, or the one whose icon is selected on the left. </summary>
    private List<uint> JobsToShow()
    {
        if (Config.AllClasses)
            return TargetService.AllJobs();

        // Without a readable selection, show what can be hunted right now.
        if (followedJob == 0)
            return TargetService.CreditableJobs();

        return IsFollowedClassLocked() ? [] : [followedJob];
    }

    private bool IsFollowedClassLocked()
        => followedJob != 0 && !HuntProgress.IsGrandCompany(followedJob) && !HuntProgress.IsUnlocked(followedJob);

    private void RefreshList(bool force)
    {
        if (list == null || countText == null)
            return;

        var rows = targets.Refresh(JobsToShow());
        TargetService.Sort(rows, Config.Sort, Config.NearestFirst);

        var message = !Config.AllClasses && IsFollowedClassLocked() ? ClassLockedMessage : NothingLeftMessage;
        if (message != emptyMessage)
        {
            emptyMessage = message;
            list.NoResultsTextNode.String = message;
        }

        // Keep rows from jumping under the cursor: while the mouse is over the list,
        // only numbers update. The order changes once a target is added or removed.
        if (!force && IsCursorOverList() && SameRows(rows, shownRows))
            rows = shownRows;

        if (!rows.SequenceEqual(shownRows))
        {
            shownRows = rows;
            list.OptionsList = rows;
        }

        if (resetScrollRequested)
        {
            resetScrollRequested = false;
            list.ResetScroll();
        }

        list.Update();
        countText.String = rows.Count == 1 ? "1 target" : $"{rows.Count} targets";
    }

    private static bool SameRows(List<TargetRow> left, List<TargetRow> right)
    {
        if (left.Count != right.Count)
            return false;

        var set = new HashSet<TargetRow>(right);
        return left.All(set.Contains);
    }

    private bool IsCursorOverList()
    {
        if (list == null || !TryGetCursor(out var x, out var y))
            return false;

        AtkResNode* node = list;
        return node != null && node->CheckCollisionAtCoords(x, y, true);
    }

    private static void OnRowClicked(TargetRow? row)
    {
        if (row == null)
            return;

        HuntActions.Activate(row.Location, IsShiftHeld);
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs eventArgs)
            return;

        try
        {
            var eventType = (AtkEventType)eventArgs.AtkEventType;

            if (Config.DebugLog && eventType is not (AtkEventType.MouseMove or AtkEventType.MouseOver or AtkEventType.MouseOut
                    or AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut or AtkEventType.FocusStart or AtkEventType.FocusStop))
                Plugin.Log.Info($"[debug] Hunting Log event {eventType} ({(int)eventType}), param {eventArgs.EventParam}");

            if (combined || panel == null || eventType != AtkEventType.ListItemClick)
                return;

            var addon = (AtkUnitBase*)eventArgs.Addon.Address;
            if (addon != null)
                HandleNativeRowClick(addon);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to handle a Hunting Log click.");
        }
    }

    private void OnNativeListClick(AddonEventType type, AddonEventData eventData)
    {
        try
        {
            if (Config.DebugLog)
                Plugin.Log.Info($"[debug] Hunting Log list click via own listener, param {eventData.Param}");

            if (combined || panel == null)
                return;

            var addon = (AtkUnitBase*)eventData.AddonPointer;
            if (addon != null)
                HandleNativeRowClick(addon);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to handle a Hunting Log click.");
        }
    }

    /// <summary>
    /// A row of the game's own list was clicked. If the cursor is on that row's monster icon,
    /// flag the monster on the map. The row is found by position and read by name, so the
    /// game's list itself is never modified.
    /// </summary>
    private void HandleNativeRowClick(AtkUnitBase* addon)
    {
        // The same click can arrive through both listeners; act on it once.
        var now = Environment.TickCount64;
        if (now - lastNativeActivation < ClickDebounceMs)
            return;

        if (!TryGetCursor(out var x, out var y))
            return;

        var listNode = addon->GetNodeById(TreeListNodeId);
        if (listNode == null || (ushort)listNode->Type < 1000)
            return;

        var listComponent = ((AtkComponentNode*)listNode)->Component;
        if (listComponent == null)
            return;

        var nodes = listComponent->UldManager.NodeList;
        var count = listComponent->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            if (node == null || (ushort)node->Type != MonsterRowComponentId || !node->IsVisible())
                continue;

            var row = ((AtkComponentNode*)node)->Component;
            if (row == null)
                continue;

            var icon = row->UldManager.SearchNodeById(RowIconNodeId);
            if (icon == null || !icon->CheckCollisionAtCoords(x, y, true))
                continue;

            var nameNode = row->UldManager.SearchNodeById(RowNameNodeId);
            if (nameNode == null || nameNode->Type != NodeType.Text)
                return;

            var shownName = ((AtkTextNode*)nameNode)->NodeText.ToString();
            var monster = FindMonster(addon, shownName);
            if (Config.DebugLog)
                Plugin.Log.Info($"[debug] Icon click on \"{shownName}\" -> {(monster == null ? "no match" : monster.Name)}");

            if (monster == null || monster.Locations.Count == 0)
                return;

            var territory = (uint)Plugin.ClientState.TerritoryType;
            var location = monster.Locations.FirstOrDefault(candidate => candidate.Terri == territory) ?? monster.Locations[0];
            lastNativeActivation = now;
            HuntActions.Activate(location, IsShiftHeld);
            return;
        }
    }

    /// <summary> Finds a monster by the name the game shows, preferring the class and rank on screen. </summary>
    private HuntMonster? FindMonster(AtkUnitBase* addon, string shownName)
    {
        var wanted = Normalize(shownName);
        if (wanted.Length == 0)
            return null;

        var job = SelectedJob(addon);
        var rank = CurrentRank();
        if (job != 0 && data.JobRanks.TryGetValue(job, out var ranks) && rank < ranks.Count)
        {
            var onPage = ranks[rank].Tasks.SelectMany(task => task.Monsters).FirstOrDefault(monster => Matches(monster, wanted));
            if (onPage != null)
                return onPage;
        }

        return data.AllMonsters().FirstOrDefault(monster => Matches(monster, wanted));
    }

    private static bool Matches(HuntMonster monster, string wanted)
        => Normalize(monster.DisplayName) == wanted || Normalize(monster.Name) == wanted;

    private static string Normalize(string name)
        => new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim().ToLowerInvariant();

    /// <summary>
    /// The class or Grand Company whose icon is selected on the left, as the key used by the
    /// hunt data (a class id, or 10000 + Grand Company id). 0 when it cannot be told.
    /// </summary>
    private static uint SelectedJob(AtkUnitBase* addon)
    {
        // The highlighted icon is what the player sees, so it decides.
        var tab = CheckedTab(addon);
        if (tab == GrandCompanyTabNodeId)
            return HuntProgress.CurrentGrandCompany;

        if (tab >= FirstClassTabNodeId && tab - FirstClassTabNodeId < ClassTabOrder.Length)
            return ClassTabOrder[tab - FirstClassTabNodeId];

        // No highlighted icon could be found: fall back on the game's own record of the page.
        var agent = AgentMonsterNote.Instance();
        if (agent == null)
            return 0;

        if (agent->BaseId == ClassBaseId && HuntProgress.IsHuntingClass(agent->ClassId))
            return agent->ClassId;

        return agent->BaseId == GrandCompanyBaseId ? HuntProgress.CurrentGrandCompany : 0;
    }

    /// <summary> Node id of the highlighted class or Grand Company icon on the left, or 0 if none is. </summary>
    private static uint CheckedTab(AtkUnitBase* addon)
    {
        for (var id = FirstClassTabNodeId; id <= GrandCompanyTabNodeId; id++)
        {
            var node = addon->GetNodeById(id);
            if (node == null)
                continue;

            var type = (ushort)node->Type;
            if (type != ClassTabComponentId && type != GrandCompanyTabComponentId)
                continue;

            var button = (AtkComponentButton*)((AtkComponentNode*)node)->Component;
            if (button != null && button->IsChecked)
                return id;
        }

        return 0;
    }

    private static byte CurrentRank()
    {
        var agent = AgentMonsterNote.Instance();
        return agent == null ? (byte)0 : agent->Rank;
    }

    private void WarnIfSelectionUnknown()
    {
        if (followedJob != 0 || warnedUnknownSelection)
            return;

        warnedUnknownSelection = true;
        Plugin.Log.Warning("Could not tell which class is selected in the Hunting Log, so the list shows the current class and Grand Company. \"/hlplus debug\" logs the details.");
    }

    private void LogSelection(AtkUnitBase* addon, string reason)
    {
        if (!Config.DebugLog)
            return;

        var agent = AgentMonsterNote.Instance();
        var record = agent == null
            ? "none"
            : $"class id {agent->ClassId}, base {agent->BaseId}, index {agent->ClassIndex}, rank {agent->Rank}";
        Plugin.Log.Info($"[debug] Hunting Log selection ({reason}): following {followedJob}, highlighted button node {CheckedTab(addon)}, game record: {record}");
    }

    private static AtkUnitBase* GetAddon() => RaptureAtkUnitManager.Instance()->GetAddonByName(AddonName, 1);

    private static bool IsShiftHeld => Plugin.KeyState[VirtualKey.SHIFT];

    private static bool TryGetCursor(out short x, out short y)
    {
        x = 0;
        y = 0;

        var input = UIInputData.Instance();
        if (input == null)
            return false;

        var cursorX = input->CursorInputs.PositionX;
        var cursorY = input->CursorInputs.PositionY;
        if (cursorX is < 0 or > short.MaxValue || cursorY is < 0 or > short.MaxValue)
            return false;

        x = (short)cursorX;
        y = (short)cursorY;
        return true;
    }
}
