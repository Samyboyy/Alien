using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public enum AlertLevel : byte { Calm, Wary, Hunting }

/// <summary>
/// Structured search and alertness (host only). The creature never learns where anyone is hiding: it chooses rooms and points
/// from its last evidence, the room connections, reachability and what it already checked, and it finds players only by
/// actually seeing them (low eye while looking under furniture).
///
///   evidence room (Local)  ->  a few connected rooms (Nearby)  ->  heightened patrol that calms over time
///
/// Every phase has a time cap and every room a check budget, so a search always ends.
/// </summary>
public partial class CreatureAI
{
    [Header("Room search")]
    [Tooltip("Searching the room that holds the evidence. Scaled 0.8 (calm) to 1.2 (fully alert).")] public float localSearchSeconds = 20f;
    [Tooltip("Then searching a few connected rooms. Same alertness scaling.")] public float nearbySearchSeconds = 38f;
    [Tooltip("Afterwards it patrols with heightened alertness that fades over this long")] public float alertSeconds = 52f;
    [Tooltip("Ordinary search points checked in the evidence room")] public int localPointBudget = 4;
    [Tooltip("Hiding places checked in the evidence room (a place the creature watched someone crawl under is always checked first and is free)")] public int localHidingBudget = 2;
    [Tooltip("How many connected rooms it moves through after the evidence room")] public int nearbyRooms = 3;
    public int nearbyPointBudget = 2;
    public int nearbyHidingBudget = 1;
    [Tooltip("Pause at an ordinary search point while it looks around")] public float inspectPause = 1.6f;
    [Tooltip("Time spent looking under a hiding place from its opening")] public float hidingInspectSeconds = 2.5f;
    [Tooltip("Eye height while looking under furniture")] public float inspectEyeHeight = 0.7f;
    [Tooltip("Give up on one search point after this long (blocked / unreachable)")] public float pointTimeout = 12f;
    [Tooltip("Patrol speed gain at full alertness; waits also shorten")] public float alertPatrolSpeedBonus = 0.4f;

    enum SearchStep : byte { Travel, Inspect, Transit }

    readonly SearchMemory memory = new();
    NavMeshPath searchPath; // created in Awake
    readonly List<(float score, Transform point, HidingSpot spot)> candidates = new();
    float alertness; // 0..1, host only; only the coarse AlertLevel is replicated
    SearchPhase searchPhase = SearchPhase.Done;
    SearchStep step;
    RoomVolume currentRoom;
    Transform targetPoint;
    HidingSpot targetSpot, witnessedSpot;
    bool inspectingLow;
    float phaseTime, inspectTimer, inspectTotal, yawBase;
    int pointsChecked, hidingChecked, roomsVisited;

    float AlertScale => Mathf.Lerp(0.8f, 1.2f, alertness);

    // ---------- Alertness ----------

    // Alertness lasts after sight is lost: it is held while hunting and decays only during the heightened patrol.
    // It scales search persistence and patrol speed. It never gives a position and never extends hearing.
    void UpdateAlertness()
    {
        if (state.Value == CreatureState.Patrol) alertness = Mathf.Max(0f, alertness - Time.deltaTime / Mathf.Max(1f, alertSeconds));
        AlertLevel level = state.Value != CreatureState.Patrol ? AlertLevel.Hunting : alertness > 0.05f ? AlertLevel.Wary : AlertLevel.Calm;
        if (alertLevel.Value != level) alertLevel.Value = level; // written only on change, so no network traffic per frame
    }

    // ---------- Rooms ----------

    static RoomVolume RoomAt(Vector3 p)
    {
        foreach (var r in RoomVolume.All) if (r.Contains(p)) return r;
        return null;
    }

    // Evidence in a corridor (no volume): the closest room within reach of it.
    static RoomVolume NearestRoom(Vector3 p, float maxDistance)
    {
        RoomVolume best = null;
        float bestD = maxDistance;
        foreach (var r in RoomVolume.All)
        {
            float d = r.DistanceTo(p);
            if (d <= bestD) { bestD = d; best = r; }
        }
        return best;
    }

    // ---------- Search lifecycle ----------

