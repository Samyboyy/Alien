using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

// Cross-system regression tests (pure rule functions only): sight versus sound versus decoys versus lockers versus vents, the creature's
// decision log, and the round-reset invariants. They call the real rule code; they do not repeat the intended answer. They do not verify the
// creature's movement, the NavMesh, Netcode delivery or any multiplayer behaviour.
public class RegressionTests
{
    // ---------- Vent routing never teleports ----------

    // Entrances 0-4, junctions 5 and 6: 0,1,2 hang off junction 5; 3,4 off junction 6; 5-6 joined.
    static VentGraph Network()
    {
        var g = new VentGraph(7);
        g.AddEdge(0, 5, 8f);
        g.AddEdge(1, 5, 12f);
        g.AddEdge(2, 5, 9f);
        g.AddEdge(5, 6, 20f);
        g.AddEdge(3, 6, 7f);
        g.AddEdge(4, 6, 15f);
        return g;
    }

    [Test]
    public void EveryVentRouteIsAContinuousWalkAlongRealEdges()
    {
        var g = Network();
        for (int from = 0; from < 5; from++)
            for (int to = 0; to < 5; to++)
            {
                var nodes = new List<int>();
                var edges = new List<int>();
                Assert.IsTrue(g.Route(from, to, nodes, edges, out float length), $"{from}->{to}");
                Assert.AreEqual(from, nodes[0]);
                Assert.AreEqual(to, nodes[^1]);
                float sum = 0f;
                for (int k = 0; k < edges.Count; k++)
                {
                    var (a, b, len) = g.Edge(edges[k]);
                    Assert.IsTrue((a == nodes[k] && b == nodes[k + 1]) || (b == nodes[k] && a == nodes[k + 1]), "each step uses an edge that really joins those two nodes: no jumps");
                    sum += len;
                }
                Assert.AreEqual(length, sum, 1e-3f);
            }
    }

    [Test]
    public void AReroutePicksOnlyAnExistingReachableExitAndTheBetterOne()
    {
        var g = Network();
        // Standing at junction 5; evidence is near exit 3 (6 m on foot) and far from the others.
        float[] metres = { 40f, 40f, 40f, 6f, 40f, -1f, -1f };
        float[] none = new float[7];
        int best = VentRules.BestExitFrom(g, 5, metres, none, 3f, 5f, null);
        Assert.AreEqual(3, best, "the legal exit that gets it nearest the evidence, by the duct graph and the ground walk");
        float[] unreachable = { -1f, -1f, -1f, -1f, -1f, -1f, -1f };
        Assert.AreEqual(-1, VentRules.BestExitFrom(g, 5, unreachable, none, 3f, 5f, null), "no exit: it keeps its route, it never invents a destination");
        float[] unsafe3 = { 40f, 40f, 40f, float.PositiveInfinity, 40f, 0f, 0f };
        Assert.AreNotEqual(3, VentRules.BestExitFrom(g, 5, metres, unsafe3, 3f, 5f, null), "an exit with a player at it is never chosen");
    }

    [Test]
    public void ARetryCooldownStopsCancelReenterOscillation()
    {
        var a = new VentAttempt();
        int begun = 0;
        double now = 0;
        for (int i = 0; i < 100; i++) // every 0.5 s: try to start a trip, and cancel it for evidence at once
        {
            now += 0.5;
            if (a.Begin(1, 2, now)) { begun++; a.Cancel(now, 6f); }
        }
        Assert.LessOrEqual(begun, 9, "at most one attempt every six seconds, however often it tries");
        Assert.GreaterOrEqual(begun, 7);
    }

