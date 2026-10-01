using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum VentPhase : byte { None, Approaching, Entering, Travelling, Preparing, Exiting }

/// <summary>
/// Creature-only ventilation (host only decisions). The creature uses vents to REPOSITION towards what it knows: its own evidence
/// (last confirmed sighting, accepted sounds), its search memory, and on hunting patrol a stale or recently active room. It never
/// reads a hidden player's position to choose a route or an exit, and never vents while it has a confirmed sighting or is chasing.
///
/// How often: an explicit, seeded vent DESIRE (VentCadence). After each trip (and at the round start) a random value from the round
/// seed fixes when desire reaches 1: about 10-15 s for the first tactical trip, 40-60 s on calm hunting, 20-35 s when alert or
/// searching, shortened by frustration (time since it last saw anyone), never sooner than the absolute minimum (ventCooldown). Desire
/// only ENCOURAGES: while it is high the creature looks for a trip every few seconds and accepts trips that are a little slower than
/// walking, but a trip still needs a reachable entrance, a connected exit and a fair emergence.
///
/// A trip is explicit state in the replicated <see cref="VentPhase"/> (CreatureState.Vent while it lasts):
///   Approaching - walks to the entrance on the NavMesh. Fresh sight cancels it.
///   Entering    - faces the vent and waits a short delay (animation hook); sight still cancels. Then it COMMITS: the agent and collider
///                 are switched off and it walks through the mouth into the wall.
///   Travelling  - follows the authored duct route at ventSpeed, continuously, at its real position (the body is hidden inside the
///                 walls). No ground SIGHT runs; hearing is sampled a few times a second and the best new sound is held until the next
///                 junction, where it may change the exit (never mid-duct).
///   Preparing   - waits at the hidden pre-exit point for the warning period (the warning sound plays at the exit), and for the exit
///                 to be clear of every living player.
///   Exiting     - walks out through the mouth onto the NavMesh, re-enables the agent and resumes towards the evidence.
/// Replicated: the phase, and the entry and exit ids (small values for presentation and late joiners); position by the usual
/// NetworkTransform.
/// </summary>
public partial class CreatureAI
{
    [Header("Ventilation")]
    public bool ventEnabled = true;
    [Tooltip("Metres per second along the duct")] public float ventSpeed = 5f;
    [Tooltip("Pause at the mouth before going in (a future animation)")] public float ventEntryDelay = 1f;
    [Tooltip("Warning before it emerges, at the hidden pre-exit point (seconds)")] public float ventWarning = 1.25f;
    [Tooltip("ABSOLUTE minimum seconds between the end of one trip and the start of the next, whatever the desire")] public float ventCooldown = 18f;
    [Tooltip("The trip must save at least this many seconds over walking (after desire and patrol slack)")] public float ventMinAdvantage = 4f;
    [Tooltip("...and take no more than this fraction of the walking time")] [Range(0.2f, 1f)] public float ventMaxTimeFraction = 0.8f;
    [Tooltip("Never bother for destinations closer than this on foot (m)")] public float ventMinGroundMetres = 15f;
    [Tooltip("A sound must have at least this outer range to be worth a vent trip on its own (sprinting 14, doors 10)")] public float ventMinLoudness = 12f;
    [Tooltip("An entrance further than this from the creature is not considered (m)")] public float ventEntryMaxMetres = 40f;
    [Tooltip("While hunting on patrol (no evidence), a vent is taken even if it is only this many seconds slower than the walk")] public float ventPatrolBias = 12f;
    [Tooltip("Penalty (s) for an entrance used a moment ago; fades with age")] public float ventReusePenalty = 15f;

