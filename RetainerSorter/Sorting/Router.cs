using System;
using System.Collections.Generic;

namespace RetainerSorter.Sorting;

/// <summary>
/// Turns a plan into single moves. Every call looks at the current state and returns the next
/// move only, so a run can be interrupted, resumed, or surprised by outside changes at any point.
/// </summary>
public static class Router
{
    /// <summary>
    /// One bag slot is always kept free. It is what lets a full retainer and a full bag trade
    /// items: take one out, put one in, repeat.
    /// </summary>
    public const int ReservedPlayerSlots = 1;

    /// <summary> The next move to make while the retainer at <paramref name="r"/> is open, or null when it is done. </summary>
    public static Move? NextMove(WorldState world, SortPlan plan, IItemCatalog catalog, int r)
    {
        var retainer = world.Retainers[r];
        var player = world.Player;
        var targets = plan.RetainerTargets[r];

        // 1. Deliveries that top up a stack already here. These need no free slot.
        var pendingDeliveries = false;
        for (var p = 0; p < player.Slots.Length; p++)
        {
            if (plan.PlayerTargets[p] != r || player.Slots[p].IsEmpty)
                continue;

            pendingDeliveries = true;
            ref readonly var item = ref player.Slots[p];
            if (item.Collectable)
                continue;

            var stackSize = catalog.Get(item.ItemId).StackSize;
            if (stackSize <= 1)
                continue;

            for (var s = 0; s < retainer.Slots.Length; s++)
            {
                ref readonly var other = ref retainer.Slots[s];
                if (!other.IsEmpty && other.Quantity < stackSize && other.SameStackAs(item) && (targets[s] == r || targets[s] == SortPlan.Stay))
                    return new Move(MoveKind.Deposit, p, s);
            }
        }

        // 2. Deliveries into an empty slot.
        var firstEmpty = Array.FindIndex(retainer.Slots, slot => slot.IsEmpty);
        if (pendingDeliveries && firstEmpty >= 0)
        {
            for (var p = 0; p < player.Slots.Length; p++)
            {
                if (plan.PlayerTargets[p] == r && !player.Slots[p].IsEmpty)
                    return new Move(MoveKind.Deposit, p, firstEmpty);
            }
        }

        // 3. Combine partial stacks here. Doing this before taking anything out means fewer trips.
        if (FindMerge(retainer, targets, catalog) is { } merge)
            return merge;

        // 4. Take out what belongs elsewhere.
        var freePlayerSlot = Array.FindIndex(player.Slots, slot => slot.IsEmpty);
        if (freePlayerSlot < 0)
            return null;

        var free = player.FreeSlots();
        var mayUseReserve = pendingDeliveries && firstEmpty < 0;
        if (free <= ReservedPlayerSlots && !mayUseReserve)
            return null;

        var count = world.Retainers.Count;
        var load = new int[count];
        for (var p = 0; p < player.Slots.Length; p++)
        {
            var target = plan.PlayerTargets[p];
            if (target >= 0 && !player.Slots[p].IsEmpty)
                load[target]++;
        }

        var leaving = false;
        for (var s = 0; s < targets.Length; s++)
        {
            var target = targets[s];
            if (target >= 0 && target != r && !retainer.Slots[s].IsEmpty)
            {
                load[target]++;
                leaving = true;
            }
        }

        if (!leaving)
            return null;

        // Fill the bag for one destination at a time, biggest batch first, so the next stop
        // empties as much of it as possible.
        var bestSlot = -1;
        var bestLoad = -1;
        var bestDistance = int.MaxValue;
        for (var s = 0; s < targets.Length; s++)
        {
            var target = targets[s];
            if (target < 0 || target == r || retainer.Slots[s].IsEmpty)
                continue;

            // The bag cannot hold two copies of a unique item; the second waits for the first to be delivered.
            var itemId = retainer.Slots[s].ItemId;
            if (catalog.Get(itemId).IsUnique && Array.Exists(player.Slots, slot => !slot.IsEmpty && slot.ItemId == itemId))
                continue;

            var distance = (target - r + count) % count;
            if (load[target] > bestLoad || (load[target] == bestLoad && distance < bestDistance))
            {
                bestSlot = s;
                bestLoad = load[target];
                bestDistance = distance;
            }
        }

        return bestSlot < 0 ? null : new Move(MoveKind.Withdraw, bestSlot, freePlayerSlot);
    }

    /// <summary> Two partial stacks of the same item in one retainer: the smaller is poured into the larger. </summary>
    public static Move? FindMerge(RetainerState retainer, int[] targets, IItemCatalog catalog)
    {
        var seen = new Dictionary<(uint ItemId, bool Hq, int Target), int>();
        for (var s = 0; s < retainer.Slots.Length; s++)
        {
            ref readonly var item = ref retainer.Slots[s];
            if (item.IsEmpty || item.Collectable || targets[s] == SortPlan.Stay || targets[s] == SortPlan.NoItem)
                continue;

            var stackSize = catalog.Get(item.ItemId).StackSize;
            if (stackSize <= 1 || item.Quantity >= stackSize)
                continue;

            var key = (item.ItemId, item.Hq, targets[s]);
            if (!seen.TryGetValue(key, out var first))
            {
                seen[key] = s;
                continue;
            }

            return retainer.Slots[first].Quantity >= item.Quantity
                ? new Move(MoveKind.Merge, s, first)
                : new Move(MoveKind.Merge, first, s);
        }

        return null;
    }

    /// <summary>
    /// Which retainer to open next, or -1 when nothing is left to do. Deliveries come first: the
    /// retainer waiting for the most items in the bag, then the one with the most to take out.
    /// </summary>
    public static int NextRetainer(WorldState world, SortPlan plan, IItemCatalog catalog, int current, ISet<int> blocked)
    {
        var count = world.Retainers.Count;
        var deliveries = new int[count];
        var player = world.Player;
        for (var p = 0; p < player.Slots.Length; p++)
        {
            var target = plan.PlayerTargets[p];
            if (target >= 0 && !player.Slots[p].IsEmpty)
                deliveries[target]++;
        }

        var best = -1;
        var bestDeliveries = 0;
        var bestLeaving = 0;
        var bestDistance = int.MaxValue;
        var anchor = Math.Max(current, 0);
        for (var r = 0; r < count; r++)
        {
            if (r == current || !plan.Usable[r] || blocked.Contains(r))
                continue;

            var leaving = Leaving(world.Retainers[r], plan.RetainerTargets[r], r);
            if (deliveries[r] == 0 && leaving == 0)
                continue;

            var distance = (r - anchor + count) % count;
            var better = deliveries[r] > bestDeliveries
                || (deliveries[r] == bestDeliveries && leaving > bestLeaving)
                || (deliveries[r] == bestDeliveries && leaving == bestLeaving && distance < bestDistance);
            if (best < 0 || better)
            {
                best = r;
                bestDeliveries = deliveries[r];
                bestLeaving = leaving;
                bestDistance = distance;
            }
        }

        if (best >= 0)
            return best;

        // Only tidying up inside single retainers is left.
        for (var r = 0; r < count; r++)
        {
            if (r != current && plan.Usable[r] && !blocked.Contains(r) && FindMerge(world.Retainers[r], plan.RetainerTargets[r], catalog) != null)
                return r;
        }

        return -1;
    }

    public static int Leaving(RetainerState retainer, int[] targets, int position)
    {
        var leaving = 0;
        for (var s = 0; s < targets.Length; s++)
        {
            if (targets[s] >= 0 && targets[s] != position && !retainer.Slots[s].IsEmpty)
                leaving++;
        }

        return leaving;
    }
}
