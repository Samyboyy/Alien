using System;
using System.Collections.Generic;

/// <summary>What a semantic anchor marks. Future systems (items, objectives, creature, vents, cameras, hazards) read these by kind, never by name.</summary>
public enum AnchorKind : byte
{
    DoorSocket, VentEntrance, VentNode, Item, Objective, Hiding, SearchPoint, PatrolPoint, CameraMount, Hazard, PlayerSpawn, CreatureSpawn,
}

/// <summary>The meaning of an item anchor: what may later be placed on it.</summary>
public enum ItemAnchorCategory : byte { General, Tool, Medical, Keycard, PowerPart, Supply, Document }

/// <summary>Acoustic character of a room: metadata for the future audio pass (reverb and ambience), not used by placement.</summary>
public enum RoomAcoustics : byte { SmallRoom, MediumRoom, LargeHall, Machinery, Corridor }

/// <summary>
/// The pure description of a physical room prefab that placement needs: the footprint in cells and the sockets. Built from a prefab
/// (ShipRoomPrefab.ToTemplate) or from a blueprint (RoomBlueprint.ToTemplate); both must agree, and a test checks that they do.
/// </summary>
public sealed class RoomTemplate
{
    public const int AnchorKindCount = 12;

    /// <summary>The variant id, unique per prefab, e.g. "bridge.a".</summary>
    public string id = "";
    /// <summary>The room definition (RoomSpec id) this variant is for, e.g. "bridge".</summary>
    public string definitionId = "";
    public RoomCategory category;
    public int sizeX, sizeZ;
    public readonly List<SocketSpec> sockets = new();
    public readonly int[] anchorCounts = new int[AnchorKindCount];
    /// <summary>Every anchor with its semantics and room-local position (the counts above are derived from it).</summary>
    public readonly List<AnchorInfo> anchors = new();
    /// <summary>The room's bounds in metres (the box used for overlap checks); 0 means the prefab has none.</summary>
    public float boundsWidth, boundsDepth;
    public RoomAcoustics acoustics;

    public int AnchorCount(AnchorKind k) => anchorCounts[(int)k];
    public int Area => sizeX * sizeZ;

    public int IndexOfSocket(string socketId)
    {
        for (int i = 0; i < sockets.Count; i++) if (sockets[i].id == socketId) return i;
        return -1;
    }

    /// <summary>The cell a socket opens from, after turning the footprint.</summary>
    public Int2 SocketCell(int socket, int rot) => Grid.RotateCell(sockets[socket].cell, sizeX, sizeZ, rot);
    public GridSide SocketSide(int socket, int rot) => Grid.Rotate(sockets[socket].side, rot);

    /// <summary>Sockets cannot be on the same cell and side, and every socket must sit on the footprint edge it claims to face.</summary>
    public List<string> Problems()
    {
        var list = new List<string>();
        if (sizeX < 1 || sizeZ < 1) list.Add($"{id}: footprint {sizeX}x{sizeZ} is not positive");
        var seen = new HashSet<string>();
        foreach (var s in sockets)
        {
            if (string.IsNullOrEmpty(s.id)) list.Add($"{id}: a socket has no id");
            else if (!seen.Add(s.id)) list.Add($"{id}: socket id '{s.id}' is used twice");
            bool onEdge = s.side switch
            {
                GridSide.North => s.cell.z == sizeZ - 1,
                GridSide.South => s.cell.z == 0,
                GridSide.East => s.cell.x == sizeX - 1,
                _ => s.cell.x == 0,
            };
            if (s.cell.x < 0 || s.cell.z < 0 || s.cell.x >= sizeX || s.cell.z >= sizeZ || !onEdge)
                list.Add($"{id}: socket '{s.id}' at {s.cell} does not sit on its {s.side} edge of a {sizeX}x{sizeZ} footprint");
        }
        for (int i = 0; i < sockets.Count; i++)
            for (int j = i + 1; j < sockets.Count; j++)
                if (sockets[i].cell == sockets[j].cell && sockets[i].side == sockets[j].side) list.Add($"{id}: sockets '{sockets[i].id}' and '{sockets[j].id}' overlap");
        return list;
    }
}

/// <summary>Which semantic anchors a room of a given definition must carry, derived from its definition's features. Missing ones are reported.</summary>
public static class AnchorRules
{
    static readonly HashSet<RoomCategory> NeedsHiding = new()
    {
        RoomCategory.CrewQuarters, RoomCategory.CargoBay, RoomCategory.Medbay, RoomCategory.MessHall, RoomCategory.Workshop,
        RoomCategory.SecondaryCargoHold, RoomCategory.MaintenanceRoom, RoomCategory.OfficersQuarters, RoomCategory.Engineering, RoomCategory.PlayerStart,
    };

    public static int Required(RoomSpec spec, RoomTemplate t, AnchorKind kind)
    {
        bool large = t.Area >= 24; // 96 square metres
        switch (kind)
        {
            case AnchorKind.PatrolPoint:
            case AnchorKind.SearchPoint: return large ? 2 : 1;
            case AnchorKind.PlayerSpawn: return spec.category == RoomCategory.PlayerStart ? 4 : 1;
            case AnchorKind.DoorSocket:
                int n = 0;
                foreach (var s in t.sockets) if (s.door != DoorPolicy.Forbidden) n++;
                return (spec.features & RoomFeatures.Doors) != 0 ? n : 0;
            case AnchorKind.VentEntrance:
            case AnchorKind.VentNode: return (spec.features & RoomFeatures.Vents) != 0 ? 1 : 0;
            case AnchorKind.Item: return (spec.features & RoomFeatures.ItemAnchors) != 0 ? (large ? 3 : 1) : 0;
            case AnchorKind.Objective: return (spec.features & RoomFeatures.Objectives) != 0 ? 1 : 0;
            case AnchorKind.CameraMount: return (spec.features & RoomFeatures.Cameras) != 0 ? 1 : 0;
            case AnchorKind.Hazard: return (spec.features & RoomFeatures.Hazards) != 0 ? 1 : 0;
            case AnchorKind.Hiding: return NeedsHiding.Contains(spec.category) ? 1 : 0;
            default: return 0;
        }
    }

    public static List<string> Missing(RoomSpec spec, RoomTemplate t)
    {
        var list = new List<string>();
        for (int k = 0; k < RoomTemplate.AnchorKindCount; k++)
        {
            int need = Required(spec, t, (AnchorKind)k);
            if (t.anchorCounts[k] < need) list.Add($"{t.id}: needs {need} {(AnchorKind)k} anchor(s), has {t.anchorCounts[k]}");
        }
        if (t.boundsWidth <= 0f || t.boundsDepth <= 0f) list.Add($"{t.id}: has no room bounds");
        return list;
    }
}
