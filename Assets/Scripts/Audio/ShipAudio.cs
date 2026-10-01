using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What kind of sound a source plays. Without an AudioMixer asset (Unity has no supported public API to create or edit one from a
/// script), this is the central routing: it decides the category volume and whether the ship's reverb applies.
///   World, Creature, Players - in the world: full room reverb, occlusion where it matters.
///   OwnBody                  - your own footsteps: a lighter share of the reverb.
///   Ambience, Internal, UI   - dry: the background bed, and subjective sounds (heartbeat, tension riser, your own breathing).
/// </summary>
public enum AudioCategory : byte { World, Creature, Players, OwnBody, Ambience, Internal, UI }

public static class AudioRouting
{
    /// <summary>Sets a source up for its category: reverb send and doppler. Volume is applied when it plays (<see cref="Volume"/>).</summary>
    public static void Configure(AudioSource s, AudioCategory category)
    {
        var bank = AudioBank.Get();
        s.bypassReverbZones = category is AudioCategory.Ambience or AudioCategory.Internal or AudioCategory.UI;
        s.reverbZoneMix = category == AudioCategory.OwnBody ? (bank != null ? bank.ownBodyReverbMix : 0.5f) : 1f;
        s.dopplerLevel = 0f;
    }

    /// <summary>Master volume times the category's volume, from the AudioBank.</summary>
    public static float Volume(AudioCategory category)
    {
        var b = AudioBank.Get();
        if (b == null) return 1f;
        float v = category switch
        {
            AudioCategory.World => b.worldVolume,
            AudioCategory.Creature => b.creatureVolume,
            AudioCategory.Players or AudioCategory.OwnBody => b.playersVolume,
            AudioCategory.Ambience => b.ambienceVolume,
            AudioCategory.Internal => b.internalVolume,
            _ => b.uiVolume,
        };
        return b.masterVolume * v;
    }

    /// <summary>
    /// A custom rolloff curve (x = distance / maxDistance, as Unity expects) from <see cref="AudioRules.DistanceAttenuation"/>: full
    /// volume inside <paramref name="full"/>, a perceptual inverse-distance fall, silent at <paramref name="max"/>.
    /// </summary>
    public static AnimationCurve Rolloff(float full, float max)
    {
        const int Steps = 24;
        var keys = new Keyframe[Steps + 1];
        for (int i = 0; i <= Steps; i++)
        {
            float x = i / (float)Steps;
            keys[i] = new Keyframe(x, AudioRules.DistanceAttenuation(x * max, full, max));
        }
        for (int i = 0; i <= Steps; i++) // gentle tangents from the neighbours, so the curve is smooth but never rises
        {
            float l = keys[Mathf.Max(0, i - 1)].value, r = keys[Mathf.Min(Steps, i + 1)].value;
            float slope = (r - l) / ((Mathf.Min(Steps, i + 1) - Mathf.Max(0, i - 1)) / (float)Steps);
            keys[i].inTangent = keys[i].outTangent = Mathf.Min(0f, slope);
        }
        return new AnimationCurve(keys);
    }
}

/// <summary>The one enabled AudioListener in this process (each player instance has its own), re-found at most once a second.</summary>
public static class AudioListenerLocator
{
    static AudioListener cached;
    static float nextSearch;

    public static AudioListener Current
    {
        get
        {
            if (cached != null && cached.isActiveAndEnabled) return cached;
            if (Time.unscaledTime < nextSearch) return null;
            nextSearch = Time.unscaledTime + 1f;
            cached = null;
            foreach (var l in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (l.isActiveAndEnabled) { cached = l; break; }
            return cached;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        cached = null;
        nextSearch = 0f;
    }
}

/// <summary>
/// Reusable occlusion for important 3D sounds (the creature, doors, vent mouths, other players). A few times a second, and only while
/// the listener is within earshot (<see cref="maxRange"/>), ONE ray from the local listener counts the solid things in the way (walls, closed doors; an open
/// door has slid out of the way). More of them means a lower low-pass cutoff and a somewhat lower volume, never silence. Changes are
/// smoothed. Entirely local: every peer computes it for its own listener; nothing reaches the creature's logical hearing.
/// Add it AFTER the AudioSources on the same object (the low-pass filter acts on the sources before it).
/// </summary>
[DisallowMultipleComponent]
public class AudioOcclusion : MonoBehaviour
{
    [Tooltip("Colliders under this transform (the emitter's own body or door) never count as occluding it")] public Transform ignoreRoot;
    [Tooltip("No rays while the listener is farther than this (the sound is inaudible there anyway)")] public float maxRange = 30f;

    static readonly RaycastHit[] hits = new RaycastHit[8];
    readonly List<SfxPool> pools = new();
    readonly List<AudioSource> loops = new();
    readonly List<float> loopBase = new();
    AudioLowPassFilter filter;
    float timer, amount, targetAmount, appliedGain = -1f, appliedCutoff = -1f;

    public bool Obstructed { get; private set; }
    public int Obstacles { get; private set; }
    public float Cutoff { get; private set; } = 22000f;
    public float Gain { get; private set; } = 1f;

    void Awake()
    {
        filter = GetComponent<AudioLowPassFilter>();
        if (filter == null) filter = gameObject.AddComponent<AudioLowPassFilter>();
        filter.cutoffFrequency = 22000f;
        timer = Random.value * 0.12f; // spread the rays of many emitters over frames
    }

    public void Attach(SfxPool pool)
    {
        if (pool != null && !pools.Contains(pool)) pools.Add(pool);
    }

    /// <summary>A looping source whose own volume is <paramref name="baseVolume"/>; occlusion multiplies it.</summary>
    public void AttachLoop(AudioSource source, float baseVolume)
    {
        int i = loops.IndexOf(source);
        if (i < 0) { loops.Add(source); loopBase.Add(baseVolume); }
        else loopBase[i] = baseVolume;
        appliedGain = -1f;
    }

    /// <summary>Re-evaluate at once and jump straight to the result (an emitter that was just moved, or a decision that needs it now).</summary>
    public void Snap()
    {
        var bank = AudioBank.Get();
        if (bank == null) return;
        Evaluate(bank);
        amount = targetAmount;
        appliedGain = appliedCutoff = -1f;
        Update();
    }

    void Update()
    {
        var bank = AudioBank.Get();
        if (bank == null) return;
        if ((timer -= Time.deltaTime) <= 0f)
        {
            timer = 1f / Mathf.Max(1f, bank.occlusionHz);
            Evaluate(bank); // kept current while in earshot, so a sound that starts is already right (no clear first step behind a wall)
        }
        amount = Mathf.MoveTowards(amount, targetAmount, Time.deltaTime / Mathf.Max(0.02f, bank.occlusionSmoothSeconds));
        Cutoff = AudioRules.OcclusionCutoff(amount, Mathf.Min(22000f, ShipAcoustics.WorldCutoffCap), bank.occludedCutoff);
        Gain = AudioRules.OcclusionGain(amount, bank.occludedGain);
        if (Mathf.Abs(Cutoff - appliedCutoff) > 5f) { filter.cutoffFrequency = Cutoff; appliedCutoff = Cutoff; }
        if (Mathf.Abs(Gain - appliedGain) > 0.002f)
        {
            appliedGain = Gain;
            foreach (var p in pools) p.SetGain(Gain);
            for (int i = 0; i < loops.Count; i++) if (loops[i] != null) loops[i].volume = loopBase[i] * Gain;
        }
    }

    void Evaluate(AudioBank bank)
    {
        var listener = AudioListenerLocator.Current;
        if (listener == null) { targetAmount = 0f; Obstructed = false; Obstacles = 0; return; }
        Vector3 from = listener.transform.position, to = transform.position, d = to - from;
        float len = d.magnitude;
        if (len > maxRange) return; // out of earshot: no ray
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
}

public static class AudioClipTools
{
    /// <summary>
    /// A short piece cut from a clip (which must be Decompress On Load), with short fades at both ends so it never clicks. Null when the
    /// samples cannot be read. Used for the heartbeat beats and for the temporary vent-grille fallback cut from the door sound.
    /// </summary>
    public static AudioClip Slice(AudioClip src, float start, float length, float fadeOutSeconds, string name)
    {
        if (src == null) return null;
        int ch = src.channels, freq = src.frequency;
        int first = Mathf.Clamp((int)(start * freq), 0, src.samples - 1);
        int frames = Mathf.Min((int)(length * freq), src.samples - first);
        if (frames <= 0) return null;
        var data = new float[frames * ch];
        if (!src.GetData(data, first)) return null;
        int fadeIn = Mathf.Min(frames, freq / 200), fadeOut = Mathf.Min(frames, (int)(freq * fadeOutSeconds));
        for (int f = 0; f < frames; f++)
        {
            float g = Mathf.Min(f < fadeIn ? (f + 1f) / fadeIn : 1f, frames - f <= fadeOut ? (frames - f) / (float)Mathf.Max(1, fadeOut) : 1f);
            for (int c = 0; c < ch; c++) data[f * ch + c] *= g;
        }
        var clip = AudioClip.Create(name, frames, ch, freq, false);
        clip.SetData(data, 0);
        return clip;
    }
}
