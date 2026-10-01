using System;

// The standard connection-socket contract. A socket is a doorway in a room's outer wall: it sits on one boundary cell of the room's footprint
// and faces outwards. Two rooms connect when two sockets are compatible and aligned, either touching (a direct connection) or joined by a
// corridor of modular pieces. A socket not used by the logical graph is sealed with a wall module.

public enum SocketWidth : byte { Standard, Wide }
public enum SocketHeight : byte { Standard, Tall }

/// <summary>What the doorway belongs to. Only sockets of the same type connect.</summary>
public enum SocketConnectionType : byte { Standard, Service }

public enum DoorPolicy : byte { Forbidden, Permitted, Required }

/// <summary>How a socket may be joined to another.</summary>
[Flags]
public enum SocketSupport : byte
{
    None = 0,
    Corridor = 1,
    Airlock = 2,
    Direct = 4,
    All = Corridor | Airlock | Direct,
}

public enum ConnectionMode : byte { Direct, Corridor, Airlock }

/// <summary>One socket of a room template, in the template's own (unrotated) cells.</summary>
public sealed class SocketSpec
{
    /// <summary>Stable within its template, e.g. "n1" (north side, cell 1). Never derived from the object's name at run time.</summary>
    public string id = "";
    /// <summary>The boundary cell the socket opens from, local to the footprint.</summary>
    public Int2 cell;
    /// <summary>The side of the footprint the socket faces (outwards).</summary>
    public GridSide side;
    public SocketWidth width;
    public SocketHeight height;
    public SocketConnectionType type;
    public DoorPolicy door = DoorPolicy.Permitted;
    public SocketSupport support = SocketSupport.Corridor | SocketSupport.Direct;

    public SocketSpec Clone() => (SocketSpec)MemberwiseClone();
}

/// <summary>The result of comparing two sockets' properties (alignment is checked separately, by position).</summary>
public readonly struct SocketMatch
{
    public readonly bool ok;
    public readonly string reason;
    public readonly SocketSupport modes;
    public SocketMatch(bool ok, string reason, SocketSupport modes) { this.ok = ok; this.reason = reason; this.modes = modes; }
}

public static class SocketRules
{
    /// <summary>Same width and height class, same connection type, doors not forbidden on one side and required on the other, and at least one way to join them.</summary>
    public static SocketMatch Compatibility(SocketSpec a, SocketSpec b)
    {
        if (a.width != b.width) return new SocketMatch(false, $"width {a.width} does not match {b.width}", SocketSupport.None);
        if (a.height != b.height) return new SocketMatch(false, $"height {a.height} does not match {b.height}", SocketSupport.None);
        if (a.type != b.type) return new SocketMatch(false, $"connection type {a.type} does not match {b.type}", SocketSupport.None);
        if ((a.door == DoorPolicy.Forbidden && b.door == DoorPolicy.Required) || (a.door == DoorPolicy.Required && b.door == DoorPolicy.Forbidden))
            return new SocketMatch(false, "one socket forbids a door that the other requires", SocketSupport.None);
        var modes = a.support & b.support;
        if (modes == SocketSupport.None) return new SocketMatch(false, $"no shared connection mode ({a.support} vs {b.support})", SocketSupport.None);
        return new SocketMatch(true, null, modes);
    }

    /// <summary>
    /// A connection is a doorway when either side requires a door. A direct connection needs both sockets to allow doors if one requires it
    /// (a required door is built into the wall between them, so neither may forbid it).
    /// </summary>
    public static bool NeedsDoor(SocketSpec a, SocketSpec b) => a.door == DoorPolicy.Required || b.door == DoorPolicy.Required;

    public static bool Aligned(GridSide sideA, Int2 cellA, GridSide sideB, Int2 cellB, ConnectionMode mode)
    {
        // Direct: the two boundary cells are neighbours across the shared wall and the sockets face each other.
        // Corridor/Airlock alignment is the corridor's job: both sockets open into the corridor path's end cells.
        if (mode != ConnectionMode.Direct) return true;
        return sideA == Grid.Opposite(sideB) && cellA + Grid.Step(sideA) == cellB;
    }
}
