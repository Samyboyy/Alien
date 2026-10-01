using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-emitter acoustics for important 3D sounds (the creature, doors, vent mouths, players, the motion tracker, flickering lights).
/// Kept separate on purpose from the listener's late room reverb (ShipAcoustics):
///
///  - Early reflections: an AudioReverbFilter set to the space the EMITTER is in (corridor: short and bright; machinery: dense and a
///    little longer; small room: tight; large open space: less immediate; crawlspace and ducts: boxy), mostly early reflections with only
///    a little of its own late part. Strongest up close, where the direct sound otherwise dominates, easing off with distance (the zone's
///    late reverb carries distant sounds). A capped number of emitters may use it at once (ReflectionBudget); beyond that a sound plays
///    dry rather than creating anything new. Internal, ambience and UI sounds never get one (AcousticRules.CategoryReflectionWeight).
///  - Occlusion: a few times a second while the listener is in earshot, ONE ray from the local listener counts walls and closed doors in
///    the way: a lower low-pass cutoff and a somewhat lower volume, never silence. Out of earshot it returns to clear.
///
/// The low-pass sits after the reflections, so a sound behind a wall is muffled with its reflections. Everything is local to this peer and
/// purely cosmetic: nothing reaches the creature's logical hearing. Add it AFTER the AudioSources on the same object.
/// </summary>
[DisallowMultipleComponent]
public class EmitterAcoustics : MonoBehaviour
{
    /// <summary>Every live emitter, for the on-demand diagnostics only (never searched in a hot path).</summary>
    public static readonly List<EmitterAcoustics> All = new();

    static ReflectionBudget budget;
    static readonly List<bool> zoneContains = new();
    static readonly List<int> zonePriority = new();
    static readonly RaycastHit[] hits = new RaycastHit[8];

    public static int ReflectionSlotsInUse => budget?.InUse ?? 0;
    public static int ReflectionSlotsCapacity => budget?.Capacity ?? 0;

    [Tooltip("Colliders under this transform (the emitter's own body or door) never count as occluding it")] public Transform ignoreRoot;
    [Tooltip("No rays while the listener is farther than this (the sound is inaudible there anyway)")] public float maxRange = 30f;
    [Tooltip("Share of the early reflections for this emitter (world sounds 1, your own body lightly, internal 0)")] [Range(0f, 1f)] public float reflectionWeight = 1f;
    [Tooltip("Off for sounds the listener makes itself (nothing can be between you and your own feet)")] public bool occlude = true;
    [Tooltip("Editor preview only: use this space instead of looking it up")] public bool useOverrideSpace;
    public AcousticSpace overrideSpace;

    readonly List<SfxPool> pools = new();
    readonly List<AudioSource> loops = new();
    readonly List<float> loopBase = new();
    AudioReverbFilter reflection;
    AudioLowPassFilter lowPass;
    float timer, amount, targetAmount, appliedGain = -1f, appliedCutoff = -1f, silentTime, spaceTimer;
    bool hasSlot;
    Vector3 spacePosition = new(float.MaxValue, 0f, 0f);
    ReflectionSettings current, target;

    public bool Obstructed { get; private set; }
    public int Obstacles { get; private set; }
    public float Cutoff { get; private set; } = 22000f;
    public float Gain { get; private set; } = 1f;
    public float ListenerDistance { get; private set; } = -1f;
    public AcousticSpace Space { get; private set; } = AcousticSpace.Neutral;
    /// <summary>The current early-reflection weight after distance (0 when it has no slot), for diagnostics.</summary>
    public float ReflectionNow { get; private set; }
    public bool HasReflectionSlot => hasSlot;

    void Awake()
    {
        // Order matters: the sources (already on this object), then the reflections, then the low-pass that muffles both.
        reflection = gameObject.AddComponent<AudioReverbFilter>();
        reflection.reverbPreset = AudioReverbPreset.User;
        reflection.dryLevel = 0f;
        reflection.enabled = false;
        lowPass = GetComponent<AudioLowPassFilter>();
        if (lowPass == null) lowPass = gameObject.AddComponent<AudioLowPassFilter>();
        lowPass.cutoffFrequency = 22000f;
        timer = Random.value * 0.12f; // spread the rays of many emitters over frames
        current = target = AcousticDefaults.Reflections(AcousticSpace.Neutral);
    }

    void OnEnable() => All.Add(this);

    void OnDisable()
    {
        All.Remove(this);
        ReleaseSlot();
    }

    public void Attach(SfxPool pool)
    {
        if (pool == null || pools.Contains(pool)) return;
        pools.Add(pool);
        pool.Acoustics = this;
    }

