using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the noisemaker rules (pure logic). They do not verify physics, Netcode delivery, audio, the HUD, how the creature moves
// or any multiplayer behaviour; those need the runtime checklist.
public class NoisemakerTests
{
    // ---------- Inventory and throw validation ----------

    [Test]
    public void ACapacityOfTwoRejectsTheThird()
    {
        Assert.IsTrue(NoisemakerRules.CanAdd(0, 2));
        Assert.IsTrue(NoisemakerRules.CanAdd(1, 2));
        Assert.IsFalse(NoisemakerRules.CanAdd(2, 2), "a full inventory leaves the pickup where it is");
    }

    [Test]
    public void TwoPlayersTakingTheSamePickupInOneTickOnlyOneGetsIt()
    {
        // The host handles requests one after the other; the pickup is hidden in the same step that the count rises.
        int countA = 0, countB = 0;
        bool available = true;
        bool Take(ref int count)
        {
            if (!available || !NoisemakerRules.CanAdd(count, 2)) return false;
            count++;
            available = false;
            return true;
        }
        bool a = Take(ref countA), b = Take(ref countB);
        Assert.IsTrue(a ^ b, "exactly one of them got it");
        Assert.AreEqual(1, countA + countB);
    }

    [Test]
    public void ThrowChecksRunInOrderAndAcceptAValidThrow()
    {
        Assert.AreEqual(ThrowResult.Ok, NoisemakerRules.ValidateThrow(true, true, true, 1, 10.0, 9.0));
        Assert.AreEqual(ThrowResult.NotOwner, NoisemakerRules.ValidateThrow(false, true, true, 1, 10.0, 9.0));
        Assert.AreEqual(ThrowResult.NotInPlay, NoisemakerRules.ValidateThrow(true, false, true, 1, 10.0, 9.0), "dead or escaped");
        Assert.AreEqual(ThrowResult.RoundNotActive, NoisemakerRules.ValidateThrow(true, true, false, 1, 10.0, 9.0), "lobby or round over");
        Assert.AreEqual(ThrowResult.NoneLeft, NoisemakerRules.ValidateThrow(true, true, true, 0, 10.0, 9.0), "nothing to throw");
        Assert.AreEqual(ThrowResult.Cooldown, NoisemakerRules.ValidateThrow(true, true, true, 1, 10.0, 10.5));
    }

    [Test]
    public void SpammingTheThrowIsLimitedByTheCooldown()
    {
        double next = 0;
        int thrown = 0, count = 100;
        for (int i = 0; i < 200; i++) // 200 requests in two seconds
        {
            double now = i * 0.01;
            if (NoisemakerRules.ValidateThrow(true, true, true, count, now, next) != ThrowResult.Ok) continue;
            thrown++;
            count--;
            next = now + 1.2;
        }
        Assert.AreEqual(2, thrown);
    }

    [Test]
    public void InvalidAimsAreRejected()
    {
        float x, y, z;
        Assert.IsFalse(NoisemakerRules.ConstrainAim(float.NaN, 0, 1, 0, 1, 25, -50, 60, out x, out y, out z));
        Assert.IsFalse(NoisemakerRules.ConstrainAim(0, float.PositiveInfinity, 1, 0, 1, 25, -50, 60, out x, out y, out z));
        Assert.IsFalse(NoisemakerRules.ConstrainAim(0, 0, 0, 0, 1, 25, -50, 60, out x, out y, out z), "no direction at all");
        Assert.IsFalse(NoisemakerRules.ConstrainAim(0, 0, 1, float.NaN, 1, 25, -50, 60, out x, out y, out z));
        Assert.IsFalse(NoisemakerRules.ConstrainAim(0, 0, 1, 0, 0, 25, -50, 60, out x, out y, out z), "the host knows no facing");
    }

