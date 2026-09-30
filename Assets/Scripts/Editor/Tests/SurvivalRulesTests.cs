using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for stamina and search/pursuit rules (pure logic, no scene needed).
public class SurvivalRulesTests
{
    // Same numbers as the controller's defaults: 5 s of sprint, 2 s delay, 8 s full recovery, resume at 25%.
    const float Drain = 1f / 5f, Delay = 2f, Regen = 1f / 8f, Resume = 0.25f, Dt = 0.05f;

    static void Run(StaminaModel s, float seconds, bool sprinting)
    {
        for (float t = 0f; t < seconds - 1e-4f; t += Dt) s.Tick(Dt, sprinting, Drain, Delay, Regen, Resume);
    }

    [Test]
    public void StaminaSupportsAboutFiveSecondsOfSprintThenExhausts()
    {
        var s = new StaminaModel();
        Run(s, 4.5f, true);
        Assert.IsTrue(s.CanSprint, "still sprinting at 4.5 s");
        Assert.Greater(s.Value, 0f);
        Run(s, 0.7f, true);
        Assert.IsTrue(s.Exhausted);
        Assert.IsFalse(s.CanSprint);
        Assert.AreEqual(0f, s.Value);
    }

    [Test]
    public void WalkingOrStandingNeverDrains()
    {
        var s = new StaminaModel();
        Run(s, 30f, false);
        Assert.AreEqual(1f, s.Value, 1e-4f);
        Assert.IsFalse(s.Exhausted);
    }

    [Test]
    public void RecoveryWaitsForTheDelayThenRefillsAndExhaustionNeedsAQuarter()
    {
        var s = new StaminaModel();
        Run(s, 6f, true); // exhausted
        Assert.IsTrue(s.Exhausted);

        Run(s, 1.9f, false);
        Assert.AreEqual(0f, s.Value, "no regeneration during the 2 s delay");
        Run(s, 1.0f, false); // 0.9 s of regeneration: about 11%
        Assert.IsTrue(s.Exhausted, "below 25% sprint stays blocked");
        Run(s, 1.5f, false); // now past 25%
        Assert.IsFalse(s.Exhausted, "resumes once a quarter has recovered");
        Assert.IsTrue(s.CanSprint);

        Run(s, 9f, false); // well past 8 s of regeneration
        Assert.AreEqual(1f, s.Value, 1e-4f);
    }

    [Test]
    public void TappingSprintCannotBeatTheRegenerationDelay()
    {
        var s = new StaminaModel();
        float before = s.Value;
        // A quarter second of sprint, then 1.5 s of rest, repeated: the rest is always shorter than the 2 s delay.
        for (int i = 0; i < 12; i++)
        {
            Run(s, 0.25f, true);
            Run(s, 1.5f, false);
            Assert.LessOrEqual(s.Value, before + 1e-4f, "tapping never regenerates");
            before = s.Value;
        }
        Assert.Less(s.Value, 0.5f, "tapping only wastes stamina");
    }

    [Test]
    public void ExhaustedPlayerCannotRestartSprintNearZero()
    {
        var s = new StaminaModel();
        Run(s, 6f, true);
        Run(s, 2.4f, false); // past the delay: a few percent back
        Assert.Greater(s.Value, 0f);
        Assert.IsFalse(s.CanSprint, "a little stamina is not enough while exhausted");
    }

    [Test]
    public void ResetRestoresFullStaminaAndClearsExhaustion()
    {
        var s = new StaminaModel();
        Run(s, 6f, true);
        Assert.IsTrue(s.Exhausted);
        s.Reset();
        Assert.AreEqual(1f, s.Value);
        Assert.IsFalse(s.Exhausted);
        Assert.IsTrue(s.CanSprint);
        Run(s, 1f, false);
        Assert.AreEqual(1f, s.Value, "stays full after reset");
    }

