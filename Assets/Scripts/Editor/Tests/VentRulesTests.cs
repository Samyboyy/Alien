using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the ventilation graph and the rules for choosing, spacing and fairness of vent trips (pure logic).
// These do not verify any Unity behaviour: NavMesh, networking, movement or rendering.
public class VentRulesTests
{
    const float Walk = 3f, Vent = 4f;

    // Entrances 0..3, junction 4. 0-4 (10 m), 1-4 (10 m), 2-4 (30 m), 3-4 (12 m); 2 is the far end.
    static VentGraph Network()
    {
        var g = new VentGraph(5);
        g.AddEdge(0, 4, 10f);
        g.AddEdge(1, 4, 10f);
        g.AddEdge(2, 4, 30f);
        g.AddEdge(3, 4, 12f);
        return g;
    }

    static float[] Zero(int n) => new float[n];

    [Test]
    public void RouteCostIsTheSumOfDuctLengths()
    {
        var g = Network();
        Assert.AreEqual(20f, g.Distance(0, 1), 1e-4f, "through the junction");
        Assert.AreEqual(40f, g.Distance(0, 2), 1e-4f);
        Assert.AreEqual(0f, g.Distance(1, 1));
        var nodes = new List<int>();
        var edges = new List<int>();
        Assert.IsTrue(g.Route(0, 3, nodes, edges, out float len));
        CollectionAssert.AreEqual(new[] { 0, 4, 3 }, nodes);
        CollectionAssert.AreEqual(new[] { 0, 3 }, edges);
        Assert.AreEqual(22f, len, 1e-4f);
        Assert.AreEqual(1, g.Degree(0));
        Assert.AreEqual(4, g.Degree(4), "the junction");
    }

    [Test]
    public void DisconnectedGraphGivesNoRouteAndNoPlan()
    {
        var g = new VentGraph(4);
        g.AddEdge(0, 1, 5f);
        g.AddEdge(2, 3, 5f);
        Assert.AreEqual(-1f, g.Distance(0, 3));
        Assert.IsFalse(g.Route(0, 3, null, null, out _));
        // Entry 0 and exit 3 are the only reachable pair for the creature, but they are not connected.
        var plan = VentRules.Choose(g, new[] { 0f, -1f, -1f, 0f }, new[] { -1f, -1f, -1f, 0f }, Zero(4), Zero(4), 100f, Walk, Vent, 1f, 1f);
        Assert.IsFalse(plan.found);
    }

    [Test]
    public void ExitIsChosenFromTheEvidenceNotFromAnyPlayer()
    {
        var g = Network();
        // The creature is at entrance 0 (0 m). Evidence is closest on foot to exit 2 (5 m) and far from the others (80 m).
        var entry = new[] { 0f, 30f, 60f, 30f };
        var exit = new[] { 80f, 80f, 5f, 80f };
        var plan = VentRules.Choose(g, entry, exit, Zero(4), Zero(4), groundSeconds: 200f, Walk, Vent, 5f, 0.9f);
        Assert.IsTrue(plan.found);
        Assert.AreEqual(0, plan.entry);
        Assert.AreEqual(2, plan.exit, "the exit nearest the remembered evidence");
        Assert.AreEqual(40f / Vent + 5f / Walk, plan.seconds, 1e-3f);
        Assert.AreEqual(200f - plan.seconds, plan.advantage, 1e-3f);
    }

    [Test]
    public void AVentWithNoMeaningfulAdvantageIsRejected()
    {
        var g = Network();
        var entry = new[] { 0f, 30f, 60f, 30f };
        var exit = new[] { 80f, 80f, 5f, 80f };
        // Walking takes 25 s; the best vent trip takes about 11.7 s, saving 13 s.
        Assert.IsTrue(VentRules.Choose(g, entry, exit, Zero(4), Zero(4), 25f, Walk, Vent, 5f, 0.9f).found);
        Assert.IsFalse(VentRules.Choose(g, entry, exit, Zero(4), Zero(4), 25f, Walk, Vent, 20f, 0.9f).found, "saves less than the minimum advantage");
        Assert.IsFalse(VentRules.Choose(g, entry, exit, Zero(4), Zero(4), 12f, Walk, Vent, 0.1f, 0.9f).found, "not clearly faster than walking");
        Assert.IsFalse(VentRules.Choose(g, entry, exit, Zero(4), Zero(4), 10f, Walk, Vent, 0f, 1f).found, "slower than walking");
    }