    [Test]
    public void AimIsAUnitVectorNearTheFacingAndWithASanePitch()
    {
        Assert.IsTrue(NoisemakerRules.ConstrainAim(0, 0.2f, 1, 0, 1, 25, -50, 60, out float x, out float y, out float z));
        Assert.AreEqual(1f, x * x + y * y + z * z, 1e-4f);
        Assert.AreEqual(0f, x, 1e-4f, "straight ahead stays straight ahead");

        // Aiming straight BACKWARD (a client lying about its facing) is pulled round to within the allowed yaw of the real facing.
        Assert.IsTrue(NoisemakerRules.ConstrainAim(0, 0, -1, 0, 1, 25, -50, 60, out x, out y, out z));
        Assert.Greater(z, 0.85f, "it still goes forward");
        Assert.AreEqual(25f, System.Math.Abs(System.Math.Atan2(x, z) * 180.0 / System.Math.PI), 0.1, "at the edge of the allowed yaw");

        // Straight up and straight down are limited too.
        Assert.IsTrue(NoisemakerRules.ConstrainAim(0, 1, 0, 0, 1, 25, -50, 60, out x, out y, out z));
        Assert.AreEqual(System.Math.Sin(60 * System.Math.PI / 180), y, 1e-3);
        Assert.IsTrue(NoisemakerRules.ConstrainAim(0, -1, 0, 0, 1, 25, -50, 60, out x, out y, out z));
        Assert.AreEqual(System.Math.Sin(-50 * System.Math.PI / 180), y, 1e-3);
    }

    [Test]
    public void TheClientSuppliesOnlyADirection()
    {
        // The API takes no origin, speed or range: the host derives them. A huge aim vector is the same direction as a unit one.
        Assert.IsTrue(NoisemakerRules.ConstrainAim(0, 0, 1e6f, 0, 1, 25, -50, 60, out float x1, out float y1, out float z1));
        Assert.IsTrue(NoisemakerRules.ConstrainAim(0, 0, 1, 0, 1, 25, -50, 60, out float x2, out float y2, out float z2));
        Assert.AreEqual((x1, y1, z1), (x2, y2, z2));
    }

    // ---------- Impacts and the device's life ----------

    [Test]
    public void TinyAndRepeatedImpactsMakeNothing()
    {
        Assert.IsFalse(NoisemakerRules.AcceptImpact(10.0, double.NegativeInfinity, 0.35f, 0.4f, 1.5f), "too slow to matter");
        Assert.IsTrue(NoisemakerRules.AcceptImpact(10.0, double.NegativeInfinity, 0.35f, 3f, 1.5f));
        Assert.IsFalse(NoisemakerRules.AcceptImpact(10.1, 10.0, 0.35f, 3f, 1.5f), "a bounce right after the last");
        Assert.IsTrue(NoisemakerRules.AcceptImpact(10.4, 10.0, 0.35f, 3f, 1.5f));
        Assert.IsFalse(NoisemakerRules.AcceptImpact(10.0, double.NegativeInfinity, 0.35f, float.NaN, 1.5f));
        int events = 0;
        double last = double.NegativeInfinity;
        for (int i = 0; i < 100; i++) // a ball bouncing every frame at 60 Hz for 1.6 s
            if (NoisemakerRules.AcceptImpact(i / 60.0, last, 0.35f, 4f, 1.5f)) { events++; last = i / 60.0; }
        Assert.LessOrEqual(events, 5);
    }

    [Test]
    public void ImpactNoiseRangeIsBoundedAndGrowsWithSpeed()
    {
        Assert.AreEqual(0f, NoisemakerRules.ImpactRange(1f, 1.5f, 9f, 2f, 8f));
        Assert.AreEqual(2f, NoisemakerRules.ImpactRange(1.5f, 1.5f, 9f, 2f, 8f), 1e-4f);
        Assert.AreEqual(8f, NoisemakerRules.ImpactRange(500f, 1.5f, 9f, 2f, 8f), "never above the configured maximum");
        Assert.Less(NoisemakerRules.ImpactRange(3f, 1.5f, 9f, 2f, 8f), NoisemakerRules.ImpactRange(7f, 1.5f, 9f, 2f, 8f));
    }

    static NoisemakerTimeline Timeline() => new(0.8f, 0.4f, 7f, 1f, 1.5f, 6f);

