using UnityEngine;

/// <summary>
/// The door's sound, on every peer (added at runtime by SlidingDoor when an AudioBank exists). Played from the door's replicated
/// open/closed change, so players and the creature forcing it open are both heard. Closing uses the same recording a little lower
/// and quieter so the two are not identical. Cosmetic only: it is separate from the door's logical noise event.
/// </summary>
public class DoorAudio : MonoBehaviour
{
    AudioBank bank;
    SfxPool pool;
    System.Random rng;

    void Awake()
    {
        bank = AudioBank.Get();
        if (bank == null) { enabled = false; return; }
        pool = new SfxPool(gameObject, 2, 1f, bank.doorMaxDistance, AudioRolloffMode.Linear, bank.doorFullDistance);
        rng = new System.Random(GetInstanceID());
    }

    public void Play(bool opening)
    {
        if (pool == null || bank.doorSound == null) return;
        float jitter = 0.03f * (2f * (float)rng.NextDouble() - 1f);
        pool.Play(bank.doorSound, bank.doorVolume * (opening ? 1f : 0.85f), (opening ? 1f : 0.9f) + jitter);
    }
}
