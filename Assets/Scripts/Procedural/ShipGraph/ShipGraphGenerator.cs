using System.Collections.Generic;
using System.Linq;

/// <summary>What one generation produced: the graph (the last attempt's, even when it failed), its validation, and why earlier attempts failed.</summary>
public sealed class ShipGraphResult
{
    public int seed;
    public bool success;
    public ShipGraph graph;
    public GraphValidationReport report;
    public int attempts;
    public readonly List<string> attemptFailures = new();

    public string Summary => success
        ? $"seed {seed}: {graph.NodeCount} rooms, {graph.EdgeCount} connections, {report.loops} loops, attempt {attempts}, fingerprint {graph.Fingerprint()}"
        : $"seed {seed}: FAILED after {attempts} attempt(s). Last: {(attemptFailures.Count > 0 ? attemptFailures[^1] : "-")}";
}

/// <summary>
/// Builds a deliberate single-deck ship graph from a seed and a room catalogue. Deterministic: everything random comes from ShipRng streams
/// derived from (seed, attempt), the catalogue is processed in id order, and no hash-ordered collection is ever enumerated.
///
/// Each attempt:
///   1. picks the rooms: every mandatory room, a weighted handful of specialised rooms, then structural rooms to fill the count;
///   2. places them in sectors (bow to stern: Forward, Central, Crew, Industrial), balancing the sectors and spreading rooms that ask for it;
///   3. splits each sector into the primary route and its side rooms, and orders the route: the Bridge's host at the forward end, the
///      spawn at the aft end of the crew sector, a junction-capable room (Power Control by preference) at the stern;
///   4. adds overlapping loops along the route (a chord every few rooms, through a structural connector room where one is free), so no single
///      room can cut the ship in two;
///   5. hangs the remaining rooms off the route as side branches (at most two deep), honouring capacities, forbidden and preferred
///      neighbours, spawn distances, escape-pod separation and the "close to" rules;
///   6. validates the result with ShipGraphValidator.
/// An attempt that cannot satisfy a rule is abandoned and the next attempt (a different stream) starts again; after maxAttempts the result
/// fails with every attempt's reason. An invalid graph is never returned as a success.
/// </summary>
public sealed class ShipGraphGenerator
{
    readonly List<RoomSpec> specs;
    readonly ShipGraphSettings settings;
    readonly ShipGraphValidator validator;
    readonly List<string> catalogueProblems = new();

    public ShipGraphSettings Settings => settings;
    public IReadOnlyList<RoomSpec> Specs => specs;
    public ShipGraphValidator Validator => validator;

    public ShipGraphGenerator(IEnumerable<RoomSpec> catalogue, ShipGraphSettings settings = null)
    {
        this.settings = settings ?? new ShipGraphSettings();
        specs = catalogue.Where(s => s != null).OrderBy(s => s.id, System.StringComparer.Ordinal).ToList();
        validator = new ShipGraphValidator(specs, this.settings);
        foreach (var dup in specs.GroupBy(s => s.id).Where(g => g.Count() > 1)) catalogueProblems.Add($"definition id '{dup.Key}' is used {dup.Count()} times");
        foreach (var s in specs)
        {
            if (string.IsNullOrEmpty(s.id)) catalogueProblems.Add($"a definition ({s.displayName}) has no id");
            if (s.minCount > s.maxCount) catalogueProblems.Add($"{s.id}: minimum count above maximum");
            if (s.minConnections > s.maxConnections) catalogueProblems.Add($"{s.id}: minimum connections above maximum");
            if (s.sectors == SectorMask.None) catalogueProblems.Add($"{s.id}: allowed in no sector");
            if (s.roles == GraphRoles.None) catalogueProblems.Add($"{s.id}: has no graph role");
        }
        foreach (var c in new[] { RoomCategory.PlayerStart, RoomCategory.Bridge, RoomCategory.Engineering, RoomCategory.EscapePodBay })
            if (!specs.Any(s => s.category == c && s.minCount > 0)) catalogueProblems.Add($"the catalogue has no mandatory {c}");
    }