    [Test]
    public void TiesAreBrokenDeterministically()
    {
        var g = Network();
        // Entrances 0 and 1 are identical (10 m from the junction), exits 0/1 identical too.
        var entry = new[] { 5f, 5f, -1f, -1f };
        var exit = new[] { 5f, 5f, -1f, -1f };
        var plan = VentRules.Choose(g, entry, exit, Zero(4), Zero(4), 100f, Walk, Vent, 1f, 1f);
        Assert.IsTrue(plan.found);
        Assert.AreEqual(0, plan.entry, "lowest entry on a tie");
        Assert.AreEqual(1, plan.exit);
        var again = VentRules.Choose(g, entry, exit, Zero(4), Zero(4), 100f, Walk, Vent, 1f, 1f);
        Assert.AreEqual(plan.entry, again.entry);
        Assert.AreEqual(plan.exit, again.exit);
        // Two equal-length routes between the same nodes: the lower node index is the way through.
        var ring = new VentGraph(4);
        ring.AddEdge(0, 1, 5f);
        ring.AddEdge(1, 3, 5f);
        ring.AddEdge(0, 2, 5f);
        ring.AddEdge(2, 3, 5f);
        var nodes = new List<int>();
        ring.Route(0, 3, nodes, null, out _);
        CollectionAssert.AreEqual(new[] { 0, 1, 3 }, nodes);
    }

    [Test]
    public void RecentlyUsedEntrancesAreAvoidedMostWhenMostRecent()
    {
        var recent = new List<int> { 2, 3, 1 }; // 1 was the latest
        float last = VentRules.ReusePenalty(1, recent, 12f);
        float older = VentRules.ReusePenalty(2, recent, 12f);
        Assert.AreEqual(12f, last, 1e-4f);
        Assert.Less(older, last);
        Assert.AreEqual(0f, VentRules.ReusePenalty(0, recent, 12f));

        // With the penalty, the freshly used exit loses to an almost-as-good alternative.
        var g = Network();
        var entry = new[] { 0f, -1f, -1f, -1f };
        var exit = new[] { -1f, 10f, 12f, -1f };
        var none = VentRules.Choose(g, entry, exit, Zero(4), new[] { 0f, 0f, 0f, 0f }, 200f, Walk, Vent, 1f, 1f);
        var penalised = VentRules.Choose(g, entry, exit, Zero(4), new[] { 0f, last, 0f, 0f }, 200f, Walk, Vent, 1f, 1f);
        Assert.AreEqual(1, none.exit);
        Assert.AreEqual(2, penalised.exit, "not the exit it just used");
    }

    [Test]
    public void CooldownBlocksRepeatedVentTrips()
    {
        Assert.IsFalse(VentRules.CooldownReady(110, 100, 45));
        Assert.IsTrue(VentRules.CooldownReady(145, 100, 45));
        Assert.IsTrue(VentRules.CooldownReady(10, double.NegativeInfinity, 45), "after a reset there is no history");
    }

    [Test]
    public void DangerousOrWatchedExitsAreRejectedOrPenalised()
    {
        Assert.IsTrue(float.IsPositiveInfinity(VentRules.ExitPenalty(1.5f, false, false, 2.5f, 10f, 12f)), "right on top of a player: never");
        Assert.IsTrue(float.IsPositiveInfinity(VentRules.ExitPenalty(1.5f, true, true, 2.5f, 10f, 12f)), "even when hunting that spot");
        Assert.AreEqual(12f, VentRules.ExitPenalty(6f, true, false, 2.5f, 10f, 12f), "in direct view and close");
        Assert.AreEqual(0f, VentRules.ExitPenalty(6f, true, true, 2.5f, 10f, 12f), "already hunting evidence there: no view penalty");
        Assert.AreEqual(0f, VentRules.ExitPenalty(6f, false, false, 2.5f, 10f, 12f), "close but unseen");
        Assert.AreEqual(0f, VentRules.ExitPenalty(30f, true, false, 2.5f, 10f, 12f), "far enough that a view does not matter");

        var g = Network();
        var plan = VentRules.Choose(g, new[] { 0f, -1f, -1f, -1f }, new[] { -1f, 10f, 12f, -1f }, Zero(4),
            new[] { 0f, float.PositiveInfinity, 0f, 0f }, 200f, Walk, Vent, 1f, 1f);
        Assert.AreEqual(2, plan.exit, "the unsafe exit is skipped");
    }

    [Test]
    public void ReplanningInTheDuctPicksTheBestRemainingExitAndHonoursExclusions()
    {
        var g = Network();
        var exit = new[] { 60f, 60f, 5f, 60f };
        var pen = Zero(4);
        // The creature is at the junction (node 4).
        Assert.AreEqual(2, VentRules.BestExitFrom(g, 4, exit, pen, Walk, Vent, null), "30 m of duct then 5 m on foot beats 10 m of duct then 60 m");
        Assert.AreEqual(1, VentRules.BestExitFrom(g, 4, exit, pen, Walk, Vent, new HashSet<int> { 2, 0 }), "the best alternative, never an excluded one");
        Assert.AreEqual(-1, VentRules.BestExitFrom(g, 4, exit, pen, Walk, Vent, new HashSet<int> { 0, 1, 2, 3 }));
        Assert.AreEqual(1, VentRules.BestExitFrom(g, 4, exit, pen, Walk, Vent, new HashSet<int> { 0, 2, 3 }));
    }

    [Test]
    public void RemainingTravelTimeComesFromRouteLengthAndSpeed()
    {
        Assert.AreEqual(5f, VentRules.RemainingSeconds(40f, 20f, 4f), 1e-4f);
        Assert.AreEqual(0f, VentRules.RemainingSeconds(40f, 50f, 4f));
        Assert.IsTrue(float.IsPositiveInfinity(VentRules.RemainingSeconds(10f, 0f, 0f)));
    }