    [Header("Vent cadence (desire; seeded from the round seed)")]
    [Tooltip("The first tactical trip of a round may come this soon after the start (s, random in the range)")] public float ventFirstMin = 10f;
    public float ventFirstMax = 15f;
    [Tooltip("Calm hunting: desire reaches 1 this long after the previous trip (s, random in the range)")] public float ventCalmMin = 40f;
    public float ventCalmMax = 60f;
    [Tooltip("Alert or searching: desire reaches 1 this long after the previous trip (s, random in the range)")] public float ventAlertMin = 20f;
    public float ventAlertMax = 35f;
    [Tooltip("At full frustration the interval shrinks to this fraction (still never below ventCooldown)")] [Range(0.2f, 1f)] public float ventFrustratedFactor = 0.75f;
    [Tooltip("Once it wants a vent, a trip may be this many seconds slower than walking (up to twice this as desire keeps growing)")] public float ventDesireBias = 25f;
    [Tooltip("While it wants a vent, it looks for a trip this often (s)")] public float ventDesireCheckSeconds = 2.5f;
    [Tooltip("While it wants a vent, destinations down to this far on foot are worth it (m)")] public float ventDesireMinGroundMetres = 12f;

    [Header("Vents as a hunting tactic (frustration)")]
    [Tooltip("Frustration starts rising this long after it last SAW a player (s)")] public float ventFrustrationGrace = 20f;
    [Tooltip("...and reaches its maximum this much later (s)")] public float ventFrustrationSeconds = 90f;
    [Tooltip("At full frustration, patrol vent trips are accepted even if this many seconds slower than walking, and it favours far rooms")] public float ventFrustrationBonus = 45f;
    [Tooltip("At full frustration, trips down to this many metres on foot are considered")] public float ventFrustratedMinGround = 6f;

    [Header("Hearing in the duct")]
    [Tooltip("How often it listens while travelling in a duct (s)")] public float ventHearInterval = 0.25f;
    [Tooltip("A sound heard in the duct is kept this long for the next junction (s)")] public float ventPendingMaxAge = 10f;
    [Tooltip("Re-plan at a junction only if the new exit is this many seconds better")] public float ventReplanGain = 3f;

    [Header("Vent emergence fairness (every living player is checked)")]
    [Tooltip("Never emerge with a living player closer than this to the exit (m)")] public float emergeMinPlayerDistance = 2.5f;
    [Tooltip("An exit ANY player can see from closer than this is penalised, unless it is hunting evidence there (m)")] public float emergeViewDistance = 10f;
    [Tooltip("Penalty (s) for an exit in a player's direct view")] public float emergeViewPenalty = 12f;
    [Tooltip("Evidence within this distance of an exit counts as hunting there (m)")] public float emergeNearEvidence = 6f;
    [Tooltip("Wait this long for a blocked or watched exit before trying another")] public float emergeWaitMax = 5f;
    [Tooltip("After this long it emerges anyway, as long as the spot is physically clear")] public float emergeGiveUp = 25f;

    enum HearMode : byte { Act, Pending }

    struct HeardSound
    {
        public Vector3 position;
        public float strength;
        public ulong emitter;
    }

    readonly NetworkVariable<VentPhase> ventPhase = new(VentPhase.None);
    readonly NetworkVariable<double> ventPhaseStart = new(0); // server time the current phase began: a late joiner knows how much of a cue is left
    readonly NetworkVariable<sbyte> ventEntryNet = new(-1); // presentation only: where the entry sounds play
    readonly NetworkVariable<sbyte> ventExitNet = new(-1);  // presentation only: where the warning and exit sounds play
    public VentPhase CurrentVentPhase => ventPhase.Value;
    public int VentEntryId => ventEntryNet.Value;
    public int VentExitId => ventExitNet.Value;
    /// <summary>Seconds since the current vent phase began (server time, so the same on every peer).</summary>
    public double VentPhaseElapsed => NetworkManager != null ? NetworkManager.ServerTime.Time - ventPhaseStart.Value : 0;

    // Every phase change goes through here: the start time is written with it (only on a change, never per frame).
    void SetVentPhase(VentPhase phase)
    {
        ventPhaseStart.Value = NetworkManager.ServerTime.Time;
        ventPhase.Value = phase;
    }

