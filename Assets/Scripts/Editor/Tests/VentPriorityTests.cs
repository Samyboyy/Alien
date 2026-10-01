using NUnit.Framework;

// EditMode tests for the vent commitment and evidence-priority rules (pure logic). They do not verify the creature's movement, the NavMesh, the
// vent audio, Netcode replication, or any multiplayer behaviour; those need the runtime checklist.
public class VentPriorityTests
{
    static readonly VentEvidenceTuning T = VentEvidenceTuning.Default;

    static VentEvidenceAction Pre(VentEvidenceKind kind, float strength = 0.6f, float distance = 6f, bool hunting = true, bool playerHeld = false, float age = 0.2f) =>
        VentEvidenceRules.Decide(VentCommitment.Approaching, kind, strength, age, distance, hunting, playerHeld, T);

    static VentEvidenceAction InDuct(VentEvidenceKind kind, float strength = 0.6f, float distance = 6f, bool hunting = true, bool playerHeld = false, float age = 0.2f) =>
        VentEvidenceRules.Decide(VentCommitment.InDuct, kind, strength, age, distance, hunting, playerHeld, T);

    // ---------- Before the commit point ----------

    [Test]
    public void DirectSightAndAWatchedHidingEntryAlwaysCancelTheApproach()
    {
        foreach (var commit in new[] { VentCommitment.Approaching, VentCommitment.PreEntry })
        {
            Assert.AreEqual(VentEvidenceAction.Cancel, VentEvidenceRules.Decide(commit, VentEvidenceKind.DirectSight, 0f, 0f, 30f, false, false, T), "whatever the strength or distance");
            Assert.AreEqual(VentEvidenceAction.Cancel, VentEvidenceRules.Decide(commit, VentEvidenceKind.WitnessedHiding, 0.1f, 0f, 30f, false, false, T), "a locker or table it watched somebody enter");
        }
    }

