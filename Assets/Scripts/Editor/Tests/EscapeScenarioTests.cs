using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

// EditMode tests for semantic anchors, item placement and the Escape Director (pure: no scene). Multi-seed tests name the failing layout and
// scenario seeds so they can be reproduced in Alien > Procedural Ship > Physical Ship (enter both seeds).
public class EscapeScenarioTests
{
    const int Layouts = 120, ScenarioSeedsPerLayout = 2, RoundSeeds = 12;

    static readonly List<RoomSpec> Specs = DefaultRoomCatalogue.Create();
    static readonly Dictionary<string, RoomSpec> SpecById = Specs.ToDictionary(s => s.id);
    static readonly TemplateLibrary Library = TemplateLibrary.FromBlueprints();
    static readonly Dictionary<int, (ShipGraph graph, ShipLayout layout)> layouts = new();
    static readonly Dictionary<(int, int), ScenarioResult> scenarios = new();

    static (ShipGraph graph, ShipLayout layout) Layout(int seed)
    {
        if (layouts.TryGetValue(seed, out var l)) return l;
        var g = new ShipGraphGenerator(Specs).Generate(seed);
        Assert.IsTrue(g.success, $"layout seed {seed}: logical graph failed");
        var p = new ShipPlacer(Library, Specs).Place(g.graph);
        Assert.IsTrue(p.success, $"layout seed {seed}: placement failed: {p.Summary}");
        return layouts[seed] = (g.graph, p.layout);
    }

    static DirectorInput Input(int layoutSeed)
    {
        var (g, l) = Layout(layoutSeed);
        return new DirectorInput { graph = g, layoutSeed = layoutSeed, templateOf = i => l.rooms[i].template, specOf = id => SpecById[id] };
    }

    static ScenarioResult Scenario(int layoutSeed, int scenarioSeed)
    {
        if (scenarios.TryGetValue((layoutSeed, scenarioSeed), out var r)) return r;
        return scenarios[(layoutSeed, scenarioSeed)] = EscapeDirector.Generate(Input(layoutSeed), scenarioSeed);
    }

    static string Seeds(int layout, int scenario) => $"layout seed {layout}, scenario seed {scenario}";

    // ---------- Semantic anchors ----------

    [Test]
    public void EveryInitialSemanticCategoryExistsAndItemAnchorsAreWellFormed()
    {
        var seen = new HashSet<AnchorSemantic>();
        foreach (var bp in GreyboxBlueprints.Create())
            foreach (var a in bp.anchors)
            {
                if (a.semantic != AnchorSemantic.None) seen.Add(a.semantic);
                if (a.kind != AnchorKind.Item || a.semantic == AnchorSemantic.None) continue;
                Assert.AreNotEqual(ItemClassMask.None, a.allowed, $"{bp.variantId}/{a.id} allows nothing");
                Assert.AreEqual(ItemClassMask.None, a.allowed & a.forbidden, $"{bp.variantId}/{a.id} both allows and forbids");
                Assert.Greater(a.weight, 0f, $"{bp.variantId}/{a.id}");
                Assert.IsFalse(string.IsNullOrEmpty(a.id));
            }
        foreach (AnchorSemantic s in Enum.GetValues(typeof(AnchorSemantic)))
            if (s != AnchorSemantic.None) Assert.IsTrue(seen.Contains(s), $"no room authors a {s} anchor");
    }

