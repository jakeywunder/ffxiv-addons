using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerSorter.Sorting;

/// <summary> Where every stack should end up. Built fresh from a snapshot; never edited by hand. </summary>
public sealed class SortPlan
{
    /// <summary> The slot is empty, or not something the sorter handles. </summary>
    public const int NoItem = -2;

    /// <summary> The stack stays exactly where it is. </summary>
    public const int Stay = -1;

    /// <summary> Per retainer position, per slot: <see cref="NoItem"/>, <see cref="Stay"/>, or the target position. </summary>
    public int[][] RetainerTargets = [];

    /// <summary> Per player slot, for items in transit: the target position, or <see cref="Stay"/> if nowhere can take it. </summary>
    public int[] PlayerTargets = [];

    /// <summary> Retainers the sorter may open: scanned and not locked. </summary>
    public bool[] Usable = [];

    /// <summary> Slots each retainer will use once everything is in place. </summary>
    public int[] FinalSlots = [];

    /// <summary> Which rules (by index, -1 for unmatched items) put something in each retainer. </summary>
    public List<SortedSet<int>> RulesAt = [];

    public List<string> Warnings = [];

    /// <summary> Things worth telling the player that are not problems, such as why an item stays put. </summary>
    public List<string> Notes = [];

    /// <summary> Usable retainers whose inventory has not been read yet. Nothing can be planned until this is empty. </summary>
    public List<int> Unscanned = [];
}

/// <param name="excluded">Items to leave alone for this run, for example ones the game refused to move.</param>
public sealed class Planner(IItemCatalog catalog, Profile profile, IReadOnlySet<uint>? excluded = null)
{
    private readonly Dictionary<uint, int> ruleCache = [];

    private struct Unit
    {
        public int Retainer; // -1 while in the player's bag
        public int Slot;
        public int Quantity;
        public bool Hq;
        public bool Collectable;
    }

    private sealed class Group
    {
        public required ItemMeta Meta;
        public readonly List<Unit> Units = [];
        public int RuleIndex = -1;
        public int Need;
    }

