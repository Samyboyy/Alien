using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the acoustic polish and motion-tracker rules (pure logic). They do not verify what anything sounds like, the Unity audio
// filters, the HUD, Netcode delivery or multiplayer behaviour.
public class AcousticTrackerTests
{
    static readonly AcousticSpace[] Spaces =
        { AcousticSpace.SmallRoom, AcousticSpace.Corridor, AcousticSpace.LargeMachinery, AcousticSpace.LargeOpen, AcousticSpace.Compartment, AcousticSpace.Crawlspace, AcousticSpace.Duct };

    // ---------- Acoustics ----------

    // AudioCategory ids (ShipAudio.cs), as AcousticRules takes them.
    const int Creature = 1, OwnBody = 3, Ambience = 4, Internal = 5, Ui = 6;

    [Test]
    public void InternalAmbienceAndUiSoundsStayDry()
    {
        Assert.AreEqual(0f, AcousticRules.CategoryReflectionWeight(Ambience, 0.35f));
        Assert.AreEqual(0f, AcousticRules.CategoryReflectionWeight(Internal, 0.35f));
        Assert.AreEqual(0f, AcousticRules.CategoryReflectionWeight(Ui, 0.35f));
        Assert.AreEqual(1f, AcousticRules.CategoryReflectionWeight(Creature, 0.35f));
        Assert.AreEqual(0.35f, AcousticRules.CategoryReflectionWeight(OwnBody, 0.35f), 1e-5f, "your own body is reflected lightly");
    }

    [Test]
    public void CloseSoundsGetAudibleReflectionsInEveryRealSpace()
    {
        Assert.AreEqual(1f, AcousticRules.ReflectionDistanceWeight(0.5f, 4f, 20f, 0.35f));
        Assert.AreEqual(0, AcousticRules.WeightToMillibels(1f), "full weight up close: no reduction");
        foreach (var space in Spaces)
        {
            var s = AcousticRules.Bounded(AcousticDefaults.Reflections(space), 0, -300, 600, -1000);
            Assert.Greater(AcousticRules.ReflectionEnergy(s), 0.1f, $"{space} has a real close reflection");
        }
        Assert.Less(AcousticRules.ReflectionEnergy(AcousticDefaults.Reflections(AcousticSpace.Neutral)), 0.001f, "neutral is dry");
    }

    [Test]
    public void ReflectionsEaseOffWithDistanceButNeverVanish()
    {
        float prev = 2f;
        for (float d = 0f; d <= 30f; d += 1f)
        {
            float w = AcousticRules.ReflectionDistanceWeight(d, 4f, 20f, 0.35f);
            Assert.LessOrEqual(w, prev + 1e-6f);
            Assert.GreaterOrEqual(w, 0.35f - 1e-6f);
            prev = w;
        }
        Assert.Less(AcousticRules.WeightToMillibels(0.35f), 0);
        Assert.AreEqual(-6000, AcousticRules.WeightToMillibels(0f));
    }

    [Test]
    public void ProfilesAreOrderedByCharacter()
    {
        float E(AcousticSpace s) => AcousticRules.ReflectionEnergy(AcousticDefaults.Reflections(s));
        Assert.Greater(E(AcousticSpace.LargeMachinery), E(AcousticSpace.Corridor), "machinery is stronger than a corridor");
        Assert.Greater(E(AcousticSpace.Corridor), E(AcousticSpace.SmallRoom), "a metal corridor is brighter than a small room");
        Assert.Greater(E(AcousticSpace.SmallRoom), E(AcousticSpace.LargeOpen), "a large hold has less immediate reflection");
        Assert.Greater(AcousticDefaults.Reflections(AcousticSpace.LargeOpen).reflectionsDelay, AcousticDefaults.Reflections(AcousticSpace.Corridor).reflectionsDelay,
            "far walls reflect later");
        Assert.Greater(AcousticDefaults.Reflections(AcousticSpace.LargeMachinery).decayTime, AcousticDefaults.Reflections(AcousticSpace.Compartment).decayTime);
        Assert.Less(AcousticDefaults.Reflections(AcousticSpace.Crawlspace).reflectionsDelay, AcousticDefaults.Reflections(AcousticSpace.Corridor).reflectionsDelay, "boxy: very short");
    }