    // fullHistory=false keeps what was already searched (used when evidence merely moves); true is a fresh start.
    void ClearSearch(bool fullHistory = true)
    {
        ReleaseLocker(); // an unfinished locker inspection never leaves a door held open
        searchPhase = SearchPhase.Done;
        step = SearchStep.Travel;
        currentRoom = null;
        targetPoint = null;
        targetSpot = null;
        inspectingLow = false;
        pointsChecked = hidingChecked = roomsVisited = 0;
        phaseTime = 0f;
        ClearTransit();
        if (!fullHistory) return;
        memory.Clear();
        witnessedSpot = null;
    }

    // A hiding place the creature WATCHED the lost target enter, or failing that one the last confirmed sighting was crouched over,
    // is searched first, deliberately, not by chance. Both come from observation only: if the player moved on unseen the place is
    // simply empty when checked, and whether it is empty never changes this choice.
    void NoteWitnessedSpot()
    {
        witnessedSpot = WatchedSpotOf(target);
        if (witnessedSpot != null) return;
        if (evidenceKind != EvidenceKind.Sight || !evidenceCrouched) return;
        float best = float.MaxValue;
        foreach (var h in HidingSpot.All)
        {
            if (!h.ContainsFootprint(evidencePos, 0.6f)) continue;
            float d = Vector3.Distance(h.transform.position, evidencePos);
            if (d < best) { best = d; witnessedSpot = h; }
        }
    }

    void BeginRoomSearch()
    {
        searchPhase = SearchPhase.Local;
        phaseTime = 0f;
        roomsVisited = 0;
        if (state.Value != CreatureState.Search) state.Value = CreatureState.Search; // arrived (from an Investigate): now searching
        EnterRoom(RoomAt(searchCenter) ?? NearestRoom(searchCenter, 14f));
        reason = currentRoom != null ? $"searching {currentRoom.roomName}" : "searching around the evidence";
        NextTarget();
    }

    void EnterRoom(RoomVolume room)
    {
        currentRoom = room;
        NoteRoomVisit(room);
        pointsChecked = hidingChecked = 0;
        if (room != null) memory.MarkRoom(room.GetInstanceID());
    }

    int PointBudget => searchPhase == SearchPhase.Local ? localPointBudget : nearbyPointBudget;
    int HidingBudget => searchPhase == SearchPhase.Local ? localHidingBudget : nearbyHidingBudget;

    void UpdateRoomSearch()
    {
        // Phase caps apply at every moment, even mid-travel, so the search is bounded whatever the points do.
        phaseTime += Time.deltaTime;
        float scale = AlertScale;
        var next = SearchRules.Advance(searchPhase, phaseTime, localSearchSeconds * scale, nearbySearchSeconds * scale);
        if (next != searchPhase)
        {
            inspectingLow = false;
            if (next == SearchPhase.Nearby) StartNearby();
            else FinishSearch("search time ran out");
            return;
        }
        if (searchPhase == SearchPhase.Done) return;

        if (step == SearchStep.Inspect) { UpdateInspect(); return; }
        if (step == SearchStep.Transit) { UpdateTransit(); return; }

        travelTimer += Time.deltaTime;
        if (agent.pathPending) return;
        bool atEnd = Arrived();
        if (!atEnd && travelTimer <= pointTimeout) return;

        if (atEnd && travelTimer <= pointTimeout)
        {
            if (agent.pathStatus == NavMeshPathStatus.PathComplete) { BeginInspect(); return; }
            if (agent.pathStatus == NavMeshPathStatus.PathPartial && TryOpenDoor()) return; // a door on the way to the point
        }
        SkipTarget(); // unreachable, blocked by a reinforced door, or took too long
    }

    // ---------- Choosing where to look ----------