    public SortPlan Build(WorldState world)
    {
        var count = world.Retainers.Count;
        var plan = new SortPlan
        {
            RetainerTargets = new int[count][],
            PlayerTargets = new int[Layout.PlayerSlots],
            Usable = new bool[count],
            FinalSlots = new int[count],
        };

        Array.Fill(plan.PlayerTargets, SortPlan.NoItem);

        var capacity = new int[count];
        for (var r = 0; r < count; r++)
        {
            plan.RetainerTargets[r] = new int[Layout.RetainerSlots];
            Array.Fill(plan.RetainerTargets[r], SortPlan.NoItem);
            plan.RulesAt.Add([]);

            var locked = profile.LockedRetainers.Contains(r) || world.Retainers[r].Unavailable;
            if (!locked && !world.Retainers[r].Known)
                plan.Unscanned.Add(r);

            plan.Usable[r] = !locked && world.Retainers[r].Known;
            capacity[r] = plan.Usable[r] ? Layout.RetainerSlots : 0;
        }

        if (plan.Unscanned.Count > 0)
            return plan;

        // Collect every stack the sorter may handle, grouped by item.
        var groups = new Dictionary<uint, Group>();
        for (var r = 0; r < count; r++)
        {
            if (!plan.Usable[r])
                continue;

            var slots = world.Retainers[r].Slots;
            for (var s = 0; s < slots.Length; s++)
            {
                if (!slots[s].IsEmpty)
                    AddUnit(groups, slots[s], r, s);
            }
        }

        var player = world.Player;
        for (var p = 0; p < player.Slots.Length; p++)
        {
            if (player.Transit[p] && !player.Slots[p].IsEmpty)
                AddUnit(groups, player.Slots[p], -1, p);
        }

        var misc = profile.MiscRetainers.Where(r => r >= 0 && r < count && plan.Usable[r]).Distinct().OrderBy(r => r).ToList();

        // Items that must not move claim their slots first.
        var movable = new List<Group>();
        var strays = new List<(Group Group, Unit Unit)>();
        var uniqueSets = new List<Group>();
        foreach (var group in groups.Values)
        {
            group.RuleIndex = FindRule(group.Meta);
            var pinned = (group.RuleIndex >= 0 && profile.Rules[group.RuleIndex].KeepInPlace) || excluded?.Contains(group.Meta.ItemId) == true;

            // Unique items come with two limits: a retainer can hold only one copy, and a copy
            // cannot pass through the player's bag while the player holds one.
            var carried = group.Meta.IsUnique && player.OwnedUniqueIds.Contains(group.Meta.ItemId);
            var severalCopies = group.Meta.IsUnique && group.Units.Count > 1;

            if (!pinned && !carried && !severalCopies)
            {
                group.Need = SlotsNeeded(group.Meta, group.Units);
                movable.Add(group);
                continue;
            }

            // Several copies of a unique item: every copy counts as staying for now. Once all the
            // slots that are certainly taken are known, some copies may be given a better home.
            if (!pinned && !carried)
                uniqueSets.Add(group);
            else if (!pinned)
                plan.Notes.Add($"{group.Meta.Name}: unique, and you are carrying one yourself, so it stays put.");

            foreach (var unit in group.Units)
            {
                if (unit.Retainer < 0)
                {
                    if (pinned || carried)
                        strays.Add((group, unit));

                    continue;
                }

                plan.RetainerTargets[unit.Retainer][unit.Slot] = SortPlan.Stay;
                capacity[unit.Retainer]--;
                plan.FinalSlots[unit.Retainer]++;
            }
        }

        if (uniqueSets.Count > 0)
        {
            // When only some copies can move, take them from where they are most out of place:
            // retainers that a rule sets aside for something, emptiest first.
            var themed = profile.Rules.Where(rule => !rule.KeepInPlace).SelectMany(rule => rule.Retainers).ToHashSet();
            var used = world.Retainers.Select(retainer => retainer.UsedSlots()).ToArray();
            int Reluctance(Unit unit) => unit.Retainer < 0 ? -1 : (themed.Contains(unit.Retainer) ? 0 : 1000) + used[unit.Retainer];

            foreach (var group in uniqueSets)
            {
                var destinations = RuleHasDestinations(group.RuleIndex, plan) ? Destinations(profile.Rules[group.RuleIndex], plan) : misc;
                var order = group.Units.OrderBy(Reluctance).ThenBy(unit => unit.Retainer).ThenBy(unit => unit.Slot).ToList();
                SpreadUniqueCopies(group, order, destinations, capacity, plan, strays);
            }
        }

        // Rules claim space in order. Whatever does not fit waits for the overflow pass, so one
        // rule's overflow never takes the space another rule was promised.
        var overflow = new List<(Group Group, int After)>();
        var byRule = movable.GroupBy(group => RuleHasDestinations(group.RuleIndex, plan) ? group.RuleIndex : -1)
            .ToDictionary(set => set.Key, set => set.ToList());

        for (var ruleIndex = 0; ruleIndex < profile.Rules.Count; ruleIndex++)
        {
            if (!byRule.TryGetValue(ruleIndex, out var members))
                continue;

            var rule = profile.Rules[ruleIndex];
            var destinations = Destinations(rule, plan);
            members.Sort(Comparer(rule.Sort));
            foreach (var group in members)
            {
                if (!TryPlace(group, destinations, capacity, plan, ruleIndex))
                    overflow.Add((group, destinations[^1]));
            }
        }

        if (byRule.TryGetValue(-1, out var unmatched))
        {
            unmatched.Sort(Comparer(SortKey.Level));
            foreach (var group in unmatched)
            {
                // Without a misc retainer, unmatched items gather wherever most of them already are.
                var destinations = misc.Count > 0 ? misc : Home(group);
                if (destinations.Count == 0)
                    overflow.Add((group, count - 1));
                else if (!TryPlace(group, destinations, capacity, plan, -1))
                    overflow.Add((group, destinations[^1]));
            }
        }

        foreach (var (group, after) in overflow)
            PlaceOverflow(group, after, capacity, plan);

        foreach (var (group, unit) in strays)
            PlaceStray(group, unit, world, capacity, plan);

        return plan;
    }

    /// <summary>
    /// Several copies of a unique item can never share a retainer. Each destination that lacks
    /// one gets one, taken from the copies sitting elsewhere; the rest stay where they are.
    /// </summary>
    /// <param name="order">The group's copies, the ones to move first at the front. Copies being carried lead.</param>
    private static void SpreadUniqueCopies(Group group, List<Unit> order, List<int> destinations, int[] capacity, SortPlan plan, List<(Group Group, Unit Unit)> strays)
    {
        // Every copy on a retainer has already been counted as staying where it is.
        var holders = group.Units.Where(unit => unit.Retainer >= 0).Select(unit => unit.Retainer).ToHashSet();
        var open = new Queue<int>(destinations.Where(destination => !holders.Contains(destination)));

        var left = 0;
        foreach (var unit in order)
        {
            var outside = unit.Retainer < 0 || !destinations.Contains(unit.Retainer);
            if (!outside)
                continue;

            while (open.Count > 0 && capacity[open.Peek()] <= 0)
                open.Dequeue();

            if (open.Count == 0)
            {
                if (unit.Retainer < 0)
                    strays.Add((group, unit));
                else
                    left++;

                continue;
            }

            // The copy leaves its slot, which frees it for something else.
            if (unit.Retainer >= 0)
            {
                capacity[unit.Retainer]++;
                plan.FinalSlots[unit.Retainer]--;
            }

            Claim(plan, unit, open.Dequeue(), capacity, group.RuleIndex);
        }

        if (left > 0 && destinations.Count > 0)
            plan.Notes.Add($"{group.Meta.Name}: unique, one per retainer, so {left} {(left == 1 ? "copy stays" : "copies stay")} put.");
    }

