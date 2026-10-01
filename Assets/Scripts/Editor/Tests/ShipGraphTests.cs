using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for the procedural ship graph (pure: no assets, scenes or Unity types). Every multi-seed test names the failing seed so it can
// be reproduced in Alien > Procedural Ship > Graph Generator.
public class ShipGraphTests
{
    const int SampleSeeds = 1000;

    static ShipGraphGenerator Gen() => new(DefaultRoomCatalogue.Create(), new ShipGraphSettings());

    static readonly Dictionary<int, ShipGraphResult> cache = new();

    // One shared large sample (generation is deterministic, so caching does not hide anything).
    static IEnumerable<ShipGraphResult> Sample()
    {
        var gen = Gen();
        for (int seed = 0; seed < SampleSeeds; seed++)
        {
            if (!cache.TryGetValue(seed, out var r)) cache[seed] = r = gen.Generate(seed);
            yield return r;
        }
    }

    static string Why(ShipGraphResult r, string what) => $"seed {r.seed}: {what}\n{(r.graph != null ? ShipGraphText.Describe(r) : string.Join("\n", r.attemptFailures))}";

    // ---------- Determinism ----------

    [Test]
    public void TheSameSeedProducesAnIdenticalGraph()
    {
        foreach (int seed in new[] { 0, 1, 7, 42, 12345, -9, int.MaxValue, int.MinValue })
        {
            var a = Gen().Generate(seed);
            var b = Gen().Generate(seed); // a fresh generator: nothing carries over between instances
            Assert.IsTrue(a.success, Why(a, "did not generate"));
            Assert.AreEqual(a.graph.Canonical(), b.graph.Canonical(), $"seed {seed} is not deterministic");
            Assert.AreEqual(a.graph.Fingerprint(), b.graph.Fingerprint());
            Assert.IsTrue(ShipGraphValidator.CheckDeterminism(Gen(), seed, out string detail), detail);
        }
    }

    [Test]
    public void TheCatalogueOrderDoesNotChangeTheResult()
    {
        var shuffled = DefaultRoomCatalogue.Create();
        shuffled.Reverse();
        for (int seed = 0; seed < 20; seed++)
            Assert.AreEqual(Gen().Generate(seed).graph.Canonical(), new ShipGraphGenerator(shuffled).Generate(seed).graph.Canonical(), $"seed {seed}");
    }

    [Test]
    public void TheRandomStreamIsFixedForAGivenSeed()
    {
        // The generator's own SplitMix64: the same numbers on every machine and runtime. A change here changes every ship ever generated.
        var rng = new ShipRng(ShipRng.Derive(1, 0));
        var first = new[] { rng.Range(0, 1000), rng.Range(0, 1000), rng.Range(0, 1000) };
        var again = new ShipRng(ShipRng.Derive(1, 0));
        CollectionAssert.AreEqual(first, new[] { again.Range(0, 1000), again.Range(0, 1000), again.Range(0, 1000) });
        Assert.AreNotEqual(ShipRng.Derive(1, 0), ShipRng.Derive(1, 1), "each attempt has its own stream");
        Assert.AreNotEqual(ShipRng.Derive(1, 0), ShipRng.Derive(2, 0));
        var counts = new int[10];
        var r = new ShipRng(99);
        for (int i = 0; i < 10000; i++) counts[r.Range(0, 9)]++;
        Assert.IsTrue(counts.All(c => c > 850 && c < 1150), "roughly uniform");
    }

    [Test]
    public void DifferentSeedsGiveMeaningfullyDifferentShips()
    {
        var gen = Gen();
        var results = Enumerable.Range(0, 60).Select(gen.Generate).ToList();
        Assert.AreEqual(60, results.Select(r => r.graph.Fingerprint()).Distinct().Count(), "every seed gives its own graph");
        Assert.GreaterOrEqual(results.Select(r => r.graph.NodeCount).Distinct().Count(), 4, "room counts vary");
        var optionalSets = results.Select(r => string.Join(",", r.graph.nodes.Where(n => n.definitionId != null).Select(n => n.definitionId).Distinct().OrderBy(x => x))).Distinct().Count();
        Assert.GreaterOrEqual(optionalSets, 40, "the choice of rooms varies");
        var bridgeHosts = results.Select(r => r.graph.nodes[r.graph.nodes[r.graph.FirstOf(RoomCategory.Bridge)].neighbours[0]].category).Distinct().Count();
        Assert.GreaterOrEqual(bridgeHosts, 2, "the structure around the Bridge varies");
    }