    /// <summary>A looping source whose own volume is <paramref name="baseVolume"/> (category included); occlusion multiplies it.</summary>
    public void AttachLoop(AudioSource source, float baseVolume)
    {
        int i = loops.IndexOf(source);
        if (i < 0) { loops.Add(source); loopBase.Add(baseVolume); }
        else loopBase[i] = baseVolume;
        appliedGain = -1f;
    }

    /// <summary>Counts the sources playing right now (diagnostics).</summary>
    public int PlayingVoices()
    {
        int n = 0;
        foreach (var p in pools) n += p.PlayingCount;
        foreach (var s in loops) if (s != null && s.isPlaying) n++;
        return n;
    }

    /// <summary>
    /// Called just before one of its sources starts: takes a reflection slot if one is free and sets the reflections for where the emitter
    /// is right now (no audible jump at the start of the sound). Without a slot the sound plays dry.
    /// </summary>
    public void BeforePlay()
    {
        var bank = AudioBank.Get();
        silentTime = 0f;
        if (bank == null) return;
        // An idle emitter only looks now and then (Update), so look again now and start from the fresh occlusion.
        bool wasIdle = !Audible();
        Evaluate(bank);
        if (wasIdle) { amount = targetAmount; ApplyOcclusion(bank); }
        if (!hasSlot && bank.reflectionsEnabled && reflectionWeight > 0f)
        {
            budget ??= new ReflectionBudget(bank.maxReflectionEmitters);
            budget.Capacity = Mathf.Max(0, bank.maxReflectionEmitters);
            hasSlot = budget.TryAcquire();
        }
        if (!hasSlot) { reflection.enabled = false; ReflectionNow = 0f; return; }
        UpdateSpace(bank, true);
        target = ReflectionTarget(bank);
        current = target;
        ApplyReflection();
        reflection.enabled = true;
    }

    /// <summary>Re-evaluate at once and jump straight to the result (an emitter that was just moved, or a decision that needs it now).</summary>
    public void Snap()
    {
        var bank = AudioBank.Get();
        if (bank == null) return;
        Evaluate(bank);
        UpdateSpace(bank, true);
        amount = targetAmount;
        appliedGain = appliedCutoff = -1f;
        ApplyOcclusion(bank);
    }

    void ReleaseSlot()
    {
        if (!hasSlot) return;
        hasSlot = false;
        budget?.Release();
        if (reflection != null) reflection.enabled = false;
        ReflectionNow = 0f;
    }

    void Update()
    {
        var bank = AudioBank.Get();
        if (bank == null) return;
        float dt = Time.deltaTime;
        foreach (var p in pools) p.Tick();
        bool audible = Audible();

        if ((timer -= dt) <= 0f)
        {
            // Full rate while sounding; an idle emitter looks a quarter as often (a sound that starts looks again at once: BeforePlay).
            timer = (audible ? 1f : 4f) / Mathf.Max(1f, bank.occlusionHz);
            Evaluate(bank);
            if (hasSlot) { UpdateSpace(bank, false); target = ReflectionTarget(bank); }
        }
        amount = Mathf.MoveTowards(amount, targetAmount, dt / Mathf.Max(0.02f, bank.occlusionSmoothSeconds));
        ApplyOcclusion(bank);

        if (!hasSlot) return;
        silentTime = audible ? 0f : silentTime + dt;
        if (silentTime > bank.reflectionTailSeconds) { ReleaseSlot(); return; }
        float k = bank.reflectionTransitionSeconds;
        current.room = Mathf.RoundToInt(AcousticRules.Approach(current.room, target.room, dt, k));
        current.roomHF = Mathf.RoundToInt(AcousticRules.Approach(current.roomHF, target.roomHF, dt, k));
        current.reflections = Mathf.RoundToInt(AcousticRules.Approach(current.reflections, target.reflections, dt, k));
        current.reverb = Mathf.RoundToInt(AcousticRules.Approach(current.reverb, target.reverb, dt, k));
        current.decayTime = AcousticRules.Approach(current.decayTime, target.decayTime, dt, k);
        current.decayHFRatio = AcousticRules.Approach(current.decayHFRatio, target.decayHFRatio, dt, k);
        current.reflectionsDelay = AcousticRules.Approach(current.reflectionsDelay, target.reflectionsDelay, dt, k);
        current.reverbDelay = AcousticRules.Approach(current.reverbDelay, target.reverbDelay, dt, k);
        current.diffusion = AcousticRules.Approach(current.diffusion, target.diffusion, dt, k);
        current.density = AcousticRules.Approach(current.density, target.density, dt, k);
        ApplyReflection();
    }

    bool Audible()
    {
        foreach (var p in pools) if (p.IsPlaying) return true;
        foreach (var s in loops) if (s != null && s.isPlaying) return true;
        return false;
    }

