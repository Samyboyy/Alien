using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for the physical ship placement (pure: no assets, scenes or Unity types). Multi-seed tests name the failing seed so it can be
// reproduced in Alien > Procedural Ship > Physical Ship.
public class ShipPlacementTests
{
    const int RepresentativeSeeds = 40;

    static readonly List<RoomSpec> Specs = DefaultRoomCatalogue.Create();
    static readonly TemplateLibrary Library = TemplateLibrary.FromBlueprints();
    static readonly Dictionary<int, (ShipGraphResult graph, ShipPlacementResult placement)> cache = new();

    static ShipPlacer NewPlacer(PlacementSettings s = null) => new(Library, Specs, s);

    static (ShipGraphResult graph, ShipPlacementResult placement) Placed(int seed)
    {
        if (cache.TryGetValue(seed, out var r)) return r;
        var g = new ShipGraphGenerator(Specs).Generate(seed);
        Assert.IsTrue(g.success, $"seed {seed}: the logical graph failed: {g.Summary}");
        var p = NewPlacer().Place(g.graph);
        return cache[seed] = (g, p);
    }

    static string Why(int seed, string what, ShipPlacementResult p) =>
        $"seed {seed}: {what}\n{(p.success ? p.layout.Summary() : string.Join("\n", p.attemptFailures.TakeLast(3)) + "\n" + p.failureMap)}";

    // Deep copy of a layout so a test can break it without touching the cache.
    static ShipLayout Copy(ShipLayout l)
    {
        var c = new ShipLayout { seed = l.seed, graph = l.graph, settings = l.settings };
        foreach (var r in l.rooms) c.rooms.Add(new RoomPlacement { node = r.node, template = r.template, rot = r.rot, origin = r.origin, deck = r.deck });
        foreach (var k in l.connections)
        {
            var n = new PhysicalConnection { edge = k.edge, edgeId = k.edgeId, nodeA = k.nodeA, socketA = k.socketA, nodeB = k.nodeB, socketB = k.socketB, mode = k.mode };
            n.cells.AddRange(k.cells);
            c.connections.Add(n);
        }
        c.sealedSockets.AddRange(l.sealedSockets);
        c.ComputeBounds();
        return c;
    }

    static LayoutReport Validate(ShipLayout l) => new ShipLayoutValidator(l.settings).Validate(l, Specs);

    // ---------- Grid and socket maths ----------

    [Test]
    public void RotatingACellMatchesRotatingItsPointInContinuousSpace()
    {
        // Unity yaws a prefab about its pivot (the footprint's minimum corner); the integer maths must agree with the real rotation for every
        // footprint, cell and turn, including the shift that puts the minimum corner back at the origin.
        foreach (var (sx, sz) in new[] { (1, 1), (3, 2), (5, 4), (2, 7) })
            for (int rot = 0; rot < 4; rot++)
                for (int x = 0; x < sx; x++)
                    for (int z = 0; z < sz; z++)
                    {
                        var cell = Grid.RotateCell(new Int2(x, z), sx, sz, rot);
                        Grid.RotatedSize(sx, sz, rot, out int rx, out int rz);
                        Assert.That(cell.x, Is.InRange(0, rx - 1), $"{sx}x{sz} rot {rot} cell {x},{z}");
                        Assert.That(cell.z, Is.InRange(0, rz - 1));

                        // The same point through a real clockwise yaw (x' = z, z' = -x), then the pivot offset.
                        float px = x + 0.5f, pz = z + 0.5f;
                        float ax = px, az = pz;
                        for (int i = 0; i < rot; i++) (ax, az) = (az, -ax);
                        var off = Grid.PivotOffset(sx, sz, rot);
                        ax += off.x;
                        az += off.z;
                        Assert.AreEqual(cell.x + 0.5f, ax, 1e-4, $"{sx}x{sz} rot {rot} cell {x},{z} x");
                        Assert.AreEqual(cell.z + 0.5f, az, 1e-4, $"{sx}x{sz} rot {rot} cell {x},{z} z");
                    }
    }

    [Test]
    public void FourQuarterTurnsAreTheIdentity()
    {
        var c = new Int2(2, 1);
        Assert.AreEqual(c, Grid.RotateCell(c, 5, 3, 0));
        Assert.AreEqual(c, Grid.RotateCell(c, 5, 3, 4));
        Assert.AreEqual(GridSide.East, Grid.Rotate(GridSide.North, 1));
        Assert.AreEqual(GridSide.North, Grid.Rotate(GridSide.North, 4));
        Assert.AreEqual(GridSide.West, Grid.Rotate(GridSide.North, -1));
        Assert.AreEqual(GridSide.South, Grid.Opposite(GridSide.North));
        Assert.AreEqual(0b0101, Grid.RotateMask(0b1010, 1));
    }

