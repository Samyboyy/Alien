using UnityEngine;

/// <summary>Grid to world conversions for a placed ship. One cell is cellSize metres; deck d sits d * DeckHeight above deck 0.</summary>
public static class LayoutWorld
{
    public static Vector3 CellCentre(Int2 c, int deck, float cellSize) => new((c.x + 0.5f) * cellSize, deck * GreyboxScale.DeckHeight, (c.z + 0.5f) * cellSize);

    /// <summary>
    /// Where a room prefab's pivot (the minimum corner of its unrotated footprint) goes so that, yawed by 90 * rotation degrees about the pivot,
    /// it covers exactly the placed footprint.
    /// </summary>
    public static Vector3 RoomPivot(RoomPlacement r, float cellSize)
    {
        var off = Grid.PivotOffset(r.template.sizeX, r.template.sizeZ, r.rot);
        return new Vector3((r.origin.x + off.x) * cellSize, r.deck * GreyboxScale.DeckHeight, (r.origin.z + off.z) * cellSize);
    }

    public static Quaternion RoomRotation(RoomPlacement r) => Quaternion.Euler(0f, 90f * r.rot, 0f);

    /// <summary>The placed footprint as a world box (full height of the room).</summary>
    public static Bounds RoomBox(RoomPlacement r, float cellSize)
    {
        var min = new Vector3(r.origin.x * cellSize, r.deck * GreyboxScale.DeckHeight, r.origin.z * cellSize);
        var size = new Vector3(r.SizeX * cellSize, GreyboxScale.WallHeight, r.SizeZ * cellSize);
        return new Bounds(min + size * 0.5f, size);
    }

    public static Bounds CellBox(Int2 c, int deck, float cellSize)
    {
        var centre = CellCentre(c, deck, cellSize);
        return new Bounds(centre + Vector3.up * GreyboxScale.WallHeight * 0.5f, new Vector3(cellSize, GreyboxScale.WallHeight, cellSize));
    }

    public static Vector3 ToVector(Vector2Int c, int deck, float cellSize) => CellCentre(new Int2(c.x, c.y), deck, cellSize);
}