    [Test]
    public void ADecoyNeverOutranksDirectSightOrAHeldTrailBeforeCommit()
    {
        var t = VentEvidenceTuning.Default;
        var decoyWithTrail = VentEvidenceRules.Decide(VentCommitment.Approaching, VentEvidenceKind.Decoy, 1f, 0f, 1f, true, true, t);
        var sight = VentEvidenceRules.Decide(VentCommitment.Approaching, VentEvidenceKind.DirectSight, 0f, 0f, 99f, false, false, t);
        Assert.AreEqual(VentEvidenceAction.Ignore, decoyWithTrail);
        Assert.AreEqual(VentEvidenceAction.Cancel, sight);
        // In the duct the same noisemaker is only stored, never a re-route, while a trail is held.
        Assert.AreEqual(VentEvidenceAction.Store, VentEvidenceRules.Decide(VentCommitment.InDuct, VentEvidenceKind.Decoy, 1f, 0f, 1f, true, true, t));
    }

    // ---------- Decoys ----------

    [Test]
    public void ADecoyCannotBreakAChaseOrEraseAValuableTrailButCanAttractAnIdleCreature()
    {
        // What the creature holds: a fresh sighting of a player (score 1, a player trail).
        Assert.IsFalse(DecoyRules.Accept(false, true, 1f, true, false, 1f, 1.5f, true), "fresh sight is a player trail");
        // The same trail faded to nothing, or a patrol with no evidence at all.
        Assert.IsTrue(DecoyRules.Accept(false, true, 0f, true, false, 0.4f, 1.5f, true));
        Assert.IsTrue(DecoyRules.Accept(false, true, 0f, false, false, 0.4f, 1.5f, true));
    }

    [Test]
    public void RepeatedPulsesFromTheDeviceBeingFollowedNeverSwitchTargetsAndAnotherDeviceMustBeClearlyBetter()
    {
        int switches = 0;
        for (int i = 0; i < 30; i++)
        {
            if (DecoyRules.Accept(false, true, 0.5f, false, sameSourceAsCurrent: true, 0.4f, 1.5f, i % 2 == 0)) continue; // keeps heading for the same device
            switches++;
        }
        Assert.AreEqual(0, switches);
        Assert.IsFalse(DecoyRules.Accept(false, true, 0.5f, false, false, 0.6f, 1.5f, true), "a similar second device does not pull it away");
        Assert.IsTrue(DecoyRules.Accept(false, true, 0.2f, false, false, 0.9f, 1.5f, true), "a clearly stronger one may");
    }

    // ---------- Lockers: what an opened door reveals ----------

    // The authored numbers: a locker's cover 0.85 with a 0.25 residual, from an open door the creature is inspecting.
    static float SecondsToRecognise(bool doorOpen, bool watched)
    {
        float cover = 0.65f + (0.85f - 0.65f) * 0.25f; // Lerp(referenceCover, lockerCover, residual) while inspecting
        float baseSeconds = SightRules.ConcealedSeconds(0.5f, cover, 0.65f, 0.25f);
        if (watched) baseSeconds = System.Math.Max(0.25f, baseSeconds * 0.6f);
        float exposure = doorOpen ? 3f / 3f : 0f; // a closed door leaves no unobstructed body sample at all
        float rate = SightRules.Rate(exposure, 1.5f, 0f, 14f, 90f, 2.5f, 0f, 1f, baseSeconds, 0.3f, 0.5f);
        return rate <= 0f ? float.PositiveInfinity : 1f / rate;
    }

    [Test]
    public void AClosedLockerDoorMeansNobodyIsEverRecognisedAnOpenOneMeansAShortTenseWindow()
    {
        Assert.IsTrue(float.IsPositiveInfinity(SecondsToRecognise(false, false)), "no sample, no recognition: nobody is caught through a closed door");
        float open = SecondsToRecognise(true, false), watched = SecondsToRecognise(true, true);
        Assert.That(open, Is.InRange(0.3f, 1.5f), "short but not instant");
        Assert.Less(watched, open, "a watched entry is recognised faster");
        Assert.Less(open, 2f, "inside its look time (2 s) the occupant is recognised");
    }

    // ---------- The decision log ----------