    public ShipGraphResult Generate(int seed)
    {
        var result = new ShipGraphResult { seed = seed };
        if (catalogueProblems.Count > 0)
        {
            result.attemptFailures.Add("catalogue: " + string.Join("; ", catalogueProblems));
            return result;
        }
        for (int attempt = 0; attempt < settings.maxAttempts; attempt++)
        {
            result.attempts = attempt + 1;
            var rng = new ShipRng(ShipRng.Derive(seed, attempt));
            var graph = new Build(this, rng).Run(out string why);
            if (graph == null)
            {
                result.attemptFailures.Add($"attempt {attempt}: {why}");
                continue;
            }
            graph.seed = seed;
            graph.attempt = attempt;
            var report = validator.Validate(graph);
            result.graph = graph;
            result.report = report;
            if (report.Valid)
            {
                result.success = true;
                return result;
            }
            var first = report.issues.First(i => i.severity == IssueSeverity.Error);
            result.attemptFailures.Add($"attempt {attempt}: built but invalid: {first.code}: {first.message}");
        }
        return result;
    }

    // ---------- One attempt ----------

    sealed class Room
    {
        public RoomSpec spec;
        public ShipSector sector;
        public int order; // selection order, the stable tie-break
        public int node = -1;
        public int depth; // 0 on the route or a loop connector, 1 and 2 for side branches
    }

    sealed class Build
    {
        readonly ShipGraphGenerator gen;
        readonly ShipGraphSettings s;
        readonly ShipRng rng;
        readonly List<Room> rooms = new();
        readonly ShipGraph g = new();
        readonly Dictionary<int, Room> byNode = new();
        readonly Dictionary<string, int> occurrences = new();
        int[] spawnDist;

        public Build(ShipGraphGenerator gen, ShipRng rng)
        {
            this.gen = gen;
            s = gen.settings;
            this.rng = rng;
        }

        public ShipGraph Run(out string why)
        {
            why = SelectRooms() ?? AssignSectors() ?? FillStructural() ?? Layout();
            return why == null ? g : null;
        }

        // ----- 1. Which rooms -----

        string SelectRooms()
        {
            int n = rng.Range(s.minRooms, s.maxRooms);
            foreach (var spec in gen.specs.Where(x => x.tier == RoomTier.Mandatory || x.minCount > 0))
            {
                int count = rng.Range(spec.minCount, spec.maxCount);
                for (int i = 0; i < count; i++) Add(spec, ShipSector.Forward);
            }
            if (rooms.Count > n - 4) return $"the mandatory rooms ({rooms.Count}) leave no room in {n}";
            target = n;

            var pool = gen.specs.Where(x => x.tier == RoomTier.Specialised).ToList();
            int want = System.Math.Min(rng.Range(s.minSpecialised, s.maxSpecialised), n - rooms.Count - 6);
            var weights = new List<float>();
            for (int k = 0; k < want; k++)
            {
                weights.Clear();
                foreach (var spec in pool) weights.Add(Count(spec) < spec.maxCount ? spec.weight : 0f);
                int i = rng.Weighted(weights);
                if (i < 0) break;
                Add(pool[i], ShipSector.Forward);
            }
            return null;
        }

        int target;

        void Add(RoomSpec spec, ShipSector sector) => rooms.Add(new Room { spec = spec, sector = sector, order = rooms.Count });

        int Count(RoomSpec spec) => rooms.Count(r => r.spec == spec);

        // ----- 2. Sectors -----

        string AssignSectors()
        {
            var counts = new int[4];
            var tied = new List<ShipSector>();
            foreach (var r in rooms.OrderBy(r => Popcount(r.spec.sectors)).ThenBy(r => r.order))
            {
                var allowed = Sectors(r.spec.sectors);
                if (r.spec.spreadAcrossSectors)
                {
                    var unused = allowed.Where(sec => !rooms.Any(o => o != r && o.spec == r.spec && o.node == -2 && o.sector == sec)).ToList();
                    if (unused.Count > 0) allowed = unused;
                }
                if (allowed.Count == 0) return $"{r.spec.id} is allowed in no sector";
                int best = allowed.Min(sec => counts[(int)sec]);
                tied.Clear();
                tied.AddRange(allowed.Where(sec => counts[(int)sec] == best));
                r.sector = tied[rng.Range(0, tied.Count - 1)];
                r.node = -2; // marks "sector assigned" for the spread rule
                counts[(int)r.sector]++;
            }
            foreach (var r in rooms) r.node = -1;
            return null;
        }

