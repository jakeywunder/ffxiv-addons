using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using RetainerSorter.Data;
using RetainerSorter.Game;
using RetainerSorter.Sorting;

namespace RetainerSorter.Ui;

/// <summary>
/// The sorter's own window, built from native game nodes. Three tabs:
/// Sort (preview, Execute, Stop), Rules (profiles and their rules), and Options.
/// </summary>
internal sealed unsafe partial class SorterWindow : NativeAddon
{
    public static readonly Vector2 WindowSize = new(700.0f, 670.0f);

    private const float TabBarHeight = 28.0f;
    private const float RowHeight = 24.0f;
    private const long ProgressIntervalMs = 250;
    private const long ForecastIntervalMs = 2000;

    private enum Tab
    {
        Sort,
        Deposit,
        Rules,
        Options,
    }

    private const string DepositTabLabel = "Deposit";

    private Tab tab = Tab.Sort;
    private TabBarNode? tabBar;
    private bool openOnDeposit;
    private readonly ResNode?[] panels = new ResNode?[4];
    private readonly List<Action> collapseDropDowns = [];

    // Set while the code itself fills in controls, so their change handlers stay quiet.
    private bool updating;
    private bool built;
    private bool wasRunning;
    private long nextProgress;
    private long nextForecast;
    private int expectedMoves;

    private static Configuration Config => Plugin.Config;
    private static Profile Profile => Plugin.Config.Active;

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        collapseDropDowns.Clear();

        var origin = ContentStartPosition;
        var size = ContentSize;

        var tabs = new TabBarNode
        {
            Position = origin,
            Size = new Vector2(size.X, TabBarHeight),
        };
        tabs.AddTab("Sort", () => ShowTab(Tab.Sort));
        tabs.AddTab(DepositTabLabel, () => ShowTab(Tab.Deposit));
        tabs.AddTab("Rules", () => ShowTab(Tab.Rules));
        tabs.AddTab("Options", () => ShowTab(Tab.Options));
        tabs.AttachNode(this);
        tabBar = tabs;

        var panelPosition = origin + new Vector2(0.0f, TabBarHeight + 8.0f);
        var panelSize = size - new Vector2(0.0f, TabBarHeight + 8.0f);
        for (var i = 0; i < panels.Length; i++)
        {
            panels[i] = new ResNode
            {
                Position = panelPosition,
                Size = panelSize,
                IsVisible = i == 0,
            };
            panels[i]!.AttachNode(this);
        }

        BuildSortTab(panels[(int)Tab.Sort]!, panelSize);
        BuildDepositTab(panels[(int)Tab.Deposit]!, panelSize);
        BuildRulesTab(panels[(int)Tab.Rules]!, panelSize);
        BuildOptionsTab(panels[(int)Tab.Options]!, panelSize);

        built = true;
        tab = Tab.Sort;
        wasRunning = Plugin.Executor.IsRunning;
        RefreshRules();
        RefreshOptions();
        RefreshSort();

