using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>The root of a generated ship in a scene.</summary>
public class GeneratedShip : MonoBehaviour
{
    public int seed;
    [Tooltip("Seed of the escape scenario (which pods work, which doors are gated, where items may lie). The layout seed is 'seed'.")]
    public int scenarioSeed;
    public string scenarioFingerprint = "";
    public string graphFingerprint = "", layoutFingerprint = "", summary = "";
    public float cellSize = GreyboxScale.Cell;
    public PlacementSettings placementSettings = new();
    public List<NodeRecord> nodes = new();
    public List<ConnectionRecord> connections = new();
    public List<int> route = new();
    [Tooltip("Validation findings from the last check, and where they are (for the debug view).")]
    public List<string> issues = new();
    public List<Vector3> issueMarkers = new();
#if UNITY_EDITOR
    /// <summary>Developer-only solution report. Editor only (stripped from builds) and never shown to players.</summary>
    [TextArea(4, 30)] public string devSolutionReport = "";
#endif

    /// <summary>Rebuilds the logical graph from the records (rooms keep their ids, edges their ids).</summary>
    public ShipGraph ToGraph()
    {
        var g = new ShipGraph { seed = seed };
        foreach (var n in nodes)
        {
            int dot = n.id.LastIndexOf('.');
            int occurrence = dot >= 0 && int.TryParse(n.id.Substring(dot + 1), out int o) ? o : 0;
            g.AddNode(new RoomSpec { id = n.definitionId, displayName = n.displayName, category = n.category }, n.sector, n.deck, occurrence);
        }
        foreach (var c in connections) g.AddEdge(c.nodeA, c.nodeB); // connections are stored in edge order, so edge indices come back the same
        g.route.AddRange(route);
        return g;
    }

}