    // ---------- The large sample: every rule on every seed ----------

    [Test]
    public void ALargeSampleOfSeedsAllGenerateValidShipsInBoundedTime()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int maxAttempts = 0;
        foreach (var r in Sample())
        {
            Assert.IsTrue(r.success, Why(r, "failed to generate a valid ship"));
            Assert.IsTrue(r.report.Valid, Why(r, "reported success with errors"));
            maxAttempts = System.Math.Max(maxAttempts, r.attempts);
        }
        Assert.Less(sw.Elapsed.TotalSeconds, 60, "1000 seeds must not take unbounded time");
        Assert.Less(maxAttempts, new ShipGraphSettings().maxAttempts, "no seed needs the last attempt");
    }

    [Test]
    public void MandatoryRoomsHaveTheirExactCounts()
    {
        foreach (var r in Sample())
        {
            var g = r.graph;
            Assert.That(g.NodeCount, Is.InRange(24, 30), Why(r, "room count"));
            Assert.AreEqual(1, g.AllOf(RoomCategory.PlayerStart).Count, Why(r, "player start"));
            Assert.AreEqual(1, g.AllOf(RoomCategory.Bridge).Count, Why(r, "bridge"));
            Assert.AreEqual(1, g.AllOf(RoomCategory.Engineering).Count, Why(r, "engineering"));
            Assert.AreEqual(2, g.AllOf(RoomCategory.EscapePodBay).Count, Why(r, "escape pods"));
            foreach (var c in new[] { RoomCategory.Security, RoomCategory.Medbay, RoomCategory.MessHall, RoomCategory.PowerControl, RoomCategory.CargoBay, RoomCategory.Workshop })
                Assert.AreEqual(1, g.AllOf(c).Count, Why(r, c.ToString()));
            Assert.That(g.AllOf(RoomCategory.CrewQuarters).Count, Is.InRange(1, 2), Why(r, "crew quarters"));
        }
    }

    [Test]
    public void EveryShipIsConnectedWithAtLeastThreeLoops()
    {
        foreach (var r in Sample())
        {
            Assert.AreEqual(1, r.graph.Components().Count, Why(r, "not connected"));
            Assert.GreaterOrEqual(r.graph.LoopCount(), 3, Why(r, "too few loops"));
        }
    }

    [Test]
    public void BridgeForwardEngineeringAftAndFarApart()
    {
        foreach (var r in Sample())
        {
            var g = r.graph;
            int bridge = g.FirstOf(RoomCategory.Bridge), eng = g.FirstOf(RoomCategory.Engineering), spawn = g.FirstOf(RoomCategory.PlayerStart);
            Assert.AreEqual(ShipSector.Forward, g.nodes[bridge].sector, Why(r, "bridge sector"));
            Assert.AreEqual(ShipSector.Industrial, g.nodes[eng].sector, Why(r, "engineering sector"));
            Assert.AreEqual(1, g.nodes[bridge].Degree, Why(r, "the bridge is a terminal room"));
            Assert.LessOrEqual(g.nodes[eng].Degree, 2, Why(r, "engineering is terminal or near-terminal"));
            Assert.GreaterOrEqual(g.Distance(bridge, eng), 7, Why(r, "bridge and engineering too close"));
            var d = g.Distances(spawn);
            Assert.LessOrEqual(d.Count(x => x > d[bridge]), 3, Why(r, "the bridge is not among the furthest rooms"));
        }
    }

    [Test]
    public void EscapePodsAreSeparatedAndNeverNextToTheSpawn()
    {
        foreach (var r in Sample())
        {
            var g = r.graph;
            var pods = g.AllOf(RoomCategory.EscapePodBay);
            int spawn = g.FirstOf(RoomCategory.PlayerStart);
            foreach (int p in pods)
            {
                Assert.AreEqual(1, g.nodes[p].Degree, Why(r, "pod is not terminal"));
                Assert.IsFalse(g.Connected(p, spawn), Why(r, "pod next to the spawn"));
                Assert.GreaterOrEqual(g.Distance(spawn, p), 3, Why(r, "pod too close to the spawn"));
            }
            Assert.GreaterOrEqual(g.Distance(pods[0], pods[1]), 5, Why(r, "pods too close together"));
            Assert.AreNotEqual(g.nodes[pods[0]].neighbours[0], g.nodes[pods[1]].neighbours[0], Why(r, "pods share a branch"));
        }
    }

    [Test]
    public void DegreesStayWithinTheirBoundsAndForbiddenNeighboursNeverMeet()
    {
        var specs = DefaultRoomCatalogue.Create().ToDictionary(s => s.id);
        foreach (var r in Sample())
            foreach (var n in r.graph.nodes)
            {
                var spec = specs[n.definitionId];
                Assert.That(n.Degree, Is.InRange(spec.minConnections, System.Math.Min(spec.maxConnections, 4)), Why(r, $"{n.id} degree {n.Degree}"));
                Assert.IsTrue(spec.Allows(RoomSpec.RoleForDegree(n.Degree)), Why(r, $"{n.id} role"));
                foreach (int m in n.neighbours)
                {
                    var other = r.graph.nodes[m];
                    Assert.IsFalse(spec.forbiddenNeighbours.Contains(other.category), Why(r, $"{n.id} is next to a forbidden {other.category}"));
                }
            }
    }

    [Test]
    public void IdentifiersAreStableAndUnique()
    {
        foreach (var r in Sample().Take(200))
        {
            var g = r.graph;
            Assert.AreEqual(g.NodeCount, g.nodes.Select(n => n.id).Distinct().Count(), Why(r, "duplicate node id"));
            Assert.AreEqual(g.EdgeCount, g.edges.Select(e => e.id).Distinct().Count(), Why(r, "duplicate edge id"));
            foreach (var n in g.nodes) StringAssert.IsMatch(@"^[a-z0-9_]+\.\d+$", n.id);
            // Occurrence numbers run 0..k-1 for each definition.
            foreach (var grp in g.nodes.GroupBy(n => n.definitionId))
                CollectionAssert.AreEquivalent(Enumerable.Range(0, grp.Count()).Select(i => $"{grp.Key}.{i}"), grp.Select(n => n.id), Why(r, grp.Key));
        }
    }

    [Test]
    public void TheShipHasARouteSideBranchesAndEnoughRoomsForPatrols()
    {
        foreach (var r in Sample())
        {
            Assert.GreaterOrEqual(r.report.routeLength, 10, Why(r, "route"));
            Assert.GreaterOrEqual(r.report.sideBranches, 4, Why(r, "side branches"));
            Assert.GreaterOrEqual(r.report.nonTerminal, 12, Why(r, "non-terminal rooms"));
            Assert.IsFalse(r.report.Has(GraphIssueCode.SectorCutVertex), Why(r, "a single room cuts a sector off"));
        }
    }

    // ---------- The validator catches broken graphs ----------

    static (ShipGraph graph, ShipGraphValidator validator) Valid()
    {
        var gen = Gen();
        var r = gen.Generate(3);
        Assert.IsTrue(r.success);
        return (r.graph.Clone(), gen.Validator);
    }

    [Test]
    public void AnEscapePodBesideTheSpawnIsCaught()
    {
        var (g, v) = Valid();
        int spawn = g.FirstOf(RoomCategory.PlayerStart), pod = g.FirstOf(RoomCategory.EscapePodBay);
        g.AddEdge(spawn, pod);
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.SpawnNextToEscapePod));
        Assert.IsTrue(rep.Has(GraphIssueCode.ForbiddenNeighbour));
        Assert.IsTrue(rep.Has(GraphIssueCode.RoleNotPermitted), "the pod is no longer terminal");
        Assert.IsFalse(rep.Valid);
    }

    [Test]
    public void ADisconnectedRoomIsCaught()
    {
        var (g, v) = Valid();
        int bridge = g.FirstOf(RoomCategory.Bridge);
        g.RemoveEdge(bridge, g.nodes[bridge].neighbours[0]);
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.Disconnected));
        CollectionAssert.Contains(rep.issues.First(i => i.code == GraphIssueCode.Disconnected).nodes, bridge, "the issue names the cut-off room");
    }

    [Test]
    public void DuplicateIdsSelfAndBrokenConnectionsAreCaught()
    {
        var (g, v) = Valid();
        g.nodes[1].id = g.nodes[0].id;
        Assert.IsTrue(v.Validate(g).Has(GraphIssueCode.DuplicateNodeId));

        (g, v) = Valid();
        g.edges.Add(new ShipGraphEdge { a = 2, b = 2, id = "x--x" });
        Assert.IsTrue(v.Validate(g).Has(GraphIssueCode.SelfEdge));

        (g, v) = Valid();
        g.edges.Add(new ShipGraphEdge { a = 0, b = 999, id = "x--y" });
        Assert.IsTrue(v.Validate(g).Has(GraphIssueCode.BadEdge));

        (g, v) = Valid();
        var e = g.edges[0];
        g.edges.Add(new ShipGraphEdge { a = e.a, b = e.b, id = e.id });
        Assert.IsTrue(v.Validate(g).Has(GraphIssueCode.DuplicateEdge));

        (g, v) = Valid();
        g.nodes[4].definitionId = "no_such_room";
        g.nodes[4].id = "no_such_room.0";
        Assert.IsTrue(v.Validate(g).Has(GraphIssueCode.UnknownDefinition));
    }

    [Test]
    public void SectorAndDeckViolationsAreCaught()
    {
        var (g, v) = Valid();
        g.nodes[g.FirstOf(RoomCategory.Bridge)].sector = ShipSector.Industrial;
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.BridgeNotForward));
        Assert.IsTrue(rep.Has(GraphIssueCode.SectorNotPermitted));

        (g, v) = Valid();
        g.nodes[0].deck = 1;
        Assert.IsTrue(v.Validate(g).Has(GraphIssueCode.DeckNotPermitted), "version 1 has a single deck");
    }

    [Test]
    public void ARemovedLoopNetworkIsCaught()
    {
        // Keep only the route itself and the side rooms: every loop goes, and interior route rooms become cut points.
        var (g, v) = Valid();
        var route = new HashSet<int>(g.route);
        foreach (var e in g.edges.ToList())
        {
            bool consecutive = route.Contains(e.a) && route.Contains(e.b) && System.Math.Abs(g.route.IndexOf(e.a) - g.route.IndexOf(e.b)) == 1;
            bool leaf = g.nodes[e.a].Degree == 1 || g.nodes[e.b].Degree == 1;
            bool connector = (!route.Contains(e.a) && g.nodes[e.a].neighbours.Count(route.Contains) == 2) || (!route.Contains(e.b) && g.nodes[e.b].neighbours.Count(route.Contains) == 2);
            if (!consecutive && !leaf && (route.Contains(e.a) && route.Contains(e.b) || connector)) g.RemoveEdge(e.a, e.b);
        }
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.TooFewLoops), string.Join("\n", rep.issues));
        Assert.IsTrue(rep.Has(GraphIssueCode.SectorCutVertex), string.Join("\n", rep.issues));
    }

    [Test]
    public void TooManyConnectionsAndBadCountsAreCaught()
    {
        var (g, v) = Valid();
        int hub = g.route[g.route.Count / 2];
        foreach (var n in g.nodes.Where(n => n.index != hub && !g.Connected(hub, n.index)).Take(5).ToList()) g.AddEdge(hub, n.index);
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.MaxDegreeExceeded));
        Assert.IsTrue(rep.Has(GraphIssueCode.ConnectionRange));

        // A tiny graph: the wrong number of rooms and missing mandatory rooms.
        var tiny = new ShipGraph();
        var specs = DefaultRoomCatalogue.Create();
        var start = specs.First(s => s.category == RoomCategory.PlayerStart);
        var bridge = specs.First(s => s.category == RoomCategory.Bridge);
        tiny.AddNode(start, ShipSector.Crew, 0, 0);
        tiny.AddNode(bridge, ShipSector.Forward, 0, 0);
        tiny.AddEdge(0, 1);
        var small = v.Validate(tiny);
        Assert.IsTrue(small.Has(GraphIssueCode.RoomCount));
        Assert.IsTrue(small.Has(GraphIssueCode.OccurrenceRange), "engineering, escape pods... are missing");
        Assert.IsTrue(small.Has(GraphIssueCode.SectorEmpty));
    }

    [Test]
    public void ProximityRulesAreCheckedStrictAndSoft()
    {
        // The Mess Hall must be within 2 of a Crew Quarters (strict): move every crew quarters to the bow, far from the mess. A ship whose crew
        // quarters are all dead ends is used, so moving them keeps the ship connected.
        var gen = Gen();
        var g = Enumerable.Range(0, 200).Select(gen.Generate)
            .First(x => x.graph.AllOf(RoomCategory.CrewQuarters).All(c => x.graph.nodes[c].Degree == 1)).graph.Clone();
        var v = gen.Validator;
        foreach (int crew in g.AllOf(RoomCategory.CrewQuarters))
        {
            foreach (int nb in g.nodes[crew].neighbours.ToList()) g.RemoveEdge(crew, nb);
            g.AddEdge(crew, g.route[0]);
        }
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.ProximityRequired), string.Join(System.Environment.NewLine, rep.issues));
        Assert.AreEqual(IssueSeverity.Error, rep.issues.First(i => i.code == GraphIssueCode.ProximityRequired).severity);
    }

    // ---------- Failure is clear, never silent ----------

    [Test]
    public void ImpossibleSettingsFailWithDiagnosticsInsteadOfReturningAnInvalidShip()
    {
        var settings = new ShipGraphSettings { minRooms = 60, maxRooms = 70, maxAttempts = 5 };
        var r = new ShipGraphGenerator(DefaultRoomCatalogue.Create(), settings).Generate(1);
        Assert.IsFalse(r.success);
        Assert.AreEqual(5, r.attemptFailures.Count, "one reason per attempt");
        Assert.IsTrue(r.report == null || !r.report.Valid);
    }

    [Test]
    public void ABrokenCatalogueFailsBeforeGenerating()
    {
        var specs = DefaultRoomCatalogue.Create();
        specs.Add(specs[0].Clone()); // a duplicate id
        var r = new ShipGraphGenerator(specs).Generate(1);
        Assert.IsFalse(r.success);
        Assert.IsNull(r.graph);
        StringAssert.Contains("is used 2 times", r.attemptFailures[0]);

        var noBridge = DefaultRoomCatalogue.Create().Where(s => s.category != RoomCategory.Bridge).ToList();
        var r2 = new ShipGraphGenerator(noBridge).Generate(1);
        Assert.IsFalse(r2.success);
        StringAssert.Contains("Bridge", r2.attemptFailures[0]);
    }

    [Test]
    public void TheDefaultCatalogueIsComplete()
    {
        var specs = DefaultRoomCatalogue.Create();
        Assert.AreEqual(specs.Count, specs.Select(s => s.id).Distinct().Count(), "ids are unique");
        Assert.AreEqual(System.Enum.GetValues(typeof(RoomCategory)).Length, specs.Select(s => s.category).Distinct().Count(), "every category has a definition");
        Assert.AreEqual(12, specs.Count(s => s.tier == RoomTier.Mandatory), "the 11 required rooms and the escape access");
        Assert.AreEqual(14, specs.Count(s => s.tier == RoomTier.Specialised));
        Assert.AreEqual(10, specs.Count(s => s.tier == RoomTier.Structural));
        foreach (var s in specs)
        {
            Assert.That(s.id, Does.Match(@"^[a-z0-9_]+$"));
            Assert.LessOrEqual(s.minCount, s.maxCount, s.id);
            Assert.AreEqual((s.roles & GraphRoles.Terminal) != 0, s.mayBeDeadEnd, $"{s.id}: dead-end flag and terminal role agree");
        }
    }
}
