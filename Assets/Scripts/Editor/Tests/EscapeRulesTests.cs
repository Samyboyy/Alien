using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests (Window > General > Test Runner > EditMode). Pure logic only; no scene needed.
public class EscapeRulesTests
{
    [Test]
    public void SoundStrengthFallsWithEffectiveDistance()
    {
        Assert.AreEqual(1f, EscapeRules.SoundStrength(14f, 0f), 1e-4f, "full strength at the source");
        Assert.AreEqual(0.5f, EscapeRules.SoundStrength(14f, 7f), 1e-4f);
        Assert.AreEqual(0f, EscapeRules.SoundStrength(14f, 14f), 1e-4f, "nothing at the edge of the range");
        Assert.AreEqual(0f, EscapeRules.SoundStrength(14f, 30f), "never negative");
        Assert.AreEqual(0f, EscapeRules.SoundStrength(0f, 0f), "a sound without range is inaudible");
        // Crouching (2 m) is quiet but not silent; sprinting carries far further than walking.
        Assert.Greater(EscapeRules.SoundStrength(2f, 1f), 0f);
        Assert.Greater(EscapeRules.SoundStrength(14f, 9f), EscapeRules.SoundStrength(7f, 9f));
    }

    [Test]
    public void WallsAndDoorsMuffleButDoNotSilenceALoudSound()
    {
        float open = EscapeRules.EffectiveDistance(6f, 0, 0, 0, 6f, 3f, 1.5f);
        float behindDoor = EscapeRules.EffectiveDistance(6f, 0, 1, 0, 6f, 3f, 1.5f);
        float behindWall = EscapeRules.EffectiveDistance(6f, 1, 0, 0, 6f, 3f, 1.5f);
        Assert.AreEqual(6f, open, 1e-4f);
        Assert.AreEqual(9f, behindDoor, 1e-4f);
        Assert.AreEqual(12f, behindWall, 1e-4f);
        // A sprinting player (14 m) 6 m away behind a closed door is still heard, more weakly; behind a wall barely.
        float sprint = EscapeRules.SoundStrength(14f, behindDoor);
        Assert.Greater(sprint, 0.3f);
        Assert.Less(sprint, EscapeRules.SoundStrength(14f, open));
        Assert.Greater(EscapeRules.SoundStrength(14f, behindWall), 0f);
        // A walking player (7 m) behind a wall at the same spot is not.
        Assert.AreEqual(0f, EscapeRules.SoundStrength(7f, behindWall));
        // Weaker sounds give vaguer evidence.
        Assert.AreEqual(0f, EscapeRules.PositionError(1f, 4f), 1e-4f);
        Assert.AreEqual(4f, EscapeRules.PositionError(0f, 4f), 1e-4f);
        Assert.Greater(EscapeRules.PositionError(0.2f, 4f), EscapeRules.PositionError(0.8f, 4f));
    }

    [Test]
    public void SoundEventsAreHandledOnceAndExpire()
    {
        Assert.IsTrue(EscapeRules.IsFresh(11, 10, 0.1f, 1.5f));
        Assert.IsFalse(EscapeRules.IsFresh(10, 10, 0.1f, 1.5f), "already handled: cannot refresh pursuit again");
        Assert.IsFalse(EscapeRules.IsFresh(7, 10, 0.1f, 1.5f), "older than what was handled");
        Assert.IsFalse(EscapeRules.IsFresh(12, 10, 1.5f, 1.5f), "stale sounds are ignored");
        Assert.IsFalse(EscapeRules.IsFresh(12, 10, -0.1f, 1.5f), "not from the future");
    }

    [Test]
    public void EvidenceFadesOverTime()
    {
        Assert.AreEqual(1f, EscapeRules.EvidenceScore(1f, 0f, 8f), 1e-4f);
        Assert.AreEqual(0.5f, EscapeRules.EvidenceScore(1f, 4f, 8f), 1e-4f);
        Assert.AreEqual(0f, EscapeRules.EvidenceScore(1f, 8f, 8f), "fully forgotten");
        Assert.AreEqual(0f, EscapeRules.EvidenceScore(1f, 20f, 8f));
        Assert.AreEqual(0.25f, EscapeRules.EvidenceScore(0.5f, 4f, 8f), 1e-4f);
    }

