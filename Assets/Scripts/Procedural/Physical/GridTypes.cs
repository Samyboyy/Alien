using System;

// Pure grid maths for the physical ship (no Unity types, so placement is testable outside the editor).
// World convention, matching Unity: +X is east, +Z is north, yaw turns clockwise seen from above (north -> east). One grid cell is
// PlacementSettings.cellSize metres square. The ship's long axis is X: sector Forward (the bow) at low X, Industrial (the stern) at high X.

/// <summary>A cell on one deck's grid.</summary>
public readonly struct Int2 : IEquatable<Int2>
{
    public readonly int x, z;
    public Int2(int x, int z) { this.x = x; this.z = z; }

    public bool Equals(Int2 o) => x == o.x && z == o.z;
    public override bool Equals(object o) => o is Int2 i && Equals(i);
    public override int GetHashCode() => x * 73856093 ^ z * 19349663;
    public override string ToString() => $"({x},{z})";
    public static bool operator ==(Int2 a, Int2 b) => a.Equals(b);
    public static bool operator !=(Int2 a, Int2 b) => !a.Equals(b);
    public static Int2 operator +(Int2 a, Int2 b) => new(a.x + b.x, a.z + b.z);
    public static Int2 operator -(Int2 a, Int2 b) => new(a.x - b.x, a.z - b.z);
    public static Int2 operator *(Int2 a, int k) => new(a.x * k, a.z * k);

    public int Manhattan(Int2 o) => Math.Abs(x - o.x) + Math.Abs(z - o.z);
}

/// <summary>The four cell sides. The numeric order is clockwise from north, so a quarter turn is "+1".</summary>
public enum GridSide : byte { North, East, South, West }

public static class Grid
{
    public static GridSide Rotate(GridSide s, int quarterTurns) => (GridSide)(((int)s + quarterTurns % 4 + 4) & 3);
    public static GridSide Opposite(GridSide s) => Rotate(s, 2);

    public static Int2 Step(GridSide s) => s switch
    {
        GridSide.North => new Int2(0, 1),
        GridSide.East => new Int2(1, 0),
        GridSide.South => new Int2(0, -1),
        _ => new Int2(-1, 0),
    };

    /// <summary>Bit for a side in a corridor opening mask (N=1, E=2, S=4, W=8).</summary>
    public static int Bit(GridSide s) => 1 << (int)s;

    public static int RotateMask(int mask, int quarterTurns)
    {
        int r = 0;
        for (int s = 0; s < 4; s++)
            if ((mask & (1 << s)) != 0) r |= 1 << (int)Rotate((GridSide)s, quarterTurns);
        return r;
    }

    /// <summary>Footprint size after <paramref name="quarterTurns"/> clockwise quarter turns.</summary>
    public static void RotatedSize(int sizeX, int sizeZ, int quarterTurns, out int sx, out int sz)
    {
        bool swap = (quarterTurns & 1) != 0;
        sx = swap ? sizeZ : sizeX;
        sz = swap ? sizeX : sizeZ;
    }

    /// <summary>
    /// A cell of a footprint of the given size, after turning the footprint clockwise about its own minimum corner and moving it back so its
    /// minimum corner is the origin again. One quarter turn maps (x, z) to (z, sizeX - 1 - x).
    /// </summary>
    public static Int2 RotateCell(Int2 c, int sizeX, int sizeZ, int quarterTurns)
    {
        int k = ((quarterTurns % 4) + 4) % 4;
        for (int i = 0; i < k; i++)
        {
            c = new Int2(c.z, sizeX - 1 - c.x);
            (sizeX, sizeZ) = (sizeZ, sizeX);
        }
        return c;
    }

    /// <summary>
    /// Where the minimum corner of a prefab whose pivot is its own minimum corner must be put (in cells, relative to the placed footprint's
    /// minimum corner) after the prefab is yawed by 90 * quarterTurns degrees about that pivot. Used to instantiate rotated rooms.
    /// </summary>
    public static Int2 PivotOffset(int sizeX, int sizeZ, int quarterTurns) => (((quarterTurns % 4) + 4) % 4) switch
    {
        0 => new Int2(0, 0),
        1 => new Int2(0, sizeX),
        2 => new Int2(sizeX, sizeZ),
        _ => new Int2(sizeZ, 0),
    };

    /// <summary>The same turn as <see cref="RotateCell"/>, done with continuous coordinates in cell units (a point in [0,sizeX] x [0,sizeZ]).</summary>
    public static void RotatePoint(float x, float z, int sizeX, int sizeZ, int quarterTurns, out float rx, out float rz)
    {
        int k = ((quarterTurns % 4) + 4) % 4;
        for (int i = 0; i < k; i++)
        {
            (x, z) = (z, sizeX - x);
            (sizeX, sizeZ) = (sizeZ, sizeX);
        }
        rx = x;
        rz = z;
    }
}
