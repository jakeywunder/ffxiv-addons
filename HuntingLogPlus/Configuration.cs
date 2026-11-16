using System;
using Dalamud.Configuration;

namespace HuntingLogPlus;

public enum SortMode
{
    Zone = 0,
    Rank = 1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary> Show the combined "everything I still need" list instead of the game's per-class page. </summary>
    public bool ShowAllRemaining;

    /// <summary> Include every unlocked class, not only the current class and Grand Company. </summary>
    public bool AllClasses;

    /// <summary> Put targets in the current zone first, closest on top. </summary>
    public bool NearestFirst;

    /// <summary> When a target is finished, flag the next nearest one on the map. </summary>
    public bool AutoFlagNext;

    public SortMode Sort = SortMode.Zone;

    /// <summary> Log native Hunting Log events, to help tune icon clicks. Toggled with "/hlplus debug". </summary>
    public bool DebugLog;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
