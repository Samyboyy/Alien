using UnityEngine;

/// <summary>
/// All game audio clips and their volumes in one asset at Assets/Resources/AudioBank.asset, created and filled by
/// Alien > Setup Audio (clips come from Assets/Sounds by file name). Runtime code loads it with Get(); when it is missing the
/// audio components simply do not start. Re-running the setup refreshes the clips and leaves the tuning values alone.
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

    [Header("Volumes (0..1)")]
    [Range(0f, 1f)] public float atmosVolume = 0.1f;
    [Range(0f, 1f)] public float riserVolume = 0.5f;
    [Range(0f, 1f)] public float ownFootstepVolume = 0.5f;
    [Range(0f, 1f)] public float otherFootstepVolume = 0.9f;
    [Range(0f, 1f)] public float monsterFootstepVolume = 1f;
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
    [Tooltip("Monster footsteps are at full volume inside this distance (m)...")] public float monsterStepFullDistance = 6f;
    [Tooltip("...and fade out to nothing by this distance (m), in a straight line, so they carry much further")] public float monsterStepMaxDistance = 34f;
    [Tooltip("0 = flat, 1 = fully positioned in 3D. Mostly 3D, a little flat so it is never lost.")] [Range(0f, 1f)] public float monsterStepSpatial = 0.85f;

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

/// <summary>A few AudioSources on one object, used round-robin, so each one-shot gets its own pitch and volume.</summary>
public sealed class SfxPool
{
    readonly AudioSource[] sources;
    int next;

    public SfxPool(GameObject host, int size, float spatialBlend, float maxDistance,
        AudioRolloffMode rolloff = AudioRolloffMode.Logarithmic, float minDistance = 1.5f)
    {
        sources = new AudioSource[size];
        for (int i = 0; i < size; i++)
        {
            var s = host.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = spatialBlend;
            s.rolloffMode = rolloff;
            s.minDistance = minDistance;
            s.maxDistance = maxDistance;
            s.dopplerLevel = 0f;
            sources[i] = s;
        }
    }

    public void Play(AudioClip clip, float volume, float pitch)
    {
        if (clip == null) return;
        var s = sources[next];
        next = (next + 1) % sources.Length;
        s.clip = clip;
        s.volume = Mathf.Clamp01(volume);
        s.pitch = pitch;
        s.Play();
    }

    public bool IsPlaying
    {
        get { foreach (var s in sources) if (s.isPlaying) return true; return false; }
    }
}
