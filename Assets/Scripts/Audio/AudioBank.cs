using UnityEngine;

/// <summary>
/// All game audio clips and their volumes in one asset at Assets/Resources/AudioBank.asset, created and filled by
/// Alien > Setup Audio (clips come from Assets/Sounds by file name). Runtime code loads it with Get(); when it is missing the
/// audio components simply do not start. Re-running the setup refreshes the clips and leaves the tuning values alone; one-off tuning
/// changes are applied by versioned migrations (Setup Audio, or Alien > Apply Creature Pressure And Audio Tuning) and logged.
/// </summary>
[CreateAssetMenu(menuName = "Alien/Audio Bank")]
public class AudioBank : ScriptableObject
{
    [Header("Clips (filled by Alien > Setup Audio)")]
    public AudioClip footstepQuiet;
    public AudioClip footstepRegular1;
    public AudioClip footstepRegular2;
    public AudioClip[] monsterFootsteps = new AudioClip[0];
    public AudioClip snarl;
    public AudioClip tensionRiser;
    public AudioClip atmos;
    public AudioClip heartbeat;
    public AudioClip scaredBreath;
    public AudioClip runBreath;
    public AudioClip doorSound;
    [Tooltip("Seconds into the heartbeat clip where one beat (lub-dub) starts. Measured by Setup Audio.")] public float heartbeatBeatStart = 1.4f;
    [Tooltip("Seconds into the riser where it peaks. Measured by Setup Audio.")] public float riserPeakSeconds = 4.4f;
    [Tooltip("Loudness of the riser clip over time (0..1, perceptual). Measured by Setup Audio.")] public float[] riserEnvelope = new float[0];
    [Tooltip("Seconds per entry of riserEnvelope")] public float riserEnvelopeStep = 0.1f;

    [Header("Routing (no AudioMixer: these are the group volumes, see AudioRouting)")]
    [Range(0f, 1f)] public float masterVolume = 1f;
    [Range(0f, 1f)] public float worldVolume = 1f;
    [Range(0f, 1f)] public float creatureVolume = 1f;
    [Range(0f, 1f)] public float playersVolume = 1f;
    [Range(0f, 1f)] public float ambienceVolume = 1f;
    [Tooltip("Heartbeat, tension riser, your own breathing: dry, never in the room reverb")] [Range(0f, 1f)] public float internalVolume = 1f;
    [Range(0f, 1f)] public float uiVolume = 1f;
    [Tooltip("Share of the room reverb on your OWN footsteps (others' footsteps and world sounds get all of it)")] [Range(0f, 1.1f)] public float ownBodyReverbMix = 0.5f;

    [Header("Volumes (0..1)")]
    [Range(0f, 1f)] public float atmosVolume = 0.1f;
    [Range(0f, 1f)] public float riserVolume = 0.5f;
    [Range(0f, 1f)] public float ownFootstepVolume = 0.5f;
    [Range(0f, 1f)] public float otherFootstepVolume = 0.9f;
    [Range(0f, 1f)] public float monsterFootstepVolume = 0.9f;
    [Range(0f, 1f)] public float snarlVolume = 0.85f;
    [Range(0f, 1f)] public float heartbeatVolume = 0.35f;
    [Range(0f, 1f)] public float scaredBreathVolume = 0.15f;
    [Range(0f, 1f)] public float runBreathVolume = 0.18f;
    [Range(0f, 1f)] public float doorVolume = 0.8f;

    [Header("Footsteps")]
    [Tooltip("Chance of the second regular footstep instead of the first")] [Range(0f, 1f)] public float regular2Chance = 0.3f;
    [Tooltip("Random pitch spread either side of 1, to hide repetition")] [Range(0f, 0.2f)] public float pitchSpread = 0.05f;
    [Tooltip("Metres the monster covers per footstep at walking pace")] public float monsterStride = 1.5f;
    [Tooltip("Hearing range of other players' footsteps (m)")] public float footstepMaxDistance = 22f;
    [Tooltip("Other players' footsteps are at full volume inside this distance (m)")] public float footstepFullDistance = 2f;
    [Tooltip("Monster footsteps are at full volume inside this distance (m)...")] public float monsterStepFullDistance = 3.5f;
    [Tooltip("...then fall off perceptually (inverse distance, eased to silence) and are silent by this distance (m)")] public float monsterStepMaxDistance = 22f;
    [Tooltip("0 = flat, 1 = fully positioned in 3D. Keep at 1: any flat share never fades with distance.")] [Range(0f, 1f)] public float monsterStepSpatial = 1f;

