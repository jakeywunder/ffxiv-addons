using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using KamiToolKit.Nodes;
using Lumina.Text.ReadOnly;
using Newtonsoft.Json;
using RetainerSorter.Data;
using RetainerSorter.Sorting;

namespace RetainerSorter.Ui;

/// <summary> The Rules tab: profiles, the ordered list of rules, and the editor for the chosen rule. </summary>
internal sealed unsafe partial class SorterWindow
{
    private const string ExportPrefix = "RSORT1:";
    private const float EditorLabelWidth = 108.0f;

    private DropDownNode<int>? profileDropDown;
    private TextInputNode? profileNameInput;
    private ListNode<RuleRow, RuleRowNode>? ruleList;
    private DropDownNode<CategoryOption>? categoryDropDown;
    private TextInputNode? nameInput;
    private DropDownNode<SortKey>? sortDropDown;
    private CheckboxNode? keepCheckbox;
    private readonly CheckboxNode?[] ruleRetainerChecks = new CheckboxNode?[Layout.MaxRetainers];

    private List<RuleRow> shownRules = [];
    private int selectedRule;

    private Rule? SelectedRule
    {
        get
        {
            var rules = Profile.Rules;
            if (rules.Count == 0)
                return null;

            selectedRule = Math.Clamp(selectedRule, 0, rules.Count - 1);
            return rules[selectedRule];
        }
    }