        static int Popcount(SectorMask m)
        {
            int c = 0;
            for (int v = (int)m; v != 0; v >>= 1) c += v & 1;
            return c;
        }

        static List<ShipSector> Sectors(SectorMask m)
        {
            var list = new List<ShipSector>();
            for (int i = 0; i < 4; i++) if (((int)m & (1 << i)) != 0) list.Add((ShipSector)i);
            return list;
        }

        // ----- 3. Structural fill -----

        string FillStructural()
        {
            var pool = gen.specs.Where(x => x.tier == RoomTier.Structural).ToList();
            var weights = new List<float>();
            var tied = new List<int>();
            while (rooms.Count < target)
            {
                // The sector furthest below an even share, ties at random.
                float ideal = target / 4f;
                float worst = float.MinValue;
                tied.Clear();
                for (int sec = 0; sec < 4; sec++)
                {
                    float deficit = ideal - rooms.Count(r => (int)r.sector == sec);
                    if (deficit > worst + 1e-4f) { worst = deficit; tied.Clear(); tied.Add(sec); }
                    else if (System.Math.Abs(deficit - worst) <= 1e-4f) tied.Add(sec);
                }
                var sector = (ShipSector)tied[rng.Range(0, tied.Count - 1)];
                bool needRoute = NeedsRouteRoom(sector);
                bool needJunction = NeedsJunction(sector);
                int pick = -1;
                for (int pass = 0; pass < 3 && pick < 0; pass++)
                {
                    weights.Clear();
                    foreach (var spec in pool)
                    {
                        bool ok = spec.AllowsSector(sector) && Count(spec) < spec.maxCount;
                        if (pass == 0 && needJunction) ok &= JunctionCapable(spec);
                        if (pass <= 1 && needRoute) ok &= spec.CanBeOnRoute;
                        weights.Add(ok ? spec.weight : 0f);
                    }
                    pick = rng.Weighted(weights);
                }
                if (pick < 0) return $"no structural room left for the {sector} sector";
                Add(pool[pick], sector);
            }
            return null;
        }

        // Loops and side branches need route rooms that can take three or more connections: every sector wants its share of the route.
        bool NeedsRouteRoom(ShipSector sector) =>
            rooms.Count(r => r.sector == sector && JunctionCapable(r.spec) && !Anchored(r.spec))
            < System.Math.Max(s.minRoutePerSector + 1, (int)System.Math.Round(target / 4f * s.routeShare));

        // The two ends of the route host the Bridge and Engineering on top of a loop: they need a junction-capable room.
        bool NeedsJunction(ShipSector sector) =>
            (sector == ShipSector.Forward || sector == ShipSector.Industrial) && !rooms.Any(r => r.sector == sector && JunctionCapable(r.spec));

        // The most connections a room can end with: its definition, the ship's socket limit, and two when it may not be a junction.
        int MaxDegree(RoomSpec spec) => System.Math.Min(System.Math.Min(spec.maxConnections, s.maxDegree), spec.Allows(GraphRoles.Junction) ? int.MaxValue : 2);

        bool JunctionCapable(RoomSpec spec) => spec.Allows(GraphRoles.Junction) && MaxDegree(spec) >= 3;

        static bool Anchored(RoomSpec spec) => spec.category is RoomCategory.Bridge or RoomCategory.Engineering or RoomCategory.EscapePodBay;

        // ----- 4 and 5. Layout -----

