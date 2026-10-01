using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Host-side LOGICAL footstep and breathing noise events for the creature (no audio is played). Derived from each
/// player's replicated movement and crouch state on the host, so a client cannot under-report sprinting and nothing extra
/// goes over the network. Footsteps are emitted per distance travelled (a stride), only while actually moving on the
/// ground, so standing still or pushing into a wall is silent. Crouch is quiet, not silent. Breathing is constant and
/// close-range, and gets louder for a few seconds after sprinting. Dead and escaped players emit nothing.
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class FootstepNoise : NetworkBehaviour, IRoundResettable
{
    [Header("Footstep range (m)")]
    public float sprintRange = 14f;
    public float walkRange = 7f;
    public float crouchRange = 2f;

    [Header("Breathing range (m)")]
    public float breathRange = 1f;
    public float heavyBreathRange = 3f;
    [Tooltip("Seconds for heavy breathing to fall back to normal after sprinting")] public float breathRecoverSeconds = 4f;
    public float breathInterval = 1f;

    [Header("Cadence")]
    [Tooltip("Metres travelled per footstep")] public float sprintStride = 2.2f;
    public float walkStride = 1.6f;
    public float crouchStride = 1.2f;
    [Tooltip("Horizontal m/s above which the player counts as moving at all")] public float moveSpeed = 0.6f;
    [Tooltip("Vertical m/s above which the player counts as airborne (no footsteps)")] public float maxGroundedVerticalSpeed = 3f;
    [Tooltip("Speed is averaged over this window")] public float sampleInterval = 0.2f;

    [Header("Hidden in a locker (m)")]
    [Tooltip("Breathing range while hidden: extremely quiet")] public float hiddenBreathRange = 0.5f;
    [Tooltip("...a little more when the creature is close (fear)")] public float hiddenFearBreathRange = 1.2f;
    [Tooltip("The creature counts as close for the fear effect inside this distance")] public float fearDistance = 6f;

    NetworkFirstPersonController controller;
    PlayerHiding hiding;

    /// <summary>Diagnostics: the outer range of the last breathing noise made (0 = none: the breath is held), and when.</summary>
    public float LastBreathRange { get; private set; }
    public double LastBreathAt { get; private set; }
    PlayerLife life;
    Vector3 lastPos;
    float sinceSample, sampleDist, sampleVert, speed, verticalSpeed, stride, heavy, breathTimer;

    void Awake()
    {
        controller = GetComponent<NetworkFirstPersonController>();
        hiding = GetComponent<PlayerHiding>();
        life = GetComponent<PlayerLife>();
    }

    public override void OnNetworkSpawn() => lastPos = transform.position;

    public void ResetForRound(System.Random rng)
    {
        stride = heavy = speed = verticalSpeed = sampleDist = sampleVert = sinceSample = 0f;
        breathTimer = breathInterval;
        lastPos = transform.position;
    }

    void Update()
    {
        if (!IsServer || !IsSpawned) return;

        Vector3 delta = transform.position - lastPos;
        lastPos = transform.position;
        if (life != null && !life.IsAlive) { stride = heavy = speed = 0f; return; } // dead or escaped: silent

        float dt = Time.deltaTime;
        if (hiding != null && hiding.SuppressFootsteps)
        {
            // In a locker (or just snapped in or out): no footsteps, and the teleport is not walking. Breathing still follows its rules below.
            stride = speed = sampleDist = sampleVert = sinceSample = 0f;
            heavy = Mathf.Max(0f, heavy - dt / Mathf.Max(0.01f, breathRecoverSeconds));
            EmitBreathing(dt);
            return;
        }
        Vector3 flat = new Vector3(delta.x, 0f, delta.z);
        float dist = flat.magnitude;
        if (dist > 3f) return; // a respawn teleport, not walking

        sampleDist += dist;
        sampleVert += Mathf.Abs(delta.y);
        if ((sinceSample += dt) >= sampleInterval)
        {
            speed = sampleDist / sinceSample;
            verticalSpeed = sampleVert / sinceSample;
            sampleDist = sampleVert = sinceSample = 0f;
        }

        // Midway between the controller's walk and sprint speeds, so walking never counts as sprinting.
        float sprintThreshold = (controller.walkSpeed + controller.sprintSpeed) * 0.5f;
        heavy = speed > sprintThreshold ? 1f : Mathf.Max(0f, heavy - dt / Mathf.Max(0.01f, breathRecoverSeconds));

        // Footsteps: one per stride travelled, only while moving and on the ground.
        if (speed > moveSpeed && verticalSpeed < maxGroundedVerticalSpeed)
        {
            bool crouching = controller.IsCrouched;
            bool sprinting = !crouching && speed > sprintThreshold;
            float length = crouching ? crouchStride : sprinting ? sprintStride : walkStride;
            stride += dist;
            if (stride >= length)
            {
                stride %= length;
                if (crouching) NoiseSystem.Emit(transform.position, crouchRange, "crouch step", SoundKind.CrouchStep, OwnerClientId);
                else if (sprinting) NoiseSystem.Emit(transform.position, sprintRange, "sprint step", SoundKind.SprintStep, OwnerClientId);
                else NoiseSystem.Emit(transform.position, walkRange, "walk step", SoundKind.WalkStep, OwnerClientId);
            }
        }
        else stride = 0f;

        EmitBreathing(dt);
    }

    // Breathing: always, even standing still, but only audible very close. Holding the breath (in a locker) silences THIS noise and nothing
    // else; a hidden player breathes extremely quietly; heavy breathing after a sprint stays more audible; fear raises the quiet breathing a little.
    void EmitBreathing(float dt)
    {
        if ((breathTimer -= dt) > 0f) return;
        breathTimer = breathInterval;
        bool hidden = hiding != null && hiding.IsHidden;
        float range = Mathf.Lerp(breathRange, heavyBreathRange, heavy);
        if (hidden || (hiding != null && hiding.HoldingBreath))
            range = HiddenBreathing.Range(hiding.HoldingBreath, heavy > 0.05f, breathRange, heavyBreathRange, hiddenBreathRange, hiddenFearBreathRange,
                hidden && hiding.CreatureClose(fearDistance), hidden);
        LastBreathRange = range;
        LastBreathAt = Time.timeAsDouble;
        if (range <= 0f) return; // holding the breath: nothing to hear
        NoiseSystem.Emit(transform.position, range, heavy > 0.05f ? "heavy breathing" : "breathing",
            heavy > 0.05f ? SoundKind.HeavyBreathing : SoundKind.Breathing, OwnerClientId);
    }
}
