using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum VentPhase : byte { None, Approaching, Entering, Travelling, Preparing, Exiting }

/// <summary>
/// Creature-only ventilation (host only decisions). The creature may use a vent to REPOSITION towards remembered evidence, never to
/// reach a player it can see on foot and never from knowledge of where anyone is: the destination is its own evidence (last confirmed
/// sighting, accepted sound) or, on heightened patrol, its next patrol point. Vents are chosen only when the whole trip (walk to the
/// entrance, ride the duct, walk from the exit) is clearly faster than going on foot, and not again before a cooldown.
///
/// A trip is explicit state in the replicated <see cref="VentPhase"/> (CreatureState.Vent while it lasts):
///   Approaching - walks to the entrance on the NavMesh. Fresh sight cancels it.
///   Entering    - faces the vent and waits a short delay (animation hook); sight still cancels. Then it COMMITS: the agent and collider
///                 are switched off and it walks through the mouth into the wall.
///   Travelling  - follows the authored duct route at ventSpeed, continuously, at its real position (the body is hidden inside the
///                 walls). No ground perception runs. At a junction it may re-plan if NEW evidence was heard.
///   Preparing   - waits at the hidden pre-exit point for the warning period (audio/animation hook), and for the exit to be clear.
///   Exiting     - walks out through the mouth onto the NavMesh, re-enables the agent and resumes towards the evidence.
/// Only the phase is replicated (plus the ordinary NetworkTransform position), so late joiners see the right state.
/// </summary>
public partial class CreatureAI
{
    [Header("Ventilation")]
    public bool ventEnabled = true;
    [Tooltip("Metres per second along the duct")] public float ventSpeed = 5f;
    [Tooltip("Pause at the mouth before going in (a future animation)")] public float ventEntryDelay = 1f;
    [Tooltip("Warning before it emerges, at the hidden pre-exit point (seconds)")] public float ventWarning = 1.25f;
    [Tooltip("Seconds after a trip before another may start")] public float ventCooldown = 45f;
    [Tooltip("No vent trips this long after a round starts")] public float ventFirstDelay = 20f;
    [Tooltip("The trip must save at least this many seconds over walking")] public float ventMinAdvantage = 4f;
    [Tooltip("...and take no more than this fraction of the walking time")] [Range(0.2f, 1f)] public float ventMaxTimeFraction = 0.8f;
    [Tooltip("Never bother for destinations closer than this on foot (m)")] public float ventMinGroundMetres = 15f;
    [Tooltip("A sound must have at least this outer range to be worth a vent trip (sprinting 14, doors 10)")] public float ventMinLoudness = 12f;
    [Tooltip("An entrance further than this from the creature is not considered (m)")] public float ventEntryMaxMetres = 40f;
    [Tooltip("While hunting on patrol (no evidence), a vent is taken even if it is only this many seconds slower than the walk: vents are part of the strategy, not just a shortcut")] public float ventPatrolBias = 12f;
    [Tooltip("Penalty (s) for an entrance used a moment ago; fades with age")] public float ventReusePenalty = 15f;

    [Header("Vents as a hunting tactic (frustration)")]
    [Tooltip("Frustration starts rising this long after it last SAW a player (s)")] public float ventFrustrationGrace = 20f;
    [Tooltip("...and reaches its maximum this much later (s)")] public float ventFrustrationSeconds = 90f;
    [Tooltip("At full frustration, patrol vent trips are accepted even if this many seconds slower than walking, and it favours far rooms")] public float ventFrustrationBonus = 45f;
    [Tooltip("At full frustration, trips down to this many metres on foot are considered")] public float ventFrustratedMinGround = 6f;

    [Header("Vent emergence fairness")]
    [Tooltip("Never emerge with a living player closer than this to the exit (m)")] public float emergeMinPlayerDistance = 2.5f;
    [Tooltip("An exit a player can see from closer than this is penalised, unless it is hunting evidence there (m)")] public float emergeViewDistance = 10f;
    [Tooltip("Penalty (s) for an exit in a player's direct view")] public float emergeViewPenalty = 12f;
    [Tooltip("Evidence within this distance of an exit counts as hunting there (m)")] public float emergeNearEvidence = 6f;
    [Tooltip("Wait this long for a blocked or watched exit before trying another")] public float emergeWaitMax = 5f;
    [Tooltip("After this long it emerges anyway, as long as the spot is physically clear")] public float emergeGiveUp = 25f;
    [Tooltip("Re-plan at a junction only if the new exit is this many seconds better")] public float ventReplanGain = 3f;