    [Test]
    public void EverySocketOfEveryTemplateStaysOnItsEdgeUnderEveryRotation()
    {
        foreach (var t in Library.All)
            for (int rot = 0; rot < 4; rot++)
            {
                Grid.RotatedSize(t.sizeX, t.sizeZ, rot, out int sx, out int sz);
                for (int s = 0; s < t.sockets.Count; s++)
                {
                    var cell = t.SocketCell(s, rot);
                    var side = t.SocketSide(s, rot);
                    bool onEdge = side switch
                    {
                        GridSide.North => cell.z == sz - 1,
                        GridSide.South => cell.z == 0,
                        GridSide.East => cell.x == sx - 1,
                        _ => cell.x == 0,
                    };
                    Assert.IsTrue(onEdge, $"{t.id} socket {t.sockets[s].id} rot {rot}: cell {cell} is not on its {side} edge of a {sx}x{sz} footprint");
                }
            }
    }

    [Test]
    public void TwoRoomsPlacedAcrossAWallHaveSocketsThatMeetAtEveryRotation()
    {
        // Place room B directly east of room A through their sockets, for every rotation of both: the outside cell of A's socket must be B's
        // boundary cell, and B's must be A's.
        var t = Library.For("mess_hall")[0];
        for (int ra = 0; ra < 4; ra++)
            for (int rb = 0; rb < 4; rb++)
                for (int sa = 0; sa < t.sockets.Count; sa++)
                    for (int sb = 0; sb < t.sockets.Count; sb++)
                    {
                        var a = new RoomPlacement { template = t, rot = ra, origin = new Int2(0, 0) };
                        if (a.SocketSide(sa) != GridSide.East || t.SocketSide(sb, rb) != GridSide.West) continue;
                        var outside = a.OutsideCell(sa);
                        var b = new RoomPlacement { template = t, rot = rb, origin = outside - t.SocketCell(sb, rb) };
                        Assert.AreEqual(outside, b.SocketCell(sb), $"ra {ra} rb {rb} sa {sa} sb {sb}");
                        Assert.AreEqual(b.OutsideCell(sb), a.SocketCell(sa));
                        Assert.IsFalse(a.Overlaps(b), "touching rooms do not overlap");
                        Assert.IsTrue(SocketRules.Aligned(a.SocketSide(sa), a.SocketCell(sa), b.SocketSide(sb), b.SocketCell(sb), ConnectionMode.Direct));
                    }
    }

    [Test]
    public void SocketCompatibilityChecksEveryProperty()
    {
        SocketSpec S() => new() { id = "x", side = GridSide.North, support = SocketSupport.Corridor | SocketSupport.Direct };
        Assert.IsTrue(SocketRules.Compatibility(S(), S()).ok);

        var wide = S(); wide.width = SocketWidth.Wide;
        Assert.IsFalse(SocketRules.Compatibility(S(), wide).ok);
        StringAssert.Contains("width", SocketRules.Compatibility(S(), wide).reason);

        var tall = S(); tall.height = SocketHeight.Tall;
        Assert.IsFalse(SocketRules.Compatibility(S(), tall).ok);

        var service = S(); service.type = SocketConnectionType.Service;
        Assert.IsFalse(SocketRules.Compatibility(S(), service).ok);

        var noDoor = S(); noDoor.door = DoorPolicy.Forbidden;
        var needsDoor = S(); needsDoor.door = DoorPolicy.Required;
        Assert.IsFalse(SocketRules.Compatibility(noDoor, needsDoor).ok, "a required door against a forbidden one");
        Assert.IsTrue(SocketRules.Compatibility(noDoor, S()).ok);
        Assert.IsTrue(SocketRules.NeedsDoor(needsDoor, S()));

        var airlockOnly = S(); airlockOnly.support = SocketSupport.Airlock;
        Assert.IsFalse(SocketRules.Compatibility(S(), airlockOnly).ok, "no shared connection mode");
        var all = S(); all.support = SocketSupport.All;
        Assert.AreEqual(SocketSupport.Corridor | SocketSupport.Direct, SocketRules.Compatibility(S(), all).modes);

        Assert.IsTrue(SocketRules.Aligned(GridSide.East, new Int2(3, 1), GridSide.West, new Int2(4, 1), ConnectionMode.Direct));
        Assert.IsFalse(SocketRules.Aligned(GridSide.East, new Int2(3, 1), GridSide.West, new Int2(4, 2), ConnectionMode.Direct), "a cell off");
        Assert.IsFalse(SocketRules.Aligned(GridSide.East, new Int2(3, 1), GridSide.East, new Int2(4, 1), ConnectionMode.Direct), "not facing");
    }