    [Test]
    public void WetLevelsAreAlwaysBounded()
    {
        foreach (var space in Spaces)
            foreach (int offset in new[] { 0, -1000, 3000, 9000 })
            {
                var s = AcousticRules.Bounded(AcousticDefaults.Reflections(space), offset, -300, 600, -1000);
                Assert.LessOrEqual(s.room, -300);
                Assert.LessOrEqual(s.reflections, 600);
                Assert.LessOrEqual(s.reverb, -1000, "the emitter's own late part stays small; the zone gives the tail");
                Assert.GreaterOrEqual(s.room, -10000);
            }
    }

    [Test]
    public void VolumeIsComposedOnceFromItsBase()
    {
        float v = AcousticRules.Compose(0.5f, 0.8f, 0.5f);
        Assert.AreEqual(0.2f, v, 1e-6f);
        // A new occlusion gain starts again from the base values, never from the already-reduced volume.
        Assert.AreEqual(AcousticRules.Compose(0.5f, 0.8f, 0.25f), AcousticRules.Compose(0.5f, 0.8f, 0.25f));
        Assert.AreEqual(0.1f, AcousticRules.Compose(0.5f, 0.8f, 0.25f), 1e-6f);
        Assert.AreEqual(1f, AcousticRules.Compose(2f, 3f, 4f), "never above full");
    }

    [Test]
    public void TransitionsNeverOvershoot()
    {
        foreach (float dt in new[] { 0.001f, 0.016f, 0.5f, 5f })
        {
            float a = AcousticRules.Approach(0f, 10f, dt, 0.4f);
            Assert.That(a, Is.InRange(0f, 10f));
            float b = AcousticRules.Approach(10f, -5f, dt, 0.4f);
            Assert.That(b, Is.InRange(-5f, 10f));
        }
        Assert.AreEqual(3f, AcousticRules.Approach(1f, 3f, 0.1f, 0f), "zero seconds snaps");
    }

    [Test]
    public void ReflectionSlotsAreCapped()
    {
        var budget = new ReflectionBudget(2);
        Assert.IsTrue(budget.TryAcquire());
        Assert.IsTrue(budget.TryAcquire());
        Assert.IsFalse(budget.TryAcquire(), "a third sound plays dry");
        budget.Release();
        Assert.IsTrue(budget.TryAcquire());
        budget.Clear();
        budget.Release();
        Assert.AreEqual(0, budget.InUse, "never below zero");
    }

    [Test]
    public void RoomToneLayersFollowTheSpace()
    {
        Assert.AreEqual((0f, 0f, 0f), AcousticDefaults.ToneLevels(AcousticSpace.Neutral));
        Assert.Greater(AcousticDefaults.ToneLevels(AcousticSpace.LargeMachinery).machinery, AcousticDefaults.ToneLevels(AcousticSpace.Corridor).machinery);
        Assert.Greater(AcousticDefaults.ToneLevels(AcousticSpace.Corridor).air, AcousticDefaults.ToneLevels(AcousticSpace.Compartment).air);
        Assert.AreEqual(1f, AcousticDefaults.ToneLevels(AcousticSpace.Crawlspace).duct);
    }

    [Test]
    public void LoopRegionSkipsTheFadesOfABed()
    {
        var db = new List<float>();
        for (int i = 0; i < 10; i++) db.Add(-60f + i * 4f); // 1 s fade in
        for (int i = 0; i < 700; i++) db.Add(-17f + (i % 7) - 3f);
        for (int i = 0; i < 40; i++) db.Add(-20f - i * 1.5f); // 4 s fade out
        var (start, end) = ClipRegions.LoopRegion(db, 0.1f, 8f, 0.5f, 10f);
        Assert.That(start, Is.InRange(0.8f, 2f));
        Assert.That(end, Is.InRange(70f, 72f));
        Assert.AreEqual((0f, 0f), ClipRegions.LoopRegion(new List<float> { -20, -20, -20 }, 0.1f, 8f, 0.5f, 10f), "too short to loop");
    }

