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

    /// <summary> The spot to send the player to: in the current zone when the monster lives there, otherwise in the nearest of its zones. </summary>
    public HuntLocation Location { get; set; } = null!;

    public bool InCurrentZone { get; set; }

    /// <summary>
    /// How far the target's zone is from the player's zone on the world map (see <see cref="Game.ZoneDistance"/>).
    /// 0 in the current zone, <see cref="float.MaxValue"/> when it cannot be measured.
    /// </summary>
    public float ZoneDistance { get; set; } = float.MaxValue;

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
    private static readonly ZoneDistance Zones = new(Sheets.Aetheryte, Sheets.TerritoryType);

    private readonly Dictionary<TargetKey, TargetRow> rows = new();

    /// <summary> The logs whose kills count right now: the current class and the Grand Company. </summary>
    public static List<uint> CreditableJobs()
    {
        var jobs = new List<uint>();
        if (!HuntProgress.IsReady)
            return jobs;

        var currentClass = HuntProgress.CurrentClass;
        if (currentClass != 0)
            jobs.Add(currentClass);

        var grandCompany = HuntProgress.CurrentGrandCompany;
        if (grandCompany != 0)
            jobs.Add(grandCompany);

        return jobs;
    }

    /// <summary> Every class the player has unlocked, plus the Grand Company. </summary>
    public static List<uint> AllJobs()
    {
        var jobs = new List<uint>();
        if (!HuntProgress.IsReady)
            return jobs;

        jobs.AddRange(HuntProgress.ClassJobs.Where(HuntProgress.IsUnlocked));

        var grandCompany = HuntProgress.CurrentGrandCompany;
        if (grandCompany != 0)
            jobs.Add(grandCompany);

        return jobs;
    }

    /// <summary>
    /// Refreshes the unfinished targets from game memory.
    /// </summary>
    /// <param name="jobs"> The classes and Grand Company whose logs to read. </param>
    public List<TargetRow> Refresh(IReadOnlyList<uint> jobs)
    {
        var result = new List<TargetRow>();
        if (!HuntProgress.IsReady)
        {
            rows.Clear();
            return result;
        }

        var currentClass = HuntProgress.CurrentClass;
        var grandCompany = HuntProgress.CurrentGrandCompany;

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
                    row.Location = PickLocation(monster, territory);
                    row.InCurrentZone = row.Location.Terri == territory;
                    row.ZoneDistance = row.Location.IsDuty ? float.MaxValue : Zones.Between(territory, row.Location.Terri);
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
    /// The spot to send the player to for a monster that can live in several zones: the one in the
    /// current zone if there is one, otherwise the one in the nearest zone.
    /// </summary>
    private static HuntLocation PickLocation(HuntMonster monster, uint territory)
    {
        HuntLocation? nearest = null;
        var nearestDistance = float.MaxValue;

        foreach (var location in monster.Locations)
        {
            if (location.Terri == territory)
                return location;

            if (location.IsDuty)
                continue;

            var distance = Zones.Between(territory, location.Terri);
            if (nearest == null || distance < nearestDistance)
            {
                nearest = location;
                nearestDistance = distance;
            }
        }

        return nearest ?? monster.Locations[0];
    }

    /// <summary>
    /// Orders targets by zone or by rank. With <paramref name="nearestFirst"/>, targets in the current
    /// zone come first by distance, then other zones from the nearest to the farthest, then dungeons.
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

                // Inside the current zone the yalms decide; between other zones, how far the zone is.
                var distance = Bucket(left) switch
                {
                    0 => left.Distance.CompareTo(right.Distance),
                    1 => left.ZoneDistance.CompareTo(right.ZoneDistance),
                    _ => 0,
                };
                if (distance != 0)
                    return distance;
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