    Renderer[] ventRenderers;
    CapsuleCollider ventCollider;
    NavMeshPath ventPath;
    readonly List<Vector3> ventPoints = new(), ventTmp = new();
    readonly List<(int index, int node)> ventJunctions = new();
    readonly List<int> ventHistory = new();
    readonly List<float> fairDistances = new();
    readonly List<bool> fairViews = new();
    readonly PendingEvidence<HeardSound> ventPending = new();
    float[] ventCum = new float[0], ventExitM = new float[0], ventExitPen = new float[0];
    CreatureState ventResume;
    string ventReason = "-", ventEvidenceNote = "-";
    int ventEntry = -1, ventExit = -1, ventNextJunction;
    bool ventCommitted, ventAltTried, ventFirstOfRound = true;
    float ventTimer, ventWait, ventTravelled, ventLength, ventEnterEnd, ventTick, ventHearTimer, ventWatchdog, ventPlanSeconds, ventGroundSeconds;
    float ventCycleU = 0.5f, ventDesireTimer;
    double lastVentEnd = double.NegativeInfinity, lastSightedAt;
    double ventCycleStart; // desire is measured from here: the round start, then the end of each trip
    int ventTrips;
    System.Random ventRng = new(7); // reseeded every round from the round seed, separately from other choices
    string ventVerdict = "-"; // why the last consideration did or did not lead to a vent (for F3)

    /// <summary>
    /// 0..1: how long it has gone without SEEING a player (after a grace period). The longer it has had no success, the more it wants to
    /// switch tactics: vents come sooner and far, stale rooms are preferred. Based only on its own sightings.
    /// </summary>
    float Frustration() => Mathf.Clamp01(((float)(Time.timeAsDouble - lastSightedAt) - ventFrustrationGrace) / Mathf.Max(1f, ventFrustrationSeconds));

    // How alert it is for cadence purposes: hunting real evidence (searching, investigating or pursuing it) counts as fully alert;
    // otherwise its alertness, which only evidence raises and which fades on patrol. A calm room-to-room hunt, or searching the room it
    // came out of a vent into, is NOT alert, so it does not shorten the cadence by itself.
    float VentAlert() => evidenceKind != EvidenceKind.None && state.Value is CreatureState.Search or CreatureState.Investigate or CreatureState.Pursue
        ? 1f : Mathf.Clamp01(alertness);

    float VentInterval() => VentCadence.Interval(ventFirstOfRound, VentAlert(), ventCycleU, ventFirstMin, ventFirstMax, ventCalmMin, ventCalmMax,
        ventAlertMin, ventAlertMax, Frustration(), ventFrustratedFactor, ventCooldown);

    float VentDesire() => VentCadence.Desire(Time.timeAsDouble - ventCycleStart, VentInterval());

    void NewVentCycle() => ventCycleU = (float)ventRng.NextDouble();

    static CreatureVentNetwork VentNet => CreatureVentNetwork.Instance;
    bool VentReady => ventEnabled && VentNet != null && VentNet.Ready;

    void CacheVentParts()
    {
        ventRenderers ??= GetComponentsInChildren<Renderer>();
        if (ventCollider == null) ventCollider = GetComponent<CapsuleCollider>();
    }

    // Every peer: the body is hidden while inside the walls and ducts, visible at the mouth. Also drives the cosmetic vent audio.
    void OnVentPhaseChanged(VentPhase previous, VentPhase now)
    {
        ApplyVentVisuals(now);
        if (sound != null) sound.OnVentPhase(previous, now);
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

    // Fairness inputs for emerging at a point, from EVERY living player independently (VentRules.AggregateWatch): the nearest distance,
    // and whether anyone close enough can see it. Used ONLY to reject or penalise an unsafe exit; nothing is stored, nothing becomes
    // evidence, heat or memory.
    void ExitFairness(Vector3 point, out float nearest, out bool watched)
    {
        fairDistances.Clear();
        fairViews.Clear();
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.PlayerObject == null || !client.PlayerObject.TryGetComponent(out NetworkFirstPersonController p) || !Alive(p)) continue;
            float dist = Flat(p.transform.position - point).magnitude;
            bool view = false;
            if (dist < emergeViewDistance)
            {
                Vector3 head = p.transform.position + Vector3.up * 1.6f, to = point + Vector3.up - head;
                view = Vector3.Dot(Flat(p.transform.forward).normalized, Flat(to).normalized) > 0.3f
                    && !Physics.Linecast(head, point + Vector3.up, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            }
            fairDistances.Add(dist);
            fairViews.Add(view);
        }
        VentRules.AggregateWatch(fairDistances, fairViews, fairDistances.Count, emergeViewDistance, out nearest, out watched);
    }

