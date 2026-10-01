using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// The greybox prefab set placement draws from: the room prefabs and the modular corridor and wall pieces. Alien > Procedural Ship > Build
/// Greybox Library creates the default set; final hand-authored assets later replace entries here without touching the generator.
/// </summary>
[CreateAssetMenu(menuName = "Alien/Procedural Ship/Greybox Library", fileName = "GreyboxLibrary")]
public class GreyboxLibrary : ScriptableObject
{
    public List<ShipRoomPrefab> rooms = new();
    [Header("Corridor modules (one 2 m cell; opens: straight N+S, corner N+E, tee N+E+S, cross all, dead end N)")]
    public GameObject corridorStraight, corridorCorner, corridorTee, corridorCross, corridorDeadEnd;
    [Header("Closes an unused doorway")]
    public GameObject wallSeal;

    public GameObject Corridor(CorridorPieceKind kind) => kind switch
    {
        CorridorPieceKind.Straight => corridorStraight,
        CorridorPieceKind.Corner => corridorCorner,
        CorridorPieceKind.Tee => corridorTee,
        CorridorPieceKind.Cross => corridorCross,
        _ => corridorDeadEnd,
    };

    public ShipRoomPrefab Find(string variantId) => rooms.FirstOrDefault(r => r != null && r.variantId == variantId);

    /// <summary>The templates of every room prefab in the library, read from the prefabs themselves.</summary>
    public TemplateLibrary BuildTemplates()
    {
        var lib = new TemplateLibrary();
        foreach (var r in rooms) if (r != null) lib.Add(r.ToTemplate());
        return lib;
    }

    /// <summary>Everything that would stop placement or leave a room unusable: empty slots, a missing module, a definition with no prefab, malformed sockets, missing anchors or bounds.</summary>
    public List<string> Problems(IEnumerable<RoomSpec> catalogue)
    {
        var list = new List<string>();
        int empty = rooms.Count(r => r == null);
        if (empty > 0) list.Add($"{empty} empty room slot(s) in the library");
        foreach (CorridorPieceKind k in System.Enum.GetValues(typeof(CorridorPieceKind)))
            if (Corridor(k) == null) list.Add($"no corridor module for {k}");
        if (wallSeal == null) list.Add("no wall seal module");
        foreach (var r in rooms.Where(r => r != null))
        {
            if (r.bounds == null) list.Add($"{r.variantId}: no RoomBounds");
            if (r.sockets.Any(s => s == null) || r.anchors.Any(a => a == null)) list.Add($"{r.variantId}: a socket or anchor reference is empty");
            if (r.footprint.x < 1 || r.footprint.y < 1) list.Add($"{r.variantId}: footprint {r.footprint} is not positive");
        }
        list.AddRange(BuildTemplates().Problems(catalogue));
        // Prefabs built before semantic anchors existed have none: placement of items and consoles needs them.
        if (rooms.Count > 0 && !rooms.Any(r => r != null && r.anchors.Any(a => a != null && a.semantic != AnchorSemantic.None)))
            list.Add("the room prefabs carry no semantic anchors (they predate them): run Alien > Procedural Ship > Rebuild Greybox Library (replace prefabs)");
        foreach (var r in rooms.Where(r => r != null && r.definitionId == "escape_pod_bay"))
            if (!r.anchors.Any(a => a != null && a.semantic == AnchorSemantic.PodBerth)) list.Add($"{r.variantId}: no PodBerth anchor (rebuild the greybox library)");
        return list;
    }
}
