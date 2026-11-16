using System;
using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace HuntingLogPlus.Game;

/// <summary>
/// How far one zone is from another, measured the way the game prices teleports: by where the
/// zones' aetherytes sit on the world map. Used to put nearby zones first.
/// </summary>
public sealed class ZoneDistance
{
    private readonly Dictionary<uint, List<Vector2>> aetherytes = new();
    private readonly Dictionary<(uint From, uint To), float> cache = new();

    public ZoneDistance(ExcelSheet<Aetheryte> aetheryteSheet, ExcelSheet<TerritoryType> territorySheet)
    {
        foreach (var aetheryte in aetheryteSheet)
        {
            if (aetheryte.IsAetheryte && aetheryte.Territory.RowId != 0)
                Add(aetheryte.Territory.RowId, aetheryte);
        }

        // A zone without an aetheryte of its own (a housing ward, half of a city) names the one it belongs to.
        foreach (var territory in territorySheet)
        {
            if (!aetherytes.ContainsKey(territory.RowId) && territory.Aetheryte.RowId != 0 && territory.Aetheryte.IsValid)
                Add(territory.RowId, territory.Aetheryte.Value);
        }
    }

    /// <summary>
    /// World-map distance between the two zones' closest aetherytes. 0 for the same zone, and
    /// <see cref="float.MaxValue"/> when either zone has no aetheryte to measure from (a dungeon, say).
    /// </summary>
    public float Between(uint fromTerritory, uint toTerritory)
    {
        if (fromTerritory == toTerritory)
            return 0.0f;

        var key = (fromTerritory, toTerritory);
        if (cache.TryGetValue(key, out var known))
            return known;

        var distance = float.MaxValue;
        if (aetherytes.TryGetValue(fromTerritory, out var starts) && aetherytes.TryGetValue(toTerritory, out var ends))
        {
            foreach (var start in starts)
            {
                foreach (var end in ends)
                    distance = MathF.Min(distance, Vector2.Distance(start, end));
            }
        }

        cache[key] = distance;
        return distance;
    }

    private void Add(uint territory, Aetheryte aetheryte)
    {
        // An aetheryte that is not on the world map has no position to measure from.
        if (aetheryte.AetherstreamX == 0 && aetheryte.AetherstreamY == 0)
            return;

        if (!aetherytes.TryGetValue(territory, out var positions))
        {
            positions = [];
            aetherytes[territory] = positions;
        }

        positions.Add(new Vector2(aetheryte.AetherstreamX, aetheryte.AetherstreamY));
    }
}