        string Layout()
        {
            var route = new List<Room>();
            var side = new List<Room>();
            for (int sec = 0; sec < 4; sec++)
            {
                var sector = (ShipSector)sec;
                var here = rooms.Where(r => r.sector == sector).OrderBy(r => r.order).ToList();
                // Rooms that can only be a thoroughfare (airlocks, service tunnels) are kept back as loop connectors; whatever the loops do not
                // use is spliced into the route afterwards. Everything else that cannot be a dead end goes on the route.
                var must = here.Where(r => r.spec.CanBeOnRoute && !r.spec.CanBeLeaf && JunctionCapable(r.spec)).ToList();
                var passOnly = here.Where(r => r.spec.CanBeOnRoute && !r.spec.CanBeLeaf && !JunctionCapable(r.spec)).ToList();
                // The Bridge, Engineering and the escape pods are anchored side rooms (see Branches), never route rooms.
                var leafOnly = here.Where(r => !r.spec.CanBeOnRoute || Anchored(r.spec)).ToList();
                var flexible = here.Where(r => r.spec.CanBeOnRoute && r.spec.CanBeLeaf && !Anchored(r.spec)).ToList();
                rng.Shuffle(flexible);
                // Rooms that want to be near something are better as side rooms (they can choose where to hang); the rest fill the route.
                // Junction-capable rooms first (they carry the loops and the side branches); rooms that want to be near something last.
                flexible = flexible.OrderBy(r => r.spec.closeTo.Length > 0 ? 1 : 0).ThenBy(r => JunctionCapable(r.spec) ? 0 : 1).ToList();
                int want = System.Math.Max(s.minRoutePerSector, (int)System.Math.Round(here.Count * s.routeShare)) - passOnly.Count / 2;
                var onRoute = new List<Room>(must);
                // The ends of the route: Security beside the Bridge, Power Control beside Engineering, when they are in those sectors.
                var endRoom = sector == ShipSector.Forward ? RoomCategory.Security : sector == ShipSector.Industrial ? RoomCategory.PowerControl : (RoomCategory?)null;
                var preferredEnd = endRoom == null ? null : flexible.FirstOrDefault(r => r.spec.category == endRoom && JunctionCapable(r.spec));
                if (preferredEnd != null) { flexible.Remove(preferredEnd); onRoute.Add(preferredEnd); }
                // An end sector needs a junction-capable room for its end of the route.
                if ((sector == ShipSector.Forward || sector == ShipSector.Industrial) && !onRoute.Any(r => JunctionCapable(r.spec)))
                {
                    var j = flexible.FirstOrDefault(r => JunctionCapable(r.spec) && r.spec.category == (sector == ShipSector.Industrial ? RoomCategory.PowerControl : RoomCategory.Security))
                            ?? flexible.FirstOrDefault(r => JunctionCapable(r.spec));
                    if (j == null) return $"the {sector} sector has no junction-capable room for the end of the route";
                    flexible.Remove(j);
                    onRoute.Add(j);
                }
                // The route is made of rooms that can take three or more connections (it carries the loops and the side branches); a room limited
                // to two only goes on it when the sector would otherwise be too short.
                foreach (var r in flexible.Where(r => JunctionCapable(r.spec)).ToList())
                {
                    if (onRoute.Count >= want) break;
                    onRoute.Add(r);
                    flexible.Remove(r);
                }
                while (onRoute.Count < s.minRoutePerSector && flexible.Count > 0)
                {
                    onRoute.Add(flexible[0]);
                    flexible.RemoveAt(0);
                }
                if (onRoute.Count < s.minRoutePerSector) return $"the {sector} sector has only {onRoute.Count} route room(s)";
                route.AddRange(OrderSector(sector, onRoute));
                side.AddRange(passOnly);
                side.AddRange(leafOnly);
                side.AddRange(flexible);
            }
            if (route.Count < s.minRouteLength) return $"the route has only {route.Count} rooms";

            foreach (var r in route) Place(r, 0);
            for (int i = 1; i < route.Count; i++) g.AddEdge(route[i - 1].node, route[i].node);
            g.route.AddRange(route.Select(r => r.node));
            int head = route[0].node, tail = route[^1].node;
            Reserve(head, 1); // the Bridge
            Reserve(tail, 1); // Engineering

            string why = Loops(route, side);
            if (why != null) return why;
            SpliceUnusedConnectors(side);
            spawnDist = g.Distances(g.FirstOf(RoomCategory.PlayerStart));
            return Branches(side, head, tail);
        }