    private void BuildRulesTab(ResNode panel, Vector2 size)
    {
        // Profile row.
        Label(panel, new Vector2(0.0f, 2.0f), new Vector2(54.0f, 22.0f), "Profile");
        profileDropDown = new DropDownNode<int>
        {
            Position = new Vector2(56.0f, 0.0f),
            Size = new Vector2(226.0f, 24.0f),
            MaxListOptions = 8,
            GetLabelFunction = index => index >= 0 && index < Config.Profiles.Count ? Config.Profiles[index].Name : string.Empty,
            OnOptionSelected = index => Later(() => SelectProfile(index)),
        };
        profileDropDown.AttachNode(panel);
        collapseDropDowns.Add(() => profileDropDown?.Collapse(false));

        var x = 292.0f;
        Button(panel, new Vector2(x, -2.0f), 64.0f, "New", () => Later(NewProfile), "Start a new profile with the common rules.");
        Button(panel, new Vector2(x + 68.0f, -2.0f), 64.0f, "Copy", () => Later(CopyProfile), "Duplicate this profile.");
        Button(panel, new Vector2(x + 136.0f, -2.0f), 64.0f, "Delete", () => Later(DeleteProfile), "Delete this profile.");
        Button(panel, new Vector2(x + 204.0f, -2.0f), 64.0f, "Export", ExportProfile, "Copy this profile to the clipboard as text, to share it.");
        Button(panel, new Vector2(x + 272.0f, -2.0f), 64.0f, "Import", () => Later(ImportProfile), "Add a profile from text on the clipboard.");

        Label(panel, new Vector2(0.0f, 36.0f), new Vector2(54.0f, 22.0f), "Name");
        profileNameInput = new TextInputNode
        {
            Position = new Vector2(56.0f, 32.0f),
            Size = new Vector2(226.0f, 28.0f),
            MaxCharacters = 40,
            OnInputComplete = text => RenameProfile(text),
            OnFocusLost = () => RenameProfile(profileNameInput?.String ?? default),
        };
        profileNameInput.AttachNode(panel);

        Label(panel, new Vector2(292.0f, 36.0f), new Vector2(size.X - 292.0f, 22.0f),
            "Rules are checked top to bottom. The first match wins.", RowTone.Dim);

        // The rules.
        ruleList = new ListNode<RuleRow, RuleRowNode>
        {
            Position = new Vector2(0.0f, 66.0f),
            Size = new Vector2(size.X, 162.0f),
            ItemSpacing = 1.0f,
            AutoResetScroll = false,
            OptionsList = [],
            OnItemSelected = row =>
            {
                if (row == null)
                    return;

                selectedRule = row.Index;
                Later(RefreshRules);
            },
        };
        ruleList.NoResultsTextNode.TextId = 0;
        ruleList.NoResultsTextNode.String = "No rules yet. Duplicates are still gathered together.";
        ruleList.AttachNode(panel);

        var y = 234.0f;
        Button(panel, new Vector2(0.0f, y), 100.0f, "Add rule", () => Later(AddRule));
        Button(panel, new Vector2(104.0f, y), 90.0f, "Remove", () => Later(RemoveRule));
        Button(panel, new Vector2(198.0f, y), 90.0f, "Move up", () => Later(() => MoveRule(-1)), "Rules higher up are checked first.");
        Button(panel, new Vector2(292.0f, y), 100.0f, "Move down", () => Later(() => MoveRule(1)));

        // The editor for the chosen rule.
        y = 272.0f;
        Label(panel, new Vector2(0.0f, y + 2.0f), new Vector2(EditorLabelWidth, 22.0f), "Items");
        categoryDropDown = new DropDownNode<CategoryOption>
        {
            Position = new Vector2(EditorLabelWidth, y),
            Size = new Vector2(300.0f, 24.0f),
            MaxListOptions = 12,
            GetLabelFunction = option => option.Label,
            Options = Plugin.Catalog.Options.ToList(),
            OnOptionSelected = option => EditRule(rule => rule.Category = option.Key),
        };
        categoryDropDown.AttachNode(panel);
        collapseDropDowns.Add(() => categoryDropDown?.Collapse(false));

        y += 30.0f;
        Label(panel, new Vector2(0.0f, y + 4.0f), new Vector2(EditorLabelWidth, 22.0f), "Name contains");
        nameInput = new TextInputNode
        {
            Position = new Vector2(EditorLabelWidth, y),
            Size = new Vector2(300.0f, 28.0f),
            MaxCharacters = 60,
            PlaceholderString = "optional",
            OnInputReceived = text => EditRule(rule => rule.NameContains = text.ExtractText().Trim(), save: false),
            OnInputComplete = _ => Config.Save(),
            OnFocusLost = Config.Save,
        };
        nameInput.AttachNode(panel);

        y += 34.0f;
        Label(panel, new Vector2(0.0f, y + 2.0f), new Vector2(EditorLabelWidth, 22.0f), "Fill in order of");
        sortDropDown = new DropDownNode<SortKey>
        {
            Position = new Vector2(EditorLabelWidth, y),
            Size = new Vector2(300.0f, 24.0f),
            MaxListOptions = 3,
            GetLabelFunction = SortLabel,
            Options = [SortKey.Level, SortKey.ItemLevel, SortKey.Name],
            OnOptionSelected = key => EditRule(rule => rule.Sort = key),
        };
        sortDropDown.AttachNode(panel);
        collapseDropDowns.Add(() => sortDropDown?.Collapse(false));

        y += 32.0f;
        keepCheckbox = Checkbox(panel, new Vector2(0.0f, y), 420.0f, "Leave these items wherever they are",
            value => EditRule(rule => rule.KeepInPlace = value),
            "Matching items are never moved. Use it to protect things from the rules below.");

        y += 28.0f;
        Label(panel, new Vector2(0.0f, y), new Vector2(size.X, 20.0f),
            "Put them on (filled in this order; overflow goes to the next retainer):", RowTone.Dim);

        y += 24.0f;
        var columnWidth = size.X / 2.0f;
        for (var i = 0; i < ruleRetainerChecks.Length; i++)
        {
            var position = i;
            var column = i / 5;
            var row = i % 5;
            ruleRetainerChecks[i] = Checkbox(panel, new Vector2(column * columnWidth, y + row * 22.0f), columnWidth - 10.0f, $"{i + 1}.",
                value => EditRule(rule => SetMember(rule.Retainers, position, value)));
        }
    }

    private void ForgetRulesNodes()
    {
        profileDropDown = null;
        profileNameInput = null;
        ruleList = null;
        categoryDropDown = null;
        nameInput = null;
        sortDropDown = null;
        keepCheckbox = null;
        Array.Clear(ruleRetainerChecks);
        shownRules = [];
    }

    private void RefreshRules()
    {
        if (!built || profileDropDown == null)
            return;

        Quietly(() =>
        {
            profileDropDown.Options = Enumerable.Range(0, Config.Profiles.Count).ToList();
            profileDropDown.SelectedOption = Config.ActiveProfile;
            profileNameInput!.String = Profile.Name;
        });

        RefreshRuleList();
        RefreshEditor();
    }

