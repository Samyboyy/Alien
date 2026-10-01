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
