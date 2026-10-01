using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Deliberate exploration through doors (host only). The search chooses its next room from explicit room connections (RoomLink),
/// walks to the connection's own door, opens that door (never a nearby one that merely happens to be in the way), waits until the
/// way is navigable, then crosses into the room and searches it with the existing search-point and hiding-place system.
/// A connection that cannot be used (door refused or re-closed too often, blocked, too slow) is remembered as failed for the rest
/// of that search and another room is chosen. Fresh sight or a sound that moves the evidence clears all of this, as before.
/// Patrol can occasionally explore through an ordinary door the same way, rarely and not into rooms it visited recently.
/// </summary>
public partial class CreatureAI
{
    [Header("Room exploration")]
    [Tooltip("How many connections away a search may look for its next room")] public int searchHops = 3;
    [Tooltip("Stand this far from a door, on the side we come from, to open it (m)")] public float doorApproachOffset = 1.4f;
    [Tooltip("Give up on one room connection after this long (door refused or re-closed, blocked, too slow)")] public float transitTimeout = 30f;
    [Tooltip("Times it will try to open one door on a connection before giving up on it")] public int maxDoorAttempts = 3;

    [Header("Hunting (calm patrol is not idle: it goes looking)")]
    [Tooltip("Chance, each time patrol finishes waiting at a point, to go and search another room instead (through its doors or by vent)")] [Range(0f, 1f)] public float huntPlanChance = 0.75f;
    [Tooltip("Minimum seconds between hunting plans")] public float huntPlanCooldown = 10f;
    [Tooltip("A room not visited for this long counts as fully stale (s)")] public float huntStaleSeconds = 240f;
    [Tooltip("Where players were seen or heard fades by half over this long (s)")] public float huntHeatHalfLife = 90f;
    [Tooltip("Weight of staleness, heat and distance when choosing the next room")] public float huntStaleWeight = 1f;
    public float huntHeatWeight = 1.5f;
    public float huntDistanceWeight = 0.4f;

    readonly List<RoomLink> hops = new();
    readonly Dictionary<int, double> recentRooms = new();
    readonly Dictionary<int, (float heat, double time)> roomHeat = new(); // where it recently saw or heard players (evidence only)
    RoomVolume transitTarget, hopFrom;
    int hopIndex, doorIndex, doorAttempts;
    bool hopForward, doorGoalSet, crossGoalSet;
    float transitTimer;
    double nextExploreAt;
    string transitNote = "-";
    System.Random aiRng = new(1); // reseeded every round by RoundManager's seed

    // ---------- Starting a transit ----------

    // Next room for the search, from the explicit connections. False when none are authored (the old neighbour logic is used then).
    bool TryLinkedNextRoom()
    {
        if (RoomLink.All.Count == 0) return false;
        var all = RoomVolume.All;
        var from = currentRoom ?? NearestRoom(searchCenter, 25f);
        int start = all.IndexOf(from);
        if (start < 0) { FinishSearch("no connected rooms"); return true; }

        var links = BuildLinkList(out var sources);

        var searched = new HashSet<int>();
        var distance = new float[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            if (memory.RoomSearched(all[i].GetInstanceID())) searched.Add(i);
            distance[i] = all[i].DistanceTo(evidencePos);
        }
        int target = RoomRoutes.NextRoom(links, all.Count, start, searched, distance, searchHops, memory.FailedLinks);
        var path = target < 0 ? null : RoomRoutes.Path(links, all.Count, start, target, memory.FailedLinks);
        if (path == null || path.Count == 0) { FinishSearch("no unsearched connected rooms"); return true; }

        hops.Clear();
        foreach (int id in path) hops.Add(sources.Find(l => l.id == id));
        roomsVisited++;
        hopFrom = from;
        BeginTransit(all[target]);
        return true;
    }

    // The explicit connections as plain data for the pure routing rules, in id order. `allowed` is read now, so a door that was
    // made reinforced in the Inspector is respected.
    List<RoomRoutes.Link> BuildLinkList(out List<RoomLink> sources)
    {
        var all = RoomVolume.All;
        sources = new List<RoomLink>(RoomLink.All);
        sources.Sort((x, y) => x.id.CompareTo(y.id));
        var links = new List<RoomRoutes.Link>(sources.Count);
        foreach (var l in sources)
            links.Add(new RoomRoutes.Link { id = l.id, a = all.IndexOf(l.roomA), b = all.IndexOf(l.roomB), allowed = l.Allowed });
        return links;
    }

    void BeginTransit(RoomVolume target)
    {
        transitTarget = target;
        hopIndex = 0;
        transitTimer = 0f;
        step = SearchStep.Transit;
        StartHop();
        reason = $"going to {target.roomName}";
    }

    void StartHop()
    {
        var link = hops[hopIndex];
        hopForward = link.roomA == hopFrom;
        doorIndex = 0;
        doorAttempts = 0;
        doorGoalSet = crossGoalSet = false;
    }

    SlidingDoor DoorAt(RoomLink link, int i) => link.doors[hopForward ? i : link.doors.Length - 1 - i];

