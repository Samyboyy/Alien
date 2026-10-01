using UnityEngine;

/// <summary>
/// The local player's ambience (owner only, added at runtime by NetworkFirstPersonController when an AudioBank exists):
///  - the ship's layered room tone (ShipRoomTone) and the listener's room acoustics (ShipAcoustics), added from here;
///  - the tension riser (see TensionRiser), driven by how near the creature is;
///  - a subtle heartbeat that speeds up as the creature gets closer (each beat is cut from the clip and scheduled, so it can
///    go from slow to fast without pitch-shifting);
///  - breathing: out-of-breath when stamina runs low, scared when the creature is near or chasing. Only ONE breathing voice
///    ever sounds (BreathFader): a change fades the old one out completely before the new one starts.
/// Nearness is smoothed, and walls muffle it rather than silence it. Purely cosmetic; everything but the room tone is silent
/// while dead, in the lobby and after the round.
/// </summary>
public class LocalAmbience : MonoBehaviour
{
    NetworkFirstPersonController controller;
    PlayerLife life;
    AudioBank bank;
    AudioSource heartSource, scaredSource, runSource;
    AudioClip beat;
    TensionRiser riser;
    CreatureAI creature;
    readonly BreathFader breath = new();
    float riserTarget, riserLevel, heartTarget, heartLevel, sampleTimer, findTimer, beatTimer, engaged;
    bool lowStamina, lowRaw, scared;
    double runReadyAt;

    public void Begin()
    {
        bank = AudioBank.Get();
        controller = GetComponent<NetworkFirstPersonController>();
        life = GetComponent<PlayerLife>();
        if (bank == null) { enabled = false; return; }

        if (bank.tensionRiser != null) riser = new TensionRiser(bank, gameObject);

        heartSource = MakeSource("Heartbeat", null, false, AudioCategory.Internal);
        beat = AudioClipTools.Slice(bank.heartbeat, bank.heartbeatBeatStart, bank.heartbeatBeatLength, 0.15f, "heartbeat beat");
        if (bank.heartbeat != null && beat == null) Debug.LogWarning("Heartbeat: could not cut a beat from the clip (is it set to Decompress On Load? run Alien > Setup Audio).");

        scaredSource = MakeSource("Scared Breathing", bank.scaredBreath, true, AudioCategory.Internal);
        runSource = MakeSource("Run Breathing", bank.runBreath, true, AudioCategory.Internal);

        // The ship's room acoustics for this listener (reverb, the crawlspace's muffling, ambience level per space), and its room tone.
        if (GetComponent<ShipAcoustics>() == null) gameObject.AddComponent<ShipAcoustics>().Begin();
        if (GetComponent<ShipRoomTone>() == null) gameObject.AddComponent<ShipRoomTone>().Begin();
    }