    [Test]
    public void AnchorIdsAreUniqueWithinEveryRoom()
    {
        foreach (var bp in GreyboxBlueprints.Create())
        {
            var dup = bp.anchors.GroupBy(a => a.id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.IsEmpty(dup, $"{bp.variantId} has duplicate anchor ids: {string.Join(", ", dup)}");
        }
    }

    [Test]
    public void ItemClearanceVolumesNeverOverlapBlockingGeometry()
    {
        foreach (var bp in GreyboxBlueprints.Create())
            foreach (var a in bp.anchors.Where(a => a.kind == AnchorKind.Item && a.support != null))
                Assert.IsFalse(BlueprintWriter.VolumeBlocked(bp, a, a.support), $"{bp.variantId}: item anchor {a.id} ({a.semantic}) overlaps other furniture");
    }

    [Test]
    public void ItemAndConsoleAnchorsAreClearOfDoorwaysAndInsideTheRoom()
    {
        foreach (var bp in GreyboxBlueprints.Create())
            foreach (var a in bp.anchors.Where(a => a.semantic != AnchorSemantic.None))
            {
                Assert.That(a.x, Is.InRange(0.3f, bp.Width - 0.3f), $"{bp.variantId}/{a.id}");
                Assert.That(a.z, Is.InRange(0.3f, bp.Depth - 0.3f), $"{bp.variantId}/{a.id}");
                foreach (var s in bp.sockets)
                    Assert.IsFalse((int)(a.x / 2f) == s.cell.x && (int)(a.z / 2f) == s.cell.z, $"{bp.variantId}/{a.id} stands in the doorway of socket {s.id}");
            }
    }

    [Test]
    public void ConsoleRolesHaveTheirAnchorsInTheRightRooms()
    {
        bool Has(string room, AnchorSemantic s) => GreyboxBlueprints.Create().Where(b => b.definitionId == room).All(b => b.anchors.Any(a => a.kind == AnchorKind.Objective && a.semantic == s));
        Assert.IsTrue(Has("power_control", AnchorSemantic.FuseSocket));
        Assert.IsTrue(Has("engineering", AnchorSemantic.ObjectiveConsole));
        Assert.IsTrue(Has("security", AnchorSemantic.DoorControlConsole));
        Assert.IsTrue(Has("camera_control", AnchorSemantic.CameraControlConsole));
        Assert.IsTrue(Has("escape_pod_bay", AnchorSemantic.PodLaunchConsole));
        var bay = GreyboxBlueprints.Create().First(b => b.definitionId == "escape_pod_bay");
        var berth = bay.anchors.First(a => a.semantic == AnchorSemantic.PodBerth);
        Assert.That(berth.clearWidth, Is.GreaterThan(4f), "the berth's clearance volume is the pod interior");
    }

    // ---------- Placement profiles ----------

    [Test]
    public void KeycardsLieWhereAKeycardPlausiblyIsOrInAnExplicitDisplacedStory()
    {
        var profile = DefaultPlacementProfiles.Create().First(p => p.id == "keycard");
        var natural = new HashSet<RoomCategory> { RoomCategory.Security, RoomCategory.Bridge, RoomCategory.OfficersQuarters };
        double displacedWeight = 0, totalWeight = 0;
        int displaced = 0, total = 0;
        for (int seed = 0; seed < 25; seed++)
        {
            var (g, l) = Layout(seed);
            foreach (var c in PlacementEngine.Candidates(profile, g, i => l.rooms[i].template, id => SpecById[id]))
            {
                total++;
                totalWeight += c.weight;
                if (natural.Contains(c.room)) { Assert.IsFalse(c.displaced, $"{c.room}: {c.rule}"); continue; }
                Assert.IsTrue(c.displaced, $"layout seed {seed}: a keycard may lie in {c.room} only through an authored displaced rule ({c.rule})");
                Assert.IsTrue(new[] { RoomCategory.Medbay, RoomCategory.CrewQuarters, RoomCategory.MessHall }.Contains(c.room), $"unexpected displaced room {c.room}");
                displaced++;
                displacedWeight += c.weight;
            }
        }
        Assert.Greater(total, 100);
        Assert.Greater(displaced, 0, "displaced stories do occur");
        Assert.Less(displacedWeight, totalWeight * 0.4, "but by weight they are the exception");
    }

    [Test]
    public void FusesAndNoisemakersFollowTheirProfiles()
    {
        var fuse = DefaultPlacementProfiles.Create().First(p => p.id == "fuse");
        var noise = DefaultPlacementProfiles.Create().First(p => p.id == "noisemaker");
        var fuseRooms = new HashSet<RoomCategory> { RoomCategory.PowerControl, RoomCategory.Engineering, RoomCategory.Workshop, RoomCategory.AuxiliaryPower, RoomCategory.MaintenanceRoom, RoomCategory.UtilityRoom, RoomCategory.CargoBay };
        for (int seed = 0; seed < 15; seed++)
        {
            var (g, l) = Layout(seed);
            foreach (var c in PlacementEngine.Candidates(fuse, g, i => l.rooms[i].template, id => SpecById[id]))
                Assert.IsTrue(fuseRooms.Contains(c.room), $"a fuse in {c.room}");
            foreach (var c in PlacementEngine.Candidates(noise, g, i => l.rooms[i].template, id => SpecById[id]))
                Assert.IsTrue(new[] { RoomCategory.EquipmentStore, RoomCategory.Security, RoomCategory.Workshop, RoomCategory.CargoBay, RoomCategory.SecondaryCargoHold }.Contains(c.room), $"a noisemaker in {c.room}");
        }
    }

    [Test]
    public void ACriticalItemNeverLandsOnAnAnchorThatForbidsOrCannotHoldIt()
    {
        var keycard = DefaultPlacementProfiles.Create().First(p => p.id == "keycard");
        var (g, l) = Layout(1);
        foreach (var c in PlacementEngine.Candidates(keycard, g, i => l.rooms[i].template, id => SpecById[id]))
        {
            var a = l.rooms[c.node].template.anchors.First(x => x.id == c.anchorId);
            Assert.IsTrue(a.Allows(ItemClass.Keycard) && a.canHoldCritical);
        }
        // The engineering rack forbids nothing explicitly but does not allow keycards: it never appears.
        Assert.IsFalse(PlacementEngine.Candidates(keycard, g, i => l.rooms[i].template, id => SpecById[id]).Any(c => c.semantic == AnchorSemantic.EngineeringRack));
    }

    // ---------- Determinism and the sample ----------

    [Test]
    public void SameSeedsGiveIdenticalScenarios()
    {
        foreach (var (lay, scn) in new[] { (1, 1), (2, 5), (7, 99), (40, 3) })
        {
            var a = EscapeDirector.Generate(Input(lay), scn);
            var b = EscapeDirector.Generate(Input(lay), scn);
            Assert.IsTrue(a.success && b.success, Seeds(lay, scn) + " " + a.Summary);
            Assert.AreEqual(a.scenario.Canonical(), b.scenario.Canonical(), Seeds(lay, scn));
            Assert.AreEqual(a.scenario.Fingerprint(), b.scenario.Fingerprint());
            Assert.AreEqual(ScenarioReport.Describe(Layout(lay).graph, a), ScenarioReport.Describe(Layout(lay).graph, b), "the report is reproducible too");
        }
    }

    [Test]
    public void DifferentScenarioSeedsGiveDifferentScenarios()
    {
        var prints = Enumerable.Range(1, 12).Select(s => Scenario(3, s).scenario?.Fingerprint()).ToList();
        Assert.IsFalse(prints.Contains(null));
        Assert.GreaterOrEqual(prints.Distinct().Count(), 10);
    }

    [Test]
    public void ALargeSampleOfScenariosIsValidSolvableAndSolo()
    {
        int checkedRounds = 0, choices = 0, optionalItems = 0, withRemote = 0, withDisplaced = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int lay = 0; lay < Layouts; lay++)
            for (int k = 0; k < ScenarioSeedsPerLayout; k++)
            {
                int scn = k * 13 + 1;
                var r = Scenario(lay, scn);
                Assert.IsTrue(r.success, Seeds(lay, scn) + ": " + r.Summary);
                var sc = r.scenario;
                var g = Layout(lay).graph;

                Assert.IsTrue(r.solve.solved, Seeds(lay, scn));
                CollectionAssert.IsEmpty(EscapeDirector.Validate(Input(lay), sc), Seeds(lay, scn));
                Assert.IsTrue(sc.pods.Any(p => p.status == PodStatus.Operational), Seeds(lay, scn) + ": every pod is damaged or wrecked");
                Assert.IsTrue(sc.pods.Any(p => p.Usable), Seeds(lay, scn));
                Assert.GreaterOrEqual(sc.gates.Count, 1);
                Assert.IsTrue(r.solve.HasChoice, Seeds(lay, scn) + ": no meaningful route choice");
                choices++;
                if (sc.items.Any(i => i.optional)) optionalItems++;
                if (sc.gates.Any(x => x.options.Any(o => o.kind == OptionKind.Remote))) withRemote++;
                if (sc.items.Any(i => i.candidates.Any(c => c.displaced))) withDisplaced++;

                // The same scenario with only the optional equipment taken away is still solvable: nothing optional is mandatory.
                Assert.IsTrue(ScenarioSolver.Solve(g, sc, new ScenarioSolver.Options { useOptionalItems = false }).solved, Seeds(lay, scn) + ": optional items were needed");
                foreach (var it in sc.items.Where(i => i.optional)) Assert.IsFalse(it.critical);

                // Every round the host can roll is solvable by one player with one hand: the solver (any item place) and the literal simulation (this place).
                for (int round = 0; round < RoundSeeds; round++)
                {
                    int roundSeed = round * 7919 + lay;
                    var one = sc.ForRound(roundSeed);
                    var sim = OnePlayerSimulator.CanEscape(g, one, 250000);
                    Assert.IsFalse(sim.searchLimitHit, Seeds(lay, scn) + $", round seed {roundSeed}: the simulation ran out of states");
                    Assert.IsTrue(sim.escaped, Seeds(lay, scn) + $", round seed {roundSeed}: one player with one item slot cannot escape\n{ScenarioReport.Describe(g, r)}");
                    checkedRounds++;
                }
            }
        Assert.Greater(optionalItems, Layouts / 2, "optional equipment appears in most scenarios");
        Assert.Greater(withRemote, 5, "remote doors occur");
        Assert.Greater(withDisplaced, 3, "displaced stories occur");
        Assert.Less(sw.Elapsed.TotalSeconds, 300);
        Assert.AreEqual(Layouts * ScenarioSeedsPerLayout * RoundSeeds, checkedRounds);
    }

