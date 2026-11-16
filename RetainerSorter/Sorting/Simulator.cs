using System;
using System.Collections.Generic;

namespace RetainerSorter.Sorting;

public sealed class Forecast
{
    public int Moves;
    public int Visits;

    /// <summary> True when the run ends with work it could not do, usually because the bag is full. </summary>
    public bool Stuck;

    /// <summary> Items that would be left in the player's bag. </summary>
    public int Undelivered;

    public int[] Leaving = [];
    public int[] Arriving = [];
    public int[] SlotsBefore = [];
    public int[] SlotsAfter = [];
}

/// <summary>
/// Plays a whole sort on a copy of the snapshot, using the same rules for moving items as the
/// game: a stack dropped on a matching stack tops it up and keeps the remainder.
/// </summary>
public static class Simulator
{
    public static WorldState Clone(WorldState world)
    {
        var copy = new WorldState();
        foreach (var retainer in world.Retainers)
        {
            copy.Retainers.Add(new RetainerState
            {
                Position = retainer.Position,
                Id = retainer.Id,
                Name = retainer.Name,
                Known = retainer.Known,
                Unavailable = retainer.Unavailable,
                Slots = (SlotItem[])retainer.Slots.Clone(),
            });
        }

        copy.Player.Slots = (SlotItem[])world.Player.Slots.Clone();
        copy.Player.Transit = (bool[])world.Player.Transit.Clone();
        copy.Player.OwnedUniqueIds = [.. world.Player.OwnedUniqueIds];
        return copy;
    }

    /// <summary> Applies one move to the state and carries the plan's targets along with the items. </summary>
    public static void Apply(WorldState world, SortPlan plan, IItemCatalog catalog, int r, Move move)
    {
        var retainer = world.Retainers[r];
        var player = world.Player;
        var targets = plan.RetainerTargets[r];

        switch (move.Kind)
        {
            case MoveKind.Withdraw:
                player.Slots[move.ToSlot] = retainer.Slots[move.FromSlot];
                player.Transit[move.ToSlot] = true;
                plan.PlayerTargets[move.ToSlot] = targets[move.FromSlot];
                retainer.Slots[move.FromSlot] = default;
                targets[move.FromSlot] = SortPlan.NoItem;
                break;

            case MoveKind.Deposit:
                if (Pour(ref player.Slots[move.FromSlot], ref retainer.Slots[move.ToSlot], catalog))
                {
                    if (targets[move.ToSlot] == SortPlan.NoItem)
                        targets[move.ToSlot] = plan.PlayerTargets[move.FromSlot];

                    if (player.Slots[move.FromSlot].IsEmpty)
                    {
                        player.Transit[move.FromSlot] = false;
                        plan.PlayerTargets[move.FromSlot] = SortPlan.NoItem;
                    }
                }
                else
                {
                    // Different items: the game swaps them.
                    (player.Slots[move.FromSlot], retainer.Slots[move.ToSlot]) = (retainer.Slots[move.ToSlot], player.Slots[move.FromSlot]);
                    (plan.PlayerTargets[move.FromSlot], targets[move.ToSlot]) = (targets[move.ToSlot], plan.PlayerTargets[move.FromSlot]);
                }

                break;

            case MoveKind.Merge:
                if (Pour(ref retainer.Slots[move.FromSlot], ref retainer.Slots[move.ToSlot], catalog))
                {
                    if (targets[move.ToSlot] == SortPlan.NoItem)
                        targets[move.ToSlot] = targets[move.FromSlot];

                    if (retainer.Slots[move.FromSlot].IsEmpty)
                        targets[move.FromSlot] = SortPlan.NoItem;
                }

                break;
        }
    }

    /// <summary> Moves as much of <paramref name="from"/> into <paramref name="to"/> as fits. False if they cannot combine. </summary>
    internal static bool Pour(ref SlotItem from, ref SlotItem to, IItemCatalog catalog)
    {
        if (to.IsEmpty)
        {
            to = from;
            from = default;
            return true;
        }

        if (!to.SameStackAs(from))
            return false;

        var stackSize = catalog.Get(from.ItemId).StackSize;
        var amount = Math.Min(from.Quantity, stackSize - to.Quantity);
        if (amount <= 0)
            return false;

        to.Quantity += amount;
        from.Quantity -= amount;
        if (from.Quantity <= 0)
            from = default;

        return true;
    }

    /// <summary> Runs the whole sort on a copy and reports how much work it is. </summary>
    public static Forecast Run(WorldState source, SortPlan sourcePlan, IItemCatalog catalog, Action<WorldState>? inspectResult = null)
    {
        var world = Clone(source);
        var count = world.Retainers.Count;
        var plan = new SortPlan
        {
            RetainerTargets = Array.ConvertAll(sourcePlan.RetainerTargets, targets => (int[])targets.Clone()),
            PlayerTargets = (int[])sourcePlan.PlayerTargets.Clone(),
            Usable = sourcePlan.Usable,
        };

        var forecast = new Forecast
        {
            Leaving = new int[count],
            Arriving = new int[count],
            SlotsBefore = new int[count],
            SlotsAfter = new int[count],
        };

        for (var r = 0; r < count; r++)
        {
            forecast.SlotsBefore[r] = world.Retainers[r].UsedSlots();
            for (var s = 0; s < Layout.RetainerSlots; s++)
            {
                var target = plan.RetainerTargets[r][s];
                if (target >= 0 && target != r)
                {
                    forecast.Leaving[r]++;
                    forecast.Arriving[target]++;
                }
            }
        }

        for (var p = 0; p < Layout.PlayerSlots; p++)
        {
            if (plan.PlayerTargets[p] >= 0)
                forecast.Arriving[plan.PlayerTargets[p]]++;
        }

        var blocked = new HashSet<int>();
        var current = -1;
        const int moveLimit = 100_000;
        while (forecast.Moves < moveLimit)
        {
            var next = Router.NextRetainer(world, plan, catalog, current, blocked);
            if (next < 0)
                break;

            current = next;
            forecast.Visits++;
            var movesHere = 0;
            while (forecast.Moves < moveLimit && Router.NextMove(world, plan, catalog, current) is { } move)
            {
                Apply(world, plan, catalog, current, move);
                forecast.Moves++;
                movesHere++;
            }

            if (movesHere == 0)
            {
                blocked.Add(current);
                forecast.Visits--;
            }
            else
            {
                blocked.Clear();
            }
        }

        forecast.Undelivered = world.Player.TransitCount();
        for (var r = 0; r < count; r++)
        {
            forecast.SlotsAfter[r] = world.Retainers[r].UsedSlots();
            if (plan.Usable[r] && Router.Leaving(world.Retainers[r], plan.RetainerTargets[r], r) > 0)
                forecast.Stuck = true;
        }

        if (forecast.Undelivered > 0)
            forecast.Stuck = true;

        inspectResult?.Invoke(world);
        return forecast;
    }
}
