using NUnit.Framework;

// EditMode tests for the calm hunting choice of rooms (pure logic; the choice never involves any player position).
public class HuntRulesTests
{
    const float Cap = 240f, HeatCap = 3f, MaxM = 120f, SW = 1f, HW = 1.5f, DW = 0.4f;

    static float S(float since, float heat, float metres) => HuntRules.Score(since, heat, metres, Cap, HeatCap, MaxM, SW, HW, DW);

    [Test]
    public void RoomsNotVisitedForLongerAreMoreAttractive()
    {
        Assert.Greater(S(200f, 0f, 30f), S(20f, 0f, 30f));
        Assert.AreEqual(S(240f, 0f, 30f), S(900f, 0f, 30f), 1e-5f, "staleness is capped");
    }

    [Test]
    public void RoomsWherePlayersWereSeenOrHeardRankHigherAndFadeOverTime()
    {
        Assert.Greater(S(60f, 1.5f, 30f), S(60f, 0f, 30f));
        float fresh = 2f;
        float later = HuntRules.Decay(fresh, 90f, 90f);
        Assert.AreEqual(1f, later, 1e-4f, "half after one half-life");
        Assert.Less(HuntRules.Decay(fresh, 270f, 90f), later);
        Assert.AreEqual(fresh, HuntRules.Decay(fresh, 0f, 90f), 1e-5f);
        Assert.AreEqual(0f, HuntRules.Decay(fresh, 10f, 0f));
    }

    [Test]
    public void NearerRoomsAreSlightlyPreferredButFarStaleRoomsStillWin()
    {
        Assert.Greater(S(100f, 0f, 10f), S(100f, 0f, 90f));
        Assert.Greater(S(240f, 0f, 120f), S(20f, 0f, 5f), "a very stale far room beats a just-visited near one");
    }

    [Test]
    public void PickTakesTheBestEligibleRoomAndBreaksTiesByLowestIndex()
    {
        var scores = new[] { 0.2f, 0.9f, 0.9f, 1.4f };
        Assert.AreEqual(3, HuntRules.Pick(scores, new[] { true, true, true, true }));
        Assert.AreEqual(1, HuntRules.Pick(scores, new[] { true, true, true, false }), "a tie goes to the lower index");
        Assert.AreEqual(-1, HuntRules.Pick(scores, new[] { false, false, false, false }), "nothing reachable: no plan");
    }
}