    [Test]
    public void FreshEvidenceSustainsPursuitIndefinitelyButSilenceEndsIt()
    {
        const float grace = 2.5f;
        float sinceFresh = 0f;
        // One audible footstep every 0.4 s for a full minute: the pursuit never expires, however long it has been going on.
        for (int tick = 0; tick < 1200; tick++) // 60 s at 0.05 s per tick
        {
            sinceFresh += 0.05f;
            if (tick % 8 == 0) sinceFresh = 0f; // a footstep every 0.4 s
            Assert.IsFalse(SearchRules.PursuitExpired(sinceFresh, grace), $"expired at {tick * 0.05f:0.0}s although evidence kept arriving");
        }
        // The player goes quiet: pursuit ends after exactly the grace period.
        Assert.IsFalse(SearchRules.PursuitExpired(2.4f, grace));
        Assert.IsTrue(SearchRules.PursuitExpired(2.5f, grace));
    }

    [Test]
    public void SearchIsBoundedByItsPhaseTimers()
    {
        const float local = 20f, nearby = 38f;
        var phase = SearchPhase.Local;
        float inPhase = 0f, total = 0f;
        int steps = 0;
        while (phase != SearchPhase.Done && steps++ < 100000)
        {
            inPhase += 0.1f;
            total += 0.1f;
            var next = SearchRules.Advance(phase, inPhase, local, nearby);
            if (next != phase) { phase = next; inPhase = 0f; }
        }
        Assert.AreEqual(SearchPhase.Done, phase);
        Assert.LessOrEqual(total, local + nearby + 0.3f, "the whole search cannot outlast local + nearby");
        Assert.AreEqual(SearchPhase.Done, SearchRules.Advance(SearchPhase.Done, 999f, local, nearby), "done stays done");
    }

    [Test]
    public void RoomBudgetsLimitHowManyPlacesAreChecked()
    {
        int points = 0, hiding = 0;
        while (SearchRules.MayCheck(points, 4)) points++;
        while (SearchRules.MayCheck(hiding, 2)) hiding++;
        Assert.AreEqual(4, points);
        Assert.AreEqual(2, hiding);
        Assert.IsFalse(SearchRules.MayCheck(0, 0), "a zero budget checks nothing");
    }

    [Test]
    public void NearbyRoomsAreBreadthFirstWithoutRevisitsAndPassThroughSearchedRooms()
    {
        // 0 - 1 - 2 - 3, plus 1 - 4 and a loop 3 - 4.
        var adjacency = new List<int[]> { new[] { 1 }, new[] { 0, 2, 4 }, new[] { 1, 3 }, new[] { 2, 4 }, new[] { 1, 3 } };
        var none = new HashSet<int>();

        var order = SearchRules.NearbyRooms(adjacency, 0, 10, none);
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 3 }, order, "nearest connections first, every room once, start excluded");

        Assert.AreEqual(2, SearchRules.NearbyRooms(adjacency, 0, 2, none).Count, "bounded by max");
        Assert.AreEqual(0, SearchRules.NearbyRooms(adjacency, 0, 0, none).Count);

        // Room 1 already searched: it is skipped, but the search still moves on through it to reach 2 and 4.
        var searched = new HashSet<int> { 1 };
        CollectionAssert.AreEqual(new[] { 2, 4, 3 }, SearchRules.NearbyRooms(adjacency, 0, 10, searched));

        // A room with no connections yields nothing; a bad start index is handled.
        Assert.AreEqual(0, SearchRules.NearbyRooms(new List<int[]> { new int[0] }, 0, 3, none).Count);
        Assert.AreEqual(0, SearchRules.NearbyRooms(adjacency, -1, 3, none).Count);
    }

    [Test]
    public void SearchMemoryClearsOnReset()
    {
        var m = new SearchMemory();
        m.MarkPoint(10);
        m.MarkPoint(11);
        m.MarkRoom(3);
        Assert.IsTrue(m.PointChecked(10));
        Assert.IsTrue(m.RoomSearched(3));
        Assert.AreEqual(2, m.PointCount);
        m.Clear();
        Assert.IsFalse(m.PointChecked(10));
        Assert.IsFalse(m.RoomSearched(3));
        Assert.AreEqual(0, m.PointCount);
    }
}
