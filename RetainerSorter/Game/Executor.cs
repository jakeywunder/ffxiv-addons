using System;
using System.Collections.Generic;
using System.Linq;
using RetainerSorter.Data;
using RetainerSorter.Sorting;

namespace RetainerSorter.Game;

internal enum RunMode
{
    None,

    /// <summary> Open each retainer once to read its inventory. Nothing is moved. </summary>
    Scan,
    Sort,

    /// <summary> Put bag items on the retainer that already holds that item. Nothing is taken out. </summary>
    Deposit,
}

/// <summary>
/// Carries out a scan, a sort or a deposit, one small step per frame. Every item move is decided from what
/// is actually in the inventories at that moment, so stopping, resuming and outside changes are
/// all handled the same way: look again, then do the next thing.
/// </summary>
internal sealed unsafe class Executor(ItemCatalog catalog, RetainerCache cache, TransitStore transit, WorldReader reader, AutoRetainerIpc autoRetainer)
{
    private enum Phase
    {
        AtList,
        Handoff,
        WaitMenu,
        WaitInventory,
        Working,
        WaitMove,
        Closing,
        WaitQuitMenu,
        WaitList,
    }

    private const long UiPauseMs = 600;
    private const long TalkPauseMs = 400;
    private const long StepTimeoutMs = 15000;
    private const long MoveTimeoutMs = 6000;
    private const int MoveAttempts = 2;

    private readonly Random random = new();
    private readonly HashSet<int> blocked = [];
    private readonly HashSet<uint> excluded = [];
    private readonly HashSet<(ulong Retainer, uint Complete)> offeredVentures = [];
    private readonly Queue<ulong> scanQueue = new();

    // A deposit only handles what was in the bag when it was started, and opens each retainer once.
    private readonly Dictionary<int, uint> depositSlots = [];
    private readonly HashSet<int> depositVisited = [];

    private Phase phase;
    private Planner? planner;
    private long waitUntil;
    private long deadline;
    private long settleAt;
    private bool stopRequested;

    private int current = -1;
    private ulong currentId;
    private string currentName = string.Empty;
    private int movesThisVisit;

    private Move pendingMove;
    private SlotItem pendingFromBefore;
    private SlotItem pendingToBefore;
    private uint failedItem;
    private int failedAttempts;

    private bool autoRetainerWasSuppressed;
    private bool autoRetainerHeld;
    private bool handoffSawBusy;
    private long handoffIdleSince;
    private long handoffGiveUpAt;
    private bool listLostAfterHandoff;
    private int closeAttempts;

    public RunMode Mode { get; private set; }
    public bool IsRunning => Mode != RunMode.None;
    public string Status { get; private set; } = string.Empty;
    public string LastResult { get; private set; } = string.Empty;
    public int MovesDone { get; private set; }
    public int VisitsDone { get; private set; }

    /// <summary> How many moves the forecast expected when the sort started, for the progress bar. </summary>
    public int MovesExpected { get; private set; }


    /// <summary> Starts a run. Returns a message for the player if it cannot start. </summary>
    public string? Start(RunMode mode, int movesExpected = 0)
    {
        if (IsRunning)
            return "Already running.";

        if (GameUi.Ready(GameUi.RetainerList) == null)
            return "Open the retainer list at a summoning bell first.";

        var profile = Plugin.Config.Active;
        var world = reader.Read();
        excluded.Clear();
        if (mode == RunMode.Sort)
        {
            planner = new Planner(catalog, profile, excluded);
            var plan = planner.Build(world);
            if (plan.Unscanned.Count > 0)
                return "Some retainers have not been scanned yet. Press Scan first.";

            if (world.Player.FreeSlots() <= Router.ReservedPlayerSlots)
                return "Free up at least 2 inventory slots first. The more free slots, the faster the sort.";
        }
        else if (mode == RunMode.Deposit)
        {
            planner = new Planner(catalog, profile);
            if (planner.Build(world).Unscanned.Count > 0)
                return "Some retainers have not been scanned yet. Press Scan retainers on the Sort tab first.";

            depositSlots.Clear();
            depositVisited.Clear();
            var targets = DepositTargets(world, false);
            for (var p = 0; p < targets.Length; p++)
            {
                if (targets[p] >= 0)
                    depositSlots[p] = world.Player.Slots[p].ItemId;
            }

            if (depositSlots.Count == 0)
                return "Nothing in your inventory matches what your retainers already hold.";
        }
        else
        {
            scanQueue.Clear();
            var wanted = world.Retainers.Where(r => !r.Unavailable && !profile.LockedRetainers.Contains(r.Position)).ToList();
            var stale = wanted.Where(r => !cache.IsFresh(r.Id)).ToList();
            foreach (var retainer in stale.Count > 0 ? stale : wanted)
                scanQueue.Enqueue(retainer.Id);

            if (scanQueue.Count == 0)
                return "There are no retainers to scan.";
        }

        Mode = mode;
        MovesDone = 0;
        VisitsDone = 0;
        MovesExpected = movesExpected;
        LastResult = string.Empty;
        stopRequested = false;
        current = -1;
        blocked.Clear();
        failedItem = 0;
        failedAttempts = 0;

        autoRetainerHeld = autoRetainer.Available;
        if (autoRetainerHeld)
        {
            autoRetainerWasSuppressed = autoRetainer.Suppressed;
            autoRetainer.Suppressed = true;
        }

        Enter(Phase.AtList, 0);
        Status = "Starting.";
        return null;
    }

