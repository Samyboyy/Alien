using System.Collections.Generic;
using NUnit.Framework;

// EditMode tests for room connections and door-aware exploration order (pure logic, no scene).
public class RoomRoutesTests
{
    // 0 - 1 - 2 - 3 in a line, plus a shortcut 0 - 4 - 3 and a reinforced link 1 - 4.
    static List<RoomRoutes.Link> Graph(bool reinforcedAllowed = false) => new()
    {
        new RoomRoutes.Link { id = 10, a = 0, b = 1, allowed = true },
        new RoomRoutes.Link { id = 11, a = 1, b = 2, allowed = true },
        new RoomRoutes.Link { id = 12, a = 2, b = 3, allowed = true },
        new RoomRoutes.Link { id = 13, a = 0, b = 4, allowed = true },
        new RoomRoutes.Link { id = 14, a = 4, b = 3, allowed = true },
        new RoomRoutes.Link { id = 15, a = 1, b = 4, allowed = reinforcedAllowed },
    };

    [Test]
    public void PathUsesFewestLinksAndIsOrderedFromTheStart()
    {
        var path = RoomRoutes.Path(Graph(), 5, 0, 3, null);
        CollectionAssert.AreEqual(new[] { 13, 14 }, path, "0-4-3 is two links, 0-1-2-3 is three");
        CollectionAssert.AreEqual(new[] { 10, 11 }, RoomRoutes.Path(Graph(), 5, 0, 2, null));
        Assert.AreEqual(0, RoomRoutes.Path(Graph(), 5, 2, 2, null).Count, "already there");
    }

    [Test]
    public void ReinforcedLinksAreNeverUsed()
    {
        // Without the reinforced link 1-4, room 1 can only be reached through 0-1.
        CollectionAssert.AreEqual(new[] { 10 }, RoomRoutes.Path(Graph(false), 5, 0, 1, null));
        // Block 0-1 as well: 1 is then only reachable through the reinforced 1-4, which is refused, or the long way round.
        var path = RoomRoutes.Path(Graph(false), 5, 0, 1, new HashSet<int> { 10 });
        CollectionAssert.AreEqual(new[] { 13, 14, 12, 11 }, path, "the long way round, never through link 15");
        var onlyReinforced = new List<RoomRoutes.Link> { new() { id = 1, a = 0, b = 1, allowed = false } };
        Assert.IsNull(RoomRoutes.Path(onlyReinforced, 2, 0, 1, null), "a room behind a reinforced door is unreachable");
        Assert.AreEqual(-1, RoomRoutes.NextRoom(onlyReinforced, 2, 0, new HashSet<int> { 0 }, null, 3, null));
    }

    [Test]
    public void DisconnectedRoomsHaveNoRoute()
    {
        var links = new List<RoomRoutes.Link> { new() { id = 1, a = 0, b = 1, allowed = true } };
        Assert.IsNull(RoomRoutes.Path(links, 4, 0, 3, null));
        Assert.AreEqual(-1, RoomRoutes.NextRoom(links, 4, 3, new HashSet<int>(), null, 5, null));
    }

    [Test]
    public void NextRoomIsNearestFirstThenClosestToEvidenceThenLowestIndexAndNeverRevisits()
    {
        var searched = new HashSet<int> { 0 };
        var dist = new float[] { 0f, 9f, 5f, 7f, 5f };
        // From 0: rooms 1 and 4 are one link away; 4 is closer to the evidence.
        Assert.AreEqual(4, RoomRoutes.NextRoom(Graph(), 5, 0, searched, dist, 3, null));
        searched.Add(4);
        Assert.AreEqual(1, RoomRoutes.NextRoom(Graph(), 5, 0, searched, dist, 3, null), "4 is done, so the other neighbour");
        searched.Add(1);
        // 2 and 3 are both two links away (via 1 or 4): the closer to the evidence wins; searched rooms are walked through.
        Assert.AreEqual(2, RoomRoutes.NextRoom(Graph(), 5, 0, searched, dist, 3, null));
        searched.Add(2);
        Assert.AreEqual(3, RoomRoutes.NextRoom(Graph(), 5, 0, searched, dist, 3, null));
        searched.Add(3);
        Assert.AreEqual(-1, RoomRoutes.NextRoom(Graph(), 5, 0, searched, dist, 3, null), "everything searched: the search ends");
    }

    [Test]
    public void NextRoomIsBoundedByHopsAndBreaksTiesByIndex()
    {
        var chain = new List<RoomRoutes.Link>
        {
            new() { id = 1, a = 0, b = 1, allowed = true }, new() { id = 2, a = 1, b = 2, allowed = true }, new() { id = 3, a = 2, b = 3, allowed = true },
        };
        var searched = new HashSet<int> { 0, 1, 2 };
        Assert.AreEqual(-1, RoomRoutes.NextRoom(chain, 4, 0, searched, null, 2, null), "room 3 is three links away");
        Assert.AreEqual(3, RoomRoutes.NextRoom(chain, 4, 0, searched, null, 3, null));
        // Two equally near, equally far from the evidence: the lower room index.
        var fork = new List<RoomRoutes.Link> { new() { id = 5, a = 0, b = 2, allowed = true }, new() { id = 4, a = 0, b = 1, allowed = true } };
        Assert.AreEqual(1, RoomRoutes.NextRoom(fork, 3, 0, new HashSet<int> { 0 }, new float[] { 0, 3, 3 }, 2, null));
    }

    [Test]
    public void FailedLinksAreAvoidedForTheRestOfTheSearchAndForgottenOnReset()
    {
        var memory = new SearchMemory();
        memory.MarkLinkFailed(13);
        var path = RoomRoutes.Path(Graph(), 5, 0, 3, memory.FailedLinks);
        CollectionAssert.AreEqual(new[] { 10, 11, 12 }, path, "route around the failed link");
        memory.MarkRoom(7);
        memory.Clear();
        Assert.IsFalse(memory.LinkFailed(13));
        Assert.AreEqual(0, memory.FailedLinks.Count);
        Assert.IsFalse(memory.RoomSearched(7));
        CollectionAssert.AreEqual(new[] { 13, 14 }, RoomRoutes.Path(Graph(), 5, 0, 3, memory.FailedLinks));
    }

    [Test]
    public void TheSameSituationAlwaysGivesTheSameAnswer()
    {
        var shuffled = Graph();
        shuffled.Reverse();
        CollectionAssert.AreEqual(RoomRoutes.Path(Graph(), 5, 0, 3, null), RoomRoutes.Path(shuffled, 5, 0, 3, null), "link order in the list does not matter");
        Assert.AreEqual(RoomRoutes.NextRoom(Graph(), 5, 0, new HashSet<int> { 0 }, null, 3, null), RoomRoutes.NextRoom(shuffled, 5, 0, new HashSet<int> { 0 }, null, 3, null));
    }
}