    private void RefreshRuleList()
    {
        if (ruleList == null)
            return;

        var retainers = Retainers();
        var rules = Profile.Rules;
        var chosen = SelectedRule == null ? -1 : selectedRule;
        var rows = new List<RuleRow>(rules.Count);
        for (var i = 0; i < rules.Count; i++)
            rows.Add(new RuleRow(i, $"{i + 1}. {RuleSubject(rules[i])}: {RuleDestination(rules[i], retainers)}", i == chosen));

        if (rows.SequenceEqual(shownRules))
            return;

        shownRules = rows;
        ruleList.OptionsList = rows;
        ruleList.Update();
    }

    private void RefreshEditor()
    {
        if (categoryDropDown == null)
            return;

        var rule = SelectedRule;
        var retainers = Retainers();
        Quietly(() =>
        {
            var enabled = rule != null;
            categoryDropDown.IsEnabled = enabled;
            nameInput!.IsEnabled = enabled;
            sortDropDown!.IsEnabled = enabled;
            keepCheckbox!.IsEnabled = enabled;

            if (rule != null)
            {
                categoryDropDown.SelectedOption = Plugin.Catalog.Options.FirstOrDefault(option => option.Key == rule.Category)
                    ?? new CategoryOption(rule.Category, "Unknown category");
                nameInput.String = rule.NameContains;
                sortDropDown.SelectedOption = rule.Sort;
                keepCheckbox.IsChecked = rule.KeepInPlace;
            }

            for (var i = 0; i < ruleRetainerChecks.Length; i++)
            {
                var check = ruleRetainerChecks[i]!;
                check.String = $"{i + 1}. {RetainerLabel(retainers, i)}";
                check.Width = panels[(int)Tab.Rules]!.Width / 2.0f - 10.0f;
                check.IsEnabled = rule is { KeepInPlace: false };
                check.IsChecked = rule != null && rule.Retainers.Contains(i);
            }
        });
    }

    /// <summary> Applies a change from the editor to the chosen rule. </summary>
    private void EditRule(Action<Rule> change, bool save = true)
    {
        if (updating || SelectedRule is not { } rule)
            return;

        change(rule);
        if (save)
            Config.Save();

        nextForecast = 0;
        Later(() =>
        {
            RefreshRuleList();

            // Only the retainer ticks depend on the other fields; leave the text box alone while typing.
            if (save)
                RefreshEditor();
        });
    }

    private void SelectProfile(int index)
    {
        Config.ActiveProfile = Math.Clamp(index, 0, Config.Profiles.Count - 1);
        selectedRule = 0;
        Config.Save();
        nextForecast = 0;
        RefreshRules();
        RefreshOptions();
    }

    private void NewProfile()
    {
        var profile = Configuration.StarterProfile();
        profile.Name = UniqueName("New profile");
        Config.Profiles.Add(profile);
        SelectProfile(Config.Profiles.Count - 1);
    }

    private void CopyProfile()
    {
        var profile = Profile.Clone();
        profile.Name = UniqueName($"{profile.Name} copy");
        Config.Profiles.Add(profile);
        SelectProfile(Config.Profiles.Count - 1);
    }

    private void DeleteProfile()
    {
        if (Config.Profiles.Count <= 1)
        {
            Plugin.Print("The last profile cannot be deleted.");
            return;
        }

        Config.Profiles.RemoveAt(Config.ActiveProfile);
        SelectProfile(Math.Max(0, Config.ActiveProfile - 1));
    }

    private void RenameProfile(ReadOnlySeString text)
    {
        if (updating)
            return;

        var name = text.ExtractText().Trim();
        if (name.Length == 0 || name == Profile.Name)
            return;

        Profile.Name = name;
        Config.Save();
        Later(RefreshRules);
    }

    private static string UniqueName(string wanted)
    {
        var name = wanted;
        for (var n = 2; Config.Profiles.Any(profile => profile.Name == name); n++)
            name = $"{wanted} {n}";

        return name;
    }

