using System;
using System.Collections.Generic;
using System.Linq;

public enum LayoutIssueCode : byte
{
    NodeCorrespondence, EdgeCorrespondence, MissingTemplate, RoomOverlap, RoomCorridorOverlap, CorridorOverlap, SocketIncompatible, SocketMisaligned,
    CorridorBroken, SocketReused, UnsealedSocket, SealedSocketInUse, Disconnected, OutOfBounds, BridgeNotForward, EngineeringNotAft, EscapeBayNotOuter,
    EscapeBaysClose, SectorOrder, MissingAnchors, BadBounds, BadTemplate, SpawnRoom, OpenCorridorEnd, TooFewSpatialLoops,
}

public sealed class LayoutIssue
{
    public IssueSeverity severity;
    public LayoutIssueCode code;
    public string message;
    /// <summary>Logical node indices the issue concerns.</summary>
    public int[] nodes = Array.Empty<int>();
    /// <summary>Cells the issue concerns (drawn by the debug views).</summary>
    public Int2[] cells = Array.Empty<Int2>();

    public override string ToString() => $"{(severity == IssueSeverity.Error ? "ERROR" : "warning")} {code}: {message}";
}

public sealed class LayoutReport
{
    public readonly List<LayoutIssue> issues = new();
    /// <summary>Independent walkable loops in the built ship (links - places + 1, over rooms and corridor modules), crossings included.</summary>
    public int spatialLoops;
    public bool Valid => !issues.Any(i => i.severity == IssueSeverity.Error);
    public int ErrorCount => issues.Count(i => i.severity == IssueSeverity.Error);
    public bool Has(LayoutIssueCode c) => issues.Any(i => i.code == c);
}

/// <summary>
/// Checks a placed ship against its logical graph and the physical rules: one room per node and one connection per edge, no overlaps, aligned
/// and compatible sockets, continuous corridors, every unused socket sealed, everything walkable-connected, inside the boundary, and the
/// spatial rules (sector order along the ship, Bridge forward, Engineering aft, escape bays on the outside and apart). Pure: no scene needed.
/// </summary>
public sealed class ShipLayoutValidator
{
    readonly PlacementSettings settings;

    public ShipLayoutValidator(PlacementSettings settings) => this.settings = settings;

    static void Add(LayoutReport r, IssueSeverity sev, LayoutIssueCode code, string msg, int[] nodes = null, Int2[] cells = null) =>
        r.issues.Add(new LayoutIssue { severity = sev, code = code, message = msg, nodes = nodes ?? Array.Empty<int>(), cells = cells ?? Array.Empty<Int2>() });

