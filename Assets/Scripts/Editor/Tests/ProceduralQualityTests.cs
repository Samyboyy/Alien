using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for the quality and balance pass (pure): semantic relationships, escape access branches, early reservation of critical
// topology, single-deck filtering, route pressure and the batch audit. Multi-seed tests name the failing seed.
public class ProceduralQualityTests
{
    const int Seeds = 500;
    static readonly List<RoomSpec> Specs = DefaultRoomCatalogue.Create();
    static readonly Dictionary<string, RoomSpec> SpecById = Specs.ToDictionary(s => s.id);
    static readonly Dictionary<int, ShipGraphResult> graphs = new();

    static ShipGraphResult Graph(int seed)
    {
        if (!graphs.TryGetValue(seed, out var r)) graphs[seed] = r = new ShipGraphGenerator(Specs).Generate(seed);
        return r;
    }

    // ---------- Semantic relationships ----------

    [Test]
    public void TheMessHallIsAlwaysWithinTwoOfACrewQuarters()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            var r = Graph(seed);
            Assert.IsTrue(r.success, $"seed {seed}: {r.Summary}");
            var g = r.graph;
            int mess = g.FirstOf(RoomCategory.MessHall);
            var crews = g.AllOf(RoomCategory.CrewQuarters);
            if (mess < 0 || crews.Count == 0) continue;
            var d = g.Distances(mess);
            Assert.LessOrEqual(crews.Min(c => d[c]), 2, $"seed {seed}: the Mess Hall is {crews.Min(c => d[c])} connections from the nearest Crew Quarters");
        }
    }

    [Test]
    public void TheCrewMessRuleIsAHardValidationError()
    {
        var spec = SpecById["mess_hall"];
        Assert.IsTrue(spec.closeToStrict);
        CollectionAssert.Contains(spec.closeTo, RoomCategory.CrewQuarters);
        Assert.AreEqual(2, spec.closeToDistance);
    }

    [Test]
    public void PreferredRelationshipsAreMetMoreOftenThanWithoutPreferences()
    {
        // The same catalogue with every preference removed is the control: the preferences must measurably pull related rooms together.
        var control = DefaultRoomCatalogue.Create();
        foreach (var s in control) { s.placement = PlacementPreference.None; }
        var plain = new ShipGraphGenerator(control.Select(s => { var c = s.Clone(); c.preferredNeighbours = new RoomCategory[0]; return c; }).ToList());
        int metOn = 0, totOn = 0, metOff = 0, totOff = 0;
        for (int seed = 0; seed < 200; seed++)
        {
            var on = Graph(seed);
            var (m1, t1) = ShipAudit.PreferredRelationships(on.graph, SpecById);
            metOn += m1; totOn += t1;
            var off = plain.Generate(seed);
            Assert.IsTrue(off.success, $"control seed {seed}");
            var (m2, t2) = ShipAudit.PreferredRelationships(off.graph, SpecById); // scored against the real preferences
            metOff += m2; totOff += t2;
        }
        double rateOn = (double)metOn / totOn, rateOff = (double)metOff / totOff;
        Assert.Greater(rateOn, rateOff + 0.05, $"with preferences {rateOn:P0}, without {rateOff:P0}");
    }

    [Test]
    public void ManyDifferentTopologiesRemainPossible()
    {
        var bridgeHosts = new HashSet<RoomCategory>();
        var escapeHosts = new HashSet<RoomCategory>();
        var escapeSectors = new HashSet<string>();
        var medbayHosts = new HashSet<RoomCategory>();
        for (int seed = 0; seed < 150; seed++)
        {
            var g = Graph(seed).graph;
            int bridge = g.FirstOf(RoomCategory.Bridge);
            bridgeHosts.Add(g.nodes[g.nodes[bridge].neighbours[0]].category);
            foreach (int access in g.AllOf(RoomCategory.EscapeAccess))
                foreach (int v in g.nodes[access].neighbours.Where(v => g.nodes[v].category != RoomCategory.EscapePodBay)) escapeHosts.Add(g.nodes[v].category);
            escapeSectors.Add(string.Join("+", g.AllOf(RoomCategory.EscapePodBay).Select(p => g.nodes[p].sector).OrderBy(x => x)));
            int medbay = g.FirstOf(RoomCategory.Medbay);
            foreach (int v in g.nodes[medbay].neighbours) medbayHosts.Add(g.nodes[v].category);
        }
        Assert.GreaterOrEqual(bridgeHosts.Count, 3, "the Bridge hangs off several kinds of room: " + string.Join(", ", bridgeHosts));
        Assert.GreaterOrEqual(escapeHosts.Count, 4, "escape branches leave from several kinds of room: " + string.Join(", ", escapeHosts));
        Assert.GreaterOrEqual(escapeSectors.Count, 2, "escape branches sit in different sector combinations");
        Assert.GreaterOrEqual(medbayHosts.Count, 3);
        Assert.AreEqual(150, Enumerable.Range(0, 150).Select(s => Graph(s).graph.Fingerprint()).Distinct().Count());
    }

    [Test]
    public void TheSameSeedStillGivesTheSameShip()
    {
        foreach (int seed in new[] { 0, 69, 420, 12345, -3 })
        {
            Assert.IsTrue(ShipGraphValidator.CheckDeterminism(new ShipGraphGenerator(Specs), seed, out string detail), $"seed {seed}: {detail}");
            Assert.AreEqual(new ShipGraphGenerator(Specs).Generate(seed).graph.Canonical(), new ShipGraphGenerator(DefaultRoomCatalogue.Create()).Generate(seed).graph.Canonical());
        }
    }

    // ---------- Escape access branches ----------

    [Test]
    public void EveryPodBayIsATerminalBehindItsOwnEscapeAccess()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            var g = Graph(seed).graph;
            var pods = g.AllOf(RoomCategory.EscapePodBay);
            Assert.AreEqual(2, pods.Count, $"seed {seed}");
            var accesses = new HashSet<int>();
            foreach (int pod in pods)
            {
                Assert.AreEqual(1, g.nodes[pod].Degree, $"seed {seed}: {g.nodes[pod].id} is not terminal");
                int access = g.nodes[pod].neighbours[0];
                Assert.AreEqual(RoomCategory.EscapeAccess, g.nodes[access].category, $"seed {seed}: {g.nodes[pod].id} opens onto a {g.nodes[access].category}");
                Assert.AreEqual(2, g.nodes[access].Degree, $"seed {seed}: the escape access is a straight transition");
                Assert.IsTrue(accesses.Add(access), $"seed {seed}: two pods share an access room");
                Assert.AreEqual(g.nodes[pod].sector, g.nodes[access].sector, $"seed {seed}");
            }
        }
    }

    [Test]
    public void TheValidatorRejectsAPodWithoutItsAccessAndAnAccessWithoutItsPod()
    {
        var g = Graph(3).graph.Clone();
        var v = new ShipGraphGenerator(Specs).Validator;
        int pod = g.AllOf(RoomCategory.EscapePodBay)[0];
        int access = g.nodes[pod].neighbours[0];
        int host = g.nodes[access].neighbours.First(x => x != pod);
        g.RemoveEdge(pod, access);
        g.AddEdge(pod, host);
        var rep = v.Validate(g);
        Assert.IsTrue(rep.Has(GraphIssueCode.NeighbourNotAllowed), string.Join(Environment.NewLine, rep.issues));
        Assert.IsTrue(rep.Has(GraphIssueCode.RequiredNeighbourMissing));
    }

    [Test]
    public void TheEscapeAccessPrefabHasItsAnchorsAndAStraightPassage()
    {
        var bp = GreyboxBlueprints.Create().Single(b => b.definitionId == "escape_access");
        Assert.AreEqual(2, bp.sockets.Count);
        Assert.AreEqual(Grid.Opposite(bp.sockets[0].side), bp.sockets[1].side, "the ship side and the pod side face each other");
        Assert.IsTrue(bp.anchors.Any(a => a.kind == AnchorKind.Objective && a.semantic == AnchorSemantic.ObjectiveConsole), "a console location");
        Assert.IsTrue(bp.anchors.Any(a => a.kind == AnchorKind.Item && a.semantic == AnchorSemantic.EmergencyWallMount), "item anchors");
        Assert.AreEqual(2, bp.anchors.Count(a => a.kind == AnchorKind.DoorSocket), "a door anchor at each end");
        foreach (var k in new[] { AnchorKind.PatrolPoint, AnchorKind.SearchPoint, AnchorKind.VentEntrance, AnchorKind.PlayerSpawn })
            Assert.IsTrue(bp.anchors.Any(a => a.kind == k), $"{k}");
        var lib = TemplateLibrary.FromBlueprints();
        Assert.IsEmpty(lib.Problems(Specs));
    }

    // ---------- Critical topology reserved early ----------

    [Test]
    public void LatePodAndBridgeFailuresAreRareAndGenerationIsCheaper()
    {
        var audit = ShipAudit.Run(new ShipAudit.Options { firstSeed = 0, count = Seeds });
        Assert.AreEqual(Seeds, audit.rows.Count(r => r.graphOk));
        int podFailures = audit.rejections.Where(k => k.Key.Contains("escape_pod_bay")).Sum(k => k.Value);
        int bridgeFailures = audit.rejections.Where(k => k.Key.Contains("SpawnDistance") || k.Key.Contains("BridgeEngineeringTooClose")).Sum(k => k.Value);
        // Baseline before this pass (500 seeds): 775 pod-placement rejections, 925 Bridge/Engineering distance rejections, 5.9 attempts on average.
        Assert.Less(podFailures, 200, "pod placement rejections");
        Assert.Less(bridgeFailures, 50, "Bridge and Engineering distance rejections");
        Assert.Less(audit.rows.Average(r => r.attempts), 3.5, "mean attempts");
        foreach (var r in audit.rows)
        {
            Assert.GreaterOrEqual(r.bridgeSpawn, SpecById["bridge"].minSpawnDistance, $"seed {r.seed}");
            Assert.GreaterOrEqual(r.engBridge, new ShipGraphSettings().minBridgeEngineeringDistance, $"seed {r.seed}");
            Assert.GreaterOrEqual(r.loops, 3, $"seed {r.seed}");
        }
    }

    // ---------- Single deck ----------

    [Test]
    public void ASingleDeckShipNeverHasAVerticalConnectionLobby()
    {
        Assert.IsTrue(SpecById["vertical_lobby"].requiresVerticalConnection);
        for (int seed = 0; seed < Seeds; seed++)
            Assert.IsFalse(Graph(seed).graph.nodes.Any(n => n.category == RoomCategory.VerticalLobby), $"seed {seed}");

        // A hand-made single-deck ship with one is rejected.
        var g = Graph(1).graph.Clone();
        var lobby = g.AddNode(SpecById["vertical_lobby"], ShipSector.Central, 0, 0);
        g.AddEdge(lobby.index, g.route[g.route.Count / 2]);
        g.AddEdge(lobby.index, g.route[g.route.Count / 2 + 1]);
        Assert.IsTrue(new ShipGraphGenerator(Specs).Validator.Validate(g).Has(GraphIssueCode.RequiresMultiDeck));
    }

    [Test]
    public void AMultiDeckProfileMayIncludeTheLobby()
    {
        var settings = new ShipGraphSettings { deckCount = 2 };
        var gen = new ShipGraphGenerator(Specs, settings);
        int with = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var r = gen.Generate(seed);
            Assert.IsTrue(r.success, $"seed {seed}: {r.Summary}");
            if (r.graph.nodes.Any(n => n.category == RoomCategory.VerticalLobby)) with++;
            Assert.IsFalse(r.report.Has(GraphIssueCode.RequiresMultiDeck));
        }
        Assert.Greater(with, 10, "the lobby is selectable once there are two decks");
    }

    // ---------- Route pressure ----------

    static (ShipGraph g, Scenario sc) Chain(LaunchMode mode)
    {
        // start(0) - a(1) - b(2) - access(3) - pod(4); power(5) and engineering(6) off a; security(7) off b.
        var g = new ShipGraph();
        var cats = new[] { RoomCategory.PlayerStart, RoomCategory.MaintenanceRoom, RoomCategory.MaintenanceRoom, RoomCategory.EscapeAccess, RoomCategory.EscapePodBay, RoomCategory.PowerControl, RoomCategory.Engineering, RoomCategory.Security };
        for (int i = 0; i < cats.Length; i++) g.AddNode(new RoomSpec { id = "r" + i, displayName = "r" + i, category = cats[i] }, ShipSector.Central, 0, 0);
        g.AddEdge(0, 1); g.AddEdge(1, 2); g.AddEdge(2, 3); g.AddEdge(3, 4); g.AddEdge(1, 5); g.AddEdge(5, 6); g.AddEdge(2, 7);
        var sc = new Scenario { spawnNode = 0 };
        sc.pods.Add(new PodPlan { node = 4, status = PodStatus.Operational, mode = mode });
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.PodLaunch, node = 4, podNode = 4 });
        if (mode == LaunchMode.Loud)
        {
            sc.consoles.Add(new ConsolePlan { role = ConsoleRole.FuseSocket, node = 5 });
            sc.consoles.Add(new ConsolePlan { role = ConsoleRole.Generator, node = 6 });
            var fuse = new ItemPlan { id = "fuse", kind = ItemKind.Fuse, critical = true };
            fuse.candidates.Add(new SpotCandidate { node = 6, anchorId = "f", rule = "t" });
            sc.items.Add(fuse);
        }
        return (g, sc);
    }

    static EscapePlan Plan(ShipGraph g, Scenario sc, PressureSettings ps = null)
    {
        var res = ScenarioSolver.Solve(g, sc);
        Assert.IsTrue(res.solved, string.Join("; ", res.failures));
        var plan = res.plans.Single();
        RoutePressure.Assess(g, sc, plan, ps ?? new PressureSettings());
        return plan;
    }

    [Test]
    public void ADirectQuietEscapeIsBelowTheBudgetAndAKeycardDoorLiftsIt()
    {
        var (g, sc) = Chain(LaunchMode.ManualQuiet);
        var bare = Plan(g, sc);
        Assert.Less(bare.pressure, new PressureSettings().minimum, "walk four rooms and hold a lever: a trivially direct escape\n" + string.Join(", ", bare.breakdown));

        int edge = g.edges.FindIndex(e => (e.a == 3 && e.b == 4) || (e.a == 4 && e.b == 3));
        var gate = new DoorGate { edge = edge, doorNode = 4 };
        gate.options.Add(new GateOption(OptionKind.Keycard));
        sc.gates.Add(gate);
        var key = new ItemPlan { id = "keycard", kind = ItemKind.Keycard, critical = true };
        key.candidates.Add(new SpotCandidate { node = 7, anchorId = "k", rule = "t" });
        sc.items.Add(key);
        var gated = Plan(g, sc);
        Assert.GreaterOrEqual(gated.pressure, new PressureSettings().minimum, string.Join(", ", gated.breakdown));
        Assert.IsTrue(gated.requirements.Any(r => r.Contains("keycard")));
        Assert.AreEqual(0, gated.loudActions, "the quiet route stays quiet");
    }

    [Test]
    public void ALoudPoweredRouteAndAQuietPreparedRouteContrast()
    {
        var (g1, loudSc) = Chain(LaunchMode.Loud);
        var loud = Plan(g1, loudSc);
        Assert.GreaterOrEqual(loud.loudActions, 2, "the generator and the countdown");
        Assert.IsTrue(loud.requirements.Any(r => r.Contains("restore power")));
        Assert.GreaterOrEqual(loud.pressure, new PressureSettings().minimum, string.Join(", ", loud.breakdown));
        Assert.AreEqual(loud.pressure, loud.breakdown.Sum(b => b.value), 1e-4, "the breakdown adds up");
        var again = Plan(g1, loudSc);
        Assert.AreEqual(loud.pressure, again.pressure, "deterministic");

        var tight = new PressureSettings { minimum = 1000f };
        Assert.Less(Plan(g1, loudSc, tight).pressure, tight.minimum, "the budget is configurable");
    }

    [Test]
    public void GeneratedScenariosMeetTheBudgetWithContrastAndRemainSolvable()
    {
        var lib = TemplateLibrary.FromBlueprints();
        var placer = new ShipPlacer(lib, Specs);
        int twoPods = 0, contrasting = 0, deeper = 0, total = 0, withNoisemakers = 0;
        for (int seed = 0; seed < 60; seed++)
        {
            var gr = Graph(seed);
            var p = placer.Place(gr.graph);
            Assert.IsTrue(p.success, $"seed {seed}: {p.Summary}");
            var input = new DirectorInput { graph = gr.graph, layoutSeed = seed, templateOf = i => p.layout.rooms[i].template, specOf = id => SpecById[id] };
            var r = EscapeDirector.Generate(input, 1);
            Assert.IsTrue(r.success, $"seed {seed}: {r.Summary}");
            total++;
            var min = new PressureSettings().minimum;
            foreach (var plan in r.solve.plans) Assert.GreaterOrEqual(plan.pressure, min, $"seed {seed}: route to {gr.graph.nodes[plan.podNode].id}\n{ScenarioReport.Describe(gr.graph, r)}");
            if (r.solve.plans.Count == 2)
            {
                twoPods++;
                if (r.solve.plans[0].loudActions != r.solve.plans[1].loudActions || r.solve.plans[0].needsPower != r.solve.plans[1].needsPower) contrasting++;
            }
            if (r.solve.stages >= 3) deeper++;
            if (r.scenario.items.Any(i => i.cls == ItemClass.Noisemaker)) withNoisemakers++;
            // Every pod route sits behind something: the solver still proves the escape.
            Assert.IsTrue(OnePlayerSimulator.CanEscape(gr.graph, r.scenario.ForRound(seed)).escaped, $"seed {seed}");
        }
        Assert.Greater(twoPods, total / 2);
        Assert.AreEqual(twoPods, contrasting, "when both pods work, one is loud or powered and the other is not");
        Assert.Greater(deeper, total / 2, "most scenarios need three or more progression stages");
        Assert.Greater(withNoisemakers, total * 9 / 10, "optional equipment is placed in nearly every ship");
    }

    [Test]
    public void TheReportShowsEachRouteWithItsPressureBreakdown()
    {
        var lib = TemplateLibrary.FromBlueprints();
        var gr = Graph(5);
        var p = new ShipPlacer(lib, Specs).Place(gr.graph);
        var r = EscapeDirector.Generate(new DirectorInput { graph = gr.graph, layoutSeed = 5, templateOf = i => p.layout.rooms[i].template, specOf = id => SpecById[id] }, 1);
        var text = ScenarioReport.Describe(gr.graph, r);
        StringAssert.Contains("Route to", text);
        StringAssert.Contains("pressure:", text);
        StringAssert.Contains("requires:", text);
        StringAssert.Contains("loud action", text);
    }

    // ---------- The audit ----------

    [Test]
    public void TheAuditIsDeterministicCompactAndReportsEverySeed()
    {
        var a = ShipAudit.Run(new ShipAudit.Options { firstSeed = 10, count = 30, place = true, scenario = true });
        var b = ShipAudit.Run(new ShipAudit.Options { firstSeed = 10, count = 30, place = true, scenario = true });
        Assert.AreEqual(a.rows.Select(r => r.fingerprint + r.pressure), b.rows.Select(r => r.fingerprint + r.pressure));
        Assert.Less(a.Summary().Split('\n').Length, 25, "the Console summary stays short");
        var report = a.Report();
        foreach (var r in a.rows) StringAssert.Contains($"\n{r.seed}\t", report);
        Assert.IsTrue(a.rows.All(r => r.scenarioOk), string.Join("; ", a.rows.Where(r => !r.scenarioOk).Select(r => $"{r.seed}: {r.failure}")));
        StringAssert.Contains("#room", ShipAudit.Category("attempt 3: no place for escape_pod_bay.1 near crew_quarters.0"));
    }
}
