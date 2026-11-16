using System.Collections.Generic;

namespace RetainerSorter.Sorting;

// Everything in this folder is plain logic with no game access, so it can be run and tested
// outside the game. The Game folder feeds it snapshots and carries out the moves it asks for.

public static class Layout
{
    /// <summary> A retainer holds 7 pages of 25 slots. </summary>
    public const int RetainerSlots = 175;
    public const int RetainerPageSize = 25;

    /// <summary> The player's bag is 4 pages of 35 slots. </summary>
    public const int PlayerSlots = 140;
    public const int PlayerPageSize = 35;

    public const int MaxRetainers = 10;
}

public enum SortKey
{
    /// <summary> Level gathered at, else level crafted at, else a level estimated from the item level. </summary>
    Level = 0,
    ItemLevel = 1,
    Name = 2,
}

/// <summary> What the planner needs to know about an item, independent of any one stack. </summary>
public sealed class ItemMeta
{
    public uint ItemId;
    public string Name = string.Empty;
    public int StackSize = 1;
    public bool IsUnique;
    public uint UiCategory;
    public bool IsGear;

    /// <summary> Level times ten plus stars, on one scale for gathered, crafted and everything else. </summary>
    public int LevelKey;
    public int ItemLevel;

    /// <summary> The game's own category order, used to break ties. </summary>
    public int CategoryOrder;
}

public interface IItemCatalog
{
    ItemMeta Get(uint itemId);

    /// <summary> Whether an item belongs to a category key such as "g:materials" or "c:58". </summary>
    bool Matches(string categoryKey, ItemMeta meta);
}

/// <summary> The contents of one inventory slot. </summary>
public struct SlotItem
{
    public uint ItemId;
    public bool Hq;

    /// <summary> Collectables never merge with anything. </summary>
    public bool Collectable;
    public int Quantity;

    public readonly bool IsEmpty => ItemId == 0 || Quantity <= 0;

    public readonly bool SameStackAs(in SlotItem other)
        => ItemId == other.ItemId && Hq == other.Hq && !Collectable && !other.Collectable;
}

public sealed class RetainerState
{
    /// <summary> Position in the retainer list, 0-based. Rules refer to retainers by position. </summary>
    public int Position;
    public ulong Id;
    public string Name = string.Empty;

    /// <summary> The game will not let this retainer be summoned, so it is left out entirely. </summary>
    public bool Unavailable;

    /// <summary> False until the retainer's inventory has been read at least once. </summary>
    public bool Known;
    public SlotItem[] Slots = new SlotItem[Layout.RetainerSlots];

    public int UsedSlots()
    {
        var used = 0;
        foreach (var slot in Slots)
        {
            if (!slot.IsEmpty)
                used++;
        }

        return used;
    }
}

public sealed class PlayerState
{
    public SlotItem[] Slots = new SlotItem[Layout.PlayerSlots];

    /// <summary> Slots holding items the sorter took out of a retainer and still has to deliver. </summary>
    public bool[] Transit = new bool[Layout.PlayerSlots];

    /// <summary> Unique items the player holds themselves; a second copy cannot enter their inventory. </summary>
    public HashSet<uint> OwnedUniqueIds = [];

    public int FreeSlots()
    {
        var free = 0;
        foreach (var slot in Slots)
        {
            if (slot.IsEmpty)
                free++;
        }

        return free;
    }

    public int TransitCount()
    {
        var count = 0;
        for (var i = 0; i < Slots.Length; i++)
        {
            if (Transit[i] && !Slots[i].IsEmpty)
                count++;
        }

        return count;
    }
}

/// <summary> Everything the planner looks at: every retainer plus the player's bag. </summary>
public sealed class WorldState
{
    public List<RetainerState> Retainers = [];
    public PlayerState Player = new();
}

public sealed class Rule
{
    /// <summary> A category key from <see cref="IItemCatalog.Matches"/>; "any" matches everything. </summary>
    public string Category = CategoryKeys.Any;

    /// <summary> Optional: the item's name must contain this text. </summary>
    public string NameContains = string.Empty;

    /// <summary> Retainer positions (0-based) that hold the matching items, filled in list order. </summary>
    public List<int> Retainers = [];

    /// <summary> Matching items are never moved. </summary>
    public bool KeepInPlace;

    public SortKey Sort = SortKey.Level;

    public Rule Clone() => new()
    {
        Category = Category,
        NameContains = NameContains,
        Retainers = [.. Retainers],
        KeepInPlace = KeepInPlace,
        Sort = Sort,
    };
}

public sealed class Profile
{
    public string Name = "Default";

    /// <summary> Checked top to bottom; the first rule an item matches decides where it goes. </summary>
    public List<Rule> Rules = [];

    /// <summary> Retainer positions that also take items no rule matches. </summary>
    public List<int> MiscRetainers = [];

    /// <summary> Retainer positions the sorter never opens. </summary>
    public List<int> LockedRetainers = [];

    public Profile Clone() => new()
    {
        Name = Name,
        Rules = Rules.ConvertAll(rule => rule.Clone()),
        MiscRetainers = [.. MiscRetainers],
        LockedRetainers = [.. LockedRetainers],
    };
}

public static class CategoryKeys
{
    public const string Any = "any";
}

public enum MoveKind
{
    /// <summary> Player bag to the open retainer. </summary>
    Deposit,

    /// <summary> Open retainer to the player bag. </summary>
    Withdraw,

    /// <summary> Between two slots of the open retainer, to combine stacks. </summary>
    Merge,
}

/// <summary> One item move. Slot numbers are flat: page times page size plus slot. </summary>
public readonly record struct Move(MoveKind Kind, int FromSlot, int ToSlot);
