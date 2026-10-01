using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The local listener's LATE room reverb (owner only; added at runtime with the player's ambience). One Audio Reverb Zone travels with
/// the listener, so the room shape does not matter; a few times a second it looks up which acoustic zone the listener is in and blends
/// the reverb towards that space's profile (AudioBank) over the profile's transition time, so doorways are smooth. Sounds in the Ambience
/// and Internal categories bypass it (they stay dry), your own footsteps get a lighter share (AudioRouting). The close-range early
/// reflections are a separate, per-emitter layer (EmitterAcoustics); the persistent room tone is ShipRoomTone.
/// Also publishes, for the occlusion of world sounds, an upper cutoff for the current space (the crawlspace is boxy and muffled) and an
/// ambience level. No network traffic: every player instance does this for its own listener.
/// F4 toggles the audio diagnostics (development aid; off by default).
/// </summary>
public class ShipAcoustics : MonoBehaviour
{
    /// <summary>Upper limit for the low-pass on world sounds in the current space (Hz).</summary>
    public static float WorldCutoffCap { get; private set; } = 22000f;
    /// <summary>Multiplier on the background ambience in the current space.</summary>
    public static float AmbientLevel { get; private set; } = 1f;
    public static AcousticSpace CurrentSpace { get; private set; } = AcousticSpace.Neutral;
    /// <summary>Linear level of the current late reverb (from the zone's room setting), for diagnostics.</summary>
    public static float LateLevel { get; private set; }
    /// <summary>F4: show the audio and tracker diagnostics.</summary>
    public static bool DiagnosticsVisible { get; set; }
    /// <summary>Editor preview only (Alien > Audio > Acoustic Preview): forces the listener's space.</summary>
    public static bool PreviewActive { get; set; }
    public static AcousticSpace PreviewSpace { get; set; }

    const int Count = 13; // room, roomHF, roomLF, decay, decayHF, reflections, reflDelay, reverb, revDelay, diffusion, density, cutoffCap, ambient
    readonly float[] current = new float[Count], target = new float[Count];
    readonly List<bool> contains = new();
    readonly List<int> priorities = new();
    readonly StringBuilder diag = new();
    AudioReverbZone zone;
    AudioBank bank;
    GUIStyle diagStyle;
    float timer, transition = 0.8f, diagTimer;
    string diagText = "";

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
        ResetStatics();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        WorldCutoffCap = 22000f;
        AmbientLevel = 1f;
        CurrentSpace = AcousticSpace.Neutral;
        LateLevel = 0f;
        PreviewActive = false;
    }

    void Update()
    {
        if (zone == null) return;
        var kb = Keyboard.current;
        if (kb != null && kb.f4Key.wasPressedThisFrame) DiagnosticsVisible = !DiagnosticsVisible;
        float dt = Time.deltaTime;
        if ((timer -= dt) <= 0f)
        {
            timer = 0.2f;
            var space = PreviewActive ? PreviewSpace : SpaceAt(zone.transform.position);
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
        ApplyTo(zone, current);
        WorldCutoffCap = Mathf.Clamp(current[11], 500f, 22000f);
        AmbientLevel = Mathf.Max(0f, current[12]);
        LateLevel = Mathf.Pow(10f, current[0] / 2000f);
    }

    static void ApplyTo(AudioReverbZone z, float[] v)
    {
        z.room = Mathf.RoundToInt(v[0]);
        z.roomHF = Mathf.RoundToInt(v[1]);
        z.roomLF = Mathf.RoundToInt(v[2]);
        z.decayTime = Mathf.Max(0.1f, v[3]);
        z.decayHFRatio = Mathf.Clamp(v[4], 0.1f, 2f);
        z.reflections = Mathf.RoundToInt(v[5]);
        z.reflectionsDelay = Mathf.Clamp(v[6], 0f, 0.3f);
        z.reverb = Mathf.RoundToInt(v[7]);
        z.reverbDelay = Mathf.Clamp(v[8], 0f, 0.1f);
        z.diffusion = Mathf.Clamp(v[9], 0f, 100f);
        z.density = Mathf.Clamp(v[10], 0f, 100f);
    }

    /// <summary>Sets a reverb zone straight to a profile (the editor preview uses this for a listener without a player).</summary>
    public static void ApplyProfile(AudioReverbZone z, AudioBank.AcousticProfile p)
    {
        var v = new float[Count];
        Fill(v, p);
        z.reverbPreset = AudioReverbPreset.User;
        ApplyTo(z, v);
    }

    // ---------- Diagnostics (F4; built at most twice a second, only while visible) ----------

    void OnGUI()
    {
        if (!DiagnosticsVisible || zone == null) return;
        if ((diagTimer -= Time.unscaledDeltaTime) <= 0f)
        {
            diagTimer = 0.5f;
            int emitters = EmitterAcoustics.All.Count, voices = 0, reflecting = 0;
            foreach (var e in EmitterAcoustics.All)
            {
                voices += e.PlayingVoices();
                if (e.HasReflectionSlot) reflecting++;
            }
            diag.Clear();
            diag.Append("AUDIO (F4)  space ").Append(CurrentSpace).Append(PreviewActive ? " [preview]" : "")
                .Append("  late tail ").Append(LateLevel.ToString("0.00"))
                .Append("  world cutoff cap ").Append(WorldCutoffCap.ToString("0")).Append(" Hz\n")
                .Append("emitters ").Append(emitters).Append(", playing voices ").Append(voices)
                .Append(", reflection slots ").Append(EmitterAcoustics.ReflectionSlotsInUse).Append('/').Append(EmitterAcoustics.ReflectionSlotsCapacity)
                .Append(" (").Append(reflecting).Append(" emitters)\n");
            var tone = GetComponent<ShipRoomTone>();
            if (tone != null) tone.Describe(diag);
            diagText = diag.ToString();
        }
        diagStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(0.75f, 0.95f, 1f) } };
        GUI.Label(new Rect(20, 120, 900, 90), diagText, diagStyle);
    }
}