    [Header("Occlusion (walls and closed doors between a world sound and you)")]
    [Tooltip("Checks per second for each sounding emitter")] public float occlusionHz = 8f;
    [Tooltip("Share of the remaining clarity each wall or closed door takes away (0..0.95)")] [Range(0f, 0.95f)] public float occlusionPerObstacle = 0.7f;
    [Tooltip("Low-pass cutoff when fully occluded (Hz)")] public float occludedCutoff = 700f;
    [Tooltip("Volume multiplier when fully occluded; never silent")] [Range(0.05f, 1f)] public float occludedGain = 0.4f;
    [Tooltip("Seconds to settle into a new occlusion value")] public float occlusionSmoothSeconds = 0.25f;

    [Header("Tension riser (follows how near the creature is)")]
    [Tooltip("At or inside this distance the riser is at its peak (m)")] public float riserNear = 3f;
    [Tooltip("Beyond this distance the riser is silent (m)")] public float riserFar = 22f;
    [Tooltip("Share of the proximity that still counts with a wall in between")] [Range(0f, 1f)] public float riserWallFactor = 0.6f;
    [Tooltip("Proximity units per second the riser builds")] public float riserRiseRate = 0.5f;
    [Tooltip("Proximity units per second it unwinds")] public float riserFallRate = 0.25f;
    [Tooltip("The riser plays its own sound forward and cuts between places in it; it stays within a window this long around the wanted place (s)")] public float riserWindow = 8f;
    [Tooltip("How far the playhead may fall behind or ahead of the window before it cuts (s)")] public float riserTolerance = 3f;
    [Tooltip("Length of the blend between two places in the riser (s)")] public float riserCrossfade = 1.5f;

    [Header("Doors")]
    [Tooltip("Full volume inside this distance (m)...")] public float doorFullDistance = 4f;
    [Tooltip("...fading to nothing by this distance (m)")] public float doorMaxDistance = 28f;

    [Header("Vent presentation (cosmetic; the creature never hears these)")]
    [Tooltip("Grille / scrape as it goes in. Empty: a short piece of the door sound, pitched down, stands in.")] public AudioClip ventEnter;
    [Tooltip("Restrained duct rattle that follows the creature. Empty: silent (no suitable clip yet).")] public AudioClip ventTravel;
    [Tooltip("The pre-emergence warning at the EXIT. Empty: a short piece of the door sound, pitched well down, stands in.")] public AudioClip ventWarning;
    [Tooltip("Grille as it comes out. Empty: a short piece of the door sound stands in.")] public AudioClip ventExit;
    [Range(0f, 1f)] public float ventEnterVolume = 0.8f;
    [Range(0f, 1f)] public float ventTravelVolume = 0.35f;
    [Range(0f, 1f)] public float ventWarningVolume = 1f;
    [Range(0f, 1f)] public float ventExitVolume = 0.9f;
    [Tooltip("Seconds of the door sound used by the fallbacks")] public float ventFallbackLength = 1.1f;
    public float ventEnterFallbackPitch = 0.7f;
    public float ventWarningFallbackPitch = 0.55f;
    public float ventExitFallbackPitch = 0.8f;
    [Tooltip("Vent sounds are at full volume inside this distance (m)...")] public float ventSoundFullDistance = 3f;
    [Tooltip("...and silent by this distance (m)")] public float ventSoundMaxDistance = 26f;

    [Header("Ship acoustics (the listener's space; see ShipAcoustics)")]
    public AcousticProfile[] acousticProfiles = AcousticProfile.Defaults();
    [Tooltip("Used on the ship outside every acoustic zone (the corridors have zones too; this covers the odd doorway cell)")] public AcousticSpace fallbackSpace = AcousticSpace.Corridor;

    [Header("Bookkeeping")]
    [Tooltip("Setup Audio applies one-off tuning changes for each new version, once, and logs them")] public int tuningVersion;

    [Header("When the tension sounds play")]
    [Tooltip("Only while the creature is in the same room as you or can see you. It keeps counting as engaged this long after that stops, so a creature that steps out of view in your room does not cut the music.")] public float engageLinger = 3f;

