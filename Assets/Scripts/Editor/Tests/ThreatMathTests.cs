using NUnit.Framework;

// EditMode tests for the local threat vignette maths (pure logic, no rendering).
public class ThreatMathTests
{
    const float Near = 3f, Far = 12f, Relief = 0.8f, Bonus = 0.35f, Look = 0.25f;

    static float P(float exposure, float distance, float facing = 1f, float cover = 0f, bool chase = false) =>
        ThreatMath.Pressure(exposure, distance, facing, cover, chase, Near, Far, Relief, Bonus, Look);

    [Test]
    public void NoViewMeansNoPressureWhateverElseIsTrue()
    {
        Assert.AreEqual(0f, P(0f, 1f, 1f, 0f, true), "behind a wall: not even a chase, not even point blank");
        Assert.AreEqual(0f, P(0f, 5f));
    }

    [Test]
    public void CloserFacingAndPoorerCoverRaisePressure()
    {
        Assert.Greater(P(1f, 3f), P(1f, 8f));
        Assert.Greater(P(1f, 8f), P(1f, 11f));
        Assert.AreEqual(0f, P(1f, 12f), 1e-5f, "beyond the far distance nothing");
        Assert.Greater(P(1f, 4f, 1f), P(1f, 4f, 0.3f));
        Assert.Greater(P(1f, 4f, 1f, 0f), P(1f, 4f, 1f, 0.65f), "poor cover > good cover, same conditions");
        Assert.Greater(P(1f, 4f, 1f, 0.65f), P(1f, 4f, 1f, 0.8f));
        Assert.Greater(P(1f, 4f), P(1f / 3f, 4f), "fewer exposed samples, less pressure");
    }

    [Test]
    public void ChaseAddsPressureButStaysBoundedAndNeedsAView()
    {
        Assert.Greater(P(1f, 4f, 1f, 0f, true), P(1f, 4f));
        Assert.LessOrEqual(P(1f, 0.5f, 1f, 0f, true), 1f);
        Assert.Greater(P(1f, 4f, 0f, 0f, true), 0f, "a chase does not need the creature to face any particular way");
        Assert.AreEqual(0f, P(0f, 4f, 1f, 0f, true));
    }

    [Test]
    public void FacingIsSmoothAndZeroOutsideTheFieldOfView()
    {
        Assert.AreEqual(1f, ThreatMath.Facing(1f, 110f), 1e-5f);
        Assert.AreEqual(0f, ThreatMath.Facing(-1f, 110f));
        Assert.AreEqual(0f, ThreatMath.Facing(0f, 110f), "side-on is outside a 110 degree field of view");
        Assert.Greater(ThreatMath.Facing(0.95f, 110f), ThreatMath.Facing(0.7f, 110f));
    }

    [Test]
    public void IntensityFadesSmoothlyNeverJumpsAndStaysInRange()
    {
        float v = 0f;
        for (int i = 0; i < 10; i++) v = ThreatMath.Approach(v, 1f, 0.1f, 1.2f, 0.7f);
        Assert.AreEqual(1f, v, 1e-5f, "full after about a second at 1.2 per second");
        float before = v;
        v = ThreatMath.Approach(v, 0f, 0.1f, 1.2f, 0.7f);
        Assert.AreEqual(before - 0.07f, v, 1e-5f, "fade out is gradual, one small step per frame");
        for (int i = 0; i < 30; i++) v = ThreatMath.Approach(v, 0f, 0.1f, 1.2f, 0.7f);
        Assert.AreEqual(0f, v);
        Assert.AreEqual(0f, ThreatMath.Approach(0f, 0f, 1f, 1f, 1f));
        Assert.AreEqual(0.5f, ThreatMath.Approach(0.5f, 1f, 0f, 1f, 1f), "no time, no change");
        Assert.AreEqual(1f, ThreatMath.Approach(0.9f, 5f, 10f, 1f, 1f), "target above range cannot overshoot");
    }

    [Test]
    public void HidingUnderAWatchingCreatureFeelsTenseAndAChaseAddsOnlyALittle()
    {
        // Shipped defaults: cover relief 0.25, chase bonus 0.1. Hidden (table cover 0.65), two of three samples exposed, 4 m, facing.
        float hidden = ThreatMath.Pressure(2f / 3f, 4f, 1f, 0.65f, false, Near, Far, 0.25f, 0.1f, Look);
        float exposed = ThreatMath.Pressure(2f / 3f, 4f, 1f, 0f, false, Near, Far, 0.25f, 0.1f, Look);
        float chased = ThreatMath.Pressure(2f / 3f, 4f, 1f, 0.65f, true, Near, Far, 0.25f, 0.1f, Look);
        Assert.GreaterOrEqual(hidden, 0.4f, "tension while hiding from a creature that looks threatening");
        Assert.Greater(exposed, hidden, "poorer cover is still more tense");
        Assert.Less(chased - hidden, 0.25f * hidden, "a chase is only a small extra");
    }

    [Test]
    public void ProximityDrivesItAndLookingAwayOnlyTrimsIt()
    {
        float facingYou = P(1f, 2f, 1f);
        float lookingAway = P(1f, 2f, 0f);
        Assert.Greater(lookingAway, facingYou * 0.7f, "a close creature facing the other way is still stressful");
        Assert.Less(lookingAway, facingYou, "but facing you is a little worse");
        Assert.Greater(P(1f, 2f, 0f), P(1f, 9f, 1f), "close and looking away beats far and staring");
        Assert.AreEqual(0f, P(0f, 2f, 0f), "still nothing without a view");
    }
}
