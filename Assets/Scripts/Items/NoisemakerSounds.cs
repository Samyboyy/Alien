using UnityEngine;

/// <summary>
/// The noisemaker's sounds, always available. Each of the five (pickup, throw, impact, pulse, shutdown) is the AudioBank's clip when one is
/// assigned, otherwise a temporary generated stand-in (NoisemakerSynth) that is made ONCE per session and cached. So the prototype is
/// audible with every optional field unset, and the AudioBank does not even have to exist: volumes then use the same defaults as its fields.
/// </summary>
public static class NoisemakerSounds
{
    static readonly AudioClip[] cache = new AudioClip[NoisemakerSynth.SoundCount];
    public static int GeneratedCount { get; private set; }

    /// <summary>The clip to play and whether it is the generated stand-in. Never null.</summary>
    public static AudioClip Resolve(AudioBank bank, NoisemakerSound kind, out bool fallback)
    {
        var assigned = bank == null ? null : kind switch
        {
            NoisemakerSound.Pickup => bank.noisemakerPickup,
            NoisemakerSound.Throw => bank.noisemakerThrow,
            NoisemakerSound.Impact => bank.noisemakerImpact,
            NoisemakerSound.Pulse => bank.noisemakerPulse,
            _ => bank.noisemakerSpent,
        };
        fallback = assigned == null;
        return fallback ? Fallback(kind) : assigned;
    }

    /// <summary>Volume (0..1) of a kind: the bank's value, or the default when there is no bank.</summary>
    public static float Volume(AudioBank bank, NoisemakerSound kind) => bank == null ? DefaultVolume(kind) : kind switch
    {
        NoisemakerSound.Pickup => bank.noisemakerPickupVolume,
        NoisemakerSound.Throw => bank.noisemakerThrowVolume,
        NoisemakerSound.Impact => bank.noisemakerImpactVolume,
        NoisemakerSound.Pulse => bank.noisemakerPulseVolume,
        _ => bank.noisemakerSpentVolume,
    };

    static float DefaultVolume(NoisemakerSound kind) => kind switch
    {
        NoisemakerSound.Pickup => 0.6f,
        NoisemakerSound.Throw => 0.5f,
        NoisemakerSound.Impact => 0.8f,
        NoisemakerSound.Pulse => 0.7f,
        _ => 0.5f,
    };

    static AudioClip Fallback(NoisemakerSound kind)
    {
        int i = (int)kind;
        if (cache[i] != null) return cache[i];
        const int Rate = 44100;
        var data = NoisemakerSynth.Generate(kind, Rate);
        var clip = AudioClip.Create($"Noisemaker {kind} (temporary)", data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        clip.hideFlags = HideFlags.DontSave;
        cache[i] = clip;
        GeneratedCount++;
        return clip;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        for (int i = 0; i < cache.Length; i++) cache[i] = null;
        GeneratedCount = 0;
    }
}
