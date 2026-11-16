using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using HuntingLogPlus.Data;

namespace HuntingLogPlus.Game;

/// <summary> Identifies one monster entry of one class's log. </summary>
public readonly record struct TargetKey(uint Job, int Rank, int TaskIndex, int MonsterIndex);

/// <summary>
/// One unfinished Hunting Log target. Instances are kept alive between refreshes so the
/// list can tell "same row, new numbers" apart from "different row".
/// </summary>
public sealed class TargetRow
{
    public required TargetKey Key { get; init; }
    public required HuntMonster Monster { get; init; }

    public uint Job => Key.Job;
    public int Rank => Key.Rank;

    /// <summary> Kills so far. </summary>
    public int Killed { get; set; }

    /// <summary> The zone to send the player to: the current zone when the monster lives there, otherwise its first one. </summary>
    public HuntLocation Location { get; set; } = null!;

    public bool InCurrentZone { get; set; }

    /// <summary> Yalms to the stored spot, or <see cref="float.MaxValue"/> when it cannot be measured. </summary>
    public float Distance { get; set; } = float.MaxValue;

    /// <summary> True when kills count right now: the target belongs to the current class or the Grand Company. </summary>
    public bool Creditable { get; set; }

    /// <summary> True for open-world targets in the player's zone while the player's position is known. </summary>
    public bool HasDistance => Distance < float.MaxValue;
}

/// <summary> Builds and orders the list of targets the player still needs. </summary>
internal sealed class TargetService(HuntData data)
{
    private readonly Dictionary<TargetKey, TargetRow> rows = new();

    /// <summary>
    /// Refreshes the unfinished targets from game memory.
    /// </summary>
    /// <param name="allClasses"> Every unlocked class, instead of only the current one. The Grand Company is always included. </param>
    public List<TargetRow> Refresh(bool allClasses)
    {
        var result = new List<TargetRow>();
        if (!HuntProgress.IsReady)
        {
            rows.Clear();
            return result;
        }

        var currentClass = HuntProgress.CurrentClass;
        var grandCompany = HuntProgress.CurrentGrandCompany;

        var jobs = new List<uint>();
        if (allClasses)
            jobs.AddRange(HuntProgress.ClassJobs.Where(HuntProgress.IsUnlocked));
        else if (currentClass != 0)
            jobs.Add(currentClass);

        if (grandCompany != 0)
            jobs.Add(grandCompany);

        var territory = (uint)Plugin.ClientState.TerritoryType;
        var player = Plugin.ObjectTable.LocalPlayer;
        var playerPosition = player == null ? (Vector2?)null : new Vector2(player.Position.X, player.Position.Z);

        var seen = new HashSet<TargetKey>();
        foreach (var job in jobs)
        {
            if (!data.JobRanks.TryGetValue(job, out var ranks))
                continue;

            var rank = HuntProgress.GetRank(job);
            if (rank < 0 || rank >= HuntProgress.RankCount(job) || rank >= ranks.Count)
                continue;

            var tasks = ranks[rank].Tasks;
            for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
            {
                var monsters = tasks[taskIndex].Monsters;
                for (var monsterIndex = 0; monsterIndex < monsters.Count; monsterIndex++)
                {
                    var monster = monsters[monsterIndex];
                    if (monster.Locations.Count == 0)
                        continue;

                    var killed = HuntProgress.GetKills(job, taskIndex, monsterIndex);
                    if (killed >= monster.Count)
                        continue;

                    var key = new TargetKey(job, rank, taskIndex, monsterIndex);
                    if (!rows.TryGetValue(key, out var row))
                    {
                        row = new TargetRow { Key = key, Monster = monster };
                        rows[key] = row;
                    }

                    row.Killed = killed;
                    row.Creditable = job == currentClass || job == grandCompany;
                    row.Location = monster.Locations.FirstOrDefault(location => location.Terri == territory) ?? monster.Locations[0];
                    row.InCurrentZone = row.Location.Terri == territory;
                    row.Distance = row.InCurrentZone && !row.Location.IsDuty && playerPosition != null
                        ? Vector2.Distance(playerPosition.Value, row.Location.WorldPosition)
                        : float.MaxValue;

                    seen.Add(key);
                    result.Add(row);
                }
            }
        }

        foreach (var stale in rows.Keys.Where(key => !seen.Contains(key)).ToArray())
            rows.Remove(stale);

        return result;
    }

    /// <summary>
    /// Orders targets by zone or by rank. With <paramref name="nearestFirst"/>, targets in the current
    /// zone come first by distance, then other zones, then dungeons.
    /// </summary>
    public static void Sort(List<TargetRow> targets, SortMode mode, bool nearestFirst)
    {
        targets.Sort((left, right) =>
        {
            if (nearestFirst)
            {
                var bucket = Bucket(left).CompareTo(Bucket(right));
                if (bucket != 0)
                    return bucket;

                if (Bucket(left) == 0)
                {
                    var distance = left.Distance.CompareTo(right.Distance);
                    if (distance != 0)
                        return distance;
                }
            }

            var byMode = mode == SortMode.Zone ? CompareZone(left, right) : CompareRank(left, right);
            return byMode != 0 ? byMode : CompareLogOrder(left, right);
        });
    }

    private static int Bucket(TargetRow row) => row.Location.IsDuty ? 2 : row.InCurrentZone ? 0 : 1;

    private static int CompareZone(TargetRow left, TargetRow right)
    {
        var duty = left.Location.IsDuty.CompareTo(right.Location.IsDuty);
        if (duty != 0)
            return duty;

        var zone = string.Compare(left.Location.SortName, right.Location.SortName, StringComparison.OrdinalIgnoreCase);
        return zone != 0 ? zone : string.Compare(left.Monster.DisplayName, right.Monster.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareRank(TargetRow left, TargetRow right) => left.Rank.CompareTo(right.Rank);

    /// <summary> The order the game's own log uses; also the final tie-breaker so sorting is stable. </summary>
    private static int CompareLogOrder(TargetRow left, TargetRow right)
    {
        var job = HuntProgress.SortOrder(left.Job).CompareTo(HuntProgress.SortOrder(right.Job));
        if (job != 0)
            return job;

        var rank = left.Rank.CompareTo(right.Rank);
        if (rank != 0)
            return rank;

        var task = left.Key.TaskIndex.CompareTo(right.Key.TaskIndex);
        return task != 0 ? task : left.Key.MonsterIndex.CompareTo(right.Key.MonsterIndex);
    }
}
