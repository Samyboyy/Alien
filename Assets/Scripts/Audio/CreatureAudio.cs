using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The creature's cosmetic sound, on every peer (added at runtime by CreatureAI when an AudioBank exists). Driven only by its replicated
/// movement, state, vent phase and vent entry/exit ids; nothing here affects the AI, and nothing is emitted into the logical NoiseSystem.
///
/// Footsteps: one per stride walked, a different clip each time (shuffled, never twice in a row), fully 3D with a perceptual rolloff
/// (full volume close, faint by the middle distances, silent at the maximum), occluded by walls and closed doors. None while in a vent.
/// Snarl: the original recording, for the local player only, when close or hunting nearby, then a long random cooldown.
/// Vents: a grille sound at the ENTRANCE as it goes in, an optional duct rattle that follows its real (hidden) position, the warning at
/// the EXIT while it prepares to emerge, and the grille at the exit as it comes out. Clips come from the AudioBank vent slots; while a slot
/// is empty a short piece of the door sound stands in for the grille and the warning, and the rattle stays silent.
/// </summary>
public class CreatureAudio : MonoBehaviour
{
    // A sound source fixed at a vent mouth (moved there for each cue), occluded like any world sound.
    sealed class MouthEmitter
    {
        public GameObject go;
        public SfxPool pool;

        public MouthEmitter(string name, AudioBank bank, AnimationCurve curve)
        {
            go = new GameObject(name);
            pool = new SfxPool(go, 2, AudioCategory.Creature, 1f, bank.ventSoundMaxDistance, customRolloff: curve);
            occlusion = go.AddComponent<AudioOcclusion>();
            occlusion.maxRange = bank.ventSoundMaxDistance + 2f;
            occlusion.Attach(pool);
        }

        public AudioOcclusion occlusion;

        public void Play(Vector3 position, AudioClip clip, float volume, float pitch)
        {
            go.transform.position = position;
            occlusion.Snap(); // it just moved to this mouth: muffle correctly from the first moment
            pool.Play(clip, volume, pitch);
        }
    }

    CreatureAI ai;
    AudioBank bank;
    SfxPool steps, voice;
    AudioSource travel;
    AudioOcclusion occlusion;
    MouthEmitter entryMouth, exitMouth;
    AudioClip enterClip, warnClip, exitClip;
    bool enterFallback, warnFallback, exitFallback, initialised;
    NoRepeatBag bag;
    System.Random rng;
    Vector3 last;
    float stride, speed, checkTimer, sampleTime, sampleDist;
    double nextSnarl, lastStepTime;
    bool wasActive;
    int stepsPlayed;

    void Start() => EnsureInit();

