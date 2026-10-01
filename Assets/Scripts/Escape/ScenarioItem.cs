using UnityEngine;

/// <summary>
/// Puts a generated-ship item (keycard, fuse) at one of its candidate anchors each round. The candidates were chosen by the Escape Director at
/// build time and are all plausible and all reachable before the item's own obstacle, so every choice is solvable. The pick is a function of the
/// replicated round seed and the item id only (ScenarioRound.PickIndex), never of a shared random stream, so a round is reproducible from its
/// seed and restarting resets exactly the same way. The host moves the item through ItemPickup.Drop; clients do nothing here.
/// Put on the same object as an ItemPickup whose own spots are left empty.
/// </summary>
[RequireComponent(typeof(ItemPickup))]
public class ScenarioItem : MonoBehaviour, IRoundResettable
{
    [Tooltip("Stable id used with the round seed to pick a spot")] public string itemId = "";
    [Tooltip("The candidate resting points: the surface point of each item anchor")] public Transform[] spots;

    public void ResetForRound(System.Random rng)
    {
        if (spots == null || spots.Length == 0) return;
        int i = ScenarioRound.PickIndex(RoundManager.Seed, itemId, spots.Length);
        GetComponent<ItemPickup>().Drop(spots[i].position); // host only; Drop ignores other peers
    }
}
