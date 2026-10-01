using System.IO;
using NUnit.Framework;

// EditMode tests for the locker rules (pure logic). They do not verify the door, the camera, the physics, Netcode delivery, the creature's
// movement or any multiplayer behaviour; those need the runtime checklist.
public class LockerTests
{
    // ---------- Occupancy ----------

    [Test]
    public void AtMostOneOccupantAndTheSecondRequestFails()
    {
        var o = new LockerOccupancy();
        Assert.IsTrue(o.TryEnter(1));
        Assert.IsFalse(o.TryEnter(2), "a second request while occupied fails");
        Assert.AreEqual(1UL, o.Occupant);
        Assert.IsFalse(o.TryEnter(1), "even the occupant cannot enter twice");
    }

    [Test]
    public void TwoPlayersPressingAtOnceLeaveExactlyOneInside()
    {
        // The host handles the two requests one after the other, in whichever order they arrive.
        foreach (var order in new[] { new ulong[] { 1, 2 }, new ulong[] { 2, 1 } })
        {
            var o = new LockerOccupancy();
            int accepted = 0;
            foreach (ulong id in order) if (o.TryEnter(id)) accepted++;
            Assert.AreEqual(1, accepted);
            Assert.AreEqual(order[0], o.Occupant, "the first request wins");
        }
    }

    [Test]
    public void AStaleReleaseCannotEvictANewcomerAndAResetClearsEverything()
    {
        var o = new LockerOccupancy();
        o.TryEnter(1);
        Assert.IsTrue(o.Release(1));
        o.TryEnter(2);
        Assert.IsFalse(o.Release(1), "the old occupant's late release does nothing");
        Assert.AreEqual(2UL, o.Occupant);
        Assert.IsTrue(o.ForceRelease(), "disconnect, death, round reset");
        Assert.IsFalse(o.Occupied);
        Assert.IsFalse(o.ForceRelease(), "nothing left to release");
        Assert.IsFalse(o.TryEnter(0), "0 is never a player");
    }

    // ---------- Entry and exit validation ----------

    static EntryResult Entry(bool owner = true, bool alive = true, bool round = true, bool occupied = false, float distance = 1.5f, bool los = true,
        bool hidden = false, bool inspected = false, bool anchors = true, double now = 10, double next = 0) =>
        LockerRules.ValidateEntry(owner, alive, round, occupied, distance, 3.75f, los, hidden, inspected, anchors, now, next);

    [Test]
    public void AValidEntryIsAccepted() => Assert.AreEqual(EntryResult.Ok, Entry());

    [Test]
    public void EveryInvalidEntryIsRejectedForItsOwnReason()
    {
        Assert.AreEqual(EntryResult.NotOwner, Entry(owner: false));
        Assert.AreEqual(EntryResult.NotInPlay, Entry(alive: false), "dead, escaped or spectating");
        Assert.AreEqual(EntryResult.RoundNotActive, Entry(round: false));
        Assert.AreEqual(EntryResult.Occupied, Entry(occupied: true));
        Assert.AreEqual(EntryResult.TooFar, Entry(distance: 6f));
        Assert.AreEqual(EntryResult.TooFar, Entry(distance: float.NaN));
        Assert.AreEqual(EntryResult.NoLineOfSight, Entry(los: false));
        Assert.AreEqual(EntryResult.AlreadyHidden, Entry(hidden: true), "already hidden elsewhere");
        Assert.AreEqual(EntryResult.BadAnchors, Entry(anchors: false));
        Assert.AreEqual(EntryResult.TooSoon, Entry(now: 10, next: 11));
    }

    [Test]
    public void EnteringWhileTheCreatureInspectsIsRejected()
    {
        Assert.AreEqual(EntryResult.BeingInspected, Entry(inspected: true));
        Assert.IsNotEmpty(LockerRules.Explain(EntryResult.BeingInspected));
    }

    static ExitResult Exit(bool owner = true, bool hidden = true, bool held = false, bool clear = true, double now = 20, double entered = 10) =>
        LockerRules.ValidateExit(owner, hidden, held, clear, now, entered, 0.8f);