    // ---------- Following the transit ----------

    void UpdateTransit()
    {
        if (hops.Count == 0 || hopIndex >= hops.Count) { step = SearchStep.Travel; NextTarget(); return; }
        if ((transitTimer += Time.deltaTime) > transitTimeout) { FailTransit("took too long"); return; }
        var link = hops[hopIndex];
        if (!link.Allowed) { FailTransit("connection no longer allowed"); return; }

        // Doors, in order. Each is approached on the side we come from, and opened only if it is not already open.
        while (doorIndex < link.doors.Length)
        {
            var door = DoorAt(link, doorIndex);
            if (door == null || DoorPassable(door)) { doorIndex++; doorGoalSet = false; continue; }

            Vector3 reference = doorIndex == 0 ? (hopForward ? link.pointA : link.pointB).position : DoorAt(link, doorIndex - 1).ClosedPosition;
            if (!doorGoalSet)
            {
                GoTo(DoorApproach(door, reference));
                doorGoalSet = true;
                transitNote = $"{link.name}: to {door.name}";
                return;
            }
            if (agent.pathPending) return;
            if (!Arrived()) return;
            if (doorAttempts >= maxDoorAttempts) { FailTransit($"{door.name} would not stay open"); return; }
            doorAttempts++;
            doorGoalSet = false;
            transitNote = $"{link.name}: opening {door.name} ({doorAttempts}/{maxDoorAttempts})";
            BeginBash(door); // the wait-and-force-open step; resumes here once it is open and the NavMesh agrees
            return;
        }

        // Every door on this connection is open: cross to the far side.
        Vector3 far = (hopForward ? link.pointB : link.pointA).position;
        if (!crossGoalSet)
        {
            GoTo(far);
            crossGoalSet = true;
            transitNote = $"{link.name}: crossing";
            return;
        }
        if (agent.pathPending || !Arrived()) return;
        if (agent.pathStatus == NavMeshPathStatus.PathComplete) { HopDone(link); return; }
        if (TryOpenDoor()) return; // a door we did not expect on the way: the old heuristic handles surprises
        FailTransit("route blocked");
    }

    static bool DoorPassable(SlidingDoor d) => d.IsOpen && d.OpenFraction >= 0.6f;

    // A point just in front of the door on the given reference side, on the NavMesh.
    Vector3 DoorApproach(SlidingDoor door, Vector3 reference)
    {
        Vector3 n = Flat(door.Normal).normalized;
        float side = Vector3.Dot(Flat(reference - door.ClosedPosition), n) >= 0f ? 1f : -1f;
        Vector3 p = door.ClosedPosition + n * (side * doorApproachOffset);
        p.y = door.ClosedPosition.y - door.transform.lossyScale.y * 0.5f; // down to the floor
        return NavMesh.SamplePosition(p, out var hit, 2f, NavMesh.AllAreas) ? hit.position : p;
    }

    void HopDone(RoomLink link)
    {
        hopFrom = link.Other(hopFrom);
        hopIndex++;
        if (hopIndex < hops.Count) { StartHop(); return; }
        // Arrived: search the room with the usual points and hiding places.
        step = SearchStep.Travel;
        transitNote = $"arrived in {transitTarget.roomName}";
        var room = transitTarget;
        hops.Clear();
        EnterRoom(room);
        reason = $"searching {room.roomName}";
        NextTarget();
    }

    // This connection cannot be used right now: remember that for the rest of the search and pick another room.
    void FailTransit(string why)
    {
        if (hopIndex < hops.Count) memory.MarkLinkFailed(hops[hopIndex].id);
        transitNote = $"failed: {why}";
        hops.Clear();
        step = SearchStep.Travel;
        roomsVisited = Mathf.Max(0, roomsVisited - 1);
        currentRoom = RoomAt(transform.position) ?? hopFrom ?? currentRoom;
        NextNearbyRoom();
    }

    void ClearTransit()
    {
        hops.Clear();
        transitTarget = null;
        transitNote = "-";
    }

    // ---------- Hunting ----------

    void NoteRoomVisit(RoomVolume room)
    {
        if (room != null) recentRooms[room.GetInstanceID()] = Time.timeAsDouble;
    }

    // Player-caused evidence (a sighting or a sound a player made) makes the room it happened in a little more interesting for a while.
    // This is only what the creature itself saw or heard, never where anyone is now.
    void AddHeat(Vector3 position, float amount)
    {
        var room = RoomAt(position) ?? NearestRoom(position, 6f);
        if (room == null) return;
        int id = room.GetInstanceID();
        double now = Time.timeAsDouble;
        float current = roomHeat.TryGetValue(id, out var h) ? HuntRules.Decay(h.heat, (float)(now - h.time), huntHeatHalfLife) : 0f;
        roomHeat[id] = (Mathf.Min(3f, current + amount), now);
    }

    float HeatOf(RoomVolume room, double now) =>
        roomHeat.TryGetValue(room.GetInstanceID(), out var h) ? HuntRules.Decay(h.heat, (float)(now - h.time), huntHeatHalfLife) : 0f;