    [Header("Heartbeat (subtle, faster the nearer the creature is)")]
    [Tooltip("Full rate at or inside this distance (m)")] public float heartbeatNear = 3f;
    [Tooltip("No heartbeat beyond this distance (m)")] public float heartbeatFar = 11f;
    [Tooltip("Nearness (0..1) below which there is no heartbeat")] [Range(0f, 0.5f)] public float heartbeatThreshold = 0.05f;
    [Tooltip("Seconds between beats when the creature is just in range")] public float heartbeatSlowInterval = 1.4f;
    [Tooltip("Seconds between beats when it is on top of you")] public float heartbeatFastInterval = 0.55f;
    [Tooltip("Length of one beat cut from the clip (s)")] public float heartbeatBeatLength = 0.95f;

    [Header("Breathing (only ONE voice ever plays: out of breath first, then scared)")]
    [Tooltip("Scared breathing starts at this nearness (0..1 of the heartbeat range), or whenever it is chasing you")] [Range(0f, 1f)] public float scaredOn = 0.5f;
    [Tooltip("...and ends below this")] [Range(0f, 1f)] public float scaredOff = 0.3f;
    [Tooltip("Out-of-breath sound starts when stamina (0..1) falls below this, or when exhausted")] [Range(0f, 1f)] public float runBreathOnStamina = 0.2f;
    [Tooltip("...and ends once stamina has recovered to this")] [Range(0f, 1f)] public float runBreathOffStamina = 0.5f;
    [Tooltip("After an out-of-breath spell ends, the next one cannot start for this long (exhaustion overrides it)")] public float runBreathCooldown = 25f;
    public float breathFadeIn = 0.9f;
    [Tooltip("A change of breathing voice fades the old one out completely, then starts the new one")] public float breathFadeOut = 0.4f;

    [Header("Snarl (used sparingly, always the original recording)")]
    [Tooltip("0 = flat, 1 = fully 3D. Low keeps it close to the recording as made.")] [Range(0f, 1f)] public float snarlSpatial = 0.3f;
    [Tooltip("While hunting, snarl only inside this distance (m)")] public float snarlHuntDistance = 7f;
    [Tooltip("In any state, snarl only inside this distance (m)")] public float snarlCloseDistance = 3f;
    public float snarlMinCooldown = 25f;
    public float snarlMaxCooldown = 50f;
    [Tooltip("Quiet time after a round starts before the first snarl")] public float snarlFirstDelay = 12f;

    /// <summary>
    /// Reverb and environment for one kind of space. Units are the Audio Reverb Zone's (millibels, seconds). Kept restrained on purpose:
    /// short metallic reflections in corridors, a longer industrial tail in the big machinery rooms, tight small rooms, a boxy crawlspace.
    /// </summary>
    [System.Serializable]
    public class AcousticProfile
    {
        public AcousticSpace space;
        [Range(-10000, 0)] public int room = -1000;
        [Range(-10000, 0)] public int roomHF = -500;
        [Range(-10000, 0)] public int roomLF;
        [Range(0.1f, 20f)] public float decayTime = 1f;
        [Range(0.1f, 2f)] public float decayHFRatio = 0.8f;
        [Range(-10000, 1000)] public int reflections = -1500;
        [Range(0f, 0.3f)] public float reflectionsDelay = 0.005f;
        [Range(-10000, 2000)] public int reverb = -500;
        [Range(0f, 0.1f)] public float reverbDelay = 0.01f;
        [Range(0f, 100f)] public float diffusion = 100f;
        [Range(0f, 100f)] public float density = 100f;
        [Tooltip("Upper limit of the low-pass on world sounds while the listener is here (Hz)")] public float worldCutoffCap = 22000f;
        [Tooltip("Multiplier on the background ambience while the listener is here")] public float ambientLevel = 1f;
        [Tooltip("Seconds to blend into this space")] public float transitionSeconds = 0.8f;

        public static AcousticProfile Off() => new() { space = AcousticSpace.Neutral, room = -10000, roomHF = -10000, reflections = -10000, reverb = -10000, decayTime = 0.5f };