    [Test]
    public void CorridorModulesClassifyUnderEveryRotation()
    {
        foreach (var kind in new[] { CorridorPieceKind.Straight, CorridorPieceKind.Corner, CorridorPieceKind.Tee, CorridorPieceKind.Cross, CorridorPieceKind.DeadEnd })
            for (int rot = 0; rot < 4; rot++)
            {
                int mask = CorridorMath.OpenMask(kind, rot);
                Assert.IsTrue(CorridorMath.Classify(mask, out var k2, out int r2), $"{kind} rot {rot}");
                Assert.AreEqual(mask, CorridorMath.OpenMask(k2, r2), $"{kind} rot {rot} round trip");
                if (kind != CorridorPieceKind.Straight && kind != CorridorPieceKind.Cross) continue;
                Assert.AreEqual(kind, k2);
            }
        Assert.IsFalse(CorridorMath.Classify(0, out _, out _));
        Assert.AreEqual(CorridorPieceKind.Corner, Classified(Grid.Bit(GridSide.South) | Grid.Bit(GridSide.West)));
        Assert.AreEqual(CorridorPieceKind.Tee, Classified(Grid.Bit(GridSide.East) | Grid.Bit(GridSide.South) | Grid.Bit(GridSide.West)));
    }

    static CorridorPieceKind Classified(int mask) { CorridorMath.Classify(mask, out var k, out _); return k; }

    // ---------- Room blueprints (the greybox prefabs' source) ----------

    [Test]
    public void TheDefaultLibraryCoversEveryDefinitionAndIsWellFormed()
    {
        var problems = Library.Problems(Specs);
        Assert.IsEmpty(problems, string.Join("\n", problems));
        Assert.AreEqual(42, Library.All.Count());
        foreach (var spec in Specs)
            Assert.GreaterOrEqual(Library.For(spec.id).Max(t => t.sockets.Count), Math.Min(spec.maxConnections, 4), $"{spec.id} needs a variant with enough sockets");
    }

    [Test]
    public void RoomTypesAreSpatiallyDistinct()
    {
        RoomTemplate T(string id) => Library.For(id)[0];
        Assert.GreaterOrEqual(T("bridge").Area, 40, "the bridge is a wide command room");
        Assert.AreEqual(1, T("bridge").sockets.Count, "with limited entrances");
        Assert.GreaterOrEqual(T("cargo_bay").Area, 64, "the cargo bay is the largest open space");
        Assert.GreaterOrEqual(T("engineering").Area, 56);
        Assert.Greater(T("cargo_bay").Area, T("workshop").Area * 3);
        Assert.Less(T("small_storage").Area, 10);
        Assert.AreEqual(2, Library.For("crew_quarters").Count, "crew quarters has two layouts");
        Assert.AreEqual(2, Library.For("corridor_junction").Count);
        var footprints = Specs.Where(s => s.tier == RoomTier.Mandatory).Select(s => (T(s.id).sizeX, T(s.id).sizeZ)).Distinct().Count();
        Assert.GreaterOrEqual(footprints, 7, "the mandatory rooms do not all share a few footprints");
    }

