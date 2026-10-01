using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// A noisemaker lying in the ship. Like the objective items it is one scene object that is hidden when taken and put back each round on
/// a spot from <see cref="spots"/> (chosen with the round seed), never spawned or destroyed. Collecting goes through the normal interaction
/// (the host validates range and line of sight) and only succeeds while the player has room: the host adds one to that player's count and
/// hides the pickup in the same step, so two players pressing E together cannot both get it.
/// Needs NetworkObject + server-authority NetworkTransform; the visible placeholder parts are its child meshes and colliders.
/// </summary>
public class NoisemakerPickup : NetworkBehaviour, IInteractable, IRoundResettable
{
    [Tooltip("Candidate resting points (a floor point each); one is picked each round")] public Transform[] spots;

    readonly NetworkVariable<bool> available = new(true);
    Renderer[] renderers;
    Collider[] colliders;

    void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        colliders = GetComponentsInChildren<Collider>(true);
    }

    public override void OnNetworkSpawn()
    {
        available.OnValueChanged += OnAvailableChanged;
        Show(available.Value);
    }

    public override void OnNetworkDespawn()
    {
        available.OnValueChanged -= OnAvailableChanged;
    }

    void OnAvailableChanged(bool previous, bool now)
    {
        Show(now); // the pickup's click is the collector's own feedback (PlayerNoisemakers), not a sound at the pickup
    }

    // Every peer: a taken pickup is invisible and cannot be hit by interaction rays.
    void Show(bool visible)
    {
        foreach (var r in renderers) if (r != null) r.enabled = visible;
        foreach (var c in colliders) if (c != null) c.enabled = visible;
    }

    public string GetPrompt()
    {
        if (!RoundManager.IsActive) return null;
        int have = PlayerNoisemakers.LocalCount, max = PlayerNoisemakers.LocalCapacity;
        return NoisemakerRules.CanAdd(have, max) ? $"E — Take noisemaker ({have}/{max})" : $"Carrying the most noisemakers ({max})";
    }

    public bool CanInteract(NetworkFirstPersonController player) =>
        RoundManager.IsActive && available.Value && player.TryGetComponent(out PlayerNoisemakers inv) && NoisemakerRules.CanAdd(inv.Count, inv.capacity);

    public void Interact(NetworkFirstPersonController player)
    {
        if (!IsServer || !available.Value || !player.TryGetComponent(out PlayerNoisemakers inv)) return;
        if (inv.TryAdd()) available.Value = false; // one step on the host: the pickup is gone the moment the count rises
    }

    public void ResetForRound(System.Random rng)
    {
        if (!IsServer) return;
        int i = NoisemakerRules.PickSpot(rng, spots != null ? spots.Length : 0);
        if (i >= 0) GetComponent<NetworkTransform>().Teleport(spots[i].position, transform.rotation, transform.localScale);
        available.Value = true;
    }
}