    float ExitPenalty(CreatureVentEntrance e, Vector3 target, bool hasTarget)
    {
        ExitFairness(e.approach.position, out float dist, out bool watched);
        bool pursuingHere = hasTarget && Flat(target - e.approach.position).magnitude <= emergeNearEvidence;
        float p = VentRules.ExitPenalty(dist, watched, pursuingHere, emergeMinPlayerDistance, emergeViewDistance, emergeViewPenalty);
        return p + VentRules.ReusePenalty(e.id, ventHistory, ventReusePenalty);
    }

    void EnsureVentBuffers(int n)
    {
        if (ventExitM.Length != n) { ventExitM = new float[n]; ventExitPen = new float[n]; }
    }

    /// <summary>
    /// Considers a vent trip towards <paramref name="target"/> (the creature's own evidence or hunting target). Returns true if one
    /// began. Never with a confirmed sighting, never in a chase, never inside the absolute minimum interval.
    /// </summary>
    bool ConsiderVent(string why, CreatureState resume, Vector3 target, string evidenceNote)
    {
        if (!VentReady) { ventVerdict = "no vent network"; return false; }
        if (state.Value is CreatureState.Bash or CreatureState.Vent) return false;
        double now = Time.timeAsDouble;
        bool sight = NearestRecognised() != null;
        if (!VentCadence.MayConsider(sight, state.Value == CreatureState.Chase, now, lastVentEnd, ventCooldown))
        {
            ventVerdict = sight ? "a player is in sight" : state.Value == CreatureState.Chase ? "chasing" : $"minimum interval ({lastVentEnd + ventCooldown - now:0}s)";
            return false;
        }

        float frustration = resume == CreatureState.Patrol ? Frustration() : 0f;
        float desire = VentDesire();
        float ground = PathMetres(transform.position, target, false);
        bool blocked = ground < 0f;
        if (blocked) ground = Vector3.Distance(transform.position, target) * 1.5f; // no route at all: assume a long way round
        float minGround = Mathf.Lerp(ventMinGroundMetres, ventFrustratedMinGround, frustration);
        if (desire >= 1f) minGround = Mathf.Min(minGround, ventDesireMinGroundMetres);
        if (ground < minGround) { ventVerdict = $"too close to bother ({ground:0} m, needs {minGround:0})"; return false; }
        float walk = ResumeSpeed(resume);
        // On patrol a vent is a tactic, not just a shortcut; and once it WANTS a vent, a little slower trip is still fine.
        float bias = (resume == CreatureState.Patrol ? ventPatrolBias + frustration * ventFrustrationBonus : 0f) + VentCadence.Bias(desire, ventDesireBias);
        float groundSeconds = ground / walk + (blocked ? 15f : 0f) + bias;

        var net = VentNet;
        int n = net.EntranceCount;
        var entryM = new float[n];
        var entryPen = new float[n];
        EnsureVentBuffers(n);
        for (int i = 0; i < n; i++)
        {
            var e = net.entrances[i];
            bool ok = e != null && e.usable && e.approach != null;
            float toEntry = ok ? PathMetres(transform.position, e.approach.position, true) : -1f;
            entryM[i] = toEntry > ventEntryMaxMetres ? -1f : toEntry;
            ventExitM[i] = ok ? PathMetres(e.approach.position, target, false) : -1f; // doors on the ground leg can be opened
            entryPen[i] = ok ? VentRules.ReusePenalty(e.id, ventHistory, ventReusePenalty) : 0f;
            ventExitPen[i] = ok ? ExitPenalty(e, target, true) : 0f;
        }
        var plan = VentRules.Choose(net.Graph, entryM, ventExitM, entryPen, ventExitPen, groundSeconds, walk, ventSpeed, ventMinAdvantage, ventMaxTimeFraction);
        if (!plan.found)
        {
            // For the F3 line: what the best trip would have been, and why it was not good enough.
            var any = VentRules.Choose(net.Graph, entryM, ventExitM, entryPen, ventExitPen, groundSeconds, walk, ventSpeed, -9999f, 9999f);
            ventVerdict = any.found ? $"best trip {any.seconds:0}s vs {groundSeconds:0}s on foot (desire {desire:0.00}, frustration {frustration:0.00}): not worth it"
                : $"no usable entrance/exit pair (desire {desire:0.00})";
            return false;
        }

        ventVerdict = $"taken (desire {desire:0.00}, frustration {frustration:0.00})";
        ventPlanSeconds = plan.seconds;
        ventGroundSeconds = groundSeconds;
        BeginVent(plan.entry, plan.exit, why, resume, evidenceNote);
        return true;
    }

