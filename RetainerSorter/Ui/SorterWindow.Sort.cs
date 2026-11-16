using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KamiToolKit.Nodes;
using RetainerSorter.Game;
using RetainerSorter.Sorting;

namespace RetainerSorter.Ui;

/// <summary> The Sort tab: what a sort would do, and the buttons to scan, run and stop it. </summary>
internal sealed unsafe partial class SorterWindow
{
    // Rough time costs used for the estimate: the game answering a move, and one retainer's
    // greeting, menu, inventory and farewell.
    private const int MoveOverheadMs = 300;
    private const int VisitSeconds = 8;

    private TextNode? sortProfileText;
    private TextNode? summaryText;
    private TextNode? progressText;
    private ListNode<PreviewRow, PreviewRowNode>? previewList;
    private ProgressBarNode? progressBar;
    private TextButtonNode? scanButton;
    private TextButtonNode? executeButton;
    private TextButtonNode? stopButton;

    private List<PreviewRow> shownRows = [];
    private string shownSummary = string.Empty;
    private bool shownCanExecute;
    private int shownSignature;
    private string notice = string.Empty;

    private void BuildSortTab(ResNode panel, Vector2 size)
    {
        sortProfileText = Label(panel, new Vector2(0.0f, 0.0f), new Vector2(size.X, 20.0f), string.Empty, RowTone.Dim);
        summaryText = Paragraph(panel, new Vector2(0.0f, 22.0f), new Vector2(size.X, 56.0f));

        previewList = new ListNode<PreviewRow, PreviewRowNode>
        {
            Position = new Vector2(0.0f, 84.0f),
            Size = new Vector2(size.X, size.Y - 84.0f - 88.0f),
            ItemSpacing = 1.0f,
            AutoResetScroll = false,
            OptionsList = [],
        };
        previewList.NoResultsTextNode.TextId = 0;
        previewList.NoResultsTextNode.String = "Nothing to show yet.";
        previewList.AttachNode(panel);

        progressBar = new ProgressBarNode
        {
            Position = new Vector2(0.0f, size.Y - 80.0f),
            Size = new Vector2(size.X, 16.0f),
        };
        progressBar.AttachNode(panel);

        progressText = Label(panel, new Vector2(0.0f, size.Y - 60.0f), new Vector2(size.X, 22.0f), string.Empty);
        progressText.TextFlags = FFXIVClientStructs.FFXIV.Component.GUI.TextFlags.Ellipsis;

        var buttonY = size.Y - 30.0f;
        scanButton = Button(panel, new Vector2(0.0f, buttonY), 150.0f, "Scan retainers", OnScan,
            "Opens each retainer once to read its inventory. Nothing is moved.");
        executeButton = Button(panel, new Vector2(158.0f, buttonY), 150.0f, "Execute", OnExecute,
            "Carries out the sort shown above. You can stop it at any moment.");
        stopButton = Button(panel, new Vector2(316.0f, buttonY), 110.0f, "Stop", OnStop,
            "Finishes the move in progress, then stops. Execute picks up where it left off.");
    }

    private void ForgetSortNodes()
    {
        sortProfileText = null;
        summaryText = null;
        progressText = null;
        previewList = null;
        progressBar = null;
        scanButton = null;
        executeButton = null;
        stopButton = null;
        shownRows = [];
        shownSummary = string.Empty;
    }

    private void OnScan()
    {
        notice = Plugin.Executor.Start(RunMode.Scan) ?? string.Empty;
        Later(RefreshSort);
    }

    private void OnExecute()
    {
        notice = Plugin.Executor.Start(RunMode.Sort, expectedMoves) ?? string.Empty;
        Later(RefreshSort);
    }

    private void OnStop() => Plugin.Executor.RequestStop();

    private void RefreshSort()
    {
        if (!built || summaryText == null)
            return;

        nextForecast = Environment.TickCount64 + ForecastIntervalMs;

        var executor = Plugin.Executor;
        var running = executor.IsRunning;
        scanButton!.IsEnabled = !running;
        stopButton!.IsEnabled = running;
        sortProfileText!.String = $"Profile: {Profile.Name}";

        if (running)
        {
            executeButton!.IsEnabled = false;
            ShowProgress();
            return;
        }

        // Playing the whole sort through is only worth doing again when something has changed.
        var world = Plugin.Reader.Read();
        var signature = Signature(world);
        if (signature != shownSignature || shownSummary.Length == 0)
        {
            shownSignature = signature;
            var rows = new List<PreviewRow>();
            shownSummary = BuildPreview(world, rows, out shownCanExecute);
            SetRows(rows);
        }

        executeButton!.IsEnabled = shownCanExecute;
        summaryText.String = shownSummary;
        progressBar!.Progress = 0.0f;
        progressText!.String = notice.Length > 0 ? notice : executor.LastResult;
    }

