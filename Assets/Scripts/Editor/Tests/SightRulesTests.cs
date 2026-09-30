using NUnit.Framework;

// EditMode tests for gradual recognition and hiding-volume rules (pure logic, no scene needed).
public class SightRulesTests
{
    // The creature's defaults: 0.25 s to recognise an exposed close player, sight 15 m, 110 degree FOV, close sense 2.5 m.
    const float Sight = 15f, Fov = 110f, Close = 2.5f, Seconds = 0.25f, Far = 0.3f, Side = 0.5f, Hold = 0.4f, Decay = 2.5f, Dt = 0.1f;

    static float Rate(float exposure, float distance, float angle = 0f, float cover = 0f, float light = 1f) =>
        SightRules.Rate(exposure, distance, angle, Sight, Fov, Close, cover, light, Seconds, Far, Side);

    // Seconds of continuous clear observation until recognised (capped at 120).
    static float TimeToRecognise(float rate)
    {
        var a = new AwarenessState();
        float t = 0f;
        while (!a.Recognised && t < 120f) { a.Tick(Dt, rate, true, Hold, Decay); t += Dt; }
        return t;
    }

    [Test]
    public void NoSamplesMeansNoAwarenessGain()
    {
        Assert.AreEqual(0f, Rate(0f, 1f));
        var a = new AwarenessState();
        for (int i = 0; i < 100; i++) a.Tick(Dt, Rate(0f, 1f), false, Hold, Decay);
        Assert.AreEqual(0f, a.Value, "ten seconds without a sample (for example behind a wall) builds nothing");
    }

    [Test]
    public void ExposedNearbyPlayerIsRecognisedQuickly()
    {
        Assert.LessOrEqual(TimeToRecognise(Rate(1f, 3f)), 0.4f);
        Assert.LessOrEqual(TimeToRecognise(Rate(1f, 1f)), 0.3f);
    }

    [Test]
    public void ConcealmentDelaysRecognitionButSustainedObservationStillReveals()
    {
        float open = TimeToRecognise(Rate(1f, 4f));
        float hidden = TimeToRecognise(Rate(2f / 3f, 4f, 0f, 0.7f)); // two of three samples, under a table
        Assert.Greater(hidden, open * 3f, "noticeably slower under cover");
        Assert.Less(hidden, 5f, "but a sustained clear look gives it away");
        Assert.Less(TimeToRecognise(Rate(1f / 3f, 12f, 40f, 0.9f)), 120f, "even the best cover at range is finite: concealment is capped below 1");
        Assert.Greater(Rate(1f / 3f, 12f, 40f, 5f), 0f, "a huge concealment value cannot reach zero");
    }

    [Test]
    public void DistanceAngleAndLightSlowRecognitionMonotonically()
    {
        Assert.Greater(Rate(1f, 3f), Rate(1f, 9f));
        Assert.Greater(Rate(1f, 9f), Rate(1f, 15f));
        Assert.Greater(Rate(1f, 6f, 5f), Rate(1f, 6f, 50f));
        Assert.AreEqual(Rate(1f, 2f, 0f), Rate(1f, 2f, 90f), 1e-5f, "inside the close sense the angle does not matter");
        Assert.Greater(Rate(1f, 4f, 0f, 0f, 1f), Rate(1f, 4f, 0f, 0f, 0.5f), "a dim spot is slower");
        Assert.Greater(Rate(1f, 4f), Rate(0.33f, 4f), "more exposed samples are faster");
    }

    [Test]
    public void BriefFlickerKeepsAwarenessButNeverBuildsIt()
    {
        var a = new AwarenessState();
        for (int i = 0; i < 4; i++) a.Tick(Dt, 1f, true, Hold, Decay); // 0.4 of a second of view
        float v = a.Value;
        Assert.AreEqual(0.4f, v, 1e-3f);
        for (int i = 0; i < 3; i++) a.Tick(Dt, 0f, false, Hold, Decay); // 0.3 s lost, within the hold
        Assert.AreEqual(v, a.Value, 1e-4f, "held through the flicker");
        Assert.IsFalse(a.Recognised);
        for (int i = 0; i < 40; i++) a.Tick(Dt, 0f, false, Hold, Decay); // long gone: decays to nothing
        Assert.AreEqual(0f, a.Value, 1e-4f);
    }

    [Test]
    public void RecognisedStaysRecognisedWhileVisibleAndHoldsBrieflyAfter()
    {
        var a = new AwarenessState();
        for (int i = 0; i < 10; i++) a.Tick(Dt, 10f, true, Hold, Decay);
        Assert.IsTrue(a.Recognised);
        for (int i = 0; i < 20; i++) a.Tick(Dt, 0.01f, true, Hold, Decay); // visible but barely (deep cover): stays recognised
        Assert.IsTrue(a.Recognised);
        a.Tick(Dt, 0f, false, Hold, Decay);
        Assert.IsTrue(a.Holding(Hold), "a single lost tick is a flicker");
        for (int i = 0; i < 5; i++) a.Tick(Dt, 0f, false, Hold, Decay);
        Assert.IsFalse(a.Holding(Hold), "after the hold the chase no longer counts it as held");
    }