    [Test]
    public void EvidencePriorityRules()
    {
        const float margin = 1.5f;
        // Nothing remembered (or fully faded): any audible sound is accepted, inaudible ones never.
        Assert.IsTrue(EscapeRules.AcceptNoise(0f, false, false, true, 0.2f, margin, true));
        Assert.IsFalse(EscapeRules.AcceptNoise(0f, false, false, true, 0f, margin, true));

        // A door noise must not overwrite a fresh player trail, however loud, but can once the trail has faded.
        Assert.IsFalse(EscapeRules.AcceptNoise(0.8f, true, false, true, 1f, margin, true), "door noise vs fresh trail");
        Assert.IsTrue(EscapeRules.AcceptNoise(0f, true, false, true, 0.3f, margin, true), "door noise after the trail faded");

        // The pursued player's next footstep always updates the trail, even weaker than the current evidence.
        Assert.IsTrue(EscapeRules.AcceptNoise(0.9f, true, true, false, 0.2f, margin, false));

        // Another player's sound must beat the current evidence by the margin, and respect the cooldown.
        Assert.IsFalse(EscapeRules.AcceptNoise(0.5f, true, false, false, 0.6f, margin, true), "slightly stronger: no flicker");
        Assert.IsTrue(EscapeRules.AcceptNoise(0.5f, true, false, false, 0.9f, margin, true), "clearly stronger: switch");
        Assert.IsFalse(EscapeRules.AcceptNoise(0.5f, true, false, false, 0.9f, margin, false), "switch cooldown still running");

        // An incidental sound can replace weak, non-player evidence if it is stronger by the margin.
        Assert.IsTrue(EscapeRules.AcceptNoise(0.3f, false, false, true, 0.6f, margin, true));
        Assert.IsFalse(EscapeRules.AcceptNoise(0.3f, false, false, true, 0.4f, margin, true));
    }

    [Test]
    public void BoardingKeepsArrivalOrderAndCapacity()
    {
        var order = new List<ulong>();
        EscapeRules.UpdateBoarding(order, new List<ulong> { 5 });
        EscapeRules.UpdateBoarding(order, new List<ulong> { 5, 2 });
        EscapeRules.UpdateBoarding(order, new List<ulong> { 2, 5, 9 });
        CollectionAssert.AreEqual(new ulong[] { 5, 2, 9 }, order);
        Assert.IsTrue(EscapeRules.HasSeat(order, 2, 2));
        Assert.IsFalse(EscapeRules.HasSeat(order, 9, 2), "third aboard a two-seat pod stays behind");

        EscapeRules.UpdateBoarding(order, new List<ulong> { 2, 9 }); // player 5 stepped out
        CollectionAssert.AreEqual(new ulong[] { 2, 9 }, order);
        Assert.IsTrue(EscapeRules.HasSeat(order, 9, 2));
        Assert.IsFalse(EscapeRules.HasSeat(order, 5, 2));
    }

    [Test]
    public void RequirementsNeedEveryFlagAndTheItem()
    {
        Assert.IsTrue(EscapeRules.Meets(ShipFlags.None, ShipFlags.None, ItemKind.None, ItemKind.Fuse));
        Assert.IsFalse(EscapeRules.Meets(ShipFlags.PowerRestored, ShipFlags.FuseInstalled, ItemKind.None, ItemKind.None));
        Assert.IsTrue(EscapeRules.Meets(ShipFlags.PowerRestored, ShipFlags.FuseInstalled | ShipFlags.PowerRestored, ItemKind.None, ItemKind.None));
        Assert.IsFalse(EscapeRules.Meets(ShipFlags.None, ShipFlags.None, ItemKind.Keycard, ItemKind.Fuse));
        Assert.IsTrue(EscapeRules.Meets(ShipFlags.None, ShipFlags.None, ItemKind.Keycard, ItemKind.Keycard));
    }

    [Test]
    public void RoundEndsWhenNobodyIsInPlayOrEveryPodHasGone()
    {
        Assert.IsFalse(EscapeRules.IsRoundOver(0, 0, 2, 0), "no participants: keep waiting");
        Assert.IsTrue(EscapeRules.IsRoundOver(2, 0, 2, 0), "everyone dead or escaped");
        Assert.IsFalse(EscapeRules.IsRoundOver(2, 1, 2, 1), "a pod is still available");
        Assert.IsTrue(EscapeRules.IsRoundOver(3, 1, 2, 2), "all pods gone: the rest are left behind");
        Assert.IsFalse(EscapeRules.IsRoundOver(2, 1, 0, 0), "levels without pods only end on deaths");
    }

    [Test]
    public void OnlyTheDoorBetweenUsAndTheDestinationBlocks()
    {
        // Corridor along X at z=0; door A in the north wall (plane z=2) leads to a room at z=6; door B further east, same wall.
        // Creature stopped at (0,0) against door A's carve; evidence is in A's room at (0,6). Normal = +Z (walking through).
        float a = EscapeRules.DoorDetour(0, 0, 0, 6, 0, 2, 0, 1, 4f);
        Assert.Greater(a, 0f, "door between us and the evidence blocks the route");

        Assert.AreEqual(-1f, EscapeRules.DoorDetour(0, 0, 0, -6, 0, 2, 0, 1, 4f), "evidence on our side of the door: not in the way");
        Assert.AreEqual(-1f, EscapeRules.DoorDetour(0, 0, 0, 6, 9, 2, 0, 1, 4f), "door too far from where the route stopped");

        // Door B sits 3 m along the wall: also plane z=2, so it is a candidate, but its detour is longer, so A wins.
        float b = EscapeRules.DoorDetour(0, 0, 0, 6, 3, 2, 0, 1, 4f);
        Assert.Greater(b, a, "the nearer door has the shorter detour");

        // A door in a perpendicular wall (plane x=-2, normal +X) with both points on its east side is unrelated.
        Assert.AreEqual(-1f, EscapeRules.DoorDetour(0, 0, 0, 6, -2, 0, 1, 0, 4f), "unrelated door beside us");
    }
}