    // ---------- Network validation ----------

    // The Ship's topology: entrances 0-6, junctions 7 and 8; E0,E1,E4,E3-J0, J0-J1, J1-E5, J1-E6, E2-E1.
    static readonly (int a, int b)[] ShipEdges = { (0, 7), (1, 7), (4, 7), (3, 7), (7, 8), (8, 5), (8, 6), (2, 1) };

    static List<bool> All(int n, params int[] missing)
    {
        var l = new List<bool>();
        for (int i = 0; i < n; i++) l.Add(System.Array.IndexOf(missing, i) < 0);
        return l;
    }

    static List<(int, int)> Edges(params (int, int)[] e) => new(e);

    [Test]
    public void TheShipsNetworkIsValid()
    {
        var r = VentValidation.Check(All(9), 7, ShipEdges, new[] { 0, 1, 2, 3, 4, 5, 6 });
        Assert.IsTrue(r.Valid, string.Join("; ", r.problems));
        Assert.AreEqual((9, 8, 8, 1), (r.nodes, r.edges, r.validEdges, r.components));
    }

    [Test]
    public void LostEntrancesRejectExactlyTheEdgesThatUseThem()
    {
        // The real fault: the seven entrance components could not be loaded, so every edge that ends at one was rejected. Only J0-J1 survives.
        var edges = new List<(int, int)>();
        foreach (var (a, b) in ShipEdges) edges.Add((a < 7 ? -1 : a, b < 7 ? -1 : b));
        var r = VentValidation.Check(All(9, 0, 1, 2, 3, 4, 5, 6), 7, edges);
        Assert.AreEqual(1, r.validEdges);
        for (int k = 0; k < 8; k++) Assert.AreEqual(k != 4, r.problems.Exists(p => p.StartsWith($"edge {k}:")), $"edge {k}");
    }

    [Test]
    public void MissingStartMissingEndAndUnregisteredEndsAreReportedPerEdge()
    {
        var r = VentValidation.Check(All(4), 2, Edges((-1, 2), (2, -1), (0, 2), (3, 2)));
        Assert.IsTrue(r.problems.Exists(p => p.StartsWith("edge 0:") && p.Contains("start")));
        Assert.IsTrue(r.problems.Exists(p => p.StartsWith("edge 1:") && p.Contains("end")));
        Assert.IsFalse(r.problems.Exists(p => p.StartsWith("edge 2:")));
    }

    [Test]
    public void DuplicateAndSelfEdgesAndDuplicateIdsAreFound()
    {
        var r = VentValidation.Check(All(3), 2, Edges((0, 2), (2, 0), (1, 1), (1, 2)), new[] { 0, 0 });
        Assert.IsTrue(r.problems.Exists(p => p.StartsWith("edge 1:") && p.Contains("duplicates")), "a duct is travelled both ways: B-A repeats A-B");
        Assert.IsTrue(r.problems.Exists(p => p.StartsWith("edge 2:") && p.Contains("starts and ends")));
        Assert.IsTrue(r.problems.Exists(p => p.Contains("id 0 is used more than once")));
        Assert.AreEqual(2, r.validEdges);
    }

    [Test]
    public void IsolatedEntrancesAndSplitNetworksAreFound()
    {
        var isolated = VentValidation.Check(All(3), 2, Edges((0, 2)));
        Assert.IsTrue(isolated.problems.Exists(p => p.Contains("entrance 1 has no duct")));
        var split = VentValidation.Check(All(6), 4, Edges((0, 4), (1, 4), (2, 5), (3, 5)));
        Assert.AreEqual(2, split.components);
        Assert.IsTrue(split.problems.Exists(p => p.Contains("2 separate parts")));
        var lonely = VentValidation.Check(All(4), 3, Edges((0, 3), (1, 2)));
        Assert.IsTrue(lonely.problems.Exists(p => p.Contains("entrance 0 has no other entrance")), "its only neighbour is a dead-end junction");
    }

    [Test]
    public void ValidationIsDeterministicAndChangesNothing()
    {
        var present = All(9, 3);
        var edges = new List<(int, int)>(ShipEdges);
        var a = VentValidation.Check(present, 7, edges);
        var b = VentValidation.Check(present, 7, edges);
        CollectionAssert.AreEqual(a.problems, b.problems);
        Assert.AreEqual(9, present.Count);
        CollectionAssert.AreEqual(ShipEdges, edges);
        // The runtime graph is untouched by anything a round does: routes between the same nodes stay the same.
        var g = new VentGraph(9);
        foreach (var (x, y) in ShipEdges) g.AddEdge(x, y, 10f);
        var first = new List<int>();
        Assert.IsTrue(g.Route(0, 6, first, null, out float len1));
        var again = new List<int>();
        Assert.IsTrue(g.Route(0, 6, again, null, out float len2));
        CollectionAssert.AreEqual(first, again);
        Assert.AreEqual(len1, len2);
        Assert.AreEqual(8, g.EdgeCount);
    }
}