    [Test]
    public void NoFurnitureBlocksADoorwayAndEveryRoomIsWalkable()
    {
        var problems = new List<string>();
        foreach (var bp in GreyboxBlueprints.Create())
        {
            foreach (var s in bp.sockets)
                foreach (var b in bp.boxes)
                    if (b.y0 < 1.8f && b.height > 0.4f && BoxInCell(b, s)) problems.Add($"{bp.variantId}: '{b.name}' stands in the doorway cell of socket {s.id}");
            WalkabilityProblems(bp, problems);
        }
        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    static bool BoxInCell(BoxSpec b, SocketSpec s)
    {
        float x0 = s.cell.x * 2f, z0 = s.cell.z * 2f;
        return b.x0 < x0 + 2f && b.x0 + b.width > x0 && b.z0 < z0 + 2f && b.z0 + b.depth > z0;
    }

    // A 0.25 m raster of the room: solid furniture inflated by a walker's radius blocks, the walls block, and every doorway and every floor anchor
    // must be reachable from the first doorway.
    static void WalkabilityProblems(RoomBlueprint bp, List<string> problems)
    {
        const float res = 0.25f, radius = 0.4f;
        int nx = (int)(bp.Width / res), nz = (int)(bp.Depth / res);
        bool Free(int ix, int iz)
        {
            float x = (ix + 0.5f) * res, z = (iz + 0.5f) * res;
            if (x < GreyboxScale.WallThickness + 0.2f || z < GreyboxScale.WallThickness + 0.2f || x > bp.Width - GreyboxScale.WallThickness - 0.2f || z > bp.Depth - GreyboxScale.WallThickness - 0.2f)
                return false;
            foreach (var b in bp.boxes)
                if (b.y0 < 1.8f && b.height >= 0.5f && b.Overlaps(x, z, radius)) return false;
            return true;
        }
        (int, int) Cell(float x, float z) => ((int)(x / res), (int)(z / res));

        var starts = new List<(string name, int x, int z)>();
        foreach (var s in bp.sockets)
        {
            RoomBlueprint.SocketPoint(s, out float x, out float z);
            float inward = 1.0f; // one metre in from the wall line, in the middle of the doorway cell
            float ix = s.side == GridSide.East ? x - inward : s.side == GridSide.West ? x + inward : x;
            float iz = s.side == GridSide.North ? z - inward : s.side == GridSide.South ? z + inward : z;
            var (cx, cz) = Cell(ix, iz);
            starts.Add(($"socket {s.id}", cx, cz));
        }
        foreach (var a in bp.anchors)
        {
            if (a.kind != AnchorKind.PatrolPoint && a.kind != AnchorKind.SearchPoint && a.kind != AnchorKind.PlayerSpawn && a.kind != AnchorKind.Hiding && a.kind != AnchorKind.Objective) continue;
            var (cx, cz) = Cell(a.x, a.z);
            starts.Add(($"{a.kind} anchor '{a.id}'", cx, cz));
        }
        var seen = new bool[nx, nz];
        var queue = new Queue<(int, int)>();
        var first = starts[0];
        if (!Free(first.x, first.z)) { problems.Add($"{bp.variantId}: {first.name} is not on free floor"); return; }
        queue.Enqueue((first.x, first.z));
        seen[first.x, first.z] = true;
        while (queue.Count > 0)
        {
            var (x, z) = queue.Dequeue();
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int ax = x + dx, az = z + dz;
                if (ax < 0 || az < 0 || ax >= nx || az >= nz || seen[ax, az] || !Free(ax, az)) continue;
                seen[ax, az] = true;
                queue.Enqueue((ax, az));
            }
        }
        foreach (var st in starts)
        {
            if (!(st.x >= 0 && st.z >= 0 && st.x < nx && st.z < nz && Free(st.x, st.z))) problems.Add($"{bp.variantId}: {st.name} stands on furniture or in a wall");
            else if (!seen[st.x, st.z]) problems.Add($"{bp.variantId}: {st.name} cannot be walked to from {first.name}");
        }
    }

    // ---------- Placement ----------

    [Test]
    public void IdenticalSeedsGiveIdenticalLayouts()
    {
        foreach (int seed in new[] { 0, 1, 7, 42, 123 })
        {
            var g = new ShipGraphGenerator(Specs).Generate(seed);
            var a = NewPlacer().Place(g.graph);
            var b = NewPlacer().Place(new ShipGraphGenerator(Specs).Generate(seed).graph); // everything rebuilt from scratch
            Assert.IsTrue(a.success && b.success, Why(seed, "did not place", a));
            Assert.AreEqual(a.layout.Canonical(), b.layout.Canonical(), $"seed {seed} placed differently the second time");
            Assert.AreEqual(a.layout.Fingerprint(), b.layout.Fingerprint());
        }
    }

    [Test]
    public void DifferentSeedsGiveDifferentLayouts()
    {
        var prints = Enumerable.Range(0, 12).Select(s => Placed(s).placement.layout?.Fingerprint()).ToList();
        Assert.IsFalse(prints.Contains(null), "all of the first 12 seeds placed");
        Assert.AreEqual(12, prints.Distinct().Count());
    }