    readonly NetworkVariable<VentPhase> ventPhase = new(VentPhase.None);
    public VentPhase CurrentVentPhase => ventPhase.Value;

    Renderer[] ventRenderers;
    CapsuleCollider ventCollider;
    NavMeshPath ventPath;
    readonly List<Vector3> ventPoints = new(), ventTmp = new();
    readonly List<(int index, int node)> ventJunctions = new();
    readonly List<int> ventHistory = new();
    float[] ventCum = new float[0];
    CreatureState ventResume;
    string ventReason = "-", ventEvidenceNote = "-";
    int ventEntry = -1, ventExit = -1, ventNextJunction;
    bool ventCommitted, ventAltTried;
    float ventTimer, ventWait, ventTravelled, ventLength, ventEnterEnd, ventTick, ventWatchdog, ventPlanSeconds, ventGroundSeconds;
    double lastVentEnd = double.NegativeInfinity, lastSightedAt;
    string ventVerdict = "-"; // why the last consideration did or did not lead to a vent (for F3)

    /// <summary>
    /// 0..1: how long it has gone without SEEING a player (after a grace period). The longer it has had no success, the more it wants to
    /// switch tactics: vents become more attractive and far, stale rooms are preferred. Based only on its own sightings.
    /// </summary>
    float Frustration() => Mathf.Clamp01(((float)(Time.timeAsDouble - lastSightedAt) - ventFrustrationGrace) / Mathf.Max(1f, ventFrustrationSeconds));

    static CreatureVentNetwork VentNet => CreatureVentNetwork.Instance;
    bool VentReady => ventEnabled && VentNet != null && VentNet.Ready;

    void CacheVentParts()
    {
        ventRenderers ??= GetComponentsInChildren<Renderer>();
        if (ventCollider == null) ventCollider = GetComponent<CapsuleCollider>();
    }

    // Every peer: the body is hidden while inside the walls and ducts, visible at the mouth. Also the hook for cosmetic audio.
    void OnVentPhaseChanged(VentPhase previous, VentPhase now)
    {
        ApplyVentVisuals(now);
        GetComponent<CreatureAudio>()?.OnVentPhase(previous, now);
    }

    void ApplyVentVisuals(VentPhase phase)
    {
        CacheVentParts();
        bool visible = phase is VentPhase.None or VentPhase.Approaching or VentPhase.Entering or VentPhase.Exiting;
        foreach (var r in ventRenderers) if (r != null) r.enabled = visible;
    }

    // ---------- Deciding ----------

