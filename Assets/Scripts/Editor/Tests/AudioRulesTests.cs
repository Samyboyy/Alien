using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for the audio rules (pure logic; nothing here plays sound).
public class AudioRulesTests
{
    [Test]
    public void BagPlaysEveryClipOncePerCycleAndNeverRepeatsBackToBack()
    {
        var bag = new NoRepeatBag(11, new System.Random(7));
        int previous = -1;
        for (int cycle = 0; cycle < 50; cycle++)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < 11; i++)
            {
                int c = bag.Next();
                Assert.AreNotEqual(previous, c, $"repeat at cycle {cycle}, step {i}");
                Assert.IsTrue(seen.Add(c), "a clip came up twice within one cycle");
                previous = c;
            }
            Assert.AreEqual(11, seen.Count);
        }
    }

    [Test]
    public void BagHandlesTinyAndEmptyBanks()
    {
        Assert.AreEqual(-1, new NoRepeatBag(0, new System.Random(1)).Next());
        var one = new NoRepeatBag(1, new System.Random(1));
        Assert.AreEqual(0, one.Next());
        Assert.AreEqual(0, one.Next(), "a single clip has to repeat");
        var two = new NoRepeatBag(2, new System.Random(3));
        int last = two.Next();
        for (int i = 0; i < 20; i++) { int c = two.Next(); Assert.AreNotEqual(last, c); last = c; }
    }

    [Test]
    public void ProximityRisesAsTheCreatureApproachesAndWallsMuffleButDoNotSilence()
    {
        const float Near = 3f, Far = 22f, Wall = 0.6f;
        Assert.AreEqual(1f, AudioRules.Proximity(2f, Near, Far, false, Wall));
        Assert.AreEqual(0f, AudioRules.Proximity(30f, Near, Far, false, Wall));
        float prev = 2f;
        for (float d = 2f; d <= 24f; d += 1f)
        {
            float p = AudioRules.Proximity(d, Near, Far, false, Wall);
            Assert.LessOrEqual(p, prev + 1e-6f, $"tension must not rise as it moves away (d={d})");
            prev = p;
        }
        Assert.AreEqual(AudioRules.Proximity(10f, Near, Far, false, Wall) * Wall, AudioRules.Proximity(10f, Near, Far, true, Wall), 1e-5f);
        Assert.Greater(AudioRules.Proximity(10f, Near, Far, true, Wall), 0f, "audible behind a wall");
    }

    // A rise to a peak at index 4 then a slower fall, loudness 0..1.
    static readonly float[] Env = { 0.05f, 0.2f, 0.45f, 0.75f, 1f, 0.8f, 0.6f, 0.4f, 0.2f, 0.05f, 0f };

    [Test]
    public void RiserLevelMapsToThePeakOnTheWayUpAndTheFallOnTheWayDown()
    {
        Assert.AreEqual(0.4f, AudioRules.TimeForLevel(Env, 0.1f, 1f, true, 4f), 1e-5f, "full level on the way up is the peak");
        Assert.AreEqual(0.4f, AudioRules.TimeForLevel(Env, 0.1f, 1f, false, 4f), 1e-5f, "and on the way down");
        float up = AudioRules.TimeForLevel(Env, 0.1f, 0.5f, true, 4f);
        float down = AudioRules.TimeForLevel(Env, 0.1f, 0.5f, false, 4f);
        Assert.Less(up, 0.4f, "half level on the build is before the peak");
        Assert.Greater(down, 0.4f, "half level on the fall is after the peak");
        Assert.Greater(AudioRules.TimeForLevel(Env, 0.1f, 0.8f, true, 4f), AudioRules.TimeForLevel(Env, 0.1f, 0.3f, true, 4f), "louder is later on the build");
        Assert.Greater(AudioRules.TimeForLevel(Env, 0.1f, 0.2f, false, 4f), AudioRules.TimeForLevel(Env, 0.1f, 0.7f, false, 4f), "quieter is later on the fall");
        Assert.AreEqual(2.2f, AudioRules.TimeForLevel(null, 0.1f, 0.5f, true, 4.4f), 1e-5f, "no envelope: straight line to the peak");
    }

    [Test]
    public void RiserCutsOnlyWhenThePlayheadLeavesItsWindow()
    {
        // Wanted place 3.0 s, window 1.2 s (2.4..3.6), tolerance 0.5 s behind.
        Assert.IsFalse(AudioRules.CutTarget(2.9f, 3f, 1.2f, 0.5f, out _), "inside the window: keep playing");
        Assert.IsFalse(AudioRules.CutTarget(2.0f, 3f, 1.2f, 0.5f, out _), "slightly behind: still tolerated");
        Assert.IsTrue(AudioRules.CutTarget(1.0f, 3f, 1.2f, 0.5f, out float forward), "far behind: cut forward");
        Assert.AreEqual(2.4f, forward, 1e-5f);
        Assert.IsTrue(AudioRules.CutTarget(3.7f, 3f, 1.2f, 0.5f, out float back), "ran past the window (the peak loop): cut back");
        Assert.AreEqual(2.4f, back, 1e-5f);
        Assert.IsFalse(AudioRules.CutTarget(AudioRules.TimeForLevel(Env, 0.1f, 0.5f, true, 4f) + 0.1f, AudioRules.TimeForLevel(Env, 0.1f, 0.5f, true, 4f), 1.2f, 0.5f, out _));
        Assert.IsTrue(AudioRules.CutTarget(0f, 0f, 1.2f, 0.5f, out float zero) == false && zero == 0f, "wanting the very start never cuts below zero");
    }

    [Test]
    public void CrossfadeKeepsTotalPowerLevelAndNeverClicks()
    {
        for (float p = 0f; p <= 1f; p += 0.05f)
        {
            AudioRules.Crossfade(p, out float inc, out float outg);
            Assert.AreEqual(1f, inc * inc + outg * outg, 1e-4f, $"equal power at {p}");
        }
        AudioRules.Crossfade(0f, out float i0, out float o0);
        AudioRules.Crossfade(1f, out float i1, out float o1);
        Assert.AreEqual(0f, i0, 1e-5f);
        Assert.AreEqual(1f, o0, 1e-5f);
        Assert.AreEqual(1f, i1, 1e-5f);
        Assert.AreEqual(0f, o1, 1e-5f);
    }

    [Test]
    public void TensionOnlyWhenInTheSameRoomOrInSightAndItLingersWhenTheCreatureStepsOutOfView()
    {
        const float Linger = 3f, Dt = 0.1f;
        // Nearby through a wall, in a different room and out of sight: not engaged, however close.
        float r = AudioRules.EngageRemaining(false, 0f, Dt, Linger);
        Assert.AreEqual(0f, r);
        // Same room: engaged and refreshed every tick, even without a line of sight.
        r = AudioRules.EngageRemaining(true, r, Dt, Linger);
        Assert.AreEqual(Linger, r);
        // The creature goes out of sight but stays in the room: still engaged (the room flag keeps refreshing it).
        for (int i = 0; i < 50; i++) r = AudioRules.EngageRemaining(true, r, Dt, Linger);
        Assert.Greater(r, 0f);
        // It leaves the room and the player: engaged for the linger time, then it ends.
        int ticks = 0;
        while (r > 0f && ticks < 1000) { r = AudioRules.EngageRemaining(false, r, Dt, Linger); ticks++; }
        Assert.AreEqual(30, ticks, 1, "about the linger time (3 s at 10 Hz)");
        Assert.AreEqual(0f, r);
    }

    [Test]
    public void HeartbeatSpeedsUpAsTheCreatureGetsCloser()
    {
        Assert.AreEqual(1.4f, AudioRules.HeartbeatInterval(0f, 1.4f, 0.55f), 1e-5f);
        Assert.AreEqual(0.55f, AudioRules.HeartbeatInterval(1f, 1.4f, 0.55f), 1e-5f);
        Assert.Less(AudioRules.HeartbeatInterval(0.8f, 1.4f, 0.55f), AudioRules.HeartbeatInterval(0.3f, 1.4f, 0.55f));
        Assert.AreEqual(0.55f, AudioRules.HeartbeatInterval(5f, 1.4f, 0.55f), 1e-5f, "clamped");
    }

    [Test]
    public void LowStaminaHasADeadBandSoBreathingDoesNotFlicker()
    {
        Assert.IsFalse(AudioRules.LowStamina(false, 0.5f, false, 0.35f, 0.7f), "half stamina is fine");
        Assert.IsTrue(AudioRules.LowStamina(false, 0.3f, false, 0.35f, 0.7f), "getting close to empty");
        Assert.IsTrue(AudioRules.LowStamina(true, 0.5f, false, 0.35f, 0.7f), "still recovering: keep breathing hard");
        Assert.IsFalse(AudioRules.LowStamina(true, 0.7f, false, 0.35f, 0.7f), "recovered");
        Assert.IsTrue(AudioRules.LowStamina(false, 0.9f, true, 0.35f, 0.7f), "exhausted counts whatever the number says");
    }

    [Test]
    public void ExhaustionOutranksFear()
    {
        Assert.AreEqual(BreathKind.Run, AudioRules.DesiredBreath(true, true));
        Assert.AreEqual(BreathKind.Scared, AudioRules.DesiredBreath(false, true));
        Assert.AreEqual(BreathKind.None, AudioRules.DesiredBreath(false, false));
    }

    [Test]
    public void OnlyOneBreathingVoiceEverSoundsAndChangesGoThroughSilence()
    {
        var fader = new BreathFader();
        var script = new[] { BreathKind.Scared, BreathKind.Scared, BreathKind.Run, BreathKind.Run, BreathKind.Scared, BreathKind.None, BreathKind.Run };
        BreathKind previous = BreathKind.None;
        float previousGain = 0f;
        foreach (var desired in script)
            for (int i = 0; i < 80; i++) // 8 seconds per phase at 10 Hz
            {
                fader.Tick(desired, 0.1f, 0.9f, 0.4f);
                // A voice change happens only once the old voice has finished fading: the new one starts from silence.
                if (fader.Current != previous && previous != BreathKind.None && fader.Current != BreathKind.None)
                    Assert.AreEqual(0f, fader.Gain, 1e-5f, "new voice did not start from silence");
                Assert.That(fader.Gain, Is.InRange(0f, 1f));
                previous = fader.Current;
                previousGain = fader.Gain;
            }
        Assert.AreEqual(BreathKind.Run, fader.Current);
        Assert.AreEqual(1f, fader.Gain, 1e-5f, "settled at full gain");
    }

    [Test]
    public void BreathFaderFadesOutBeforeSwitchingAndClearsOnReset()
    {
        var f = new BreathFader();
        for (int i = 0; i < 30; i++) f.Tick(BreathKind.Scared, 0.1f, 0.9f, 0.4f);
        Assert.AreEqual(BreathKind.Scared, f.Current);
        f.Tick(BreathKind.Run, 0.1f, 0.9f, 0.4f);
        Assert.AreEqual(BreathKind.Scared, f.Current, "the old voice is still fading out");
        Assert.Less(f.Gain, 1f);
        for (int i = 0; i < 10; i++) f.Tick(BreathKind.Run, 0.1f, 0.9f, 0.4f);
        Assert.AreEqual(BreathKind.Run, f.Current);
        f.Clear();
        Assert.AreEqual(BreathKind.None, f.Current);
        Assert.AreEqual(0f, f.Gain);
    }

    [Test]
    public void RunBreathSpellsAreSpacedOutByACooldownButExhaustionOverridesIt()
    {
        const double Now = 100, Ready = 120;
        Assert.IsFalse(AudioRules.RunEpisode(false, true, false, Now, Ready), "a new spell during the cooldown does not start");
        Assert.IsTrue(AudioRules.RunEpisode(false, true, false, Ready + 1, Ready), "after the cooldown it may start");
        Assert.IsTrue(AudioRules.RunEpisode(true, true, false, Now, Ready), "a spell already running carries on");
        Assert.IsTrue(AudioRules.RunEpisode(false, true, true, Now, Ready), "exhaustion always counts");
        Assert.IsFalse(AudioRules.RunEpisode(true, false, false, Now, Ready), "no longer low: the spell ends");
    }
}