    [Test]
    public void AwarenessIsKeptPerPlayer()
    {
        var a = new AwarenessState();
        var b = new AwarenessState();
        for (int i = 0; i < 20; i++) { a.Tick(Dt, 2f, true, Hold, Decay); b.Tick(Dt, 0f, false, Hold, Decay); }
        Assert.IsTrue(a.Recognised);
        Assert.AreEqual(0f, b.Value, "another player's observation does not leak across");
        a.Clear();
        Assert.AreEqual(0f, a.Value);
        Assert.IsFalse(a.Holding(Hold));
    }

    // A table 1.8 x 1.3 m, volume 1.15 m high, body centre at least 0.2 m inside the sides.
    static bool Inside(float x, float z, float feetY = 0f, float height = 1f) =>
        SightRules.InsideVolume(x, z, 0.9f, 0.65f, 0.2f, feetY, feetY + height, 0f, 1.15f);

    [Test]
    public void StandingBesideOrBarelyTouchingTheVolumeGivesNoCover()
    {
        Assert.IsTrue(Inside(0f, 0f), "crouched fully underneath");
        Assert.IsTrue(Inside(0.6f, 0.3f), "well inside, off centre");
        Assert.IsFalse(Inside(0f, 0.9f), "standing beside the table");
        Assert.IsFalse(Inside(0f, 0.6f), "centre past the inset: half out of the side");
        Assert.IsFalse(Inside(0f, 0.66f), "just touching the edge");
        Assert.IsFalse(Inside(0f, 0f, 0f, 1.8f), "a standing body is taller than the volume");
        Assert.IsFalse(Inside(0f, 0f, 1.2f, 1f), "on top of the table, not under it");
    }

    [Test]
    public void OverlappingVolumesNeverStackAndResolveDeterministically()
    {
        Assert.AreEqual(2, SightRules.PickStrongest(new[] { 0.65f, 0.8f, 0.8f }, new[] { 30, 20, 10 }), "strongest wins; a tie goes to the lower key");
        Assert.AreEqual(1, SightRules.PickStrongest(new[] { 0.8f, 0.8f, 0.65f }, new[] { 20, 10, 5 }), "list order does not matter");
        Assert.AreEqual(0, SightRules.PickStrongest(new[] { 0.65f, 0.65f }, new[] { 1, 2 }), "the result is ONE volume, never a sum of two");
        Assert.AreEqual(-1, SightRules.PickStrongest(new float[0], new int[0]));
    }

    [Test]
    public void DeathTipsAreAccurateAndFallBackToNeutralText()
    {
        Assert.AreEqual("It saw you enter this hiding spot.", SightRules.Tip(DetectionReason.SawEnterSpot));
        Assert.AreEqual("Your footsteps led it towards you.", SightRules.Tip(DetectionReason.FootstepsLedHere));
        Assert.AreEqual("You were visible through the opening.", SightRules.Tip(DetectionReason.VisibleThroughOpening));
        Assert.AreEqual("It found you while inspecting this spot.", SightRules.Tip(DetectionReason.FoundWhileInspecting));
        Assert.AreEqual("It caught you.", SightRules.Tip(DetectionReason.Unknown));
        Assert.AreEqual("It caught you.", SightRules.Tip((DetectionReason)200), "unknown value is neutral, not a claim");
    }

    // ---------- Concealed recognition: the combined formula, as the creature applies it ----------

    const float Table = 0.65f, Calm = 6f, Wary = 4f, Active = 2.5f, Inspect = 0.5f;

    // Same composition as CreatureAI.Observe for a player in an eligible volume.
    static float Concealed(float attentionSeconds, float cover, float exposure = 1f, float distance = 3f, float angle = 0f, float light = 1f) =>
        SightRules.Rate(exposure, distance, angle, Sight, Fov, Close, 0f, light,
            SightRules.ConcealedSeconds(attentionSeconds, cover, Table, Seconds), Far, Side);

    [Test]
    public void ConcealedRecognitionHitsTheTargetTimesForAStandardTable()
    {
        Assert.AreEqual(Calm, TimeToRecognise(Concealed(Calm, Table)), 0.2f);
        Assert.AreEqual(Wary, TimeToRecognise(Concealed(Wary, Table)), 0.2f);
        Assert.AreEqual(Active, TimeToRecognise(Concealed(Active, Table)), 0.2f);
        Assert.AreEqual(Inspect, TimeToRecognise(Concealed(Inspect, Table)), 0.2f);
    }