    // Walking distance along the NavMesh, or -1 when there is no usable route. A partial route (a closed door) counts as the part
    // walked plus the straight remainder, unless complete is required.
    float PathMetres(Vector3 from, Vector3 to, bool requireComplete)
    {
        if (!NavMesh.SamplePosition(from, out var a, 2f, NavMesh.AllAreas) || !NavMesh.SamplePosition(to, out var b, 2f, NavMesh.AllAreas)) return -1f;
        ventPath ??= new NavMeshPath();
        if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, ventPath) || ventPath.status == NavMeshPathStatus.PathInvalid) return -1f;
        bool complete = ventPath.status == NavMeshPathStatus.PathComplete;
        if (requireComplete && !complete) return -1f;
        var c = ventPath.corners;
        float len = 0f;
        for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
        if (!complete && c.Length > 0) len += Vector3.Distance(c[^1], b.position) * 1.4f; // a closed door in the way: the rest is a guess, with a detour allowance
        return len;
    }

    float ResumeSpeed(CreatureState s) => s == CreatureState.Patrol ? patrolSpeed : SpeedFor(s);

    // Fairness inputs for emerging at a point. Players' positions are used ONLY to reject or penalise an unsafe exit; nothing is
    // stored or fed to the creature's evidence.
    void NearestPlayerTo(Vector3 point, out float distance, out bool hasView)
    {
        distance = float.MaxValue;
        hasView = false;
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.PlayerObject == null || !client.PlayerObject.TryGetComponent(out NetworkFirstPersonController p) || !Alive(p)) continue;
            Vector3 d = Flat(p.transform.position - point);
            float dist = d.magnitude;
            if (dist >= distance) continue;
            distance = dist;
            Vector3 head = p.transform.position + Vector3.up * 1.6f, to = point + Vector3.up - head;
            hasView = Vector3.Dot(Flat(p.transform.forward).normalized, Flat(to).normalized) > 0.3f
                && !Physics.Linecast(head, point + Vector3.up, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }
    }

    float ExitPenalty(CreatureVentEntrance e, Vector3 target, bool hasTarget)
    {
        NearestPlayerTo(e.approach.position, out float dist, out bool view);
        bool pursuingHere = hasTarget && Flat(target - e.approach.position).magnitude <= emergeNearEvidence;
        float p = VentRules.ExitPenalty(dist, view, pursuingHere, emergeMinPlayerDistance, emergeViewDistance, emergeViewPenalty);
        return p + VentRules.ReusePenalty(e.id, ventHistory, ventReusePenalty);
    }

    /// <summary>
    /// Considers a vent trip towards <paramref name="target"/> (the creature's own evidence or next patrol point). Returns true if one
    /// began. Never during a chase, never with a player confirmed in sight, never inside the cooldown.
    /// </summary>
    bool ConsiderVent(string why, CreatureState resume, Vector3 target, string evidenceNote)
    {
        if (!VentReady) { ventVerdict = "no vent network"; return false; }
        if (state.Value is CreatureState.Chase or CreatureState.Bash or CreatureState.Vent) return false;
        if (NearestRecognised() != null) { ventVerdict = "a player is in sight"; return false; } // a confirmed sighting: no leaving the ground route
        double now = Time.timeAsDouble;
        float frustration = resume == CreatureState.Patrol ? Frustration() : 0f;
        if (!VentRules.CooldownReady(now, lastVentEnd, ventCooldown * (1f - 0.6f * frustration)))
        { ventVerdict = $"cooldown ({lastVentEnd + ventCooldown * (1f - 0.6f * frustration) - now:0}s)"; return false; }

        float ground = PathMetres(transform.position, target, false);
        bool blocked = ground < 0f;
        if (blocked) ground = Vector3.Distance(transform.position, target) * 1.5f; // no route at all: assume a long way round
        float minGround = Mathf.Lerp(ventMinGroundMetres, ventFrustratedMinGround, frustration);
        if (ground < minGround) { ventVerdict = $"too close to bother ({ground:0} m, needs {minGround:0})"; return false; }
        float walk = ResumeSpeed(resume);
        // On patrol a vent is a tactic, not just a shortcut: the more frustrated it is, the slower a trip it will still take.
        float bias = resume == CreatureState.Patrol ? ventPatrolBias + frustration * ventFrustrationBonus : 0f;
        float groundSeconds = ground / walk + (blocked ? 15f : 0f) + bias;

        var net = VentNet;
        int n = net.EntranceCount;
        var entryM = new float[n];
        var exitM = new float[n];
        var entryPen = new float[n];
        var exitPen = new float[n];
        for (int i = 0; i < n; i++)
        {
            var e = net.entrances[i];
            bool ok = e != null && e.usable && e.approach != null;
            float toEntry = ok ? PathMetres(transform.position, e.approach.position, true) : -1f;
            entryM[i] = toEntry > ventEntryMaxMetres ? -1f : toEntry;
            exitM[i] = ok ? PathMetres(e.approach.position, target, false) : -1f; // doors on the ground leg can be opened
            entryPen[i] = ok ? VentRules.ReusePenalty(e.id, ventHistory, ventReusePenalty) : 0f;
            exitPen[i] = ok ? ExitPenalty(e, target, true) : 0f;
        }
        var plan = VentRules.Choose(net.Graph, entryM, exitM, entryPen, exitPen, groundSeconds, walk, ventSpeed, ventMinAdvantage, ventMaxTimeFraction);
        if (!plan.found)
        {
            // For the F3 line: what the best trip would have been, and why it was not good enough.
            var any = VentRules.Choose(net.Graph, entryM, exitM, entryPen, exitPen, groundSeconds, walk, ventSpeed, -9999f, 9999f);
            ventVerdict = any.found ? $"best trip {any.seconds:0}s vs {groundSeconds:0}s on foot (frustration {frustration:0.00}): not worth it"
                : $"no usable entrance/exit pair (frustration {frustration:0.00})";
            return false;
        }

        ventVerdict = $"taken (frustration {frustration:0.00})";
        ventPlanSeconds = plan.seconds;
        ventGroundSeconds = groundSeconds;
        BeginVent(plan.entry, plan.exit, why, resume, evidenceNote);
        return true;
    }

    string EvidenceNote() => evidenceKind == EvidenceKind.None ? "none"
        : $"{evidenceKind} {Time.timeAsDouble - evidenceTime:0.0}s ago at ({evidencePos.x:0}, {evidencePos.z:0}), weight {evidenceStrength:0.00}";

    // ---------- Beginning, cancelling, finishing ----------

    void BeginVent(int entry, int exit, string why, CreatureState resume, string evidenceNote)
    {
        ventEntry = entry;
        ventExit = exit;
        ventResume = resume;
        ventReason = why;
        ventEvidenceNote = evidenceNote;
        ventAltTried = ventCommitted = false;
        ventTimer = ventWait = ventTravelled = 0f;
        ventTick = 0f;
        ventWatchdog = 25f + VentNet.Graph.Distance(entry, exit) / Mathf.Max(0.5f, ventSpeed);
        ClearSearch(false);
        bashDoor = null;
        target = null;
        chaseInspectSpot = null;
        targetId.Value = ulong.MaxValue;
        arrived = false;
        state.Value = CreatureState.Vent;
        reason = $"vent: {why}";
        agent.speed = ResumeSpeed(resume);
        agent.stoppingDistance = 0.4f;
        GoTo(VentNet.entrances[entry].approach.position);
        ventPhase.Value = VentPhase.Approaching;
        Debug.Log($"Creature vent: {why}; entrance {entry} to exit {exit}, about {ventPlanSeconds:0.0}s against {ventGroundSeconds:0.0}s on foot. Evidence: {evidenceNote}");
    }

    // Before it has committed (agent still on, collider still on): simply stop.
    void CancelVent(string why)
    {
        ventPhase.Value = VentPhase.None;
        ventCommitted = false;
        reason = $"vent cancelled: {why}";
    }

    // The vent could not be used and nothing else took over: carry on as before.
    void AbandonVent(string why)
    {
        CancelVent(why);
        if (evidenceKind != EvidenceKind.None) EnterSearch(ResumeAfterVent(), $"vent abandoned ({why})");
        else EnterPatrol($"vent abandoned ({why})");
    }

    CreatureState ResumeAfterVent()
    {
        if (ventResume == CreatureState.Pursue && SearchRules.PursuitExpired((float)(Time.timeAsDouble - lastTrailTime), pursueGrace)) return CreatureState.Search;
        return ventResume == CreatureState.Patrol ? CreatureState.Search : ventResume;
    }

    // Round reset: put everything back. Returns nothing; the caller warps the creature to its start afterwards.
    void ResetVent(Vector3 startPosition)
    {
        bool wasCommitted = ventCommitted || !agent.enabled;
        ventPhase.Value = VentPhase.None;
        ventCommitted = false;
        ventHistory.Clear();
        ventPoints.Clear();
        ventEntry = ventExit = -1;
        ventReason = ventEvidenceNote = "-";
        lastVentEnd = Time.timeAsDouble - ventCooldown + ventFirstDelay; // first trip only after ventFirstDelay
        lastSightedAt = Time.timeAsDouble; // frustration counts from the start of the round
        ventVerdict = "-";
        CacheVentParts();
        if (ventCollider != null) ventCollider.enabled = true;
        ApplyVentVisuals(VentPhase.None);
        if (wasCommitted && !agent.enabled)
        {
            transform.position = startPosition; // on the NavMesh, so the agent can be enabled safely
            agent.enabled = true;
        }
    }

    // ---------- Per frame ----------

    void UpdateVent()
    {
        UpdateAlertness();
        if (!VentReady) { if (!ventCommitted) AbandonVent("vent network missing"); return; }
        var phase = ventPhase.Value;
        float dt = Time.deltaTime;

        // While it has not committed, fresh sight interrupts (Acquire cancels the vent and starts the chase).
        bool interruptible = phase == VentPhase.Approaching || (phase == VentPhase.Entering && !ventCommitted);
        if (interruptible && (ventTick -= dt) <= 0f)
        {
            ventTick = 0.1f;
            UpdateSight();
            if (Acquire()) return;
        }

        switch (phase)
        {
            case VentPhase.Approaching: UpdateApproach(); break;
            case VentPhase.Entering when !ventCommitted: UpdateEntryDelay(dt); break;
            case VentPhase.Entering:
            case VentPhase.Travelling:
            case VentPhase.Exiting: MoveAlongVent(dt); break;
            case VentPhase.Preparing: UpdatePreparing(dt); break;
        }
    }

    void UpdateApproach()
    {
        var entrance = VentNet.entrances[ventEntry];
        if ((ventWatchdog -= Time.deltaTime) <= 0f) { AbandonVent("could not reach the vent in time"); return; }
        if (agent.pathPending) return;
        float flat = Flat(entrance.approach.position - transform.position).magnitude;
        bool stopped = agent.remainingDistance <= agent.stoppingDistance + 0.1f;
        bool near = flat <= 0.8f || (stopped && flat <= 2f);
        if (!near)
        {
            // The route ended (or never existed) somewhere else: this entrance cannot be used right now.
            if (agent.pathStatus == NavMeshPathStatus.PathInvalid || stopped) AbandonVent("could not get to the vent");
            return;
        }
        agent.ResetPath();
        agent.velocity = Vector3.zero;
        ventTimer = 0f;
        ventPhase.Value = VentPhase.Entering;
    }

    void UpdateEntryDelay(float dt)
    {
        var entrance = VentNet.entrances[ventEntry];
        Vector3 to = Flat(entrance.face.position - transform.position);
        if (to.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to), 360f * dt);
        if ((ventTimer += dt) >= ventEntryDelay) CommitVent();
    }

    // The point of no return: the agent and collider go off and the creature walks into the wall at ventSpeed.
    void CommitVent()
    {
        var net = VentNet;
        var entrance = net.entrances[ventEntry];
        var exit = net.entrances[ventExit];
        if (!net.Route(ventEntry, ventExit, ventTmp, ventJunctions, out float routeLength))
        {
            AbandonVent("no duct route");
            return;
        }
        ventPoints.Clear();
        ventPoints.Add(transform.position);
        ventPoints.Add(entrance.face.position);
        ventPoints.Add(entrance.inside.position);
        int offset = ventPoints.Count;
        ventPoints.AddRange(ventTmp); // starts at the entrance's own node (top)
        ventPoints.Add(exit.inside.position);
        for (int k = 0; k < ventJunctions.Count; k++) ventJunctions[k] = (ventJunctions[k].index + offset, ventJunctions[k].node);
        BuildVentCum();
        ventEnterEnd = ventCum[2];
        ventNextJunction = 0;
        ventTravelled = 0f;
        ventCommitted = true;
        agent.enabled = false;
        if (ventCollider != null) ventCollider.enabled = false;
        Debug.Log($"Creature vent: committed, route {routeLength:0.0} m, about {routeLength / ventSpeed:0.0} s at {ventSpeed:0.0} m/s.");
    }

    void BuildVentCum()
    {
        if (ventCum.Length < ventPoints.Count) ventCum = new float[ventPoints.Count + 8];
        ventCum[0] = 0f;
        for (int i = 1; i < ventPoints.Count; i++) ventCum[i] = ventCum[i - 1] + Vector3.Distance(ventPoints[i - 1], ventPoints[i]);
        ventLength = ventCum[ventPoints.Count - 1];
    }

    Vector3 VentPositionAt(float distance, out Vector3 direction)
    {
        distance = Mathf.Clamp(distance, 0f, ventLength);
        int i = 1;
        while (i < ventPoints.Count - 1 && ventCum[i] < distance) i++;
        float seg = ventCum[i] - ventCum[i - 1];
        float t = seg > 0.0001f ? (distance - ventCum[i - 1]) / seg : 1f;
        direction = ventPoints[i] - ventPoints[i - 1];
        return Vector3.Lerp(ventPoints[i - 1], ventPoints[i], t);
    }

    // Continuous movement only: the position always advances along the authored points, never jumps.
    void MoveAlongVent(float dt)
    {
        var phase = ventPhase.Value;
        ventTravelled += ventSpeed * (phase == VentPhase.Exiting ? 0.75f : 1f) * dt;
        transform.position = VentPositionAt(ventTravelled, out Vector3 dir);
        Vector3 flatDir = Flat(dir);
        if (flatDir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flatDir), 540f * dt);

        if (phase == VentPhase.Entering)
        {
            if (ventTravelled >= ventEnterEnd) ventPhase.Value = VentPhase.Travelling;
            return;
        }
        if (phase == VentPhase.Exiting)
        {
            if (ventTravelled >= ventLength) Emerge();
            return;
        }

        while (ventNextJunction < ventJunctions.Count && ventCum[ventJunctions[ventNextJunction].index] <= ventTravelled)
        {
            int node = ventJunctions[ventNextJunction].node;
            ventNextJunction++;
            if (ReplanAtJunction(node)) return;
        }
        if (ventTravelled >= ventLength)
        {
            ventTimer = ventWait = 0f;
            ventPhase.Value = VentPhase.Preparing; // the warning hook fires from the phase change on every peer
        }
    }

    // New evidence heard in the duct, at an actual junction, may change the exit. Nothing is read from any player.
    bool ReplanAtJunction(int node)
    {
        double before = evidenceTime;
        if (!Hear(true) || evidenceTime <= before || evidenceKind == EvidenceKind.None) return false;
        var net = VentNet;
        int n = net.EntranceCount;
        var exitM = new float[n];
        var exitPen = new float[n];
        for (int j = 0; j < n; j++)
        {
            var e = net.entrances[j];
            bool ok = e != null && e.usable && e.approach != null;
            exitM[j] = ok ? PathMetres(e.approach.position, evidencePos, false) : -1f;
            exitPen[j] = ok ? ExitPenalty(e, evidencePos, true) : 0f;
        }
        float walk = ResumeSpeed(ventResume);
        int best = VentRules.BestExitFrom(net.Graph, node, exitM, exitPen, walk, ventSpeed, null);
        if (best < 0 || best == ventExit) return false;
        float Score(int j) => net.Graph.Distance(node, j) / ventSpeed + exitM[j] / walk + exitPen[j];
        float current = ventExit >= 0 && exitM[ventExit] >= 0f ? Score(ventExit) : float.PositiveInfinity;
        if (Score(best) + ventReplanGain >= current) return false;
        if (!SwitchExit(node, best)) return false;
        ventReason += " (re-planned at a junction)";
        ventEvidenceNote = EvidenceNote();
        return true;
    }

    // New route from a node (or from the pre-exit point's own entrance node) to another exit. Continuous: it starts where it is.
    bool SwitchExit(int fromNode, int newExit)
    {
        var net = VentNet;
        if (!net.Route(fromNode, newExit, ventTmp, ventJunctions, out _)) return false;
        Vector3 here = transform.position;
        ventPoints.Clear();
        ventPoints.Add(here);
        ventPoints.AddRange(ventTmp);
        ventPoints.Add(net.entrances[newExit].inside.position);
        for (int k = 0; k < ventJunctions.Count; k++) ventJunctions[k] = (ventJunctions[k].index + 1, ventJunctions[k].node);
        BuildVentCum();
        ventTravelled = 0f;
        ventNextJunction = 0;
        ventExit = newExit;
        if (ventPhase.Value != VentPhase.Travelling) ventPhase.Value = VentPhase.Travelling;
        return true;
    }

    // ---------- Emerging ----------

    bool ExitObstructed(CreatureVentEntrance e)
    {
        Vector3 p = e.approach.position;
        return Physics.CheckCapsule(p + Vector3.up * 0.55f, p + Vector3.up * 1.45f, 0.4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    void UpdatePreparing(float dt)
    {
        ventTimer += dt;
        ventWait += dt;
        if (ventTimer < ventWarning) return; // the warning period

        var exit = VentNet.entrances[ventExit];
        bool blocked = ExitObstructed(exit);
        NearestPlayerTo(exit.approach.position, out float dist, out bool view);
        bool tooClose = dist < emergeMinPlayerDistance;
        bool watched = view && dist < emergeViewDistance && !(evidenceKind != EvidenceKind.None && Flat(evidencePos - exit.approach.position).magnitude <= emergeNearEvidence);

        if (!blocked && !tooClose && (!watched || ventWait >= emergeWaitMax)) { BeginExiting(); return; }
        if (ventWait >= emergeWaitMax && !ventAltTried)
        {
            ventAltTried = true;
            if (TryAlternateExit()) return;
        }
        if (ventWait >= emergeGiveUp && !blocked && !tooClose) BeginExiting(); // waited long enough; never over a player or an obstacle
    }

    bool TryAlternateExit()
    {
        var net = VentNet;
        int n = net.EntranceCount;
        Vector3 target = evidenceKind != EvidenceKind.None ? evidencePos : transform.position;
        var exitM = new float[n];
        var exitPen = new float[n];
        for (int j = 0; j < n; j++)
        {
            var e = net.entrances[j];
            bool ok = e != null && e.usable && e.approach != null && !ExitObstructed(e);
            exitM[j] = ok ? PathMetres(e.approach.position, target, false) : -1f;
            exitPen[j] = ok ? ExitPenalty(e, target, evidenceKind != EvidenceKind.None) : 0f;
        }
        int alt = VentRules.BestExitFrom(net.Graph, ventExit, exitM, exitPen, ResumeSpeed(ventResume), ventSpeed, new HashSet<int> { ventExit });
        if (alt < 0 || !SwitchExit(ventExit, alt)) return false;
        ventReason += " (exit blocked, used another)";
        return true;
    }

    void BeginExiting()
    {
        var exit = VentNet.entrances[ventExit];
        ventPoints.Clear();
        ventPoints.Add(transform.position);
        ventPoints.Add(exit.face.position);
        ventPoints.Add(exit.approach.position);
        BuildVentCum();
        ventTravelled = 0f;
        ventPhase.Value = VentPhase.Exiting; // visible again from here; still inside the wall
    }

    void Emerge()
    {
        var exit = VentNet.entrances[ventExit];
        Vector3 p = NavMesh.SamplePosition(exit.approach.position, out var hit, 2f, NavMesh.AllAreas) ? hit.position : exit.approach.position;
        transform.position = p;
        agent.enabled = true;
        agent.Warp(p);
        if (ventCollider != null) ventCollider.enabled = true;
        ventCommitted = false;
        ventPhase.Value = VentPhase.None;
        lastVentEnd = Time.timeAsDouble;
        ventHistory.Add(ventEntry);
        ventHistory.Add(ventExit);
        while (ventHistory.Count > 6) ventHistory.RemoveAt(0);
        Debug.Log($"Creature vent: emerged at entrance {ventExit} ({exit.room?.roomName}).");
        AfterVent();
    }

    // Back on the ground: head for the remembered evidence, or search the room it came out in. It knows nothing new.
    void AfterVent()
    {
        if (evidenceKind != EvidenceKind.None) { EnterSearch(ResumeAfterVent(), "emerged from a vent, heading for the evidence"); return; }
        state.Value = CreatureState.Search;
        reason = "emerged from a vent, searching this room";
        evidencePos = transform.position; // no evidence: the search is centred on where it came out
        agent.speed = searchSpeed;
        agent.stoppingDistance = 0.4f;
        arrived = true;
        searchCenter = transform.position;
        BeginRoomSearch();
    }

    // ---------- Debug ----------

    string VentDebugText()
    {
        double cooldown = lastVentEnd + ventCooldown - Time.timeAsDouble;
        string cd = cooldown > 0 ? $"{cooldown:0}s" : "ready";
        var phase = ventPhase.Value;
        if (phase == VentPhase.None)
            return $"idle, cooldown {cd}, frustration {Frustration():0.00}, last: {ventReason}\n  last decision: {ventVerdict}\n  evidence used: {ventEvidenceNote}";
        string route = ventCommitted ? $"{ventTravelled:0.0}/{ventLength:0.0} m, {VentRules.RemainingSeconds(ventLength, ventTravelled, ventSpeed):0.0}s left" : "not committed";
        string en = ventEntry >= 0 ? $"#{ventEntry}" : "-", ex = ventExit >= 0 ? $"#{ventExit}" : "-";
        return $"{phase} entry {en} exit {ex}, {route}, cooldown {cd}\n  why: {ventReason}\n  evidence used: {ventEvidenceNote}";
    }

    void DrawVentGizmos()
    {
        if (ventPoints.Count < 2 || ventPhase.Value == VentPhase.None) return;
        Gizmos.color = Color.cyan;
        for (int i = 1; i < ventPoints.Count; i++) Gizmos.DrawLine(ventPoints[i - 1], ventPoints[i]);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.2f, 0.4f);
        if (ventExit >= 0 && VentNet != null)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(VentNet.entrances[ventExit].approach.position + Vector3.up * 0.3f, 0.6f);
        }
    }
}
