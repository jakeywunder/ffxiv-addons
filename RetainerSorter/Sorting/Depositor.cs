using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerSorter.Sorting;

/// <summary> One line of the deposit preview: a kind of item in the bag and what happens to it. </summary>
/// <param name="Retainer">Position of the retainer that already holds this item.</param>
/// <param name="Left">How much of it would not fit there.</param>
/// <param name="Kept">The player chose to keep this item with them.</param>
public sealed record DepositLine(uint ItemId, bool Hq, int Retainer, long Quantity, long Left, bool Kept);

public sealed class DepositForecast
{
    public int Moves;
    public int Visits;
    public List<DepositLine> Lines = [];
}

/// <summary>
/// Depositing: items in the player's bag go to the retainer that already holds that item.
/// Nothing is ever taken out of a retainer, and nothing is put where the item is not already kept.
/// </summary>
public static class Depositor
{
    /// <summary> The bag slot is not part of the deposit. </summary>
    public const int None = -2;

    /// <summary> For every item some usable retainer holds: the retainer holding the most of it. </summary>
    public static Dictionary<uint, int> Homes(WorldState world, Profile profile)
    {
        var best = new Dictionary<uint, (int Retainer, long Quantity)>();
        for (var r = 0; r < world.Retainers.Count; r++)
        {
            var retainer = world.Retainers[r];
            if (!retainer.Known || retainer.Unavailable || profile.LockedRetainers.Contains(r))
                continue;

            var here = new Dictionary<uint, long>();
            foreach (var slot in retainer.Slots)
            {
                if (!slot.IsEmpty)
                    here[slot.ItemId] = here.GetValueOrDefault(slot.ItemId) + slot.Quantity;
            }

            foreach (var (itemId, quantity) in here)
            {
                if (!best.TryGetValue(itemId, out var current) || quantity > current.Quantity)
                    best[itemId] = (r, quantity);
            }
        }

        return best.ToDictionary(pair => pair.Key, pair => pair.Value.Retainer);
    }

    /// <summary>
    /// Per bag slot, the retainer its item should go to, or <see cref="None"/>.
    /// Items the sorter is already carrying, unique items (a retainer cannot hold two), and
    /// anything <paramref name="heldBack"/> says to leave alone are not deposited.
    /// </summary>
    public static int[] Targets(WorldState world, IItemCatalog catalog, Profile profile, Func<uint, bool> heldBack)
    {
        var targets = new int[Layout.PlayerSlots];
        Array.Fill(targets, None);

        var homes = Homes(world, profile);
        var player = world.Player;
        for (var p = 0; p < player.Slots.Length; p++)
        {
            ref readonly var item = ref player.Slots[p];
            if (item.IsEmpty || player.Transit[p] || !homes.TryGetValue(item.ItemId, out var home))
                continue;

            if (catalog.Get(item.ItemId).IsUnique || heldBack(item.ItemId))
                continue;

            targets[p] = home;
        }

        return targets;
    }

    /// <summary>
    /// The next retainer that can take at least one of the items waiting for it. Each retainer is
    /// visited once, and a retainer too full to take anything is not opened at all.
    /// </summary>
    public static int NextRetainer(WorldState world, int[] targets, IItemCatalog catalog, ISet<int> visited)
    {
        for (var r = 0; r < world.Retainers.Count; r++)
        {
            if (!visited.Contains(r) && NextMove(world, targets, catalog, r) != null)
                return r;
        }

        return -1;
    }

    /// <summary> The next deposit into the open retainer: top up a matching stack if one has room, else use an empty slot. </summary>
    public static Move? NextMove(WorldState world, int[] targets, IItemCatalog catalog, int r)
    {
        var retainer = world.Retainers[r];
        var player = world.Player;
        var waiting = false;

        for (var p = 0; p < targets.Length; p++)
        {
            if (targets[p] != r || player.Slots[p].IsEmpty)
                continue;

            waiting = true;
            ref readonly var item = ref player.Slots[p];
            var stackSize = catalog.Get(item.ItemId).StackSize;
            if (item.Collectable || stackSize <= 1)
                continue;

            for (var s = 0; s < retainer.Slots.Length; s++)
            {
                ref readonly var other = ref retainer.Slots[s];
                if (!other.IsEmpty && other.Quantity < stackSize && other.SameStackAs(item))
                    return new Move(MoveKind.Deposit, p, s);
            }
        }

        if (!waiting)
            return null;

        var firstEmpty = Array.FindIndex(retainer.Slots, slot => slot.IsEmpty);
        if (firstEmpty < 0)
            return null;

        for (var p = 0; p < targets.Length; p++)
        {
            if (targets[p] == r && !player.Slots[p].IsEmpty)
                return new Move(MoveKind.Deposit, p, firstEmpty);
        }

        return null;
    }

    /// <summary> Plays the whole deposit on a copy and reports what would move and what would not fit. </summary>
    /// <param name="keptItems">Items the player keeps with them; listed in the forecast so the choice can be undone.</param>
    public static DepositForecast Forecast(WorldState source, IItemCatalog catalog, Profile profile, Func<uint, bool> heldBack, IReadOnlySet<uint> keptItems, Action<WorldState>? inspectResult = null)
    {
        var world = Simulator.Clone(source);
        var targets = Targets(world, catalog, profile, heldBack);
        var before = (SlotItem[])world.Player.Slots.Clone();
        var forecast = new DepositForecast();

        var visited = new HashSet<int>();
        while (NextRetainer(world, targets, catalog, visited) is var r and >= 0)
        {
            visited.Add(r);
            var movesHere = 0;
            while (NextMove(world, targets, catalog, r) is { } move)
            {
                if (!Simulator.Pour(ref world.Player.Slots[move.FromSlot], ref world.Retainers[r].Slots[move.ToSlot], catalog))
                    break;

                movesHere++;
            }

            forecast.Moves += movesHere;
            if (movesHere > 0)
                forecast.Visits++;
        }

        var totals = new Dictionary<(uint ItemId, bool Hq, int Retainer), (long Quantity, long Left)>();
        for (var p = 0; p < targets.Length; p++)
        {
            if (targets[p] < 0)
                continue;

            var key = (before[p].ItemId, before[p].Hq, targets[p]);
            var current = totals.GetValueOrDefault(key);
            totals[key] = (current.Quantity + before[p].Quantity, current.Left + (world.Player.Slots[p].IsEmpty ? 0 : world.Player.Slots[p].Quantity));
        }

        foreach (var (key, value) in totals)
            forecast.Lines.Add(new DepositLine(key.ItemId, key.Hq, key.Retainer, value.Quantity, value.Left, false));

        // Kept items that would otherwise have been deposited.
        var homes = Homes(source, profile);
        var kept = new Dictionary<(uint ItemId, bool Hq), long>();
        for (var p = 0; p < source.Player.Slots.Length; p++)
        {
            var item = source.Player.Slots[p];
            if (item.IsEmpty || source.Player.Transit[p] || !keptItems.Contains(item.ItemId) || !homes.ContainsKey(item.ItemId))
                continue;

            kept[(item.ItemId, item.Hq)] = kept.GetValueOrDefault((item.ItemId, item.Hq)) + item.Quantity;
        }

        foreach (var (key, quantity) in kept)
            forecast.Lines.Add(new DepositLine(key.ItemId, key.Hq, homes[key.ItemId], quantity, quantity, true));

        inspectResult?.Invoke(world);
        return forecast;
    }
}