        readonly Dictionary<int, int> reserve = new();
        void Reserve(int node, int n) => reserve[node] = (reserve.TryGetValue(node, out int v) ? v : 0) + n;
        int Reserved(int node) => reserve.TryGetValue(node, out int v) ? v : 0;
        int Capacity(int node) => MaxDegree(byNode[node].spec) - g.nodes[node].Degree - Reserved(node);

        List<Room> OrderSector(ShipSector sector, List<Room> onRoute)
        {
            var list = new List<Room>(onRoute);
            rng.Shuffle(list);
            if (sector == ShipSector.Forward)
            {
                var head = list.Where(r => JunctionCapable(r.spec)).OrderBy(r => r.spec.category == RoomCategory.Security ? 0 : r.spec.category == RoomCategory.CorridorJunction ? 1 : 2).First();
                list.Remove(head);
                list.Insert(0, head);
            }
            if (sector == ShipSector.Industrial)
            {
                var tail = list.Where(r => JunctionCapable(r.spec)).OrderBy(r => r.spec.category == RoomCategory.PowerControl ? 0 : r.spec.category == RoomCategory.CargoBay ? 2 : 1).First();
                list.Remove(tail);
                list.Add(tail);
            }
            if (sector == ShipSector.Crew)
            {
                var spawn = list.FirstOrDefault(r => r.spec.category == RoomCategory.PlayerStart);
                if (spawn != null) { list.Remove(spawn); list.Add(spawn); } // the aft end: the Bridge is the far end of the ship from here
            }
            return list;
        }

        void Place(Room r, int depth)
        {
            occurrences.TryGetValue(r.spec.id, out int k);
            occurrences[r.spec.id] = k + 1;
            var node = g.AddNode(r.spec, r.sector, r.spec.deck >= 0 ? r.spec.deck : 0, k);
            r.node = node.index;
            r.depth = depth;
            byNode[node.index] = r;
        }

        bool Forbidden(RoomSpec a, RoomSpec b) => a.forbiddenNeighbours.Contains(b.category) || b.forbiddenNeighbours.Contains(a.category);

        // Overlapping chords along the route: chord k spans route positions (a_k, b_k), with a_{k+1} < b_k, so every interior route room lies
        // strictly inside some chord. That makes the route biconnected: removing any one room never splits it.
        string Loops(List<Room> route, List<Room> side)
        {
            int m = route.Count - 1;
            int a = 0, chords = 0;
            var candidates = new List<int>();
            for (int guard = 0; guard < 64; guard++)
            {
                candidates.Clear();
                int lo = System.Math.Min(a + s.minLoopSpan, m), hi = System.Math.Min(a + s.maxLoopSpan, m);
                for (int b = lo; b <= hi; b++) if (Capacity(route[b].node) >= 1 && !Forbidden(route[a].spec, route[b].spec)) candidates.Add(b);
                if (candidates.Count == 0) // a shorter loop, still skipping at least one room
                    for (int b = a + 2; b < lo; b++) if (Capacity(route[b].node) >= 1 && !Forbidden(route[a].spec, route[b].spec)) candidates.Add(b);
                if (candidates.Count == 0) return $"no loop can start at route position {a}";
                int end = candidates.Contains(m) ? m : candidates[rng.Range(0, candidates.Count - 1)];
                Chord(route[a], route[end], side);
                chords++;
                if (end == m) break;
                // The next chord starts strictly inside this one (1 or 2 rooms back from its end), so the two overlap.
                candidates.Clear();
                for (int b = end - 1; b > a; b--) if (Capacity(route[b].node) >= 1) candidates.Add(b);
                if (candidates.Count == 0) return $"no loop can overlap the one ending at route position {end}";
                a = candidates[rng.Range(0, System.Math.Min(1, candidates.Count - 1))]; // one of the two latest: overlapping, but moving on
            }
            // A short route may need extra loops: further chords between route rooms two to six apart that are not yet joined.
            for (int tries = 0; chords < s.minLoops && tries < 40; tries++)
            {
                int x = rng.Range(0, m - 2), y = System.Math.Min(m, x + rng.Range(2, s.maxLoopSpan));
                if (Capacity(route[x].node) < 1 || Capacity(route[y].node) < 1 || g.Connected(route[x].node, route[y].node) || Forbidden(route[x].spec, route[y].spec)) continue;
                Chord(route[x], route[y], side);
                chords++;
            }
            if (chords < s.minLoops) return $"only {chords} loop(s) fit along a route of {route.Count}";
            return null;
        }