    public LayoutReport Validate(ShipLayout layout, IEnumerable<RoomSpec> specs = null)
    {
        var rep = new LayoutReport();
        var g = layout.graph;
        const IssueSeverity Err = IssueSeverity.Error;
        string Id(int n) => n >= 0 && n < g.nodes.Count ? g.nodes[n].id : $"node {n}";

        // ---- Correspondence: every logical node has exactly one room, every edge exactly one connection ----
        if (layout.rooms.Count != g.NodeCount)
            Add(rep, Err, LayoutIssueCode.NodeCorrespondence, $"{layout.rooms.Count} placed rooms for {g.NodeCount} logical rooms");
        for (int i = 0; i < Math.Min(layout.rooms.Count, g.NodeCount); i++)
        {
            var r = layout.rooms[i];
            if (r == null || r.template == null) { Add(rep, Err, LayoutIssueCode.MissingTemplate, $"{Id(i)} has no room template", new[] { i }); continue; }
            if (r.node != i) Add(rep, Err, LayoutIssueCode.NodeCorrespondence, $"placed room {i} is recorded for node {r.node}", new[] { i });
            if (r.template.definitionId != g.nodes[i].definitionId)
                Add(rep, Err, LayoutIssueCode.NodeCorrespondence, $"{Id(i)} uses template '{r.template.id}' of definition '{r.template.definitionId}', expected '{g.nodes[i].definitionId}'", new[] { i });
            if (r.deck != g.nodes[i].deck) Add(rep, Err, LayoutIssueCode.NodeCorrespondence, $"{Id(i)} is on deck {r.deck}, its node says deck {g.nodes[i].deck}", new[] { i });
        }
        if (rep.Has(LayoutIssueCode.MissingTemplate) || layout.rooms.Count != g.NodeCount) return rep; // nothing below is safe to evaluate

        if (layout.connections.Count != g.EdgeCount)
            Add(rep, Err, LayoutIssueCode.EdgeCorrespondence, $"{layout.connections.Count} physical connections for {g.EdgeCount} logical connections");
        var connectedPairs = new HashSet<(int, int)>();
        for (int i = 0; i < layout.connections.Count; i++)
        {
            var c = layout.connections[i];
            if (c.edge < 0 || c.edge >= g.EdgeCount) { Add(rep, Err, LayoutIssueCode.EdgeCorrespondence, $"connection {i} names edge {c.edge}, which does not exist"); continue; }
            var e = g.edges[c.edge];
            bool sameEnds = (c.nodeA == e.a && c.nodeB == e.b) || (c.nodeA == e.b && c.nodeB == e.a);
            if (!sameEnds) Add(rep, Err, LayoutIssueCode.EdgeCorrespondence, $"connection {c.edgeId} joins {Id(c.nodeA)} and {Id(c.nodeB)}, the graph edge joins {Id(e.a)} and {Id(e.b)}", new[] { e.a, e.b });
            if (!connectedPairs.Add((Math.Min(c.nodeA, c.nodeB), Math.Max(c.nodeA, c.nodeB))))
                Add(rep, Err, LayoutIssueCode.EdgeCorrespondence, $"{Id(c.nodeA)} and {Id(c.nodeB)} are connected twice", new[] { c.nodeA, c.nodeB });
        }
        for (int i = 0; i < g.EdgeCount; i++)
            if (!layout.connections.Any(c => c.edge == i)) Add(rep, Err, LayoutIssueCode.EdgeCorrespondence, $"graph edge {g.edges[i].id} has no physical connection", new[] { g.edges[i].a, g.edges[i].b });

        // ---- Overlaps ----
        for (int i = 0; i < layout.rooms.Count; i++)
            for (int j = i + 1; j < layout.rooms.Count; j++)
                if (layout.rooms[i].Overlaps(layout.rooms[j]))
                    Add(rep, Err, LayoutIssueCode.RoomOverlap, $"{Id(i)} overlaps {Id(j)}", new[] { i, j }, new[] { layout.rooms[i].origin, layout.rooms[j].origin });
        layout.Occupancy(out var clashes);
        foreach (var cell in clashes.Distinct())
        {
            bool inRoom = layout.rooms.Any(r => r.Contains(cell));
            bool inCorridor = layout.connections.Any(c => c.cells.Contains(cell));
            if (inRoom && inCorridor) Add(rep, Err, LayoutIssueCode.RoomCorridorOverlap, $"a corridor cell {cell} lies inside a room", cells: new[] { cell });
            else if (inCorridor) Add(rep, Err, LayoutIssueCode.CorridorOverlap, $"two corridors share cell {cell}", cells: new[] { cell });
        }

        // ---- Sockets: compatible, aligned, continuous, used once, otherwise sealed ----
        var usedSockets = new HashSet<(int, int)>();
        bool badSockets = false;
        foreach (var c in layout.connections)
        {
            if (c.nodeA < 0 || c.nodeA >= layout.rooms.Count || c.nodeB < 0 || c.nodeB >= layout.rooms.Count) continue;
            var ra = layout.rooms[c.nodeA];
            var rb = layout.rooms[c.nodeB];
            if (c.socketA < 0 || c.socketA >= ra.template.sockets.Count || c.socketB < 0 || c.socketB >= rb.template.sockets.Count)
            {
                Add(rep, Err, LayoutIssueCode.SocketIncompatible, $"{c.edgeId} names a socket that does not exist", new[] { c.nodeA, c.nodeB });
                badSockets = true;
                continue;
            }
            if (!usedSockets.Add((c.nodeA, c.socketA))) Add(rep, Err, LayoutIssueCode.SocketReused, $"{Id(c.nodeA)} socket {ra.template.sockets[c.socketA].id} carries two connections", new[] { c.nodeA });
            if (!usedSockets.Add((c.nodeB, c.socketB))) Add(rep, Err, LayoutIssueCode.SocketReused, $"{Id(c.nodeB)} socket {rb.template.sockets[c.socketB].id} carries two connections", new[] { c.nodeB });

            var m = SocketRules.Compatibility(ra.template.sockets[c.socketA], rb.template.sockets[c.socketB]);
            if (!m.ok) Add(rep, Err, LayoutIssueCode.SocketIncompatible, $"{c.edgeId}: {m.reason}", new[] { c.nodeA, c.nodeB });
            else if (!ModeAllowed(m.modes, c.mode)) Add(rep, Err, LayoutIssueCode.SocketIncompatible, $"{c.edgeId}: the sockets do not support a {c.mode} connection", new[] { c.nodeA, c.nodeB });

            var cellA = ra.SocketCell(c.socketA);
            var cellB = rb.SocketCell(c.socketB);
            var sideA = ra.SocketSide(c.socketA);
            var sideB = rb.SocketSide(c.socketB);
            if (c.mode == ConnectionMode.Direct)
            {
                if (c.cells.Count != 0) Add(rep, Err, LayoutIssueCode.CorridorBroken, $"{c.edgeId} is direct but has corridor cells", new[] { c.nodeA, c.nodeB });
                if (!SocketRules.Aligned(sideA, cellA, sideB, cellB, c.mode))
                    Add(rep, Err, LayoutIssueCode.SocketMisaligned, $"{c.edgeId}: socket {ra.template.sockets[c.socketA].id} of {Id(c.nodeA)} ({sideA} at {cellA}) does not meet socket {rb.template.sockets[c.socketB].id} of {Id(c.nodeB)} ({sideB} at {cellB})",
                        new[] { c.nodeA, c.nodeB }, new[] { cellA, cellB });
            }
            else
            {
                if (c.cells.Count == 0) { Add(rep, Err, LayoutIssueCode.CorridorBroken, $"{c.edgeId} is a corridor connection with no corridor cells", new[] { c.nodeA, c.nodeB }); continue; }
                if (c.cells[0] != ra.OutsideCell(c.socketA))
                    Add(rep, Err, LayoutIssueCode.SocketMisaligned, $"{c.edgeId}: the corridor starts at {c.cells[0]}, not at the cell {ra.OutsideCell(c.socketA)} outside {Id(c.nodeA)}", new[] { c.nodeA }, new[] { c.cells[0] });
                if (c.cells[^1] != rb.OutsideCell(c.socketB))
                    Add(rep, Err, LayoutIssueCode.SocketMisaligned, $"{c.edgeId}: the corridor ends at {c.cells[^1]}, not at the cell {rb.OutsideCell(c.socketB)} outside {Id(c.nodeB)}", new[] { c.nodeB }, new[] { c.cells[^1] });
                for (int k = 1; k < c.cells.Count; k++)
                    if (c.cells[k].Manhattan(c.cells[k - 1]) != 1)
                    {
                        Add(rep, Err, LayoutIssueCode.CorridorBroken, $"{c.edgeId}: corridor cells {c.cells[k - 1]} and {c.cells[k]} are not neighbours", new[] { c.nodeA, c.nodeB }, new[] { c.cells[k - 1], c.cells[k] });
                        break;
                    }
            }
        }
        if (badSockets) return rep; // the checks below index sockets
        foreach (var s in layout.sealedSockets)
            if (usedSockets.Contains((s.node, s.socket))) Add(rep, Err, LayoutIssueCode.SealedSocketInUse, $"{Id(s.node)} socket {s.socket} is both sealed and connected", new[] { s.node });
        var sealedSet = new HashSet<(int, int)>(layout.sealedSockets.Select(s => (s.node, s.socket)));
        foreach (var r in layout.rooms)
            for (int s = 0; s < r.template.sockets.Count; s++)
                if (!usedSockets.Contains((r.node, s)) && !sealedSet.Contains((r.node, s)))
                    Add(rep, Err, LayoutIssueCode.UnsealedSocket, $"{Id(r.node)} socket {r.template.sockets[s].id} is open and unused (a hole in the hull)", new[] { r.node }, new[] { r.SocketCell(s) });

        // ---- Corridor modules: clean crossings, no open ends; then true walkable connectivity (cells, not the connection list) ----
        var pieces = CorridorMath.Pieces(layout);
        var pieceIndex = new Dictionary<Int2, int>();
        for (int i = 0; i < pieces.Count; i++) pieceIndex[pieces[i].cell] = i;
        var roomAt = new Dictionary<Int2, int>();
        foreach (var r in layout.rooms) foreach (var cell in r.Cells()) roomAt[cell] = r.node;
        int total = pieces.Count + layout.rooms.Count;
        int links = 0;
        var dsu = new int[total];
        for (int i = 0; i < total; i++) dsu[i] = i;
        int Find(int x) { while (dsu[x] != x) { dsu[x] = dsu[dsu[x]]; x = dsu[x]; } return x; }
        void Union(int a, int b) { dsu[Find(a)] = Find(b); }
        foreach (var piece in pieces)
        {
            if (!piece.valid)
                Add(rep, Err, piece.owners > 1 ? LayoutIssueCode.CorridorOverlap : LayoutIssueCode.CorridorBroken,
                    piece.owners > 1 ? $"corridors share cell {piece.cell} without crossing cleanly straight over straight" : $"the corridor cell {piece.cell} opens sides {piece.mask} that match no module", cells: new[] { piece.cell });
            for (int sd = 0; sd < 4; sd++)
            {
                if ((piece.mask & (1 << sd)) == 0) continue;
                var side = (GridSide)sd;
                var n = piece.cell + Grid.Step(side);
                int me = pieceIndex[piece.cell];
                if (pieceIndex.TryGetValue(n, out int q))
                {
                    if ((pieces[q].mask & Grid.Bit(Grid.Opposite(side))) == 0)
                        Add(rep, Err, LayoutIssueCode.OpenCorridorEnd, $"the corridor at {piece.cell} opens {side} into a wall of the corridor at {n}", cells: new[] { piece.cell });
                    else { Union(me, q); if (q > me) links++; }
                }
                else if (roomAt.TryGetValue(n, out int rn))
                {
                    var room = layout.rooms[rn];
                    int sk = -1;
                    for (int k = 0; k < room.template.sockets.Count; k++)
                        if (room.SocketCell(k) == n && room.SocketSide(k) == Grid.Opposite(side)) sk = k;
                    if (sk < 0 || !usedSockets.Contains((rn, sk)))
                        Add(rep, Err, LayoutIssueCode.OpenCorridorEnd, $"the corridor at {piece.cell} opens {side} into the wall of {Id(rn)}, where it has no connected socket", new[] { rn }, new[] { piece.cell });
                    else { Union(me, pieces.Count + rn); links++; }
                }
                else Add(rep, Err, LayoutIssueCode.OpenCorridorEnd, $"the corridor at {piece.cell} opens {side} onto nothing (a hole in the hull)", cells: new[] { piece.cell });
            }
        }
        foreach (var c in layout.connections)
            if (c.mode == ConnectionMode.Direct && c.nodeA >= 0 && c.nodeA < layout.rooms.Count && c.nodeB >= 0 && c.nodeB < layout.rooms.Count)
                { Union(pieces.Count + c.nodeA, pieces.Count + c.nodeB); links++; }
        var cut = new List<int>();
        if (layout.rooms.Count > 0)
        {
            int root = Find(pieces.Count);
            for (int i = 1; i < layout.rooms.Count; i++) if (Find(pieces.Count + i) != root) cut.Add(i);
        }
        rep.spatialLoops = cut.Count == 0 ? links - total + 1 : 0;
        if (cut.Count == 0 && rep.spatialLoops < settings.minSpatialLoops)
            Add(rep, Err, LayoutIssueCode.TooFewSpatialLoops, $"the built ship has {rep.spatialLoops} walkable loops, {settings.minSpatialLoops} are required");
        if (cut.Count > 0) Add(rep, Err, LayoutIssueCode.Disconnected, $"{cut.Count} room(s) cannot be walked to from {Id(0)}: {string.Join(", ", cut.Take(6).Select(Id))}", cut.ToArray());

        // ---- Boundary and spatial rules ----
        layout.ComputeBounds();
        if (layout.WidthCells > settings.maxExtentX || layout.DepthCells > settings.maxExtentZ)
            Add(rep, Err, LayoutIssueCode.OutOfBounds, $"the ship is {layout.WidthCells} x {layout.DepthCells} cells, the boundary is {settings.maxExtentX} x {settings.maxExtentZ}");

        SpatialRules(layout, rep, Id);

        // ---- Template metadata of every placed room ----
        if (specs != null)
        {
            var specById = specs.ToDictionary(s => s.id);
            foreach (var r in layout.rooms)
            {
                foreach (var p in r.template.Problems()) Add(rep, Err, LayoutIssueCode.BadTemplate, p, new[] { r.node });
                if (specById.TryGetValue(r.template.definitionId, out var spec))
                    foreach (var p in AnchorRules.Missing(spec, r.template)) Add(rep, Err, LayoutIssueCode.MissingAnchors, p, new[] { r.node });
            }
        }
        return rep;
    }