    void ResetHunting()
    {
        recentRooms.Clear();
        roomHeat.Clear();
        nextExploreAt = Time.timeAsDouble + huntPlanCooldown * 0.5;
    }

    // Called when patrol has finished waiting at a point. Even with no evidence the creature is hunting: it picks the room it has not
    // looked in for longest (and where players were last seen or heard), preferring the nearer. It then goes there through that
    // room's doors (opening them as needed), or by vent when that is clearly quicker, and searches it.
    bool TryHunt()
    {
        double now = Time.timeAsDouble;
        if (now < nextExploreAt || RoomLink.All.Count == 0 || aiRng.NextDouble() >= huntPlanChance) return false;
        var all = RoomVolume.All;
        var here = RoomAt(transform.position) ?? NearestRoom(transform.position, 40f);
        int start = all.IndexOf(here);
        if (start < 0) return false;
        NoteRoomVisit(here);

        var links = BuildLinkList(out var sources);
        var scores = new float[all.Count];
        var eligible = new bool[all.Count];
        var paths = new List<int>[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            if (i == start) continue;
            paths[i] = RoomRoutes.Path(links, all.Count, start, i, null);
            if (paths[i] == null || paths[i].Count == 0) continue;
            float since = recentRooms.TryGetValue(all[i].GetInstanceID(), out double seen) ? (float)(now - seen) : huntStaleSeconds;
            float metres = Vector3.Distance(transform.position, all[i].transform.position);
            scores[i] = HuntRules.Score(since, HeatOf(all[i], now), metres, huntStaleSeconds, 3f, 120f, huntStaleWeight, huntHeatWeight, huntDistanceWeight * (1f - Frustration())); // frustrated: far rooms are fine
            eligible[i] = true;
        }
        int best = HuntRules.Pick(scores, eligible);
        if (best < 0) return false;

        nextExploreAt = now + huntPlanCooldown;
        var room = all[best];
        string why = $"hunting: {room.roomName} (not visited for {(recentRooms.TryGetValue(room.GetInstanceID(), out double s) ? now - s : huntStaleSeconds):0}s, heat {HeatOf(room, now):0.0})";

        // A long way off and clearly quicker by vent? Then vent; it will search the room it comes out in.
        Vector3 aim = room.searchPoints.Length > 0 && room.searchPoints[0] != null ? room.searchPoints[0].position : room.transform.position; // on the NavMesh
        if (ConsiderVent(why, CreatureState.Patrol, aim, "room staleness and heat, no player position")) return true;

        var route = new List<RoomLink>();
        foreach (int id in paths[best]) route.Add(sources.Find(l => l.id == id));
        BeginHunt(route, here, room, why);
        return true;
    }

    // A one-room search at the end of a door route: the usual search phase machinery, no player evidence involved.
    void BeginHunt(List<RoomLink> route, RoomVolume from, RoomVolume target, string why)
    {
        ClearSearch();
        state.Value = CreatureState.Search;
        reason = why;
        targetId.Value = ulong.MaxValue;
        this.target = null;
        bashDoor = null;
        chaseInspectSpot = null;
        agent.speed = searchSpeed;
        agent.stoppingDistance = 0.4f;
        evidencePos = target.searchPoints.Length > 0 && target.searchPoints[0] != null ? target.searchPoints[0].position : target.transform.position; // only used to order points; there is no evidence kind
        arrived = true;
        searchCenter = transform.position;
        searchPhase = SearchPhase.Nearby;
        phaseTime = 0f;
        roomsVisited = nearbyRooms; // exactly this one room, then back to patrol
        currentRoom = from;
        hops.Clear();
        hops.AddRange(route);
        hopFrom = from;
        BeginTransit(target);
        reason = why;
    }

    // ---------- Debug ----------

    string RoomDebugText()
    {
        var here = RoomAt(transform.position);
        string current = here != null ? here.roomName : "corridor";
        string intended = transitTarget != null ? transitTarget.roomName : currentRoom != null ? currentRoom.roomName : "-";
        string link = hopIndex < hops.Count && hops.Count > 0 ? hops[hopIndex].name : "-";
        return $"in {current}, room {intended}, link {link} (hop {Mathf.Min(hopIndex + 1, Mathf.Max(1, hops.Count))}/{hops.Count}), {transitNote}";
    }

    void DrawRoomGizmos()
    {
        if (hops.Count == 0) return;
        Gizmos.color = Color.yellow;
        foreach (var l in hops)
        {
            if (l == null || l.pointA == null) continue;
            Gizmos.DrawLine(l.pointA.position + Vector3.up * 0.5f, l.pointB.position + Vector3.up * 0.5f);
            foreach (var d in l.doors) if (d != null) Gizmos.DrawWireCube(d.ClosedPosition, d.transform.lossyScale * 1.1f);
        }
        if (transitTarget != null) Gizmos.DrawWireCube(transitTarget.transform.position + Vector3.up, new Vector3(transitTarget.size.x, 0.3f, transitTarget.size.z));
    }
}