    void NextTarget()
    {
        targetPoint = null;
        targetSpot = null;
        step = SearchStep.Travel;
        agent.speed = searchSpeed;
        agent.stoppingDistance = 0.4f;

        if (witnessedSpot != null && !memory.PointChecked(witnessedSpot.GetInstanceID()))
        {
            SelectSpot(witnessedSpot); // watched someone crawl under here: investigate it deliberately
            reason = $"checking {witnessedSpot.name}, seen crouching there";
            return;
        }

        if (currentRoom == null)
        {
            // Evidence outside every room volume (e.g. a corridor): a few spots around it, then move on.
            if (!SearchRules.MayCheck(pointsChecked, PointBudget)) { AdvanceAfterRoom(); return; }
            Vector2 r = Random.insideUnitCircle * searchRadius;
            GoTo(searchCenter + new Vector3(r.x, 0f, r.y));
            return;
        }

        // Candidates: unchecked points and hiding places of this room, within budget. Nearer to the evidence is better; hiding
        // places get a bonus. Nothing here looks at where any player actually is or whether a place is occupied.
        candidates.Clear();
        bool pointsLeft = SearchRules.MayCheck(pointsChecked, PointBudget);
        bool hidingLeft = SearchRules.MayCheck(hidingChecked, HidingBudget);
        if (pointsLeft)
            foreach (var t in currentRoom.searchPoints)
                if (t != null && !memory.PointChecked(t.GetInstanceID()))
                    candidates.Add((Score(t.position), t, null));
        if (hidingLeft)
            foreach (var h in currentRoom.hidingSpots)
                if (h != null && !memory.PointChecked(h.GetInstanceID()))
                    candidates.Add((HidingScore(h), null, h));
        candidates.Sort((a, b) => a.score.CompareTo(b.score));

        for (int i = 0; i < candidates.Count && i < 4; i++) // test only the best few for reachability
        {
            var c = candidates[i];
            Vector3 pos = c.spot != null ? InspectPosition(c.spot) : c.point.position;
            if (!Reachable(pos)) { memory.MarkPoint(c.spot != null ? c.spot.GetInstanceID() : c.point.GetInstanceID()); continue; }
            if (c.spot != null) SelectSpot(c.spot); else SelectPoint(c.point);
            return;
        }
        AdvanceAfterRoom();
    }

    float Score(Vector3 p) => Vector3.Distance(evidencePos, p) + 0.3f * Vector3.Distance(transform.position, p);

    void SelectPoint(Transform t)
    {
        targetPoint = t;
        GoTo(t.position);
    }

    void SelectSpot(HidingSpot spot)
    {
        targetSpot = spot;
        GoTo(InspectPosition(spot));
        if (spot.locker != null) Decision(DecisionReason.LockerSelected, DecisionEffect.None, spot.name + (witnessedSpot == spot ? " (watched entry)" : ""));
    }

    // The opening to look in from that is nearest to us (the spot owns its opening information).
    Vector3 InspectPosition(HidingSpot spot) => spot.NearestOpening(transform.position);

