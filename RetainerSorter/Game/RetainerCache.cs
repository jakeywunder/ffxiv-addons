using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using RetainerSorter.Sorting;

namespace RetainerSorter.Game;

/// <summary>
/// What each retainer held the last time its inventory was open. The game only knows a retainer's
/// inventory while that retainer is summoned, so the plan and preview are built from this.
/// </summary>
internal sealed class RetainerCache
{
    private sealed class Entry
    {
        public DateTime ReadAt;

        // Occupied slots only: slot, item, quantity, and 1 for high quality, 2 for collectable.
        public List<long[]> Items = [];
    }

    private readonly string path;
    private readonly HashSet<ulong> readThisSession = [];
    private Dictionary<ulong, Entry> entries = [];
    private bool dirty;

    public RetainerCache(string directory)
    {
        path = Path.Combine(directory, "retainers.json");
        try
        {
            if (File.Exists(path))
                entries = JsonConvert.DeserializeObject<Dictionary<ulong, Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Could not read the saved retainer contents; retainers will need a new scan.");
            entries = [];
        }
    }

    public bool Has(ulong retainerId) => entries.ContainsKey(retainerId);

    /// <summary> Read since the plugin was loaded, so known to be current. </summary>
    public bool IsFresh(ulong retainerId) => readThisSession.Contains(retainerId);

    public DateTime? ReadAt(ulong retainerId) => entries.TryGetValue(retainerId, out var entry) ? entry.ReadAt : null;

    public SlotItem[]? Get(ulong retainerId)
    {
        if (!entries.TryGetValue(retainerId, out var entry))
            return null;

        var slots = new SlotItem[Layout.RetainerSlots];
        foreach (var item in entry.Items)
        {
            if (item.Length < 4 || item[0] < 0 || item[0] >= slots.Length)
                continue;

            slots[item[0]] = new SlotItem
            {
                ItemId = (uint)item[1],
                Quantity = (int)item[2],
                Hq = (item[3] & 1) != 0,
                Collectable = (item[3] & 2) != 0,
            };
        }

        return slots;
    }

    public void Set(ulong retainerId, SlotItem[] slots)
    {
        var entry = new Entry { ReadAt = DateTime.UtcNow };
        for (var i = 0; i < slots.Length; i++)
        {
            if (!slots[i].IsEmpty)
                entry.Items.Add([i, slots[i].ItemId, slots[i].Quantity, (slots[i].Hq ? 1 : 0) | (slots[i].Collectable ? 2 : 0)]);
        }

        entries[retainerId] = entry;
        readThisSession.Add(retainerId);
        dirty = true;
    }

    public void Save()
    {
        if (!dirty)
            return;

        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(entries));
            dirty = false;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Could not save the retainer contents.");
        }
    }
}