    [Test]
    public void CalmConcealedIsMuchSlowerThanActiveSearchAndExposedStaysFast()
    {
        float calm = TimeToRecognise(Concealed(Calm, Table));
        float active = TimeToRecognise(Concealed(Active, Table));
        float exposed = TimeToRecognise(Rate(1f, 3f));
        Assert.Greater(calm, active * 2f);
        Assert.Greater(calm, 5f, "a calm patrol needs several seconds of sustained view");
        Assert.LessOrEqual(exposed, 0.4f, "ordinary exposed recognition is unchanged");
        Assert.Greater(TimeToRecognise(Concealed(Inspect, Table)), exposed, "even inspection is not faster than exposed");
    }

    [Test]
    public void BetterCoverTakesLongerAndNothingIsWorseThanBeingExposed()
    {
        float poor = TimeToRecognise(Concealed(Calm, 0.4f));
        float table = TimeToRecognise(Concealed(Calm, Table));
        float bunk = TimeToRecognise(Concealed(Calm, 0.8f));
        Assert.Less(poor, table);
        Assert.Less(table, bunk);
        Assert.GreaterOrEqual(TimeToRecognise(Concealed(Calm, 0f)), TimeToRecognise(Rate(1f, 3f)) - 0.2f, "no cover is never faster than exposed");
        Assert.AreEqual(Seconds, SightRules.ConcealedSeconds(0.1f, 0f, Table, Seconds), "floor is the exposed time");
    }

    [Test]
    public void ConcealedRecognitionStillDependsOnDistanceAngleAndExposedSamples()
    {
        float close = Concealed(Calm, Table);
        Assert.Less(Concealed(Calm, Table, 1f, 10f), close);
        Assert.Less(Concealed(Calm, Table, 1f, 6f, 50f), Concealed(Calm, Table, 1f, 6f, 5f));
        Assert.Less(Concealed(Calm, Table, 1f / 3f), close, "one exposed sample is slower than three");
        Assert.Less(Concealed(Calm, Table, 1f, 3f, 0f, 0.5f), close, "dim is slower");
        Assert.AreEqual(0f, Concealed(Calm, Table, 0f), "no samples, no gain");
    }

    [Test]
    public void WeakSuspicionCannotRaiseItsOwnDetectionRate()
    {
        // Attention has no awareness input: a half-built glimpse leaves a calm patrol calm.
        Assert.AreEqual(AttentionLevel.Calm, SightRules.AttentionFor(false, false, false, 0f, 0.2f));
        var a = new AwarenessState();
        float rate = Concealed(Calm, Table);
        for (int i = 0; i < 30; i++) a.Tick(Dt, rate, true, Hold, Decay); // 3 s of glimpse: about half
        Assert.Greater(a.Value, 0.3f);
        Assert.Less(a.Value, 0.7f);
        Assert.AreEqual(rate, Concealed(Calm, Table), "the rate is the same at awareness 0.5 as at 0");
        Assert.IsFalse(a.Recognised);
    }

    [Test]
    public void AttentionFollowsBehaviourAndAlertness()
    {
        Assert.AreEqual(AttentionLevel.Inspecting, SightRules.AttentionFor(true, true, false, 1f, 0.2f));
        Assert.AreEqual(AttentionLevel.Searching, SightRules.AttentionFor(false, true, false, 0f, 0.2f), "active search or pursuit");
        Assert.AreEqual(AttentionLevel.Heightened, SightRules.AttentionFor(false, false, true, 0f, 0.2f), "investigating a noise");
        Assert.AreEqual(AttentionLevel.Heightened, SightRules.AttentionFor(false, false, false, 0.8f, 0.2f), "patrol after a hunt");
        Assert.AreEqual(AttentionLevel.Calm, SightRules.AttentionFor(false, false, false, 0.1f, 0.2f), "alertness has faded");
    }

    [Test]
    public void PassingEncountersDoNotAccumulateAwareness()
    {
        var a = new AwarenessState();
        float rate = Concealed(Calm, Table);
        for (int pass = 0; pass < 10; pass++)
        {
            for (int i = 0; i < 20; i++) a.Tick(Dt, rate, true, Hold, Decay); // 2 s glance
            Assert.IsFalse(a.Recognised, $"glance {pass}");
            for (int i = 0; i < 40; i++) a.Tick(Dt, 0f, false, Hold, Decay); // 4 s looking elsewhere
            Assert.AreEqual(0f, a.Value, 1e-3f, $"decayed after glance {pass}");
        }
    }

    [Test]
    public void LosingSightInAChaseKeepsAttentionHighAndDoesNotResetAwarenessEarly()
    {
        // After a chase the creature is pursuing/searching, then heightened on patrol: never calm straight away.
        Assert.AreEqual(AttentionLevel.Searching, SightRules.AttentionFor(false, true, false, 1f, 0.2f));
        Assert.AreEqual(AttentionLevel.Heightened, SightRules.AttentionFor(false, false, false, 1f, 0.2f));
        var a = new AwarenessState();
        for (int i = 0; i < 10; i++) a.Tick(Dt, 10f, true, Hold, Decay);
        a.Tick(Dt, 0f, false, Hold, Decay);
        Assert.IsTrue(a.Recognised, "a one-tick flicker does not reset it");
    }
}
