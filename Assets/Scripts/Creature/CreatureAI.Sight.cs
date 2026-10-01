using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gradual visual recognition (host only). Each living player has their own awareness value. It grows only while real,
/// unobstructed body samples exist (walls, doors and furniture block them exactly as before) and is slowed by distance, angle,
/// and any concealment volume the player is hiding in. A player counts as RECOGNISED (a confirmed sighting) only at full
/// awareness, and the chase destination is only ever written from a recognised player with a sample this very tick.
/// Suspicion (0 &lt; awareness &lt; 1) never gives the creature a position. Hearing is a separate channel: concealment does
/// not change footsteps or breathing.
/// </summary>
public partial class CreatureAI
{
    [Header("Gradual recognition")]
    [Tooltip("Seconds for a fully exposed, close, centrally seen player to be recognised")] public float recogniseSeconds = 0.25f;
    [Tooltip("Speed multiplier at the edge of sight distance (1 = distance does not matter)")] [Range(0.05f, 1f)] public float farRecogniseFactor = 0.3f;
    [Tooltip("Speed multiplier at the edge of the field of view")] [Range(0.05f, 1f)] public float peripheralFactor = 0.5f;
    [Tooltip("Awareness is held this long when every sample is lost (edge flicker); it never grows without a sample")] public float awarenessHold = 0.4f;
    [Tooltip("Seconds for full awareness to fade once the player has been out of sight longer than the hold")] public float awarenessDecaySeconds = 2.5f;
    [Header("Concealed recognition (players hiding in an eligible volume)")]
    [Tooltip("Seconds of sustained clear, close, central observation to recognise a player in a standard hiding place, by attention. Exposed players use recogniseSeconds.")] public float concealedCalmSeconds = 6f;
    public float concealedHeightenedSeconds = 4f;
    public float concealedSearchSeconds = 2.5f;
    [Tooltip("Deliberate inspection from a valid opening")] public float concealedInspectSeconds = 0.5f;
    [Tooltip("The visualConcealment the times above are quoted for (a standard table). Better cover takes longer, poorer cover less.")] [Range(0f, 0.9f)] public float referenceCover = 0.65f;
    [Tooltip("Patrol alertness (0..1, fades after a hunt) at or above which patrol counts as heightened")] [Range(0.05f, 1f)] public float heightenedAlertness = 0.2f;

    [Header("Recognition (general)")]
    [Tooltip("Awareness the creature needs to count as having WATCHED a player enter a hiding place")] [Range(0.1f, 1f)] public float watchThreshold = 0.5f;

    [Header("Inspection reach")]
    [Tooltip("A chase that began from a deliberate inspection may grab a player inside THAT spot from this far, through a clear line from the opening. Everything else uses captureDistance.")] public float inspectReach = 1.8f;
    [Tooltip("The low inspection eye only works this close (horizontally) to an opening of the inspected spot")] public float inspectOpeningTolerance = 1f;

    const double TipMemory = 10.0;      // seconds: how recent a sound or sighting may be to explain a death
    const double WitnessMemory = 30.0;  // seconds: a witnessed entry is remembered this long

    sealed class PlayerSight
    {
        public readonly AwarenessState awareness = new();
        public int samples;                 // unobstructed body samples this tick (0..3)
        public float rate, concealment, light, baseSeconds; // last values, for the debug display
        public AttentionLevel attention;
        public bool inspected;              // deliberately inspected this tick
        public HidingSpot spot;             // volume the player qualifies for now (host-evaluated, never read by search-point choice)
        public HidingSpot memorySpot;       // creature memory: a spot it WATCHED this player enter. Written only by observed entries.
        public double memoryTime = double.NegativeInfinity;
        public HidingSpot tipSpot;          // tip only: spot of the latest entry, and whether the creature was watching it
        public bool tipSawEnter;
        public double heardTime = double.NegativeInfinity, lastConfirmed = double.NegativeInfinity;
        public bool heardTracker;           // the last sound heard from this player was its motion tracker (for the death tip)
        public bool bySound;                // the current detection started from this player's audible sound
    }

    readonly Dictionary<ulong, PlayerSight> sights = new();
    readonly List<ulong> staleSights = new();
    double lastSightTime;
    HidingSpot chaseInspectSpot; // set when a chase began from a deliberate inspection of this spot

