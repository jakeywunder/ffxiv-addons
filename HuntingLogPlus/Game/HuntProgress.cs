using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using HuntingLogPlus.Data;

namespace HuntingLogPlus.Game;

/// <summary> Reads Hunting Log progress and class unlocks from the game. </summary>
internal static unsafe class HuntProgress
{
    /// <summary> Base classes that have a Hunting Log, in the order the game shows them. </summary>
    public static readonly uint[] ClassJobs = [1, 2, 3, 4, 5, 6, 7, 26, 29];

    private const uint GrandCompanyOffset = 10000;

    public static bool IsGrandCompany(uint job) => job > GrandCompanyOffset;

    /// <summary> Number of ranks in a log; reaching it means the log is finished. </summary>
    public static int RankCount(uint job) => IsGrandCompany(job) ? 3 : 5;

    /// <summary> Slot of a class or Grand Company inside the game's hunting log memory. </summary>
    public static int MemoryIndex(uint job) => job switch
    {
        1 => 0,
        2 => 1,
        3 => 2,
        4 => 3,
        5 => 4,
        6 => 5,
        7 => 6,
        26 => 7,
        10001 => 8,
        10002 => 9,
        10003 => 10,
        29 => 11,
        _ => -1,
    };

    public static uint JobFromMemoryIndex(int index) => index switch
    {
        0 => 1,
        1 => 2,
        2 => 3,
        3 => 4,
        4 => 5,
        5 => 6,
        6 => 7,
        7 => 26,
        8 => 10001,
        9 => 10002,
        10 => 10003,
        11 => 29,
        _ => 0,
    };

    /// <summary> Position used when ordering by class: classes in game order, Grand Company last. </summary>
    public static int SortOrder(uint job)
    {
        var index = Array.IndexOf(ClassJobs, job);
        return index >= 0 ? index : ClassJobs.Length;
    }

    public static bool IsReady => Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded;

    /// <summary> Hunting log key of the player's Grand Company, or 0 when they have none. </summary>
    public static uint CurrentGrandCompany
    {
        get
        {
            var company = Plugin.PlayerState.GrandCompany.RowId;
            return company == 0 ? 0 : company + GrandCompanyOffset;
        }
    }

    /// <summary> The base class of the current job (Gladiator for Paladin), or 0 if it has no Hunting Log. </summary>
    public static uint CurrentClass
    {
        get
        {
            var classJob = Plugin.PlayerState.ClassJob;
            if (!classJob.IsValid)
                return 0;

            var parent = classJob.Value.ClassJobParent.RowId;
            var baseClass = parent == 0 ? classJob.RowId : parent;
            return Array.IndexOf(ClassJobs, baseClass) >= 0 ? baseClass : 0;
        }
    }

    /// <summary> A class counts as unlocked once it has a level. </summary>
    public static bool IsUnlocked(uint classJob)
        => Sheets.ClassJob.TryGetRow(classJob, out var row) && Plugin.PlayerState.GetClassJobLevel(row) > 0;

    /// <summary> Zero-based rank the player is currently working on; equals <see cref="RankCount"/> when finished. </summary>
    public static int GetRank(uint job)
    {
        var index = MemoryIndex(job);
        if (index < 0)
            return int.MaxValue;

        var ranks = MonsterNoteManager.Instance()->RankData;
        return index < ranks.Length ? ranks[index].Rank : int.MaxValue;
    }

    /// <summary> Kills recorded for one monster of the rank the player is currently on. </summary>
    public static int GetKills(uint job, int taskIndex, int monsterIndex)
    {
        var index = MemoryIndex(job);
        if (index < 0)
            return 0;

        var ranks = MonsterNoteManager.Instance()->RankData;
        if (index >= ranks.Length)
            return 0;

        var tasks = ranks[index].RankData;
        if (taskIndex >= tasks.Length)
            return 0;

        var counts = tasks[taskIndex].Counts;
        return monsterIndex < counts.Length ? counts[monsterIndex] : 0;
    }
}
