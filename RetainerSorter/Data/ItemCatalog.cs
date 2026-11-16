using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using RetainerSorter.Sorting;

namespace RetainerSorter.Data;

/// <summary> A choice in the rule editor's category list. </summary>
public sealed record CategoryOption(string Key, string Label);

/// <summary>
/// Item facts read from the game's own data sheets: categories, stack sizes, and the level each
/// item is gathered or crafted at. Only depends on the sheets, so it also runs outside the game.
/// </summary>
public sealed class ItemCatalog : IItemCatalog
{
    // Item categories as the game groups them ("ItemUICategory"). The ids are stable.
    private const uint Medicine = 44, Ingredient = 45, Meal = 46, Seafood = 47, Stone = 48, Metal = 49, Lumber = 50,
        Cloth = 51, Leather = 52, Bone = 53, Reagent = 54, Part = 56, Furnishing = 57, Catalyst = 60, Miscellany = 61,
        Other = 63, OutdoorFurnishing = 76, Table = 77, Tabletop = 78, WallMounted = 79, Rug = 80, Minion = 81,
        Gardening = 82, Demimateria = 83, SeasonalMiscellany = 85, TripleTriadCard = 86, OrchestrionRoll = 94, Painting = 95;

    private static readonly uint[] HiddenCategories = [39 /* Unobtainable */, 59 /* Crystal */, 100 /* Currency */];

    private readonly ExcelSheet<Item> items;
    private readonly Dictionary<uint, ItemMeta> cache = [];
    private readonly Dictionary<string, HashSet<uint>> categorySets = [];
    private readonly Dictionary<uint, int> categoryOrder = [];
    private readonly Dictionary<uint, int> gatherLevels = [];
    private readonly Dictionary<uint, int> craftLevels = [];
    private readonly int[] levelByItemLevel;

    /// <summary> Every category a rule can pick, groups first, then the game's own categories in its order. </summary>
    public IReadOnlyList<CategoryOption> Options { get; }

    public ItemCatalog(ExcelModule excel)
    {
        items = excel.GetSheet<Item>();

        var uiCategories = excel.GetSheet<ItemUICategory>()
            .Where(row => row.RowId != 0 && row.Name.ByteLength > 0)
            .OrderBy(row => row.OrderMajor).ThenBy(row => row.OrderMinor)
            .ToList();

        for (var i = 0; i < uiCategories.Count; i++)
            categoryOrder[uiCategories[i].RowId] = i;

        HashSet<uint> Major(int major) => uiCategories.Where(row => row.OrderMajor == major).Select(row => row.RowId).ToHashSet();

        var options = new List<CategoryOption> { new(CategoryKeys.Any, "Anything") };
        void Group(string key, string label, IEnumerable<uint> ids)
        {
            categorySets[key] = ids.ToHashSet();
            options.Add(new CategoryOption(key, label));
        }

        var houseParts = Enumerable.Range(64, 12).Select(id => (uint)id); // permits, roofs, walls, windows, doors, fences, flooring, lights
        uint[] furnishings = [Furnishing, OutdoorFurnishing, Table, Tabletop, WallMounted, Rug, Painting];
        var shipParts = new uint[] { 90, 91, 92, 93, 101, 102, 103, 104 };

        Group("g:materials", "Materials (all)", [Ingredient, Seafood, Stone, Metal, Lumber, Cloth, Leather, Bone, Reagent, Part]);
        Group("g:gear", "Gear (all)", Major(1).Concat(Major(2)).Concat(Major(3)).Concat(Major(4)));
        Group("g:weapons", "Gear: weapons", Major(1));
        Group("g:tools", "Gear: crafting and gathering tools", Major(2));
        Group("g:armor", "Gear: armor and shields", Major(3));
        Group("g:accessories", "Gear: accessories", Major(4));
        Group("g:consumables", "Food and medicine", [Meal, Medicine]);
        Group("g:housing", "Housing (all)", furnishings.Concat(houseParts).Append(Gardening));
        Group("g:furnishings", "Housing: furnishings", furnishings);
        Group("g:collectibles", "Minions, cards and orchestrion rolls", [Minion, TripleTriadCard, OrchestrionRoll]);
        Group("g:shipparts", "Airship and submersible parts", shipParts);
        Group("g:misc", "Miscellany and other", [Catalyst, Miscellany, Other, Demimateria, SeasonalMiscellany]);

        foreach (var row in uiCategories)
        {
            if (HiddenCategories.Contains(row.RowId))
                continue;

            Group($"c:{row.RowId}", row.Name.ExtractText(), [row.RowId]);
        }

        Options = options;

        ReadLevels(excel);
        levelByItemLevel = BuildItemLevelScale();
    }

    public string LabelFor(string key)
        => Options.FirstOrDefault(option => option.Key == key)?.Label ?? "Unknown category";

    // Few-word names for the groups, for places with very little room such as a retainer comment.
    private static readonly Dictionary<string, string> ShortGroupLabels = new()
    {
        [CategoryKeys.Any] = "Misc",
        ["g:materials"] = "Materials",
        ["g:gear"] = "Gear",
        ["g:weapons"] = "Weapons",
        ["g:tools"] = "Tools",
        ["g:armor"] = "Armor",
        ["g:accessories"] = "Accessories",
        ["g:consumables"] = "Food & medicine",
        ["g:housing"] = "Housing",
        ["g:furnishings"] = "Furnishings",
        ["g:collectibles"] = "Collectibles",
        ["g:shipparts"] = "Ship parts",
        ["g:misc"] = "Misc",
    };

    /// <summary> A short name for a category: the group's short form, or the game's own category name. </summary>
    public string ShortLabelFor(string key)
        => ShortGroupLabels.TryGetValue(key, out var label) ? label : LabelFor(key);

