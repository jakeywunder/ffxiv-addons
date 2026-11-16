using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Newtonsoft.Json;

namespace HuntingLogPlus.Data;

#pragma warning disable SeStringEvaluator

/// <summary> Hunty's monsters.json: class / Grand Company id -> ranks -> tasks -> monsters. </summary>
public sealed class HuntData
{
    public Dictionary<uint, List<HuntRank>> JobRanks { get; set; } = new();

    public static HuntData Load(string path)
        => JsonConvert.DeserializeObject<HuntData>(File.ReadAllText(path)) ?? new HuntData();

    /// <summary> Every monster entry in the data, in file order. </summary>
    public IEnumerable<HuntMonster> AllMonsters()
        => JobRanks.Values.SelectMany(ranks => ranks).SelectMany(rank => rank.Tasks).SelectMany(task => task.Monsters);
}

public sealed class HuntRank
{
    public List<HuntTask> Tasks { get; set; } = [];
}

public sealed class HuntTask
{
    public string Name { get; set; } = string.Empty;
    public List<HuntMonster> Monsters { get; set; } = [];
}

public sealed class HuntMonster
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte Count { get; set; }
    public uint Icon { get; set; }

    /// <summary> The game gives up to three zones for a monster. </summary>
    public List<HuntLocation> Locations { get; set; } = [];

    private string? displayName;

    /// <summary> Name in the game client's language, falling back to the English name from the data file. </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (displayName != null)
                return displayName;

            var evaluated = Plugin.Evaluator.EvaluateObjStr(ObjectKind.BattleNpc, Id);
            displayName = string.IsNullOrWhiteSpace(evaluated) ? Name : evaluated;
            return displayName;
        }
    }
}

public sealed class HuntLocation
{
    public uint Terri { get; set; }
    public uint Map { get; set; }

    /// <summary> Territory of the duty this monster lives in, or 0 for the open world. </summary>
    public uint Zone { get; set; }

    [JsonProperty("xCoord")] public float X { get; set; }
    [JsonProperty("yCoord")] public float Y { get; set; }

    private bool resolved;
    private string zoneName = string.Empty;
    private bool isDuty;
    private uint dutyKey;
    private string dutyName = string.Empty;
    private MapLinkPayload? mapLink;

    [JsonIgnore] public string ZoneName { get { Resolve(); return zoneName; } }
    [JsonIgnore] public bool IsDuty { get { Resolve(); return isDuty; } }
    [JsonIgnore] public uint DutyKey { get { Resolve(); return dutyKey; } }
    [JsonIgnore] public string DutyName { get { Resolve(); return dutyName; } }
    [JsonIgnore] public MapLinkPayload MapLink { get { Resolve(); return mapLink!; } }

    /// <summary> World X/Z of the stored spot, for distance checks against the player. </summary>
    [JsonIgnore] public Vector2 WorldPosition => new(MapLink.RawX / 1000.0f, MapLink.RawY / 1000.0f);

    /// <summary> What to show under the monster's name: the duty, or the zone with its coordinates. </summary>
    [JsonIgnore] public string Label => IsDuty ? DutyName : $"{ZoneName} {MapLink.CoordinateString}";

    /// <summary> Zone or duty name used for grouping when sorting by zone. </summary>
    [JsonIgnore] public string SortName => IsDuty ? DutyName : ZoneName;

    private void Resolve()
    {
        if (resolved)
            return;

        resolved = true;
        mapLink = new MapLinkPayload(Terri, Map, X, Y);

        if (Sheets.TerritoryType.TryGetRow(Terri, out var territory) && territory.Map.IsValid && territory.Map.Value.PlaceName.IsValid)
            zoneName = TitleCase(territory.Map.Value.PlaceName.Value.Name.ToString());

        if (Zone == 0)
            return;

        foreach (var content in Sheets.ContentFinderCondition)
        {
            if (content.TerritoryType.RowId != Zone || content.Name.IsEmpty)
                continue;

            isDuty = true;
            dutyName = TitleCase(content.Name.ToString());
            dutyKey = content.RowId;
            return;
        }
    }

    /// <summary> Upper-cases the first letter of every word, for duty names stored lower-case in the sheets. </summary>
    private static string TitleCase(string text)
    {
        var builder = new StringBuilder(text);
        var lastSpace = true;
        for (var i = 0; i < builder.Length; ++i)
        {
            if (builder[i] == ' ')
            {
                lastSpace = true;
            }
            else if (lastSpace)
            {
                lastSpace = false;
                builder[i] = char.ToUpperInvariant(builder[i]);
            }
        }

        return builder.ToString();
    }
}