    /// <summary>
    /// While it WANTS a vent (desire at least 1), look for a useful trip every few seconds: towards its evidence when it has some,
    /// otherwise towards a deliberately chosen stale or recently active room. Never into the room it is already in, never while
    /// looking under furniture or still searching the room it is in. Returns true if a trip began.
    /// </summary>
    bool TickVentDesire()
    {
        if (!VentReady || (ventDesireTimer -= Time.deltaTime) > 0f) return false;
        ventDesireTimer = ventDesireCheckSeconds;
        if (VentDesire() < 1f) return false;
        if (state.Value == CreatureState.Search && step == SearchStep.Inspect) return false; // never mid-look under furniture
        if (state.Value == CreatureState.Search && arrived && searchPhase == SearchPhase.Local) return false; // finish the room it is searching first

        var hereRoom = RoomAt(transform.position);
        Vector3 aim;
        string why, note;
        CreatureState resume;
        if (evidenceKind != EvidenceKind.None && state.Value != CreatureState.Patrol)
        {
            aim = evidencePos;
            why = "wants a vent: reposition towards the evidence";
            note = EvidenceNote();
            resume = state.Value;
        }
        else
        {
            if (!PickHuntRoom(preferFar: true, out var room, out _, out _)) { ventVerdict = "wants a vent, but no room to hunt"; return false; }
            aim = RoomAim(room);
            why = $"wants a vent: hunting {room.roomName}";
            note = "room staleness and heat, no player position";
            resume = state.Value == CreatureState.Patrol ? CreatureState.Patrol : CreatureState.Search;
        }
        var aimRoom = RoomAt(aim);
        if (aimRoom != null && aimRoom == hereRoom) { ventVerdict = "wants a vent, but the destination is in this room"; return false; }
        return ConsiderVent(why, resume, aim, note);
    }

    string EvidenceNote() => evidenceKind == EvidenceKind.None ? "none"
        : $"{evidenceKind} {Time.timeAsDouble - evidenceTime:0.0}s ago at ({evidencePos.x:0}, {evidencePos.z:0}), weight {evidenceStrength:0.00}";

    // ---------- Beginning, cancelling, finishing ----------

    void BeginVent(int entry, int exit, string why, CreatureState resume, string evidenceNote)
    {
        ventEntry = entry;
        ventExit = exit;
        ventEntryNet.Value = (sbyte)entry;
        ventExitNet.Value = (sbyte)exit;
        ventResume = resume;
        ventReason = why;
        ventEvidenceNote = evidenceNote;
        ventAltTried = ventCommitted = false;
        ventTimer = ventWait = ventTravelled = 0f;
        ventTick = ventHearTimer = 0f;
        ventPending.Clear();
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
        SetVentPhase(VentPhase.Approaching);
        Debug.Log($"Creature vent: {why}; entrance {entry} to exit {exit}, about {ventPlanSeconds:0.0}s against {ventGroundSeconds:0.0}s on foot. Evidence: {evidenceNote}");
    }