    bool EnsureInit()
    {
        if (initialised) return bank != null && ai != null;
        initialised = true;
        bank = AudioBank.Get();
        ai = GetComponent<CreatureAI>();
        if (bank == null || ai == null) { enabled = false; return false; }

        var host = new GameObject("Creature Audio");
        host.transform.SetParent(transform, false);
        host.transform.localPosition = Vector3.up; // roughly chest height
        var stepCurve = AudioRouting.Rolloff(bank.monsterStepFullDistance, bank.monsterStepMaxDistance);
        steps = new SfxPool(host, 3, AudioCategory.Creature, bank.monsterStepSpatial, bank.monsterStepMaxDistance, customRolloff: stepCurve);
        voice = new SfxPool(host, 1, AudioCategory.Creature, bank.snarlSpatial, bank.monsterStepMaxDistance, customRolloff: stepCurve);
        var ventCurve = AudioRouting.Rolloff(bank.ventSoundFullDistance, bank.ventSoundMaxDistance);
        travel = host.AddComponent<AudioSource>();
        travel.playOnAwake = false;
        travel.loop = true;
        travel.clip = bank.ventTravel; // null: the duct is silent until a suitable clip exists
        travel.spatialBlend = 1f;
        travel.maxDistance = bank.ventSoundMaxDistance;
        travel.rolloffMode = AudioRolloffMode.Custom;
        travel.SetCustomCurve(AudioSourceCurveType.CustomRolloff, ventCurve);
        AudioRouting.Configure(travel, AudioCategory.Creature);
        // Added after every source on this object: the low-pass sits behind them.
        occlusion = host.AddComponent<AudioOcclusion>();
        occlusion.ignoreRoot = transform;
        occlusion.maxRange = Mathf.Max(bank.monsterStepMaxDistance, bank.ventSoundMaxDistance) + 2f;
        occlusion.Attach(steps);
        occlusion.Attach(voice);
        occlusion.AttachLoop(travel, bank.ventTravelVolume * AudioRouting.Volume(AudioCategory.Creature));

        entryMouth = new MouthEmitter("Vent Entry Audio", bank, ventCurve);
        exitMouth = new MouthEmitter("Vent Exit Audio", bank, ventCurve);
        enterClip = Resolve(bank.ventEnter, "vent enter (door fallback)", out enterFallback);
        warnClip = Resolve(bank.ventWarning, "vent warning (door fallback)", out warnFallback);
        exitClip = Resolve(bank.ventExit, "vent exit (door fallback)", out exitFallback);

        rng = new System.Random(GetInstanceID());
        bag = new NoRepeatBag(bank.monsterFootsteps.Length, rng);
        last = transform.position;
        if (ai.CurrentVentPhase == VentPhase.Travelling) StartTravel(); // a late joiner arriving mid-trip hears the duct, not old one-shots
        return true;
    }

    // A dedicated clip if there is one, else a short piece of the door sound (the grille of a vent is the same kind of mechanism).
    AudioClip Resolve(AudioClip dedicated, string name, out bool fallback)
    {
        fallback = dedicated == null;
        return dedicated != null ? dedicated : AudioClipTools.Slice(bank.doorSound, 0f, bank.ventFallbackLength, 0.25f, name);
    }

    void OnDestroy()
    {
        if (entryMouth != null) Destroy(entryMouth.go);
        if (exitMouth != null) Destroy(exitMouth.go);
        if (enterFallback && enterClip != null) Destroy(enterClip);
        if (warnFallback && warnClip != null) Destroy(warnClip);
        if (exitFallback && exitClip != null) Destroy(exitClip);
    }

    void Update()
    {
        if (!initialised || steps == null) return;
        bool active = RoundManager.IsActive;
        Vector3 delta = transform.position - last;
        last = transform.position;
        if (active && !wasActive) nextSnarl = Time.timeAsDouble + bank.snarlFirstDelay;
        wasActive = active;

        // Footsteps follow the movement itself, whatever the round state, but not while it is inside a vent (it is not walking there).
        if (ai.CurrentVentPhase == VentPhase.None || ai.CurrentVentPhase == VentPhase.Approaching) Step(delta);
        else { stride = speed = 0f; sampleDist = sampleTime = 0f; }

        if ((checkTimer -= Time.deltaTime) <= 0f)
        {
            checkTimer = 0.1f;
            Listen(active);
        }
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
    }

    // ---------- Vent presentation (every peer, from the replicated phase and ids) ----------

    public void OnVentPhase(VentPhase previous, VentPhase now)
    {
        if (!EnsureInit()) return;
        switch (now)
        {
            case VentPhase.Entering:
                PlayMouth(entryMouth, ai.VentEntryId, enterClip, bank.ventEnterVolume, enterFallback ? bank.ventEnterFallbackPitch : 1f);
                break;
            case VentPhase.Travelling:
                StartTravel();
                break;
            case VentPhase.Preparing:
                StopTravel();
                PlayMouth(exitMouth, ai.VentExitId, warnClip, bank.ventWarningVolume, warnFallback ? bank.ventWarningFallbackPitch : 1f);
                break;
            case VentPhase.Exiting:
                StopTravel();
                PlayMouth(exitMouth, ai.VentExitId, exitClip, bank.ventExitVolume, exitFallback ? bank.ventExitFallbackPitch : 1f);
                break;
            default:
                StopTravel();
                break;
        }
    }