    public void RequestStop()
    {
        if (IsRunning)
            stopRequested = true;
    }

    /// <summary>
    /// Which items a deposit leaves in the bag: ones the player keeps with them, and ones a rule
    /// says to leave wherever they are. The preview uses the same test.
    /// </summary>
    public static Func<uint, bool> HeldBack(Planner planner, IReadOnlySet<uint>? refused = null)
    {
        var keep = Plugin.Config.KeepWithMe.ToHashSet();
        return itemId => keep.Contains(itemId) || refused?.Contains(itemId) == true || planner.KeepsInPlace(itemId);
    }

    private int[] DepositTargets(WorldState world, bool onlyWhatWasThereAtStart)
    {
        var targets = Depositor.Targets(world, catalog, Plugin.Config.Active, HeldBack(planner!, excluded));
        if (!onlyWhatWasThereAtStart)
            return targets;

        // Anything that arrived in the bag since Deposit was pressed was not in the preview, so it stays.
        for (var p = 0; p < targets.Length; p++)
        {
            if (targets[p] >= 0 && (!depositSlots.TryGetValue(p, out var itemId) || itemId != world.Player.Slots[p].ItemId))
                targets[p] = Depositor.None;
        }

        return targets;
    }

    public void Tick()
    {
        if (!IsRunning)
            return;

        try
        {
            // A move that was already sent is allowed to land before stopping.
            if (stopRequested && phase != Phase.WaitMove)
            {
                Finish(StoppedMessage());
                return;
            }

            var now = Environment.TickCount64;
            if (now < waitUntil)
                return;

            switch (phase)
            {
                case Phase.AtList: AtList(now); break;
                case Phase.Handoff: Handoff(now); break;
                case Phase.WaitMenu: WaitMenu(now); break;
                case Phase.WaitInventory: WaitInventory(now); break;
                case Phase.Working: Working(now); break;
                case Phase.WaitMove: WaitMove(now); break;
                case Phase.Closing: Closing(now); break;
                case Phase.WaitQuitMenu: WaitQuitMenu(now); break;
                case Phase.WaitList: WaitList(now); break;
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "The retainer sorter stopped after an error.");
            Finish("Stopped after an unexpected error. Details are in the Dalamud log (/xllog).");
        }
    }

    private void Enter(Phase next, long pauseMs, long timeoutMs = StepTimeoutMs)
    {
        var now = Environment.TickCount64;
        phase = next;
        waitUntil = now + pauseMs;
        deadline = now + pauseMs + timeoutMs;
        settleAt = 0;

        // The step-by-step trail in the Dalamud log (/xllog) is what makes a stuck run diagnosable.
        if (next != Phase.Working && next != Phase.WaitMove)
            Plugin.Log.Debug($"[{Mode}] {next}{(currentName.Length > 0 ? $" ({currentName})" : string.Empty)}");
    }