    [Test]
    public void TheRingKeepsTheNewestAndNeverGrows()
    {
        var ring = new RingBuffer<DecisionEvent>(96);
        for (int i = 0; i < 500; i++) ring.Add(new DecisionEvent { time = i, reason = DecisionReason.HeardPlayer });
        Assert.AreEqual(96, ring.Count);
        Assert.AreEqual(499.0, ring.FromNewest(0).time);
        Assert.AreEqual(404.0, ring.FromNewest(95).time, "the oldest kept is the 96th newest");
        ring.Clear();
        Assert.AreEqual(0, ring.Count);
        ring.Add(new DecisionEvent { time = 7 });
        Assert.AreEqual(7.0, ring.FromNewest(0).time);
    }

    [Test]
    public void ReasonCodesAreStable()
    {
        // Dumped files and notes refer to these numbers: they are appended to, never renumbered.
        Assert.AreEqual(0, (int)DecisionReason.Unspecified);
        Assert.AreEqual(1, (int)DecisionReason.Spawned);
        Assert.AreEqual(2, (int)DecisionReason.RoundReset);
        Assert.AreEqual(3, (int)DecisionReason.DirectSight);
        Assert.AreEqual(12, (int)DecisionReason.VentCancelled);
        Assert.AreEqual(23, (int)DecisionReason.SearchExpired);
        Assert.AreEqual(28, (int)DecisionReason.RoomHunt);
        Assert.AreEqual(System.Enum.GetValues(typeof(DecisionReason)).Length, (int)DecisionReason.RoomHunt + 1, "no gaps or duplicates");
    }

    [Test]
    public void ADecisionLineSaysWhatChangedAndWhy()
    {
        var sb = new StringBuilder();
        string Name(byte s) => s == 0 ? "Patrol" : "Investigate";
        var e = new DecisionEvent { time = 10, from = 0, to = 1, reason = DecisionReason.HeardPlayer, effect = DecisionEffect.Interrupted, target = ulong.MaxValue, evidenceKind = 2, evidenceAge = 0.5f, evidenceStrength = 0.8f, detail = "sprint" };
        DecisionFormat.Line(sb, e, Name, 12);
        string line = sb.ToString();
        StringAssert.Contains("Patrol -> Investigate", line);
        StringAssert.Contains("HeardPlayer", line);
        StringAssert.Contains("[Interrupted]", line);
        StringAssert.Contains("sprint", line);
        StringAssert.DoesNotContain("target", line, "no target when there is none");
    }

    [Test]
    public void TheDiagnosticsCarryNoRpcNetworkVariableOrPerFrameWork()
    {
        string root = FindRoot();
        if (root == null) Assert.Ignore("project folder not found from here");
        string text = File.ReadAllText(Path.Combine(root, "Assets", "Scripts", "Creature", "CreatureAI.Diagnostics.cs"));
        foreach (var forbidden in new[] { "[Rpc", "NetworkVariable", "ClientRpc", "ServerRpc" })
            Assert.IsFalse(text.Contains(forbidden), $"the diagnostics must not use {forbidden}");
        Assert.IsTrue(text.Contains("Conditional(\"DEVELOPMENT_BUILD\")"), "compiled out of release builds");
    }

    // ---------- Round reset invariants ----------