    // The sound plays at the vent mouth itself (a little in front of the grille), never at the creature's hidden position.
    void PlayMouth(MouthEmitter emitter, int id, AudioClip clip, float volume, float pitch)
    {
        if (clip == null) return;
        var net = CreatureVentNetwork.Instance;
        Vector3 at = transform.position + Vector3.up;
        if (net != null && id >= 0 && id < net.entrances.Length && net.entrances[id] != null && net.entrances[id].face != null)
        {
            var e = net.entrances[id];
            Vector3 face = e.face.position, outward = e.approach != null ? e.approach.position - face : Vector3.zero;
            outward.y = 0f;
            at = face + (outward.sqrMagnitude > 0.0001f ? outward.normalized * 0.35f : Vector3.zero);
        }
        emitter.Play(at, clip, volume, pitch);
    }

    void StartTravel()
    {
        if (travel == null || travel.clip == null || travel.isPlaying) return;
        travel.volume = bank.ventTravelVolume * AudioRouting.Volume(AudioCategory.Creature) * (occlusion != null ? occlusion.Gain : 1f);
        travel.Play();
    }

    void StopTravel()
    {
        if (travel != null && travel.isPlaying) travel.Stop();
    }

    /// <summary>Stops every vent sound (despawn, shutdown).</summary>
    public void StopVentAudio()
    {
        StopTravel();
        entryMouth?.pool.StopAll();
        exitMouth?.pool.StopAll();
    }

    // ---------- Snarl and debug ----------

    // The local listener: the rare snarl (never through a wall).
    void Listen(bool roundActive)
    {
        var nm = NetworkManager.Singleton;
        var local = nm != null && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
        if (local == null) return;
        var life = local.GetComponent<PlayerLife>();
        if (life != null && !life.IsAlive) return;

        float d = Vector3.Distance(transform.position, local.transform.position);
        var state = ai.CurrentState;
        bool hunting = state == CreatureState.Chase || state == CreatureState.Pursue;
        bool close = d <= bank.snarlCloseDistance || (hunting && d <= bank.snarlHuntDistance);
        if (!roundActive || !close || ai.CurrentVentPhase != VentPhase.None || Time.timeAsDouble < nextSnarl || voice.IsPlaying) return;
        occlusion.Snap();
        if (occlusion.Obstructed) return; // never through a wall

        voice.Play(bank.snarl, bank.snarlVolume, 1f); // the original recording, unmodified
        nextSnarl = Time.timeAsDouble + Mathf.Lerp(bank.snarlMinCooldown, bank.snarlMaxCooldown, (float)rng.NextDouble());
    }

    // F3 (the creature's debug label toggle): what the creature sounds like from where you are.
    void OnGUI()
    {
        if (!initialised || ai == null || !ai.showDebugLabel || occlusion == null) return;
        var listener = AudioListenerLocator.Current;
        float d = listener != null ? Vector3.Distance(transform.position + Vector3.up, listener.transform.position) : -1f;
        float att = d < 0f ? 0f : AudioRules.DistanceAttenuation(d, bank.monsterStepFullDistance, bank.monsterStepMaxDistance) * occlusion.Gain;
        string db = att > 0.0001f ? $"{20f * Mathf.Log10(att):0} dB" : "silent";
        GUI.Label(new Rect(20, Screen.height - 150, 760, 60),
            $"creature audio: listener {d:0.0} m, {(occlusion.Obstructed ? $"obstructed ({occlusion.Obstacles})" : "clear")}, cutoff {occlusion.Cutoff:0} Hz, " +
            $"footstep level about {db} ({att:0.00}), speed {speed:0.0} m/s, vent {ai.CurrentVentPhase}\n" +
            $"steps played {stepsPlayed}, last {(stepsPlayed == 0 ? -1 : Time.timeAsDouble - lastStepTime):0.0}s ago, space {ShipAcoustics.CurrentSpace}" +
            $"{(warnFallback ? ", vent warning: door fallback" : "")}{(travel != null && travel.clip == null ? ", duct rattle: no clip" : "")}");
    }
}
