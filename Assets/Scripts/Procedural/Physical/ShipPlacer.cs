using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>What one placement produced: the layout (null when every attempt failed), its validation, and why each failed attempt failed.</summary>
public sealed class ShipPlacementResult
{
    public int seed;
    public bool success;
    public ShipLayout layout;
    public LayoutReport report;
    public int attempts;
    public readonly List<string> attemptFailures = new();
    /// <summary>A text map (one character per cell: base-36 node index for rooms, '.' for corridors) of the furthest partial layout of the last failed attempt.</summary>
    public string failureMap;

    public string Summary => success
        ? $"{layout.Summary()}; attempt {attempts}; layout {layout.Fingerprint()}"
        : $"seed {seed}: PLACEMENT FAILED after {attempts} attempt(s).{(attemptFailures.Count > 0 ? " Last: " + attemptFailures[^1] : "")}";
}

/// <summary>
/// Turns a validated logical graph into a physical layout on a grid: a room template, a rotation and a position per room, and a corridor (or a
/// shared wall) per connection. Deterministic: the only randomness is ShipRng streams derived from the graph's seed and the attempt number.
///
/// Placement order follows the graph's structure. The primary route (the spine) goes first, one room after the other, each aft of the last, so the
/// ship runs bow to stern along X and the sectors come out in order. Then rooms with the most placed neighbours: a loop connector joins two or
/// more placed rooms and is searched for around their middle; a side branch has one placed neighbour and grows out from one of its free sockets.
/// Candidates are (template, rotation, socket, corridor length, sideways offset) combinations. The placer drops the ones that overlap, leave the
/// boundary, bury a socket or leave a placed room with too few usable sockets, scores the rest (sector side, corridor length, crowding, noise) and
/// tries them best first. Corridors are built with an A* search that avoids rooms and may cross another corridor at a Cross module. When no
/// candidate works the placer backtracks to the previous room. Each attempt has a step budget; attempts use different random streams, and the
/// later half search more widely. After the last attempt the result is a failure with every attempt's reason and a map of the furthest layout.
/// </summary>
public sealed class ShipPlacer
{
    readonly TemplateLibrary library;
    readonly List<RoomSpec> specs;
    readonly PlacementSettings settings;
    readonly ShipLayoutValidator validator;

    public PlacementSettings Settings => settings;

    public ShipPlacer(TemplateLibrary library, IEnumerable<RoomSpec> catalogue, PlacementSettings settings = null)
    {
        this.library = library;
        this.settings = settings ?? new PlacementSettings();
        specs = catalogue.OrderBy(s => s.id, StringComparer.Ordinal).ToList();
        validator = new ShipLayoutValidator(this.settings);
    }

    public ShipLayoutValidator Validator => validator;

    public ShipPlacementResult Place(ShipGraph graph)
    {
        var result = new ShipPlacementResult { seed = graph.seed };
        if (graph.NodeCount == 0) { result.attemptFailures.Add("the graph has no rooms"); return result; }
        if (graph.nodes.Any(n => n.deck != 0)) { result.attemptFailures.Add("only deck 0 can be placed so far (the data is multi-deck-ready, the placer is not)"); return result; }
        foreach (var n in graph.nodes)
            if (library.For(n.definitionId).Count == 0) { result.attemptFailures.Add($"no room template for {n.id} (definition '{n.definitionId}')"); return result; }
        int spawn = graph.FirstOf(RoomCategory.PlayerStart);
        if (spawn < 0) { result.attemptFailures.Add("the graph has no Player Start"); return result; }

        var relaxed = settings.Relaxed();
        for (int attempt = 0; attempt < settings.maxAttempts; attempt++)
        {
            result.attempts = attempt + 1;
            var a = new Attempt(this, graph, attempt, attempt >= settings.maxAttempts / 2 ? relaxed : settings);
            var layout = a.Run(out string failure);
            if (layout == null) { result.attemptFailures.Add($"attempt {attempt + 1}: {failure}"); result.failureMap = a.DeepestMap; continue; }
            var report = validator.Validate(layout, specs);
            if (!report.Valid)
            {
                result.attemptFailures.Add($"attempt {attempt + 1}: placed all rooms but validation failed: {string.Join("; ", report.issues.Where(i => i.severity == IssueSeverity.Error).Take(3).Select(i => i.message))}");
                continue;
            }
            result.layout = layout;
            result.report = report;
            result.success = true;
            return result;
        }
        return result;
    }


    // ------------------------------------------------------------------------------------------------------------------------------

    struct Candidate
    {
        public int template, rot, parentSocket, socket, ox, oz, a, b;
        public bool multi;
        public ConnectionMode mode;
        public double score;
        public int seq;
    }