    private void ExportProfile()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Profile);
            ImGui.SetClipboardText(ExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
            Plugin.Print($"Profile \"{Profile.Name}\" was copied to the clipboard. Rules refer to retainers by their place in the list, so it works for anyone.");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Could not export the profile.");
            Plugin.Print("The profile could not be copied to the clipboard.");
        }
    }

    private void ImportProfile()
    {
        try
        {
            var text = ImGui.GetClipboardText()?.Trim() ?? string.Empty;
            if (!text.StartsWith(ExportPrefix, StringComparison.Ordinal))
            {
                Plugin.Print("The clipboard does not hold a Retainer Sorter profile. Copy the exported text first.");
                return;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(text[ExportPrefix.Length..]));
            var profile = Tidy(JsonConvert.DeserializeObject<Profile>(json));
            if (profile == null)
            {
                Plugin.Print("That profile text could not be read.");
                return;
            }

            profile.Name = UniqueName(profile.Name);
            Config.Profiles.Add(profile);
            SelectProfile(Config.Profiles.Count - 1);
            Plugin.Print($"Imported profile \"{profile.Name}\".");
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Could not import a profile from the clipboard.");
            Plugin.Print("That profile text could not be read.");
        }
    }

    /// <summary> Imported text comes from someone else, so everything in it is checked before use. </summary>
    private static Profile? Tidy(Profile? profile)
    {
        if (profile == null)
            return null;

        static List<int> Positions(List<int>? list)
            => (list ?? []).Where(position => position >= 0 && position < Layout.MaxRetainers).Distinct().OrderBy(position => position).ToList();

        var name = (profile.Name ?? string.Empty).Trim();
        profile.Name = name.Length == 0 ? "Imported profile" : name[..Math.Min(name.Length, 40)];
        profile.MiscRetainers = Positions(profile.MiscRetainers);
        profile.LockedRetainers = Positions(profile.LockedRetainers);
        profile.Rules = (profile.Rules ?? []).Where(rule => rule != null).Take(100).ToList();
        foreach (var rule in profile.Rules)
        {
            rule.Category = string.IsNullOrEmpty(rule.Category) ? CategoryKeys.Any : rule.Category;
            var contains = rule.NameContains ?? string.Empty;
            rule.NameContains = contains[..Math.Min(contains.Length, 60)];
            rule.Retainers = Positions(rule.Retainers);
            if (!Enum.IsDefined(rule.Sort))
                rule.Sort = SortKey.Level;
        }

        return profile;
    }

    private void AddRule()
    {
        Profile.Rules.Add(new Rule());
        selectedRule = Profile.Rules.Count - 1;
        RulesChanged();
    }

    private void RemoveRule()
    {
        if (SelectedRule == null)
            return;

        Profile.Rules.RemoveAt(selectedRule);
        RulesChanged();
    }

    private void MoveRule(int direction)
    {
        var rules = Profile.Rules;
        var target = selectedRule + direction;
        if (SelectedRule == null || target < 0 || target >= rules.Count)
            return;

        (rules[selectedRule], rules[target]) = (rules[target], rules[selectedRule]);
        selectedRule = target;
        RulesChanged();
    }

    private void RulesChanged()
    {
        Config.Save();
        nextForecast = 0;
        RefreshRuleList();
        RefreshEditor();
    }

    private static void SetMember(List<int> list, int value, bool member)
    {
        list.Remove(value);
        if (member)
        {
            list.Add(value);
            list.Sort();
        }
    }

    /// <summary> What a rule applies to, e.g. "Materia" or "Stone named "ore"". </summary>
    private static string RuleSubject(Rule rule)
    {
        var category = Plugin.Catalog.LabelFor(rule.Category);
        return rule.NameContains.Length == 0 ? category : $"{category} named \"{rule.NameContains}\"";
    }

    private static string RuleDestination(Rule rule, List<Game.RetainerEntry> retainers)
    {
        if (rule.KeepInPlace)
            return "left where they are";

        return rule.Retainers.Count == 0
            ? "no retainer chosen yet"
            : string.Join(", ", rule.Retainers.OrderBy(position => position).Select(position => RetainerLabel(retainers, position)));
    }

    private static ReadOnlySeString SortLabel(SortKey key) => key switch
    {
        SortKey.ItemLevel => "Item level",
        SortKey.Name => "Name",
        _ => "Level gathered or crafted at",
    };
}