        // A chord goes through a free connector room (a structural side room that can be a thoroughfare) when there is one in either end's
        // sector, otherwise it is a direct connection between the two route rooms.
        void Chord(Room a, Room b, List<Room> side)
        {
            var connectors = side.Where(r => r.node < 0 && r.spec.tier != RoomTier.Mandatory && (r.sector == a.sector || r.sector == b.sector) && r.spec.Allows(GraphRoles.Thoroughfare)
                                             && MaxDegree(r.spec) >= 2 && !Forbidden(r.spec, a.spec) && !Forbidden(r.spec, b.spec))
                                 .OrderBy(r => r.order).ToList();
            // Pass-only rooms (an airlock, a service tunnel) first: a loop is exactly what they are for. Then other structural rooms.
            var passOnly = connectors.Where(r => !r.spec.CanBeLeaf).ToList();
            var structural = connectors.Where(r => r.spec.tier == RoomTier.Structural).ToList();
            var pool = passOnly.Count > 0 ? passOnly : structural.Count > 0 && rng.Chance(0.7) ? structural : null;
            if (pool != null)
            {
                var c = pool[rng.Range(0, pool.Count - 1)];
                Place(c, 0);
                g.AddEdge(a.node, c.node);
                g.AddEdge(c.node, b.node);
                return;
            }
            g.AddEdge(a.node, b.node);
        }

        // Pass-only rooms the loops did not use go into the route between two route rooms of their sector (splitting a connection keeps
        // every loop intact).
        void SpliceUnusedConnectors(List<Room> side)
        {
            foreach (var c in side.Where(r => r.node < 0 && !r.spec.CanBeLeaf).ToList())
            {
                var slots = new List<int>();
                for (int k = 1; k < g.route.Count; k++)
                {
                    var u = byNode[g.route[k - 1]]; var v = byNode[g.route[k]];
                    if ((u.sector == c.sector || v.sector == c.sector) && !Forbidden(u.spec, c.spec) && !Forbidden(v.spec, c.spec)) slots.Add(k);
                }
                if (slots.Count == 0) continue; // left for the branches, where it will fail clearly if it cannot be placed
                int at = slots[rng.Range(0, slots.Count - 1)];
                int a = g.route[at - 1], b = g.route[at];
                Place(c, 0);
                g.RemoveEdge(a, b);
                g.AddEdge(a, c.node);
                g.AddEdge(c.node, b);
                g.route.Insert(at, c.node);
            }
        }

        // ----- Side branches -----