    [Test]
    public void EquipmentStoreIsNeverRequired()
    {
        int tested = 0;
        for (int lay = 0; lay < Layouts && tested < 20; lay++)
        {
            var g = Layout(lay).graph;
            int store = g.FirstOf(RoomCategory.EquipmentStore);
            if (store < 0 || g.Components(store).Count != 1) continue; // only when the store is not a passage the ship needs
            var sc = Scenario(lay, 1).scenario;
            Assert.IsFalse(sc.items.Any(i => i.critical && i.candidates.Any(c => c.node == store)), Seeds(lay, 1));
            var res = ScenarioSolver.Solve(g, sc, new ScenarioSolver.Options { forbiddenRooms = new HashSet<int> { store } });
            Assert.IsTrue(res.solved, Seeds(lay, 1) + ": the scenario cannot be solved without entering the Equipment Store");
            tested++;
        }
        Assert.Greater(tested, 3, "the sample contained equipment stores to test");
    }

    // ---------- Round reset ----------

    [Test]
    public void RoundChoicesAreIdempotentOrderIndependentAndVaried()
    {
        int lay = Enumerable.Range(0, 30).First(l => Scenario(l, 1).scenario.items.Any(i => i.kind == ItemKind.Keycard));
        var sc = Scenario(lay, 1).scenario;
        foreach (int seed in new[] { 0, 1, 77, int.MaxValue, -5 })
        {
            Assert.AreEqual(sc.ForRound(seed).Canonical(), sc.ForRound(seed).Canonical(), "resetting twice gives the same round");
            foreach (var it in sc.items)
            {
                int a = ScenarioRound.PickIndex(seed, it.id, it.candidates.Count);
                // Asking about other items first changes nothing: no shared random stream.
                foreach (var other in sc.items) ScenarioRound.PickIndex(seed, other.id, other.candidates.Count);
                Assert.AreEqual(a, ScenarioRound.PickIndex(seed, it.id, it.candidates.Count));
                Assert.That(a, Is.InRange(0, Math.Max(0, it.candidates.Count - 1)));
            }
        }
        var key = sc.items.First(i => i.kind == ItemKind.Keycard);
        Assert.Greater(Enumerable.Range(0, 200).Select(s => ScenarioRound.PickIndex(s, key.id, key.candidates.Count)).Distinct().Count(), 1, "rounds differ");
        Assert.AreEqual(0, ScenarioRound.PickIndex(5, "x", 1));
    }