    [Test]
    public void TwoConsecutiveResetsLeaveNoStaleState()
    {
        for (int round = 0; round < 2; round++)
        {
            // Dirty everything the way a round would.
            var attempt = new VentAttempt();
            attempt.Begin(1, 2, 10);
            attempt.SetPhase(VentPhase.Entering);
            attempt.Commit();
            attempt.Reroute(3, 2);
            var pending = new PendingEvidence<int>();
            pending.Offer(1, 0.7f, 10, 10, 10);
            var decoys = new DecoyMemory();
            decoys.Examine(5, 9f, 2.5f);
            decoys.Spend(40f, 100, 30f, 45f);
            var occupancy = new LockerOccupancy();
            occupancy.TryEnter(2);
            var inspection = new LockerInspection();
            inspection.Begin(1f, 1f, 1f, 1f);
            inspection.Tick(1.5f);
            var breath = new BreathModel(5f, 1.2f, 7f, 0.3f, 0.25f);
            breath.TryHold(0);
            for (int i = 0; i < 400; i++) breath.Tick(0.02f);
            var search = new SearchMemory();
            search.MarkPoint(3);
            search.MarkRoom(4);
            search.MarkLinkFailed(5);
            var awareness = new AwarenessState();
            awareness.Tick(1f, 5f, true, 0.4f, 2.5f);
            var timeline = new NoisemakerTimeline(0.8f, 0.4f, 7f, 1f, 1.5f, 6f);
            timeline.Begin(0);
            timeline.OnImpact(0.1);
            timeline.Tick(3);
            var ring = new RingBuffer<DecisionEvent>(8);
            ring.Add(new DecisionEvent());

            // The reset (what each system's ResetForRound / Clear / Reset does).
            attempt.Reset();
            pending.Clear();
            decoys.Clear();
            occupancy.ForceRelease();
            inspection.Cancel();
            breath.Reset();
            search.Clear();
            awareness.Clear();
            timeline.Halt();
            ring.Clear();

            Assert.IsFalse(attempt.Active);
            Assert.IsTrue(attempt.MayBegin(0), "no retry cooldown survives");
            Assert.IsFalse(pending.Has);
            Assert.AreEqual(0, decoys.IgnoredCount);
            Assert.IsTrue(decoys.InterestLeft(0));
            Assert.IsFalse(occupancy.Occupied);
            Assert.IsFalse(inspection.Active);
            Assert.IsFalse(inspection.DoorShouldBeOpen);
            Assert.AreEqual(1f, breath.Level);
            Assert.IsFalse(breath.Holding);
            Assert.IsFalse(breath.ConsumeForcedRelease(), "no pending gasp");
            Assert.AreEqual(0, search.PointCount + search.Rooms.Count + search.FailedLinks.Count);
            Assert.AreEqual(0f, awareness.Value);
            Assert.IsTrue(timeline.Expired, "a device from the old round never pulses");
            Assert.AreEqual(0, ring.Count);
        }
    }

    [Test]
    public void LockerOccupancyIsReleasedWhateverEndsTheStay()
    {
        // Leaving, death, escape, disconnect and a restart all end up releasing the same occupant record.
        foreach (var how in new[] { "leave", "death", "escape", "disconnect", "restart" })
        {
            var o = new LockerOccupancy();
            Assert.IsTrue(o.TryEnter(3), how);
            bool released = how == "leave" ? o.Release(3) : o.ForceRelease();
            Assert.IsTrue(released, how);
            Assert.IsFalse(o.Occupied, how);
            Assert.IsTrue(o.TryEnter(4), $"the locker is usable again after {how}");
        }
    }

    [Test]
    public void ABreathThatRanOutGaspsOnceAndNeverAfterAReset()
    {
        var b = new BreathModel(5f, 1.2f, 7f, 0.3f, 0.25f);
        b.TryHold(0);
        for (int i = 0; i < 400; i++) b.Tick(0.02f);
        Assert.IsTrue(HiddenBreathing.Gasps(b.ConsumeForcedRelease(), 4f, 10f));
        for (int i = 0; i < 400; i++) b.Tick(0.02f);
        Assert.IsFalse(HiddenBreathing.Gasps(b.ConsumeForcedRelease(), 4f, 10f), "once");
        b.TryHold(100);
        for (int i = 0; i < 400; i++) b.Tick(0.02f);
        b.Reset();
        Assert.IsFalse(b.ConsumeForcedRelease(), "a restart discards a pending gasp");
    }

    [Test]
    public void BoardingOrderKeepsOnlyThoseStillInsideAfterAReset()
    {
        var order = new List<ulong> { 1, 2, 3 };
        EscapeRules.UpdateBoarding(order, new HashSet<ulong>());
        Assert.IsEmpty(order, "an empty pod after a restart has nobody aboard");
        EscapeRules.UpdateBoarding(order, new HashSet<ulong> { 2 });
        Assert.AreEqual(new ulong[] { 2 }, order);
    }

    static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory, "C:/Unity Projects/Alien" })
            for (var dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, "Assets", "Scripts", "Creature"))) return dir;
        return null;
    }
}