    void ApplyOcclusion(AudioBank bank)
    {
        Cutoff = AudioRules.OcclusionCutoff(amount, Mathf.Min(22000f, ShipAcoustics.WorldCutoffCap), bank.occludedCutoff);
        Gain = AudioRules.OcclusionGain(amount, bank.occludedGain);
        if (Mathf.Abs(Cutoff - appliedCutoff) > 5f) { lowPass.cutoffFrequency = Cutoff; appliedCutoff = Cutoff; }
        if (Mathf.Abs(Gain - appliedGain) > 0.002f)
        {
            appliedGain = Gain;
            foreach (var p in pools) p.SetGain(Gain);
            for (int i = 0; i < loops.Count; i++) if (loops[i] != null) loops[i].volume = AcousticRules.Compose(loopBase[i], 1f, Gain);
        }
    }

    void ApplyReflection()
    {
        reflection.room = current.room;
        reflection.roomHF = current.roomHF;
        reflection.decayTime = Mathf.Max(0.1f, current.decayTime);
        reflection.decayHFRatio = Mathf.Clamp(current.decayHFRatio, 0.1f, 2f);
        reflection.reflectionsLevel = current.reflections;
        reflection.reflectionsDelay = Mathf.Clamp(current.reflectionsDelay, 0f, 0.3f);
        reflection.reverbLevel = current.reverb;
        reflection.reverbDelay = Mathf.Clamp(current.reverbDelay, 0f, 0.1f);
        reflection.diffusion = Mathf.Clamp(current.diffusion, 0f, 100f);
        reflection.density = Mathf.Clamp(current.density, 0f, 100f);
    }

    ReflectionSettings ReflectionTarget(AudioBank bank)
    {
        var profile = bank.Profile(Space);
        var settings = profile != null ? profile.Reflection : AcousticDefaults.Reflections(Space);
        float distance = ListenerDistance < 0f ? 0f : ListenerDistance;
        float weight = reflectionWeight * AcousticRules.ReflectionDistanceWeight(distance, bank.reflectionNear, bank.reflectionFar, bank.reflectionFarWeight);
        ReflectionNow = weight;
        return AcousticRules.Bounded(settings, AcousticRules.WeightToMillibels(weight), bank.reflectionMaxRoom, bank.reflectionMaxEarly, bank.reflectionMaxOwnTail);
    }

    // The space the EMITTER is in: above the ceiling is the duct network, otherwise the highest-priority acoustic zone around it, otherwise
    // the ship's fallback. Only looked up again when the emitter has moved (or once a second for moving ones).
    void UpdateSpace(AudioBank bank, bool force)
    {
        if (useOverrideSpace) { Space = overrideSpace; return; }
        Vector3 p = transform.position;
        spaceTimer -= Time.deltaTime;
        if (!force && (p - spacePosition).sqrMagnitude < 0.5f && spaceTimer > 0f) return;
        spacePosition = p;
        spaceTimer = 1f;
        if (p.y > bank.ductHeight) { Space = AcousticSpace.Duct; return; }
        var zones = ShipAcousticZone.All;
        zoneContains.Clear();
        zonePriority.Clear();
        for (int i = 0; i < zones.Count; i++)
        {
            zoneContains.Add(zones[i].Contains(p));
            zonePriority.Add(zones[i].priority);
        }
        int best = AcousticRules.Select(zoneContains, zonePriority, zones.Count);
        Space = best >= 0 ? zones[best].space : zones.Count > 0 ? bank.fallbackSpace : AcousticSpace.Neutral;
    }

    void Evaluate(AudioBank bank)
    {
        var listener = AudioListenerLocator.Current;
        if (listener == null) { targetAmount = 0f; Obstructed = false; Obstacles = 0; ListenerDistance = -1f; return; }
        Vector3 from = listener.transform.position, to = transform.position, d = to - from;
        float len = d.magnitude;
        ListenerDistance = len;
        if (!occlude || len > maxRange)
        {
            // Out of earshot (or a sound of your own): back to clear, so nothing stale is left when it comes back into range.
            targetAmount = 0f;
            Obstructed = false;
            Obstacles = 0;
            return;
        }
        int count = 0;
        if (len > 0.05f)
        {
            int n = Physics.RaycastNonAlloc(from, d / len, hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Transform listenerRoot = listener.transform.root;
            for (int i = 0; i < n; i++)
            {
                var t = hits[i].collider.transform;
                if ((ignoreRoot != null && t.IsChildOf(ignoreRoot)) || t.root == listenerRoot) continue;
                count++;
            }
        }
        Obstacles = count;
        Obstructed = count > 0;
        targetAmount = AudioRules.OcclusionAmount(count, bank.occlusionPerObstacle);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        All.Clear();
        budget = null;
        zoneContains.Clear();
        zonePriority.Clear();
    }
}