    private void AddUnit(Dictionary<uint, Group> groups, in SlotItem item, int retainer, int slot)
    {
        if (!groups.TryGetValue(item.ItemId, out var group))
        {
            group = new Group { Meta = catalog.Get(item.ItemId) };
            groups[item.ItemId] = group;
        }

        group.Units.Add(new Unit
        {
            Retainer = retainer,
            Slot = slot,
            Quantity = item.Quantity,
            Hq = item.Hq,
            Collectable = item.Collectable,
        });
    }

    /// <summary> The index of the first rule this item matches, or -1. </summary>
    public int RuleFor(uint itemId) => FindRule(catalog.Get(itemId));

    /// <summary> Whether the first rule this item matches says to leave it wherever it is. </summary>
    public bool KeepsInPlace(uint itemId)
    {
        var index = FindRule(catalog.Get(itemId));
        return index >= 0 && profile.Rules[index].KeepInPlace;
    }

    private int FindRule(ItemMeta meta)
    {
        if (ruleCache.TryGetValue(meta.ItemId, out var cached))
            return cached;

        var found = -1;
        for (var i = 0; i < profile.Rules.Count; i++)
        {
            var rule = profile.Rules[i];
            if (rule.Category != CategoryKeys.Any && !catalog.Matches(rule.Category, meta))
                continue;

            if (rule.NameContains.Length > 0 && !meta.Name.Contains(rule.NameContains.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            found = i;
            break;
        }

        ruleCache[meta.ItemId] = found;
        return found;
    }

    private bool RuleHasDestinations(int ruleIndex, SortPlan plan)
        => ruleIndex >= 0 && Destinations(profile.Rules[ruleIndex], plan).Count > 0;

    private static List<int> Destinations(Rule rule, SortPlan plan)
        => rule.Retainers.Where(r => r >= 0 && r < plan.Usable.Length && plan.Usable[r]).Distinct().OrderBy(r => r).ToList();

    /// <summary> The retainer already holding the most of this item. </summary>
    private static List<int> Home(Group group)
    {
        var best = -1;
        var bestQuantity = 0L;
        foreach (var set in group.Units.Where(unit => unit.Retainer >= 0).GroupBy(unit => unit.Retainer).OrderBy(set => set.Key))
        {
            var quantity = set.Sum(unit => (long)unit.Quantity);
            if (quantity > bestQuantity)
            {
                best = set.Key;
                bestQuantity = quantity;
            }
        }

        return best < 0 ? [] : [best];
    }

    /// <summary> Slots a group takes once its stacks are merged as far as the game allows. </summary>
    private static int SlotsNeeded(ItemMeta meta, List<Unit> units)
    {
        if (meta.StackSize <= 1)
            return units.Count;

        long normal = 0;
        long high = 0;
        var loose = 0;
        foreach (var unit in units)
        {
            if (unit.Collectable)
                loose++;
            else if (unit.Hq)
                high += unit.Quantity;
            else
                normal += unit.Quantity;
        }

        return loose + (int)((normal + meta.StackSize - 1) / meta.StackSize) + (int)((high + meta.StackSize - 1) / meta.StackSize);
    }

    private static Comparison<Group> Comparer(SortKey key) => key switch
    {
        SortKey.Name => (a, b) => Chain(
            string.Compare(a.Meta.Name, b.Meta.Name, StringComparison.OrdinalIgnoreCase),
            a.Meta.ItemId.CompareTo(b.Meta.ItemId)),
        SortKey.ItemLevel => (a, b) => Chain(
            a.Meta.ItemLevel.CompareTo(b.Meta.ItemLevel),
            a.Meta.LevelKey.CompareTo(b.Meta.LevelKey),
            a.Meta.CategoryOrder.CompareTo(b.Meta.CategoryOrder),
            a.Meta.ItemId.CompareTo(b.Meta.ItemId)),
        _ => (a, b) => Chain(
            a.Meta.LevelKey.CompareTo(b.Meta.LevelKey),
            a.Meta.ItemLevel.CompareTo(b.Meta.ItemLevel),
            a.Meta.CategoryOrder.CompareTo(b.Meta.CategoryOrder),
            a.Meta.ItemId.CompareTo(b.Meta.ItemId)),
    };

    private static int Chain(params ReadOnlySpan<int> results)
    {
        foreach (var result in results)
        {
            if (result != 0)
                return result;
        }

        return 0;
    }

    /// <summary> Puts the whole group in the first destination with room, keeping duplicates together. </summary>
    private static bool TryPlace(Group group, List<int> destinations, int[] capacity, SortPlan plan, int ruleIndex)
    {
        foreach (var destination in destinations)
        {
            if (capacity[destination] < group.Need)
                continue;

            capacity[destination] -= group.Need;
            plan.FinalSlots[destination] += group.Need;
            plan.RulesAt[destination].Add(ruleIndex);
            foreach (var unit in group.Units)
                SetTarget(plan, unit, destination);

            return true;
        }

        return false;
    }

    private static void PlaceOverflow(Group group, int after, int[] capacity, SortPlan plan)
    {
        var count = capacity.Length;
        var order = new List<int>();
        for (var step = 1; step <= count; step++)
        {
            var r = (after + step) % count;
            if (plan.Usable[r])
                order.Add(r);
        }

        if (TryPlace(group, order, capacity, plan, group.RuleIndex))
            return;

        // No single retainer can take the whole group, so it is split stack by stack.
        // Stacks that can stay where they are do; the rest go wherever a slot is left.
        var pending = new List<Unit>();
        foreach (var unit in group.Units.OrderBy(unit => unit.Retainer).ThenBy(unit => unit.Slot))
        {
            if (unit.Retainer >= 0 && capacity[unit.Retainer] > 0)
                Claim(plan, unit, unit.Retainer, capacity, group.RuleIndex);
            else
                pending.Add(unit);
        }

        foreach (var unit in pending)
        {
            var destination = order.FirstOrDefault(r => capacity[r] > 0, -1);
            if (destination >= 0)
            {
                Claim(plan, unit, destination, capacity, group.RuleIndex);
                continue;
            }

            SetTarget(plan, unit, SortPlan.Stay);
            if (unit.Retainer >= 0)
                plan.FinalSlots[unit.Retainer]++;

            plan.Warnings.Add($"No retainer has room for {group.Meta.Name}; it stays where it is.");
        }
    }

    /// <summary> An item already in the player's bag that the rules now say should not move. </summary>
    private static void PlaceStray(Group group, Unit unit, WorldState world, int[] capacity, SortPlan plan)
    {
        var id = group.Meta.ItemId;
        var candidates = Enumerable.Range(0, capacity.Length).Where(r => plan.Usable[r] && capacity[r] > 0);
        var holders = group.Units.Where(other => other.Retainer >= 0).Select(other => other.Retainer).ToHashSet();

        int destination;
        if (group.Meta.IsUnique)
        {
            // One copy per retainer, including copies other strays were just sent to.
            destination = candidates.FirstOrDefault(r => !holders.Contains(r) && !HasIncoming(plan, world, id, r), -1);
        }
        else
        {
            destination = candidates.OrderByDescending(holders.Contains).FirstOrDefault(-1);
        }

        if (destination < 0)
        {
            plan.PlayerTargets[unit.Slot] = SortPlan.Stay;
            plan.Warnings.Add($"No retainer can take {group.Meta.Name}; it stays in your inventory.");
            return;
        }

        Claim(plan, unit, destination, capacity, group.RuleIndex);
    }

    private static bool HasIncoming(SortPlan plan, WorldState world, uint itemId, int retainer)
    {
        for (var p = 0; p < plan.PlayerTargets.Length; p++)
        {
            if (plan.PlayerTargets[p] == retainer && world.Player.Slots[p].ItemId == itemId)
                return true;
        }

        return false;
    }

    private static void Claim(SortPlan plan, Unit unit, int destination, int[] capacity, int ruleIndex)
    {
        capacity[destination]--;
        plan.FinalSlots[destination]++;
        plan.RulesAt[destination].Add(ruleIndex);
        SetTarget(plan, unit, destination);
    }

    private static void SetTarget(SortPlan plan, Unit unit, int target)
    {
        if (unit.Retainer < 0)
            plan.PlayerTargets[unit.Slot] = target;
        else
            plan.RetainerTargets[unit.Retainer][unit.Slot] = target;
    }
}