        public static AcousticProfile[] Defaults() => new[]
        {
            Off(),
            new AcousticProfile { space = AcousticSpace.SmallRoom, room = -1200, roomHF = -600, decayTime = 0.55f, decayHFRatio = 0.75f, reflections = -1400, reflectionsDelay = 0.004f, reverb = -250, reverbDelay = 0.006f, ambientLevel = 0.85f, transitionSeconds = 0.7f },
            new AcousticProfile { space = AcousticSpace.LargeMachinery, room = -1100, roomHF = -500, decayTime = 1.4f, decayHFRatio = 0.7f, reflections = -1600, reflectionsDelay = 0.012f, reverb = -150, reverbDelay = 0.02f, ambientLevel = 1.3f, transitionSeconds = 1f },
            new AcousticProfile { space = AcousticSpace.Corridor, room = -1000, roomHF = -400, decayTime = 0.85f, decayHFRatio = 0.65f, reflections = -1000, reflectionsDelay = 0.006f, reverb = -350, reverbDelay = 0.01f, diffusion = 60f, ambientLevel = 1f, transitionSeconds = 0.6f },
            new AcousticProfile { space = AcousticSpace.Compartment, room = -1300, roomHF = -700, decayTime = 0.45f, decayHFRatio = 0.8f, reflections = -1500, reflectionsDelay = 0.003f, reverb = -450, reverbDelay = 0.004f, ambientLevel = 0.75f, transitionSeconds = 0.5f },
            new AcousticProfile { space = AcousticSpace.Crawlspace, room = -900, roomHF = -1200, decayTime = 0.3f, decayHFRatio = 0.5f, reflections = -600, reflectionsDelay = 0.001f, reverb = -100, reverbDelay = 0.002f, worldCutoffCap = 3500f, ambientLevel = 0.6f, transitionSeconds = 0.4f },
        };
    }

    /// <summary>The profile for a space, or null.</summary>
    public AcousticProfile Profile(AcousticSpace space)
    {
        if (acousticProfiles == null) return null;
        foreach (var p in acousticProfiles) if (p != null && p.space == space) return p;
        return null;
    }

    static AudioBank cached;

    /// <summary>The bank, or null (logged once) when Setup Audio has not been run.</summary>
    public static AudioBank Get()
    {
        if (cached != null) return cached;
        cached = Resources.Load<AudioBank>("AudioBank");
        if (cached == null && !warned) { warned = true; Debug.LogWarning("No Assets/Resources/AudioBank.asset: run Alien > Setup Audio. Audio is off."); }
        return cached;
    }

    static bool warned;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        cached = null;
        warned = false;
    }
}

/// <summary>
/// A few AudioSources on one object, used round-robin, so each one-shot gets its own pitch and volume. The category sets the volume
/// group and reverb send (AudioRouting); an occlusion gain (AudioOcclusion) multiplies the volume of whatever is playing.
/// </summary>
public sealed class SfxPool
{
    readonly AudioSource[] sources;
    readonly float[] baseVolume;
    readonly AudioCategory category;
    int next;
    float gain = 1f;

    public SfxPool(GameObject host, int size, AudioCategory category, float spatialBlend, float maxDistance,
        AudioRolloffMode rolloff = AudioRolloffMode.Logarithmic, float minDistance = 1.5f, AnimationCurve customRolloff = null)
    {
        this.category = category;
        sources = new AudioSource[size];
        baseVolume = new float[size];
        for (int i = 0; i < size; i++)
        {
            var s = host.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = spatialBlend;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            if (customRolloff != null)
            {
                s.rolloffMode = AudioRolloffMode.Custom;
                s.SetCustomCurve(AudioSourceCurveType.CustomRolloff, customRolloff);
            }
            else s.rolloffMode = rolloff;
            AudioRouting.Configure(s, category);
            sources[i] = s;
        }
    }

    public void Play(AudioClip clip, float volume, float pitch)
    {
        if (clip == null) return;
        int i = next;
        next = (next + 1) % sources.Length;
        var s = sources[i];
        baseVolume[i] = Mathf.Clamp01(volume * AudioRouting.Volume(category));
        s.clip = clip;
        s.volume = baseVolume[i] * gain;
        s.pitch = pitch;
        s.Play();
    }

    /// <summary>Occlusion gain for whatever is playing now and next.</summary>
    public void SetGain(float g)
    {
        gain = g;
        for (int i = 0; i < sources.Length; i++) if (sources[i].isPlaying) sources[i].volume = baseVolume[i] * g;
    }

    public void StopAll()
    {
        foreach (var s in sources) if (s != null) s.Stop();
    }

    public bool IsPlaying
    {
        get { foreach (var s in sources) if (s != null && s.isPlaying) return true; return false; }
    }
}