    struct Box
    {
        public int x0, z0, x1, z1;
        public static Box Empty => new() { x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue };
        public void Add(int x, int z) { x0 = Math.Min(x0, x); z0 = Math.Min(z0, z); x1 = Math.Max(x1, x); z1 = Math.Max(z1, z); }
        public bool IsEmpty => x0 > x1;
    }

    /// <summary>One try at placing the whole graph (see the class comment). Everything random comes from the attempt's own stream.</summary>
    sealed class Attempt
    {
        readonly ShipPlacer p;
        readonly PlacementSettings s;
        readonly ShipGraph g;
        readonly ShipRng rng;
        readonly RoomPlacement[] placed;
        readonly bool[][] used;

        // Occupancy as a flat array centred on the first room: far cheaper than a dictionary in the hot loops. Cells outside it count as taken.
        readonly int[] grid;
        readonly int gridW, gridH, offX, offZ;
        const int Empty = int.MinValue;
        int[] prefix;
        bool prefixDirty = true;
        // For corridor cells: 1 = a straight run along Z (crossable while moving along X), 2 = a straight run along X, 3 = not crossable (a corner,
        // an end next to a doorway, or an existing crossing), 0 = no corridor. Lets a new corridor cross an old one with a Cross module.
        readonly byte[] axis;

        readonly PhysicalConnection[] built;
        readonly int[] remaining, order;
        readonly bool[] isSpine;
        readonly List<int>[] partners;
        readonly int[] anchor; // the placed neighbour a room is attached to with a corridor of its own choice (-1: a multi-partner connector)
        readonly bool[] wantDirect;
        readonly int[] preferredLength;
        readonly Dictionary<int, int> edgeOf = new();
        Box box = Box.Empty;
        Int2 startCell;
        int steps, deepest = -1;
        bool aborted;
        double baseZ;
        static readonly string[] WhyNames =
        {
            "overlaps a room or corridor", "child on the corridor start", "leaves the boundary", "corridor end blocked", "overlaps", "no corridor route",
            "corridor end boxed in", "loop connection cannot be routed", "a placed room ran out of usable sockets", "no free socket pair", "too close to the other escape bay",
        };
        readonly int[] rejections = new int[WhyNames.Length];

        public Attempt(ShipPlacer p, ShipGraph g, int attempt, PlacementSettings settings)
        {
            this.p = p;
            s = settings;
            this.g = g;
            offX = s.maxExtentX; offZ = s.maxExtentZ;
            gridW = offX * 2 + 1; gridH = offZ * 2 + 1;
            grid = new int[gridW * gridH];
            Array.Fill(grid, Empty);
            axis = new byte[grid.Length];
            rng = new ShipRng(ShipRng.Derive(g.seed ^ 0x51ED270B, attempt));
            int n = g.NodeCount;
            placed = new RoomPlacement[n];
            used = new bool[n][];
            built = new PhysicalConnection[g.EdgeCount];
            remaining = new int[n];
            wantDirect = new bool[g.EdgeCount];
            preferredLength = new int[g.EdgeCount];
            for (int i = 0; i < g.EdgeCount; i++)
            {
                edgeOf[Key(g.edges[i].a, g.edges[i].b)] = i;
                wantDirect[i] = rng.Chance(s.directChance);
                preferredLength[i] = rng.Range(s.corridorMinCells, s.corridorMaxCells);
            }
            for (int i = 0; i < n; i++) remaining[i] = g.nodes[i].Degree;

            // Placement order: the primary route first (it becomes the spine along X), then rooms with the most already-placed neighbours (loop
            // connectors close two placed rooms and so go before side branches, which have the whole outside to grow into).
            isSpine = new bool[n];
            partners = new List<int>[n];
            var inOrder = new bool[n];
            var list = new List<int>();
            var route = g.route.Count > 0 ? g.route : new List<int> { g.FirstOf(RoomCategory.PlayerStart) };
            foreach (int r in route) { list.Add(r); inOrder[r] = true; isSpine[r] = true; }
            anchor = new int[n];
            for (int i = 0; i < list.Count; i++)
            {
                var earlier = new HashSet<int>(list.Take(i));
                partners[list[i]] = g.nodes[list[i]].neighbours.Where(earlier.Contains).OrderBy(u => u).ToList();
                anchor[list[i]] = i == 0 ? -1 : list[i - 1];
            }
            while (list.Count < n)
            {
                int best = -1, bestCount = 0;
                for (int v = 0; v < n; v++)
                {
                    if (inOrder[v]) continue;
                    int c = g.nodes[v].neighbours.Count(u => inOrder[u]);
                    if (c > bestCount) { best = v; bestCount = c; }
                }
                if (best < 0) break; // a disconnected graph: caught in Run
                partners[best] = g.nodes[best].neighbours.Where(u => inOrder[u]).OrderBy(u => u).ToList();
                anchor[best] = partners[best].Count == 1 ? partners[best][0] : -1;
                inOrder[best] = true;
                list.Add(best);
            }
            order = list.ToArray();
        }