    [Test]
    public void CallSegmentsFindSeparateCallsInsideTheRecording()
    {
        var db = new List<float>();
        for (int i = 0; i < 600; i++) db.Add(-25f);
        foreach (int peak in new[] { 100, 250, 400, 405 }) db[peak] = -12f;
        var calls = ClipRegions.CallSegments(db, 0.1f, 7f, 8f, 1.6f, 6f, 8);
        Assert.AreEqual(3, calls.Count, "peaks closer than the gap are one call");
        Assert.AreEqual(10f - 1.6f, calls[0].start, 1e-4f);
        foreach (var c in calls) Assert.LessOrEqual(c.start + c.length, 60f);
    }

    // ---------- Tracker ----------

    const float Threshold = 0.35f, Range = 28f;

    static float Signal(float speed, float distance, bool vent = false, bool gate = true) =>
        TrackerRules.Signal(speed, distance, Threshold, Range, vent, 0.45f, gate);

    [Test]
    public void StillOrSlowCreatureShowsNothing()
    {
        Assert.AreEqual(0f, Signal(0f, 1f), "a still creature right next to you shows nothing");
        Assert.AreEqual(0f, Signal(0.3f, 5f), "below the motion threshold");
        Assert.AreEqual(0f, Signal(3f, 29f), "beyond range");
        Assert.Greater(Signal(0.5f, 5f), 0f);
    }

    [Test]
    public void NearerAndFasterIsStronger()
    {
        Assert.Greater(Signal(2f, 3f), Signal(2f, 15f));
        Assert.Greater(Signal(2f, 15f), Signal(2f, 27f));
        Assert.Greater(Signal(4f, 10f), Signal(0.6f, 10f));
        for (float d = 0f; d <= Range; d += 2f) Assert.That(Signal(5f, d), Is.InRange(0f, 1f));
    }

    [Test]
    public void VentMotionIsWeakerAndIntermittent()
    {
        Assert.Less(Signal(2f, 6f, vent: true), Signal(2f, 6f));
        Assert.Greater(Signal(2f, 6f, vent: true), 0f);
        Assert.AreEqual(0f, Signal(2f, 6f, vent: true, gate: false), "the gate closes: nothing");
        int open = 0;
        for (int i = 0; i < 1000; i++) if (TrackerRules.StructureGate(i * 0.0173, 1.7f, 0.45f, 0.3f)) open++;
        Assert.That(open / 1000f, Is.InRange(0.38f, 0.52f), "open for about the duty share");
    }

    [Test]
    public void BearingIsRelativeToCameraYaw()
    {
        Assert.AreEqual(0f, TrackerRules.Bearing(0f, 0f, 5f), 1e-4f, "straight ahead");
        Assert.AreEqual(90f, TrackerRules.Bearing(0f, 5f, 0f), 1e-4f, "to the right");
        Assert.AreEqual(180f, System.Math.Abs(TrackerRules.Bearing(0f, 0f, -5f)), 1e-4f, "behind");
        Assert.AreEqual(0f, TrackerRules.Bearing(90f, 5f, 0f), 1e-4f, "facing +x, a contact at +x is ahead");
        Assert.AreEqual(-90f, TrackerRules.Bearing(90f, 0f, 5f), 1e-4f, "facing +x, +z is to the left");
        Assert.That(TrackerRules.Bearing(-170f, 0.1f, -5f), Is.InRange(-180f, 180f));
    }

    [Test]
    public void UncertaintyGrowsWithDistance()
    {
        Assert.Less(TrackerRules.AngleUncertainty(2f, Range, 4f, 24f), TrackerRules.AngleUncertainty(20f, Range, 4f, 24f));
        Assert.Less(TrackerRules.DistanceUncertainty(2f, Range, 0.05f, 0.3f), TrackerRules.DistanceUncertainty(20f, Range, 0.05f, 0.3f));
        Assert.AreEqual(24f, TrackerRules.AngleUncertainty(100f, Range, 4f, 24f), 1e-4f, "capped at the far value");
        for (double t = 0; t < 50; t += 0.37) Assert.That(TrackerRules.Wobble(t, 1.3f), Is.InRange(-1f, 1f));
    }

