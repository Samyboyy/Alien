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
        for (int k = 0; k < edges.Length; k++)
        {
            var edge = edges[k];
            int a = -1, b = -1;
            if (edge == null || (a = NodeIndex(edge.from)) < 0 || (b = NodeIndex(edge.to)) < 0)
            {
                Debug.LogWarning($"Vent network: edge {k} is ignored. Start: {Why(edge?.from, a)}. End: {Why(edge?.to, b)}.", this);
                continue;
            }
            var line = new List<Vector3> { nodePos[a] };
            foreach (var v in edge.via) if (v != null) line.Add(v.position);
            line.Add(nodePos[b]);
            Graph.AddEdge(a, b, Length(line));
            polylines.Add(line);
        }
    }

    int NodeIndex(Transform t) => t != null && nodeOf.TryGetValue(t, out int i) ? i : -1;

    // Why an end of an edge was not accepted, for the warning: the missing reference or the object that is not a registered node.
    string Why(Transform t, int index)
    {
        if (index >= 0) return $"node {index} ok";
        if (t == null) return "no object assigned";
        return $"'{PathOf(t)}' is not a registered node (an entrance's Top point whose entrance component is missing, or a transform not listed in entrances or junctions)";
    }

    public static string PathOf(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    /// <summary>Checks the authored network without building or changing anything (editor tools and tests).</summary>
    public VentValidation.Report Validate()
    {
        var map = new Dictionary<Transform, int>();
        var present = new List<bool>();
        var ids = new List<int>();
        for (int i = 0; i < entrances.Length; i++)
        {
            var e = entrances[i];
            bool ok = e != null && e.top != null;
            present.Add(ok);
            ids.Add(e != null ? e.id : -1);
            if (ok) map[e.top] = i;
        }
        for (int j = 0; j < junctions.Length; j++)
        {
            present.Add(junctions[j] != null);
            if (junctions[j] != null) map[junctions[j]] = entrances.Length + j;
        }
        var pairs = new List<(int, int)>();
        foreach (var edge in edges)
            pairs.Add((edge != null && edge.from != null && map.TryGetValue(edge.from, out int a) ? a : -1,
                edge != null && edge.to != null && map.TryGetValue(edge.to, out int b) ? b : -1));
        return VentValidation.Check(present, entrances.Length, pairs, ids);
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
