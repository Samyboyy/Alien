using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The local listener's room acoustics (owner only; added at runtime with the player's ambience). One Audio Reverb Zone travels with
/// the listener, so the room shape does not matter; a few times a second it looks up which acoustic zone the listener is in and
/// blends the reverb towards that space's profile (AudioBank) over the profile's transition time, so doorways are smooth. Sounds in
/// the Ambience and Internal categories bypass it (they stay dry), your own footsteps get a lighter share (AudioRouting).
/// Also publishes, for the occlusion of world sounds, an upper cutoff for the current space (the crawlspace is boxy and muffled) and an
/// ambience level. No network traffic: every player instance does this for its own listener.
/// </summary>
public class ShipAcoustics : MonoBehaviour
{
    /// <summary>Upper limit for the low-pass on world sounds in the current space (Hz).</summary>
    public static float WorldCutoffCap { get; private set; } = 22000f;
    /// <summary>Multiplier on the background ambience in the current space.</summary>
    public static float AmbientLevel { get; private set; } = 1f;
    public static AcousticSpace CurrentSpace { get; private set; } = AcousticSpace.Neutral;

    const int Count = 13; // room, roomHF, roomLF, decay, decayHF, reflections, reflDelay, reverb, revDelay, diffusion, density, cutoffCap, ambient
    readonly float[] current = new float[Count], target = new float[Count];
    readonly List<bool> contains = new();
    readonly List<int> priorities = new();
    AudioReverbZone zone;
    AudioBank bank;
    float timer, transition = 0.8f;

    public void Begin()
    {
        bank = AudioBank.Get();
        if (bank == null) { enabled = false; return; }
        var listener = GetComponentInChildren<AudioListener>(true);
        var go = new GameObject("Ship Reverb");
        go.transform.SetParent(listener != null ? listener.transform : transform, false);
        zone = go.AddComponent<AudioReverbZone>();
        zone.minDistance = 500f; // the listener is always deep inside: the zone's shape never matters
        zone.maxDistance = 600f;
        zone.reverbPreset = AudioReverbPreset.User;
        Fill(current, bank.Profile(AcousticSpace.Neutral));
        Fill(target, bank.Profile(AcousticSpace.Neutral));
        Apply();
    }

    void OnDestroy()
    {
        if (zone != null) Destroy(zone.gameObject);
        WorldCutoffCap = 22000f;
        AmbientLevel = 1f;
        CurrentSpace = AcousticSpace.Neutral;
    }

    void Update()
    {
        if (zone == null) return;
        float dt = Time.deltaTime;
        if ((timer -= dt) <= 0f)
        {
            timer = 0.2f;
            var space = SpaceAt(zone.transform.position);
            var profile = bank.Profile(space);
            CurrentSpace = space;
            Fill(target, profile);
            transition = profile != null ? profile.transitionSeconds : 0.8f;
        }
        for (int i = 0; i < Count; i++) current[i] = AcousticRules.Approach(current[i], target[i], dt, transition);
        Apply();
    }

    AcousticSpace SpaceAt(Vector3 p)
    {
        var zones = ShipAcousticZone.All;
        contains.Clear();
        priorities.Clear();
        for (int i = 0; i < zones.Count; i++)
        {
            contains.Add(zones[i].Contains(p));
            priorities.Add(zones[i].priority);
        }
        int best = AcousticRules.Select(contains, priorities, zones.Count);
        return best >= 0 ? zones[best].space : zones.Count > 0 ? bank.fallbackSpace : AcousticSpace.Neutral;
    }

    static void Fill(float[] v, AudioBank.AcousticProfile p)
    {
        p ??= AudioBank.AcousticProfile.Off();
        v[0] = p.room; v[1] = p.roomHF; v[2] = p.roomLF; v[3] = p.decayTime; v[4] = p.decayHFRatio;
        v[5] = p.reflections; v[6] = p.reflectionsDelay; v[7] = p.reverb; v[8] = p.reverbDelay; v[9] = p.diffusion; v[10] = p.density;
        v[11] = p.worldCutoffCap; v[12] = p.ambientLevel;
    }

    void Apply()
    {
        zone.room = Mathf.RoundToInt(current[0]);
        zone.roomHF = Mathf.RoundToInt(current[1]);
        zone.roomLF = Mathf.RoundToInt(current[2]);
        zone.decayTime = Mathf.Max(0.1f, current[3]);
        zone.decayHFRatio = Mathf.Clamp(current[4], 0.1f, 2f);
        zone.reflections = Mathf.RoundToInt(current[5]);
        zone.reflectionsDelay = Mathf.Clamp(current[6], 0f, 0.3f);
        zone.reverb = Mathf.RoundToInt(current[7]);
        zone.reverbDelay = Mathf.Clamp(current[8], 0f, 0.1f);
        zone.diffusion = Mathf.Clamp(current[9], 0f, 100f);
        zone.density = Mathf.Clamp(current[10], 0f, 100f);
        WorldCutoffCap = Mathf.Clamp(current[11], 500f, 22000f);
        AmbientLevel = Mathf.Max(0f, current[12]);
    }
}