    [Test]
    public void RepresentativeSeedsPlaceValidShips()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int seed = 0; seed < RepresentativeSeeds; seed++)
        {
            var (_, p) = Placed(seed);
            Assert.IsTrue(p.success, Why(seed, "placement failed", p));
            Assert.IsTrue(p.report.Valid, Why(seed, string.Join("; ", p.report.issues), p));
            var l = p.layout;
            Assert.That(l.rooms.Count, Is.InRange(24, 30), Why(seed, "room count", p));
            Assert.GreaterOrEqual(p.report.spatialLoops, 3, Why(seed, "at least three spatial loops", p));
            Assert.LessOrEqual(l.WidthCells, l.settings.maxExtentX, Why(seed, "inside the boundary (X)", p));
            Assert.LessOrEqual(l.DepthCells, l.settings.maxExtentZ, Why(seed, "inside the boundary (Z)", p));
            Assert.GreaterOrEqual(l.WidthCells * l.settings.cellSize, 120f, Why(seed, "much larger than the 70 m Ship scene", p));
            Assert.GreaterOrEqual(l.SpineLengthMetres(), 100f, Why(seed, "a long spine", p));
            Assert.Less(l.CorridorCellCount, l.rooms.Sum(r => r.template.Area) * 0.6, Why(seed, "a dense ship, not empty corridors", p));
        }
        Assert.Less(sw.Elapsed.TotalSeconds, 120, "placement stays bounded");
    }

    [Test]
    public void EveryLogicalNodeHasExactlyOnePhysicalRoomAndEveryEdgeAConnection()
    {
        for (int seed = 0; seed < 15; seed++)
        {
            var (g, p) = Placed(seed);
            var l = p.layout;
            Assert.AreEqual(g.graph.NodeCount, l.rooms.Count, Why(seed, "rooms", p));
            for (int i = 0; i < l.rooms.Count; i++)
            {
                Assert.AreEqual(i, l.rooms[i].node, Why(seed, "node index", p));
                Assert.AreEqual(g.graph.nodes[i].definitionId, l.rooms[i].template.definitionId, Why(seed, g.graph.nodes[i].id, p));
                Assert.AreEqual(g.graph.nodes[i].deck, l.rooms[i].deck);
            }
            Assert.AreEqual(g.graph.EdgeCount, l.connections.Count, Why(seed, "connections", p));
            for (int e = 0; e < l.connections.Count; e++)
            {
                Assert.AreEqual(e, l.connections[e].edge);
                Assert.AreEqual(g.graph.edges[e].id, l.connections[e].edgeId);
                var ends = new[] { l.connections[e].nodeA, l.connections[e].nodeB }.OrderBy(x => x).ToArray();
                CollectionAssert.AreEqual(new[] { Math.Min(g.graph.edges[e].a, g.graph.edges[e].b), Math.Max(g.graph.edges[e].a, g.graph.edges[e].b) }, ends, Why(seed, "edge " + e, p));
            }
        }
    }

    [Test]
    public void SpatialRulesHoldOnTheLayout()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var (g, p) = Placed(seed);
            var l = p.layout;
            var gr = g.graph;
            int bridge = gr.FirstOf(RoomCategory.Bridge), eng = gr.FirstOf(RoomCategory.Engineering);
            double Cx(int n) => l.rooms[n].Centre2.x * 0.5;
            Assert.Less(Cx(bridge), Cx(eng) - 40, Why(seed, "the Bridge is well forward of Engineering", p));
            Assert.LessOrEqual((Cx(bridge) - l.Min.x) / l.WidthCells, l.settings.endZoneShare, Why(seed, "the Bridge is in the forward zone", p));
            foreach (int pod in gr.AllOf(RoomCategory.EscapePodBay)) Assert.IsTrue(ShipLayoutValidator.Exposed(l, pod), Why(seed, gr.nodes[pod].id + " is on the outside", p));
            var pods = gr.AllOf(RoomCategory.EscapePodBay);
            var a = l.rooms[pods[0]].Centre2;
            var b = l.rooms[pods[1]].Centre2;
            Assert.GreaterOrEqual((Math.Abs(a.x - b.x) + Math.Abs(a.z - b.z)) / 2, l.settings.minPodSeparationCells, Why(seed, "pods apart", p));
            // The sectors follow one another along the ship.
            var order = new[] { ShipSector.Forward, ShipSector.Central, ShipSector.Crew, ShipSector.Industrial }
                .Select(s => gr.nodes.Where(n => n.sector == s).Average(n => Cx(n.index))).ToArray();
            for (int i = 1; i < order.Length; i++) Assert.Greater(order[i], order[i - 1], Why(seed, "sector order", p));
        }
    }

    [Test]
    public void EveryUnusedSocketIsSealedAndNoCorridorEndIsOpen()
    {
        for (int seed = 0; seed < 15; seed++)
        {
            var (_, p) = Placed(seed);
            var l = p.layout;
            int sockets = l.rooms.Sum(r => r.template.sockets.Count);
            Assert.AreEqual(sockets, l.connections.Count * 2 + l.sealedSockets.Count, Why(seed, "every socket is used or sealed", p));
            Assert.IsFalse(p.report.Has(LayoutIssueCode.UnsealedSocket) || p.report.Has(LayoutIssueCode.OpenCorridorEnd), Why(seed, "no holes", p));
        }
    }

    [Test]
    public void CorridorsCrossOnlyAtCleanCrossModules()
    {
        int crossings = 0;
        for (int seed = 0; seed < 25; seed++)
        {
            var (_, p) = Placed(seed);
            foreach (var piece in CorridorMath.Pieces(p.layout))
            {
                Assert.IsTrue(piece.valid, Why(seed, $"piece at {piece.cell}", p));
                if (piece.owners > 1) { Assert.AreEqual(CorridorPieceKind.Cross, piece.kind); crossings++; }
            }
        }
        Assert.Greater(crossings, 0, "the sample contains junctions where corridors cross");
    }

    [Test]
    public void LayoutsStayInsideTheBoundaryAndFailCleanlyWhenTheyCannotFit()
    {
        var g = new ShipGraphGenerator(Specs).Generate(3).graph;
        var tight = new PlacementSettings { maxExtentX = 20, maxExtentZ = 12, maxAttempts = 6, maxStepsPerAttempt = 60 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = NewPlacer(tight).Place(g);
        Assert.IsFalse(r.success);
        Assert.AreEqual(6, r.attempts, "it stopped after the attempt limit, not before and not after");
        Assert.AreEqual(6, r.attemptFailures.Count, "one reason per attempt");
        Assert.IsNull(r.layout);
        StringAssert.Contains("could not place", r.attemptFailures[0]);
        StringAssert.Contains("room ", r.attemptFailures[0]);
        Assert.IsNotEmpty(r.failureMap, "a map of the furthest layout");
        Assert.Less(sw.Elapsed.TotalSeconds, 20, "failure is bounded, not an endless retry");
        // The same failure, the same words, every time.
        var again = NewPlacer(tight).Place(g);
        CollectionAssert.AreEqual(r.attemptFailures, again.attemptFailures);
    }

    [Test]
    public void ProblemsInputsFailBeforeSearching()
    {
        var g = new ShipGraphGenerator(Specs).Generate(1).graph;
        var noLibrary = new ShipPlacer(new TemplateLibrary(), Specs).Place(g);
        Assert.IsFalse(noLibrary.success);
        StringAssert.Contains("no room template", noLibrary.attemptFailures[0]);

        var multiDeck = g.Clone();
        multiDeck.nodes[3].deck = 1;
        var r = NewPlacer().Place(multiDeck);
        Assert.IsFalse(r.success);
        StringAssert.Contains("deck 0", r.attemptFailures[0]);
        Assert.IsFalse(NewPlacer().Place(new ShipGraph()).success);
    }

    // ---------- The layout validator catches broken layouts ----------

    [Test]
    public void AValidLayoutHasNoErrors()
    {
        var (_, p) = Placed(2);
        var rep = Validate(Copy(p.layout));
        Assert.IsTrue(rep.Valid, string.Join("\n", rep.issues));
    }

    [Test]
    public void AMisalignedRoomIsCaught()
    {
        var (_, p) = Placed(2);
        var l = Copy(p.layout);
        var c = l.connections.First(k => k.mode == ConnectionMode.Corridor);
        l.rooms[c.nodeB].origin += new Int2(1, 0); // one cell off its corridor
        var rep = Validate(l);
        Assert.IsFalse(rep.Valid);
        Assert.IsTrue(rep.Has(LayoutIssueCode.SocketMisaligned), string.Join("\n", rep.issues.Take(5)));
    }

    [Test]
    public void AMisalignedDirectConnectionIsCaught()
    {
        // Placement rarely chooses a direct connection, so build one: two mess halls touching through east and west sockets.
        var t = Library.For("mess_hall")[0];
        var g = new ShipGraph();
        var spec = Specs.First(x => x.id == "mess_hall");
        g.AddNode(spec, ShipSector.Crew, 0, 0);
        g.AddNode(spec, ShipSector.Crew, 0, 1);
        g.AddEdge(0, 1);
        int east = t.sockets.FindIndex(x => x.side == GridSide.East), west = t.sockets.FindIndex(x => x.side == GridSide.West);
        var a = new RoomPlacement { node = 0, template = t, origin = new Int2(0, 0) };
        var b = new RoomPlacement { node = 1, template = t, origin = a.OutsideCell(east) - t.SocketCell(west, 0) };
        var l = new ShipLayout { graph = g, settings = new PlacementSettings() };
        l.rooms.Add(a);
        l.rooms.Add(b);
        l.connections.Add(new PhysicalConnection { edge = 0, edgeId = g.edges[0].id, nodeA = 0, socketA = east, nodeB = 1, socketB = west, mode = ConnectionMode.Direct });
        l.ComputeBounds();
        Assert.IsFalse(Validate(l).Has(LayoutIssueCode.SocketMisaligned), "aligned to start with");
        l.rooms[1].origin += new Int2(0, 1);
        Assert.IsTrue(Validate(l).Has(LayoutIssueCode.SocketMisaligned));
    }

    [Test]
    public void OverlappingRoomsAndRoomsOnCorridorsAreCaught()
    {
        var (_, p) = Placed(2);
        var l = Copy(p.layout);
        l.rooms[1].origin = l.rooms[0].origin; // one room on top of another
        var rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.RoomOverlap), string.Join("\n", rep.issues.Take(5)));

        l = Copy(p.layout);
        var corridor = l.connections.First(k => k.cells.Count > 2);
        l.rooms[0].origin = corridor.cells[1]; // a room dropped onto a corridor
        rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.RoomCorridorOverlap) || rep.Has(LayoutIssueCode.RoomOverlap), string.Join("\n", rep.issues.Take(5)));
    }

    [Test]
    public void CorridorsSharingACellWithoutACleanCrossingAreCaught()
    {
        var (_, p) = Placed(2);
        var l = Copy(p.layout);
        var a = l.connections.First(k => k.cells.Count > 4);
        var b = l.connections.First(k => k != a && k.cells.Count > 4 && !k.cells.Intersect(a.cells).Any());
        b.cells[2] = a.cells[2]; // two corridors forced through one cell: a join, not a crossing
        var rep = Validate(l);
        Assert.IsFalse(rep.Valid);
        Assert.IsTrue(rep.Has(LayoutIssueCode.CorridorOverlap) || rep.Has(LayoutIssueCode.CorridorBroken), string.Join("\n", rep.issues.Take(5)));
    }

    [Test]
    public void ABrokenCorridorAndADisconnectedRoomAreCaught()
    {
        var (_, p) = Placed(2);
        var l = Copy(p.layout);
        var c = l.connections.First(k => k.cells.Count > 4);
        c.cells.RemoveAt(c.cells.Count / 2); // a gap in the corridor
        var rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.CorridorBroken) || rep.Has(LayoutIssueCode.OpenCorridorEnd), string.Join("\n", rep.issues.Take(5)));

        // Cut every connection of one room: it is no longer reachable.
        l = Copy(p.layout);
        int victim = l.graph.FirstOf(RoomCategory.Bridge);
        l.connections.RemoveAll(k => k.nodeA == victim || k.nodeB == victim);
        rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.Disconnected) || rep.Has(LayoutIssueCode.EdgeCorrespondence), string.Join("\n", rep.issues.Take(5)));
        Assert.IsFalse(rep.Valid);
    }

    [Test]
    public void AnUnsealedSocketIsAHole()
    {
        var (_, p) = Placed(2);
        var l = Copy(p.layout);
        Assert.IsNotEmpty(l.sealedSockets, "the sample has sealed sockets");
        l.sealedSockets.RemoveAt(0);
        var rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.UnsealedSocket), string.Join("\n", rep.issues.Take(5)));
        Assert.IsFalse(rep.Valid);

        l = Copy(p.layout);
        var used = l.connections[0];
        l.sealedSockets.Add(new SealedSocket(used.nodeA, used.socketA));
        Assert.IsTrue(Validate(l).Has(LayoutIssueCode.SealedSocketInUse));
    }

    [Test]
    public void WrongTemplatesIncompatibleSocketsAndMissingMetadataAreCaught()
    {
        var (g, p) = Placed(2);
        var l = Copy(p.layout);
        l.rooms[0].template = Library.For("bridge")[0]; // not this node's definition
        Assert.IsTrue(Validate(l).Has(LayoutIssueCode.NodeCorrespondence));

        l = Copy(p.layout);
        l.rooms.RemoveAt(l.rooms.Count - 1);
        Assert.IsTrue(Validate(l).Has(LayoutIssueCode.NodeCorrespondence));

        // An incompatible pair: make one socket wide.
        l = Copy(p.layout);
        var conn = l.connections.First(k => k.mode == ConnectionMode.Corridor);
        var wideTemplate = new RoomTemplate { id = "x.wide", definitionId = l.rooms[conn.nodeA].template.definitionId, category = l.rooms[conn.nodeA].template.category, sizeX = l.rooms[conn.nodeA].template.sizeX, sizeZ = l.rooms[conn.nodeA].template.sizeZ, boundsWidth = 1, boundsDepth = 1 };
        foreach (var s in l.rooms[conn.nodeA].template.sockets) wideTemplate.sockets.Add(s.Clone());
        wideTemplate.sockets[conn.socketA].width = SocketWidth.Wide;
        l.rooms[conn.nodeA].template = wideTemplate;
        var rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.SocketIncompatible), string.Join("\n", rep.issues.Take(5)));
        Assert.IsTrue(rep.Has(LayoutIssueCode.MissingAnchors), "a template without anchors is reported");
    }

    [Test]
    public void SpatialRuleViolationsAreCaught()
    {
        var (g, p) = Placed(2);
        // Swap the Bridge and Engineering rooms' positions' roles by marking the wrong sector order: move the Bridge to the stern end.
        var l = Copy(p.layout);
        int bridge = l.graph.FirstOf(RoomCategory.Bridge);
        l.rooms[bridge].origin = new Int2(l.Max.x + 3, l.rooms[bridge].origin.z); // far aft of everything
        var rep = Validate(l);
        Assert.IsTrue(rep.Has(LayoutIssueCode.BridgeNotForward), string.Join("\n", rep.issues.Take(6)));

        // A tight boundary.
        l = Copy(p.layout);
        l.settings = l.settings.Clone();
        l.settings.maxExtentX = 30;
        Assert.IsTrue(Validate(l).Has(LayoutIssueCode.OutOfBounds));

        // Pods too close: pull the second pod next to the first.
        l = Copy(p.layout);
        var pods = l.graph.AllOf(RoomCategory.EscapePodBay);
        l.rooms[pods[1]].origin = l.rooms[pods[0]].origin + new Int2(0, 12);
        Assert.IsTrue(Validate(l).Has(LayoutIssueCode.EscapeBaysClose));
    }

    [Test]
    public void SocketIdsAreUniqueInEveryTemplateAndMalformedTemplatesAreReported()
    {
        var t = new RoomTemplate { id = "bad.a", definitionId = "bad", sizeX = 3, sizeZ = 3 };
        t.sockets.Add(new SocketSpec { id = "n1", cell = new Int2(1, 2), side = GridSide.North });
        t.sockets.Add(new SocketSpec { id = "n1", cell = new Int2(0, 2), side = GridSide.North });
        t.sockets.Add(new SocketSpec { id = "w9", cell = new Int2(1, 1), side = GridSide.West });
        var problems = t.Problems();
        Assert.IsTrue(problems.Any(x => x.Contains("used twice")), string.Join("\n", problems));
        Assert.IsTrue(problems.Any(x => x.Contains("does not sit on its West edge")), string.Join("\n", problems));
    }

    [Test]
    public void AMissingVariantIsReportedBeforePlacement()
    {
        var lib = new TemplateLibrary();
        foreach (var t in Library.All.Where(t => t.definitionId != "workshop")) lib.Add(t);
        var problems = lib.Problems(Specs);
        Assert.IsTrue(problems.Any(x => x.Contains("no prefab variant for room definition 'workshop'")), string.Join("\n", problems));
    }

    [Test]
    public void AnchorRulesReportMissingSemanticAnchors()
    {
        var spec = Specs.First(s => s.id == "engineering");
        var t = Library.For("engineering")[0];
        Assert.IsEmpty(AnchorRules.Missing(spec, t));
        var stripped = new RoomTemplate { id = t.id, definitionId = t.definitionId, category = t.category, sizeX = t.sizeX, sizeZ = t.sizeZ };
        foreach (var s in t.sockets) stripped.sockets.Add(s.Clone());
        var missing = AnchorRules.Missing(spec, stripped);
        foreach (var kind in new[] { AnchorKind.PatrolPoint, AnchorKind.SearchPoint, AnchorKind.PlayerSpawn, AnchorKind.VentEntrance, AnchorKind.Item, AnchorKind.Objective, AnchorKind.CameraMount, AnchorKind.Hazard })
            Assert.IsTrue(missing.Any(m => m.Contains(kind.ToString())), $"{kind} is reported as missing");
        Assert.IsTrue(missing.Any(m => m.Contains("no room bounds")));
    }

    [Test]
    public void ThePlayerStartRoomCarriesFourClearSpawnPoints()
    {
        var bp = GreyboxBlueprints.Create().First(b => b.definitionId == "player_start");
        var spawns = bp.anchors.Where(a => a.kind == AnchorKind.PlayerSpawn).ToList();
        Assert.AreEqual(4, spawns.Count);
        foreach (var s in spawns) Assert.IsTrue(bp.IsFree(s.x, s.z, 0.6f), $"spawn at {s.x},{s.z} is not clear of furniture");
        for (int i = 0; i < spawns.Count; i++)
            for (int j = i + 1; j < spawns.Count; j++)
                Assert.GreaterOrEqual(Math.Sqrt(Math.Pow(spawns[i].x - spawns[j].x, 2) + Math.Pow(spawns[i].z - spawns[j].z, 2)), 1.5, "spawn points are apart");
    }
}
