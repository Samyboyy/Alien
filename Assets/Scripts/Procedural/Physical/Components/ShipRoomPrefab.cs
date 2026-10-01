using System.Collections.Generic;
using UnityEngine;

/// <summary>The root of a greybox room prefab: which room definition it is for, its footprint, and explicit references to its sockets, anchors and bounds.</summary>
public class ShipRoomPrefab : MonoBehaviour
{
    [Tooltip("Unique per prefab, e.g. 'bridge.a'.")]
    public string variantId = "";
    [Tooltip("The room definition (RoomDefinition id) this prefab is for, e.g. 'bridge'.")]
    public string definitionId = "";
    public RoomCategory category;
    [Tooltip("Footprint in grid cells (x = width, y = depth). The pivot is the footprint's minimum corner.")]
    public Vector2Int footprint;
    public float cellSize = GreyboxScale.Cell;
    public float height = GreyboxScale.WallHeight;
    public RoomAcoustics acoustics;
    public RoomBounds bounds;
    public ConnectionSocket[] sockets = new ConnectionSocket[0];
    public RoomAnchor[] anchors = new RoomAnchor[0];

    /// <summary>The pure description of this prefab that placement works with.</summary>
    public RoomTemplate ToTemplate()
    {
        var t = new RoomTemplate { id = variantId, definitionId = definitionId, category = category, sizeX = footprint.x, sizeZ = footprint.y, acoustics = acoustics };
        foreach (var s in sockets) if (s != null) t.sockets.Add(s.ToSpec());
        foreach (var a in anchors)
            if (a != null) { t.anchorCounts[(int)a.kind]++; t.anchors.Add(a.ToInfo(transform)); }
        if (bounds != null) { t.boundsWidth = bounds.size.x; t.boundsDepth = bounds.size.z; }
        return t;
    }

    public ConnectionSocket Socket(int index) => sockets[index];

    public IEnumerable<RoomAnchor> Anchors(AnchorKind kind)
    {
        foreach (var a in anchors) if (a != null && a.kind == kind) yield return a;
    }
}