    [Test]
    public void ExitRulesRefuseAnObstructedExitAndADoorHeldOpen()
    {
        Assert.AreEqual(ExitResult.Ok, Exit());
        Assert.AreEqual(ExitResult.Blocked, Exit(clear: false), "another player or a solid outside");
        Assert.AreEqual(ExitResult.HeldOpen, Exit(held: true), "no walking out through the creature");
        Assert.AreEqual(ExitResult.NotHidden, Exit(hidden: false));
        Assert.AreEqual(ExitResult.NotOwner, Exit(owner: false));
        Assert.AreEqual(ExitResult.TooSoon, Exit(now: 10.3));
        Assert.IsNotEmpty(LockerRules.Explain(ExitResult.Blocked), "a refused exit gives feedback");
    }

    [Test]
    public void HiddenPlayersCannotMoveInteractOrThrowAndRegainThemAfterwards()
    {
        Assert.IsFalse(LockerRules.MayMove(true));
        Assert.IsFalse(LockerRules.MayInteract(true));
        Assert.IsFalse(LockerRules.MayThrow(true, true));
        Assert.IsTrue(LockerRules.MayMove(false), "after the exit the controller is whole again");
        Assert.IsTrue(LockerRules.MayInteract(false));
        Assert.IsTrue(LockerRules.MayThrow(true, false));
        Assert.IsFalse(LockerRules.MayThrow(false, false), "a dead player still cannot throw");
    }

    [Test]
    public void LookingWhileHiddenStaysWithinTheLimits()
    {
        Assert.AreEqual((35f, -25f), LockerRules.ClampLook(200f, -90f, 35f, 25f));
        Assert.AreEqual((-35f, 25f), LockerRules.ClampLook(-200f, 90f, 35f, 25f));
        Assert.AreEqual((10f, 5f), LockerRules.ClampLook(10f, 5f, 35f, 25f));
    }

    [Test]
    public void ALateJoinersFirstDoorValueMakesNoSound()
    {
        Assert.IsFalse(LockerRules.DoorSoundWanted(0f));
        Assert.IsFalse(LockerRules.DoorSoundWanted(0.3f));
        Assert.IsTrue(LockerRules.DoorSoundWanted(2f));
    }

    // ---------- Breath ----------

    static BreathModel Breath() => new(5f, 1.2f, 7f, 0.3f, 0.25f);

    static void Run(BreathModel b, float seconds)
    {
        for (float t = 0; t < seconds; t += 0.02f) b.Tick(0.02f);
    }

    [Test]
    public void HoldingDrainsAndReleaseStartsADelayedRecovery()
    {
        var b = Breath();
        Assert.IsTrue(b.TryHold(0));
        Run(b, 2f);
        Assert.AreEqual(0.6f, b.Level, 0.03f, "about 3 of 5 seconds left");
        b.Release(2);
        float after = b.Level;
        Run(b, 1.0f);
        Assert.AreEqual(after, b.Level, 1e-4f, "no recovery during the delay");
        Run(b, 3f);
        Assert.Greater(b.Level, after + 0.2f, "then it recovers");
        Run(b, 8f);
        Assert.AreEqual(1f, b.Level, 1e-3f, "about seven seconds to refill");
    }

    [Test]
    public void ExhaustionForcesTheReleaseAndOneGaspOnly()
    {
        var b = Breath();
        b.TryHold(0);
        Run(b, 6f);
        Assert.IsFalse(b.Holding, "released by force");
        Assert.AreEqual(0f, b.Level);
        Assert.IsTrue(b.ConsumeForcedRelease());
        Assert.IsFalse(b.ConsumeForcedRelease(), "one gasp per exhaustion");
        Run(b, 2f);
        Assert.IsFalse(b.ConsumeForcedRelease());
    }

