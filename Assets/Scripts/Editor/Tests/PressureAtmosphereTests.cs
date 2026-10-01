using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the creature-pressure and ship-atmosphere rules (pure logic). They do not verify rendering, spatial audio,
// Netcode, scene geometry, the NavMesh or multiplayer behaviour.
public class PressureAtmosphereTests
{
    // Cadence defaults: first 10-15 s, calm 40-60 s, alert 20-35 s, frustration shortens to 60%, never below 18 s.
    static float Interval(bool first, float alert, float u, float frustration = 0f) =>
        VentCadence.Interval(first, alert, u, 10f, 15f, 40f, 60f, 20f, 35f, frustration, 0.6f, 18f);

    [Test]
    public void VentDesireRisesWithTimeAndWithAlertness()
    {
        float calm = Interval(false, 0f, 0.5f), alert = Interval(false, 1f, 0.5f);
        Assert.Less(VentCadence.Desire(10, calm), VentCadence.Desire(30, calm), "desire grows with time since the last trip");
        Assert.Greater(VentCadence.Desire(30, alert), VentCadence.Desire(30, calm), "a more alert creature wants a vent sooner");
        Assert.AreEqual(0f, VentCadence.Desire(0, calm));
        Assert.AreEqual(1f, VentCadence.Desire(calm, calm), 1e-5f, "desire reaches 1 exactly at the interval");
        Assert.Greater(VentCadence.Desire(calm * 1.5, calm), 1f);
    }

    [Test]
    public void CalmAndHeightenedCadenceUseDifferentRanges()
    {
        for (float u = 0f; u < 1f; u += 0.1f)
        {
            float calm = Interval(false, 0f, u), alert = Interval(false, 1f, u), first = Interval(true, 0f, u);
            Assert.That(calm, Is.InRange(40f, 60f));
            Assert.That(alert, Is.InRange(20f, 35f));
            Assert.That(first, Is.InRange(10f, 15f), "the first tactical trip of a round can come early");
            Assert.Less(alert, calm);
        }
    }

    [Test]
    public void FrustrationShortensTheCadenceButNeverBelowTheAbsoluteMinimum()
    {
        Assert.Less(Interval(false, 0f, 0.5f, 1f), Interval(false, 0f, 0.5f, 0f));
        for (float u = 0f; u < 1f; u += 0.1f)
            Assert.GreaterOrEqual(Interval(false, 1f, u, 1f), 18f, "never below the minimum interval");
        Assert.IsFalse(VentCadence.MayConsider(false, false, now: 110, lastTripEnd: 100, minInterval: 18), "10 s after a trip: too soon whatever the desire");
        Assert.IsTrue(VentCadence.MayConsider(false, false, 118, 100, 18));
    }

    [Test]
    public void SeededCadenceIsDeterministicForTheSameRoundSeed()
    {
        var a = new System.Random(1234);
        var b = new System.Random(1234);
        var c = new System.Random(99);
        bool differs = false;
        for (int i = 0; i < 10; i++)
        {
            float ua = (float)a.NextDouble(), ub = (float)b.NextDouble(), uc = (float)c.NextDouble();
            Assert.AreEqual(Interval(false, 0.3f, ua), Interval(false, 0.3f, ub), "same seed, same cadence");
            if (System.Math.Abs(Interval(false, 0.3f, ua) - Interval(false, 0.3f, uc)) > 0.01f) differs = true;
        }
        Assert.IsTrue(differs, "a different seed varies the cadence, so it is not clockwork");
    }

    [Test]
    public void DesireNeverBypassesTheChaseRulesOrAnInvalidRoute()
    {
        // However strong the desire, the gates come first.
        Assert.IsFalse(VentCadence.MayConsider(confirmedSight: true, chasing: false, 500, 0, 18));
        Assert.IsFalse(VentCadence.MayConsider(false, chasing: true, 500, 0, 18));
        // Desire only adds slack to the time comparison; a disconnected network still has no plan.
        var g = new VentGraph(4);
        g.AddEdge(0, 1, 10f);
        g.AddEdge(2, 3, 10f);
        float bias = VentCadence.Bias(desire: 5f, biasSeconds: 30f);
        Assert.AreEqual(60f, bias, 1e-4f, "bias is capped at twice the setting");
        var plan = VentRules.Choose(g, new[] { 0f, -1f, -1f, -1f }, new[] { -1f, -1f, -1f, 0f }, new float[4], new float[4],
            groundSeconds: 10f + bias, 3f, 5f, 4f, 0.8f);
        Assert.IsFalse(plan.found, "no route, no trip");
        Assert.AreEqual(0f, VentCadence.Bias(0.9f, 30f), "no slack before desire reaches 1");
    }