    private void AtList(long now)
    {
        var list = GameUi.Ready(GameUi.RetainerList);
        if (list == null)
        {
            if (ClickTalkIfShown(now))
                return;

            if (now > deadline)
            {
                Finish(listLostAfterHandoff
                    ? "Stopped: the retainer list closed after AutoRetainer finished. Open it again and press Execute to continue, or untick \"Step aside for AutoRetainer\" in Options."
                    : "Stopped: the retainer list is no longer open.");
            }

            return;
        }

        listLostAfterHandoff = false;

        // Never act while AutoRetainer is in the middle of something.
        if (autoRetainer.IsBusy)
        {
            Status = "Paused: AutoRetainer is working.";
            Enter(Phase.AtList, 500);
            return;
        }

        // A deposit is over in a few visits, so it does not stop to let ventures be collected.
        if (Mode != RunMode.Deposit && Plugin.Config.LetAutoRetainerWork && autoRetainerHeld && HasNewFinishedVenture())
        {
            autoRetainer.Suppressed = false;
            handoffSawBusy = false;
            handoffIdleSince = 0;
            handoffGiveUpAt = now + 5000;
            Status = "Letting AutoRetainer collect finished ventures.";
            Enter(Phase.Handoff, 500);
            return;
        }

        var world = reader.Read();
        int next;
        if (Mode == RunMode.Scan)
        {
            next = -1;
            while (scanQueue.Count > 0 && next < 0)
            {
                var id = scanQueue.Dequeue();
                next = world.Retainers.FindIndex(r => r.Id == id);
            }

            if (next < 0)
            {
                Finish("Scan complete.");
                return;
            }
        }
        else if (Mode == RunMode.Deposit)
        {
            planner = new Planner(catalog, Plugin.Config.Active);
            var targets = DepositTargets(world, true);
            next = Depositor.NextRetainer(world, targets, catalog, depositVisited);
            if (next < 0)
            {
                Finish(DepositDoneMessage(world, targets));
                return;
            }

            depositVisited.Add(next);
        }
        else
        {
            // A fresh planner per visit, so rules edited while the sort runs are picked up.
            planner = new Planner(catalog, Plugin.Config.Active, excluded);
            var plan = planner.Build(world);
            next = Router.NextRetainer(world, plan, catalog, current, blocked);
            if (next < 0)
            {
                Finish(DoneMessage(world, plan));
                return;
            }
        }

        var retainer = world.Retainers[next];
        var row = GameUi.FindRetainerRow(list, retainer.Name, next);
        if (row < 0)
        {
            Finish($"Stopped: {retainer.Name} could not be chosen in the retainer list.");
            return;
        }

        current = next;
        currentId = retainer.Id;
        currentName = retainer.Name;
        movesThisVisit = 0;
        Status = $"Opening {currentName}.";
        GameUi.SelectRetainer(list, row);
        Enter(Phase.WaitMenu, UiPauseMs);
    }

    private void Handoff(long now)
    {
        if (autoRetainer.IsBusy)
        {
            handoffSawBusy = true;
            handoffIdleSince = 0;
            Status = "Paused: AutoRetainer is working.";
            waitUntil = now + 500;
            return;
        }

        if (handoffSawBusy)
        {
            // Give it a moment to be sure it is really done, not just between two retainers.
            if (handoffIdleSince == 0)
                handoffIdleSince = now;

            if (now - handoffIdleSince < 3000)
            {
                waitUntil = now + 250;
                return;
            }
        }
        else if (now < handoffGiveUpAt)
        {
            waitUntil = now + 250;
            return;
        }

        autoRetainer.Suppressed = true;
        foreach (var entry in Inventories.Retainers())
        {
            if (entry.VentureId != 0 && entry.VentureComplete != 0)
                offeredVentures.Add((entry.Id, entry.VentureComplete));
        }

        // Venture rewards went into the bag, so the picture has changed. Start the tour afresh.
        current = -1;
        blocked.Clear();
        listLostAfterHandoff = handoffSawBusy;
        Enter(Phase.AtList, UiPauseMs);
    }

    private bool HasNewFinishedVenture()
    {
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var entry in Inventories.Retainers())
        {
            if (entry.Available && entry.VentureId != 0 && entry.VentureComplete != 0 && entry.VentureComplete <= nowSeconds
                && !offeredVentures.Contains((entry.Id, entry.VentureComplete)))
                return true;
        }