    /// <summary> A fingerprint of everything the preview depends on. </summary>
    private static int Signature(WorldState world)
    {
        var hash = new HashCode();
        hash.Add(Config.ActiveProfile);
        hash.Add(Newtonsoft.Json.JsonConvert.SerializeObject(Profile));
        hash.Add(Config.DelayMinMs);
        hash.Add(Config.DelayMaxMs);

        foreach (var retainer in world.Retainers)
        {
            hash.Add(retainer.Id);
            hash.Add(retainer.Name);
            hash.Add(retainer.Known);
            hash.Add(retainer.Unavailable);
            foreach (var slot in retainer.Slots)
            {
                hash.Add(slot.ItemId);
                hash.Add(slot.Quantity);
                hash.Add(slot.Hq);
            }
        }

        for (var i = 0; i < world.Player.Slots.Length; i++)
        {
            hash.Add(world.Player.Slots[i].ItemId);
            hash.Add(world.Player.Slots[i].Quantity);
            hash.Add(world.Player.Transit[i]);
        }

        return hash.ToHashCode();
    }

    /// <summary> Works out what a sort would do right now, from the last known contents of each retainer. </summary>
    private string BuildPreview(WorldState world, List<PreviewRow> rows, out bool canExecute)
    {
        canExecute = false;
        expectedMoves = 0;

        if (world.Retainers.Count == 0)
            return "Your retainers are not loaded yet. Open the retainer list at a summoning bell.";

        var profile = Profile;
        var plan = new Planner(Plugin.Catalog, profile).Build(world);
        var complete = plan.Unscanned.Count == 0;
        var forecast = complete ? Simulator.Run(world, plan, Plugin.Catalog) : null;

        for (var r = 0; r < world.Retainers.Count; r++)
        {
            var retainer = world.Retainers[r];
            string right;
            if (retainer.Unavailable)
                right = "not available";
            else if (profile.LockedRetainers.Contains(r))
                right = "never opened";
            else if (!retainer.Known)
                right = "not scanned yet";
            else if (forecast == null)
                right = $"{retainer.UsedSlots()} slots used";
            else
                right = $"{forecast.SlotsBefore[r]} slots now, {forecast.SlotsAfter[r]} after; {forecast.Leaving[r]} out, {forecast.Arriving[r]} in";

            rows.Add(new PreviewRow($"{r + 1}. {retainer.Name}", right, RowTone.Normal));

            if (forecast != null && plan.RulesAt[r].Count > 0)
            {
                var holds = plan.RulesAt[r].Select(index => index < 0 ? "unmatched items" : RuleSubject(profile.Rules[index]));
                rows.Add(new PreviewRow($"      holds: {string.Join(", ", holds.Distinct())}", string.Empty, RowTone.Dim));
            }
        }

        foreach (var warning in plan.Warnings.Distinct().Take(12))
            rows.Add(new PreviewRow(warning, string.Empty, RowTone.Warning));

        foreach (var note in plan.Notes.Distinct().Take(20))
            rows.Add(new PreviewRow(note, string.Empty, RowTone.Dim));

        if (!complete)
        {
            var names = string.Join(", ", plan.Unscanned.Select(r => world.Retainers[r].Name));
            return $"Not scanned yet: {names}. Press Scan retainers. It opens each one once to read its inventory and moves nothing.";
        }

        var free = world.Player.FreeSlots();
        var carried = world.Player.TransitCount();
        if (forecast!.Moves == 0 && carried == 0)
            return "Everything is already where your rules put it.";

        expectedMoves = forecast.Moves;
        canExecute = true;

        var averageDelay = (Config.DelayMinMs + Config.DelayMaxMs) / 2 + MoveOverheadMs;
        var seconds = (long)forecast.Moves * averageDelay / 1000 + (long)forecast.Visits * VisitSeconds;
        var summary = $"Ready: {forecast.Moves} moves over {forecast.Visits} retainer visits, about {Duration(seconds)}. "
            + $"{free} free inventory slots will be used for carrying.";

        if (carried > 0)
            summary += $" {carried} items from an earlier run are still in your inventory and get delivered first.";

        if (free <= Router.ReservedPlayerSlots)
            summary += " Free up at least 2 inventory slots before starting.";
        else if (forecast.Stuck)
            summary += " There is not enough free inventory space to finish everything in one run.";
        else if (free < 10)
            summary += " More free slots would make this much faster.";

        return summary;
    }

    private void ShowProgress()
    {
        if (summaryText == null)
            return;

        var executor = Plugin.Executor;
        summaryText.String = executor.Status;
        if (executor.Mode == RunMode.Scan)
        {
            progressBar!.Progress = 0.0f;
            progressText!.String = $"Scanning: {executor.VisitsDone} retainers read.";
            return;
        }

        var expected = Math.Max(executor.MovesExpected, executor.MovesDone);
        progressBar!.Progress = expected == 0 ? 0.0f : Math.Clamp((float)executor.MovesDone / expected, 0.0f, 1.0f);
        progressText!.String = $"{executor.MovesDone} of about {expected} moves, {executor.VisitsDone} retainer visits.";
    }

    private void SetRows(List<PreviewRow> rows)
    {
        if (previewList == null || rows.SequenceEqual(shownRows))
            return;

        shownRows = rows;
        previewList.OptionsList = rows;
        previewList.Update();
    }

    private static string Duration(long seconds)
    {
        if (seconds < 90)
            return $"{Math.Max(seconds, 5)} seconds";

        var minutes = (seconds + 30) / 60;
        return minutes < 90 ? $"{minutes} minutes" : $"{minutes / 60} h {minutes % 60} min";
    }
}