    PlayerSight Sight(ulong id)
    {
        if (!sights.TryGetValue(id, out var s)) sights[id] = s = new PlayerSight();
        return s;
    }

    bool Confirmed(NetworkFirstPersonController p) =>
        p != null && sights.TryGetValue(p.OwnerClientId, out var s) && s.awareness.Recognised && s.samples > 0;

    // Still the recognised target: confirmed now, or recognised and within the flicker hold (no new position is taken then).
    bool KeepsTarget(NetworkFirstPersonController p) =>
        p != null && sights.TryGetValue(p.OwnerClientId, out var s) && (s.samples > 0 ? s.awareness.Recognised : s.awareness.Holding(awarenessHold));

    // ---------- Low inspection eye ----------

    // The low eye exists only while physically standing at an opening of the spot being inspected.
    bool LowEyeActive => inspectingLow && targetSpot != null && targetSpot.AtOpening(transform.position, inspectOpeningTolerance);

    bool InspectingOpening(HidingSpot spot) => spot != null && spot == targetSpot && step == SearchStep.Inspect && LowEyeActive;

    // ---------- Attention ----------

    // Derived only from what the creature is doing and its general alertness, so it applies to every player alike.
    // Search/pursuit states stay "searching" after sight is lost; an unalerted patrol is the only calm state.
    AttentionLevel AttentionNow(bool inspecting) => SightRules.AttentionFor(inspecting,
        state.Value is CreatureState.Search or CreatureState.Pursue or CreatureState.Chase or CreatureState.Bash or CreatureState.Vent,
        state.Value == CreatureState.Investigate, alertness, heightenedAlertness);

    float AttentionSeconds(AttentionLevel a) => a switch
    {
        AttentionLevel.Inspecting => concealedInspectSeconds,
        AttentionLevel.Searching => concealedSearchSeconds,
        AttentionLevel.Heightened => concealedHeightenedSeconds,
        _ => concealedCalmSeconds,
    };

    // ---------- Per-tick update ----------

