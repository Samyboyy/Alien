using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Scene-view debug drawing for a generated ship (put on the GeneratedShip root by the builder). Everything is gizmos: nothing here exists in a
/// build. Rooms are drawn in their sector's colour, with ids; graph edges, sockets (green: connected, grey: sealed), corridors, anchors by kind and
/// the findings of the last validation (red) can each be switched on and off.
/// </summary>
[RequireComponent(typeof(GeneratedShip))]
public class ShipDebugView : MonoBehaviour
{
    public bool showBounds = true, showLabels = true, showGraphEdges = true, showSockets = true, showCorridors = true, showAnchors, showIssues = true;
    [Tooltip("Draw only rooms of this definition id (empty: all).")]
    public string onlyDefinition = "";

    public static readonly Color[] SectorColours =
    {
        new(0.35f, 0.6f, 0.95f), new(0.45f, 0.8f, 0.55f), new(0.95f, 0.75f, 0.35f), new(0.9f, 0.45f, 0.4f),
    };

    public static Color AnchorColour(AnchorKind k) => k switch
    {
        AnchorKind.DoorSocket => Color.white,
        AnchorKind.VentEntrance => new Color(0.6f, 0.6f, 0.9f),
        AnchorKind.VentNode => new Color(0.4f, 0.4f, 0.8f),
        AnchorKind.Item => Color.yellow,
        AnchorKind.Objective => Color.cyan,
        AnchorKind.Hiding => new Color(0.6f, 0.4f, 0.2f),
        AnchorKind.SearchPoint => new Color(1f, 0.5f, 0f),
        AnchorKind.PatrolPoint => Color.magenta,
        AnchorKind.CameraMount => Color.blue,
        AnchorKind.Hazard => Color.red,
        AnchorKind.PlayerSpawn => Color.green,
        _ => new Color(1f, 0f, 0.5f),
    };

    void OnDrawGizmos()
    {
        var ship = GetComponent<GeneratedShip>();
        if (ship == null) return;
        var rooms = GetComponentsInChildren<PlacedRoom>();
        foreach (var r in rooms)
        {
            if (!string.IsNullOrEmpty(onlyDefinition) && r.definitionId != onlyDefinition) continue;
            var col = SectorColours[(int)r.sector];
            if (showBounds && r.room != null && r.room.bounds != null)
            {
                var b = r.room.bounds.WorldBounds();
                Gizmos.color = col;
                Gizmos.DrawWireCube(b.center, b.size);
            }
            if (showSockets && r.room != null)
                foreach (var s in r.room.sockets)
                {
                    if (s == null) continue;
                    Gizmos.color = s.occupied ? Color.green : s.sealedOff ? Color.gray : Color.red; // red: neither connected nor sealed (a hole)
                    var p = s.transform.position + Vector3.up * 1.2f;
                    Gizmos.DrawLine(p, p + s.transform.forward * 1.2f);
                    Gizmos.DrawWireSphere(p, 0.2f);
                }
            if (showAnchors && r.room != null)
                foreach (var a in r.room.anchors)
                {
                    if (a == null) continue;
                    Gizmos.color = AnchorColour(a.kind);
                    Gizmos.DrawSphere(a.transform.position + Vector3.up * 0.1f, 0.12f);
                }
#if UNITY_EDITOR
            if (showLabels && r.room != null && r.room.bounds != null)
            {
                var b = r.room.bounds.WorldBounds();
                Handles.color = col;
                Handles.Label(b.center + Vector3.up * (b.extents.y + 0.5f), $"{r.nodeId}\n{r.sector}");
            }
#endif
        }

        if (showGraphEdges)
        {
            var byIndex = new System.Collections.Generic.Dictionary<int, PlacedRoom>();
            foreach (var r in rooms) byIndex[r.nodeIndex] = r;
            Gizmos.color = Color.white;
            foreach (var c in ship.connections)
                if (byIndex.TryGetValue(c.nodeA, out var a) && byIndex.TryGetValue(c.nodeB, out var b) && a.room != null && b.room != null && a.room.bounds != null && b.room.bounds != null)
                    Gizmos.DrawLine(a.room.bounds.WorldBounds().center + Vector3.up * 5f, b.room.bounds.WorldBounds().center + Vector3.up * 5f);
        }

        if (showCorridors)
        {
            Gizmos.color = new Color(0.8f, 0.8f, 0.9f, 0.6f);
            foreach (var c in GetComponentsInChildren<PlacedCorridor>())
                Gizmos.DrawWireCube(c.transform.position + Vector3.up * 2f, new Vector3(ship.cellSize, 4f, ship.cellSize) * 0.96f);
        }

        if (showIssues)
        {
            Gizmos.color = Color.red;
            foreach (var m in ship.issueMarkers)
            {
                Gizmos.DrawWireSphere(m + Vector3.up * 2f, 2.5f);
                Gizmos.DrawLine(m, m + Vector3.up * 8f);
            }
        }
    }
}
