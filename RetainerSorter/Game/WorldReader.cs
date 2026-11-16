using System;
using System.Collections.Generic;
using RetainerSorter.Data;
using RetainerSorter.Sorting;

namespace RetainerSorter.Game;

/// <summary> Remembers which of the player's bag slots hold items the sorter is carrying between retainers. </summary>
internal sealed class TransitStore
{
    // Written to disk at most this often while a sort runs; a crash can lose track of what was
    // picked up since the last write, which then simply stays in the bag as ordinary items.
    private const long SaveIntervalMs = 1000;

    private ulong loadedFor;
    private bool[] flags = new bool[Layout.PlayerSlots];
    private bool unsaved;
    private long lastSave;

    /// <summary> The current flags, with slots that have since been emptied cleared. </summary>
    public bool[] Flags(SlotItem[] bag)
    {
        var character = Plugin.PlayerState.ContentId;
        if (character != loadedFor)
        {
            loadedFor = character;
            flags = new bool[Layout.PlayerSlots];
            if (Plugin.Config.Transit.TryGetValue(character, out var saved))
            {
                // Only trust a saved slot if it still holds the same item.
                foreach (var entry in saved)
                {
                    if (entry.Slot >= 0 && entry.Slot < bag.Length && !bag[entry.Slot].IsEmpty
                        && bag[entry.Slot].ItemId == entry.ItemId && bag[entry.Slot].Hq == entry.Hq)
                        flags[entry.Slot] = true;
                }
            }
        }

        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i] && bag[i].IsEmpty)
            {
                flags[i] = false;
                unsaved = true;
            }
        }

        return flags;
    }

    public void Mark(int slot, bool carrying)
    {
        if (flags[slot] == carrying)
            return;

        flags[slot] = carrying;
        unsaved = true;
    }

    public void Save(SlotItem[] bag, bool force)
    {
        var now = Environment.TickCount64;
        if (!unsaved || loadedFor == 0 || (!force && now - lastSave < SaveIntervalMs))
            return;

        var list = new List<TransitSlot>();
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i] && !bag[i].IsEmpty)
                list.Add(new TransitSlot { Slot = i, ItemId = bag[i].ItemId, Hq = bag[i].Hq });
        }

        if (list.Count == 0)
            Plugin.Config.Transit.Remove(loadedFor);
        else
            Plugin.Config.Transit[loadedFor] = list;

        Plugin.Config.Save();
        unsaved = false;
        lastSave = now;
    }
}

/// <summary> Builds the planner's picture of the world: saved retainer contents, plus whatever is open right now. </summary>
internal sealed class WorldReader(ItemCatalog catalog, RetainerCache cache, TransitStore transit)
{
    public WorldState Read()
    {
        var world = new WorldState();
        var openRetainer = OpenRetainerId();

        var entries = Inventories.Retainers();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var state = new RetainerState
            {
                Position = i,
                Id = entry.Id,
                Name = entry.Name,
                Unavailable = !entry.Available,
            };

            if (entry.Id == openRetainer)
            {
                state.Slots = Inventories.ReadRetainer();
                state.Known = true;
                cache.Set(entry.Id, state.Slots);
            }
            else if (cache.Get(entry.Id) is { } saved)
            {
                state.Slots = saved;
                state.Known = true;
            }

            world.Retainers.Add(state);
        }

        world.Player.Slots = Inventories.ReadPlayer();
        world.Player.Transit = transit.Flags(world.Player.Slots);
        world.Player.OwnedUniqueIds = Inventories.OwnedUniques(catalog, world.Player.Slots, world.Player.Transit);
        return world;
    }

    /// <summary> The retainer whose inventory is open and fully loaded, or 0. </summary>
    public static unsafe ulong OpenRetainerId()
        => GameUi.RetainerInventory() != null && Inventories.RetainerLoaded() ? GameUi.ActiveRetainerId() : 0;
}