        return false;
    }

    private void WaitMenu(long now)
    {
        if (ClickTalkIfShown(now))
            return;

        var menu = GameUi.Ready(GameUi.SelectString);
        if (menu != null)
        {
            var index = GameUi.FindEntrustEntry(GameUi.MenuEntries(menu));
            if (index < 0)
            {
                Finish($"Stopped: {currentName}'s menu was not the one expected.");
                return;
            }

            GameUi.SelectMenuEntry(menu, index);
            Enter(Phase.WaitInventory, UiPauseMs);
            return;
        }

        if (now > deadline)
            Finish($"Stopped: {currentName}'s menu did not open.");
    }

    private void WaitInventory(long now)
    {
        if (ClickTalkIfShown(now))
            return;

        if (GameUi.RetainerInventory() != null && Inventories.RetainerLoaded())
        {
            // Make sure the game opened the retainer that was asked for before touching anything.
            if (GameUi.ActiveRetainerId() != currentId)
            {
                Finish("Stopped: a different retainer opened than the one expected. Nothing was moved from it.");
                return;
            }

            if (settleAt == 0)
            {
                settleAt = now + 500;
                return;
            }

            if (now < settleAt)
                return;

            cache.Set(currentId, Inventories.ReadRetainer());
            VisitsDone++;
            if (Mode == RunMode.Scan)
            {
                Status = $"Read {currentName}.";
                Enter(Phase.Closing, UiPauseMs);
            }
            else
            {
                Status = Mode == RunMode.Deposit ? $"Depositing with {currentName}." : $"Sorting {currentName}.";
                Enter(Phase.Working, 0);
            }

            return;
        }

        if (now > deadline)
            Finish($"Stopped: {currentName}'s inventory did not open.");
    }

    private void Working(long now)
    {
        if (WorldReader.OpenRetainerId() != currentId)
        {
            Finish($"Stopped: {currentName}'s inventory was closed.");
            return;
        }

        var world = reader.Read();
        var chosen = Mode == RunMode.Deposit
            ? Depositor.NextMove(world, DepositTargets(world, true), catalog, current)
            : Router.NextMove(world, planner!.Build(world), catalog, current);

        if (chosen is not { } move)
        {
            if (movesThisVisit == 0)
                blocked.Add(current);
            else
                blocked.Clear();

            transit.Save(world.Player.Slots, true);
            Enter(Phase.Closing, UiPauseMs);
            return;
        }

        pendingMove = move;
        pendingFromBefore = Inventories.ReadSlot(move.Kind != MoveKind.Deposit, move.FromSlot);
        pendingToBefore = Inventories.ReadSlot(move.Kind != MoveKind.Withdraw, move.ToSlot);

        // The bag slot is marked before the item arrives, so it is never mistaken for the player's own.
        if (move.Kind == MoveKind.Withdraw)
            transit.Mark(move.ToSlot, true);

        Inventories.Execute(move);
        Enter(Phase.WaitMove, 0, MoveTimeoutMs);
    }

    private void WaitMove(long now)
    {
        var from = Inventories.ReadSlot(pendingMove.Kind != MoveKind.Deposit, pendingMove.FromSlot);
        var to = Inventories.ReadSlot(pendingMove.Kind != MoveKind.Withdraw, pendingMove.ToSlot);
        var landed = !Same(from, pendingFromBefore) || !Same(to, pendingToBefore);

        if (landed)
        {
            MovesDone++;
            movesThisVisit++;
            failedItem = 0;
            failedAttempts = 0;
            transit.Save(Inventories.ReadPlayer(), false);
            Enter(Phase.Working, random.Next(Plugin.Config.DelayMinMs, Plugin.Config.DelayMaxMs + 1));
            return;
        }

        if (now <= deadline)
            return;

        // The game did not carry out the move. Try once more, then leave that item alone.
        var item = pendingFromBefore.ItemId;
        failedAttempts = item == failedItem ? failedAttempts + 1 : 1;
        failedItem = item;
        if (failedAttempts >= MoveAttempts && item != 0)
        {
            excluded.Add(item);
            Plugin.Log.Warning($"{catalog.Get(item).Name} would not move, so it is being left where it is.");
            failedItem = 0;
            failedAttempts = 0;
        }

        Enter(Phase.Working, 1000);
    }

    private void Closing(long now)
    {
        cache.Save();
        closeAttempts = 1;
        GameUi.CloseRetainerInventory(false);
        Enter(Phase.WaitQuitMenu, UiPauseMs);
    }

    private void WaitQuitMenu(long now)
    {
        if (ClickTalkIfShown(now))
            return;

        if (GameUi.RetainerInventoryVisible())
        {
            if (now > deadline)
            {
                Finish($"Stopped: {currentName}'s inventory would not close.");
                return;
            }

            // Ask again every couple of seconds until it goes away, more bluntly after the first try.
            closeAttempts++;
            GameUi.CloseRetainerInventory(closeAttempts > 2);
            waitUntil = now + 2000;
            return;
        }

        var menu = GameUi.Ready(GameUi.SelectString);
        if (menu != null)
        {
            GameUi.SelectMenuEntry(menu, GameUi.FindQuitEntry(GameUi.MenuEntries(menu)));
            Enter(Phase.WaitList, UiPauseMs);
            return;
        }

        if (GameUi.Ready(GameUi.RetainerList) != null)
        {
            Enter(Phase.AtList, UiPauseMs);
            return;
        }

        if (now > deadline)
            Finish($"Stopped: could not leave {currentName}.");
    }

    private void WaitList(long now)
    {
        if (ClickTalkIfShown(now))
            return;

        if (GameUi.Ready(GameUi.RetainerList) != null && !GameUi.IsVisible(GameUi.SelectString))
        {
            Enter(Phase.AtList, UiPauseMs);
            return;
        }

        if (now > deadline)
            Finish("Stopped: the retainer list did not come back.");
    }

    private bool ClickTalkIfShown(long now)
    {
        var talk = GameUi.Ready(GameUi.Talk);
        if (talk == null)
            return false;

        GameUi.ClickTalk(talk);
        waitUntil = now + TalkPauseMs;
        return true;
    }

    private string StoppedMessage()
    {
        if (Mode == RunMode.Scan)
            return "Scan stopped.";

        if (Mode == RunMode.Deposit)
            return $"Deposit stopped after {MovesDone} moves. Everything not yet deposited is still in your inventory.";

        var carrying = reader.Read().Player.TransitCount();
        return carrying > 0
            ? $"Stopped after {MovesDone} moves. {carrying} items being carried are in your inventory; press Execute to finish delivering them."
            : $"Stopped after {MovesDone} moves. Press Execute to continue.";
    }

    private string DepositDoneMessage(WorldState world, int[] targets)
    {
        var message = MovesDone == 0
            ? "Nothing could be deposited."
            : $"Deposited: {MovesDone} moves over {VisitsDone} retainer visits.";

        var left = new HashSet<uint>();
        for (var p = 0; p < targets.Length; p++)
        {
            if (targets[p] >= 0 && !world.Player.Slots[p].IsEmpty)
                left.Add(world.Player.Slots[p].ItemId);
        }

        if (left.Count > 0)
            message += $" No room on their retainer for: {string.Join(", ", left.Take(8).Select(id => catalog.Get(id).Name))}{(left.Count > 8 ? $" and {left.Count - 8} more" : string.Empty)}.";

        if (excluded.Count > 0)
            message += $" The game would not move: {string.Join(", ", excluded.Select(id => catalog.Get(id).Name))}.";

        return message;
    }

    private string DoneMessage(WorldState world, SortPlan plan)
    {
        var message = MovesDone == 0
            ? "Everything is already in place."
            : $"Done: {MovesDone} moves over {VisitsDone} retainer visits.";

        var carrying = world.Player.TransitCount();
        if (carrying > 0)
            message += $" {carrying} items could not be delivered and are in your inventory.";

        var remaining = 0;
        for (var r = 0; r < world.Retainers.Count; r++)
        {
            if (plan.Usable[r])
                remaining += Router.Leaving(world.Retainers[r], plan.RetainerTargets[r], r);
        }

        if (remaining > 0)
            message += $" {remaining} stacks could not be moved; free up inventory space and run it again.";

        if (excluded.Count > 0)
            message += $" Left alone because the game would not move them: {string.Join(", ", excluded.Select(id => catalog.Get(id).Name))}.";

        return message;
    }

    private void Finish(string message, bool updateComments = true)
    {
        if (autoRetainerHeld)
        {
            autoRetainer.Suppressed = autoRetainerWasSuppressed;
            autoRetainerHeld = false;
        }

        try
        {
            transit.Save(Inventories.ReadPlayer(), true);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Could not save which items are being carried.");
        }

        cache.Save();
        Mode = RunMode.None;
        Status = string.Empty;
        LastResult = message;
        stopRequested = false;
        Plugin.Print(message);

        // Whatever just ran may have changed what the retainers hold.
        if (updateComments)
            RetainerNotes.Refresh();
    }

    /// <summary> Makes sure AutoRetainer is released if the plugin unloads mid-run. </summary>
    public void Dispose()
    {
        if (IsRunning)
            Finish("Stopped: the plugin was unloaded.", false);
    }

    private static bool Same(SlotItem a, SlotItem b)
        => a.ItemId == b.ItemId && a.Quantity == b.Quantity && a.Hq == b.Hq;
}
