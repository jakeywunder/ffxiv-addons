using System;
using System.Collections.Generic;
using System.Linq;
using HuntingLogPlus.Data;

namespace HuntingLogPlus.Game;

/// <summary>
/// Watches Hunting Log progress and, when a target is finished, flags the next nearest one.
/// Only looks at targets that can be credited right now: the current class and the Grand Company.
/// </summary>
internal sealed class AutoAdvance(HuntData data)
{
    private const long CheckIntervalMs = 500;
    private const long QuietAfterLoginMs = 10000;

    // Has its own row cache so it never disturbs the rows the list is showing.
    private readonly TargetService targets = new(data);

    private HashSet<TargetKey>? previous;
    private uint previousClass;
    private uint previousGrandCompany;
    private long nextCheck;
    private long quietUntil = Environment.TickCount64 + QuietAfterLoginMs;

    /// <summary> Progress arrives a moment after login; ignore changes until it has settled. </summary>
    public void OnLogin()
    {
        previous = null;
        quietUntil = Environment.TickCount64 + QuietAfterLoginMs;
    }

    public void Tick()
    {
        var now = Environment.TickCount64;
        if (now < nextCheck)
            return;

        nextCheck = now + CheckIntervalMs;

        if (!Plugin.Config.AutoFlagNext || !HuntProgress.IsReady || now < quietUntil)
        {
            previous = null;
            return;
        }

        var currentClass = HuntProgress.CurrentClass;
        var grandCompany = HuntProgress.CurrentGrandCompany;
        var rows = targets.Refresh(false);
        var keys = rows.Select(row => row.Key).ToHashSet();

        if (previous != null && currentClass == previousClass && grandCompany == previousGrandCompany)
        {
            // One kill finishes one target (two if a class and the Grand Company share a monster).
            // Anything bigger is the game reloading its data, not the player hunting.
            var finished = previous.Count(key => !keys.Contains(key));
            if (finished is 1 or 2)
                FlagNext(rows);
        }

        previous = keys;
        previousClass = currentClass;
        previousGrandCompany = grandCompany;
    }

    private static void FlagNext(List<TargetRow> rows)
    {
        if (rows.Count == 0)
        {
            HuntActions.Print("Nothing left to hunt for your current class and Grand Company.");
            return;
        }

        var nearest = rows.Where(row => row.HasDistance).MinBy(row => row.Distance);
        if (nearest != null)
        {
            HuntActions.FlagQuietly(nearest.Location);
            HuntActions.PrintWithMapLink($"Next target: {nearest.Monster.DisplayName}, {nearest.Distance:F0}y away.", nearest.Location);
            return;
        }

        TargetService.Sort(rows, Plugin.Config.Sort, false);
        var elsewhere = rows.FirstOrDefault(row => !row.Location.IsDuty);
        if (elsewhere != null)
        {
            HuntActions.FlagQuietly(elsewhere.Location);
            HuntActions.PrintWithMapLink($"No targets left in this zone. Next: {elsewhere.Monster.DisplayName} in {elsewhere.Location.ZoneName}.", elsewhere.Location);
            return;
        }

        var dungeon = rows[0];
        HuntActions.Print($"Only dungeon targets are left. Next: {dungeon.Monster.DisplayName} in {dungeon.Location.DutyName}.");
    }
}