    // Runs on every perception tick, before the state logic, so Acquire/UpdateChase see this tick's values.
    void UpdateSight()
    {
        double now = Time.timeAsDouble;
        float dt = Mathf.Clamp((float)(now - lastSightTime), 0.02f, 0.3f);
        lastSightTime = now;

        staleSights.Clear();
        foreach (var id in sights.Keys)
            if (!PlayerById(id, out var known) || !Alive(known)) staleSights.Add(id); // left play or disconnected: forget them
        foreach (var id in staleSights) sights.Remove(id);

        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.PlayerObject == null || !client.PlayerObject.TryGetComponent(out NetworkFirstPersonController p) || !Alive(p)) continue;
            Observe(p, Sight(p.OwnerClientId), dt, now);
        }
    }

    bool PlayerById(ulong id, out NetworkFirstPersonController p)
    {
        p = null;
        return NetworkManager.ConnectedClients.TryGetValue(id, out var c) && c.PlayerObject != null && c.PlayerObject.TryGetComponent(out p);
    }

    void Observe(NetworkFirstPersonController p, PlayerSight s, float dt, double now)
    {
        Vector3 eye = EyePoint;
        Vector3 to = BodyPoint(p, 0.55f) - eye;
        float dist = to.magnitude;
        Vector3 flat = new Vector3(to.x, 0f, to.z);
        float angle = Vector3.Angle(transform.forward, flat);

        // Samples need range and field of view (the close sense ignores the angle), then a real unobstructed line per sample.
        bool inView = dist <= sightDistance && (dist <= closeSenseDistance || angle <= fieldOfView * 0.5f);
        s.samples = inView ? VisibleSamples(eye, p) : 0;

        // Hiding eligibility, host side: body inside the volume in the required stance. Never used to choose where to search.
        float height = p.GetComponent<CharacterController>().height;
        var before = s.spot;
        s.spot = HidingSpot.Resolve(p.transform.position, height, p.IsCrouched);
        if (s.spot != before && s.spot != null) OnEnteredSpot(p, s, now);

        s.inspected = InspectingOpening(s.spot);
        s.attention = AttentionNow(s.inspected);

        // Exposed: the ordinary time. Hiding in an eligible volume: a separate time set by attention and the spot's cover (see
        // SightRules.ConcealedSeconds); distance, angle and exposed samples then scale it exactly as for an exposed player.
        float cover = 0f, light = 1f, baseSeconds = recogniseSeconds;
        if (s.spot != null)
        {
            // From an opening the creature is looking in properly: the spot's cover quality mostly stops mattering.
            cover = s.inspected ? Mathf.Lerp(referenceCover, s.spot.visualConcealment, s.spot.inspectionResidual) : s.spot.visualConcealment;
            light = s.inspected ? Mathf.Max(s.spot.lightVisibility, 1f) : s.spot.lightVisibility; // inspecting with a light
            baseSeconds = SightRules.ConcealedSeconds(AttentionSeconds(s.attention), cover, referenceCover, recogniseSeconds);
            // It watched this very player go into this spot and has come to open it: no hesitation (still never faster than in the open).
            if (s.inspected && s.spot == s.memorySpot) baseSeconds = Mathf.Max(recogniseSeconds, baseSeconds * witnessedInspectFactor);
        }
        float exposure = s.samples / (float)bodySamples.Length;
        s.rate = SightRules.Rate(exposure, dist, angle, sightDistance, fieldOfView, closeSenseDistance, 0f, light,
            baseSeconds, farRecogniseFactor, peripheralFactor);
        s.concealment = cover;
        s.light = light;
        s.baseSeconds = baseSeconds;
        // Weak suspicion changes nothing here: it neither raises alertness nor the attention level, so a glimpse cannot speed up
        // its own detection. Only a recognised sighting (Acquire) or the creature's behaviour does.
        s.awareness.Tick(dt, s.rate, s.samples > 0, awarenessHold, awarenessDecaySeconds);

        if (s.awareness.Recognised && s.samples > 0)
        {
            // A fresh detection (no confirmed sighting for a while): did this player's sound lead the creature here?
            if (now - s.lastConfirmed > TipMemory) s.bySound = now - s.heardTime < TipMemory;
            s.lastConfirmed = now;
            // Seen somewhere other than the watched spot: the old entry no longer says where they are.
            if (s.spot != s.memorySpot) s.memorySpot = null;
        }
    }

    // One ray per body sample (head, chest, legs at the current stance). A sample is visible when nothing but the player is hit.
    int VisibleSamples(Vector3 eye, NetworkFirstPersonController p)
    {
        int n = 0;
        foreach (float f in bodySamples)
            if (!Physics.Linecast(eye, BodyPoint(p, f), out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || hit.collider.GetComponentInParent<NetworkFirstPersonController>() == p)
                n++;
        return n;
    }

    bool HasLineOfSight(NetworkFirstPersonController p) => VisibleSamples(EyePoint, p) > 0;

    // ---------- Watched entry ----------

    // The player just became eligible for a volume. If the creature was really looking (samples now, awareness already building)
    // it remembers the furniture; an unwatched entry changes nothing the creature knows.
    void OnEnteredSpot(NetworkFirstPersonController p, PlayerSight s, double now)
    {
        bool watched = s.samples > 0 && s.awareness.Value >= watchThreshold;
        s.tipSpot = s.spot;
        s.tipSawEnter = watched;
        if (!watched) return;

        s.memorySpot = s.spot;
        s.memoryTime = now;
        witnessedSpot = s.spot;
        // Already chasing: losing sight later sends it to this spot (NoteWitnessedSpot). Otherwise go and look now.
        if (!active || state.Value == CreatureState.Chase || state.Value == CreatureState.Bash) return;
        // Before it has committed to a vent, a hiding place it watched somebody enter always cancels the trip; in the duct it cannot see at all.
        if (state.Value == CreatureState.Vent && !CancelVentAttemptForEvidence("watched a player enter a hiding place")) return;
        SetEvidence(EvidenceKind.Sight, s.spot.LookPoint, 0.7f, p.OwnerClientId, true);
        EnterSearch(CreatureState.Investigate, $"saw a player enter {s.spot.name}", DecisionReason.WatchedHideEntry);
    }

    // The spot a lost chase target was WATCHED entering, from memory only (never from where they are now).
    HidingSpot WatchedSpotOf(NetworkFirstPersonController p) =>
        p != null && sights.TryGetValue(p.OwnerClientId, out var s) && Time.timeAsDouble - s.memoryTime < WitnessMemory ? s.memorySpot : null;

    // ---------- Acquire, reach, capture ----------

    // The nearest player who is recognised and confirmed this tick.
    NetworkFirstPersonController NearestRecognised()
    {
        NetworkFirstPersonController best = null;
        float bestDist = float.MaxValue;
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.PlayerObject == null || !client.PlayerObject.TryGetComponent(out NetworkFirstPersonController p)) continue;
            if (!Alive(p) || !Confirmed(p)) continue;
            float d = (p.transform.position - transform.position).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    // Standard reach for everyone, crouched or not. Only a chase that began from a deliberate inspection of the spot the player is
    // in may grab further, and then only along a clear line from the opening (tabletops, walls and closed doors block it).
    bool CanReach(NetworkFirstPersonController p)
    {
        float flatDist = FlatDistance(p);
        if (flatDist <= captureDistance && HasLineOfSight(p)) return true;
        if (chaseInspectSpot == null || flatDist > inspectReach || Sight(p.OwnerClientId).spot != chaseInspectSpot) return false;
        Vector3 from = transform.position + Vector3.up * inspectEyeHeight;
        foreach (float f in bodySamples)
            if (!Physics.Linecast(from, BodyPoint(p, f), out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || hit.collider.GetComponentInParent<NetworkFirstPersonController>() == p)
                return true;
        return false;
    }

    // Only claims what the recorded evidence supports; anything else is the neutral text.
    DetectionReason ReasonFor(PlayerSight s)
    {
        if (chaseInspectSpot != null && s.spot == chaseInspectSpot) return DetectionReason.FoundWhileInspecting;
        if (s.spot != null)
        {
            if (s.tipSawEnter && s.tipSpot == s.spot) return DetectionReason.SawEnterSpot;
            return s.bySound ? SoundReason(s) : DetectionReason.VisibleThroughOpening;
        }
        return s.bySound ? SoundReason(s) : s.lastConfirmed > double.NegativeInfinity ? DetectionReason.SeenInOpen : DetectionReason.Unknown;
    }

    static DetectionReason SoundReason(PlayerSight s) => s.heardTracker ? DetectionReason.TrackerLedHere : DetectionReason.FootstepsLedHere;

    void NoteHeard(ulong emitter, SoundKind kind, double now)
    {
        var s = Sight(emitter);
        s.heardTime = now;
        s.heardTracker = kind == SoundKind.Tracker;
    }

    void Capture(NetworkFirstPersonController p)
    {
        var s = Sight(p.OwnerClientId);
        p.GetComponent<PlayerLife>().Kill(ReasonFor(s));
        sights.Remove(p.OwnerClientId); // a dead player's awareness and history are gone
        EnterSearch(CreatureState.Search, "captured a player", DecisionReason.Captured); // keeps hunting: reacquires any other recognised player
    }

    // ---------- Debug ----------

    string SightDebugText()
    {
        if (sights.Count == 0) return "-";
        var sb = new StringBuilder();
        foreach (var kv in sights)
        {
            var s = kv.Value;
            string spot = s.spot != null ? s.spot.name : "-";
            string why = s.awareness.Recognised ? $" REC {ReasonFor(s)}" : "";
            string ttr = s.rate > 0.001f ? $"{(1f - s.awareness.Value) / s.rate:0.0}s" : "never";
            string mod = s.spot != null ? $"hiding base {s.baseSeconds:0.0}s (x{s.baseSeconds / Mathf.Max(0.01f, recogniseSeconds):0.0}), cover {s.concealment:0.00}, light {s.light:0.00}" : "exposed";
            sb.Append($"\n  P{kv.Key}: aware {s.awareness.Value:0.00} ({s.rate:0.00}/s, {ttr} left), attn {s.attention}, {mod}, samples {s.samples}/{bodySamples.Length}, spot {spot}{(s.inspected ? ", INSPECTED" : "")}{why}");
        }
        return sb.ToString();
    }

    int SightDebugLines => sights.Count;

    void DrawSightGizmos()
    {
        foreach (var kv in sights)
        {
            var s = kv.Value;
            if (s.spot == null || !PlayerById(kv.Key, out var p)) continue;
            Gizmos.color = s.awareness.Recognised ? Color.red : Color.Lerp(Color.green, Color.yellow, s.awareness.Value);
            Gizmos.DrawWireSphere(p.transform.position + Vector3.up * 0.5f, 0.45f);
            Gizmos.DrawLine(p.transform.position + Vector3.up * 0.5f, s.spot.LookPoint);
        }
    }
}
