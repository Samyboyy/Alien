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

    [Header("Early reflections (per emitter, close range; see EmitterAcoustics)")]
    [Tooltip("Master switch for the close-range metallic reflections on world sounds")] public bool reflectionsEnabled = true;
    [Tooltip("Most emitters that may use reflections at the same time; others play dry")] public int maxReflectionEmitters = 12;
    [Tooltip("Full reflections inside this distance (m)...")] public float reflectionNear = 4f;
    [Tooltip("...easing to reflectionFarWeight by this distance (m); the late room reverb carries distant sounds")] public float reflectionFar = 20f;
    [Range(0f, 1f)] public float reflectionFarWeight = 0.35f;
    [Tooltip("Share of the reflections on your own footsteps and tracker")] [Range(0f, 1f)] public float ownBodyReflectionWeight = 0.35f;
    [Tooltip("Seconds to blend the reflections when an emitter moves between spaces")] public float reflectionTransitionSeconds = 0.4f;
    [Tooltip("Seconds an emitter keeps its reflection slot after its sound ends (the reflections' tail)")] public float reflectionTailSeconds = 0.6f;
    [Tooltip("Ceiling on the overall reflection level (mB): keeps every profile restrained")] public int reflectionMaxRoom = -300;
    [Tooltip("Ceiling on the early reflection level (mB)")] public int reflectionMaxEarly = 600;
    [Tooltip("Ceiling on an emitter's own late part (mB); the listener's zone gives the real late tail")] public int reflectionMaxOwnTail = -1000;
    [Tooltip("Above this height an emitter is in the overhead ducts (Duct profile)")] public float ductHeight = 4.2f;

    [Header("Room tone (layered beds; see ShipRoomTone)")]
    [Tooltip("Steady air-handling layer for corridors and habitation (ATMOS_1). The global ship bed is 'atmos' above.")] public AudioClip airTone;
    [Tooltip("Optional machinery layer for the engine rooms. Empty: the bed's level per space carries the machinery rooms.")] public AudioClip machineryTone;
    [Tooltip("Optional layer for the crawlspace and ducts")] public AudioClip ductTone;
    [Range(0f, 1f)] public float airToneVolume = 0.12f;
    [Range(0f, 1f)] public float machineryToneVolume = 0.25f;
    [Range(0f, 1f)] public float ductToneVolume = 0.2f;
    [Tooltip("Seamless loop region of the bed (s): x start, y end; 0,0 = whole clip. Measured by Apply Acoustic Polish (ATMOS_2 fades out at its end).")] public Vector2 atmosLoop;
    public Vector2 airToneLoop;
    public Vector2 machineryToneLoop;
    public Vector2 ductToneLoop;
    [Tooltip("Crossfade at each loop seam (s)")] public float roomToneLoopCrossfade = 2.5f;
    [Tooltip("Seconds to blend the layers when you move between spaces")] public float roomToneTransition = 2.5f;
    [Tooltip("Room tone level while spectating after death")] [Range(0f, 1f)] public float spectatorRoomTone = 0.7f;

    [Header("Flickering lights")]
    [Tooltip("Electrical crackle for a failing light (FX_LIGHTFLICKER_01: a long, quiet electrical bed; short pieces of it are used)")] public AudioClip flickerCrackle;
    [Range(0f, 1f)] public float flickerVolume = 0.5f;
    [Tooltip("A light makes this sound at most once per this many seconds, at the start of a flicker burst")] public float flickerCooldown = 8f;
    [Tooltip("Length of each crackle piece (s)")] public float flickerPieceLength = 0.6f;
    public float flickerMaxDistance = 12f;

    [Header("Creature voice (cosmetic; the creature never hears itself)")]
    [Tooltip("Distant roaming/hunting call (MONSTER_WHISTLE_FAR_01: a long recording of separate calls; roamCallSegments picks them out)")] public AudioClip roamCall;
    [Tooltip("Start and length (s) of each call inside roamCall. Measured by Apply Acoustic Polish.")] public Vector2[] roamCallSegments = new Vector2[0];
    [Tooltip("Short aggressive sound on a chase starting (MONSTER_SNARL_1_FASTER). Empty: the snarl is used.")] public AudioClip chaseSnarl;
    [Range(0f, 1f)] public float roamCallVolume = 0.8f;
    [Range(0f, 1f)] public float chaseSnarlVolume = 0.9f;
    [Range(0f, 1f)] public float searchSnarlVolume = 0.55f;
    [Tooltip("The roaming call carries far (m)")] public float roamCallMaxDistance = 45f;
    public float roamCallFullDistance = 6f;

    [Header("Motion tracker")]
    [Tooltip("The tracker beep. Empty: a short generated beep stands in (temporary).")] public AudioClip trackerBeep;
    [Range(0f, 1f)] public float trackerOwnerVolume = 0.45f;
    [Range(0f, 1f)] public float trackerRemoteVolume = 0.6f;
    public float trackerRemoteFullDistance = 2f;
    public float trackerRemoteMaxDistance = 18f;

    [Header("Throwable noisemaker (all optional; see ThrownNoisemaker)")]
    [Tooltip("Picking one up. Empty: silent.")] public AudioClip noisemakerPickup;
    [Tooltip("Throwing it (the handling). Empty: silent.")] public AudioClip noisemakerThrow;
    [Tooltip("Hitting something. Empty: a short piece of a metal footstep, pitched down, stands in (temporary).")] public AudioClip noisemakerImpact;
    [Tooltip("One electronic pulse. Empty: a short generated chirp stands in (temporary).")] public AudioClip noisemakerPulse;
    [Tooltip("Shutting down. Empty: the generated chirp, lower and softer.")] public AudioClip noisemakerSpent;
    [Range(0f, 1f)] public float noisemakerPickupVolume = 0.6f;
    [Range(0f, 1f)] public float noisemakerThrowVolume = 0.5f;
    [Range(0f, 1f)] public float noisemakerImpactVolume = 0.8f;
    [Range(0f, 1f)] public float noisemakerPulseVolume = 0.7f;
    [Range(0f, 1f)] public float noisemakerSpentVolume = 0.5f;
    [Tooltip("Pulses are at full volume inside this distance (m)...")] public float noisemakerFullDistance = 3f;
    [Tooltip("...and silent by this distance (m). Audible playback only: how far the CREATURE hears a pulse is the device's pulseRange. Was 30; 40 lets the pulse carry a little better at long range and changes nothing close up.")] public float noisemakerMaxDistance = 40f;

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

        [Header("Early reflections and room tone (filled by Alien > Apply Acoustic Polish; untick to use the built-in defaults)")]
        public bool polishConfigured;
        public int reflRoom = -1000;
        public int reflRoomHF = -500;
        public float reflDecayTime = 0.35f;
        public float reflDecayHFRatio = 0.6f;
        public int reflEarly = -300;
        public float reflEarlyDelay = 0.005f;
        public int reflTail = -2600;
        public float reflTailDelay = 0.006f;
        public float reflDiffusion = 80f;
        public float reflDensity = 85f;
        [Range(0f, 1f)] public float airToneLevel = 0.5f;
        [Range(0f, 1f)] public float machineryToneLevel;
        [Range(0f, 1f)] public float ductToneLevel;

        /// <summary>The early-reflection settings: the tuned values once configured, otherwise the built-in defaults for this space.</summary>
        public ReflectionSettings Reflection => polishConfigured
            ? new ReflectionSettings(reflRoom, reflRoomHF, reflDecayTime, reflDecayHFRatio, reflEarly, reflEarlyDelay, reflTail, reflTailDelay, reflDiffusion, reflDensity)
            : AcousticDefaults.Reflections(space);

        /// <summary>Room-tone layer levels (air, machinery, duct): tuned once configured, otherwise the built-in defaults.</summary>
        public (float air, float machinery, float duct) ToneLevels => polishConfigured ? (airToneLevel, machineryToneLevel, ductToneLevel) : AcousticDefaults.ToneLevels(space);

        /// <summary>Copies the built-in reflection and tone defaults for this space into the tunable fields and marks them configured.</summary>
        public void ApplyPolishDefaults()
        {
            var r = AcousticDefaults.Reflections(space);
            reflRoom = r.room; reflRoomHF = r.roomHF; reflDecayTime = r.decayTime; reflDecayHFRatio = r.decayHFRatio;
            reflEarly = r.reflections; reflEarlyDelay = r.reflectionsDelay; reflTail = r.reverb; reflTailDelay = r.reverbDelay;
            reflDiffusion = r.diffusion; reflDensity = r.density;
            (airToneLevel, machineryToneLevel, ductToneLevel) = AcousticDefaults.ToneLevels(space);
            polishConfigured = true;
        }

        public static AcousticProfile Off() => new() { space = AcousticSpace.Neutral, room = -10000, roomHF = -10000, reflections = -10000, reverb = -10000, decayTime = 0.5f };

        public static AcousticProfile[] Defaults()
        {
            var all = BaseDefaults();
            foreach (var p in all) p.ApplyPolishDefaults();
            return all;
        }

        /// <summary>The late-reverb part of a profile for a space that a bank created before LargeOpen and Duct existed is missing.</summary>
        public static AcousticProfile BaseDefault(AcousticSpace space)
        {
            foreach (var p in BaseDefaults()) if (p.space == space) return p;
            return null;
        }

        static AcousticProfile[] BaseDefaults() => new[]
        {
            Off(),
            new AcousticProfile { space = AcousticSpace.SmallRoom, room = -1200, roomHF = -600, decayTime = 0.55f, decayHFRatio = 0.75f, reflections = -1400, reflectionsDelay = 0.004f, reverb = -250, reverbDelay = 0.006f, ambientLevel = 0.85f, transitionSeconds = 0.7f },
            new AcousticProfile { space = AcousticSpace.LargeMachinery, room = -1100, roomHF = -500, decayTime = 1.4f, decayHFRatio = 0.7f, reflections = -1600, reflectionsDelay = 0.012f, reverb = -150, reverbDelay = 0.02f, ambientLevel = 1.3f, transitionSeconds = 1f },
            new AcousticProfile { space = AcousticSpace.Corridor, room = -1000, roomHF = -400, decayTime = 0.85f, decayHFRatio = 0.65f, reflections = -1000, reflectionsDelay = 0.006f, reverb = -350, reverbDelay = 0.01f, diffusion = 60f, ambientLevel = 1f, transitionSeconds = 0.6f },
            new AcousticProfile { space = AcousticSpace.Compartment, room = -1300, roomHF = -700, decayTime = 0.45f, decayHFRatio = 0.8f, reflections = -1500, reflectionsDelay = 0.003f, reverb = -450, reverbDelay = 0.004f, ambientLevel = 0.75f, transitionSeconds = 0.5f },
            new AcousticProfile { space = AcousticSpace.Crawlspace, room = -900, roomHF = -1200, decayTime = 0.3f, decayHFRatio = 0.5f, reflections = -600, reflectionsDelay = 0.001f, reverb = -100, reverbDelay = 0.002f, worldCutoffCap = 3500f, ambientLevel = 0.6f, transitionSeconds = 0.4f },
            // A big open hold: less immediate reflection, a more apparent late tail.
            new AcousticProfile { space = AcousticSpace.LargeOpen, room = -1000, roomHF = -550, decayTime = 1.9f, decayHFRatio = 0.65f, reflections = -2000, reflectionsDelay = 0.02f, reverb = -50, reverbDelay = 0.03f, ambientLevel = 1.15f, transitionSeconds = 1.1f },
            // The overhead ducts (only an emitter is ever there; the listener never is).
            new AcousticProfile { space = AcousticSpace.Duct, room = -900, roomHF = -900, decayTime = 0.35f, decayHFRatio = 0.55f, reflections = -700, reflectionsDelay = 0.002f, reverb = -200, reverbDelay = 0.003f, worldCutoffCap = 6000f, ambientLevel = 0.7f, transitionSeconds = 0.5f },
        };
    }

    static readonly System.Collections.Generic.Dictionary<AcousticSpace, AcousticProfile> builtIn = new();

    /// <summary>
    /// The profile for a space. A bank made before a space existed (LargeOpen, Duct) gets the built-in profile until
    /// Alien > Apply Acoustic Polish adds a tunable one.
    /// </summary>
    public AcousticProfile Profile(AcousticSpace space)
    {
        if (acousticProfiles != null)
            foreach (var p in acousticProfiles) if (p != null && p.space == space) return p;
        if (!builtIn.TryGetValue(space, out var d))
        {
            d = AcousticProfile.BaseDefault(space);
            d?.ApplyPolishDefaults();
            builtIn[space] = d;
        }
        return d;
    }

    /// <summary>True when the bank itself has a profile for the space (validation).</summary>
    public bool HasOwnProfile(AcousticSpace space)
    {
        if (acousticProfiles != null) foreach (var p in acousticProfiles) if (p != null && p.space == space) return true;
        return false;
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
/// group and reverb send (AudioRouting); the emitter's acoustics (EmitterAcoustics) add early reflections and an occlusion gain. A source's
/// volume is always composed once from its clip volume, its category volume and the current occlusion (AcousticRules.Compose), so nothing
/// is applied twice. Sources are created once; nothing is instantiated per sound.
/// </summary>
public sealed class SfxPool
{
    readonly AudioSource[] sources;
    const float PieceFadeIn = 0.03f;
    readonly float[] clipVolume, categoryVolume, fade;
    readonly double[] endTime, fadeInFrom;
    readonly AudioCategory category;
    int next;
    float gain = 1f;

    /// <summary>Set by EmitterAcoustics.Attach: told just before a sound starts, so its reflections are right from the first moment.</summary>
    public EmitterAcoustics Acoustics { get; set; }
    public AudioCategory Category => category;
    /// <summary>The source the most recent sound was started on (diagnostics: is it really playing, at what volume).</summary>
    public AudioSource LastSource { get; private set; }

    public SfxPool(GameObject host, int size, AudioCategory category, float spatialBlend, float maxDistance,
        AudioRolloffMode rolloff = AudioRolloffMode.Logarithmic, float minDistance = 1.5f, AnimationCurve customRolloff = null)
    {
        this.category = category;
        sources = new AudioSource[size];
        clipVolume = new float[size];
        categoryVolume = new float[size];
        fade = new float[size];
        endTime = new double[size];
        fadeInFrom = new double[size];
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

    public void Play(AudioClip clip, float volume, float pitch) => Start(clip, volume, pitch, 0f, 0f, 0f);

    /// <summary>
    /// Plays only a piece of a clip (a call inside a long recording, a crackle from a long bed): from <paramref name="start"/> for
    /// <paramref name="length"/> seconds of the clip, fading out over the last <paramref name="fadeSeconds"/> (and, when cut from the
    /// middle, fading in over the same time) so it never clicks.
    /// </summary>
    public void PlaySegment(AudioClip clip, float start, float length, float volume, float pitch, float fadeSeconds = 0.12f) =>
        Start(clip, volume, pitch, Mathf.Max(0f, start), Mathf.Max(0.05f, length), Mathf.Max(0.01f, fadeSeconds));

    void Start(AudioClip clip, float volume, float pitch, float start, float length, float fadeSeconds)
    {
        if (clip == null) return;
        Acoustics?.BeforePlay();
        int i = next;
        next = (next + 1) % sources.Length;
        var s = sources[i];
        clipVolume[i] = volume;
        categoryVolume[i] = AudioRouting.Volume(category);
        bool midClip = length > 0f && start > 0f;
        s.clip = clip;
        s.pitch = pitch;
        s.volume = midClip ? 0f : AcousticRules.Compose(clipVolume[i], categoryVolume[i], gain); // a piece from the middle fades in (Tick)
        s.time = Mathf.Min(start, Mathf.Max(0f, clip.length - 0.05f));
        s.Play();
        LastSource = s;
        float p = Mathf.Abs(pitch) < 0.01f ? 1f : Mathf.Abs(pitch);
        endTime[i] = length > 0f ? Time.timeAsDouble + length / p : 0;
        fadeInFrom[i] = midClip ? Time.timeAsDouble : double.NegativeInfinity;
        fade[i] = fadeSeconds;
    }

    /// <summary>Ends pieces on time with their fade, and keeps their occlusion gain current. Called every frame by the emitter's acoustics (cheap: a few sources).</summary>
    public void Tick()
    {
        double now = Time.timeAsDouble;
        for (int i = 0; i < sources.Length; i++)
        {
            if (endTime[i] <= 0) continue;
            var s = sources[i];
            double left = endTime[i] - now;
            if (left <= 0 || !s.isPlaying) { s.Stop(); endTime[i] = 0; continue; }
            float v = AcousticRules.Compose(clipVolume[i], categoryVolume[i], gain);
            double age = now - fadeInFrom[i];
            float fadeIn = Mathf.Max(PieceFadeIn, fade[i]);
            if (age < fadeIn) v *= (float)(age / fadeIn);
            if (left < fade[i]) v *= (float)(left / fade[i]);
            s.volume = v;
        }
    }

    /// <summary>Occlusion gain for whatever is playing now and next (always from the uncombined base). Pieces take it in Tick.</summary>
    public void SetGain(float g)
    {
        gain = g;
        for (int i = 0; i < sources.Length; i++)
            if (sources[i].isPlaying && endTime[i] <= 0) sources[i].volume = AcousticRules.Compose(clipVolume[i], categoryVolume[i], g);
    }

    public void StopAll()
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (sources[i] != null) sources[i].Stop();
            endTime[i] = 0;
        }
    }

    public bool IsPlaying
    {
        get { foreach (var s in sources) if (s != null && s.isPlaying) return true; return false; }
    }

    public int PlayingCount
    {
        get { int n = 0; foreach (var s in sources) if (s != null && s.isPlaying) n++; return n; }
    }
}
