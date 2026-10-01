using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Audible footsteps and the occasional snarl of the creature, on every peer (added at runtime by CreatureAI when an AudioBank
/// exists). Purely cosmetic: driven by the creature's replicated movement and state, no effect on hearing or the AI.
/// Footsteps: one per stride walked, each a different clip from a shuffled bag (never the same twice in a row) with a little
/// pitch and volume variation, audible from far away. Snarl: the original recording, only for the local player, only when the creature is close (or hunting nearby), and then not
/// again for a long random cooldown. A wall between creature and listener muffles both (low-pass), it does not silence them.
/// </summary>
public class CreatureAudio : MonoBehaviour
{
    CreatureAI ai;
    AudioBank bank;
    SfxPool steps, voice;
    AudioLowPassFilter muffle;
    NoRepeatBag bag;
    System.Random rng;
    Vector3 last;
    float stride, speed, checkTimer, cutoff = 22000f;
    double nextSnarl;
    bool wasActive, blocked;
    float sampleTime, sampleDist;
    int stepsPlayed;
    double lastStepTime;

    void Start()
    {
        bank = AudioBank.Get();
        ai = GetComponent<CreatureAI>();
        if (bank == null || ai == null) { enabled = false; return; }

        var host = new GameObject("Creature Audio");
        host.transform.SetParent(transform, false);
        host.transform.localPosition = Vector3.up; // roughly chest height
        // Footsteps carry: full volume close, then a straight-line fade to silence far away (not the steep default falloff).
        steps = new SfxPool(host, 3, bank.monsterStepSpatial, bank.monsterStepMaxDistance, AudioRolloffMode.Linear, bank.monsterStepFullDistance);
        voice = new SfxPool(host, 1, bank.snarlSpatial, bank.monsterStepMaxDistance, AudioRolloffMode.Linear, bank.monsterStepFullDistance);
        // Added after the sources on purpose: component order is the audio effect order, so the filter sits behind them.
        muffle = host.AddComponent<AudioLowPassFilter>();
        muffle.cutoffFrequency = cutoff;
        rng = new System.Random(GetInstanceID());
        bag = new NoRepeatBag(bank.monsterFootsteps.Length, rng);
        last = transform.position;
        Debug.Log($"CreatureAudio ready on '{name}': {bank.monsterFootsteps.Length} footstep clips, audible to {bank.monsterStepMaxDistance:0} m. (F3 shows live audio stats.)");
    }

    void Update()
    {
        if (steps == null) return;
        bool active = RoundManager.IsActive;
        Vector3 delta = transform.position - last;
        last = transform.position;
        if (active && !wasActive) nextSnarl = Time.timeAsDouble + bank.snarlFirstDelay;
        wasActive = active;

        // Footsteps follow the movement itself, whatever the round state, but not while it is inside a vent (it is not walking there).
        if (ai.CurrentVentPhase == VentPhase.None || ai.CurrentVentPhase == VentPhase.Approaching) Step(delta);
        else { stride = speed = 0f; }

        if ((checkTimer -= Time.deltaTime) <= 0f)
        {
            checkTimer = 0.1f;
            Listen(active);
        }
        // Smooth the muffling so it never clicks.
        float want = blocked ? 1400f : 22000f;
        cutoff = Mathf.MoveTowards(cutoff, want, (want > cutoff ? 40000f : 60000f) * Time.deltaTime);
        muffle.cutoffFrequency = cutoff;
    }

    // One footstep per stride, only while really moving.
    void Step(Vector3 delta)
    {
        Vector3 flat = new Vector3(delta.x, 0f, delta.z);
        float dist = flat.magnitude;
        if (dist > 3f) return; // teleport (round reset)

        // Speed over a 0.2 s window (like the players' logical footsteps), so uneven frame-to-frame movement cannot stall the steps.
        sampleDist += dist;
        if ((sampleTime += Time.deltaTime) >= 0.2f)
        {
            speed = sampleDist / sampleTime;
            sampleDist = sampleTime = 0f;
        }
        if (speed < 0.4f) { stride = 0f; return; }

        stride += dist;
        float length = Mathf.Max(0.5f, bank.monsterStride * Mathf.Lerp(0.9f, 1.5f, Mathf.InverseLerp(1.8f, 4.5f, speed))); // longer strides at a run
        if (stride < length) return;
        stride %= length;

        int i = bag.Next();
        if (i < 0) return;
        float volume = bank.monsterFootstepVolume * Mathf.Lerp(0.8f, 1f, Mathf.InverseLerp(1.8f, 4.5f, speed)) * (0.9f + 0.1f * (float)rng.NextDouble());
        float pitch = 1f + bank.pitchSpread * (2f * (float)rng.NextDouble() - 1f);
        steps.Play(bank.monsterFootsteps[i], volume, pitch);
        stepsPlayed++;
        lastStepTime = Time.timeAsDouble;
        if (stepsPlayed == 1) Debug.Log($"CreatureAudio: first footstep (clip {i}, volume {volume:0.00}, speed {speed:0.0} m/s).");
    }

    // ---------- Vent hooks (cosmetic, on every peer) ----------
    // Called when the replicated vent phase changes. There are no suitable vent sounds yet, so these are deliberately silent:
    // add a clip to the AudioBank and play it here. They are separate from the AI's logical hearing: the creature's own sounds are
    // ignored by its hearing, and nothing here emits a NoiseSystem event.
    //   Entering    -> the creature squeezes into the vent (entry sound / scrape)
    //   Travelling  -> movement and rattling in the duct (loop, ideally 3D from the creature's real position)
    //   Preparing   -> the pre-emergence warning the players can hear
    //   Exiting     -> the grate and the creature coming out
    public void OnVentPhase(VentPhase previous, VentPhase now)
    {
        // Intentionally empty: see above.
    }

    // F3 (the creature's debug label toggle) shows what this component is doing, so a silent creature can be diagnosed by eye.
    void OnGUI()
    {
        if (ai == null || !ai.showDebugLabel) return;
        var nm = NetworkManager.Singleton;
        var local = nm != null && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
        float d = local != null ? Vector3.Distance(transform.position, local.transform.position) : -1f;
        GUI.Label(new Rect(20, Screen.height - 130, 520, 40),
            $"monster audio: {stepsPlayed} steps played, last {(stepsPlayed == 0 ? -1 : Time.timeAsDouble - lastStepTime):0.0}s ago, speed {speed:0.0} m/s, " +
            $"distance {d:0.0} m, {(blocked ? "muffled by a wall" : "clear")}, cutoff {cutoff:0} Hz");
    }

    // The local listener: muffling by walls, and the rare snarl.
    void Listen(bool roundActive)
    {
        var nm = NetworkManager.Singleton;
        var local = nm != null && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
        if (local == null) return;
        var life = local.GetComponent<PlayerLife>();
        if (life != null && !life.IsAlive) { blocked = false; return; }

        Vector3 ear = local.transform.position + Vector3.up * 1.5f, mouth = transform.position + Vector3.up;
        blocked = Physics.Linecast(mouth, ear, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            && hit.transform.root != local.transform.root;

        float d = Vector3.Distance(transform.position, local.transform.position);
        var state = ai.CurrentState;
        bool hunting = state == CreatureState.Chase || state == CreatureState.Pursue;
        bool close = d <= bank.snarlCloseDistance || (hunting && d <= bank.snarlHuntDistance);
        if (!roundActive || !close || blocked || Time.timeAsDouble < nextSnarl || voice.IsPlaying) return;

        voice.Play(bank.snarl, bank.snarlVolume, 1f); // the original recording, unmodified
        nextSnarl = Time.timeAsDouble + Mathf.Lerp(bank.snarlMinCooldown, bank.snarlMaxCooldown, (float)rng.NextDouble());
    }
}