    [Test]
    public void NearbySprintingCancelsButDistantSprintingIsOnlyKept()
    {
        Assert.AreEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerSprint, 0.5f, 10f));
        Assert.AreEqual(VentEvidenceAction.Store, Pre(VentEvidenceKind.PlayerSprint, 0.3f, 30f), "too far to give up the trip, but remembered");
    }

    [Test]
    public void WalkingCancelsOnlyWhenCloseAndAlreadyHunting()
    {
        Assert.AreEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerWalk, 0.5f, 5f, hunting: true));
        Assert.AreNotEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerWalk, 0.5f, 5f, hunting: false), "a calm patrol finishes its trip");
        Assert.AreNotEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerWalk, 0.5f, 20f, hunting: true), "too far");
    }

    [Test]
    public void WeakDistantAmbienceDoorsAndMachineryNeverCancel()
    {
        Assert.AreEqual(VentEvidenceAction.Ignore, Pre(VentEvidenceKind.Incidental, 0.9f, 2f), "doors, machinery and other incidental sound, however loud and close");
        Assert.AreEqual(VentEvidenceAction.Ignore, Pre(VentEvidenceKind.PlayerSprint, 0.05f, 3f), "hardly any strength at all");
        Assert.AreEqual(VentEvidenceAction.Ignore, Pre(VentEvidenceKind.PlayerBreath, 0.5f, 3f), "ordinary breathing");
        Assert.AreNotEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerHeavyBreath, 0.5f, 12f), "heavy breathing too far away");
    }

    [Test]
    public void CloseHeavyBreathingAndAStrongPlayerImpactCancel()
    {
        Assert.AreEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerHeavyBreath, 0.6f, 2.5f));
        Assert.AreEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerImpact, 0.7f, 12f));
        Assert.AreNotEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PlayerImpact, 0.3f, 3f), "a weak impact");
    }

    [Test]
    public void AFreshPursuedTrailOutranksANoisemaker()
    {
        Assert.AreEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.PursuedTrail, 0.3f, 25f), "the pursued player's trail");
        Assert.AreEqual(VentEvidenceAction.Ignore, Pre(VentEvidenceKind.Decoy, 0.9f, 5f, playerHeld: true), "a noisemaker never overrides a held player trail");
        Assert.AreEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.Decoy, 0.8f, 5f, playerHeld: false), "a strong one may influence an idle creature");
        Assert.AreNotEqual(VentEvidenceAction.Cancel, Pre(VentEvidenceKind.Decoy, 0.3f, 5f, playerHeld: false), "a weak one does not");
    }

    [Test]
    public void OldEvidenceIsIgnored()
    {
        Assert.AreEqual(VentEvidenceAction.Ignore, Pre(VentEvidenceKind.PlayerSprint, 0.9f, 3f, age: 5f));
        Assert.AreEqual(VentEvidenceAction.Ignore, VentEvidenceRules.Decide(VentCommitment.InDuct, VentEvidenceKind.PursuedTrail, 0.9f, 5f, 3f, true, true, T), "expired evidence reroutes nothing");
    }

    [Test]
    public void SpammingWeakNoisesCanNeverCancelATrip()
    {
        int cancels = 0;
        for (int i = 0; i < 2000; i++)
            foreach (var kind in new[] { VentEvidenceKind.Incidental, VentEvidenceKind.PlayerBreath, VentEvidenceKind.PlayerWalk })
                if (Pre(kind, 0.12f, 3f, hunting: true, age: i % 2 * 0.1f) == VentEvidenceAction.Cancel) cancels++;
        Assert.AreEqual(0, cancels);
    }

    // ---------- The attempt: cancel, retry, stale callbacks ----------

    static VentAttempt Approaching(double now = 10)
    {
        var a = new VentAttempt();
        Assert.IsTrue(a.Begin(3, 5, now));
        return a;
    }

    [Test]
    public void CancellingBeforeTheCommitPointReleasesTheEntranceAndExitAndNeverEntersTheDuct()
    {
        var a = Approaching();
        a.SetPhase(VentPhase.Entering);
        Assert.IsTrue(a.Interruptible);
        Assert.IsTrue(a.Cancel(11, 6f));
        Assert.IsFalse(a.Active);
        Assert.AreEqual((-1, -1), (a.Entry, a.Exit), "nothing stays reserved");
        Assert.AreEqual(VentPhase.None, a.Phase);
        Assert.IsFalse(a.Committed);
        Assert.AreEqual(1, a.Cancelled);
        Assert.IsFalse(a.Commit(), "it can never go on to commit");
    }

    [Test]
    public void ACancelledAttemptStartsTheRetryCooldown()
    {
        var a = Approaching(10);
        a.Cancel(10.5, 6f);
        Assert.IsFalse(a.MayBegin(12), "not straight away");
        Assert.IsFalse(a.Begin(3, 5, 12), "the same vent is not picked again at once");
        Assert.IsTrue(a.MayBegin(16.6), "after the cooldown it may try again");
        Assert.IsTrue(a.Begin(3, 5, 16.6));
    }

    [Test]
    public void ALateCallbackCannotRestartACancelledTrip()
    {
        var a = Approaching();
        a.SetPhase(VentPhase.Entering);
        a.Cancel(11, 6f);
        // The wind-up timer of the old attempt fires after the cancel.
        Assert.IsFalse(a.Commit());
        a.SetPhase(VentPhase.Entering); // even a stray phase write does not revive it
        Assert.IsFalse(a.Commit(), "no active attempt");
        Assert.IsFalse(a.Active);
    }

    [Test]
    public void AfterTheCommitPointItCanOnlyReroute()
    {
        var a = Approaching();
        a.SetPhase(VentPhase.Entering);
        Assert.IsTrue(a.Commit());
        a.SetPhase(VentPhase.Travelling);
        Assert.AreEqual(VentCommitment.InDuct, a.Commitment);
        Assert.IsFalse(a.Interruptible);
        Assert.IsFalse(a.Cancel(12, 6f), "it cannot be taken back out into the room");
        Assert.IsTrue(a.Active);
        Assert.AreEqual(VentEvidenceAction.Reroute, InDuct(VentEvidenceKind.PlayerSprint, 0.5f, 10f));
        Assert.AreEqual(VentEvidenceAction.Reroute, InDuct(VentEvidenceKind.PursuedTrail, 0.3f, 25f));
    }

    [Test]
    public void ACommittedTripCanChangeItsExitAFewTimesAtMost()
    {
        var a = Approaching();
        a.SetPhase(VentPhase.Entering);
        a.Commit();
        Assert.IsFalse(a.Reroute(5, 2), "same exit: nothing changes");
        Assert.IsTrue(a.Reroute(2, 2));
        Assert.AreEqual(2, a.Exit);
        Assert.IsTrue(a.Reroute(4, 2));
        Assert.IsFalse(a.Reroute(1, 2), "bounded: no endless changes of mind");
        Assert.AreEqual(4, a.Exit);
        var uncommitted = Approaching();
        Assert.IsFalse(uncommitted.Reroute(2, 2), "a trip that has not committed is cancelled, not re-routed");
    }

    [Test]
    public void ADecoyWhilePlayerEvidenceIsHeldOnlyGetsStoredInTheDuct()
    {
        Assert.AreEqual(VentEvidenceAction.Store, InDuct(VentEvidenceKind.Decoy, 0.9f, 5f, playerHeld: true));
        Assert.AreEqual(VentEvidenceAction.Reroute, InDuct(VentEvidenceKind.Decoy, 0.9f, 5f, playerHeld: false));
    }

    [Test]
    public void ComingOutFinishesBeforeAnyGroundReactionAndOnlyStoresEvidence()
    {
        var a = Approaching();
        a.SetPhase(VentPhase.Entering);
        a.Commit();
        a.SetPhase(VentPhase.Exiting);
        Assert.AreEqual(VentCommitment.Emerging, a.Commitment);
        Assert.IsFalse(a.Cancel(20, 6f), "no snapping back into the duct");
        foreach (var kind in new[] { VentEvidenceKind.PlayerSprint, VentEvidenceKind.PursuedTrail, VentEvidenceKind.Decoy, VentEvidenceKind.PlayerImpact })
            Assert.AreEqual(VentEvidenceAction.Store, VentEvidenceRules.Decide(VentCommitment.Emerging, kind, 0.8f, 0.1f, 3f, true, false, T), $"{kind} is kept for after");
        a.Finish();
        Assert.IsFalse(a.Active);
        Assert.IsTrue(a.MayBegin(20), "a completed trip has no retry cooldown (the ordinary cadence applies)");
        Assert.AreEqual(0, a.Cancelled, "a completed trip is not a cancelled one");
    }

    [Test]
    public void ARoundResetClearsTheAttemptTheRetryCooldownAndHeldEvidence()
    {
        var a = Approaching();
        a.Cancel(11, 600f);
        Assert.IsFalse(a.MayBegin(12));
        a.Reset();
        Assert.IsTrue(a.MayBegin(0), "the cooldown does not survive the reset");
        Assert.AreEqual(0, a.Cancelled);
        var held = new PendingEvidence<int>();
        held.Offer(1, 0.5f, 10, 10, 10);
        held.Clear();
        Assert.IsFalse(held.Has);
    }

    [Test]
    public void ExpiredHeldEvidenceReroutesNothing()
    {
        var held = new PendingEvidence<int>();
        held.Offer(7, 0.8f, 10, 10, 10);
        Assert.IsFalse(held.TryTake(25, 10, out _), "older than the maximum age");
        held.Offer(7, 0.8f, 30, 30, 10);
        Assert.IsTrue(held.TryTake(33, 10, out int v));
        Assert.AreEqual(7, v);
    }

    [Test]
    public void EvidenceFromPlayersWhoLeftPlayIsDiscarded()
    {
        Assert.IsFalse(VentEvidenceRules.Usable(true, false), "dead, escaped or disconnected");
        Assert.IsTrue(VentEvidenceRules.Usable(true, true));
        Assert.IsTrue(VentEvidenceRules.Usable(false, false), "a sound that was not a player's");
    }

    // ---------- Choosing the exit ----------

    // Entrances 0-2 around junction 3: 0-3 is 10 m, 1-3 is 10 m, 2-3 is 40 m.
    static VentGraph Graph()
    {
        var g = new VentGraph(4);
        g.AddEdge(0, 3, 10f);
        g.AddEdge(1, 3, 10f);
        g.AddEdge(2, 3, 40f);
        return g;
    }

    [Test]
    public void ARerouteGoesToTheMateriallyBetterExit()
    {
        var g = Graph();
        // Walking from each exit to the evidence: exit 0 is far (60 m), exit 1 is near (6 m), exit 2 is near (5 m) but 40 m away through the duct.
        var metres = new float[] { 60f, 6f, 5f, -1f };
        var penalty = new float[] { 0f, 0f, 0f, 0f };
        Assert.AreEqual(1, VentRules.BestExitFrom(g, 3, metres, penalty, 3f, 5f, null));
    }

    [Test]
    public void TheEntranceJustUsedIsOnlyChosenWhenItIsGenuinelyBest()
    {
        var g = Graph();
        float[] metres = { 6f, 12f, -1f, -1f }; // exit 0 (where it went in) is the shorter walk to the evidence, exit 1 a little longer
        float[] none = { 0f, 0f, 0f, 0f };
        Assert.AreEqual(0, VentRules.BestExitFrom(g, 3, metres, none, 3f, 5f, null), "without a penalty the shorter ground walk wins");
        float[] returnPenalty = { 8f, 0f, 0f, 0f };
        Assert.AreEqual(1, VentRules.BestExitFrom(g, 3, metres, returnPenalty, 3f, 5f, null), "with the return penalty it does not loop back");
        float[] farOther = { 6f, 90f, -1f, -1f };
        Assert.AreEqual(0, VentRules.BestExitFrom(g, 3, farOther, returnPenalty, 3f, 5f, null), "but it may return when that is clearly the best way");
    }

    [Test]
    public void AnUnreachableOrUnsafeExitIsNeverChosen()
    {
        var g = Graph();
        float[] metres = { 5f, 6f, -1f, -1f };
        float[] penalty = { float.PositiveInfinity, 0f, 0f, 0f }; // a player stands at exit 0
        Assert.AreEqual(1, VentRules.BestExitFrom(g, 3, metres, penalty, 3f, 5f, null));
        float[] none = { -1f, -1f, -1f, -1f };
        Assert.AreEqual(-1, VentRules.BestExitFrom(g, 3, none, new float[4], 3f, 5f, null));
    }

    [Test]
    public void TheCommitmentLevelsFollowThePhases()
    {
        Assert.AreEqual(VentCommitment.None, VentEvidenceRules.CommitmentOf(VentPhase.None, false));
        Assert.AreEqual(VentCommitment.Approaching, VentEvidenceRules.CommitmentOf(VentPhase.Approaching, false));
        Assert.AreEqual(VentCommitment.PreEntry, VentEvidenceRules.CommitmentOf(VentPhase.Entering, false));
        Assert.AreEqual(VentCommitment.InDuct, VentEvidenceRules.CommitmentOf(VentPhase.Entering, true), "walking into the wall is committed");
        Assert.AreEqual(VentCommitment.InDuct, VentEvidenceRules.CommitmentOf(VentPhase.Travelling, true));
        Assert.AreEqual(VentCommitment.InDuct, VentEvidenceRules.CommitmentOf(VentPhase.Preparing, true), "waiting hidden at the exit can still change exit");
        Assert.AreEqual(VentCommitment.Emerging, VentEvidenceRules.CommitmentOf(VentPhase.Exiting, true));
    }
}