    bool Reachable(Vector3 p) =>
        NavMesh.SamplePosition(p, out var hit, 1.5f, NavMesh.AllAreas)
        && NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, searchPath)
        && searchPath.status != NavMeshPathStatus.PathInvalid;

    void SkipTarget()
    {
        MarkTargetChecked();
        NextTarget();
    }

    void MarkTargetChecked()
    {
        if (targetSpot != null) memory.MarkPoint(targetSpot.GetInstanceID());
        else if (targetPoint != null) memory.MarkPoint(targetPoint.GetInstanceID());
    }

    // ---------- Looking ----------

    // Pause, look around. A hiding place is looked into from its opening with a low eye, through normal sight checks.
    // This is the place for future crouch / peek / reach animations: start them here and end them in FinishInspect.
    void BeginInspect()
    {
        step = SearchStep.Inspect;
        agent.ResetPath();
        inspectTotal = inspectTimer = targetSpot != null ? hidingInspectSeconds : inspectPause;
        inspectingLow = targetSpot != null;
        yawBase = transform.eulerAngles.y;
        if (targetSpot != null && targetSpot.locker != null)
        {
            inspectingLow = false; // set only while it is really looking into the open locker
            BeginLockerInspect(targetSpot);
        }
    }

    static readonly float[] sweep = { -50f, 50f, 0f }; // short changes of facing while looking around

    void UpdateInspect()
    {
        inspectTimer -= Time.deltaTime;
        Quaternion want;
        if (targetSpot != null)
        {
            Vector3 to = Flat(targetSpot.LookPoint - transform.position);
            want = to.sqrMagnitude > 0.01f ? Quaternion.LookRotation(to) : transform.rotation;
        }
        else
        {
            int i = Mathf.Clamp((int)((1f - inspectTimer / Mathf.Max(0.01f, inspectTotal)) * sweep.Length), 0, sweep.Length - 1);
            want = Quaternion.Euler(0f, yawBase + sweep[i], 0f);
        }
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, 200f * Time.deltaTime);

        if (lockerHeld != null)
        {
            // A locker has its own timeline (wind up, open, look, close); the usual timer does not apply to it.
            if (!UpdateLockerInspect()) return;
            inspectingLow = false;
            hidingChecked++;
            MarkTargetChecked();
            NextTarget();
            return;
        }
        if (inspectTimer > 0f) return;
        inspectingLow = false;
        if (targetSpot != null) hidingChecked++; else pointsChecked++;
        MarkTargetChecked();
        NextTarget();
    }

    // ---------- Moving on ----------

    void AdvanceAfterRoom()
    {
        if (searchPhase == SearchPhase.Local) StartNearby();
        else NextNearbyRoom();
    }

    void StartNearby()
    {
        searchPhase = SearchPhase.Nearby;
        phaseTime = 0f;
        roomsVisited = 0;
        NextNearbyRoom();
    }

    void NextNearbyRoom()
    {
        if (roomsVisited >= nearbyRooms) { FinishSearch("searched the nearby rooms"); return; }
        if (TryLinkedNextRoom()) return; // explicit connections: go through the right door

        var all = RoomVolume.All;
        var from = currentRoom ?? NearestRoom(searchCenter, 25f);
        int start = all.IndexOf(from);
        if (start < 0) { FinishSearch("no connected rooms"); return; }

        // Adjacency from the explicit room connections, nearest to the evidence first.
        var adjacency = new List<int[]>(all.Count);
        foreach (var r in all)
        {
            var ids = new List<int>();
            foreach (var n in r.neighbours) { int i = n != null ? all.IndexOf(n) : -1; if (i >= 0) ids.Add(i); }
            ids.Sort((a, b) => all[a].DistanceTo(evidencePos).CompareTo(all[b].DistanceTo(evidencePos)));
            adjacency.Add(ids.ToArray());
        }
        var visitedIndices = new HashSet<int>();
        for (int i = 0; i < all.Count; i++) if (memory.RoomSearched(all[i].GetInstanceID())) visitedIndices.Add(i);

        var next = SearchRules.NearbyRooms(adjacency, start, 1, visitedIndices);
        if (next.Count == 0) { FinishSearch("no unsearched connected rooms"); return; }

        roomsVisited++;
        EnterRoom(all[next[0]]);
        reason = $"moving on to {currentRoom.roomName}";
        NextTarget();
    }

    // Search over: heightened patrol. Alertness stays and decays during it; there is no instant return to calm.
    void FinishSearch(string why)
    {
        searchPhase = SearchPhase.Done;
        EnterPatrol(why + ", heightened patrol", DecisionReason.SearchExpired);
    }

    // ---------- Debug ----------

    string SearchDebugText()
    {
        if (searchPhase == SearchPhase.Done && state.Value != CreatureState.Search && state.Value != CreatureState.Investigate) return "-";
        float scale = AlertScale;
        float cap = (searchPhase == SearchPhase.Local ? localSearchSeconds : nearbySearchSeconds) * scale;
        string room = currentRoom != null ? currentRoom.roomName : "none";
        string point = targetSpot != null ? $"hiding {targetSpot.name}" : targetPoint != null ? targetPoint.name : "-";
        string doing = step == SearchStep.Inspect ? (lockerHeld != null ? $"locker {lockerInspection.Stage}" : $"inspecting {inspectTimer:0.0}s") : "travelling";
        return $"{searchPhase} in {room}, {point} ({doing})\n  points {pointsChecked}/{PointBudget}, hiding {hidingChecked}/{HidingBudget}, rooms {roomsVisited}/{nearbyRooms}, {Mathf.Max(0f, cap - phaseTime):0}s left, alert {alertness:0.00}";
    }

    void DrawSearchGizmos()
    {
        if (currentRoom != null)
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f);
            Gizmos.DrawWireCube(currentRoom.transform.position + Vector3.up * 0.1f, new Vector3(currentRoom.size.x, 0.2f, currentRoom.size.z));
        }
        if (searchPhase != SearchPhase.Done)
        {
            Gizmos.color = Color.white;
            if (targetPoint != null) Gizmos.DrawWireSphere(targetPoint.position, 0.4f);
            if (targetSpot != null) { Gizmos.color = Color.magenta; Gizmos.DrawWireSphere(InspectPosition(targetSpot), 0.4f); }
        }
    }
}