    static bool ModeAllowed(SocketSupport modes, ConnectionMode mode) => mode switch
    {
        ConnectionMode.Direct => (modes & SocketSupport.Direct) != 0,
        ConnectionMode.Corridor => (modes & SocketSupport.Corridor) != 0,
        _ => (modes & SocketSupport.Airlock) != 0,
    };

    void SpatialRules(ShipLayout layout, LayoutReport rep, Func<int, string> id)
    {
        var g = layout.graph;
        const IssueSeverity Err = IssueSeverity.Error;
        int x0 = layout.Min.x, x1 = layout.Max.x;
        double length = Math.Max(1, x1 - x0 + 1);

        double CentreX(int n) => layout.rooms[n].Centre2.x * 0.5;

        // Sectors run bow to stern along X: their centres must be in order, with some room between neighbours.
        var centres = new List<(ShipSector sector, double x)>();
        foreach (ShipSector s in Enum.GetValues(typeof(ShipSector)))
        {
            var members = g.nodes.Where(n => n.sector == s).ToList();
            if (members.Count > 0) centres.Add((s, members.Average(n => CentreX(n.index))));
        }
        for (int i = 1; i < centres.Count; i++)
            if (centres[i].x < centres[i - 1].x + 4)
                Add(rep, Err, LayoutIssueCode.SectorOrder, $"sector {centres[i].sector} (centre x {centres[i].x:0.#}) is not aft of {centres[i - 1].sector} (centre x {centres[i - 1].x:0.#}) by at least 4 cells");

        int bridge = g.FirstOf(RoomCategory.Bridge), eng = g.FirstOf(RoomCategory.Engineering);
        if (bridge >= 0 && (CentreX(bridge) - x0) / length > settings.endZoneShare)
            Add(rep, Err, LayoutIssueCode.BridgeNotForward, $"{id(bridge)} is {(CentreX(bridge) - x0) / length:P0} of the way along the ship; the Bridge must be within the forward {settings.endZoneShare:P0}", new[] { bridge });
        if (eng >= 0 && (x1 + 1 - CentreX(eng)) / length > settings.endZoneShare)
            Add(rep, Err, LayoutIssueCode.EngineeringNotAft, $"{id(eng)} is not within the aft {settings.endZoneShare:P0} of the ship", new[] { eng });

        var pods = g.AllOf(RoomCategory.EscapePodBay);
        foreach (int p in pods)
            if (!Exposed(layout, p))
                Add(rep, Err, LayoutIssueCode.EscapeBayNotOuter, $"{id(p)} is buried inside the ship: other rooms or corridors lie beyond it on every side", new[] { p });
        for (int i = 0; i < pods.Count; i++)
            for (int j = i + 1; j < pods.Count; j++)
            {
                var a = layout.rooms[pods[i]].Centre2;
                var b = layout.rooms[pods[j]].Centre2;
                int d = (Math.Abs(a.x - b.x) + Math.Abs(a.z - b.z)) / 2;
                if (d < settings.minPodSeparationCells)
                    Add(rep, Err, LayoutIssueCode.EscapeBaysClose, $"{id(pods[i])} and {id(pods[j])} are {d} cells apart; at least {settings.minPodSeparationCells} are required", new[] { pods[i], pods[j] });
            }
    }

    /// <summary>
    /// True when a room touches the outside of the ship: in at least one of the four directions nothing is built anywhere along the strip the
    /// room's footprint sweeps out. Such a room can have an outward-facing hull wall (and, later, a visible launch window).
    /// </summary>
    public static bool Exposed(ShipLayout layout, int node)
    {
        var r = layout.rooms[node];
        var max = r.Max;
        var occupied = layout.Occupancy(out _);
        for (int d = 0; d < 4; d++)
        {
            bool blocked = false;
            foreach (var kv in occupied)
            {
                if (kv.Value == node) continue;
                var c = kv.Key;
                switch ((GridSide)d)
                {
                    case GridSide.North: blocked = c.x >= r.origin.x && c.x <= max.x && c.z > max.z; break;
                    case GridSide.South: blocked = c.x >= r.origin.x && c.x <= max.x && c.z < r.origin.z; break;
                    case GridSide.East: blocked = c.z >= r.origin.z && c.z <= max.z && c.x > max.x; break;
                    default: blocked = c.z >= r.origin.z && c.z <= max.z && c.x < r.origin.x; break;
                }
                if (blocked) break;
            }
            if (!blocked) return true;
        }
        return false;
    }
}
