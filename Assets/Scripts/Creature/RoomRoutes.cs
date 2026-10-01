using System.Collections.Generic;

// Pure room-connection rules with no Unity types, so they can be unit tested (Editor/Tests/RoomRoutesTests.cs).

/// <summary>
/// Room graph queries over explicit connections (RoomLink). Everything is deterministic: ties go to the lower link id or
/// room index, so the same situation always gives the same answer. A link that is not <c>allowed</c> (a reinforced door the creature
/// cannot open) or is in the avoid set is never used.
/// </summary>
public static class RoomRoutes
{
    public struct Link
    {
        public int id, a, b;
        public bool allowed;

        public int Other(int room) => room == a ? b : a;
        public bool Touches(int room) => room == a || room == b;
    }

    /// <summary>Link ids from <paramref name="from"/> to <paramref name="to"/>, fewest hops first. Empty when equal, null when unreachable.</summary>
    public static List<int> Path(IList<Link> links, int roomCount, int from, int to, ICollection<int> avoidLinks)
    {
        if (from < 0 || to < 0 || from >= roomCount || to >= roomCount) return null;
        if (from == to) return new List<int>();
        var ordered = Ordered(links);
        var viaLink = new int[roomCount];
        var viaRoom = new int[roomCount];
        var seen = new bool[roomCount];
        var queue = new Queue<int>();
        seen[from] = true;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            foreach (var l in ordered)
            {
                if (!Usable(l, avoidLinks) || !l.Touches(room)) continue;
                int next = l.Other(room);
                if (next < 0 || next >= roomCount || seen[next]) continue;
                seen[next] = true;
                viaLink[next] = l.id;
                viaRoom[next] = room;
                if (next == to) return Unwind(viaLink, viaRoom, from, to);
                queue.Enqueue(next);
            }
        }
        return null;
    }

    /// <summary>
    /// The next room to search: not searched yet, within <paramref name="maxHops"/> links, nearest first, then closest to the
    /// evidence, then lowest index. Searched rooms are walked through, not stopped at. -1 when there is none.
    /// </summary>
    public static int NextRoom(IList<Link> links, int roomCount, int from, ICollection<int> searchedRooms,
        IList<float> distanceToEvidence, int maxHops, ICollection<int> avoidLinks)
    {
        if (from < 0 || from >= roomCount || maxHops <= 0) return -1;
        var ordered = Ordered(links);
        var hops = new int[roomCount];
        for (int i = 0; i < roomCount; i++) hops[i] = -1;
        hops[from] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            if (hops[room] >= maxHops) continue;
            foreach (var l in ordered)
            {
                if (!Usable(l, avoidLinks) || !l.Touches(room)) continue;
                int next = l.Other(room);
                if (next < 0 || next >= roomCount || hops[next] >= 0) continue;
                hops[next] = hops[room] + 1;
                queue.Enqueue(next);
            }
        }
        int best = -1;
        for (int r = 0; r < roomCount; r++)
        {
            if (r == from || hops[r] < 0 || searchedRooms.Contains(r)) continue;
            if (best < 0 || hops[r] < hops[best]) { best = r; continue; }
            if (hops[r] > hops[best]) continue;
            float dr = distanceToEvidence != null && r < distanceToEvidence.Count ? distanceToEvidence[r] : 0f;
            float db = distanceToEvidence != null && best < distanceToEvidence.Count ? distanceToEvidence[best] : 0f;
            if (dr < db) best = r; // equal: the lower index (already best) stays
        }
        return best;
    }

    static bool Usable(Link l, ICollection<int> avoid) => l.allowed && (avoid == null || !avoid.Contains(l.id));

    static List<Link> Ordered(IList<Link> links)
    {
        var list = new List<Link>(links);
        list.Sort((x, y) => x.id.CompareTo(y.id));
        return list;
    }

    static List<int> Unwind(int[] viaLink, int[] viaRoom, int from, int to)
    {
        var path = new List<int>();
        for (int room = to; room != from; room = viaRoom[room]) path.Add(viaLink[room]);
        path.Reverse();
        return path;
    }
}
