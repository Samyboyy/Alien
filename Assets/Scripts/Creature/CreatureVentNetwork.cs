using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The creature-only duct network: entrances plus junctions, joined by authored runs (each a real polyline of waypoints with a
/// calculated length). The creature travels these runs continuously at its vent speed; nothing here moves it or teleports it.
/// Not connected to the player crawlspace. Built into a pure VentGraph once, when enabled.
/// </summary>
public class CreatureVentNetwork : MonoBehaviour
{
    [System.Serializable]
    public class Edge
    {
        public Transform from, to;
        [Tooltip("Waypoints between the two ends, in order from 'from' to 'to'")] public Transform[] via = new Transform[0];
    }

    public static CreatureVentNetwork Instance { get; private set; }

    public CreatureVentEntrance[] entrances = new CreatureVentEntrance[0];
    [Tooltip("Junction nodes (node index = entrances.Length + position here)")] public Transform[] junctions = new Transform[0];
    public Edge[] edges = new Edge[0];

    public VentGraph Graph { get; private set; }
    Vector3[] nodePos = new Vector3[0];
    readonly List<List<Vector3>> polylines = new(); // per graph edge, from -> to
    readonly Dictionary<Transform, int> nodeOf = new();

    public int EntranceCount => entrances.Length;
    public bool Ready => Graph != null && entrances.Length > 1;

    void OnEnable()
    {
        Instance = this;
        Build();
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    public void Build()
    {
        nodeOf.Clear();
        polylines.Clear();
        int n = entrances.Length + junctions.Length;
        nodePos = new Vector3[n];
        for (int i = 0; i < entrances.Length; i++)
        {
            var e = entrances[i];
            if (e == null || e.top == null) continue;
            nodeOf[e.top] = i;
            nodePos[i] = e.top.position;
        }
        for (int j = 0; j < junctions.Length; j++)
        {
            if (junctions[j] == null) continue;
            nodeOf[junctions[j]] = entrances.Length + j;
            nodePos[entrances.Length + j] = junctions[j].position;
        }
        Graph = new VentGraph(n);
        foreach (var edge in edges)
        {
            if (edge == null || edge.from == null || edge.to == null || !nodeOf.TryGetValue(edge.from, out int a) || !nodeOf.TryGetValue(edge.to, out int b))
            {
                Debug.LogWarning("Vent network: an edge has a missing or unknown end and is ignored.", this);
                continue;
            }
            var line = new List<Vector3> { nodePos[a] };
            foreach (var v in edge.via) if (v != null) line.Add(v.position);
            line.Add(nodePos[b]);
            Graph.AddEdge(a, b, Length(line));
            polylines.Add(line);
        }
    }

    public bool IsJunction(int node) => node >= entrances.Length;

    public Vector3 NodePosition(int node) => nodePos[node];

    /// <summary>
    /// Waypoints from node <paramref name="from"/> to node <paramref name="to"/> along the shortest duct route, starting with the
    /// first node. <paramref name="junctionAt"/> gets (index into points, node) for every junction passed through (where the creature
    /// may re-plan). False when the nodes are not connected.
    /// </summary>
    public bool Route(int from, int to, List<Vector3> points, List<(int index, int node)> junctionAt, out float length)
    {
        points.Clear();
        junctionAt?.Clear();
        var nodes = new List<int>();
        var edgeIds = new List<int>();
        if (Graph == null || !Graph.Route(from, to, nodes, edgeIds, out length)) { length = 0f; return false; }
        points.Add(nodePos[from]);
        for (int k = 0; k < edgeIds.Count; k++)
        {
            var (a, _, _) = Graph.Edge(edgeIds[k]);
            var line = polylines[edgeIds[k]];
            bool forward = a == nodes[k];
            if (forward) for (int i = 1; i < line.Count; i++) points.Add(line[i]);
            else for (int i = line.Count - 2; i >= 0; i--) points.Add(line[i]);
            if (IsJunction(nodes[k + 1]) && k + 1 < nodes.Count - 1) junctionAt?.Add((points.Count - 1, nodes[k + 1]));
        }
        return true;
    }

    public static float Length(IList<Vector3> pts)
    {
        float len = 0f;
        for (int i = 1; i < pts.Count; i++) len += Vector3.Distance(pts[i - 1], pts[i]);
        return len;
    }

    void OnDrawGizmos()
    {
        if (edges == null) return;
        if (!Application.isPlaying || Graph == null) Build(); // in play mode the built graph is reused
        for (int e = 0; e < Graph.EdgeCount; e++)
        {
            var line = polylines[e];
            Gizmos.color = new Color(1f, 0.6f, 0.1f);
            for (int i = 1; i < line.Count; i++) Gizmos.DrawLine(line[i - 1], line[i]);
            // Direction cone-ish marker and travel time at the middle of the run.
            Vector3 mid = line[line.Count / 2];
            Gizmos.DrawWireSphere(mid, 0.25f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(mid + Vector3.up * 0.6f, $"{Graph.Edge(e).length:0} m  {Graph.Edge(e).length / 4f:0.0} s at 4 m/s");
#endif
        }
        for (int i = 0; i < nodePos.Length; i++)
        {
            Gizmos.color = IsJunction(i) ? Color.yellow : new Color(0.2f, 0.9f, 1f);
            Gizmos.DrawSphere(nodePos[i], IsJunction(i) ? 0.45f : 0.3f);
        }
    }
}
