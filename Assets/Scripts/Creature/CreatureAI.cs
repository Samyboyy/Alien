using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

public enum CreatureState : byte { Patrol, Chase, Search, Investigate, Bash, Pursue }

public enum EvidenceKind : byte { None, Sight, Noise }

/// <summary>
/// Sight- and hearing-driven creature. ONLY the host runs perception, decisions, memory and the NavMeshAgent; the agent is
/// disabled on clients. A server-authority NetworkTransform replicates movement; small NetworkVariables replicate the
/// behaviour state and alert level (used for the tint and debug label).
///
/// Evidence: the last confirmed sighting, or a sound event's position (a snapshot, never tracked afterwards).
/// Chase -> (sight lost) -> Pursue: chase speed towards the last evidence by NavMesh, for as long as fresh evidence keeps
/// arriving (sighting or the pursued player's audible footsteps/breathing); only when it stops for pursueGrace seconds does
/// it slow to Search. Search (CreatureAI.Search.cs) is structured: the room holding the evidence, a limited number of
/// hiding places and ordinary points, then a few connected rooms, then a heightened patrol that calms over time.
/// Hearing: each sound has an outer range; strength falls with distance and occlusion (walls, closed doors). Incidental
/// sounds (doors, impacts) never erase a fresh player trail.
/// Sight (CreatureAI.Sight.cs): gradual. Each player has their own awareness; only full awareness plus a real unobstructed body
/// sample is a confirmed sighting, and only that writes a chase destination or allows a capture.
/// Doors: a closed door is a carved-out hole in the NavMesh and the creature cannot use switches. When a route ends at a
/// closed door between it and its goal, it does a short wind-up, forces that door open (if SlidingDoor.creatureCanOpen),
/// and resumes as soon as the gap is wide enough and the NavMesh has a route.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public partial class CreatureAI : NetworkBehaviour, IRoundResettable
{
    [Header("Patrol")]
    public Transform[] patrolPoints;
    public float patrolSpeed = 1.8f;
    public float patrolWait = 2f;

    [Header("Chase")]
    [Tooltip("Chase and pursuit speed (players: walk 2.8, sprint 5.5)")] public float chaseSpeed = 4.5f;
    [Tooltip("Agent stops this far from the target (clamped below captureDistance so capture range is reachable)")] public float stopDistance = 0.9f;
    [Tooltip("Host kills a living player closer than this with an unobstructed line")] public float captureDistance = 1.4f;

    [Header("Pursuit (after sight is lost)")]
    [Tooltip("Pursuit only ends after this many seconds WITHOUT fresh evidence (new sighting, or the pursued player's audible sound)")] public float pursueGrace = 2.5f;
    [Tooltip("Strength (0..1) of the pursued player's sound that sends a searching creature back into pursuit")] public float pursueResumeStrength = 0.2f;

    [Header("Sight")]
    public float sightDistance = 15f;
    [Range(10, 360)] public float fieldOfView = 110f;
    [Tooltip("Players this close are sensed outside the field of view too (still needs a clear line)")] public float closeSenseDistance = 2.5f;
    public float eyeHeight = 1.6f;

    [Header("Hearing")]
    [Tooltip("Minimum strength (0..1) for a sound to count at all")] public float minStrength = 0.05f;
    [Tooltip("A sound event older than this is ignored; each event is handled at most once")] public float soundStaleSeconds = 1.5f;
    [Tooltip("Metres of extra distance per wall between the creature and the sound (muffles, never blocks completely)")] public float wallPenalty = 6f;
    public float doorPenalty = 3f;
    [Tooltip("Low cover such as crates, tables and beds")] public float obstaclePenalty = 1.5f;
    [Tooltip("A sound at the very edge of its range can place the evidence up to this far from the real spot")] public float maxPositionError = 4f;
    [Tooltip("A sound must be this many times stronger than the current evidence to replace it (unless it is the pursued player's trail)")] public float switchMargin = 1.5f;
    [Tooltip("Minimum seconds between switching evidence to a different/incidental sound")] public float switchCooldown = 1.5f;
    [Tooltip("Remembered evidence loses its weight over this many seconds")] public float evidenceFadeSeconds = 8f;
    public float investigateSpeed = 3f;

    [Header("Search movement")]
    public float searchSpeed = 2.5f;
    [Tooltip("Used only when the evidence is outside every room volume")] public float searchRadius = 3f;

    [Header("Doors")]
    [Tooltip("Wind-up before forcing a door while pursuing. Placeholder timing: an opening animation can replace it (see DoorWindupFinished).")] public float doorWindupPursuit = 0.5f;
    [Tooltip("Wind-up before forcing a door while investigating or searching")] public float doorWindupInvestigate = 1f;
    [Tooltip("Only doors this close to where the route stops are candidates")] public float doorSearchRadius = 4f;
    [Tooltip("Progress on a door is kept this long, so closing it again does not restart the wind-up")] public float doorProgressMemory = 10f;
    [Tooltip("Resume through the door once it is at least this open AND the NavMesh has a complete route")] [Range(0.3f, 1f)] public float minOpenFraction = 0.6f;
    [Tooltip("If the route is still incomplete this long after the door stopped moving, resume anyway")] public float doorSettleTimeout = 1f;

    [Tooltip("Give up on a destination after this long (blocked / unreachable)")]
    public float maxTravelTime = 20f;

    [Header("Debug / visuals")]
    public Renderer bodyRenderer; // tinted by state on every peer
    [Tooltip("F3 toggles it at runtime; also draws recent sound ranges as gizmos on the host")] public bool showDebugLabel;

    readonly NetworkVariable<CreatureState> state = new(CreatureState.Patrol);
    readonly NetworkVariable<AlertLevel> alertLevel = new(AlertLevel.Calm); // changes rarely; lets every peer show it
    readonly NetworkVariable<ulong> targetId = new(ulong.MaxValue); // debug only

    readonly RaycastHit[] hits = new RaycastHit[8];
    static readonly float[] bodySamples = { 0.9f, 0.55f, 0.2f }; // fractions of the player's CURRENT capsule height (head, chest, legs)
    NavMeshPath path;
    NavMeshAgent agent;
    NetworkFirstPersonController target;
    SlidingDoor bashDoor; // door being opened right now, if any
    CreatureState resumeState;
    Vector3 evidencePos, searchCenter, goalPos, startPos; // evidencePos = remembered destination (host only)
    EvidenceKind evidenceKind;
    float evidenceStrength;
    bool evidenceCrouched; // the player was crouched when last SEEN there
    ulong evidenceEmitter = NoiseSystem.NoEmitter; // player the evidence came from, if any
    double evidenceTime, lastSwitchTime, lastTrailTime;
    Quaternion startRot;
    bool active; // false until the round starts / after it ends
    int patrolIndex;
    bool hasDestination, arrived, routeClear;
    float perceptionTimer, waitTimer, travelTimer, doorSettle, doorWindup, routeTimer;
    SlidingDoor[] doors = new SlidingDoor[0];
    SlidingDoor memoDoor; // door we were working on, and how far we got
    float memoProgress;
    double memoTime;
    ulong handledNoiseId; // noise events up to this id were already handled
    NoiseSystem.Noise lastSound; // debug: last accepted sound
    float lastSoundStrength;
    double lastSoundTime;
    string reason = "-"; // debug: why the state last changed

    /// <summary>Replicated behaviour state, readable on every peer (used by the cosmetic CreatureAudio).</summary>
    public CreatureState CurrentState => state.Value;

    /// <summary>
    /// Readable on every peer (replicated state): is it in a confirmed chase of exactly this player? Only ever asked about the
    /// asking player's own id, so it tells nobody anything about another player. Used by the cosmetic ThreatVisuals only.
    /// </summary>
    public bool IsChasing(ulong clientId) => state.Value == CreatureState.Chase && targetId.Value == clientId;

    /// <summary>Where it is currently trying to get to: the evidence while travelling to it, else the current search point.</summary>
    Vector3 CurrentGoal => arrived ? goalPos : evidencePos;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        path = new NavMeshPath();
        searchPath = new NavMeshPath();
        startPos = transform.position;
        startRot = transform.rotation;
    }

    public override void OnNetworkSpawn()
    {
        state.OnValueChanged += OnStateChanged;
        Tint(state.Value);
        if (AudioBank.Get() != null && GetComponent<CreatureAudio>() == null) gameObject.AddComponent<CreatureAudio>(); // cosmetic footsteps and snarl

        agent.enabled = false; // clients never run navigation
        if (!IsServer) return;
        active = false; // idle until RoundManager starts the round
        doors = FindObjectsByType<SlidingDoor>(FindObjectsSortMode.None);
        handledNoiseId = NoiseSystem.LastId;

        if (NavMesh.SamplePosition(transform.position, out var hit, 3f, NavMesh.AllAreas))
        {
            transform.position = hit.position;
            agent.enabled = true;
            EnterPatrol("spawned");
        }
        else
            Debug.LogWarning("Creature: no NavMesh near start position. Run Alien > Rebake Navigation and save the scene.");
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnStateChanged;
        agent.enabled = false;
    }

    void OnStateChanged(CreatureState _, CreatureState s) => Tint(s);

    void Tint(CreatureState s)
    {
        if (bodyRenderer == null) return;
        Color c = s switch
        {
            CreatureState.Chase => Color.red,
            CreatureState.Pursue => new Color(1f, 0.2f, 0.45f), // chasing evidence, target not in sight
            CreatureState.Search => Color.yellow,
            CreatureState.Investigate => new Color(1f, 0.55f, 0f),
            CreatureState.Bash => new Color(0.6f, 0.2f, 1f),
            _ => Color.green,
        };
        bodyRenderer.material.SetColor("_BaseColor", c); // HDRP/Lit property
    }

    // ---------- Host-only AI ----------

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.f3Key.wasPressedThisFrame) showDebugLabel = !showDebugLabel;

        if (!IsServer || !active || !agent.enabled || !agent.isOnNavMesh) return;

        UpdateAlertness();

        // Perception at 10 Hz is plenty and keeps raycasts cheap.
        bool tick = (perceptionTimer -= Time.deltaTime) <= 0f;
        if (tick)
        {
            perceptionTimer = 0.1f;
            UpdateSight(); // per-player awareness first, so Acquire / UpdateChase see this tick's values
        }

        switch (state.Value)
        {
            case CreatureState.Chase:
                if (tick) UpdateChase();
                break;
            case CreatureState.Bash:
                if (tick && Acquire()) return;
                if (tick) Hear(); // may move the remembered destination; the door attempt carries on
                UpdateBash();
                break;
            default: // Patrol, Search, Investigate, Pursue: sight first, then hearing
                if (tick && (Acquire() || Hear())) return;
                if (state.Value == CreatureState.Patrol) UpdatePatrol();
                else UpdateSearch();
                break;
        }
    }

    void EnterPatrol(string why)
    {
        state.Value = CreatureState.Patrol;
        reason = why;
        targetId.Value = ulong.MaxValue;
        target = null;
        bashDoor = null;
        chaseInspectSpot = null;
        ClearEvidence(); // gave up (or fresh start): nothing remembered while patrolling...
        ClearSearch(); // ...and no search in progress. Alertness is kept: it decays on its own while patrolling.
        agent.speed = patrolSpeed;
        agent.stoppingDistance = 0.3f;
        hasDestination = false;
        waitTimer = 0f;
    }

    void SetEvidence(EvidenceKind kind, Vector3 pos, float strength, ulong emitter, bool crouched = false)
    {
        evidenceKind = kind;
        evidencePos = pos;
        evidenceStrength = strength;
        evidenceEmitter = emitter;
        evidenceCrouched = crouched;
        evidenceTime = Time.timeAsDouble;
        if (emitter != NoiseSystem.NoEmitter) lastTrailTime = evidenceTime; // a player's sighting or sound: fresh trail
    }

    void ClearEvidence()
    {
        evidenceKind = EvidenceKind.None;
        evidenceEmitter = NoiseSystem.NoEmitter;
        evidenceCrouched = false;
    }

    float SpeedFor(CreatureState s) =>
        s == CreatureState.Pursue || s == CreatureState.Chase ? chaseSpeed : s == CreatureState.Investigate ? investigateSpeed : searchSpeed;

    // Go to the remembered evidence; on arrival the structured room search starts (CreatureAI.Search.cs).
    // Search/Investigate/Pursue differ only in speed and what ends the approach: Pursue keeps chase speed while fresh evidence
    // keeps arriving, Investigate was started by a noise.
    // Also used when evidence changes: the remembered destination is kept, never re-read from a player.
    void EnterSearch(CreatureState s, string why)
    {
        state.Value = s;
        reason = why;
        targetId.Value = ulong.MaxValue;
        target = null;
        bashDoor = null;
        chaseInspectSpot = null;
        agent.speed = SpeedFor(s);
        agent.stoppingDistance = 0.3f;
        arrived = false;
        ClearSearch(false); // new evidence: start the room search afresh on arrival (what was searched stays remembered)
        GoTo(evidencePos);
    }

    // Pursuit has run out of steam: same evidence, slower, then the local search.
    void DowngradeToSearch(string why)
    {
        state.Value = CreatureState.Search;
        reason = why;
        agent.speed = searchSpeed;
    }

    void EnterChase(NetworkFirstPersonController p, string why)
    {
        state.Value = CreatureState.Chase;
        reason = why;
        bashDoor = null; // sight interrupts any door attempt (progress is kept)
        agent.speed = chaseSpeed;
        agent.stoppingDistance = Mathf.Min(stopDistance, captureDistance * 0.7f);
        SetTarget(p);
    }

    void SetTarget(NetworkFirstPersonController p)
    {
        target = p;
        targetId.Value = p.OwnerClientId;
    }

    // Only a RECOGNISED player with an unobstructed sample this tick is a confirmed sighting (see CreatureAI.Sight.cs).
    bool Acquire()
    {
        var p = NearestRecognised();
        if (p == null) return false;
        HidingSpot inspected = InspectingOpening(Sight(p.OwnerClientId).spot) ? Sight(p.OwnerClientId).spot : null; // before the search state is cleared
        SetEvidence(EvidenceKind.Sight, p.transform.position, 1f, p.OwnerClientId, p.IsCrouched);
        alertness = 1f;
        ClearSearch(); // a confirmed sighting: old search history no longer applies
        chaseInspectSpot = inspected;
        EnterChase(p, inspected != null ? $"found a player inspecting {inspected.name}" : "recognised a player");
        agent.SetDestination(evidencePos);
        return true;
    }

    // ---------- Hearing ----------

    // Handles every new sound event once. Among those that pass the evidence rule, the pursued player's trail wins,
    // otherwise the strongest. Returns true when it took over the creature's behaviour this tick.
    bool Hear()
    {
        double now = Time.timeAsDouble;
        bool trail = evidenceKind != EvidenceKind.None && evidenceEmitter != NoiseSystem.NoEmitter;
        float curScore = evidenceKind == EvidenceKind.None ? 0f
            : EscapeRules.EvidenceScore(evidenceStrength, (float)(now - evidenceTime), evidenceFadeSeconds);
        bool cooldownOver = now - lastSwitchTime >= switchCooldown;

        ulong newest = handledNoiseId;
        bool found = false, pickPursued = false;
        NoiseSystem.Noise pick = default;
        float pickStrength = 0f, pickKey = -1f;
        foreach (var n in NoiseSystem.Recent)
        {
            if (n.id > newest) newest = n.id;
            if (n.emitter == NoiseSystem.CreatureEmitter) continue; // its own noises never distract it
            if (!EscapeRules.IsFresh(n.id, handledNoiseId, (float)(now - n.time), soundStaleSeconds)) continue;

            float straight = Vector3.Distance(transform.position, n.position);
            if (straight > n.loudness) continue; // outside its outer range
            float strength = EscapeRules.SoundStrength(n.loudness, OccludedDistance(straight, n.position));
            if (strength < minStrength) continue;

            bool pursued = trail && n.IsPlayerSound && n.emitter == evidenceEmitter;
            if (!EscapeRules.AcceptNoise(curScore, trail, pursued, !n.IsPlayerSound, strength, switchMargin, cooldownOver)) continue;

            float key = strength + (pursued ? 2f : 0f);
            if (key >= pickKey) { found = true; pick = n; pickStrength = strength; pickKey = key; pickPursued = pursued; }
        }
        handledNoiseId = newest; // everything seen this tick is consumed, accepted or not
        if (!found) return false;

        // Louder/closer = more accurate. The offset is a one-off blur of the position at emission time, nothing is tracked.
        Vector3 pos = pick.position;
        float error = EscapeRules.PositionError(pickStrength, maxPositionError);
        if (error > 0.05f)
        {
            Vector2 r = Random.insideUnitCircle * error;
            pos += new Vector3(r.x, 0f, r.y);
        }
        pos = NavMesh.SamplePosition(pos, out var snap, 4f, NavMesh.AllAreas) ? snap.position : pick.position;

        if (pick.IsPlayerSound) Sight(pick.emitter).heardTime = now; // a sound only, for the death tip; it grants no sight
        lastSound = pick;
        lastSoundStrength = pickStrength;
        lastSoundTime = now;
        if (!pickPursued) lastSwitchTime = now;
        alertness = Mathf.Max(alertness, 0.5f + 0.5f * pickStrength); // heard something: more alert, never told where anyone is
        SetEvidence(EvidenceKind.Noise, pos, pickStrength, pick.IsPlayerSound ? pick.emitter : NoiseSystem.NoEmitter);

        if (state.Value == CreatureState.Bash)
        {
            // Evidence moved while a door is being opened: carry on with the door, then resume towards the NEW evidence.
            arrived = false;
            ClearSearch(false);
            return false;
        }

        string why = $"heard {pick.kind} ({pickStrength:0.00})";
        if (pickPursued && (state.Value == CreatureState.Pursue || pickStrength >= pursueResumeStrength))
            EnterSearch(CreatureState.Pursue, why + " from the pursued player");
        else
            EnterSearch(CreatureState.Investigate, why);
        return true;
    }

    // Lightweight occlusion: ONE ray from the creature's eye to the sound; each wall, closed door or low obstacle on the way
    // adds a distance penalty. No path queries, and a sound behind a closed door is muffled, not lost.
    // ponytail: straight-line approximation, sound does not bend around corners; use NavMesh distance if that feels wrong.
    float OccludedDistance(float straight, Vector3 source)
    {
        Vector3 from = EyePoint, to = source + Vector3.up;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.1f) return straight;

        int count = Physics.RaycastNonAlloc(from, d / len, hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        int walls = 0, doorCount = 0, obstacles = 0;
        for (int i = 0; i < count; i++)
        {
            var col = hits[i].collider;
            if (col.GetComponentInParent<NetworkFirstPersonController>() != null || col.GetComponentInParent<CreatureAI>() != null) continue;
            if (Vector3.Distance(col.ClosestPoint(source), source) < 0.75f) continue; // the thing that made the sound
            if (col.GetComponentInParent<SlidingDoor>() != null) doorCount++;
            else if (col.bounds.size.y >= 3f) walls++;
            else obstacles++;
        }
        return EscapeRules.EffectiveDistance(straight, walls, doorCount, obstacles, wallPenalty, doorPenalty, obstaclePenalty);
    }

    // ---------- Chase ----------

    void UpdateChase()
    {
        // Capture needs a legitimately recognised player (confirmed this tick) plus reach and an unobstructed line. Nothing here
        // depends on stance: crouched players are not easier to catch, and tabletops, beds, walls and closed doors block the line.
        if (target != null && target.IsSpawned && Alive(target) && Confirmed(target) && CanReach(target))
        {
            Capture(target);
            return;
        }

        // Keep the current target while it stays recognised (a brief flicker is held); otherwise take the nearest recognised player.
        // A disconnected target is a destroyed object, so the null check covers it.
        bool gone = target == null || !target.IsSpawned || !Alive(target); // disconnected, dead or escaped
        if (gone || !KeepsTarget(target))
        {
            var next = NearestRecognised();
            if (next == null)
            {
                // Target left play: nothing to chase, but it stays alert (patrols heightened). Target merely out of sight:
                // keep chasing the last CONFIRMED position (evidencePos is only ever written from a confirmed sighting).
                if (gone) { EnterPatrol("target left play"); return; }
                NoteWitnessedSpot();
                EnterSearch(CreatureState.Pursue, "lost sight, following last evidence");
                return;
            }
            SetTarget(next);
        }
        if (!Confirmed(target)) return; // flicker hold: carry on to the last confirmed position, no new one is taken
        SetEvidence(EvidenceKind.Sight, target.transform.position, 1f, target.OwnerClientId, target.IsCrouched); // only while actually seen
        agent.SetDestination(evidencePos);
    }

    float FlatDistance(NetworkFirstPersonController p)
    {
        Vector3 d = p.transform.position - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    static bool Alive(NetworkFirstPersonController p) => !p.TryGetComponent(out PlayerLife life) || life.IsAlive;

    // ---------- Round control (host only, called by RoundManager) ----------

    /// <summary>Back to the start position, hunting. Clears all evidence, alertness, search history, door attempts and timers.</summary>
    public void ResetForRound(System.Random rng)
    {
        if (!IsServer) return;
        active = true;
        handledNoiseId = NoiseSystem.LastId; // RoundManager cleared the noise list; never act on older events
        lastSound = default;
        lastSoundStrength = 0f;
        lastSwitchTime = lastTrailTime = double.NegativeInfinity;
        sights.Clear(); // every player's awareness and detection history
        lastSightTime = Time.timeAsDouble;
        chaseInspectSpot = null;
        bashDoor = null;
        memoDoor = null;
        memoProgress = 0f;
        travelTimer = 0f;
        alertness = 0f;
        alertLevel.Value = AlertLevel.Calm;
        ClearEvidence();
        ClearSearch();
        doors = FindObjectsByType<SlidingDoor>(FindObjectsSortMode.None);
        patrolIndex = 0;
        if (!agent.enabled) return;
        agent.ResetPath();
        agent.Warp(startPos);
        transform.rotation = startRot;
        GetComponent<NetworkTransform>().Teleport(startPos, startRot, transform.localScale);
        EnterPatrol("round reset");
    }

    public void OnRoundOver() => Stop();

    /// <summary>Freeze in place (round over).</summary>
    public void Stop()
    {
        if (!IsServer) return;
        active = false;
        if (agent.enabled && agent.isOnNavMesh) { agent.ResetPath(); agent.velocity = Vector3.zero; }
    }

    // ---------- Patrol / approach ----------

    void UpdatePatrol()
    {
        if (patrolPoints == null || patrolPoints.Length == 0) return;

        if (!hasDestination)
        {
            travelTimer = 0f;
            hasDestination = true;
            agent.speed = patrolSpeed * (1f + alertPatrolSpeedBonus * alertness); // heightened patrol while still alert
            // Unreachable / off-mesh point: pretend we timed out so we move on next frame.
            if (!GoTo(patrolPoints[patrolIndex].position)) travelTimer = maxTravelTime;
            return;
        }

        travelTimer += Time.deltaTime;
        if (!Arrived() && travelTimer < maxTravelTime) return;

        // ponytail: no stuck detection beyond maxTravelTime; add if the creature snags on geometry.
        waitTimer += Time.deltaTime;
        if (waitTimer >= patrolWait * (1f - 0.5f * alertness))
        {
            waitTimer = 0f;
            patrolIndex = (patrolIndex + 1) % patrolPoints.Length;
            hasDestination = false;
        }
    }

    // Pursue / Investigate / Search: travel to the evidence, then search around it.
    void UpdateSearch()
    {
        if (arrived) { UpdateRoomSearch(); return; }

        travelTimer += Time.deltaTime;
        float sinceFresh = (float)(Time.timeAsDouble - lastTrailTime);
        bool pursuing = state.Value == CreatureState.Pursue;
        if (pursuing && SearchRules.PursuitExpired(sinceFresh, pursueGrace))
            DowngradeToSearch("no fresh evidence, pursuit grace ran out");

        if (agent.pathPending) return;
        bool atEnd = agent.pathStatus == NavMeshPathStatus.PathInvalid || agent.remainingDistance <= agent.stoppingDistance + 0.1f;
        bool timedOut = travelTimer > maxTravelTime;
        if (!atEnd && !timedOut) return;

        // Stopped at the end of a PARTIAL path: the route is cut by a closed door, so this is not the remembered spot.
        if (!timedOut && agent.pathStatus == NavMeshPathStatus.PathPartial && TryOpenDoor()) return;

        // Still pursuing with fresh evidence: stay on it. Wait at the evidence for the next sighting/footstep instead of
        // starting a search while the player is still audible.
        if (state.Value == CreatureState.Pursue) return;

        // Reached the remembered spot (complete path), or as close as the NavMesh allows (reinforced door / no route).
        searchCenter = agent.pathStatus == NavMeshPathStatus.PathComplete ? evidencePos : transform.position;
        arrived = true;
        BeginRoomSearch();
    }

    // ---------- Doors ----------

    // The route to the goal ends here at a partial path. Pick the closed, openable door that actually lies between
    // us and the goal: it must be near where the route stopped AND have us and the goal on opposite sides.
    // Among those, the shortest detour wins. Other doors (beside us, on our side, or reinforced) are never considered.
    // An alternate route needs no code: the NavMesh already prefers any complete path around a carved door.
    bool TryOpenDoor()
    {
        Vector3 here = Flat(transform.position), dest = Flat(CurrentGoal);
        SlidingDoor best = null;
        float bestDetour = float.MaxValue;
        foreach (var d in doors)
        {
            if (d == null || !d.IsSpawned || d.IsOpen || !d.creatureCanOpen) continue;
            Vector3 dp = d.ClosedPosition, n = d.Normal;
            float detour = EscapeRules.DoorDetour(here.x, here.z, dest.x, dest.z, dp.x, dp.z, n.x, n.z, doorSearchRadius);
            if (detour < 0f) continue; // too far, or on our side: not the door in the way
            if (detour < bestDetour) { bestDetour = detour; best = d; }
        }
        if (best == null) return false;

        // Keep progress if we were already working on this door, so closing it again cannot stall us forever.
        bool continuing = best == memoDoor && Time.timeAsDouble - memoTime < doorProgressMemory;
        memoProgress = continuing ? memoProgress : 0f;
        memoDoor = best;
        bashDoor = best;
        resumeState = state.Value;
        doorWindup = resumeState == CreatureState.Pursue || resumeState == CreatureState.Chase ? doorWindupPursuit : doorWindupInvestigate;
        doorSettle = doorSettleTimeout;
        routeTimer = 0f;
        routeClear = false;
        state.Value = CreatureState.Bash;
        reason = $"blocked by {best.name}";
        agent.ResetPath();
        return true;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // The door action has two phases: a wind-up (placeholder timer) and the physical opening (host-driven by SlidingDoor).
    // To add an animation later, start it when the wind-up begins and make DoorWindupFinished wait for its contact event.
    bool DoorWindupFinished() => memoProgress >= doorWindup;

    void UpdateBash()
    {
        if (bashDoor == null) { ResumeAfterDoor("door attempt lost"); return; }

        if (bashDoor.IsOpen) // forced by us, or opened by a player meanwhile
        {
            // Resume as soon as the gap is physically wide enough AND the NavMesh (carve) offers a route. The timeout only
            // covers a carve that has not caught up after the door stopped, so it never waits longer than needed.
            if (!bashDoor.IsMoving) doorSettle -= Time.deltaTime;
            if (bashDoor.OpenFraction >= minOpenFraction && (RouteClear() || doorSettle <= 0f))
                ResumeAfterDoor($"{bashDoor.name} open, resuming");
            return;
        }

        doorSettle = doorSettleTimeout;
        Vector3 to = Flat(bashDoor.ClosedPosition - transform.position);
        if (to.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(to), 360f * Time.deltaTime);

        memoProgress += Time.deltaTime;
        memoTime = Time.timeAsDouble;
        if (!DoorWindupFinished()) return;

        memoDoor = null; // done with it; a later closing starts a fresh attempt
        memoProgress = 0f;
        if (!bashDoor.ForceOpen()) ResumeAfterDoor("door is reinforced"); // path again; search/give-up rules apply
    }

    // Back to what it was doing, with search progress and memory intact, heading for the same (or freshly updated) goal.
    void ResumeAfterDoor(string why)
    {
        state.Value = resumeState;
        reason = why;
        bashDoor = null;
        agent.speed = SpeedFor(resumeState);
        GoTo(CurrentGoal);
    }

    // Complete NavMesh path to the current goal right now (checked at most 10 times a second).
    bool RouteClear()
    {
        if ((routeTimer -= Time.deltaTime) > 0f) return routeClear;
        routeTimer = 0.1f;
        routeClear = NavMesh.SamplePosition(CurrentGoal, out var hit, 2f, NavMesh.AllAreas)
            && NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, path)
            && path.status == NavMeshPathStatus.PathComplete;
        return routeClear;
    }

    // Snap to the NavMesh, then path. False when the point is off the mesh or has no path at all.
    // A partial path (e.g. target behind a closed door) still returns true: the creature walks as close as it can.
    bool GoTo(Vector3 pos)
    {
        travelTimer = 0f;
        goalPos = pos;
        if (!NavMesh.SamplePosition(pos, out var hit, 2f, NavMesh.AllAreas)) return false;
        return agent.SetDestination(hit.position);
    }

    bool Arrived() =>
        !agent.pathPending &&
        (agent.pathStatus == NavMeshPathStatus.PathInvalid || agent.remainingDistance <= agent.stoppingDistance + 0.1f);

    // ---------- Sight ----------

    // While looking under furniture from its opening the eye is low (a future crouch/peek animation would drive this).
    Vector3 EyePoint => transform.position + Vector3.up * (LowEyeActive ? inspectEyeHeight : eyeHeight);

    static Vector3 BodyPoint(NetworkFirstPersonController p, float fraction) =>
        p.transform.position + Vector3.up * (p.GetComponent<CharacterController>().height * fraction);

    // ---------- Debug ----------

    void OnDrawGizmosSelected()
    {
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(eye, sightDistance);
        Gizmos.color = Color.red;
        Gizmos.DrawRay(eye, Quaternion.Euler(0f, fieldOfView * 0.5f, 0f) * transform.forward * sightDistance);
        Gizmos.DrawRay(eye, Quaternion.Euler(0f, -fieldOfView * 0.5f, 0f) * transform.forward * sightDistance);
        Gizmos.DrawWireSphere(transform.position, closeSenseDistance);

        if (!Application.isPlaying) return;
        if (evidenceKind != EvidenceKind.None) // host only: remembered destination
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(evidencePos, 0.5f);
            Gizmos.DrawLine(transform.position + Vector3.up, evidencePos + Vector3.up);
        }
        if (bashDoor != null) // host only: door being opened
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireCube(bashDoor.ClosedPosition, bashDoor.transform.lossyScale * 1.05f);
        }
        DrawSearchGizmos();
        DrawSightGizmos();
    }

    // Host only (noises live on the host): outer range of every sound from the last second or so, by category.
    void OnDrawGizmos()
    {
        if (!showDebugLabel || !Application.isPlaying) return;
        double now = Time.timeAsDouble;
        foreach (var n in NoiseSystem.Recent)
        {
            if (now - n.time > 1.2) continue;
            Gizmos.color = n.kind switch
            {
                SoundKind.SprintStep => Color.red,
                SoundKind.WalkStep => Color.yellow,
                SoundKind.CrouchStep => Color.green,
                SoundKind.Breathing or SoundKind.HeavyBreathing => Color.cyan,
                SoundKind.Door => new Color(1f, 0.5f, 0f),
                SoundKind.Impact => Color.magenta,
                _ => Color.white,
            };
            Gizmos.DrawWireSphere(n.position, n.loudness);
        }
    }

    void OnGUI()
    {
        if (!showDebugLabel || !IsSpawned || Camera.allCamerasCount == 0) return;
        Camera cam = Camera.allCameras[0]; // only the local player's (or lobby) camera is enabled
        Vector3 sp = cam.WorldToScreenPoint(transform.position + Vector3.up * 2.4f);
        if (sp.z <= 0f) return;
        string who = targetId.Value == ulong.MaxValue ? "-" : $"player {targetId.Value}";
        string extra = "";
        if (IsServer) // memory, sounds, search plan and door actions live on the host only
        {
            double now = Time.timeAsDouble;
            RoomVolume evRoom = evidenceKind == EvidenceKind.None ? null : RoomAt(evidencePos);
            string ev = evidenceKind == EvidenceKind.None ? "-"
                : $"{evidenceKind} {now - evidenceTime:0.0}s ago, w {evidenceStrength:0.00}, room {(evRoom != null ? evRoom.roomName : "none")}";
            string dest = evidenceKind == EvidenceKind.None ? "-" : $"({evidencePos.x:0}, {evidencePos.z:0})";
            string snd = lastSound.id == 0 ? "-" : $"{lastSound.kind} {lastSoundStrength:0.00}, {now - lastSoundTime:0.0}s ago";
            string door = bashDoor == null ? "-" : $"{bashDoor.name} {memoProgress:0.0}/{doorWindup:0.0}s";
            extra = $"\nwhy: {reason}\nsound: {snd}\nevidence: {ev}\ndest: {dest}\ndoor: {door}\nsearch: {SearchDebugText()}\nsight: {SightDebugText()}";
        }
        GUI.Label(new Rect(sp.x - 240, Screen.height - sp.y, 480, 210 + 16 * SightDebugLines), $"{state.Value}  alert: {alertLevel.Value}\ntarget: {who}{extra}");
    }
}
