using System;
using System.Collections.Generic;
using System.Linq;

namespace RetainerSorter.Sorting;

/// <summary>
/// Describes what each retainer holds in a few words, for the game's retainer comment: the kinds
/// of item, never single items. "Materials 1-35", "Housing", "Gear, Misc".
/// </summary>
public static class Notes
{
    /// <summary> The game's own limit for a retainer comment. </summary>
    public const int MaxLength = 30;

    // Broad kinds for items no rule covers, checked in this order.
    private static readonly (string Key, string Label)[] BroadKinds =
    [
        ("c:58", "Materia"),
        ("g:materials", "Materials"),
        ("g:gear", "Gear"),
        ("g:housing", "Housing"),
        ("c:46", "Food"),
        ("c:44", "Medicine"),
        ("c:55", "Dyes"),
        ("g:collectibles", "Collectibles"),
        ("g:shipparts", "Ship parts"),
    ];

    private const string Everything = "Misc";

    private sealed class Tally
    {
        public required string Label;
        public Rule? Rule;
        public int Slots;
        public int LowLevel = int.MaxValue;
        public int HighLevel = int.MinValue;
        public int LowItemLevel = int.MaxValue;
        public int HighItemLevel = int.MinValue;
        public string FirstName = string.Empty;
        public string LastName = string.Empty;
    }

    /// <summary>
    /// One comment per retainer position, or null where the comment should be left alone
    /// (the retainer has not been read, cannot be summoned, or is marked never to be opened).
    /// </summary>
    /// <param name="shortLabel">A few-word name for a category key, e.g. "g:materials" to "Materials".</param>
    public static string?[] Build(WorldState world, Profile profile, IItemCatalog catalog, Func<string, string> shortLabel)
    {
        var planner = new Planner(catalog, profile);
        var notes = new string?[world.Retainers.Count];
        for (var r = 0; r < notes.Length; r++)
        {
            var retainer = world.Retainers[r];
            if (!retainer.Known || retainer.Unavailable || profile.LockedRetainers.Contains(r))
                continue;

            var tallies = new Dictionary<string, Tally>();
            foreach (var slot in retainer.Slots)
            {
                if (slot.IsEmpty)
                    continue;

                var meta = catalog.Get(slot.ItemId);
                var ruleIndex = planner.RuleFor(slot.ItemId);
                var rule = ruleIndex >= 0 ? profile.Rules[ruleIndex] : null;

                // A rule that takes "anything" says nothing about what the items are.
                if (rule is { Category: CategoryKeys.Any, NameContains.Length: 0 })
                    rule = null;

                var key = rule != null ? $"rule:{ruleIndex}" : BroadKind(meta, catalog);
                if (!tallies.TryGetValue(key, out var tally))
                {
                    tally = new Tally { Label = rule != null ? RuleLabel(rule, shortLabel) : key, Rule = rule };
                    tallies[key] = tally;
                }

                tally.Slots++;
                tally.LowLevel = Math.Min(tally.LowLevel, meta.LevelKey / 10);
                tally.HighLevel = Math.Max(tally.HighLevel, meta.LevelKey / 10);
                tally.LowItemLevel = Math.Min(tally.LowItemLevel, meta.ItemLevel);
                tally.HighItemLevel = Math.Max(tally.HighItemLevel, meta.ItemLevel);
                if (tally.FirstName.Length == 0 || string.Compare(meta.Name, tally.FirstName, StringComparison.OrdinalIgnoreCase) < 0)
                    tally.FirstName = meta.Name;

                if (tally.LastName.Length == 0 || string.Compare(meta.Name, tally.LastName, StringComparison.OrdinalIgnoreCase) > 0)
                    tally.LastName = meta.Name;
            }

            // Biggest share first; rules with the same label (say two "Lumber" rules) count as one.
            var parts = tallies.Values
                .OrderByDescending(tally => tally.Slots)
                .ThenBy(tally => tally.Label, StringComparer.OrdinalIgnoreCase)
                .Select(Describe)
                .Distinct()
                .ToList();

            notes[r] = Compose(parts);
        }

        return notes;
    }

    private static string BroadKind(ItemMeta meta, IItemCatalog catalog)
    {
        foreach (var (key, label) in BroadKinds)
        {
            if (catalog.Matches(key, meta))
                return label;
        }

        return Everything;
    }

    private static string RuleLabel(Rule rule, Func<string, string> shortLabel)
    {
        if (rule.NameContains.Length == 0)
            return shortLabel(rule.Category);

        return rule.Category == CategoryKeys.Any ? $"\"{rule.NameContains}\"" : $"{shortLabel(rule.Category)} \"{rule.NameContains}\"";
    }

    /// <summary>
    /// A rule spread over several retainers fills them in order, so each retainer's share is
    /// worth saying: the level range, item level range, or first letters it ended up with.
    /// </summary>
    private static string Describe(Tally tally)
    {
        if (tally.Rule is not { Retainers.Count: > 1 } rule)
            return tally.Label;

        switch (rule.Sort)
        {
            case SortKey.ItemLevel:
                return $"{tally.Label} i{Range(tally.LowItemLevel, tally.HighItemLevel)}";

            case SortKey.Name:
                var first = Initial(tally.FirstName);
                var last = Initial(tally.LastName);
                return first == last ? $"{tally.Label} {first}" : $"{tally.Label} {first}-{last}";

            default:
                return $"{tally.Label} {Range(tally.LowLevel, tally.HighLevel)}";
        }
    }

    private static string Range(int low, int high) => low == high ? $"{low}" : $"{low}-{high}";

    private static string Initial(string name) => name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();

    /// <summary> As many kinds as fit in the comment, then "+N" for the rest. </summary>
    private static string Compose(List<string> parts)
    {
        if (parts.Count == 0)
            return "Empty";

        var text = parts[0].Length > MaxLength ? parts[0][..MaxLength] : parts[0];
        var used = 1;
        for (var i = 1; i < parts.Count; i++)
        {
            var candidate = $"{text}, {parts[i]}";
            var rest = parts.Count - i - 1;
            var suffix = rest > 0 ? $" +{rest}" : string.Empty;
            if (candidate.Length + suffix.Length > MaxLength)
                break;

            text = candidate;
            used++;
        }

        var remaining = parts.Count - used;
        if (remaining > 0 && text.Length + $" +{remaining}".Length <= MaxLength)
            text += $" +{remaining}";

        return text;
    }
}
