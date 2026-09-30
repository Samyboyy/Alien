using UnityEngine;

/// <summary>
/// Audible footsteps of a player, on every peer (added at runtime by NetworkFirstPersonController when an AudioBank exists).
/// It works from the player's own replicated movement, like FootstepNoise, but only makes sound: it never emits noise events, so
/// what the creature can hear is unchanged. Your own steps are flat and quieter, other players' are 3D.
/// Crouching uses the quiet clip; walking and sprinting use the regular clips (the second one now and then), sprinting a little
/// higher and louder. Pitch and volume vary slightly so steps do not sound identical.
/// </summary>
public class FootstepAudio : MonoBehaviour
{
    NetworkFirstPersonController controller;
    PlayerLife life;
    FootstepNoise tuning; // stride lengths and thresholds, shared with the logical footsteps
    AudioBank bank;
    SfxPool pool;
    bool own;
    Vector3 last;
    float stride, speed;
    System.Random rng;

    public void Begin(bool isOwner)
    {
        own = isOwner;
        bank = AudioBank.Get();
        controller = GetComponent<NetworkFirstPersonController>();
        life = GetComponent<PlayerLife>();
        tuning = GetComponent<FootstepNoise>();
        pool = new SfxPool(gameObject, 3, own ? 0f : 1f, bank != null ? bank.footstepMaxDistance : 22f);
        rng = new System.Random(GetInstanceID());
        last = transform.position;
    }

    void Update()
    {
        if (bank == null || controller == null) return;
        Vector3 delta = transform.position - last;
        last = transform.position;
        if (life != null && !life.IsAlive) { stride = speed = 0f; return; }

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 flat = new Vector3(delta.x, 0f, delta.z);
        float dist = flat.magnitude;
        if (dist > 3f) return; // teleport (respawn), not walking
        speed = Mathf.Lerp(speed, dist / dt, 0.25f);
        bool airborne = Mathf.Abs(delta.y) / dt > (tuning != null ? tuning.maxGroundedVerticalSpeed : 3f);
        if (speed <= (tuning != null ? tuning.moveSpeed : 0.6f) || airborne) { stride = 0f; return; }

        bool crouching = controller.IsCrouched;
        float sprintAt = (controller.walkSpeed + controller.sprintSpeed) * 0.5f;
        bool sprinting = !crouching && speed > sprintAt;
        float length = tuning == null ? (crouching ? 1.2f : sprinting ? 2.2f : 1.6f)
            : crouching ? tuning.crouchStride : sprinting ? tuning.sprintStride : tuning.walkStride;

        stride += dist;
        if (stride < length) return;
        stride %= length;

        AudioClip clip = crouching ? bank.footstepQuiet
            : rng.NextDouble() < bank.regular2Chance && bank.footstepRegular2 != null ? bank.footstepRegular2 : bank.footstepRegular1;
        float volume = (own ? bank.ownFootstepVolume : bank.otherFootstepVolume) * (sprinting ? 1.15f : 1f) * (0.9f + 0.1f * (float)rng.NextDouble());
        float pitch = 1f + bank.pitchSpread * (2f * (float)rng.NextDouble() - 1f) + (sprinting ? 0.06f : 0f);
        pool.Play(clip, volume, pitch);
    }
}
