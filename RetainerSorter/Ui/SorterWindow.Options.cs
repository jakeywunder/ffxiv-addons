using System;
using System.Numerics;
using KamiToolKit.Nodes;
using RetainerSorter.Game;
using RetainerSorter.Sorting;

namespace RetainerSorter.Ui;

/// <summary> The Options tab: what each retainer is for, the pause between moves, and AutoRetainer. </summary>
internal sealed unsafe partial class SorterWindow
{
    private const int DelayStepMs = 50;

    private readonly TextNode?[] optionNames = new TextNode?[Layout.MaxRetainers];
    private readonly CheckboxNode?[] miscChecks = new CheckboxNode?[Layout.MaxRetainers];
    private readonly CheckboxNode?[] lockChecks = new CheckboxNode?[Layout.MaxRetainers];
    private NumericInputNode? delayMinInput;
    private NumericInputNode? delayMaxInput;
    private CheckboxNode? autoRetainerCheck;
    private CheckboxNode? commentsCheck;

    private void BuildOptionsTab(ResNode panel, Vector2 size)
    {
        Label(panel, new Vector2(0.0f, 0.0f), new Vector2(size.X, 20.0f),
            "These belong to the profile chosen on the Rules tab.", RowTone.Dim);

        for (var i = 0; i < Layout.MaxRetainers; i++)
        {
            var position = i;
            var y = 26.0f + i * RowHeight;
            optionNames[i] = Label(panel, new Vector2(0.0f, y), new Vector2(210.0f, 20.0f), string.Empty);
            optionNames[i]!.TextFlags = FFXIVClientStructs.FFXIV.Component.GUI.TextFlags.Ellipsis;

            miscChecks[i] = Checkbox(panel, new Vector2(216.0f, y), 220.0f, "Also takes unmatched items",
                value => SetRetainerOption(Profile.MiscRetainers, position, value),
                "Items that match no rule are gathered on the retainers ticked here.");

            lockChecks[i] = Checkbox(panel, new Vector2(446.0f, y), 150.0f, "Never open",
                value => SetRetainerOption(Profile.LockedRetainers, position, value),
                "The sorter leaves this retainer alone: nothing goes in or comes out.");
        }

        var below = 26.0f + Layout.MaxRetainers * RowHeight + 14.0f;
        Label(panel, new Vector2(0.0f, below), new Vector2(size.X, 20.0f), "Pause between item moves, in milliseconds. Each pause is a random time in this range.");

        below += 26.0f;
        Label(panel, new Vector2(0.0f, below + 5.0f), new Vector2(64.0f, 20.0f), "Shortest");
        delayMinInput = DelayInput(panel, new Vector2(66.0f, below), value =>
        {
            Config.DelayMinMs = value;
            Config.DelayMaxMs = Math.Max(Config.DelayMaxMs, value);
        });

        Label(panel, new Vector2(220.0f, below + 5.0f), new Vector2(60.0f, 20.0f), "Longest");
        delayMaxInput = DelayInput(panel, new Vector2(282.0f, below), value =>
        {
            Config.DelayMaxMs = value;
            Config.DelayMinMs = Math.Min(Config.DelayMinMs, value);
        });

        below += 44.0f;
        autoRetainerCheck = Checkbox(panel, new Vector2(0.0f, below), size.X - 10.0f,
            "Step aside for AutoRetainer when ventures finish",
            value =>
            {
                if (updating)
                    return;

                Config.LetAutoRetainerWork = value;
                Config.Save();
            },
            "Between two retainers, the sorter pauses and lets AutoRetainer collect and reassign finished ventures, then carries on.");

        var hint = Paragraph(panel, new Vector2(0.0f, below + 26.0f), new Vector2(size.X, 54.0f), RowTone.Dim);
        hint.String = "AutoRetainer is told to hold off while a retainer is being sorted, and the sorter always waits while AutoRetainer is busy. "
            + "With the box unticked, AutoRetainer stays paused until the sort ends.";

        below += 26.0f + 64.0f;
        commentsCheck = Checkbox(panel, new Vector2(0.0f, below), size.X - 10.0f,
            "Write each retainer's comment from what it holds",
            value =>
            {
                if (updating)
                    return;

                Config.WriteNotes = value;
                Config.Save();
                if (!value)
                    return;

                var changed = RetainerNotes.Refresh();
                Plugin.Print(changed == 0
                    ? "Retainer comments are already up to date, or no retainer has been scanned yet."
                    : $"Rewrote the comments of {changed} retainers. They show in the retainer list.");
            },
            "The comment is the note shown beside each retainer in the retainer list. This replaces comments you wrote yourself.");

        var commentsHint = Paragraph(panel, new Vector2(0.0f, below + 26.0f), new Vector2(size.X, 54.0f), RowTone.Dim);
        commentsHint.String = "For example \"Materia\", \"Materials 1-35\" or \"Housing, Misc\": kinds of item, never single items. "
            + "Comments are rewritten after every scan, sort and deposit, and when you close a retainer. Retainers marked \"Never open\" keep theirs.";
    }

    private NumericInputNode DelayInput(ResNode panel, Vector2 position, Action<int> apply)
    {
        var input = new NumericInputNode
        {
            Position = position,
            Size = new Vector2(130.0f, 28.0f),
            Min = 100,
            Max = 5000,
            Step = DelayStepMs,
            OnValueUpdate = value =>
            {
                if (updating)
                    return;

                apply(value);
                Config.Normalize();
                Config.Save();
                nextForecast = 0;
                Later(RefreshOptions);
            },
        };
        input.AttachNode(panel);
        return input;
    }

    private void ForgetOptionsNodes()
    {
        Array.Clear(optionNames);
        Array.Clear(miscChecks);
        Array.Clear(lockChecks);
        delayMinInput = null;
        delayMaxInput = null;
        autoRetainerCheck = null;
        commentsCheck = null;
    }

    private void SetRetainerOption(System.Collections.Generic.List<int> list, int position, bool value)
    {
        if (updating)
            return;

        SetMember(list, position, value);
        Config.Save();
        nextForecast = 0;
    }

    private void RefreshOptions()
    {
        if (!built || delayMinInput == null)
            return;

        var retainers = Retainers();
        var profile = Profile;
        Quietly(() =>
        {
            for (var i = 0; i < Layout.MaxRetainers; i++)
            {
                optionNames[i]!.String = $"{i + 1}. {RetainerLabel(retainers, i)}";
                miscChecks[i]!.IsChecked = profile.MiscRetainers.Contains(i);
                lockChecks[i]!.IsChecked = profile.LockedRetainers.Contains(i);
            }

            if (delayMinInput.Value != Config.DelayMinMs)
                delayMinInput.Value = Config.DelayMinMs;

            if (delayMaxInput!.Value != Config.DelayMaxMs)
                delayMaxInput.Value = Config.DelayMaxMs;

            autoRetainerCheck!.IsChecked = Config.LetAutoRetainerWork;
            commentsCheck!.IsChecked = Config.WriteNotes;
        });
    }
}