    [Test]
    public void AnExitWatchedByAFartherPlayerCountsAsWatched()
    {
        // Player 0 is nearest but looking away; player 1 is a little farther and can see the exit.
        var distances = new List<float> { 3f, 5f };
        var views = new List<bool> { false, true };
        VentRules.AggregateWatch(distances, views, 2, 10f, out float nearest, out bool watched);
        Assert.AreEqual(3f, nearest, 1e-5f, "the nearest distance is the minimum over everyone");
        Assert.IsTrue(watched, "one player looking away must not hide another who can see the exit");
        // Someone who can see it but is beyond the view distance does not count as watching.
        VentRules.AggregateWatch(new List<float> { 3f, 25f }, new List<bool> { false, true }, 2, 10f, out _, out bool far);
        Assert.IsFalse(far);
        VentRules.AggregateWatch(new List<float>(), new List<bool>(), 0, 10f, out float none, out bool noWatch);
        Assert.AreEqual(float.MaxValue, none);
        Assert.IsFalse(noWatch);
        // The aggregate feeds the existing penalty rule: watched and near gives the view penalty.
        Assert.AreEqual(12f, VentRules.ExitPenalty(nearest, watched, false, 2.5f, 10f, 12f));
    }

    [Test]
    public void PendingDuctEvidenceSurvivesUntilAJunctionAndIsThenConsumed()
    {
        var p = new PendingEvidence<string>();
        Assert.IsTrue(p.Offer("sprint in Cargo", priority: 0.4f, time: 10, now: 10, maxAge: 8));
        // Five seconds later the creature reaches the junction: still fresh, taken, and gone afterwards.
        Assert.IsTrue(p.TryTake(now: 15, maxAge: 8, out string got));
        Assert.AreEqual("sprint in Cargo", got);
        Assert.IsFalse(p.Has, "consumed at the junction");
        Assert.IsFalse(p.TryTake(16, 8, out _));
    }

    [Test]
    public void StrongerOrPursuedEvidenceReplacesWeakerAndStaleEvidenceExpires()
    {
        var p = new PendingEvidence<string>();
        p.Offer("weak", 0.2f, 0, 0, 8);
        Assert.IsTrue(p.Offer("strong", 0.6f, 1, 1, 8), "a stronger sound replaces a weaker one");
        Assert.IsFalse(p.Offer("weaker again", 0.3f, 2, 2, 8), "a weaker sound does not replace a stronger one");
        Assert.AreEqual("strong", p.Value);
        Assert.IsTrue(p.Offer("pursued trail", 2.2f, 3, 3, 8), "the pursued player's own trail outranks louder strangers");
        Assert.IsTrue(p.Expired(now: 12, maxAge: 8));
        Assert.IsTrue(p.Offer("anything new", 0.1f, 12, 12, 8), "an expired one is replaced by anything");
        Assert.IsFalse(p.TryTake(now: 30, maxAge: 8, out _), "too old by the junction: dropped");
        Assert.IsFalse(p.Has);
        p.Offer("x", 1f, 40, 40, 8);
        p.Clear(); // round reset
        Assert.IsFalse(p.Has);
    }

    [Test]
    public void FootstepAttenuationFallsSteadilyAndIsSilentAtTheMaximum()
    {
        const float Full = 3.5f, Max = 22f;
        Assert.AreEqual(1f, AudioRules.DistanceAttenuation(2f, Full, Max));
        float prev = 1f;
        for (float d = Full; d <= Max + 2f; d += 0.5f)
        {
            float g = AudioRules.DistanceAttenuation(d, Full, Max);
            Assert.LessOrEqual(g, prev + 1e-6f, $"never louder further away (d={d})");
            prev = g;
        }
        Assert.AreEqual(0f, AudioRules.DistanceAttenuation(Max, Full, Max));
        Assert.Less(AudioRules.DistanceAttenuation(18f, Full, Max), 0.1f, "distant steps are faint");
        Assert.Greater(AudioRules.DistanceAttenuation(6f, Full, Max), 0.4f, "nearby steps stay threatening");
    }

