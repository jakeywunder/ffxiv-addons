using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;
using RetainerSorter.Game;
using RetainerSorter.Sorting;

namespace RetainerSorter.Ui;

/// <summary>
/// The Deposit tab: items in the player's inventory that a retainer already holds, and the button
/// that sends each to that retainer. Clicking an item keeps it with the player instead.
/// </summary>
internal sealed unsafe partial class SorterWindow
{
    // The game's own high-quality mark, part of its font.
    private const string HighQualityMark = " ";

    private TextNode? depositSummary;
    private TextNode? depositStatus;
    private ListNode<DepositRow, DepositRowNode>? depositList;
    private TextButtonNode? depositButton;
    private TextButtonNode? depositStopButton;

    private List<DepositRow> shownDepositRows = [];
    private string shownDepositSummary = string.Empty;
    private bool shownCanDeposit;
    private int shownDepositSignature;
    private int expectedDeposits;
    private string depositNotice = string.Empty;

    private void BuildDepositTab(ResNode panel, Vector2 size)
    {
        var hint = Paragraph(panel, new Vector2(0.0f, 0.0f), new Vector2(size.X, 40.0f), RowTone.Dim);
        hint.String = "Items in your inventory that one of your retainers already holds are put with that retainer. "
            + "Click an item to keep it with you instead; that choice is remembered.";

        depositSummary = Label(panel, new Vector2(0.0f, 44.0f), new Vector2(size.X, 20.0f), string.Empty);

        depositList = new ListNode<DepositRow, DepositRowNode>
        {
            Position = new Vector2(0.0f, 70.0f),
            Size = new Vector2(size.X, size.Y - 70.0f - 66.0f),
            ItemSpacing = 1.0f,
            AutoResetScroll = false,
            OptionsList = [],
            OnItemSelected = OnDepositRowClicked,
        };
        depositList.NoResultsTextNode.TextId = 0;
        depositList.NoResultsTextNode.String = "Nothing to deposit.";
        depositList.AttachNode(panel);

        depositStatus = Label(panel, new Vector2(0.0f, size.Y - 58.0f), new Vector2(size.X, 22.0f), string.Empty);
        depositStatus.TextFlags = FFXIVClientStructs.FFXIV.Component.GUI.TextFlags.Ellipsis;

        var buttonY = size.Y - 30.0f;
        depositButton = Button(panel, new Vector2(0.0f, buttonY), 150.0f, "Deposit", OnDeposit,
            "Puts the items listed above with the retainers that already hold them. Nothing is taken out of a retainer.");
        depositStopButton = Button(panel, new Vector2(158.0f, buttonY), 110.0f, "Stop", OnStop,
            "Finishes the move in progress, then stops.");
    }

    private void ForgetDepositNodes()
    {
        depositSummary = null;
        depositStatus = null;
        depositList = null;
        depositButton = null;
        depositStopButton = null;
        shownDepositRows = [];
        shownDepositSummary = string.Empty;
    }

    private void OnDeposit()
    {
        depositNotice = Plugin.Executor.Start(RunMode.Deposit, expectedDeposits) ?? string.Empty;
        Later(RefreshDeposit);
    }

    private void OnDepositRowClicked(DepositRow? row)
    {
        if (row == null || Plugin.Executor.IsRunning)
            return;

        if (!Config.KeepWithMe.Remove(row.ItemId))
            Config.KeepWithMe.Add(row.ItemId);

        Config.Save();
        Later(() =>
        {
            // The click was a toggle, not a selection, so the list's own highlight is dropped.
            depositList?.ClearSelection();
            RefreshDeposit();
        });
    }

    private void RefreshDeposit()
    {
        if (!built || depositSummary == null)
            return;

        nextForecast = Environment.TickCount64 + ForecastIntervalMs;

        var executor = Plugin.Executor;
        var running = executor.IsRunning;
        depositStopButton!.IsEnabled = running;
        if (running)
        {
            depositButton!.IsEnabled = false;
            ShowDepositProgress();
            return;
        }

        var world = Plugin.Reader.Read();
        var signature = HashCode.Combine(Signature(world), string.Join(',', Config.KeepWithMe));
        if (signature != shownDepositSignature || shownDepositSummary.Length == 0)
        {
            shownDepositSignature = signature;
            var rows = new List<DepositRow>();
            shownDepositSummary = BuildDepositPreview(world, rows, out shownCanDeposit);
            if (!rows.SequenceEqual(shownDepositRows))
            {
                shownDepositRows = rows;
                depositList!.OptionsList = rows;
                depositList.Update();
            }
        }

        depositButton!.IsEnabled = shownCanDeposit;
        depositSummary.String = shownDepositSummary;
        depositStatus!.String = depositNotice.Length > 0 ? depositNotice : executor.LastResult;
    }

    private string BuildDepositPreview(WorldState world, List<DepositRow> rows, out bool canDeposit)
    {
        canDeposit = false;
        expectedDeposits = 0;

        if (world.Retainers.Count == 0)
            return "Your retainers are not loaded yet. Open the retainer list at a summoning bell.";

        var profile = Profile;
        var planner = new Planner(Plugin.Catalog, profile);
        if (planner.Build(world).Unscanned.Count > 0)
            return "Some retainers have not been scanned yet. Press Scan retainers on the Sort tab first.";

        var forecast = Depositor.Forecast(world, Plugin.Catalog, profile, Executor.HeldBack(planner), Config.KeepWithMe.ToHashSet());
        var lines = forecast.Lines
            .Select(line => (Line: line, Name: Plugin.Catalog.Get(line.ItemId).Name))
            .OrderBy(entry => entry.Line.Kept)
            .ThenBy(entry => entry.Line.Retainer)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        var kinds = 0;
        foreach (var (line, name) in lines)
        {
            var retainer = world.Retainers[line.Retainer].Name;
            var left = $"{name}{(line.Hq ? HighQualityMark : string.Empty)} x{line.Quantity}";
            if (line.Kept)
            {
                rows.Add(new DepositRow(line.ItemId, left, "kept with you", RowTone.Dim));
            }
            else if (line.Left >= line.Quantity)
            {
                rows.Add(new DepositRow(line.ItemId, left, $"no room with {retainer}", RowTone.Warning));
            }
            else
            {
                kinds++;
                var right = line.Left == 0 ? $"to {retainer}" : $"to {retainer}; {line.Left} will not fit";
                rows.Add(new DepositRow(line.ItemId, left, right, RowTone.Normal));
            }
        }

        if (forecast.Moves == 0)
        {
            return rows.Any(row => row.Tone == RowTone.Warning)
                ? "Nothing fits: the retainers that hold these items are full."
                : "Nothing in your inventory matches what your retainers already hold.";
        }

        expectedDeposits = forecast.Moves;
        canDeposit = true;
        return $"{kinds} {(kinds == 1 ? "kind of item" : "kinds of item")} to deposit, over {forecast.Visits} retainer {(forecast.Visits == 1 ? "visit" : "visits")}.";
    }

    private void ShowDepositProgress()
    {
        if (depositSummary == null)
            return;

        var executor = Plugin.Executor;
        depositSummary.String = executor.Status;
        if (executor.Mode != RunMode.Deposit)
        {
            depositStatus!.String = "A scan or sort is running; see the Sort tab.";
            return;
        }

        var expected = Math.Max(executor.MovesExpected, executor.MovesDone);
        depositStatus!.String = $"{executor.MovesDone} of about {expected} deposits, {executor.VisitsDone} retainer visits.";
    }
}