    [Test]
    public void BeepIntervalShrinksWithSignalAndStaysBounded()
    {
        float prev = float.MaxValue;
        for (float s = 0.05f; s <= 1.0001f; s += 0.05f)
        {
            float i = TrackerRules.BeepInterval(s, 0.32f, 1.25f, 1.8f);
            Assert.That(i, Is.InRange(0.32f, 1.25f));
            Assert.LessOrEqual(i, prev);
            prev = i;
        }
        Assert.AreEqual(1.8f, TrackerRules.BeepInterval(0f, 0.32f, 1.25f, 1.8f), 1e-5f, "idle sweep");
        Assert.AreEqual(0f, TrackerRules.BeepInterval(0f, 0.32f, 1.25f, 0f), "no idle beeps when disabled");
        Assert.AreEqual(0.32f, TrackerRules.BeepInterval(5f, 0.32f, 1.25f, 1.8f), 1e-5f);
    }

    [Test]
    public void BeepNoiseRangeIsClamped()
    {
        Assert.AreEqual(8f, TrackerRules.ClampNoiseRange(50f, 0.5f, 8f));
        Assert.AreEqual(6f, TrackerRules.ClampNoiseRange(6f, 0.5f, 8f));
        Assert.AreEqual(0.5f, TrackerRules.ClampNoiseRange(-3f, 0.5f, 8f));
    }

    [Test]
    public void HostRejectsSpamAndInvalidBeeps()
    {
        double last = double.NegativeInfinity;
        Assert.IsTrue(TrackerRules.AcceptBeep(10.0, last, 0.25f, true, true, true, true));
        last = 10.0;
        Assert.IsFalse(TrackerRules.AcceptBeep(10.1, last, 0.25f, true, true, true, true), "too soon");
        Assert.IsTrue(TrackerRules.AcceptBeep(10.3, last, 0.25f, true, true, true, true));
        int accepted = 0;
        for (int i = 0; i < 100; i++) // 100 requests in one second
            if (TrackerRules.AcceptBeep(20.0 + i * 0.01, last, 0.25f, true, true, true, true)) { accepted++; last = 20.0 + i * 0.01; }
        Assert.LessOrEqual(accepted, 4, "spam is limited to the host's own rate");
        Assert.IsFalse(TrackerRules.AcceptBeep(40, 0, 0.25f, false, true, true, true), "not the owner");
        Assert.IsFalse(TrackerRules.AcceptBeep(40, 0, 0.25f, true, false, true, true), "dead");
        Assert.IsFalse(TrackerRules.AcceptBeep(40, 0, 0.25f, true, true, false, true), "round not active");
        Assert.IsFalse(TrackerRules.AcceptBeep(40, 0, 0.25f, true, true, true, false), "tracker not raised");
    }

    // ---------- Lifecycle ----------

    [Test]
    public void BlipHistoryKeepsTheNewestAndClears()
    {
        var h = new BlipHistory(3);
        for (int i = 0; i < 5; i++) h.Add(i, -i, i);
        Assert.AreEqual(3, h.Count);
        h.Get(0, out float x, out float y, out double t);
        Assert.AreEqual(4f, x);
        Assert.AreEqual(-4f, y);
        Assert.AreEqual(4.0, t);
        h.Get(2, out x, out _, out _);
        Assert.AreEqual(2f, x, "oldest kept");
        h.Clear();
        Assert.AreEqual(0, h.Count, "a reset leaves nothing to draw");
    }

    [Test]
    public void LateJoinerNeverHearsAnExpiredWarning()
    {
        Assert.AreEqual(1f, VentCueRules.LateJoinOffset(1f, 3f, 1f, 0.2f), 1e-5f, "joins mid-warning: the rest of it");
        Assert.AreEqual(-1f, VentCueRules.LateJoinOffset(2.9f, 3f, 1f, 0.2f), "almost over: nothing");
        Assert.AreEqual(-1f, VentCueRules.LateJoinOffset(30f, 3f, 1f, 0.2f), "long expired: nothing");
        Assert.AreEqual(1.1f, VentCueRules.LateJoinOffset(2f, 3f, 0.55f, 0.2f), 1e-5f, "a pitched-down fallback plays slower");
        Assert.AreEqual(0f, VentCueRules.LateJoinOffset(-1f, 3f, 1f, 0.2f), "clock skew counts as the start");
    }
}