        // ---------- occupancy ----------

        int Index(Int2 c) => (c.z + offZ) * gridW + (c.x + offX);
        bool InGrid(Int2 c) => c.x > -offX && c.x < offX && c.z > -offZ && c.z < offZ;
        bool Taken(Int2 c) => !InGrid(c) || grid[Index(c)] != Empty;
        void SetOwner(Int2 c, int o) { grid[Index(c)] = o; prefixDirty = true; }
        void ClearOwner(Int2 c) { if (InGrid(c)) { grid[Index(c)] = Empty; axis[Index(c)] = 0; prefixDirty = true; } }

        bool CanCross(Int2 c, int d)
        {
            if (!s.allowCrossings || !InGrid(c)) return false;
            int i = Index(c);
            if (grid[i] == Empty || grid[i] >= 0) return false;
            return (d & 1) == 0 ? axis[i] == 2 : axis[i] == 1; // moving N/S crosses a run along X; moving E/W crosses a run along Z
        }

        // A summed-area table of the occupancy, rebuilt lazily when cells changed: a rectangle is free in O(1).
        void RebuildPrefix()
        {
            prefix ??= new int[(gridW + 1) * (gridH + 1)];
            int w1 = gridW + 1;
            for (int z = 0; z < gridH; z++)
            {
                int row = 0;
                for (int x = 0; x < gridW; x++)
                {
                    if (grid[z * gridW + x] != Empty) row++;
                    prefix[(z + 1) * w1 + x + 1] = prefix[z * w1 + x + 1] + row;
                }
            }
            prefixDirty = false;
        }

        bool RectFree(int x0, int z0, int x1, int z1)
        {
            if (x0 <= -offX || z0 <= -offZ || x1 >= offX || z1 >= offZ) return false;
            return RectCount(x0, z0, x1, z1) == 0;
        }

        // Occupied cells inside a rectangle (clipped to the grid).
        int RectCount(int x0, int z0, int x1, int z1)
        {
            if (prefixDirty) RebuildPrefix();
            x0 = Math.Max(x0, -offX + 1); z0 = Math.Max(z0, -offZ + 1); x1 = Math.Min(x1, offX - 1); z1 = Math.Min(z1, offZ - 1);
            if (x0 > x1 || z0 > z1) return 0;
            int w1 = gridW + 1;
            int ax = x0 + offX, az = z0 + offZ, bx = x1 + offX + 1, bz = z1 + offZ + 1;
            return prefix[bz * w1 + bx] - prefix[az * w1 + bx] - prefix[bz * w1 + ax] + prefix[az * w1 + ax];
        }

        static int Key(int a, int b) => Math.Min(a, b) * 4096 + Math.Max(a, b);
        int EdgeIndex(int a, int b) => edgeOf[Key(a, b)];
        void Reject(int why) => rejections[why]++;

        // A free cell is "open" when a flood fill from it reaches a fair stretch of free cells: it is not a closed pocket between rooms and
        // corridors, so a corridor leaving it has somewhere to go. Cheap forward checking: a placement that boxes a socket in fails at once, at
        // the room that caused it, instead of many rooms later.
        readonly Queue<Int2> floodQueue = new();
        readonly HashSet<Int2> floodSeen = new();

        bool Open(Int2 start)
        {
            if (Taken(start)) return false;
            floodQueue.Clear();
            floodSeen.Clear();
            floodQueue.Enqueue(start);
            floodSeen.Add(start);
            while (floodQueue.Count > 0)
            {
                if (floodSeen.Count >= s.openCells) return true;
                var c = floodQueue.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    var n = c + Grid.Step((GridSide)d);
                    if (Taken(n) || !floodSeen.Add(n)) continue;
                    floodQueue.Enqueue(n);
                }
            }
            return false;
        }

        int Usable(int node)
        {
            var r = placed[node];
            int n = 0;
            for (int sk = 0; sk < r.template.sockets.Count; sk++)
                if (!used[node][sk] && Open(r.OutsideCell(sk))) n++;
            return n;
        }

        // ---------- diagnostics ----------

        public string DeepestMap { get; private set; }