        if (openOnDeposit)
        {
            openOnDeposit = false;
            tabs.SelectTab(DepositTabLabel);
            ShowTab(Tab.Deposit);
        }
    }

    /// <summary> Opens the window on the Deposit tab, or switches to it. Main thread only. </summary>
    public void OpenDeposit()
    {
        if (built && IsOpen)
        {
            tabBar?.SelectTab(DepositTabLabel);
            ShowTab(Tab.Deposit);
            return;
        }

        openOnDeposit = true;
        Open();
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        // The nodes go away with the window.
        built = false;
        tabBar = null;
        collapseDropDowns.Clear();
        Array.Clear(panels);
        ForgetSortNodes();
        ForgetDepositNodes();
        ForgetRulesNodes();
        ForgetOptionsNodes();
    }

    /// <summary> Called every frame by the plugin. Keeps progress and the preview current while the window is open. </summary>
    public void Tick()
    {
        if (!built || !IsOpen)
        {
            deferred = null;
            return;
        }

        if (deferred != null)
        {
            var run = deferred;
            deferred = null;
            run();
        }

        var now = Environment.TickCount64;
        var running = Plugin.Executor.IsRunning;
        if (running != wasRunning)
        {
            wasRunning = running;
            RefreshSort();
            RefreshDeposit();
            return;
        }

        if (running)
        {
            if (now >= nextProgress)
            {
                nextProgress = now + ProgressIntervalMs;
                ShowProgress();
                ShowDepositProgress();
            }
        }
        else if (now >= nextForecast)
        {
            if (tab == Tab.Sort)
                RefreshSort();
            else if (tab == Tab.Deposit)
                RefreshDeposit();
        }
    }

    private void ShowTab(Tab next)
    {
        if (!built)
            return;

        foreach (var collapse in collapseDropDowns)
            collapse();

        tab = next;
        for (var i = 0; i < panels.Length; i++)
            panels[i]!.IsVisible = i == (int)next;

        // Shown and hidden nodes need to be picked up for drawing and mouse input.
        if (InternalAddon != null)
        {
            InternalAddon->UldManager.UpdateDrawNodeList();
            InternalAddon->UpdateCollisionNodeList(false);
        }

        switch (next)
        {
            case Tab.Sort: RefreshSort(); break;
            case Tab.Deposit: RefreshDeposit(); break;
            case Tab.Rules: RefreshRules(); break;
            case Tab.Options: RefreshOptions(); break;
        }
    }

    /// <summary>
    /// Runs something on the next frame. Controls call back from inside their own click handling,
    /// and rebuilding a list or drop-down at that moment would pull the rug out from under them.
    /// </summary>
    private void Later(Action action) => deferred += action;

    private Action? deferred;

    private void Quietly(Action fill)
    {
        updating = true;
        try
        {
            fill();
        }
        finally
        {
            updating = false;
        }
    }

    private static List<RetainerEntry> Retainers() => Inventories.Retainers();

    private static string RetainerLabel(List<RetainerEntry> retainers, int position)
        => position < retainers.Count ? retainers[position].Name : $"Retainer {position + 1}";

    private static TextNode Label(NodeBase parent, Vector2 position, Vector2 size, ReadOnlySeString text, RowTone tone = RowTone.Normal, uint fontSize = 13)
    {
        var node = new TextNode
        {
            Position = position,
            Size = size,
            FontSize = fontSize,
            AlignmentType = AlignmentType.Left,
            TextColor = RowColors.For(tone),
            TextOutlineColor = RowColors.Outline,
            String = text,
        };
        node.AttachNode(parent);
        return node;
    }

    private static TextNode Paragraph(NodeBase parent, Vector2 position, Vector2 size, RowTone tone = RowTone.Normal)
    {
        var node = Label(parent, position, size, string.Empty, tone);
        node.AlignmentType = AlignmentType.TopLeft;
        node.TextFlags = TextFlags.MultiLine | TextFlags.WordWrap;
        node.LineSpacing = 17;
        return node;
    }

    private static TextButtonNode Button(NodeBase parent, Vector2 position, float width, ReadOnlySeString text, Action onClick, string? tooltip = null)
    {
        var node = new TextButtonNode
        {
            Position = position,
            Size = new Vector2(width, 28.0f),
            String = text,
            OnClick = onClick,
        };
        if (tooltip != null)
            node.TextTooltip = tooltip;

        node.AttachNode(parent);
        return node;
    }

    private static CheckboxNode Checkbox(NodeBase parent, Vector2 position, float width, ReadOnlySeString text, Action<bool> onClick, string? tooltip = null)
    {
        var node = new CheckboxNode
        {
            Position = position,
        };

        // Font size first so the label measures itself correctly, then a fixed width so the
        // layout does not depend on the text.
        node.Label.FontSize = 13;
        node.String = text;
        node.Size = new Vector2(width, 20.0f);
        node.OnClick = onClick;
        if (tooltip != null)
            node.CollisionNode.TextTooltip = tooltip;

        node.AttachNode(parent);
        return node;
    }
}