    [Test]
    public void ADeviceGoesThroughItsPhasesInOrder()
    {
        var t = Timeline();
        t.Begin(0);
        Assert.AreEqual(NoisemakerPhase.Thrown, t.Phase);
        t.Tick(0.5);
        Assert.AreEqual(NoisemakerPhase.Thrown, t.Phase, "still in the air");
        t.OnImpact(1.0);
        Assert.AreEqual(NoisemakerPhase.Settling, t.Phase);
        t.Tick(1.5);
        Assert.AreEqual(NoisemakerPhase.Settling, t.Phase, "not armed before the arming delay");
        t.Tick(1.85);
        Assert.AreEqual(NoisemakerPhase.Armed, t.Phase);
        t.Tick(2.3);
        Assert.AreEqual(NoisemakerPhase.Pulsing, t.Phase);
        t.Tick(9.4);
        Assert.AreEqual(NoisemakerPhase.Spent, t.Phase);
        Assert.IsFalse(t.Expired);
        t.Tick(11.0);
        Assert.IsTrue(t.Expired);
    }

    [Test]
    public void PulsesStayWithinTheirBoundsAndNeverBurst()
    {
        var t = Timeline();
        t.Begin(0);
        t.OnImpact(0.5);
        int pulses = 0, perTickMax = 0;
        double first = -1, previous = -1, minGap = double.MaxValue;
        for (double now = 0.5; now < 14; now += 0.016)
        {
            int before = pulses;
            if (t.Tick(now)) { pulses++; if (first < 0) first = now; if (previous >= 0) minGap = System.Math.Min(minGap, now - previous); previous = now; }
            perTickMax = System.Math.Max(perTickMax, pulses - before);
        }
        Assert.That(pulses, Is.InRange(6, 8), "about seven pulses");
        Assert.AreEqual(1, perTickMax, "at most one logical noise per tick");
        Assert.GreaterOrEqual(minGap, 0.95, "about one a second");
        Assert.That(first, Is.InRange(1.7, 1.9), "the first pulse follows the arming");
        Assert.AreEqual(pulses, t.PulsesFired);
    }

    [Test]
    public void AHitchNeverTurnsIntoABurstOfPulses()
    {
        var t = Timeline();
        t.Begin(0);
        t.OnImpact(0);
        for (double now = 0; now < 2.5; now += 0.05) t.Tick(now);
        int pulses = 0;
        for (double now = 2.5; now < 2.5 + 5.0; now += 5.0) if (t.Tick(now)) pulses++; // one tick, five seconds later
        Assert.LessOrEqual(pulses, 1);
    }

    [Test]
    public void ADeviceThatNeverLandsStillArmsAndEnds()
    {
        var t = Timeline();
        t.Begin(0);
        for (double now = 0; now < 30; now += 0.1) t.Tick(now);
        Assert.IsTrue(t.Expired, "stuck in a vent or lost: it still runs out");
    }

    [Test]
    public void LaterImpactsDoNotRestartTheArming()
    {
        var t = Timeline();
        t.Begin(0);
        t.OnImpact(1.0);
        t.OnImpact(1.6);
        t.Tick(1.85);
        Assert.AreEqual(NoisemakerPhase.Armed, t.Phase, "armed 0.8 s after the FIRST impact");
    }