        string Snapshot()
        {
            if (box.IsEmpty) return "";
            var sb = new System.Text.StringBuilder();
            for (int z = box.z1; z >= box.z0; z--)
            {
                for (int x = box.x0; x <= box.x1; x++)
                {
                    int o = InGrid(new Int2(x, z)) ? grid[Index(new Int2(x, z))] : Empty;
                    if (o == Empty) sb.Append(' ');
                    else if (o < 0) sb.Append('.');
                    else sb.Append("0123456789abcdefghijklmnopqrstuvwxyz"[o % 36]);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public ShipLayout Run(out string failure)
        {
            failure = null;
            if (order.Length != g.NodeCount) { failure = "the logical graph is not connected"; return null; }
            if (TryNode(0)) return Finish();
            string what = g.nodes[order[Math.Min(Math.Max(deepest, 0), order.Length - 1)]].id;
            string why = rejections.All(n => n == 0) ? "no candidate positions" : string.Join(", ", Enumerable.Range(0, rejections.Length).Where(i => rejections[i] > 0)
                .OrderByDescending(i => rejections[i]).ThenBy(i => i).Take(6).Select(i => $"{WhyNames[i]} x{rejections[i]}"));
            failure = $"{(aborted ? "step budget of " + s.maxStepsPerAttempt + " used up" : "no placement possible")}; could not place {what} (room {Math.Max(deepest, 0) + 1} of {order.Length}); main rejections: {why}";
            return null;
        }

        ShipLayout Finish()
        {
            var layout = new ShipLayout { seed = g.seed, graph = g, settings = s };
            foreach (var r in placed) layout.rooms.Add(r);
            foreach (var c in built) layout.connections.Add(c);
            layout.ComputeBounds();
            // Move everything so the minimum corner is the origin: layouts do not depend on where the first room happened to start.
            var shift = new Int2(-layout.Min.x, -layout.Min.z);
            foreach (var r in layout.rooms) r.origin += shift;
            foreach (var c in layout.connections)
                for (int i = 0; i < c.cells.Count; i++) c.cells[i] += shift;
            foreach (var r in layout.rooms)
                for (int sk = 0; sk < r.template.sockets.Count; sk++)
                    if (!used[r.node][sk]) layout.sealedSockets.Add(new SealedSocket(r.node, sk));
            layout.ComputeBounds();
            return layout;
        }

        // ---------- search ----------

        bool TryNode(int idx)
        {
            if (idx > deepest) { deepest = idx; Array.Clear(rejections, 0, rejections.Length); DeepestMap = Snapshot(); }
            if (idx == order.Length) return true;
            int v = order[idx];
            var candidates = idx == 0 ? RootCandidates(v) : anchor[v] < 0 ? MultiCandidates(v) : AnchoredCandidates(v, anchor[v]);
            candidates.Sort((a, b) => a.score != b.score ? a.score.CompareTo(b.score) : a.seq.CompareTo(b.seq));
            int succeeded = 0, failed = 0;
            foreach (var c in candidates)
            {
                if (aborted) return false;
                if (succeeded >= s.candidatesPerRoom || failed >= s.maxFailedCandidates) break;
                if (steps > s.maxStepsPerAttempt) { aborted = true; return false; }
                var log = new UndoLog();
                if (!Apply(v, c, idx == 0, log)) { Undo(log); failed++; continue; }
                succeeded++;
                steps++;
                if (TryNode(idx + 1)) return true;
                Undo(log);
            }
            return false;
        }

        // The first room (the forward end of the spine) goes at the origin; only its template and turn vary. Turns that put a socket on its
        // aft (east) side are preferred, since the spine runs that way.
        List<Candidate> RootCandidates(int v)
        {
            var list = new List<Candidate>();
            var templates = p.library.For(g.nodes[v].definitionId);
            int seq = 0;
            for (int t = 0; t < templates.Count; t++)
            {
                if (templates[t].sockets.Count < g.nodes[v].Degree) continue;
                for (int r = 0; r < 4; r++)
                {
                    bool east = false;
                    for (int sk = 0; sk < templates[t].sockets.Count; sk++) east |= templates[t].SocketSide(sk, r) == GridSide.East;
                    list.Add(new Candidate { template = t, rot = r, score = (east ? 0 : 10) + rng.NextDouble(), seq = seq++ });
                }
            }
            return list;
        }

        // A room attached to one placed neighbour (the spine's next room, a side branch, the first half of a connector chain): the candidates
        // are the positions reachable from one of the neighbour's free sockets with a corridor.
        List<Candidate> AnchoredCandidates(int v, int par)
        {
            var list = new List<Candidate>();
            var templates = p.library.For(g.nodes[v].definitionId);
            var P = placed[par];
            int edge = EdgeIndex(par, v);
            int degree = g.nodes[v].Degree;
            int seq = 0;
            double parentCx = P.Centre2.x * 0.5;

            for (int sp = 0; sp < P.template.sockets.Count; sp++)
            {
                if (used[par][sp]) continue;
                var outP = P.OutsideCell(sp);
                if (Taken(outP)) continue;
                startCell = outP;
                var d = P.SocketSide(sp);
                var perp = Grid.Step(Grid.Rotate(d, 1));
                for (int t = 0; t < templates.Count; t++)
                {
                    var tpl = templates[t];
                    if (tpl.sockets.Count < degree) continue;
                    for (int sc = 0; sc < tpl.sockets.Count; sc++)
                    {
                        var match = SocketRules.Compatibility(P.template.sockets[sp], tpl.sockets[sc]);
                        if (!match.ok) continue;
                        for (int rot = 0; rot < 4; rot++)
                        {
                            var sideC = tpl.SocketSide(sc, rot);
                            Grid.RotatedSize(tpl.sizeX, tpl.sizeZ, rot, out int sx, out int sz);
                            var local = tpl.SocketCell(sc, rot);

                            if (wantDirect[edge] && (match.modes & SocketSupport.Direct) != 0 && sideC == Grid.Opposite(d))
                                Consider(list, ref seq, v, t, rot, sp, sc, outP - local, outP, sx, sz, ConnectionMode.Direct, 0, 0, edge, parentCx);

                            if ((match.modes & SocketSupport.Corridor) == 0 || sideC == d) continue;
                            var step = Grid.Step(d);
                            var facing = Grid.Step(sideC);
                            for (int a = 0; a < s.corridorMaxCells + 3; a++)
                                for (int b = -s.lateralRange; b <= s.lateralRange; b++)
                                {
                                    var childOut = outP + step * a + perp * b;
                                    int cells = a + Math.Abs(b) + 1;
                                    if (cells < s.corridorMinCells || cells > s.treeCorridorMaxCells) continue;
                                    // The child's doorway must face back towards the corridor's axis, or the corridor has to wrap around the child.
                                    if (facing.x * (outP.x - childOut.x) + facing.z * (outP.z - childOut.z) < 0) continue;
                                    Consider(list, ref seq, v, t, rot, sp, sc, childOut - facing - local, childOut, sx, sz, ConnectionMode.Corridor, a, b, edge, parentCx);
                                }
                        }
                    }
                }
            }
            return list;
        }

        // Cheap rejection (overlap, boundary, spine direction), then scoring. The corridor itself is only routed when the candidate is applied.
        void Consider(List<Candidate> list, ref int seq, int v, int t, int rot, int sp, int sc, Int2 origin, Int2 childOut, int sx, int sz, ConnectionMode mode,
            int a, int b, int edge, double parentCx)
        {
            int x1 = origin.x + sx - 1, z1 = origin.z + sz - 1;
            if (!RectFree(origin.x, origin.z, x1, z1)) { Reject(0); return; }
            if (startCell.x >= origin.x && startCell.x <= x1 && startCell.z >= origin.z && startCell.z <= z1) { Reject(1); return; }
            if (mode == ConnectionMode.Corridor && Taken(childOut)) { Reject(3); return; }
            var nb = box;
            nb.Add(origin.x, origin.z);
            nb.Add(x1, z1);
            if (mode == ConnectionMode.Corridor) nb.Add(childOut.x, childOut.z);
            if (nb.x1 - nb.x0 + 1 > s.maxExtentX || nb.z1 - nb.z0 + 1 > s.maxExtentZ) { Reject(2); return; }

            double cx = origin.x + (sx - 1) * 0.5, cz = origin.z + (sz - 1) * 0.5;
            var cat = g.nodes[v].category;
            double score = 0;
            if (isSpine[v])
            {
                if (cx < parentCx + s.minSpineAdvance) { Reject(4); return; } // the spine only ever moves aft
                score += Math.Abs(cz - baseZ) * s.lateralWeight;
            }
            else
            {
                if (cat == RoomCategory.EscapePodBay)
                    foreach (var other in placed)
                        if (other != null && other.template.category == RoomCategory.EscapePodBay && (Math.Abs(cx - other.Centre2.x * 0.5) + Math.Abs(cz - other.Centre2.z * 0.5)) < s.minPodSeparationCells)
                        { Reject(10); return; }
                score += Math.Abs(cx - parentCx) * 0.15;
                if (cat == RoomCategory.Bridge) score += (cx - parentCx) * 0.6;          // the Bridge goes as far forward as it can
                else if (cat == RoomCategory.Engineering) score -= (cx - parentCx) * 0.6; // Engineering as far aft
                else if (cat == RoomCategory.EscapePodBay) score -= Math.Min(Math.Abs(cz - baseZ), 24) * 0.5; // pods go out to the sides
            }
            score += mode == ConnectionMode.Corridor ? (a + Math.Abs(b) + 1) * s.corridorWeight + Math.Abs(a + Math.Abs(b) + 1 - preferredLength[edge]) * 0.3 : -2.0;
            score += RectCount(origin.x - s.crowdRadius, origin.z - s.crowdRadius, x1 + s.crowdRadius, z1 + s.crowdRadius) * s.crowdWeight;
            score += rng.NextDouble() * s.jitter;
            list.Add(new Candidate { template = t, rot = rot, parentSocket = sp, socket = sc, ox = origin.x, oz = origin.z, a = a, b = b, mode = mode, score = score, seq = seq++ });
        }

        // A room that joins two or more placed rooms (a loop connector, or a room closing a loop): any free position near the middle of its
        // partners. Its connections are routed to each partner when the candidate is applied.
        List<Candidate> MultiCandidates(int v)
        {
            var list = new List<Candidate>();
            var templates = p.library.For(g.nodes[v].definitionId);
            int degree = g.nodes[v].Degree;
            double mx = 0, mz = 0;
            foreach (int u in partners[v]) { mx += placed[u].Centre2.x * 0.5; mz += placed[u].Centre2.z * 0.5; }
            mx /= partners[v].Count;
            mz /= partners[v].Count;
            int seq = 0;
            int R = s.multiSearchRadius;
            for (int t = 0; t < templates.Count; t++)
            {
                var tpl = templates[t];
                if (tpl.sockets.Count < degree) continue;
                for (int rot = 0; rot < 4; rot++)
                {
                    Grid.RotatedSize(tpl.sizeX, tpl.sizeZ, rot, out int sx, out int sz);
                    for (int cx = (int)mx - R; cx <= (int)mx + R; cx++)
                        for (int cz = (int)mz - R; cz <= (int)mz + R; cz++)
                        {
                            int ox = cx - sx / 2, oz = cz - sz / 2, x1 = ox + sx - 1, z1 = oz + sz - 1;
                            if (!RectFree(ox, oz, x1, z1)) continue;
                            var nb = box;
                            nb.Add(ox, oz);
                            nb.Add(x1, z1);
                            if (nb.x1 - nb.x0 + 1 > s.maxExtentX || nb.z1 - nb.z0 + 1 > s.maxExtentZ) continue;
                            double ccx = ox + (sx - 1) * 0.5, ccz = oz + (sz - 1) * 0.5;
                            double score = 0;
                            foreach (int u in partners[v])
                                score += Math.Abs(ccx - placed[u].Centre2.x * 0.5) + Math.Abs(ccz - placed[u].Centre2.z * 0.5);
                            score += RectCount(ox - s.crowdRadius, oz - s.crowdRadius, x1 + s.crowdRadius, z1 + s.crowdRadius) * s.crowdWeight;
                            score += rng.NextDouble() * s.jitter;
                            list.Add(new Candidate { template = t, rot = rot, ox = ox, oz = oz, multi = true, mode = ConnectionMode.Corridor, score = score, seq = seq++ });
                        }
                }
            }
            return list;
        }

        // ---------- applying and undoing ----------

        sealed class UndoLog
        {
            public int node = -1;
            public readonly List<Int2> cells = new();
            public readonly List<int> edges = new();
            public readonly List<(int node, int socket)> sockets = new();
            public readonly List<(Int2 cell, byte old)> crossed = new();
            public Box box;
        }

        void Undo(UndoLog log)
        {
            foreach (int e in log.edges)
            {
                var c = built[e];
                remaining[c.nodeA]++;
                remaining[c.nodeB]++;
                built[e] = null;
            }
            foreach (var (n, sk) in log.sockets) used[n][sk] = false;
            foreach (var c in log.cells) ClearOwner(c);
            foreach (var (cell, old) in log.crossed) axis[Index(cell)] = old;
            if (log.node >= 0) placed[log.node] = null;
            box = log.box;
        }

        bool Apply(int v, Candidate c, bool root, UndoLog log)
        {
            log.box = box;
            var tpl = p.library.For(g.nodes[v].definitionId)[c.template];
            var room = new RoomPlacement { node = v, template = tpl, rot = c.rot, origin = root ? new Int2(0, 0) : new Int2(c.ox, c.oz), deck = g.nodes[v].deck };
            foreach (var cell in room.Cells())
            {
                if (Taken(cell)) { Reject(4); return false; }
                SetOwner(cell, v);
                log.cells.Add(cell);
            }
            placed[v] = room;
            log.node = v;
            used[v] = new bool[tpl.sockets.Count];
            box.Add(room.origin.x, room.origin.z);
            box.Add(room.Max.x, room.Max.z);
            if (root)
            {
                baseZ = room.Centre2.z * 0.5;
                return true;
            }

            if (c.multi)
            {
                foreach (int u in partners[v])
                    if (!RouteLoop(v, u, log)) { Reject(7); return false; }
            }
            else
            {
                int par = anchor[v];
                int edge = EdgeIndex(par, v);
                if (c.mode == ConnectionMode.Direct) Connect(edge, par, c.parentSocket, v, c.socket, ConnectionMode.Direct, null, log);
                else
                {
                    var cs = placed[par].OutsideCell(c.parentSocket);
                    var ce = room.OutsideCell(c.socket);
                    if (Taken(ce)) { Reject(3); return false; }
                    if (!Open(cs) || !Open(ce)) { Reject(6); return false; }
                    var path = Route(cs, ce, s.treeCorridorMaxCells);
                    if (path == null) { Reject(5); return false; }
                    Connect(edge, par, c.parentSocket, v, c.socket, ConnectionMode.Corridor, path, log);
                }
                foreach (int u in partners[v])
                    if (u != par && !RouteLoop(v, u, log)) { Reject(7); return false; }
            }

            // Every placed room must still have enough usable sockets for the connections it has yet to get.
            for (int n = 0; n < placed.Length; n++)
                if (placed[n] != null && remaining[n] > 0 && Usable(n) < remaining[n]) { Reject(8); return false; }
            return true;
        }

        void Connect(int edge, int from, int fromSocket, int to, int toSocket, ConnectionMode mode, List<Int2> pathFromTo, UndoLog log)
        {
            var e = g.edges[edge];
            var conn = new PhysicalConnection { edge = edge, edgeId = e.id, mode = mode };
            bool fromIsA = e.a == from;
            conn.nodeA = fromIsA ? from : to;
            conn.socketA = fromIsA ? fromSocket : toSocket;
            conn.nodeB = fromIsA ? to : from;
            conn.socketB = fromIsA ? toSocket : fromSocket;
            if (pathFromTo != null)
            {
                if (!fromIsA) pathFromTo.Reverse();
                conn.cells.AddRange(pathFromTo);
                for (int i = 0; i < conn.cells.Count; i++)
                {
                    var cell = conn.cells[i];
                    box.Add(cell.x, cell.z);
                    if (Taken(cell))
                    {
                        // A crossing: both corridors now share one Cross module, which nothing may cross again.
                        log.crossed.Add((cell, axis[Index(cell)]));
                        axis[Index(cell)] = 3;
                        continue;
                    }
                    SetOwner(cell, -(edge + 1));
                    log.cells.Add(cell);
                    bool straight = i > 0 && i < conn.cells.Count - 1 && conn.cells[i - 1].x - cell.x == cell.x - conn.cells[i + 1].x && conn.cells[i - 1].z - cell.z == cell.z - conn.cells[i + 1].z;
                    axis[Index(cell)] = !straight ? (byte)3 : conn.cells[i - 1].x != cell.x ? (byte)2 : (byte)1;
                }
            }
            built[edge] = conn;
            log.edges.Add(edge);
            used[from][fromSocket] = true;
            used[to][toSocket] = true;
            log.sockets.Add((from, fromSocket));
            log.sockets.Add((to, toSocket));
            remaining[from]--;
            remaining[to]--;
        }

        // Joins a newly placed room to an earlier neighbour: a direct connection if two free sockets already face each other across a wall,
        // else the shortest corridor between the nearest compatible free sockets.
        bool RouteLoop(int v, int u, UndoLog log)
        {
            int edge = EdgeIndex(v, u);
            var rv = placed[v];
            var ru = placed[u];
            var pairs = new List<(int sv, int su, int dist, bool direct)>();
            for (int sv = 0; sv < rv.template.sockets.Count; sv++)
            {
                if (used[v][sv]) continue;
                for (int su = 0; su < ru.template.sockets.Count; su++)
                {
                    if (used[u][su]) continue;
                    var m = SocketRules.Compatibility(rv.template.sockets[sv], ru.template.sockets[su]);
                    if (!m.ok) continue;
                    if ((m.modes & SocketSupport.Direct) != 0 && SocketRules.Aligned(rv.SocketSide(sv), rv.SocketCell(sv), ru.SocketSide(su), ru.SocketCell(su), ConnectionMode.Direct))
                        pairs.Add((sv, su, -1, true));
                    else if ((m.modes & SocketSupport.Corridor) != 0 && !Taken(rv.OutsideCell(sv)) && !Taken(ru.OutsideCell(su)))
                        pairs.Add((sv, su, rv.OutsideCell(sv).Manhattan(ru.OutsideCell(su)), false));
                }
            }
            if (pairs.Count == 0) Reject(9);
            pairs.Sort((x, y) => x.dist != y.dist ? x.dist.CompareTo(y.dist) : x.sv != y.sv ? x.sv.CompareTo(y.sv) : x.su.CompareTo(y.su));
            int tries = 0;
            foreach (var pr in pairs)
            {
                if (pr.direct) { Connect(edge, v, pr.sv, u, pr.su, ConnectionMode.Direct, null, log); return true; }
                if (pr.dist > s.maxLoopCorridorCells) break;
                if (tries++ >= 5) break;
                var a = rv.OutsideCell(pr.sv);
                var b = ru.OutsideCell(pr.su);
                if (!Open(a) || !Open(b)) continue;
                var path = Route(a, b, s.maxLoopCorridorCells);
                if (path != null) { Connect(edge, v, pr.sv, u, pr.su, ConnectionMode.Corridor, path, log); return true; }
            }
            return false;
        }

        // ---------- corridor routing (A*; turns cost a little so corridors prefer straight runs) ----------

        bool InBox(Int2 c)
        {
            int x0 = Math.Min(box.x0, c.x), x1 = Math.Max(box.x1, c.x), z0 = Math.Min(box.z0, c.z), z1 = Math.Max(box.z1, c.z);
            return x1 - x0 + 1 <= s.maxExtentX && z1 - z0 + 1 <= s.maxExtentZ;
        }

        readonly List<(Int2 cell, int dir, int prev, float g)> nodes = new();

        List<Int2> Route(Int2 from, Int2 to, int maxCells)
        {
            steps++;
            if (Taken(from) || Taken(to) || !InBox(from) || !InBox(to)) return null;
            if (from == to) return new List<Int2> { from };

            nodes.Clear();
            var best = new Dictionary<long, float>();
            var heap = new List<(float f, int seq, int node)>();
            int seq = 0;
            long StateKey(Int2 c, int dir) => ((long)(c.x + 100000) * 200000L + (c.z + 100000)) * 5 + dir;
            void Push((float f, int seq, int node) e)
            {
                heap.Add(e);
                int i = heap.Count - 1;
                while (i > 0)
                {
                    int par = (i - 1) / 2;
                    if (Less(heap[i], heap[par])) { (heap[i], heap[par]) = (heap[par], heap[i]); i = par; } else break;
                }
            }
            (float f, int seq, int node) Pop()
            {
                var top = heap[0];
                heap[0] = heap[^1];
                heap.RemoveAt(heap.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < heap.Count && Less(heap[l], heap[m])) m = l;
                    if (r < heap.Count && Less(heap[r], heap[m])) m = r;
                    if (m == i) break;
                    (heap[i], heap[m]) = (heap[m], heap[i]);
                    i = m;
                }
                return top;
            }

            nodes.Add((from, 4, -1, 1f));
            best[StateKey(from, 4)] = 1f;
            Push((from.Manhattan(to), seq++, 0));
            while (heap.Count > 0)
            {
                var cur = Pop();
                var (cell, dir, _, gCost) = nodes[cur.node];
                if (best.TryGetValue(StateKey(cell, dir), out float known) && gCost > known) continue;
                if (cell == to)
                {
                    var path = new List<Int2>();
                    for (int i = cur.node; i >= 0; i = nodes[i].prev) path.Add(nodes[i].cell);
                    path.Reverse();
                    return path;
                }
                bool onCrossing = cell != from && Taken(cell); // standing on an existing corridor: must go straight on through it
                for (int d = 0; d < 4; d++)
                {
                    if (onCrossing && d != dir) continue;
                    var next = cell + Grid.Step((GridSide)d);
                    bool cross = false;
                    if (Taken(next))
                    {
                        if (!CanCross(next, d)) continue;
                        cross = true;
                    }
                    if (!InBox(next)) continue;
                    float ng = gCost + 1f + (dir != 4 && dir != d ? 0.4f : 0f) + (cross ? s.crossingCost : 0f);
                    if (ng - 1f > maxCells) continue;
                    long key = StateKey(next, d);
                    if (best.TryGetValue(key, out float old) && old <= ng) continue;
                    best[key] = ng;
                    nodes.Add((next, d, cur.node, ng));
                    Push((ng + next.Manhattan(to), seq++, nodes.Count - 1));
                }
            }
            return null;
        }

        static bool Less((float f, int seq, int node) a, (float f, int seq, int node) b) => a.f != b.f ? a.f < b.f : a.seq < b.seq;
    }
}