    [Test]
    public void AHoldBeforeTheMinimumRecoveryIsRefused()
    {
        var b = Breath();
        b.TryHold(0);
        Run(b, 6f); // empty
        Assert.IsFalse(b.TryHold(6), "empty: refused");
        Run(b, 1.2f + 0.05f * 7f); // delay, then a little recovery (~5 %)
        Assert.IsFalse(b.TryHold(8), "below the minimum");
        Run(b, 3f);
        Assert.IsTrue(b.TryHold(11), "enough recovered");
    }

    [Test]
    public void SpammingHoldAndReleaseCannotHoldTheBreathIndefinitely()
    {
        var b = Breath();
        double now = 0;
        float held = 0f;
        for (int i = 0; i < 3000; i++) // 60 seconds of a client sending hold and release as fast as it likes
        {
            now += 0.02;
            if (!b.Holding) b.TryHold(now);
            else if (i % 7 == 0) b.Release(now);
            if (b.Holding) held += 0.02f;
            b.Tick(0.02f);
        }
        Assert.Less(held, 30f, "it cannot hold more than about half of the time, and never without limit");
        Assert.GreaterOrEqual(b.Level, 0f);
        Run(b, 20f);
        Assert.IsFalse(b.Holding && b.Level <= 0f);
    }

    [Test]
    public void RapidTransitionsAreIgnoredByTheGap()
    {
        var b = Breath();
        Assert.IsTrue(b.TryHold(0));
        b.Release(0.05);
        Assert.IsFalse(b.TryHold(0.1), "too soon after the last change");
        Assert.IsTrue(b.TryHold(0.5));
    }

    // ---------- Breathing noise ----------

    [Test]
    public void HoldingTheBreathSilencesOnlyTheBreathingNoise()
    {
        Assert.AreEqual(0f, HiddenBreathing.Range(true, false, 1f, 3f, 0.5f, 1.2f, true, true));
        Assert.AreEqual(0f, HiddenBreathing.Range(true, true, 1f, 3f, 0.5f, 1.2f, false, true), "even after a sprint, while it is held");
        // Footsteps and every other sound are other systems: nothing here can change them.
        Assert.Greater(HiddenBreathing.Range(false, false, 1f, 3f, 0.5f, 1.2f, false, true), 0f, "not holding: still a quiet breath");
    }

    [Test]
    public void HiddenBreathingIsQuietHeavyBreathingStaysAudibleAndFearRaisesTheQuietOne()
    {
        float Range(bool heavy, bool close, bool hidden = true) => HiddenBreathing.Range(false, heavy, 1f, 3f, 0.5f, 1.2f, close, hidden);
        Assert.AreEqual(0.5f, Range(false, false), "extremely quiet");
        Assert.AreEqual(3f, Range(true, false), "heavy breathing after a sprint is still more audible");
        Assert.AreEqual(1.2f, Range(false, true), "fear raises it a little when the creature is close");
        Assert.Less(Range(false, true), 1f * 1.5f);
        Assert.AreEqual(1f, Range(false, false, hidden: false), "outside a locker the normal rules apply");
        Assert.AreEqual(3f, Range(true, false, hidden: false));
    }

    [Test]
    public void TheGaspNeedsAForcedReleaseAndACloseCreature()
    {
        Assert.IsTrue(HiddenBreathing.Gasps(true, 5f, 10f));
        Assert.IsFalse(HiddenBreathing.Gasps(true, 20f, 10f), "nobody near: nothing to hear");
        Assert.IsFalse(HiddenBreathing.Gasps(false, 2f, 10f), "an ordinary release is silent");
    }

    // ---------- The creature's inspection ----------

