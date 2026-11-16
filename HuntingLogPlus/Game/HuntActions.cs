using System.Linq;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using HuntingLogPlus.Data;

namespace HuntingLogPlus.Game;

/// <summary> What happens when a target is picked: map flag, teleport, or Duty Finder. </summary>
internal static unsafe class HuntActions
{
    private const string ChatTag = "Hunting Log+";
    private const uint FlagIconId = 60561;

    /// <summary>
    /// Opens the map with a flag on the monster's spot, or the Duty Finder for dungeon monsters.
    /// </summary>
    /// <param name="teleport"> Also teleport to the nearest aetheryte first (needs the Teleporter plugin). </param>
    public static void Activate(HuntLocation location, bool teleport)
    {
        if (location.IsDuty)
        {
            AgentContentsFinder.Instance()->OpenRegularDuty(location.DutyKey, false);
            return;
        }

        if (teleport)
        {
            if (Plugin.Teleport.IsAvailable)
                TeleportToNearestAetheryte(location);
            else
                Plugin.ChatGui.PrintError("Shift+Click teleport needs the Teleporter plugin.", ChatTag);
        }

        Plugin.GameGui.OpenMapWithMapLink(location.MapLink);
    }

    /// <summary> Places the map flag without opening the map, so it does not interrupt a fight. </summary>
    public static void FlagQuietly(HuntLocation location)
    {
        var world = location.WorldPosition;
        AgentMap.Instance()->SetFlagMapMarker(location.Terri, location.Map, new Vector3(world.X, 0.0f, world.Y), FlagIconId);
    }

    /// <summary> Prints a chat line ending in a clickable map link to the location. </summary>
    public static void PrintWithMapLink(string text, HuntLocation location)
    {
        var message = new SeStringBuilder()
            .AddText(text + " ")
            .Append(SeString.CreateMapLink(location.Terri, location.Map, location.X, location.Y))
            .Build();

        Plugin.ChatGui.Print(message, ChatTag);
    }

    public static void Print(string text) => Plugin.ChatGui.Print(text, ChatTag);

    // From Hunty, originally from HuntBuddy:
    // https://github.com/SheepGoMeh/HuntBuddy/blob/5a92e0e104839c30eaf398790dee32b793c3c53e/HuntBuddy/Location.cs#L520
    private static void TeleportToNearestAetheryte(HuntLocation location)
    {
        if (!Sheets.Map.TryGetRow(location.Map, out var map))
            return;

        var coords = new Vector2(location.X, location.Y);
        var nearestAetheryteId = Sheets.MapMarker
            .SelectMany(markers => markers)
            .Where(marker => marker.DataType == 3 && marker.RowId == map.MapMarkerRange)
            .Select(marker => new
            {
                Distance = Vector2.DistanceSquared(coords, MarkerToMapCoords(marker.X, marker.Y, map.SizeFactor)),
                marker.DataKey.RowId,
            })
            .MinBy(marker => marker.Distance)?.RowId ?? 0;

        foreach (var aetheryte in Sheets.Aetheryte)
        {
            if (!aetheryte.IsAetheryte || aetheryte.Territory.RowId != location.Terri || aetheryte.RowId != nearestAetheryteId)
                continue;

            Plugin.Teleport.UseTeleport(aetheryte.RowId);
            return;
        }
    }

    private static Vector2 MarkerToMapCoords(int x, int y, float scale)
    {
        var factor = scale / 100.0f;
        return new Vector2(RawToMap((int)((x - 1024) * factor * 1000.0f), scale), RawToMap((int)((y - 1024) * factor * 1000.0f), scale));
    }

    private static float RawToMap(int position, float scale)
    {
        var factor = scale / 100.0f;
        var scaled = (float)(position * (double)factor / 1000.0f);
        return (40.96f / factor * ((scaled + 1024.0f) / 2048.0f)) + 1.0f;
    }
}
