using UnityEngine;

/// <summary>
/// The door's sound, on every peer (added at runtime by SlidingDoor when an AudioBank exists). Played from the door's replicated
/// open/closed change, so players and the creature forcing it open are both heard. Closing uses the same recording a little lower
/// and quieter so the two are not identical. The sound comes from a fixed point in the doorway (the slab slides into the wall), with
/// the perceptual rolloff and occlusion of other world sounds. Cosmetic only: it is separate from the door's logical noise event.
/// </summary>
public class DoorAudio : MonoBehaviour
{
    AudioBank bank;
    SfxPool pool;
    GameObject emitter;
    System.Random rng;

    void Awake()
    {
        bank = AudioBank.Get();
        if (bank == null) { enabled = false; return; }
        emitter = new GameObject($"Door Audio {name}");
        var door = GetComponent<SlidingDoor>();
        emitter.transform.position = door != null ? door.ClosedPosition : transform.position; // the doorway, even for a late joiner who sees it open
        pool = new SfxPool(emitter, 2, AudioCategory.World, 1f, bank.doorMaxDistance, customRolloff: AudioRouting.Rolloff(bank.doorFullDistance, bank.doorMaxDistance));
        var occlusion = emitter.AddComponent<EmitterAcoustics>();
        occlusion.ignoreRoot = transform; // the door's own slab never muffles its own sound
        occlusion.maxRange = bank.doorMaxDistance + 2f;
        occlusion.Attach(pool);
        rng = new System.Random(GetInstanceID());
    }

    void OnDestroy()
    {
        if (emitter != null) Destroy(emitter);
    }

    public void Play(bool opening)
    {
        if (pool == null || bank.doorSound == null) return;
        float jitter = 0.03f * (2f * (float)rng.NextDouble() - 1f);
        pool.Play(bank.doorSound, bank.doorVolume * (opening ? 1f : 0.85f), (opening ? 1f : 0.9f) + jitter);
    }
}
