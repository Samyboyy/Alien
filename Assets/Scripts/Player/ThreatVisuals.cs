using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Local-only peripheral stress effect: a dark vignette with a little lens distortion (HDRP's own Vignette and Lens Distortion
/// overrides, blended by the weight of ONE runtime-owned global Volume). Only the owning client creates it, so other players'
/// views and the shared Volume profiles are never touched.
///
/// It is cosmetic. It reads the creature's replicated transform and the local player's own state, and never writes anything back:
/// detection, hearing and concealment do not know it exists. Stress comes from what this player could plausibly perceive: a
/// creature CLOSE (proximity is the main driver; which way it looks only trims the effect a little), with an unobstructed view of their body (poor cover raises it, good cover lowers it a little), plus a
/// small extra for a confirmed chase of this very player. The aim is tension while hiding from a creature that looks threatening
/// but has not found you, so a chase adds only a little. No view, no effect, so a wall between them is silent. Not a detection meter.
/// Active only for a living local player while the round is running; cleared at once on death, escape, round end and disconnect.
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class ThreatVisuals : NetworkBehaviour
{
    [Header("Master")]
    public bool enableEffect = true;
    [Tooltip("Overall strength: 0 disables the effect, 1 is full")] [Range(0f, 1f)] public float strength = 1f;

    [Header("Maximum effect (at full threat and strength 1)")]
    [Range(0f, 0.6f)] public float maxVignette = 0.22f;
    [Tooltip("Lens Distortion intensity. If it bulges the edges the wrong way, flip the sign.")] [Range(-0.5f, 0.5f)] public float maxDistortion = 0.12f;
    [Tooltip("Units per second the effect grows")] public float fadeInSpeed = 1.2f;
    [Tooltip("Units per second the effect fades once the threat is gone")] public float fadeOutSpeed = 0.7f;

    [Header("Threat")]
    [Tooltip("At or closer than this the creature counts as fully near (m)")] public float nearDistance = 3f;
    [Tooltip("At or beyond this it causes no pressure (m)")] public float farDistance = 12f;
    [Tooltip("How much the creature looking AWAY lowers the pressure (0 = proximity only, 1 = it must face you). Low: being close is what matters.")] [Range(0f, 1f)] public float facingInfluence = 0.25f;
    [Tooltip("How much good cover lowers the pressure. Low on purpose: a hidden player should still feel a watching creature.")] [Range(0f, 1f)] public float coverRelief = 0.25f;
    [Tooltip("Small extra while the creature is confirmed to be chasing THIS player and can see them. The effect is mainly for hiding under threat, not for chases.")] [Range(0f, 1f)] public float chaseBonus = 0.1f;

    [Header("Debug preview (off by default, bypasses the threat calculation)")]
    public bool previewEnabled;
    [Range(0f, 1f)] public float preview = 1f;

    [Tooltip("Read-only: current smoothed intensity 0..1")] public float currentIntensity;

    static readonly float[] bodySamples = { 0.9f, 0.55f, 0.2f }; // same fractions as the creature's sight rays

    NetworkFirstPersonController controller;
    CharacterController cc;
    PlayerLife life;
    CreatureAI creature;
    Volume volume;
    VolumeProfile profile;
    Vignette vignette;
    LensDistortion distortion;
    float pressure, sampleTimer, findTimer;

    void Awake()
    {
        controller = GetComponent<NetworkFirstPersonController>();
        cc = GetComponent<CharacterController>();
        life = GetComponent<PlayerLife>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner) CreateVolume();
    }

    public override void OnNetworkDespawn() => Teardown();

    public override void OnDestroy()
    {
        Teardown();
        base.OnDestroy();
    }

    // ---------- Runtime-owned volume ----------

    void CreateVolume()
    {
        var go = new GameObject("Threat Volume (runtime)") { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(transform, false);
        volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 1000f; // above scene volumes: the weight blends our values over whatever they set
        volume.weight = 0f;
        volume.enabled = false;

        profile = ScriptableObject.CreateInstance<VolumeProfile>(); // our own instance; shared profiles are never edited
        profile.hideFlags = HideFlags.DontSave;
        vignette = profile.Add<Vignette>(true);
        distortion = profile.Add<LensDistortion>(true);
        vignette.color.value = Color.black;
        vignette.smoothness.value = 0.85f; // soft falloff: darkens only the far edges
        distortion.scale.value = 1f;
        volume.sharedProfile = profile;
        ApplyLevels();
    }

    void Teardown()
    {
        if (volume != null) Destroy(volume.gameObject);
        if (profile != null) Destroy(profile);
        volume = null;
        profile = null;
        vignette = null;
        distortion = null;
        currentIntensity = pressure = 0f;
    }

    void ApplyLevels()
    {
        vignette.intensity.value = maxVignette;
        distortion.intensity.value = maxDistortion;
    }

    // ---------- Per frame ----------

    void Update()
    {
        if (!IsSpawned || !IsOwner || volume == null) return;

        bool alive = life == null || life.IsAlive;
        bool playing = RoundManager.IsActive && alive;
        bool previewing = previewEnabled && alive; // debug only: works without a round so the maximum can be inspected
        if (!enableEffect || strength <= 0f || !(playing || previewing))
        {
            Clear();
            return;
        }

        float target;
        if (previewing) target = preview;
        else
        {
            if ((sampleTimer -= Time.deltaTime) <= 0f) { sampleTimer = 0.1f; pressure = Sample(); }
            target = pressure;
        }

        currentIntensity = ThreatMath.Approach(currentIntensity, target, Time.unscaledDeltaTime, fadeInSpeed, fadeOutSpeed);
        ApplyLevels();
        volume.enabled = currentIntensity > 0.001f;
        volume.weight = currentIntensity * strength;
    }

    // Dead, escaped, spectating, round over, lobby, disabled: gone immediately, and a restart begins from zero.
    void Clear()
    {
        currentIntensity = pressure = 0f;
        volume.weight = 0f;
        volume.enabled = false;
    }

    // ---------- Threat (10 Hz) ----------

    float Sample()
    {
        if (creature == null)
        {
            if ((findTimer -= 0.1f) > 0f) return 0f;
            findTimer = 1f;
            creature = FindFirstObjectByType<CreatureAI>();
            if (creature == null) return 0f;
        }

        // The creature's eye to this player's body, exactly like its own sight rays. Walls, closed doors and furniture block these,
        // so nothing behind geometry can ever register.
        Vector3 eye = creature.transform.position + Vector3.up * creature.eyeHeight;
        float height = cc.height;
        int seen = 0;
        foreach (float f in bodySamples)
            if (!Physics.Linecast(eye, transform.position + Vector3.up * (height * f), out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || hit.transform.IsChildOf(transform))
                seen++;
        if (seen == 0) return 0f;

        Vector3 chest = transform.position + Vector3.up * (height * 0.55f);
        Vector3 to = chest - eye;
        float distance = to.magnitude;
        Vector3 flatTo = new Vector3(to.x, 0f, to.z), flatFwd = creature.transform.forward;
        flatFwd.y = 0f;
        float facing = flatTo.sqrMagnitude > 0.0001f && flatFwd.sqrMagnitude > 0.0001f
            ? ThreatMath.Facing(Vector3.Dot(flatTo.normalized, flatFwd.normalized), creature.fieldOfView) : 1f;

        // Local estimate of this player's own cover (same volume rules as the host, but only used for the visual).
        var spot = HidingSpot.Resolve(transform.position, height, controller.IsCrouched);
        float cover = spot != null ? spot.visualConcealment : 0f;

        return ThreatMath.Pressure(seen / (float)bodySamples.Length, distance, facing, cover, creature.IsChasing(OwnerClientId),
            nearDistance, farDistance, coverRelief, chaseBonus, facingInfluence);
    }
}