    // Flat (2D), not tied to any position, and dry (Ambience/Internal bypass the room reverb).
    AudioSource MakeSource(string name, AudioClip clip, bool loop, AudioCategory category)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = loop;
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        AudioRouting.Configure(s, category);
        return s;
    }

    void OnDestroy()
    {
        if (heartSource != null) Destroy(heartSource.gameObject);
        if (scaredSource != null) Destroy(scaredSource.gameObject);
        if (runSource != null) Destroy(runSource.gameObject);
        if (beat != null) Destroy(beat);
        riser?.Dispose();
    }

    void Update()
    {
        if (bank == null) return;
        float dt = Time.deltaTime;
        bool on = RoundManager.IsActive && (life == null || life.IsAlive);

        if ((sampleTimer -= dt) <= 0f)
        {
            sampleTimer = 0.1f;
            Sample(on);
        }
        // Slow build and slower unwind, so the tension swells and recedes instead of tracking every step.
        riserLevel = Smooth(riserLevel, riserTarget, dt);
        heartLevel = Smooth(heartLevel, heartTarget, dt);
        if (!on)
        {
            if (riserLevel < 0.05f) riserLevel = 0f;
            if (heartLevel < 0.02f) heartLevel = 0f;
        }

        riser?.Tick(riserLevel, dt);
        UpdateHeartbeat(on, dt);
        UpdateBreathing(on, dt);
    }

    float Smooth(float level, float target, float dt) =>
        Mathf.MoveTowards(level, target, (target > level ? bank.riserRiseRate : bank.riserFallRate) * dt);

    // 10 Hz: how near the creature is, for the riser and (over a shorter range) the heartbeat.
    void Sample(bool on)
    {
        riserTarget = heartTarget = 0f;
        if (!on) { engaged = 0f; return; }
        if (creature == null)
        {
            if ((findTimer -= 0.1f) > 0f) return;
            findTimer = 1f;
            creature = FindFirstObjectByType<CreatureAI>();
            if (creature == null) return;
        }
        Vector3 ear = transform.position + Vector3.up * 1.5f, body = creature.transform.position + Vector3.up;
        float d = Vector3.Distance(body, ear);

        // The tension plays only when the creature is in the same room as the player, or can see the player. Being merely close
        // through a wall does not count. The linger keeps it going when a creature in your room steps out of view.
        var creatureRoom = RoomVolume.At(creature.transform.position);
        bool sameRoom = creatureRoom != null && creatureRoom == RoomVolume.At(transform.position);
        Vector3 creatureEye = creature.transform.position + Vector3.up * creature.eyeHeight;
        bool inSight = d <= creature.sightDistance
            && (!Physics.Linecast(creatureEye, ear, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || hit.transform.IsChildOf(transform));
        engaged = AudioRules.EngageRemaining(sameRoom || inSight, engaged, 0.1f, bank.engageLinger);
        if (engaged <= 0f) return;

        riserTarget = AudioRules.Proximity(d, bank.riserNear, bank.riserFar, false, 1f);
        heartTarget = AudioRules.Proximity(d, bank.heartbeatNear, bank.heartbeatFar, false, 1f);
    }

    // One beat at a time, scheduled. The interval shrinks as the creature closes in.
    void UpdateHeartbeat(bool on, float dt)
    {
        if (beat == null || !on || heartLevel < bank.heartbeatThreshold) { beatTimer = 0f; return; }
        if ((beatTimer -= dt) > 0f) return;
        beatTimer = AudioRules.HeartbeatInterval(heartLevel, bank.heartbeatSlowInterval, bank.heartbeatFastInterval);
        heartSource.PlayOneShot(beat, Mathf.Clamp01(bank.heartbeatVolume * Mathf.Lerp(0.45f, 1f, heartLevel) * AudioRouting.Volume(AudioCategory.Internal)));
    }

    // Out of breath beats scared; never both. BreathFader guarantees one voice at a time.
    void UpdateBreathing(bool on, float dt)
    {
        lowRaw = AudioRules.LowStamina(lowRaw, controller.Stamina01, controller.StaminaExhausted, bank.runBreathOnStamina, bank.runBreathOffStamina);
        bool spell = AudioRules.RunEpisode(lowStamina, lowRaw, controller.StaminaExhausted, Time.timeAsDouble, runReadyAt);
        if (lowStamina && !spell) runReadyAt = Time.timeAsDouble + bank.runBreathCooldown; // a spell just ended: rest before the next
        lowStamina = spell;
        bool chased = creature != null && creature.IsChasing(controller.OwnerClientId);
        scared = heartLevel >= (scared ? bank.scaredOff : bank.scaredOn) || chased;
        var desired = on ? AudioRules.DesiredBreath(lowStamina, scared) : BreathKind.None;
        breath.Tick(desired, dt, bank.breathFadeIn, bank.breathFadeOut);

        float inner = AudioRouting.Volume(AudioCategory.Internal);
        Drive(scaredSource, breath.Current == BreathKind.Scared, breath.Gain * bank.scaredBreathVolume * inner);
        Drive(runSource, breath.Current == BreathKind.Run, breath.Gain * bank.runBreathVolume * inner);
    }

    // A breathing voice plays only while it is THE current one; otherwise it is stopped.
    static void Drive(AudioSource s, bool current, float volume)
    {
        if (s.clip == null) return;
        if (!current) { if (s.isPlaying) s.Stop(); return; }
        if (!s.isPlaying) s.Play();
        s.volume = Mathf.Clamp01(volume);
    }
}