    [Test]
    public void SpotSelectionIsDeterministic()
    {
        for (int seed = 1; seed < 20; seed++)
            Assert.AreEqual(NoisemakerRules.PickSpot(new System.Random(seed), 3), NoisemakerRules.PickSpot(new System.Random(seed), 3));
        var seen = new HashSet<int>();
        for (int seed = 1; seed < 200; seed++) seen.Add(NoisemakerRules.PickSpot(new System.Random(seed), 3));
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, seen, "all spots can come up");
        Assert.AreEqual(-1, NoisemakerRules.PickSpot(new System.Random(1), 0));
    }

    // ---------- What the creature makes of a decoy ----------

    const float Margin = 1.25f;

    static bool Accept(float score, bool trail, float strength, bool same = false, bool recognised = false, bool interest = true, bool cooldown = true) =>
        DecoyRules.Accept(recognised, interest, score, trail, same, strength, Margin, cooldown);

    [Test]
    public void SightAndAFreshPlayerTrailOutrankADecoy()
    {
        // Direct sight, a witnessed hiding place and a followed footstep trail are all evidence from a player with weight left.
        Assert.IsFalse(Accept(1f, true, 1f), "even a point-blank decoy cannot beat a fresh trail");
        Assert.IsFalse(Accept(0.3f, true, 1f), "a still-weighted trail is never replaced");
        Assert.IsFalse(Accept(0.01f, true, 1f, same: true));
    }

    [Test]
    public void PatrolAndStaleEvidenceAcceptADecoy()
    {
        Assert.IsTrue(Accept(0f, false, 0.4f), "nothing to protect: patrol, calm or searching with no evidence");
        Assert.IsTrue(Accept(0f, true, 0.4f), "the player trail has faded away completely");
    }

    [Test]
    public void ADecoyMustBeClearlyStrongerThanWeakEvidence()
    {
        Assert.IsFalse(Accept(0.5f, false, 0.55f), "barely louder: not worth switching");
        Assert.IsTrue(Accept(0.3f, false, 0.8f), "much stronger than a weak environmental sound");
        Assert.IsFalse(Accept(0.3f, false, 0.8f, cooldown: false), "the switch cooldown still applies");
    }

    [Test]
    public void TheDeviceItIsHeadingForKeepsRefreshingTheEvidence()
    {
        Assert.IsTrue(Accept(0.6f, false, 0.2f, same: true), "repeated pulses sustain the approach even when quieter");
        Assert.IsFalse(Accept(0.6f, false, 0.2f, same: false));
    }

    [Test]
    public void ARecognisedDeviceIsIgnoredButNewOnesStayValid()
    {
        var memory = new DecoyMemory();
        Assert.IsFalse(memory.Examine(5, 1f, 2.5f));
        Assert.IsFalse(memory.IsIgnored(5), "not yet");
        Assert.IsFalse(memory.Examine(5, 1f, 2.5f));
        Assert.IsTrue(memory.Examine(5, 1f, 2.5f), "examined long enough");
        Assert.IsTrue(memory.IsIgnored(5));
        Assert.IsFalse(Accept(0f, false, 1f, recognised: memory.IsIgnored(5)), "its later pulses are ignored");
        Assert.IsFalse(memory.IsIgnored(6));
        Assert.IsTrue(Accept(0f, false, 1f, recognised: memory.IsIgnored(6)), "a second device is a fresh decoy");
    }

    [Test]
    public void RecognisingOneDeviceTeachesNothingAboutAnother()
    {
        var memory = new DecoyMemory();
        Assert.IsTrue(memory.Examine(1, 3f, 2.5f));
        Assert.IsFalse(memory.Examine(2, 1f, 2.5f), "device 2 starts its own examination from zero");
        Assert.AreEqual(1, memory.IgnoredCount);
    }

    [Test]
    public void DecoysCannotHoldTheCreatureForever()
    {
        var memory = new DecoyMemory();
        double now = 0;
        bool bored = false;
        for (int i = 0; i < 1000 && !bored; i++) // devices keep coming; it keeps following them
        {
            now += 0.1;
            memory.Spend(0.1f, now, 30f, 45f);
            bored = !memory.InterestLeft(now);
        }
        Assert.IsTrue(bored, "the budget runs out");
        Assert.AreEqual(30.0, now, 0.2);
        Assert.IsFalse(Accept(0f, false, 1f, interest: memory.InterestLeft(now)));
        Assert.IsTrue(memory.InterestLeft(now + 46), "and comes back after the quiet period");
    }

    [Test]
    public void TwoDevicesCannotFlipTheCreatureBackAndForth()
    {
        // It is heading for device 1 (score 0.6). Device 2 pulses at similar strength: it must not win.
        double now = 0;
        int switches = 0;
        for (int i = 0; i < 20; i++)
        {
            now += 0.5;
            bool cooldownOver = i % 3 == 0;
            if (Accept(0.6f, false, 0.65f, same: false, cooldown: cooldownOver)) switches++;
        }
        Assert.AreEqual(0, switches, "a similar strength never beats the margin");
    }

    [Test]
    public void ForgettingDevicesThatNoLongerExist()
    {
        var memory = new DecoyMemory();
        memory.Examine(1, 5f, 2.5f);
        memory.Examine(2, 5f, 2.5f);
        memory.Examine(3, 1f, 2.5f);
        memory.Prune(t => t == 2);
        Assert.IsFalse(memory.IsIgnored(1));
        Assert.IsTrue(memory.IsIgnored(2));
        Assert.AreEqual(1, memory.IgnoredCount);
    }

    [Test]
    public void ARoundResetClearsEverythingTheCreatureLearned()
    {
        var memory = new DecoyMemory();
        memory.Examine(1, 5f, 2.5f);
        memory.Spend(40f, 100, 30f, 45f);
        Assert.IsFalse(memory.InterestLeft(101));
        memory.Clear();
        Assert.AreEqual(0, memory.IgnoredCount);
        Assert.IsTrue(memory.InterestLeft(101));
        Assert.IsFalse(memory.IsIgnored(1));
    }

    // ---------- A throw is a transaction ----------

    [Test]
    public void AFailedSpawnDoesNotConsumeANoisemaker()
    {
        int count = 2;
        var result = NoisemakerRules.Commit(ref count, () => false);
        Assert.AreEqual(ThrowResult.SpawnFailed, result);
        Assert.AreEqual(2, count, "nothing was taken");
    }

    [Test]
    public void ASuccessfulSpawnConsumesExactlyOne()
    {
        int count = 2, spawned = 0;
        var result = NoisemakerRules.Commit(ref count, () => { spawned++; return true; });
        Assert.AreEqual(ThrowResult.Ok, result);
        Assert.AreEqual(1, count);
        Assert.AreEqual(1, spawned);
    }

    [Test]
    public void NothingIsSpawnedWhenNoneAreLeftOrThereIsNoSpawner()
    {
        int count = 0, spawned = 0;
        Assert.AreEqual(ThrowResult.NoneLeft, NoisemakerRules.Commit(ref count, () => { spawned++; return true; }));
        Assert.AreEqual(0, spawned, "no spawn is even attempted");
        count = 1;
        Assert.AreEqual(ThrowResult.SpawnFailed, NoisemakerRules.Commit(ref count, null));
        Assert.AreEqual(1, count);
    }

    [Test]
    public void ARetryAfterAFailureCostsOnlyWhatItSpawns()
    {
        int count = 1;
        bool prefabFixed = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt == 2) prefabFixed = true;
            NoisemakerRules.Commit(ref count, () => prefabFixed);
        }
        Assert.AreEqual(0, count, "two failures cost nothing, the success cost one");
    }

    // ---------- Placement ----------

    static PlacementBox Box(string name, float cx, float cy, float cz, float hx, float hy, float hz, bool trigger = false, bool own = false) =>
        new() { name = name, kind = "BoxCollider", cx = cx, cy = cy, cz = cz, hx = hx, hy = hy, hz = hz, trigger = trigger, own = own };

    // A pickup 0.17 x 0.07 x 0.17 resting 5 mm above a floor whose top is y = 0 (the spot is at y = 0.04, so it spans 0.005..0.075).
    static PlacementResult Place(params PlacementBox[] boxes) =>
        NoisemakerPlacement.Evaluate(5f, 5f, 0.085f, 0.085f, 0.005f, 0.075f, 0.01f, 0.15f, boxes);

    [Test]
    public void AFloorSupportedPickupIsAccepted()
    {
        var r = Place(Box("Ship Geometry/Floor", 0, -0.1f, 0, 35, 0.1f, 25));
        Assert.IsTrue(r.Valid);
        Assert.AreEqual("Ship Geometry/Floor", r.support);
    }

    [Test]
    public void TheFloorTouchingThePickupIsNotAnObstruction()
    {
        // A floor slab whose top is a few millimetres into the pickup's skin must not count as blocking.
        var r = Place(Box("Floor", 0, -0.1f, 0, 35, 0.1f + 0.008f, 25));
        Assert.IsEmpty(r.blockers);
        Assert.IsTrue(r.supported);
    }

    [Test]
    public void TriggersAndTheOwnHierarchyAreIgnored()
    {
        var r = Place(
            Box("Floor", 0, -0.1f, 0, 35, 0.1f, 25),
            Box("Rooms/Room Engine", 5, 2, 5, 4, 2, 4, trigger: true),
            Box("Acoustic Zones/Acoustic Engine", 5, 2.2f, 5, 3, 2.2f, 3, trigger: true),
            Box("Noisemakers/Noisemaker Pickup 0 Engine", 5, 0.04f, 5, 0.085f, 0.035f, 0.085f, own: true));
        Assert.IsTrue(r.Valid);
        Assert.AreEqual(3, r.ignored.Count);
    }

    [Test]
    public void AWallThroughThePickupIsRejectedAndNamed()
    {
        var r = Place(Box("Floor", 0, -0.1f, 0, 35, 0.1f, 25), Box("Ship Geometry/Crate", 5.2f, 0.8f, 5, 0.8f, 0.8f, 0.8f));
        Assert.AreEqual(1, r.blockers.Count);
        Assert.AreEqual("Ship Geometry/Crate", r.blockers[0].name);
        StringAssert.Contains("Ship Geometry/Crate", NoisemakerPlacement.Describe(r.blockers[0]));
        StringAssert.Contains("BoxCollider", NoisemakerPlacement.Describe(r.blockers[0]));
    }

    [Test]
    public void ASpotWithNoFloorUnderItIsRejected()
    {
        Assert.IsFalse(Place().supported);
        Assert.IsFalse(Place(Box("Far floor", 0, -1f, 0, 35, 0.1f, 25)).supported, "a floor a metre down is not support");
        Assert.IsFalse(Place(Box("Floor elsewhere", 30, -0.1f, 30, 2, 0.1f, 2)).supported, "a floor that is not under the pickup");
    }

    [Test]
    public void ANeighbouringWallThatDoesNotTouchThePickupIsFine()
    {
        var r = Place(Box("Floor", 0, -0.1f, 0, 35, 0.1f, 25), Box("Wall", 5.6f, 2, 5, 0.2f, 2, 5));
        Assert.IsTrue(r.Valid, "a wall 0.3 m from the pickup's edge is clear");
    }

    [Test]
    public void TheShipsAuthoredSpotsPassOnTheirRealGeometry()
    {
        // The Ship's floor is a slab with its top at y = 0, and the nearest solid object to each of the 15 candidate spots is over half a metre
        // away (measured from the saved scene's colliders). The only collider that ever overlapped a spot was the pickup's own: the spots
        // that failed the first validation (each pickup's first spot) were false positives, and none needed to move.
        var floor = Box("Ship Geometry/Floor", 0, -0.1f, 0, 35, 0.1f, 27);
        foreach (var (x, z) in new[] { (-18f, 18f), (-26f, 2f), (18f, 2f), (12f, 20f), (18f, -18f), (-14f, 16f), (-16f, -2f), (12f, -2f), (16f, 16f), (26f, -16f) })
        {
            var own = Box("Noisemakers/Noisemaker Pickup", x, 0.04f, z, 0.085f, 0.035f, 0.085f, own: true);
            var r = NoisemakerPlacement.Evaluate(x, z, 0.085f, 0.085f, 0.005f, 0.075f, 0.01f, 0.15f, new[] { floor, own });
            Assert.IsTrue(r.Valid, $"({x}, {z})");
        }
    }

    // ---------- Sound: stand-ins, and each event heard once ----------

    [Test]
    public void EveryStandInSoundIsMadeCorrectlyAndTheSameEveryTime()
    {
        foreach (NoisemakerSound kind in System.Enum.GetValues(typeof(NoisemakerSound)))
        {
            var a = NoisemakerSynth.Generate(kind, 44100);
            var b = NoisemakerSynth.Generate(kind, 44100);
            Assert.Greater(a.Length, 1000, $"{kind} has some length");
            CollectionAssert.AreEqual(a, b, $"{kind} is deterministic");
            float peak = 0f;
            foreach (float v in a) { Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v)); peak = System.Math.Max(peak, System.Math.Abs(v)); }
            Assert.AreEqual(NoisemakerSynth.Peak(kind), peak, 1e-3f, $"{kind} is normalised to its stated peak");
            Assert.AreEqual(0f, a[0], 1e-3f, "starts from silence (no click)");
            Assert.AreEqual(0f, a[a.Length - 1], 1e-3f, "ends in silence");
        }
    }

    [Test]
    public void ThePulseCarriesAndThePickupIsQuiet()
    {
        Assert.Greater(NoisemakerSynth.Peak(NoisemakerSound.Pulse), NoisemakerSynth.Peak(NoisemakerSound.Throw));
        Assert.Greater(NoisemakerSynth.Peak(NoisemakerSound.Throw), NoisemakerSynth.Peak(NoisemakerSound.Pickup));
        Assert.LessOrEqual(NoisemakerSynth.Peak(NoisemakerSound.Impact), 0.9f, "nothing is near full scale");
    }

    [Test]
    public void EachPulseSequenceNumberIsPlayedOnceAndNeverAgain()
    {
        var gate = new NoisemakerEventGate();
        gate.Begin(0);
        Assert.IsTrue(gate.Direct(1));
        Assert.IsFalse(gate.Direct(1), "the same sequence is never played twice");
        Assert.IsTrue(gate.Direct(2));
        Assert.IsFalse(gate.Direct(1), "an older one never plays");
    }

    [Test]
    public void ALateJoinerStartsFromTheCurrentCounterAndReplaysNothing()
    {
        var gate = new NoisemakerEventGate();
        gate.Begin(5); // joined while the device was on its fifth pulse
        Assert.IsFalse(gate.Replicated(5, false), "the pulse that was already current is history");
        Assert.IsFalse(gate.Replicated(3, false));
        Assert.IsTrue(gate.Replicated(6, false), "the next real pulse is heard");
    }

    [Test]
    public void HostAndRemotePeersEachHearAnEventExactlyOnce()
    {
        var host = new NoisemakerEventGate();
        var remote = new NoisemakerEventGate();
        host.Begin(0);
        remote.Begin(0);
        int hostPlays = 0, remotePlays = 0;
        for (int seq = 1; seq <= 7; seq++)
        {
            if (host.Direct(seq)) hostPlays++;                  // the host plays it when it makes it...
            if (host.Replicated(seq, true)) hostPlays++;        // ...and its own replicated callback must not play it again
            if (remote.Replicated(seq, false)) remotePlays++;   // a remote peer plays it when the counter arrives
            if (remote.Replicated(seq, false)) remotePlays++;   // a repeated delivery changes nothing
        }
        Assert.AreEqual(7, hostPlays);
        Assert.AreEqual(7, remotePlays);
    }

    [Test]
    public void EveryActualPulseMakesOneLogicalNoiseAndOnePlaybackRequest()
    {
        var timeline = new NoisemakerTimeline(0.8f, 0.4f, 7f, 1f, 1.5f, 6f);
        var gate = new NoisemakerEventGate();
        timeline.Begin(0);
        timeline.OnImpact(0.2);
        gate.Begin(0);
        int noises = 0, requests = 0, pulses = 0;
        for (double now = 0.2; now < 14; now += 0.016)
        {
            if (!timeline.Tick(now)) continue;
            noises++;                 // the host's NoiseSystem.Emit
            pulses++;                 // the replicated counter
            if (gate.Direct(pulses)) requests++;
        }
        Assert.That(noises, Is.InRange(6, 8));
        Assert.AreEqual(noises, requests);
        Assert.AreEqual(noises, timeline.PulsesFired);
    }

    [Test]
    public void AHaltedDeviceNeverPulsesAgain()
    {
        var timeline = new NoisemakerTimeline(0.8f, 0.4f, 7f, 1f, 1.5f, 6f);
        timeline.Begin(0);
        timeline.OnImpact(0);
        int pulses = 0;
        double now = 0;
        for (; now < 3.5; now += 0.016) if (timeline.Tick(now)) pulses++;
        Assert.Greater(pulses, 0);
        timeline.Halt(); // a round reset or a despawn
        for (; now < 20; now += 0.016) Assert.IsFalse(timeline.Tick(now));
        Assert.IsTrue(timeline.Expired);
    }

    [Test]
    public void ImpactStrengthTravelsWithItsSequenceNumber()
    {
        for (int seq = 1; seq < 300; seq += 7)
            foreach (byte level in new byte[] { 0, 1, 128, 255 })
            {
                int packed = NoisemakerRules.PackImpact(seq, level);
                Assert.AreEqual(seq, NoisemakerRules.ImpactSequence(packed));
                Assert.AreEqual(level, NoisemakerRules.ImpactLevel(packed));
            }
        Assert.Greater(NoisemakerRules.PackImpact(2, 0), NoisemakerRules.PackImpact(1, 255), "a later impact always has the larger value");
    }
}
