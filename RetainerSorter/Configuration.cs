using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using RetainerSorter.Sorting;

namespace RetainerSorter;

/// <summary> A bag slot holding an item the sorter took out of a retainer and has not delivered yet. </summary>
public sealed class TransitSlot
{
    public int Slot;
    public uint ItemId;
    public bool Hq;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<Profile> Profiles = [];
    public int ActiveProfile;

    /// <summary> The pause between two item moves is a random time between these, in milliseconds. </summary>
    public int DelayMinMs = 300;
    public int DelayMaxMs = 650;

    /// <summary> Between retainers, step aside so AutoRetainer can collect finished ventures. </summary>
    public bool LetAutoRetainerWork = true;

    /// <summary> Items the player keeps on them: a deposit never takes these out of the bag. </summary>
    public List<uint> KeepWithMe = [];

    /// <summary> Keep each retainer's comment in the retainer list describing what it holds. </summary>
    public bool WriteNotes;

    /// <summary>
    /// Items in transit per character. Saved so a sort that was stopped, or cut short by a crash,
    /// still knows which bag slots are its own when it is resumed.
    /// </summary>
    public Dictionary<ulong, List<TransitSlot>> Transit = [];

    // Not saved: it is one of Profiles. If it were written out too, loading would pour its
    // rules into that same profile a second time.
    [Newtonsoft.Json.JsonIgnore]
    public Profile Active
    {
        get
        {
            if (Profiles.Count == 0)
                Profiles.Add(StarterProfile());

            ActiveProfile = Math.Clamp(ActiveProfile, 0, Profiles.Count - 1);
            return Profiles[ActiveProfile];
        }
    }

    /// <summary> Common rules with no retainers ticked yet; until one is ticked a rule does nothing. </summary>
    public static Profile StarterProfile() => new()
    {
        Name = "Default",
        Rules =
        [
            new Rule { Category = "c:58" },
            new Rule { Category = "g:materials" },
            new Rule { Category = "g:gear", Sort = SortKey.ItemLevel },
            new Rule { Category = "g:housing", Sort = SortKey.Name },
            new Rule { Category = "g:consumables" },
        ],
    };

    public void Normalize()
    {
        _ = Active;

        // A rule that repeats an earlier one never matches anything, so dropping it changes nothing.
        foreach (var profile in Profiles)
        {
            var seen = new HashSet<string>();
            profile.Rules.RemoveAll(rule => !seen.Add($"{rule.Category}|{rule.NameContains}|{string.Join(",", rule.Retainers)}|{rule.KeepInPlace}|{rule.Sort}"));
            profile.MiscRetainers = [.. new SortedSet<int>(profile.MiscRetainers)];
            profile.LockedRetainers = [.. new SortedSet<int>(profile.LockedRetainers)];
            foreach (var rule in profile.Rules)
                rule.Retainers = [.. new SortedSet<int>(rule.Retainers)];
        }

        DelayMinMs = Math.Clamp(DelayMinMs, 100, 5000);
        DelayMaxMs = Math.Clamp(DelayMaxMs, DelayMinMs, 10000);
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
