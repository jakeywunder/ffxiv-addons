using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using RetainerSorter.Sorting;

namespace RetainerSorter.Game;

internal readonly record struct RetainerEntry(ulong Id, string Name, bool Available, ushort VentureId, uint VentureComplete);

/// <summary> Reads inventories from game memory and asks the game to move single stacks. </summary>
internal static unsafe class Inventories
{
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    ];

    private static readonly InventoryType[] PlayerPages =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    // Everywhere else the player's own gear can be: what is equipped, and the armoury chest.
    private static readonly InventoryType[] GearContainers =
    [
        InventoryType.EquippedItems,
        (InventoryType)3200, (InventoryType)3201, (InventoryType)3202, (InventoryType)3203, (InventoryType)3204,
        (InventoryType)3205, (InventoryType)3206, (InventoryType)3207, (InventoryType)3208, (InventoryType)3209,
        (InventoryType)3300, (InventoryType)3400, (InventoryType)3500,
    ];

    /// <summary> The player's retainers in the order the retainer list shows them. </summary>
    public static List<RetainerEntry> Retainers()
    {
        var result = new List<RetainerEntry>();
        var manager = RetainerManager.Instance();
        if (manager == null)
            return result;

        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count && i < Layout.MaxRetainers; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0)
                continue;

            result.Add(new RetainerEntry(retainer->RetainerId, retainer->NameString, retainer->Available, retainer->VentureId, retainer->VentureComplete));
        }

        return result;
    }

    /// <summary> True once the game has received every page of the open retainer's inventory. </summary>
    public static bool RetainerLoaded()
    {
        var manager = InventoryManager.Instance();
        foreach (var page in RetainerPages)
        {
            var container = manager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded || container->Size < Layout.RetainerPageSize)
                return false;
        }

        return true;
    }

    public static SlotItem[] ReadRetainer() => Read(RetainerPages, Layout.RetainerPageSize);

    public static SlotItem[] ReadPlayer() => Read(PlayerPages, Layout.PlayerPageSize);

    /// <summary> Unique items the player holds in the given bag slots, on their person, or in the armoury chest. </summary>
    public static HashSet<uint> OwnedUniques(IItemCatalog catalog, SlotItem[] bag, bool[] transit)
    {
        var owned = new HashSet<uint>();
        for (var i = 0; i < bag.Length; i++)
        {
            if (!bag[i].IsEmpty && !transit[i] && catalog.Get(bag[i].ItemId).IsUnique)
                owned.Add(bag[i].ItemId);
        }

        var manager = InventoryManager.Instance();
        foreach (var type in GearContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId != 0 && catalog.Get(item->ItemId).IsUnique)
                    owned.Add(item->ItemId);
            }
        }

        return owned;
    }

    public static SlotItem ReadSlot(bool retainer, int flatSlot)
    {
        var (type, slot) = Address(retainer, flatSlot);
        var container = InventoryManager.Instance()->GetInventoryContainer(type);
        return container == null ? default : Convert(container->GetInventorySlot(slot));
    }

    /// <summary>
    /// Asks the game to move a stack, exactly as dragging it would: onto an empty slot it moves,
    /// onto a matching stack it tops that stack up.
    /// </summary>
    public static void Execute(Move move)
    {
        var (fromType, fromSlot) = Address(move.Kind != MoveKind.Deposit, move.FromSlot);
        var (toType, toSlot) = Address(move.Kind != MoveKind.Withdraw, move.ToSlot);
        InventoryManager.Instance()->MoveItemSlot(fromType, (ushort)fromSlot, toType, (ushort)toSlot, true);
    }

    private static (InventoryType Type, int Slot) Address(bool retainer, int flatSlot)
        => retainer
            ? (RetainerPages[flatSlot / Layout.RetainerPageSize], flatSlot % Layout.RetainerPageSize)
            : (PlayerPages[flatSlot / Layout.PlayerPageSize], flatSlot % Layout.PlayerPageSize);

    private static SlotItem[] Read(InventoryType[] pages, int pageSize)
    {
        var slots = new SlotItem[pages.Length * pageSize];
        var manager = InventoryManager.Instance();
        for (var page = 0; page < pages.Length; page++)
        {
            var container = manager->GetInventoryContainer(pages[page]);
            if (container == null)
                continue;

            for (var slot = 0; slot < pageSize && slot < container->Size; slot++)
                slots[page * pageSize + slot] = Convert(container->GetInventorySlot(slot));
        }

        return slots;
    }

    private static SlotItem Convert(InventoryItem* item)
    {
        if (item == null || item->ItemId == 0 || item->Quantity <= 0)
            return default;

        return new SlotItem
        {
            ItemId = item->ItemId,
            Quantity = item->Quantity,
            Hq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
            Collectable = item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable),
        };
    }
}
