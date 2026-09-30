using System.Collections.Generic;

// Pure search/pursuit rules with no Unity types, so they can be unit tested (Editor/Tests/SearchRulesTests.cs).

public enum SearchPhase : byte { Local, Nearby, Done }

public static class SearchRules
{
    /// <summary>
    /// Local room search, then a few connected rooms, then done. Each phase has its own time cap, so the whole search is
    /// bounded by <c>local + nearby</c> whatever happens to individual points.
    /// </summary>
    public static SearchPhase Advance(SearchPhase phase, float secondsInPhase, float localSeconds, float nearbySeconds)
    {
        if (phase == SearchPhase.Local && secondsInPhase >= localSeconds) return SearchPhase.Nearby;
        if (phase == SearchPhase.Nearby && secondsInPhase >= nearbySeconds) return SearchPhase.Done;
        return phase;
    }

    /// <summary>A room/search has a limited budget of checks (of hiding places, of ordinary points).</summary>
    public static bool MayCheck(int checkedCount, int budget) => checkedCount < budget;

    /// <summary>
    /// Up to <paramref name="max"/> rooms in breadth-first order from <paramref name="start"/> (nearest connections first),
    /// skipping rooms already searched. Searched rooms are still walked through, so a search can move past them.
    /// Never returns the start room or a visited room, never repeats one.
    /// </summary>
    public static List<int> NearbyRooms(IList<int[]> adjacency, int start, int max, ICollection<int> visited)
    {
        var result = new List<int>();
        if (start < 0 || start >= adjacency.Count || max <= 0) return result;
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0 && result.Count < max)
        {
            int room = queue.Dequeue();
            foreach (int next in adjacency[room])
            {
                if (next < 0 || next >= adjacency.Count || !seen.Add(next)) continue;
                queue.Enqueue(next);
                if (!visited.Contains(next) && result.Count < max) result.Add(next);
            }
        }
        return result;
    }

    /// <summary>
    /// Pursuit (chase speed towards the last evidence) continues while fresh evidence keeps arriving: the clock is the time since
    /// the LAST fresh sighting or trail sound, never the time since the pursuit began.
    /// </summary>
    public static bool PursuitExpired(float secondsSinceFreshEvidence, float grace) => secondsSinceFreshEvidence >= grace;
}

/// <summary>What the creature has already searched this hunt: points and rooms by id. Cleared on a new sighting, calm and round reset.</summary>
public sealed class SearchMemory
{
    readonly HashSet<int> points = new();
    readonly HashSet<int> rooms = new();

    public ICollection<int> Rooms => rooms;
    public int PointCount => points.Count;

    public bool PointChecked(int id) => points.Contains(id);
    public void MarkPoint(int id) => points.Add(id);
    public bool RoomSearched(int id) => rooms.Contains(id);
    public void MarkRoom(int id) => rooms.Add(id);

    public void Clear()
    {
        points.Clear();
        rooms.Clear();
    }
}