    // Before it has committed (agent still on, collider still on): simply stop.
    void CancelVent(string why)
    {
        SetVentPhase(VentPhase.None);
        ventEntryNet.Value = ventExitNet.Value = -1;
        ventCommitted = false;
        ventPending.Clear();
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

    // Round reset: put everything back. The caller warps the creature to its start afterwards.
    void ResetVent(Vector3 startPosition, int seed)
    {
        bool wasCommitted = ventCommitted || !agent.enabled;
        SetVentPhase(VentPhase.None);
        ventEntryNet.Value = ventExitNet.Value = -1;
        ventCommitted = false;
        ventHistory.Clear();
        ventPoints.Clear();
        ventPending.Clear();
        ventEntry = ventExit = -1;
        ventReason = ventEvidenceNote = "-";
        ventRng = new System.Random(seed);
        ventFirstOfRound = true;
        ventTrips = 0;
        NewVentCycle();
        double now = Time.timeAsDouble;
        lastVentEnd = double.NegativeInfinity; // no trip yet: the minimum interval does not apply, the first-trip range does
        ventCycleStart = now;
        lastSightedAt = now; // frustration counts from the start of the round
        ventDesireTimer = ventDesireCheckSeconds;
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

        // In the duct: listen (never look), and keep the best new sound for the next junction.
        if (phase == VentPhase.Travelling && (ventHearTimer -= dt) <= 0f)
        {
            ventHearTimer = ventHearInterval;
            Hear(HearMode.Pending);
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

    // Called by Hear in Pending mode: the best accepted sound is held, not acted on, until a junction.
    void OfferPendingSound(Vector3 position, float strength, ulong emitter, bool pursued, double now)
    {
        var sound = new HeardSound { position = position, strength = strength, emitter = emitter };
        if (ventPending.Offer(sound, strength + (pursued ? 2f : 0f), now, now, ventPendingMaxAge))
            ventVerdict = $"heard something in the duct ({strength:0.00}), held for the next junction";
    }

    // A held sound becomes ordinary evidence (and room heat) when the creature reaches a junction or the end of the route.
    bool AdoptPendingSound()
    {
        if (!ventPending.TryTake(Time.timeAsDouble, ventPendingMaxAge, out var sound)) return false;
        lastSwitchTime = Time.timeAsDouble;
        SetEvidence(EvidenceKind.Noise, sound.position, sound.strength, sound.emitter);
        return true;
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
        SetVentPhase(VentPhase.Entering);
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
            if (ventTravelled >= ventEnterEnd) SetVentPhase(VentPhase.Travelling);
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
            if (AdoptPendingSound()) ventEvidenceNote = EvidenceNote(); // heard on the last stretch: it heads there after emerging
            ventTimer = ventWait = 0f;
            SetVentPhase(VentPhase.Preparing); // the warning plays at the exit from the phase change, on every peer
        }
    }

    // At an actual junction: a sound held since the last one becomes evidence, and may change the exit if that is clearly better
    // under the gain rule. Nothing is read from any player.
    bool ReplanAtJunction(int node)
    {
        if (!AdoptPendingSound()) return false;
        var net = VentNet;
        int n = net.EntranceCount;
        EnsureVentBuffers(n);
        for (int j = 0; j < n; j++)
        {
            var e = net.entrances[j];
            bool ok = e != null && e.usable && e.approach != null;
            ventExitM[j] = ok ? PathMetres(e.approach.position, evidencePos, false) : -1f;
            ventExitPen[j] = ok ? ExitPenalty(e, evidencePos, true) : 0f;
        }
        float walk = ResumeSpeed(ventResume);
        int best = VentRules.BestExitFrom(net.Graph, node, ventExitM, ventExitPen, walk, ventSpeed, null);
        ventEvidenceNote = EvidenceNote();
        if (best < 0 || best == ventExit) return false;
        float Score(int j) => net.Graph.Distance(node, j) / ventSpeed + ventExitM[j] / walk + ventExitPen[j];
        float current = ventExit >= 0 && ventExitM[ventExit] >= 0f ? Score(ventExit) : float.PositiveInfinity;
        if (Score(best) + ventReplanGain >= current) return false;
        if (!SwitchExit(node, best)) return false;
        ventReason += " (re-planned at a junction)";
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
        ventExitNet.Value = (sbyte)newExit;
        if (ventPhase.Value != VentPhase.Travelling) SetVentPhase(VentPhase.Travelling);
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
        ExitFairness(exit.approach.position, out float nearest, out bool seen);
        bool tooClose = nearest < emergeMinPlayerDistance;
        bool hunting = evidenceKind != EvidenceKind.None && Flat(evidencePos - exit.approach.position).magnitude <= emergeNearEvidence;
        bool watched = seen && !hunting;

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
        Vector3 aim = evidenceKind != EvidenceKind.None ? evidencePos : transform.position;
        EnsureVentBuffers(n);
        for (int j = 0; j < n; j++)
        {
            var e = net.entrances[j];
            bool ok = e != null && e.usable && e.approach != null && !ExitObstructed(e);
            ventExitM[j] = ok ? PathMetres(e.approach.position, aim, false) : -1f;
            ventExitPen[j] = ok ? ExitPenalty(e, aim, evidenceKind != EvidenceKind.None) : 0f;
        }
        int alt = VentRules.BestExitFrom(net.Graph, ventExit, ventExitM, ventExitPen, ResumeSpeed(ventResume), ventSpeed, new HashSet<int> { ventExit });
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
        SetVentPhase(VentPhase.Exiting); // visible again from here; still inside the wall
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
        SetVentPhase(VentPhase.None);
        ventEntryNet.Value = ventExitNet.Value = -1;
        ventPending.Clear();
        lastVentEnd = ventCycleStart = Time.timeAsDouble;
        ventFirstOfRound = false;
        ventTrips++;
        NewVentCycle();
        ventHistory.Add(ventEntry);
        ventHistory.Add(ventExit);
        while (ventHistory.Count > 6) ventHistory.RemoveAt(0);
        Debug.Log($"Creature vent: emerged at entrance {ventExit} ({exit.room?.roomName}); trip {ventTrips} this round.");
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
        double minLeft = ventCooldown - (Time.timeAsDouble - lastVentEnd);
        string gate = minLeft > 0 ? $"min interval {minLeft:0}s" : "ready";
        string cadence = $"desire {VentDesire():0.00} (interval {VentInterval():0}s{(ventFirstOfRound ? ", first trip" : "")}, alert {VentAlert():0.00}), {gate}, frustration {Frustration():0.00}, trips {ventTrips}";
        var phase = ventPhase.Value;
        if (phase == VentPhase.None)
            return $"idle, {cadence}\n  last: {ventReason}\n  last decision: {ventVerdict}\n  evidence used: {ventEvidenceNote}";
        string route = ventCommitted ? $"{ventTravelled:0.0}/{ventLength:0.0} m, {VentRules.RemainingSeconds(ventLength, ventTravelled, ventSpeed):0.0}s left" : "not committed";
        string en = ventEntry >= 0 ? $"#{ventEntry}" : "-", ex = ventExit >= 0 ? $"#{ventExit}" : "-";
        string held = ventPending.Has ? $", holding a sound ({Time.timeAsDouble - ventPending.Time:0.0}s old)" : "";
        return $"{phase} entry {en} exit {ex}, {route}{held}\n  {cadence}\n  why: {ventReason}\n  evidence used: {ventEvidenceNote}";
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
