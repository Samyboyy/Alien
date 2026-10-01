using System.Collections.Generic;
using System.Text;

/// <summary>A room in the logical graph. Abstract position only: a sector and a deck, never world coordinates.</summary>
public sealed class ShipGraphNode
{
    public int index;
    /// <summary>Stable identifier: "{definition id}.{occurrence}", e.g. "escape_pod_bay.1". The same seed and catalogue always give the same ids.</summary>
    public string id;
    public string definitionId;
    public string displayName;
    public RoomCategory category;
    public ShipSector sector;
    public int deck;
    public readonly List<int> neighbours = new();

    public int Degree => neighbours.Count;
}

public sealed class ShipGraphEdge
{
    /// <summary>Stable identifier: the two node ids in ordinal order, joined with "--".</summary>
    public string id;
    public int a, b;

    public int Other(int node) => node == a ? b : a;
}

/// <summary>
/// The generated logical ship: rooms, undirected connections, and the primary route (the spine) from the bow to the stern. Indices are dense
/// and in creation order; ids are stable strings. Holds the graph algorithms the generator and validator share (distances, connectivity,
/// loops, a fingerprint for determinism checks).
/// </summary>
public sealed class ShipGraph
{
    public int seed;
    public int attempt;
    public readonly List<ShipGraphNode> nodes = new();
    public readonly List<ShipGraphEdge> edges = new();
    /// <summary>The primary route, as node indices from the forward end to the aft end.</summary>
    public readonly List<int> route = new();

    public int NodeCount => nodes.Count;
    public int EdgeCount => edges.Count;

    public ShipGraphNode AddNode(RoomSpec spec, ShipSector sector, int deck, int occurrence)
    {
        var n = new ShipGraphNode
        {
            index = nodes.Count, id = $"{spec.id}.{occurrence}", definitionId = spec.id, displayName = spec.displayName,
            category = spec.category, sector = sector, deck = deck,
        };
        nodes.Add(n);
        return n;
    }

    public bool Connected(int a, int b) => a >= 0 && a < nodes.Count && nodes[a].neighbours.Contains(b);

    /// <summary>Adds an undirected connection. False (nothing added) for a self-connection, a duplicate or a bad index.</summary>
    public bool AddEdge(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= nodes.Count || b >= nodes.Count || Connected(a, b)) return false;
        string ia = nodes[a].id, ib = nodes[b].id;
        edges.Add(new ShipGraphEdge { a = a, b = b, id = string.CompareOrdinal(ia, ib) <= 0 ? $"{ia}--{ib}" : $"{ib}--{ia}" });
        nodes[a].neighbours.Add(b);
        nodes[b].neighbours.Add(a);
        return true;
    }

    public bool RemoveEdge(int a, int b)
    {
        int i = edges.FindIndex(e => (e.a == a && e.b == b) || (e.a == b && e.b == a));
        if (i < 0) return false;
        edges.RemoveAt(i);
        nodes[a].neighbours.Remove(b);
        nodes[b].neighbours.Remove(a);
        return true;
    }

    public int FirstOf(RoomCategory c)
    {
        foreach (var n in nodes) if (n.category == c) return n.index;
        return -1;
    }

    public List<int> AllOf(RoomCategory c)
    {
        var list = new List<int>();
        foreach (var n in nodes) if (n.category == c) list.Add(n.index);
        return list;
    }

    // ---------- Algorithms ----------

    /// <summary>Connection counts from <paramref name="from"/> to every room (-1 = unreachable). <paramref name="removed"/> is treated as absent.</summary>
    public int[] Distances(int from, int removed = -1)
    {
        var d = new int[nodes.Count];
        for (int i = 0; i < d.Length; i++) d[i] = -1;
        if (from < 0 || from >= nodes.Count || from == removed) return d;
        var queue = new Queue<int>();
        d[from] = 0;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in nodes[u].neighbours)
            {
                if (v == removed || d[v] >= 0) continue;
                d[v] = d[u] + 1;
                queue.Enqueue(v);
            }
        }
        return d;
    }

    public int Distance(int a, int b) => a < 0 || b < 0 ? -1 : Distances(a)[b];

    /// <summary>Connected components (each a list of node indices), ignoring <paramref name="removed"/>.</summary>
    public List<List<int>> Components(int removed = -1)
    {
        var seen = new bool[nodes.Count];
        var result = new List<List<int>>();
        for (int s = 0; s < nodes.Count; s++)
        {
            if (seen[s] || s == removed) continue;
            var comp = new List<int>();
            var stack = new Stack<int>();
            stack.Push(s);
            seen[s] = true;
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                comp.Add(u);
                foreach (int v in nodes[u].neighbours)
                    if (v != removed && !seen[v]) { seen[v] = true; stack.Push(v); }
            }
            comp.Sort();
            result.Add(comp);
        }
        return result;
    }

    /// <summary>Independent loops (the cyclomatic number): connections - rooms + components.</summary>
    public int LoopCount() => edges.Count - nodes.Count + Components().Count;

    /// <summary>
    /// A canonical description of the whole graph (ids, categories, sectors, decks, sorted connections, the route). Two graphs are the same
    /// exactly when their fingerprints are equal.
    /// </summary>
    public string Canonical()
    {
        var sb = new StringBuilder();
        foreach (var n in nodes) sb.Append(n.id).Append('|').Append((int)n.category).Append('|').Append((int)n.sector).Append('|').Append(n.deck).Append(';');
        var ids = new List<string>();
        foreach (var e in edges) ids.Add(e.id);
        ids.Sort(System.StringComparer.Ordinal);
        foreach (var id in ids) sb.Append(id).Append(';');
        foreach (int r in route) sb.Append(r).Append(',');
        return sb.ToString();
    }

    /// <summary>A short hash of <see cref="Canonical"/> (FNV-1a, 64 bit), for display and quick comparisons.</summary>
    public string Fingerprint()
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in Canonical()) unchecked { h = (h ^ c) * 1099511628211UL; }
        return h.ToString("x16");
    }

    public ShipGraph Clone()
    {
        var g = new ShipGraph { seed = seed, attempt = attempt };
        foreach (var n in nodes)
            g.nodes.Add(new ShipGraphNode { index = n.index, id = n.id, definitionId = n.definitionId, displayName = n.displayName, category = n.category, sector = n.sector, deck = n.deck });
        foreach (var e in edges)
        {
            g.edges.Add(new ShipGraphEdge { a = e.a, b = e.b, id = e.id });
            g.nodes[e.a].neighbours.Add(e.b);
            g.nodes[e.b].neighbours.Add(e.a);
        }
        g.route.AddRange(route);
        return g;
    }
}