    [Test]
    public void TheDoorIsClosedUntilTheCreatureOpensItAndClosedAgainAfterwards()
    {
        var i = new LockerInspection();
        i.Begin(1f, 0.6f, 2f, 0.5f);
        Assert.AreEqual(LockerStage.Windup, i.Stage);
        Assert.IsFalse(i.DoorShouldBeOpen, "closed while it winds up: nothing can be seen or caught");
        i.Tick(1.01f);
        Assert.AreEqual(LockerStage.Opening, i.Stage);
        Assert.IsTrue(i.DoorShouldBeOpen);
        Assert.IsFalse(i.Looking, "it has not looked in yet");
        i.Tick(0.6f);
        Assert.AreEqual(LockerStage.Looking, i.Stage);
        Assert.IsTrue(i.DoorShouldBeOpen && i.Looking, "open and looking: recognition is possible now");
        i.Tick(2f);
        Assert.AreEqual(LockerStage.Closing, i.Stage);
        Assert.IsFalse(i.DoorShouldBeOpen);
        i.Tick(0.6f);
        Assert.AreEqual(LockerStage.Done, i.Stage, "an empty locker is closed and the search carries on");
    }

    [Test]
    public void AnInspectionAlwaysEndsWithinItsTotalAndCanBeCancelled()
    {
        var i = new LockerInspection();
        i.Begin(0.3f, 0.6f, 2f, 0.5f);
        float t = 0;
        while (i.Active && t < 100) { i.Tick(0.016f); t += 0.016f; }
        Assert.AreEqual(LockerStage.Done, i.Stage);
        Assert.AreEqual(i.TotalSeconds, t, 0.05f, "bounded by the sum of its stages");
        i.Begin(1f, 1f, 1f, 1f);
        i.Cancel();
        Assert.IsFalse(i.Active);
        Assert.IsFalse(i.DoorShouldBeOpen, "an interrupted inspection leaves no door open");
    }

    [Test]
    public void ZeroLengthStagesAreSkipped()
    {
        var i = new LockerInspection();
        i.Begin(0f, 0f, 1f, 0f);
        Assert.AreEqual(LockerStage.Looking, i.Stage, "a witnessed entry goes straight to the door");
        i.Tick(1.1f);
        Assert.AreEqual(LockerStage.Done, i.Stage);
    }

    // ---------- Choosing lockers: by place only ----------

    [Test]
    public void SelectionScoreHasNoOccupancyInputAndIsTheSameForEveryLocker()
    {
        // The signature takes distances and a place flag only. The same place always scores the same, empty or not.
        float a = HidingChoice.Score(4f, 6f, true, false, 3f, 2f);
        float b = HidingChoice.Score(4f, 6f, true, false, 3f, 2f);
        Assert.AreEqual(a, b);
        Assert.AreEqual(HidingChoice.Score(4f, 6f, false, false, 3f, 2f), a, "an enclosed place scores like furniture unless a sound came from beside it");
    }

    [Test]
    public void ASoundBesideALockerMakesItABetterChoiceNotACertainOne()
    {
        float near = HidingChoice.Score(1.5f, 5f, true, true, 3f, 2f);
        float nearFurniture = HidingChoice.Score(1.5f, 5f, false, true, 3f, 2f);
        float far = HidingChoice.Score(8f, 5f, true, true, 3f, 2f);
        Assert.AreEqual(-2f, near - nearFurniture, 1e-4f, "a bounded bonus: it is better, not certain");
        Assert.AreEqual(HidingChoice.Score(8f, 5f, false, true, 3f, 2f), far, 1e-4f, "no bonus when the sound was elsewhere");
    }

    [Test]
    public void NothingInTheCreaturesCodeReadsWhoIsInsideALocker()
    {
        // The guarantee is structural: the occupant is exposed by HideLocker for networking and diagnostics only, and the creature's scripts
        // never use those members. Reading them would be a source change this test catches.
        string root = FindRoot();
        if (root == null) Assert.Ignore("project folder not found from here");
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Assets", "Scripts", "Creature"), "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "OccupantId", "IsOccupied", "occupant.Value", "LockerOccupancy" })
                Assert.IsFalse(text.Contains(forbidden), $"{Path.GetFileName(file)} must not use {forbidden}");
        }
    }

    static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), System.AppContext.BaseDirectory, "C:/Unity Projects/Alien" })
            for (var dir = start; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, "Assets", "Scripts", "Creature"))) return dir;
        return null;
    }
}