    public bool Matches(string categoryKey, ItemMeta meta)
        => categoryKey == CategoryKeys.Any || (categorySets.TryGetValue(categoryKey, out var set) && set.Contains(meta.UiCategory));

    public ItemMeta Get(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var meta))
            return meta;

        meta = new ItemMeta { ItemId = itemId, Name = $"Item {itemId}" };
        if (items.TryGetRow(itemId, out var row))
        {
            meta.Name = row.Name.ExtractText();
            meta.StackSize = Math.Max(1, (int)row.StackSize);
            meta.IsUnique = row.IsUnique;
            meta.UiCategory = row.ItemUICategory.RowId;
            meta.IsGear = row.EquipSlotCategory.RowId != 0;
            meta.ItemLevel = (int)row.LevelItem.RowId;
            meta.CategoryOrder = categoryOrder.GetValueOrDefault(meta.UiCategory, int.MaxValue);
            meta.LevelKey = LevelKeyFor(itemId, meta);
        }

        cache[itemId] = meta;
        return meta;
    }

    /// <summary>
    /// Gathered items use the level they are gathered at, crafted items their recipe level.
    /// Gear and everything else (monster drops, vendor goods) get a level estimated from their item
    /// level, so all of them sort on one scale.
    /// </summary>
    private int LevelKeyFor(uint itemId, ItemMeta meta)
    {
        if (!meta.IsGear)
        {
            if (gatherLevels.TryGetValue(itemId, out var gathered))
                return gathered;

            if (craftLevels.TryGetValue(itemId, out var crafted))
                return crafted;
        }

        return levelByItemLevel[Math.Clamp(meta.ItemLevel, 0, levelByItemLevel.Length - 1)];
    }

    private void ReadLevels(ExcelModule excel)
    {
        static int Key(int level, int stars) => level * 10 + Math.Min(stars, 9);

        void Gathered(uint itemId, RowRef<GatheringItemLevelConvertTable> level)
        {
            if (itemId == 0 || itemId >= 1_000_000 || !level.IsValid || level.Value.GatheringItemLevel == 0)
                return;

            var key = Key(level.Value.GatheringItemLevel, level.Value.Stars);
            if (!gatherLevels.TryGetValue(itemId, out var existing) || key < existing)
                gatherLevels[itemId] = key;
        }

        foreach (var row in excel.GetSheet<GatheringItem>())
            Gathered(row.Item.RowId, row.GatheringItemLevel);

        foreach (var row in excel.GetSheet<FishParameter>())
            Gathered(row.Item.RowId, row.GatheringItemLevel);

        foreach (var row in excel.GetSheet<SpearfishingItem>())
            Gathered(row.Item.RowId, row.GatheringItemLevel);

        foreach (var row in excel.GetSheet<Recipe>())
        {
            var itemId = row.ItemResult.RowId;
            if (itemId == 0 || !row.RecipeLevelTable.IsValid || row.RecipeLevelTable.Value.ClassJobLevel == 0)
                continue;

            var key = Key(row.RecipeLevelTable.Value.ClassJobLevel, row.RecipeLevelTable.Value.Stars);
            if (!craftLevels.TryGetValue(itemId, out var existing) || key < existing)
                craftLevels[itemId] = key;
        }
    }

    /// <summary>
    /// For each item level, the typical level of materials that have one, never decreasing.
    /// </summary>
    private int[] BuildItemLevelScale()
    {
        var samples = new Dictionary<int, List<int>>();
        foreach (var (itemId, key) in gatherLevels.Concat(craftLevels))
        {
            if (!items.TryGetRow(itemId, out var row) || row.EquipSlotCategory.RowId != 0)
                continue;

            var itemLevel = (int)row.LevelItem.RowId;
            if (itemLevel <= 0)
                continue;

            if (!samples.TryGetValue(itemLevel, out var list))
                samples[itemLevel] = list = [];

            list.Add(key);
        }

        // Item level 1 is also what the game gives items that have no real level, so it says nothing.
        samples.Remove(1);

        // Fit a never-decreasing curve through the typical level at each item level. Neighbouring
        // points that are out of order are pooled, weighted by how many items back them, so a
        // handful of oddly levelled items cannot drag the whole scale up.
        var blocks = new List<(int FirstItemLevel, double Sum, int Weight)>();
        foreach (var (itemLevel, list) in samples.OrderBy(pair => pair.Key))
        {
            list.Sort();
            blocks.Add((itemLevel, (double)list[list.Count / 2] * list.Count, list.Count));
            while (blocks.Count > 1 && blocks[^2].Sum / blocks[^2].Weight > blocks[^1].Sum / blocks[^1].Weight)
            {
                var merged = (blocks[^2].FirstItemLevel, blocks[^2].Sum + blocks[^1].Sum, blocks[^2].Weight + blocks[^1].Weight);
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add(merged);
            }
        }

        var highest = samples.Count == 0 ? 1 : samples.Keys.Max();
        var scale = new int[highest + 1];
        var block = -1;
        var current = 10; // level 1
        for (var itemLevel = 0; itemLevel <= highest; itemLevel++)
        {
            while (block + 1 < blocks.Count && blocks[block + 1].FirstItemLevel <= itemLevel)
            {
                block++;
                current = Math.Max(current, (int)Math.Round(blocks[block].Sum / blocks[block].Weight));
            }

            scale[itemLevel] = current;
        }

        return scale;
    }

    /// <summary> For checking the scale by eye. </summary>
    public int EstimatedLevelKey(int itemLevel) => levelByItemLevel[Math.Clamp(itemLevel, 0, levelByItemLevel.Length - 1)];
}