    // ---------- The solver on hand-built cases ----------

    // start(0) - a(1) - b(2) - pod(3), plus a side room s(4) off a.
    static (ShipGraph g, Scenario sc) Chain()
    {
        var g = new ShipGraph();
        var cats = new[] { RoomCategory.PlayerStart, RoomCategory.MaintenanceRoom, RoomCategory.MaintenanceRoom, RoomCategory.EscapePodBay, RoomCategory.Security, RoomCategory.PowerControl, RoomCategory.Engineering };
        for (int i = 0; i < cats.Length; i++) g.AddNode(new RoomSpec { id = "r" + i, displayName = "r" + i, category = cats[i] }, ShipSector.Central, 0, 0);
        g.AddEdge(0, 1); g.AddEdge(1, 2); g.AddEdge(2, 3); g.AddEdge(1, 4); g.AddEdge(1, 5); g.AddEdge(5, 6);
        var sc = new Scenario { spawnNode = 0 };
        sc.pods.Add(new PodPlan { node = 3, status = PodStatus.Operational, mode = LaunchMode.ManualQuiet });
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.PodLaunch, node = 3, anchorId = "p", podNode = 3 });
        return (g, sc);
    }

    static ItemPlan Item(ItemKind kind, bool critical, params int[] nodes)
    {
        var it = new ItemPlan { id = kind.ToString().ToLowerInvariant(), kind = kind, critical = critical, optional = !critical };
        foreach (int n in nodes) it.candidates.Add(new SpotCandidate { node = n, anchorId = "a" + n, rule = "test" });
        return it;
    }

    static DoorGate Gate(ShipGraph g, int a, int b, params GateOption[] options)
    {
        int edge = g.edges.FindIndex(e => (e.a == a && e.b == b) || (e.a == b && e.b == a));
        var gate = new DoorGate { edge = edge, doorNode = b };
        gate.options.AddRange(options);
        return gate;
    }

    [Test]
    public void AKeyBehindItsOwnLockIsUnsolvable()
    {
        var (g, sc) = Chain();
        sc.gates.Add(Gate(g, 1, 2, new GateOption(OptionKind.Keycard)));
        sc.items.Add(Item(ItemKind.Keycard, true, 3)); // the key is in the pod room, behind the door that needs it
        var res = ScenarioSolver.Solve(g, sc);
        Assert.IsFalse(res.solved);
        Assert.IsTrue(res.failures.Any(f => f.Contains("keycard cannot be fetched")), string.Join("\n", res.failures));
        Assert.IsFalse(OnePlayerSimulator.CanEscape(g, sc).escaped, "and the literal simulation agrees");

        sc.items.Clear();
        sc.items.Add(Item(ItemKind.Keycard, true, 4)); // in the side room: fine
        Assert.IsTrue(ScenarioSolver.Solve(g, sc).solved);
        Assert.IsTrue(OnePlayerSimulator.CanEscape(g, sc).escaped);
    }

    [Test]
    public void ADependencyCycleWithNoEntryPointIsRejectedAndAnOverrideBreaksIt()
    {
        // The only fuse is behind a door that needs power, and power needs the fuse.
        var (g, sc) = Chain();
        sc.pods[0].mode = LaunchMode.Loud;
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.FuseSocket, node = 5, anchorId = "s" });
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.Generator, node = 6, anchorId = "g" });
        sc.gates.Add(Gate(g, 1, 4, new GateOption(OptionKind.Power)));
        sc.items.Add(Item(ItemKind.Fuse, true, 4));
        var res = ScenarioSolver.Solve(g, sc);
        Assert.IsFalse(res.solved, "fuse -> power -> door -> fuse is a cycle");
        Assert.IsTrue(res.failures.Any(f => f.Contains("power is never restored")), string.Join("\n", res.failures));
        Assert.IsFalse(OnePlayerSimulator.CanEscape(g, sc).escaped);

        sc.gates[0].options.Add(new GateOption(OptionKind.Override)); // an external entry point: the manual override
        Assert.IsTrue(ScenarioSolver.Solve(g, sc).solved);
        var sim = OnePlayerSimulator.CanEscape(g, sc, wantPlan: true);
        Assert.IsTrue(sim.escaped);
        Assert.IsTrue(sim.plan.Any(p => p.Contains("Override")), string.Join("\n", sim.plan));
    }

    [Test]
    public void RemoteDoorsNeedTheirControlRoomToBeReachable()
    {
        var (g, sc) = Chain();
        sc.gates.Add(Gate(g, 1, 2, new GateOption(OptionKind.Remote, 6))); // the control room is behind the power room, reachable
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.RemoteDoor, node = 6, anchorId = "d", doorEdge = sc.gates[0].edge });
        Assert.IsTrue(ScenarioSolver.Solve(g, sc).solved);
        Assert.IsTrue(OnePlayerSimulator.CanEscape(g, sc).escaped);

        sc.gates.Add(Gate(g, 1, 5, new GateOption(OptionKind.Keycard)));
        sc.gates.Add(Gate(g, 5, 6, new GateOption(OptionKind.Remote, 6)));
        sc.items.Add(Item(ItemKind.Keycard, true, 6)); // the key and the control room are behind the doors that need them
        Assert.IsFalse(ScenarioSolver.Solve(g, sc).solved);
        Assert.IsFalse(OnePlayerSimulator.CanEscape(g, sc).escaped);
    }

    [Test]
    public void PodsAreHandledOneByOne()
    {
        var (g, sc) = Chain();
        sc.pods[0].status = PodStatus.Unusable;
        Assert.IsFalse(ScenarioSolver.Solve(g, sc).solved, "no usable pod");
        sc.pods[0].status = PodStatus.Damaged; // needs power; there is no power chain
        var res = ScenarioSolver.Solve(g, sc);
        Assert.IsFalse(res.solved);
        Assert.IsTrue(res.failures.Any(f => f.Contains("needs power")), string.Join("\n", res.failures));
        sc.pods[0].status = PodStatus.Operational;
        Assert.IsTrue(ScenarioSolver.Solve(g, sc).solved);
    }

    [Test]
    public void OptionalItemsNeverUnlockAnything()
    {
        var (g, sc) = Chain();
        sc.gates.Add(Gate(g, 2, 3, new GateOption(OptionKind.Keycard)));
        var optionalKey = Item(ItemKind.Keycard, false, 4); // an optional keycard would open the pod door...
        sc.items.Add(optionalKey);
        Assert.IsFalse(ScenarioSolver.Solve(g, sc).solved, "...but optional items are ignored, so the scenario is not solvable by it");
        Assert.IsTrue(ScenarioSolver.Solve(g, sc, new ScenarioSolver.Options { useOptionalItems = true }) != null);
    }

    [Test]
    public void DifferentCostsAreAChoiceAndIdenticalOnesAreNot()
    {
        var (g, sc) = Chain();
        sc.pods.Add(new PodPlan { node = 6, status = PodStatus.Operational, mode = LaunchMode.ManualQuiet });
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.PodLaunch, node = 6, anchorId = "q", podNode = 6 });
        Assert.IsFalse(ScenarioSolver.Solve(g, sc).HasChoice, "two quiet pods with no obstacles cost the same");
        sc.pods[1].mode = LaunchMode.Loud;
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.FuseSocket, node = 5, anchorId = "s" });
        sc.consoles.Add(new ConsolePlan { role = ConsoleRole.Generator, node = 6, anchorId = "g" });
        sc.items.Add(Item(ItemKind.Fuse, true, 4));
        var res = ScenarioSolver.Solve(g, sc);
        Assert.IsTrue(res.solved);
        Assert.IsTrue(res.HasChoice, "a quiet slow pod against a loud powered one");
    }

    [Test]
    public void ScenarioValidationCatchesBadAnchorsAndStoreItems()
    {
        int lay = Enumerable.Range(0, 30).First(l => Scenario(l, 1).scenario.items.Any(i => i.kind == ItemKind.Keycard));
        var input = Input(lay);
        var sc = Scenario(lay, 1).scenario;
        var bad = sc.ForRound(0); // a copy to break
        var key = bad.items.First(i => i.kind == ItemKind.Keycard);
        key.candidates[0] = new SpotCandidate { node = key.candidates[0].node, anchorId = "no_such_anchor", rule = "x" };
        Assert.IsTrue(EscapeDirector.Validate(input, bad).Any(p => p.Contains("does not exist")));

        bad = sc.ForRound(0);
        bad.items.First(i => i.kind == ItemKind.Keycard).cls = ItemClass.Fuse; // the anchor does not allow it
        Assert.IsTrue(EscapeDirector.Validate(input, bad).Any(p => p.Contains("does not allow")));

        bad = sc.ForRound(0);
        bad.gates.Add(new DoorGate { edge = 0 }); // no way to open
        Assert.IsTrue(EscapeDirector.Validate(input, bad).Any(p => p.Contains("no way to open")));
    }

    [Test]
    public void TheDeveloperReportListsEverythingAndIsMarkedAsSpoilers()
    {
        var g = Layout(2).graph;
        var r = Scenario(2, 1);
        var text = ScenarioReport.Describe(g, r);
        StringAssert.Contains("spoilers", text);
        StringAssert.Contains("layout seed 2, scenario seed 1", text);
        foreach (var it in r.scenario.items.Where(i => i.critical))
            foreach (var c in it.candidates) StringAssert.Contains(c.anchorId, text);
        StringAssert.Contains("Reachability stages", text);
        StringAssert.Contains("Intended solutions", text);
        StringAssert.Contains("Route choices", text);
        var failed = new ScenarioResult();
        failed.rejections.Add("attempt 1: unsolvable: test reason");
        StringAssert.Contains("test reason", ScenarioReport.Describe(g, failed));
    }

    [Test]
    public void GenerationFailsCleanlyWhenNoScenarioFits()
    {
        var input = Input(1);
        var hard = new DirectorSettings { maxAttempts = 3, minGates = 40, maxGates = 40 };
        var r = EscapeDirector.Generate(input, 1, hard);
        Assert.IsTrue(r.success || r.rejections.Count == 3, "either it found something or it gave a reason for each attempt");
        var noBays = new DirectorInput { graph = new ShipGraph(), templateOf = input.templateOf, specOf = input.specOf };
        var none = EscapeDirector.Generate(noBays, 1);
        Assert.IsFalse(none.success);
        StringAssert.Contains("no player start", none.rejections[0]);
    }
}