    [Test]
    public void OcclusionMufflesAndQuietensButNeverSilences()
    {
        Assert.AreEqual(0f, AudioRules.OcclusionAmount(0, 0.6f));
        float one = AudioRules.OcclusionAmount(1, 0.6f), two = AudioRules.OcclusionAmount(2, 0.6f);
        Assert.Greater(two, one, "more walls, more occlusion");
        Assert.Less(AudioRules.OcclusionAmount(4, 0.6f), 1f, "rises quickly, never quite total");
        Assert.Greater(AudioRules.OcclusionGain(1f, 0.45f), 0f, "never silent");
        Assert.AreEqual(1f, AudioRules.OcclusionGain(0f, 0.45f));
        Assert.Less(AudioRules.OcclusionGain(two, 0.45f), AudioRules.OcclusionGain(one, 0.45f));
        Assert.AreEqual(22000f, AudioRules.OcclusionCutoff(0f, 22000f, 800f), 1f);
        Assert.AreEqual(800f, AudioRules.OcclusionCutoff(1f, 22000f, 800f), 1f);
        Assert.Less(AudioRules.OcclusionCutoff(two, 22000f, 800f), AudioRules.OcclusionCutoff(one, 22000f, 800f), "high frequencies drop first");
    }

    [Test]
    public void AcousticZoneSelectionPrefersTheMostSpecificContainingZone()
    {
        var contains = new List<bool> { true, true, false };
        var priority = new List<int> { 0, 2, 5 };
        Assert.AreEqual(1, AcousticRules.Select(contains, priority, 3), "the pod inside the room wins; a zone not containing the listener never does");
        Assert.AreEqual(0, AcousticRules.Select(new List<bool> { true, true }, new List<int> { 1, 1 }, 2), "ties go to the lower index");
        Assert.AreEqual(-1, AcousticRules.Select(new List<bool> { false }, new List<int> { 0 }, 1), "outside every zone: the caller's fallback");
    }

    [Test]
    public void AcousticTransitionsAreSmoothAndNeverOvershoot()
    {
        float v = 0f;
        float prev = v;
        for (int i = 0; i < 100; i++)
        {
            v = AcousticRules.Approach(v, 1f, 0.02f, 0.8f);
            Assert.GreaterOrEqual(v, prev);
            Assert.LessOrEqual(v, 1f);
            prev = v;
        }
        Assert.Greater(v, 0.9f, "settled after a couple of transition times");
        Assert.AreEqual(0.5f, AcousticRules.Approach(0f, 0.5f, 0.1f, 0f), "zero time snaps");
        Assert.AreEqual(1f, AcousticRules.Approach(0f, 1f, 5f, 0.5f), "a long frame never overshoots");
    }

    [Test]
    public void FlickerStaysWithinBoundsIsSeededAndHasLongSteadyGaps()
    {
        var a = new FlickerSchedule(42, 4f, 9f, 0.2f, 0.08f, 3);
        var b = new FlickerSchedule(42, 4f, 9f, 0.2f, 0.08f, 3);
        float steady = 0f, shortestSteady = float.MaxValue;
        bool sawDip = false, wasDipping = false;
        for (int i = 0; i < 6000; i++) // two minutes at 50 Hz
        {
            float la = a.Tick(0.02f, true), lb = b.Tick(0.02f, true);
            Assert.AreEqual(la, lb, "same seed, same flicker");
            Assert.That(la, Is.InRange(0.2f, 1f));
            if (la < 1f) { sawDip = true; if (!wasDipping && steady > 0f) shortestSteady = System.Math.Min(shortestSteady, steady); steady = 0f; wasDipping = true; }
            else { steady += 0.02f; wasDipping = false; }
        }
        Assert.IsTrue(sawDip);
        Assert.Greater(shortestSteady, 0f, "even inside an event the light comes back between dips");
        // Count events: dips separated by more than a second of steady light. No constant strobing.
        var c = new FlickerSchedule(7, 4f, 9f, 0.2f, 0.08f, 3);
        int events = 0;
        float sinceDip = 99f;
        for (int i = 0; i < 6000; i++)
        {
            bool dip = c.Tick(0.02f, true) < 1f;
            if (dip && sinceDip > 1f) events++;
            sinceDip = dip ? 0f : sinceDip + 0.02f;
        }
        Assert.That(events, Is.InRange(8, 32), "an event every 4 to 9 s over two minutes");
    }

    [Test]
    public void DisabledFlickerIsSteady()
    {
        var f = new FlickerSchedule(3, 0.5f, 0.6f, 0.1f, 0.05f, 3);
        for (int i = 0; i < 1000; i++) Assert.AreEqual(1f, f.Tick(0.02f, false));
    }
}