        string Branches(List<Room> side, int head, int tail)
        {
            var remaining = side.Where(r => r.node < 0).ToList();
            rng.Shuffle(remaining);
            // Anchored rooms first, then rooms that can carry a further branch, then the escape pods, then plain dead ends.
            remaining = remaining.OrderBy(r => r.spec.category == RoomCategory.Bridge ? 0 : r.spec.category == RoomCategory.Engineering ? 1
                                               : r.spec.CanBeOnRoute && !Anchored(r.spec) ? 2 : r.spec.category == RoomCategory.EscapePodBay ? 3 : 4).ToList();
            var candidates = new List<(int node, double score)>();
            foreach (var r in remaining)
            {
                if (r.spec.category == RoomCategory.Bridge) { reserve[head] = Reserved(head) - 1; if (!Attach(r, head)) return "the Bridge cannot hang off the forward end of the route"; continue; }
                if (r.spec.category == RoomCategory.Engineering) { reserve[tail] = Reserved(tail) - 1; if (!Attach(r, tail)) return "Engineering cannot hang off the aft end of the route"; continue; }

                candidates.Clear();
                FindHosts(r, r.sector, candidates);
                // No room in its sector: another sector its definition allows (the room then belongs to that sector).
                if (candidates.Count == 0)
                    foreach (var other in Sectors(r.spec.sectors).Where(x => x != r.sector).OrderBy(x => System.Math.Abs((int)x - (int)r.sector)).ThenBy(x => (int)x))
                    {
                        FindHosts(r, other, candidates);
                        if (candidates.Count == 0) continue;
                        r.sector = other;
                        break;
                    }
                if (candidates.Count == 0) return $"no place for {r.spec.id} in the {r.sector} sector";
                var best = candidates[0];
                foreach (var c in candidates) if (c.score > best.score) best = c;
                Attach(r, best.node);
            }
            return null;
        }

        void FindHosts(Room r, ShipSector sector, List<(int node, double score)> candidates)
        {
                foreach (var host in g.nodes)
                {
                    var hr = byNode[host.index];
                    if (hr.sector != sector || hr.depth + 1 > s.maxBranchDepth || Capacity(host.index) < 1) continue;
                    if (!hr.spec.Allows(RoomSpec.RoleForDegree(host.Degree + 1)) || Forbidden(hr.spec, r.spec)) continue;
                    int d = spawnDist[host.index] + 1;
                    if (spawnDist[host.index] < 0 || (r.spec.minSpawnDistance >= 0 && d < r.spec.minSpawnDistance) || (r.spec.maxSpawnDistance >= 0 && d > r.spec.maxSpawnDistance)) continue;
                    if (r.spec.category == RoomCategory.EscapePodBay && !PodHostOk(host.index)) continue;
                    if (r.spec.closeToStrict && r.spec.closeTo.Length > 0 && !CloseEnough(host.index, r.spec, out bool targetExists) && targetExists) continue;
                    double score = rng.NextDouble() - 0.6 * host.Degree + (hr.depth == 0 ? 0.5 : 0);
                    if (r.spec.preferredNeighbours.Contains(hr.spec.category) || hr.spec.preferredNeighbours.Contains(r.spec.category)) score += 2;
                    if (r.spec.closeTo.Length > 0 && CloseEnough(host.index, r.spec, out _)) score += 3;
                    candidates.Add((host.index, score));
                }
        }

        bool Attach(Room r, int host)
        {
            if (Capacity(host) < 1 || Forbidden(byNode[host].spec, r.spec)) return false;
            Place(r, byNode[host].depth + 1);
            g.AddEdge(host, r.node);
            if (spawnDist.Length < g.nodes.Count) System.Array.Resize(ref spawnDist, g.nodes.Count);
            spawnDist[r.node] = spawnDist[host] < 0 ? -1 : spawnDist[host] + 1;
            return true;
        }

        // A second escape pod hangs off a different room and far enough from the first.
        bool PodHostOk(int host)
        {
            foreach (int other in g.AllOf(RoomCategory.EscapePodBay))
            {
                int host0 = g.nodes[other].neighbours.Count > 0 ? g.nodes[other].neighbours[0] : -1;
                if (host0 == host) return false;
                int d = g.Distance(host, other);
                if (d >= 0 && d + 1 < s.minEscapePodSeparation) return false;
            }
            return true;
        }

        // Would a room hung off this host be within its closeTo distance of a target? targetExists is false when no target room is placed yet.
        bool CloseEnough(int host, RoomSpec spec, out bool targetExists)
        {
            var d = g.Distances(host);
            targetExists = false;
            bool close = false;
            foreach (var n in g.nodes)
            {
                if (!spec.closeTo.Contains(n.category)) continue;
                targetExists = true;
                if (d[n.index] >= 0 && d[n.index] + 1 <= spec.closeToDistance) close = true;
            }
            return close;
        }
    }
}
