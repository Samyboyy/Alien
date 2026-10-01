using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Checks a ship graph against the room catalogue and the whole-ship rules, and returns structured findings (code, severity, message, the
/// rooms concerned). Works on any graph, not only generated ones, so it also catches hand-made or corrupted graphs. Never changes the graph.
/// </summary>
public sealed class ShipGraphValidator
{
    static readonly Regex NodeIdPattern = new(@"^[a-z0-9_]+\.\d+$");

    readonly Dictionary<string, RoomSpec> byId = new();
    readonly List<RoomSpec> specs;
    readonly ShipGraphSettings s;

    public ShipGraphValidator(IEnumerable<RoomSpec> catalogue, ShipGraphSettings settings)
    {
        specs = catalogue.Where(x => x != null).OrderBy(x => x.id, System.StringComparer.Ordinal).ToList();
        foreach (var spec in specs) if (!string.IsNullOrEmpty(spec.id) && !byId.ContainsKey(spec.id)) byId[spec.id] = spec;
        s = settings ?? new ShipGraphSettings();
    }

    public GraphValidationReport Validate(ShipGraph g)
    {
        var r = new GraphValidationReport();
        void Error(GraphIssueCode code, string message, params int[] nodes) => r.issues.Add(new GraphIssue { severity = IssueSeverity.Error, code = code, message = message, nodes = nodes });
        void Warn(GraphIssueCode code, string message, params int[] nodes) => r.issues.Add(new GraphIssue { severity = IssueSeverity.Warning, code = code, message = message, nodes = nodes });
        string N(int i) => i >= 0 && i < g.nodes.Count ? g.nodes[i].id : $"#{i}";

        // ---------- Identifiers and structure ----------
        var ids = new HashSet<string>();
        for (int i = 0; i < g.nodes.Count; i++)
        {
            var n = g.nodes[i];
            if (n.index != i) Error(GraphIssueCode.BadNodeId, $"{n.id} has index {n.index} but sits at {i}", i);
            if (string.IsNullOrEmpty(n.id) || !NodeIdPattern.IsMatch(n.id) || !n.id.StartsWith(n.definitionId + ".")) Error(GraphIssueCode.BadNodeId, $"'{n.id}' is not a stable id of the form '{n.definitionId}.N'", i);
            if (!ids.Add(n.id ?? "")) Error(GraphIssueCode.DuplicateNodeId, $"the id '{n.id}' is used more than once", i);
        }
        var edgeIds = new HashSet<string>();
        var pairs = new HashSet<long>();
        bool edgesOk = true;
        foreach (var e in g.edges)
        {
            if (e.a < 0 || e.b < 0 || e.a >= g.nodes.Count || e.b >= g.nodes.Count) { Error(GraphIssueCode.BadEdge, $"connection '{e.id}' refers to a room that does not exist"); edgesOk = false; continue; }
            if (e.a == e.b) { Error(GraphIssueCode.SelfEdge, $"{N(e.a)} is connected to itself", e.a); edgesOk = false; continue; }
            long key = (long)System.Math.Min(e.a, e.b) << 32 | (uint)System.Math.Max(e.a, e.b);
            if (!pairs.Add(key)) Error(GraphIssueCode.DuplicateEdge, $"{N(e.a)} and {N(e.b)} are connected twice", e.a, e.b);
            string ia = g.nodes[e.a].id, ib = g.nodes[e.b].id;
            string expected = string.CompareOrdinal(ia, ib) <= 0 ? $"{ia}--{ib}" : $"{ib}--{ia}";
            if (e.id != expected) Error(GraphIssueCode.DuplicateEdgeId, $"connection id '{e.id}' should be '{expected}'", e.a, e.b);
            if (!edgeIds.Add(e.id ?? "")) Error(GraphIssueCode.DuplicateEdgeId, $"the connection id '{e.id}' is used more than once", e.a, e.b);
            if (!g.nodes[e.a].neighbours.Contains(e.b) || !g.nodes[e.b].neighbours.Contains(e.a)) Error(GraphIssueCode.BadEdge, $"connection '{e.id}' is missing from a room's neighbour list", e.a, e.b);
        }
        if (!edgesOk) return r; // the rest assumes the connections are sound

        // ---------- Definitions, counts, decks, sectors ----------
        if (g.nodes.Count < s.minRooms || g.nodes.Count > s.maxRooms) Error(GraphIssueCode.RoomCount, $"{g.nodes.Count} rooms; the ship needs {s.minRooms}-{s.maxRooms}");
        var specOf = new RoomSpec[g.nodes.Count];
        for (int i = 0; i < g.nodes.Count; i++)
        {
            var n = g.nodes[i];
            if (!byId.TryGetValue(n.definitionId ?? "", out var spec)) { Error(GraphIssueCode.UnknownDefinition, $"{n.id} uses the unknown definition '{n.definitionId}'", i); continue; }
            if (spec.category != n.category) Error(GraphIssueCode.UnknownDefinition, $"{n.id} is a {n.category} but its definition is a {spec.category}", i);
            specOf[i] = spec;
            if (!spec.AllowsSector(n.sector)) Error(GraphIssueCode.SectorNotPermitted, $"{n.id} is in the {n.sector} sector, which its definition does not allow", i);
            if (n.deck < 0 || n.deck >= s.deckCount || (spec.deck >= 0 && spec.deck != n.deck)) Error(GraphIssueCode.DeckNotPermitted, $"{n.id} is on deck {n.deck}", i);
            if (spec.requiresVerticalConnection && s.deckCount <= 1) Error(GraphIssueCode.RequiresMultiDeck, $"{n.id} connects decks, but this ship has a single deck", i);
        }
        foreach (var spec in specs)
        {
            int count = g.nodes.Count(n => n.definitionId == spec.id);
            int min = spec.mandatory ? System.Math.Max(1, spec.minCount) : spec.minCount;
            if (count < min || count > spec.maxCount) Error(GraphIssueCode.OccurrenceRange, $"{count} x {spec.displayName}; the definition allows {min}-{spec.maxCount}");
        }
        for (int sec = 0; sec < 4; sec++)
            if (!g.nodes.Any(n => (int)n.sector == sec)) Error(GraphIssueCode.SectorEmpty, $"the {(ShipSector)sec} sector has no rooms");

        // ---------- Connectivity ----------
        var comps = g.Components();
        if (comps.Count != 1)
        {
            var main = comps.OrderByDescending(c => c.Count).First();
            var cut = Enumerable.Range(0, g.nodes.Count).Where(i => !main.Contains(i)).ToArray();
            Error(GraphIssueCode.Disconnected, $"{comps.Count} separate parts; not reachable: {string.Join(", ", cut.Take(8).Select(N))}{(cut.Length > 8 ? "..." : "")}", cut);
        }
        r.loops = g.LoopCount();
        if (r.loops < s.minLoops) Error(GraphIssueCode.TooFewLoops, $"{r.loops} loop(s); at least {s.minLoops} are needed");

        // ---------- Per room: connections, role, dead ends, neighbours ----------
        for (int i = 0; i < g.nodes.Count; i++)
        {
            var spec = specOf[i];
            if (spec == null) continue;
            int deg = g.nodes[i].Degree;
            if (deg > s.maxDegree) Error(GraphIssueCode.MaxDegreeExceeded, $"{N(i)} has {deg} connections; no room may have more than {s.maxDegree}", i);
            if (deg < spec.minConnections || deg > spec.maxConnections) Error(GraphIssueCode.ConnectionRange, $"{N(i)} has {deg} connection(s); its definition allows {spec.minConnections}-{spec.maxConnections}", i);
            var role = RoomSpec.RoleForDegree(deg);
            if (!spec.Allows(role)) Error(GraphIssueCode.RoleNotPermitted, $"{N(i)} is a {role} ({deg} connections); its definition allows {spec.roles}", i);
            if (deg == 1 && !spec.mayBeDeadEnd) Error(GraphIssueCode.DeadEndNotAllowed, $"{N(i)} is a dead end, which its definition does not allow", i);
            if (spec.onlyNeighbours.Length > 0)
                foreach (int j in g.nodes[i].neighbours)
                    if (!spec.onlyNeighbours.Contains(g.nodes[j].category))
                        Error(GraphIssueCode.NeighbourNotAllowed, $"{N(i)} opens onto {N(j)}; it may only connect to {string.Join("/", spec.onlyNeighbours)}", i, j);
            foreach (var need in spec.requiredNeighbours)
                if (!g.nodes[i].neighbours.Any(j => g.nodes[j].category == need))
                    Error(GraphIssueCode.RequiredNeighbourMissing, $"{N(i)} has no {need} next to it", i);
            foreach (int j in g.nodes[i].neighbours)
                if (j > i && specOf[j] != null && (spec.forbiddenNeighbours.Contains(g.nodes[j].category) || specOf[j].forbiddenNeighbours.Contains(g.nodes[i].category)))
                    Error(GraphIssueCode.ForbiddenNeighbour, $"{N(i)} and {N(j)} must not be neighbours", i, j);
        }
        if (comps.Count != 1) return r; // distance rules need one connected ship

        // ---------- Distances from the spawn ----------
        int spawn = g.FirstOf(RoomCategory.PlayerStart);
        var dist = spawn >= 0 ? g.Distances(spawn) : null;
        if (dist != null)
        {
            r.maxDistance = dist.Max();
            for (int i = 0; i < g.nodes.Count; i++)
            {
                var spec = specOf[i];
                if (spec == null || i == spawn) continue;
                if ((spec.minSpawnDistance >= 0 && dist[i] < spec.minSpawnDistance) || (spec.maxSpawnDistance >= 0 && dist[i] > spec.maxSpawnDistance))
                    Error(GraphIssueCode.SpawnDistance, $"{N(i)} is {dist[i]} connection(s) from the spawn; its definition wants {Range(spec.minSpawnDistance, spec.maxSpawnDistance)}", i);
            }
        }

        // ---------- Proximity ("close to") ----------
        for (int i = 0; i < g.nodes.Count; i++)
        {
            var spec = specOf[i];
            if (spec == null || spec.closeTo.Length == 0) continue;
            var targets = g.nodes.Where(n => n.index != i && spec.closeTo.Contains(n.category)).Select(n => n.index).ToList();
            if (targets.Count == 0) continue; // nothing to be near (an optional target that was not generated)
            var d = g.Distances(i);
            int nearest = targets.Min(t => d[t]);
            if (nearest > spec.closeToDistance)
            {
                string msg = $"{N(i)} is {nearest} connection(s) from the nearest {string.Join("/", spec.closeTo)}; it should be within {spec.closeToDistance}";
                if (spec.closeToStrict) Error(GraphIssueCode.ProximityRequired, msg, i); else Warn(GraphIssueCode.ProximityPreferred, msg, i);
            }
        }

        // ---------- The primary route ----------
        var onRoute = new HashSet<int>(g.route);
        r.routeLength = g.route.Count;
        bool routeOk = g.route.Count >= s.minRouteLength && onRoute.Count == g.route.Count && g.route.All(i => i >= 0 && i < g.nodes.Count);
        if (routeOk)
        {
            for (int k = 1; k < g.route.Count && routeOk; k++)
                if (!g.Connected(g.route[k - 1], g.route[k]) || g.nodes[g.route[k]].sector < g.nodes[g.route[k - 1]].sector) routeOk = false;
            routeOk &= g.nodes[g.route[0]].sector == ShipSector.Forward && g.nodes[g.route[^1]].sector == ShipSector.Industrial;
            routeOk &= spawn < 0 || onRoute.Contains(spawn);
            for (int sec = 0; sec < 4; sec++) routeOk &= g.route.Count(i => (int)g.nodes[i].sector == sec) >= s.minRoutePerSector;
        }
        if (!routeOk)
            Error(GraphIssueCode.RouteInvalid, $"the primary route must be a connected path of at least {s.minRouteLength} unique rooms from the Forward to the Industrial sector, in sector order, through the spawn, with at least {s.minRoutePerSector} rooms in every sector");

        // Side branches and their depth: multi-source distance from the route.
        if (onRoute.Count > 0)
        {
            var depth = new int[g.nodes.Count];
            for (int i = 0; i < depth.Length; i++) depth[i] = onRoute.Contains(i) ? 0 : -1;
            var q = new Queue<int>(onRoute.OrderBy(i => i));
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                foreach (int v in g.nodes[u].neighbours) if (depth[v] < 0) { depth[v] = depth[u] + 1; q.Enqueue(v); }
            }
            r.sideBranches = g.nodes.Count(n => depth[n.index] == 1 && n.neighbours.Count(v => onRoute.Contains(v)) == 1 && n.neighbours.All(v => onRoute.Contains(v) || depth[v] > 1));
            if (r.sideBranches < s.minSideBranches) Error(GraphIssueCode.TooFewSideBranches, $"{r.sideBranches} side branch(es); at least {s.minSideBranches} are needed");
            foreach (var n in g.nodes) if (depth[n.index] > s.maxBranchDepth) Error(GraphIssueCode.BranchTooDeep, $"{n.id} is {depth[n.index]} rooms off the route; at most {s.maxBranchDepth}", n.index);
        }

        // ---------- Room mix ----------
        r.deadEnds = g.nodes.Count(n => n.Degree == 1);
        r.nonTerminal = g.nodes.Count - r.deadEnds;
        if (r.nonTerminal < s.minNonTerminalRooms) Error(GraphIssueCode.TooFewNonTerminal, $"{r.nonTerminal} rooms with two or more connections; patrols and searches need at least {s.minNonTerminalRooms}");
        int genericDeadEnds = g.nodes.Count(n => n.Degree == 1 && specOf[n.index] != null && specOf[n.index].tier == RoomTier.Structural);
        if (genericDeadEnds * 2 > r.deadEnds) Warn(GraphIssueCode.GenericDeadEnds, $"{genericDeadEnds} of {r.deadEnds} dead ends are generic rooms; dead ends are meant mostly for specialised rooms");

        // ---------- Escape pods ----------
        var pods = g.AllOf(RoomCategory.EscapePodBay);
        for (int a = 0; a < pods.Count; a++)
        {
            if (spawn >= 0 && g.Connected(spawn, pods[a])) Error(GraphIssueCode.SpawnNextToEscapePod, $"{N(pods[a])} is next to the spawn", pods[a], spawn);
            for (int b = a + 1; b < pods.Count; b++)
            {
                int d = g.Distance(pods[a], pods[b]);
                if (d < s.minEscapePodSeparation) Error(GraphIssueCode.EscapePodSeparation, $"{N(pods[a])} and {N(pods[b])} are {d} connection(s) apart; at least {s.minEscapePodSeparation}", pods[a], pods[b]);
                if (Anchor(g, pods[a], onRoute) == Anchor(g, pods[b], onRoute)) Error(GraphIssueCode.EscapePodSameBranch, $"{N(pods[a])} and {N(pods[b])} are on the same branch", pods[a], pods[b]);
            }
        }

        // ---------- Bridge and Engineering ----------
        int bridge = g.FirstOf(RoomCategory.Bridge), engineering = g.FirstOf(RoomCategory.Engineering);
        if (bridge >= 0 && g.nodes[bridge].sector != ShipSector.Forward) Error(GraphIssueCode.BridgeNotForward, $"the Bridge is in the {g.nodes[bridge].sector} sector", bridge);
        if (engineering >= 0 && g.nodes[engineering].sector != ShipSector.Industrial) Error(GraphIssueCode.EngineeringNotAft, $"Engineering is in the {g.nodes[engineering].sector} sector", engineering);
        if (bridge >= 0 && engineering >= 0)
        {
            int d = g.Distance(bridge, engineering);
            if (d < s.minBridgeEngineeringDistance) Error(GraphIssueCode.BridgeEngineeringTooClose, $"the Bridge and Engineering are {d} connection(s) apart; at least {s.minBridgeEngineeringDistance}", bridge, engineering);
        }
        if (bridge >= 0 && dist != null)
        {
            int further = dist.Count(d => d > dist[bridge]);
            if (further > s.bridgeMaxRoomsFurther) Error(GraphIssueCode.BridgeNotFurthest, $"{further} rooms are further from the spawn than the Bridge ({dist[bridge]}); at most {s.bridgeMaxRoomsFurther}", bridge);
        }

        // ---------- Redundancy: no single room may cut a whole sector off ----------
        if (s.requireSectorRedundancy)
        {
            for (int v = 0; v < g.nodes.Count; v++)
            {
                if (g.nodes[v].Degree < 2) continue;
                var parts = g.Components(v);
                if (parts.Count < 2) continue;
                var largest = parts.OrderByDescending(p => p.Count).First();
                for (int sec = 0; sec < 4; sec++)
                {
                    bool exists = g.nodes.Any(n => n.index != v && (int)n.sector == sec);
                    if (exists && !largest.Any(i => (int)g.nodes[i].sector == sec))
                        Error(GraphIssueCode.SectorCutVertex, $"removing {N(v)} cuts the whole {(ShipSector)sec} sector off from the ship", v);
                }
            }
        }
        return r;
    }

    // The route room a side room hangs from (itself when on the route).
    static int Anchor(ShipGraph g, int node, HashSet<int> route)
    {
        if (route.Contains(node)) return node;
        var d = g.Distances(node);
        int best = -1;
        foreach (int r in route.OrderBy(i => i)) if (d[r] >= 0 && (best < 0 || d[r] < d[best])) best = r;
        return best;
    }

    static string Range(int min, int max) => min >= 0 && max >= 0 ? $"{min}-{max}" : min >= 0 ? $"at least {min}" : $"at most {max}";

    /// <summary>Generates the seed twice and compares the canonical graphs. Detail explains a difference.</summary>
    public static bool CheckDeterminism(ShipGraphGenerator generator, int seed, out string detail)
    {
        var a = generator.Generate(seed);
        var b = generator.Generate(seed);
        if (a.graph == null || b.graph == null) { detail = a.graph == null && b.graph == null ? "both runs produced no graph" : "only one run produced a graph"; return a.graph == null && b.graph == null; }
        string ca = a.graph.Canonical(), cb = b.graph.Canonical();
        if (ca == cb) { detail = $"identical ({a.graph.Fingerprint()})"; return true; }
        int i = 0;
        while (i < ca.Length && i < cb.Length && ca[i] == cb[i]) i++;
        detail = $"the two runs differ from character {i}: '{Snip(ca, i)}' vs '{Snip(cb, i)}'";
        return false;
    }

    static string Snip(string s, int i) => s.Substring(i, System.Math.Min(40, s.Length - i));
}
