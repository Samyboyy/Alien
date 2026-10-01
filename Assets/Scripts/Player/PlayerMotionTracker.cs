using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A prototype hand-held motion tracker. Q toggles it (toggle rather than hold: Q sits above A, so holding it would block strafing).
///
/// Owner only, for the display and detection: it senses MOTION from what this client can already see (the creature's and teammates'
/// replicated positions over time, and whether the creature's body is hidden inside the vent structure). It never reads the host's AI
/// state. A still creature shows nothing; movement above a threshold shows an approximate blip whose direction is relative to the camera's
/// yaw, with drift that grows with distance. Movement inside the vent structure is weaker and comes and goes.
///
/// Risk: every beep is a real sound. The owner hears it at once (flat, light reflections); the host validates one request per beep (owner,
/// alive, round active, tracker raised, cooldown), emits ONE logical noise at the owner's real position with its own configured range, and
/// echoes it to every peer; the owner ignores the echo (already heard), everyone else hears one 3D beep from the owner. A client sends no
/// position, loudness or timing the host would trust.
///
/// Available only while the round is active, the player is alive (not spectating) and the cursor is captured; anything else lowers it and
/// clears the display at once (death, escape, round end, restart, disconnect, despawn, scene exit).
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class PlayerMotionTracker : NetworkBehaviour
{
    [Header("Detection")]
    [Tooltip("Maximum detection range (m)")] public float range = 28f;
    [Tooltip("Slower than this (m/s) counts as still: no signal")] public float motionThreshold = 0.35f;
    [Tooltip("Show moving teammates too (the sensor cannot tell what is moving)")] public bool detectTeammates = true;
    [Tooltip("Signal share for motion inside the vent structure")] [Range(0f, 1f)] public float structureFactor = 0.45f;
    [Tooltip("Motion in the vent structure comes and goes over this period (s)...")] public float structurePeriod = 1.7f;
    [Tooltip("...showing for this share of it")] [Range(0f, 1f)] public float structureDuty = 0.45f;

    [Header("Uncertainty")]
    [Tooltip("Angular drift either side, up close (degrees)")] public float nearAngleError = 4f;
    [Tooltip("Angular drift either side, at the edge of range (degrees)")] public float farAngleError = 24f;
    [Tooltip("Distance drift as a fraction of the distance, up close")] [Range(0f, 1f)] public float nearDistanceError = 0.05f;
    [Tooltip("...and at the edge of range")] [Range(0f, 1f)] public float farDistanceError = 0.3f;
    [Tooltip("How quickly the shown blip follows the estimate (per second)")] public float displayFollow = 5f;

    [Header("Beeps")]
    [Tooltip("Seconds between beeps for the strongest contact")] public float fastestInterval = 0.32f;
    [Tooltip("Seconds between beeps for the weakest contact")] public float slowestInterval = 1.25f;
    [Tooltip("Seconds between idle sweep beeps with no contact (0 = silent when nothing moves)")] public float idleInterval = 1.8f;
    [Tooltip("Delay after raising before the first beep (s)")] public float firstBeepDelay = 0.35f;

    [Header("Logical noise (host)")]
    [Tooltip("Range of the noise each beep makes for the creature (m)")] public float beepNoiseRange = 6f;
    [Tooltip("The noise range is never above this (m)")] public float maxBeepNoiseRange = 8f;
    [Tooltip("The host accepts at most one beep per this many seconds")] public float hostMinBeepInterval = 0.25f;

    [Header("Display")]
    [Tooltip("Dial diameter as a share of the screen height")] [Range(0.12f, 0.45f)] public float dialSize = 0.26f;
    [Tooltip("Seconds a blip takes to fade")] public float blipFade = 2.4f;

    const int MaxContacts = 5;
    static readonly List<PlayerMotionTracker> all = new();

    // Owner-written "raised" state, so the host can check the tracker is in use before accepting a beep.
    readonly NetworkVariable<bool> raised = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    NetworkFirstPersonController controller;
    PlayerLife life;
    AudioBank bank;
    GameObject audioHost;
    SfxPool beeps;
    double hostLastAccepted = double.NegativeInfinity;

    // Owner: contacts (the creature first, then teammates), sampled speed and the shown, drifting offset (world x/z).
    readonly Transform[] contact = new Transform[MaxContacts];
    readonly bool[] isCreature = new bool[MaxContacts];
    readonly Vector3[] lastPos = new Vector3[MaxContacts];
    readonly float[] sampleDist = new float[MaxContacts], speed = new float[MaxContacts], signal = new float[MaxContacts];
    readonly Vector2[] shown = new Vector2[MaxContacts];
    readonly BlipHistory history = new(16);
    CreatureAI creature;
    int contacts;
    float sampleTime, findTimer, beepTimer, strongest, strongestSmoothed, ventGatePhase;
    double lastBeepAt = double.NegativeInfinity;
    bool on, lastResultAccepted, haveResult;
    int accepted, rejected;

    public bool Raised => on;

    void Awake()
    {
        controller = GetComponent<NetworkFirstPersonController>();
        life = GetComponent<PlayerLife>();
    }

    public override void OnNetworkSpawn()
    {
        all.Add(this);
        bank = AudioBank.Get();
        audioHost = new GameObject("Tracker Audio");
        audioHost.transform.SetParent(transform, false);
        audioHost.transform.localPosition = new Vector3(0.25f, 1.2f, 0.3f); // roughly in the hand
        var acoustics = default(EmitterAcoustics);
        if (IsOwner)
        {
            // Your own tracker: flat and clear, a light share of the reverb and reflections, never occluded.
            beeps = new SfxPool(audioHost, 2, AudioCategory.OwnBody, 0f, 10f);
            acoustics = audioHost.AddComponent<EmitterAcoustics>(); // after the sources: the filters sit behind them
            acoustics.occlude = false;
            acoustics.reflectionWeight = bank != null ? bank.ownBodyReflectionWeight : 0.35f;
        }
        else
        {
            float full = bank != null ? bank.trackerRemoteFullDistance : 2f, max = bank != null ? bank.trackerRemoteMaxDistance : 18f;
            beeps = new SfxPool(audioHost, 2, AudioCategory.Players, 1f, max, customRolloff: AudioRouting.Rolloff(full, max));
            acoustics = audioHost.AddComponent<EmitterAcoustics>();
            acoustics.ignoreRoot = transform;
            acoustics.maxRange = max + 2f;
        }
        acoustics.Attach(beeps);
        ventGatePhase = Random.value * 10f;
    }

    public override void OnNetworkDespawn()
    {
        all.Remove(this);
        Lower(false); // no network writes while despawning
        if (audioHost != null) Destroy(audioHost);
        audioHost = null;
        beeps = null;
    }

    public override void OnDestroy()
    {
        all.Remove(this);
        if (audioHost != null) Destroy(audioHost);
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    bool Available => RoundManager.IsActive && (life == null || life.IsAlive) && controller.CursorCaptured;

    // ---------- Owner ----------

    void Update()
    {
        if (!IsSpawned || !IsOwner) return;
        if (!Available) { if (on) Lower(); return; }
        var kb = Keyboard.current;
        if (kb != null && kb.qKey.wasPressedThisFrame) { if (on) Lower(); else Raise(); }
        if (!on) return;

        float dt = Time.deltaTime;
        if (findTimer <= 0f || (contacts > 0 && isCreature[0] && contact[0] == null)) { findTimer = 1f; FindContacts(); }
        findTimer -= dt;
        Sense(dt);

        beepTimer -= dt;
        float interval = TrackerRules.BeepInterval(strongest, fastestInterval, slowestInterval, idleInterval);
        if (interval > 0f) beepTimer = Mathf.Min(beepTimer, interval); // a contact closing in speeds up the next beep at once
        if (beepTimer <= 0f)
        {
            if (interval > 0f) Beep();
            beepTimer = interval > 0f ? interval : 0.2f;
        }
    }

    void Raise()
    {
        on = true;
        raised.Value = true;
        beepTimer = firstBeepDelay; // also lets the raised state reach the host before the first beep request
        findTimer = 0f;
        history.Clear();
    }

    /// <summary>Off and cleared at once (death, escape, round end or restart, menu, despawn).</summary>
    void Lower(bool writeState = true)
    {
        on = false;
        if (writeState && IsSpawned && IsOwner && raised.Value) raised.Value = false;
        history.Clear();
        contacts = 0;
        strongest = strongestSmoothed = 0f;
        lastBeepAt = double.NegativeInfinity;
        haveResult = false;
        beeps?.StopAll();
    }

    // Once a second: the creature and (optionally) the living teammates. A registry, no scene search per frame.
    void FindContacts()
    {
        if (creature == null) creature = FindFirstObjectByType<CreatureAI>();
        int n = 0;
        if (creature != null) Track(n++, creature.transform, true);
        if (detectTeammates)
            foreach (var t in all)
            {
                if (n >= MaxContacts) break;
                if (t == this || t == null || !t.IsSpawned || (t.life != null && !t.life.IsAlive)) continue;
                Track(n++, t.transform, false);
            }
        for (int i = n; i < contacts; i++) contact[i] = null;
        contacts = n;
    }

    void Track(int i, Transform t, bool creatureContact)
    {
        if (contact[i] == t) return;
        contact[i] = t;
        isCreature[i] = creatureContact;
        lastPos[i] = t.position;
        sampleDist[i] = speed[i] = signal[i] = 0f;
        shown[i] = Flat(t.position - transform.position);
    }

    void Sense(float dt)
    {
        // Speed over a short window from the replicated positions (frame-to-frame interpolation is uneven).
        sampleTime += dt;
        bool sample = sampleTime >= 0.2f;
        double now = Time.timeAsDouble;
        strongest = 0f;
        Vector3 me = transform.position;
        for (int i = 0; i < contacts; i++)
        {
            var t = contact[i];
            if (t == null) { signal[i] = 0f; continue; }
            Vector3 p = t.position;
            float moved = Flat(p - lastPos[i]).magnitude;
            lastPos[i] = p;
            if (moved > 5f) moved = 0f; // a teleport (round reset), not motion
            sampleDist[i] += moved;
            if (sample) { speed[i] = sampleDist[i] / sampleTime; sampleDist[i] = 0f; }

            bool inStructure = isCreature[i] && creature != null && creature.CurrentVentPhase is VentPhase.Travelling or VentPhase.Preparing;
            Vector2 offset = Flat(p - me);
            float distance = offset.magnitude;
            bool gate = TrackerRules.StructureGate(now, structurePeriod, structureDuty, ventGatePhase);
            signal[i] = TrackerRules.Signal(speed[i], distance, motionThreshold, range, inStructure, structureFactor, gate);
            if (signal[i] > strongest) strongest = signal[i];

            // The estimate drifts smoothly: angle and distance errors grow with distance. The shown blip follows it.
            float angleError = TrackerRules.AngleUncertainty(distance, range, nearAngleError, farAngleError) * TrackerRules.Wobble(now, i * 3.1f + 0.7f);
            float distanceScale = 1f + TrackerRules.DistanceUncertainty(distance, range, nearDistanceError, farDistanceError) * TrackerRules.Wobble(now * 0.8, i * 5.3f + 2.9f);
            Vector2 estimate = Rotate(offset, angleError) * distanceScale;
            shown[i] = Vector2.Lerp(shown[i], estimate, 1f - Mathf.Exp(-displayFollow * dt));
        }
        if (sample) sampleTime = 0f;
        strongestSmoothed = Mathf.MoveTowards(strongestSmoothed, strongest, dt * 2.5f);
    }

    void Beep()
    {
        lastBeepAt = Time.timeAsDouble;
        for (int i = 0; i < contacts; i++)
            if (signal[i] > 0f) history.Add(shown[i].x, shown[i].y, lastBeepAt); // what the pulse "saw", fading afterwards
        byte level = (byte)Mathf.RoundToInt(Mathf.Clamp01(strongest) * 255f);
        PlayBeep(level, true);
        BeepRequestRpc(level);
    }

    void PlayBeep(byte level, bool own)
    {
        if (beeps == null) return;
        var clip = bank != null && bank.trackerBeep != null ? bank.trackerBeep : ProceduralBeep();
        float pitch = 1f + 0.12f * (level / 255f); // a little higher with a stronger contact (cosmetic)
        float volume = own ? (bank != null ? bank.trackerOwnerVolume : 0.45f) : (bank != null ? bank.trackerRemoteVolume : 0.6f);
        beeps.Play(clip, volume, pitch);
    }

    // ---------- Host validation and echo ----------

    // Nothing in the request is trusted for the noise: the level only picks the pitch of the echo.
    [Rpc(SendTo.Server)]
    void BeepRequestRpc(byte level, RpcParams rpcParams = default)
    {
        double now = Time.timeAsDouble;
        bool ok = TrackerRules.AcceptBeep(now, hostLastAccepted, hostMinBeepInterval, rpcParams.Receive.SenderClientId == OwnerClientId,
            life == null || life.IsAlive, RoundManager.IsActive, raised.Value);
        if (!ok)
        {
            if (rpcParams.Receive.SenderClientId == OwnerClientId) BeepRejectedRpc();
            return;
        }
        hostLastAccepted = now;
        NoiseSystem.Emit(transform.position, TrackerRules.ClampNoiseRange(beepNoiseRange, 0.5f, maxBeepNoiseRange), "motion tracker", SoundKind.Tracker, OwnerClientId);
        BeepEchoRpc(level);
    }

    // Every peer: one 3D beep from this player. The owner already heard its own, so it only notes that the host accepted it.
    [Rpc(SendTo.Everyone)]
    void BeepEchoRpc(byte level)
    {
        if (IsOwner) { accepted++; lastResultAccepted = haveResult = true; return; }
        PlayBeep(level, false);
    }

    [Rpc(SendTo.Owner)]
    void BeepRejectedRpc()
    {
        rejected++;
        lastResultAccepted = false;
        haveResult = true;
    }

    // ---------- Helpers ----------

    static Vector2 Flat(Vector3 v) => new(v.x, v.z);

    static Vector2 Rotate(Vector2 v, float degrees)
    {
        float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(v.x * c + v.y * s, -v.x * s + v.y * c);
    }

    static AudioClip proceduralBeep;

    // Temporary stand-in until AudioBank.trackerBeep has a real recording: a short two-tone electronic blip, made once and reused.
    static AudioClip ProceduralBeep()
    {
        if (proceduralBeep != null) return proceduralBeep;
        const int Rate = 44100;
        int frames = (int)(Rate * 0.11f);
        var data = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-t * 32f);
            data[i] = env * (0.75f * Mathf.Sin(2f * Mathf.PI * 1760f * t) + 0.25f * Mathf.Sin(2f * Mathf.PI * 3520f * t)) * 0.8f;
        }
        proceduralBeep = AudioClip.Create("Tracker Beep (procedural)", frames, 1, Rate, false);
        proceduralBeep.SetData(data, 0);
        proceduralBeep.hideFlags = HideFlags.DontSave;
        return proceduralBeep;
    }

    // ---------- Display (owner; cached textures and styles, no allocation per OnGUI) ----------

    static Texture2D dialTex, ringTex, dotTex, glowTex;
    GUIStyle hintStyle, diagStyle;
    static readonly GUIContent hintOff = new("Q  motion tracker"), hintOnText = new("Q  lower tracker");
    readonly StringBuilder diag = new();
    string diagText = "";
    float diagTimer;

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout) return;
        if (!Available) return;
        EnsureTextures();
        hintStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, normal = { textColor = new Color(0.6f, 0.95f, 0.8f, 0.7f) } };
        hintStyle.fontSize = Mathf.Max(11, Screen.height / 70); // follows the resolution

        float size = Mathf.Round(Screen.height * dialSize), margin = Mathf.Round(Screen.height * 0.03f);
        var dial = new Rect(Screen.width - size - margin, Screen.height - size - margin, size, size);
        float hintHeight = hintStyle.fontSize + 8f;
        if (!on)
        {
            GUI.Label(new Rect(dial.xMax - 260f, dial.yMax - hintHeight, 260f, hintHeight), hintOff, hintStyle);
            return;
        }
        if (Event.current.type != EventType.Repaint) return;

        Color old = GUI.color;
        double now = Time.timeAsDouble;
        Vector2 c = dial.center;
        float r = size * 0.5f;

        // Glow: a soft halo around the device and a faint wash in the screen corner, both stronger with the signal.
        float glow = 0.18f + 0.55f * strongestSmoothed;
        GUI.color = new Color(0.35f, 1f, 0.7f, glow * 0.5f);
        GUI.DrawTexture(new Rect(c.x - r * 1.6f, c.y - r * 1.6f, r * 3.2f, r * 3.2f), glowTex);
        GUI.color = new Color(0.35f, 1f, 0.7f, 0.08f * strongestSmoothed);
        GUI.DrawTexture(new Rect(Screen.width - size * 2.4f, Screen.height - size * 2.4f, size * 4.8f, size * 4.8f), glowTex);

        GUI.color = Color.white;
        GUI.DrawTexture(dial, dialTex);

        // The pulse: a ring expanding from the centre after each beep.
        float pulse = (float)((now - lastBeepAt) / 0.7);
        if (pulse >= 0f && pulse < 1f)
        {
            float pr = r * pulse;
            GUI.color = new Color(0.5f, 1f, 0.8f, 0.55f * (1f - pulse));
            GUI.DrawTexture(new Rect(c.x - pr, c.y - pr, pr * 2f, pr * 2f), ringTex);
        }

        float yaw = controller.playerCamera != null ? controller.playerCamera.transform.eulerAngles.y : transform.eulerAngles.y;
        float blip = Mathf.Max(6f, size * 0.07f);

        // History: what earlier pulses saw, fading.
        for (int i = 0; i < history.Count; i++)
        {
            history.Get(i, out float hx, out float hy, out double t);
            float a = 1f - (float)((now - t) / blipFade);
            if (a <= 0f) continue;
            DrawBlip(c, r, yaw, range, hx, hy, blip * 0.8f, new Color(0.45f, 1f, 0.75f, 0.45f * a));
        }
        // The live estimate of each moving contact.
        for (int i = 0; i < contacts; i++)
            if (signal[i] > 0f) DrawBlip(c, r, yaw, range, shown[i].x, shown[i].y, blip, new Color(0.7f, 1f, 0.85f, 0.35f + 0.65f * signal[i]));

        // Signal strength: five rising bars to the left of the dial, right-most tallest.
        float bw = Mathf.Max(3f, size * 0.03f), bh = size * 0.3f;
        for (int k = 0; k < 5; k++)
        {
            bool lit = strongestSmoothed > k / 5f + 0.02f;
            GUI.color = lit ? new Color(0.5f, 1f, 0.75f, 0.85f) : new Color(0.3f, 0.5f, 0.4f, 0.3f);
            float h = bh * (k + 1) / 5f;
            float x = dial.x - bw * 1.8f * (5 - k) - bw;
            GUI.DrawTexture(new Rect(x, dial.yMax - h, bw, h), Texture2D.whiteTexture);
        }
        GUI.color = old;
        GUI.Label(new Rect(dial.xMax - 260f, dial.y - hintHeight, 260f, hintHeight), hintOnText, hintStyle);

        if (ShipAcoustics.DiagnosticsVisible) DrawDiagnostics();
    }

    // A blip stored as a world x/z offset, drawn relative to the camera's current yaw (forward is up).
    static void DrawBlip(Vector2 c, float r, float yaw, float range, float dx, float dz, float size, Color color)
    {
        float distance = Mathf.Sqrt(dx * dx + dz * dz);
        float bearing = TrackerRules.Bearing(yaw, dx, dz) * Mathf.Deg2Rad;
        float d = Mathf.Min(1f, distance / Mathf.Max(0.01f, range)) * r * 0.92f;
        float x = c.x + Mathf.Sin(bearing) * d, y = c.y - Mathf.Cos(bearing) * d;
        GUI.color = color;
        GUI.DrawTexture(new Rect(x - size * 0.5f, y - size * 0.5f, size, size), dotTex);
    }

    void DrawDiagnostics()
    {
        if ((diagTimer -= Time.unscaledDeltaTime) <= 0f)
        {
            diagTimer = 0.5f;
            diag.Clear();
            diag.Append("TRACKER (F4)  signal ").Append(strongest.ToString("0.00"))
                .Append("  beep every ").Append(TrackerRules.BeepInterval(strongest, fastestInterval, slowestInterval, idleInterval).ToString("0.00")).Append(" s")
                .Append("  host: ").Append(haveResult ? lastResultAccepted ? "accepted" : "REJECTED" : "-")
                .Append(" (").Append(accepted).Append(" ok, ").Append(rejected).Append(" rejected)\n");
            for (int i = 0; i < contacts; i++)
            {
                if (contact[i] == null) continue;
                Vector2 off = Flat(contact[i].position - transform.position);
                float yaw = controller.playerCamera != null ? controller.playerCamera.transform.eulerAngles.y : transform.eulerAngles.y;
                diag.Append(isCreature[i] ? "creature" : "teammate").Append(": speed ").Append(speed[i].ToString("0.0"))
                    .Append(" m/s, signal ").Append(signal[i].ToString("0.00"))
                    .Append(", bearing ~").Append(Mathf.Round(TrackerRules.Bearing(yaw, off.x, off.y) / 10f) * 10f).Append('°');
                if (isCreature[i] && creature != null) diag.Append(", vent ").Append(creature.CurrentVentPhase);
                diag.Append('\n');
            }
            diagText = diag.ToString();
        }
        diagStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.75f, 1f, 0.8f) } };
        GUI.color = Color.white;
        GUI.Label(new Rect(20, 210, 900, 110), diagText, diagStyle);
    }

    static readonly float[] rangeRings = { 0.25f, 0.5f, 0.75f, 0.985f };

    static void EnsureTextures()
    {
        if (dialTex != null) return;
        dialTex = Make(256, (x, y) =>
        {
            float d = Mathf.Sqrt(x * x + y * y);
            if (d > 1f) return Color.clear;
            float edge = Mathf.Clamp01((1f - d) * 60f);
            float ring = 0f;
            foreach (float rr in rangeRings) ring = Mathf.Max(ring, 1f - Mathf.Abs(d - rr) * 140f);
            float cross = Mathf.Abs(x) < 0.006f || Mathf.Abs(y) < 0.006f ? 0.25f : 0f;
            float a = Mathf.Max(Mathf.Clamp01(ring) * (d > 0.97f ? 0.6f : 0.3f), cross);
            if (y < -0.9f && Mathf.Abs(x) < 0.03f) a = 0.9f; // forward mark at the top
            return Color.Lerp(new Color(0.02f, 0.08f, 0.06f, 0.72f * edge), new Color(0.45f, 1f, 0.75f, 0.9f * edge), a);
        });
        ringTex = Make(256, (x, y) =>
        {
            float d = Mathf.Sqrt(x * x + y * y);
            float a = Mathf.Clamp01(1f - Mathf.Abs(d - 0.97f) * 40f);
            return new Color(1f, 1f, 1f, a);
        });
        dotTex = Make(32, (x, y) =>
        {
            float d = Mathf.Sqrt(x * x + y * y);
            float a = Mathf.Clamp01(1f - d);
            return new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
        });
        glowTex = Make(64, (x, y) =>
        {
            float d = Mathf.Sqrt(x * x + y * y);
            float a = Mathf.Clamp01(1f - d);
            return new Color(1f, 1f, 1f, a * a);
        });
    }

    // f(x, y) over -1..1 with y DOWN like the GUI (Texture2D rows run bottom-up, so the row index is flipped). Made once per session.
    static Texture2D Make(int size, System.Func<float, float, Color> f)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
        var px = new Color[size * size];
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float x = (i + 0.5f) / size * 2f - 1f, y = 1f - (j + 0.5f) / size * 2f;
                px[j * size + i] = f(x, y);
            }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return tex;
    }
}
