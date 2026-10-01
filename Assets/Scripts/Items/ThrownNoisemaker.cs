using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A thrown noisemaker. The HOST spawns it, simulates its Rigidbody and decides everything (clients' physics is kinematic through
/// NetworkRigidbody; the position replicates through a server-authority NetworkTransform). Its life runs on the host (NoisemakerTimeline):
/// thrown, settling after its first meaningful impact, armed, a fixed number of electronic pulses, spent, then despawned.
///
/// Network state is small: the phase, a pulse counter and an impact counter (it carries the impact strength in the same value). Sound has ONE
/// path per peer: the host plays an event directly at the moment it makes it, and a remote peer plays it when the counter arrives
/// (NoisemakerEventGate), so nothing is heard twice and a late joiner, who starts from the counters' current values, hears nothing from the
/// past. No audio is sent every frame.
///
/// Logical noise (what the creature can hear) is separate from the audio: each real pulse and each meaningful impact emits ONE event at the
/// device's position, tagged with this device's token so the creature can recognise THIS device as a decoy. Playing a sound never emits
/// anything, and a missing clip, a muted peer or a failed audio pool never stops the logical event. Every sound exists even with all the
/// optional AudioBank clips unset (NoisemakerSounds supplies cached stand-ins). Placeholder geometry only.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ThrownNoisemaker : NetworkBehaviour, IRoundResettable
{
    [Header("Timing (s)")]
    [Tooltip("After the first meaningful impact, before it arms")] public float armDelay = 0.8f;
    [Tooltip("Armed and blinking, before the first pulse")] public float armedSeconds = 0.4f;
    [Tooltip("How long it pulses")] public float pulseSeconds = 7f;
    [Tooltip("Seconds between pulses")] public float pulseInterval = 1f;
    [Tooltip("It stays as a dead prop this long, then despawns")] public float spentSeconds = 1.5f;
    [Tooltip("If it never lands (stuck, lost) it arms anyway after this long")] public float flightTimeout = 6f;

    [Header("Logical noise (host)")]
    [Tooltip("Outer hearing range of each pulse (m)")] public float pulseRange = 14f;
    [Tooltip("Impacts slower than this (m/s) make no sound and no noise")] public float minImpactSpeed = 1.5f;
    [Tooltip("An impact at this speed makes the loudest noise")] public float maxImpactSpeed = 9f;
    [Tooltip("Outer range of the quietest and the loudest impact noise (m)")] public float impactMinRange = 2f;
    public float impactMaxRange = 8f;
    [Tooltip("At most one impact sound and noise per this many seconds")] public float impactCooldown = 0.35f;

    [Header("Physics (host)")]
    public float maxLaunchSpeed = 14f;
    public float maxAngularVelocity = 20f;
    [Range(0f, 1f)] public float bounciness = 0.25f;
    [Tooltip("Despawn if it ever falls below this height (out of the world)")] public float killHeight = -20f;

    [Header("Audio")]
    [Tooltip("Used only when there is no AudioBank (it normally supplies these)")] public float defaultFullDistance = 3f;
    public float defaultMaxDistance = 40f;
    [Tooltip("Quietest share of the impact volume (a tap) up to the full volume (a hard hit)")] [Range(0f, 1f)] public float impactQuietShare = 0.35f;

    [Header("Presentation")]
    [Tooltip("The small status light (emissive); blinks with the pulses")] public Renderer statusLight;
    public Color lightColor = new Color(1f, 0.45f, 0.1f);

    [Header("Diagnostics (off by default)")]
    [Tooltip("Log one line per impact, pulse and sound request, and the reason whenever a sound cannot play. The F4 overlay shows the same state without logging.")] public bool logEvents;

    static readonly List<ThrownNoisemaker> all = new();
    static readonly HashSet<ulong> liveTokens = new();
    static PhysicsMaterial bounceMaterial;
    static AnimationCurve rolloff;
    static float rolloffFull = -1f, rolloffMax = -1f;
    static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");

    public static IReadOnlyList<ThrownNoisemaker> All => all;
    public static int ActiveCount => all.Count;
    public static bool IsLive(ulong token) => liveTokens.Contains(token);

    readonly NetworkVariable<NoisemakerPhase> phase = new(NoisemakerPhase.Thrown);
    readonly NetworkVariable<int> pulses = new(0);
    readonly NetworkVariable<int> impacts = new(0); // NoisemakerRules.PackImpact: sequence and strength together

    Rigidbody body;
    Collider[] colliders;
    NoisemakerTimeline timeline;
    SfxPool pool;
    EmitterAcoustics acoustics;
    MaterialPropertyBlock block;
    AudioBank bank;
    readonly NoisemakerEventGate pulseGate = new(), impactGate = new();
    double lastImpactAt = double.NegativeInfinity, nextPulseAt;
    float lightUntil, lastImpactSpeed;
    bool lightApplied, reportedPoolFailure;
    Color shownColor;

    // What the audio path last did, for the F4 overlay (strings are only built when a sound is requested, a few times a second at most).
    int meaningfulImpacts, logicalPulses, soundRequests, soundsPlayed;
    string lastSound = "none yet", poolState = "not built";

    /// <summary>Stable identity of this device for the creature's recognition (0 is never used). Never reused within a session.</summary>
    public ulong Token => NetworkObjectId + 1;
    public NoisemakerPhase Phase => phase.Value;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        colliders = GetComponentsInChildren<Collider>();
        if (bounceMaterial == null)
            bounceMaterial = new PhysicsMaterial("Noisemaker") { dynamicFriction = 0.6f, staticFriction = 0.6f, bounciness = bounciness, bounceCombine = PhysicsMaterialCombine.Minimum, hideFlags = HideFlags.DontSave };
        foreach (var c in colliders) c.sharedMaterial = bounceMaterial;
        body.maxAngularVelocity = maxAngularVelocity;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    public override void OnNetworkSpawn()
    {
        all.Add(this);
        liveTokens.Add(Token);
        bank = AudioBank.Get(); // may be null: the sounds still play (NoisemakerSounds), with the default volumes
        pulseGate.Begin(pulses.Value); // a late joiner starts here: expired sounds are never replayed
        impactGate.Begin(NoisemakerRules.ImpactSequence(impacts.Value));
        pulses.OnValueChanged += OnPulses;
        impacts.OnValueChanged += OnImpacts;
        phase.OnValueChanged += OnPhase;
        block = new MaterialPropertyBlock();
        ApplyPhase(phase.Value, false);
        EnsurePool();
        if (IsServer)
        {
            timeline = new NoisemakerTimeline(armDelay, armedSeconds, pulseSeconds, pulseInterval, spentSeconds, flightTimeout);
            timeline.Begin(Time.timeAsDouble);
        }
    }

    public override void OnNetworkDespawn()
    {
        pulses.OnValueChanged -= OnPulses;
        impacts.OnValueChanged -= OnImpacts;
        phase.OnValueChanged -= OnPhase;
        timeline?.Halt();
        Forget();
        if (pool != null) pool.StopAll(); // no source keeps playing on a device that is going away
        pool = null;
        acoustics = null;
    }

    public override void OnDestroy()
    {
        Forget();
        base.OnDestroy();
    }

    void Forget()
    {
        all.Remove(this);
        liveTokens.Remove(Token);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        all.Clear();
        liveTokens.Clear();
        bounceMaterial = null;
        rolloff = null;
        rolloffFull = rolloffMax = -1f;
    }

    // A world sound like any other: category routing, perceptual rolloff, close reflections, occlusion. Two sources are enough (a pulse and an
    // impact can overlap); nothing is created per sound. Built on first use, and it does not need the AudioBank to exist.
    bool EnsurePool()
    {
        if (pool != null) return true;
        if (!isActiveAndEnabled) { poolState = "device inactive"; return false; }
        bank ??= AudioBank.Get();
        float full = bank != null ? bank.noisemakerFullDistance : defaultFullDistance, max = bank != null ? bank.noisemakerMaxDistance : defaultMaxDistance;
        if (max <= full || max <= 0f) { poolState = $"bad distances (full {full}, max {max})"; return false; }
        if (rolloff == null || !Mathf.Approximately(rolloffFull, full) || !Mathf.Approximately(rolloffMax, max))
        {
            rolloffFull = full;
            rolloffMax = max;
            rolloff = AudioRouting.Rolloff(full, max);
        }
        pool = new SfxPool(gameObject, 2, AudioCategory.World, 1f, max, customRolloff: rolloff);
        acoustics = gameObject.AddComponent<EmitterAcoustics>(); // after the sources: the filters sit behind them
        acoustics.ignoreRoot = transform; // its own body never muffles it
        acoustics.maxRange = max + 2f;
        acoustics.Attach(pool);
        poolState = $"2 sources, World, 3D, {full:0}-{max:0} m, bank {(bank != null ? "found" : "MISSING (defaults)")}";
        return true;
    }

    // ---------- Host: launch, physics, life ----------

    /// <summary>Host: give it its velocity (clamped), and keep it from colliding with its thrower.</summary>
    public void Launch(Vector3 velocity, CharacterController thrower)
    {
        if (!IsServer) return;
        if (thrower != null) foreach (var c in colliders) Physics.IgnoreCollision(c, thrower);
        body.linearVelocity = Vector3.ClampMagnitude(velocity, maxLaunchSpeed);
        body.angularVelocity = Random.insideUnitSphere * 6f;
    }

    void Update()
    {
        if (IsSpawned) UpdateLight();
        if (!IsServer || !IsSpawned || timeline == null) return;

        double now = Time.timeAsDouble;
        if (transform.position.y < killHeight) { Trace("fell out of the world"); Remove(); return; }
        var before = timeline.Phase;
        bool pulse = timeline.Tick(now);
        if (phase.Value != timeline.Phase)
        {
            phase.Value = timeline.Phase;
            Trace($"phase {before} -> {timeline.Phase}");
            if (timeline.Phase == NoisemakerPhase.Spent) PlaySound(NoisemakerSound.Spent, 1f); // the host plays it directly; a remote peer gets the phase
        }
        if (pulse)
        {
            // ONE logical event per pulse, at the device's real position, with this device's identity. Range comes from here, never a client.
            NoiseSystem.Emit(transform.position, pulseRange, "noisemaker", SoundKind.Electronic, NoiseSystem.NoEmitter, Token);
            logicalPulses++;
            nextPulseAt = timeline.NextPulseTime;
            pulses.Value++;
            Trace($"pulse {pulses.Value}: logical noise emitted (range {pulseRange:0} m)");
            if (pulseGate.Direct(pulses.Value)) OnPulse();
        }
        if (timeline.Phase == NoisemakerPhase.Spent && !body.isKinematic) Settle();
        if (timeline.Expired) Remove();
    }

    // Spent: stop simulating and stop being an obstacle.
    void Settle()
    {
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.isKinematic = true;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (!IsServer || !IsSpawned || timeline == null) return;
        var other = collision.collider;
        if (other.GetComponentInParent<NetworkFirstPersonController>() != null || other.GetComponentInParent<CreatureAI>() != null) return; // no sound or noise from bumping a body
        double now = Time.timeAsDouble;
        float speed = collision.relativeVelocity.magnitude;
        if (!NoisemakerRules.AcceptImpact(now, lastImpactAt, impactCooldown, speed, minImpactSpeed)) return;
        lastImpactAt = now;
        lastImpactSpeed = speed;
        meaningfulImpacts++;
        timeline.OnImpact(now);
        float range = NoisemakerRules.ImpactRange(speed, minImpactSpeed, maxImpactSpeed, impactMinRange, impactMaxRange);
        // Incidental, at the device, no player attached: it says nothing about who threw it.
        NoiseSystem.Emit(transform.position, range, "noisemaker impact", SoundKind.Impact, NoiseSystem.NoEmitter, Token);
        byte level = (byte)Mathf.RoundToInt(Mathf.Clamp01((speed - minImpactSpeed) / Mathf.Max(0.01f, maxImpactSpeed - minImpactSpeed)) * 255f);
        int sequence = NoisemakerRules.ImpactSequence(impacts.Value) + 1;
        impacts.Value = NoisemakerRules.PackImpact(sequence, level);
        Trace($"impact {sequence} at {speed:0.0} m/s with {collision.collider.name}: logical noise range {range:0.0} m");
        if (impactGate.Direct(sequence)) PlaySound(NoisemakerSound.Impact, level / 255f);
    }

    void Remove()
    {
        if (IsSpawned && IsServer) NetworkObject.Despawn(true);
    }

    public void ResetForRound(System.Random rng)
    {
        timeline?.Halt(); // nothing more is reported, even before the despawn completes
        Remove(); // a new round starts without the last one's devices
    }

    public void OnRoundOver()
    {
        timeline?.Halt();
        Remove();
    }

    // ---------- Sound and light: one path per peer ----------

    // Remote peers: the replicated counters. (On the host the same callbacks fire, but the gate ignores them: it already played its own.)
    void OnPulses(int previous, int now)
    {
        if (pulseGate.Replicated(now, IsServer)) OnPulse();
    }

    void OnImpacts(int previous, int packed)
    {
        if (impactGate.Replicated(NoisemakerRules.ImpactSequence(packed), IsServer))
            PlaySound(NoisemakerSound.Impact, NoisemakerRules.ImpactLevel(packed) / 255f);
    }

    void OnPhase(NoisemakerPhase previous, NoisemakerPhase now)
    {
        ApplyPhase(now, true);
        if (!IsServer && now == NoisemakerPhase.Spent) PlaySound(NoisemakerSound.Spent, 1f);
    }

    void OnPulse()
    {
        lightUntil = Time.time + 0.18f;
        PlaySound(NoisemakerSound.Pulse, 1f);
    }

    void ApplyPhase(NoisemakerPhase now, bool live)
    {
        if (now == NoisemakerPhase.Spent)
            foreach (var c in colliders) if (c != null) c.enabled = false; // a dead prop never blocks a door or a step
    }

    // Plays one sound at the device. Never silent without saying why (logged once when it cannot be played at all).
    void PlaySound(NoisemakerSound kind, float level)
    {
        soundRequests++;
        if (!EnsurePool())
        {
            if (!reportedPoolFailure) Debug.LogError($"Noisemaker {Token}: its sounds cannot play: {poolState}.", this);
            reportedPoolFailure = true;
            return;
        }
        var clip = NoisemakerSounds.Resolve(bank, kind, out bool fallback);
        float volume = NoisemakerSounds.Volume(bank, kind);
        if (kind == NoisemakerSound.Impact) volume *= Mathf.Lerp(impactQuietShare, 1f, level);
        float pitch = kind == NoisemakerSound.Pulse ? 1f + 0.015f * ((pulses.Value * 7) % 5 - 2) : 1f; // a hair of variation, the same on every peer
        pool.Play(clip, volume, pitch);
        var source = pool.LastSource;
        bool playing = source != null && source.isPlaying;
        if (playing) soundsPlayed++;
        lastSound = $"{kind} '{clip.name}'{(fallback ? " (temporary stand-in)" : "")}, volume {volume:0.00}, pitch {pitch:0.00}, source {(playing ? "playing" : "NOT playing")}, listener {(AudioListenerLocator.Current != null ? "found" : "NONE")}";
        if (logEvents) Debug.Log($"Noisemaker {Token}: {lastSound}", this);
    }

    void Trace(string what)
    {
        if (logEvents) Debug.Log($"Noisemaker {Token} (host): {what}", this);
    }

    // The status light: slow blink while armed, a flash on each pulse, off when spent.
    void UpdateLight()
    {
        if (statusLight == null || block == null) return;
        var p = phase.Value;
        float level = p switch
        {
            NoisemakerPhase.Spent => 0f,
            NoisemakerPhase.Pulsing => Time.time < lightUntil ? 1f : 0.12f,
            NoisemakerPhase.Armed => Mathf.Repeat(Time.time * 4f, 1f) < 0.5f ? 0.8f : 0.1f,
            _ => 0.05f,
        };
        Color c = lightColor * (level * 4f);
        if (lightApplied && c == shownColor) return;
        lightApplied = true;
        shownColor = c;
        block.SetColor(EmissiveColor, c);
        statusLight.SetPropertyBlock(block);
    }

    // ---------- Diagnostics ----------

    public void Describe(StringBuilder sb, CreatureAI creature)
    {
        sb.Append("device ").Append(Token).Append(": ").Append(phase.Value)
            .Append(", impacts ").Append(meaningfulImpacts).Append(" (last ").Append(lastImpactSpeed.ToString("0.0")).Append(" m/s)")
            .Append(", ").Append(phase.Value == NoisemakerPhase.Settling && timeline != null ? "arming" : "pulse #" + pulses.Value)
            .Append(", next pulse ").Append(phase.Value == NoisemakerPhase.Pulsing ? Mathf.Max(0f, (float)(nextPulseAt - Time.timeAsDouble)).ToString("0.0") + " s" : "-")
            .Append(", logical noises ").Append(logicalPulses).Append(", range ").Append(pulseRange.ToString("0")).Append(" m");
        if (creature != null) sb.Append(" | creature: ").Append(creature.DecoyNote(Token));
        sb.Append("\n   audio: ").Append(soundsPlayed).Append('/').Append(soundRequests).Append(" played; pool ").Append(poolState).Append("; last: ").Append(lastSound).Append('\n');
    }

    void OnDrawGizmosSelected() => DrawRange();

    void OnDrawGizmos()
    {
        if (Application.isPlaying && ShipAcoustics.DiagnosticsVisible) DrawRange();
    }

    void DrawRange()
    {
        Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, pulseRange);
    }
}
