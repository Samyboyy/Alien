using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// A world item (keycard, fuse). One scene object per item: hidden while carried or used up, moved when dropped,
/// never spawned or destroyed. Each round the host places it on a random spot from <see cref="spots"/>.
/// Needs NetworkObject + server-authority NetworkTransform + Collider + Renderer on the same object.
/// </summary>
public class ItemPickup : NetworkBehaviour, IInteractable, IRoundResettable
{
    public ItemKind kind = ItemKind.Keycard;
    [Tooltip("Candidate resting points (the item sits on top); one is picked at random each round")]
    public Transform[] spots;

    readonly NetworkVariable<bool> available = new(true);

    public Vector3 FloorPoint => transform.position - Vector3.up * (transform.lossyScale.y * 0.5f);

    public override void OnNetworkSpawn()
    {
        available.OnValueChanged += OnAvailableChanged;
        Show(available.Value);
    }

    public override void OnNetworkDespawn() => available.OnValueChanged -= OnAvailableChanged;

    void OnAvailableChanged(bool _, bool now) => Show(now);

    // Every peer: a hidden item is invisible and cannot be hit by interaction rays.
    void Show(bool visible)
    {
        GetComponent<Renderer>().enabled = visible;
        GetComponent<Collider>().enabled = visible;
    }

    public string GetPrompt()
    {
        if (!RoundManager.IsActive) return null; // unusable in the lobby / after the round
        var held = PlayerInventory.LocalItem;
        return held == ItemKind.None ? $"E — Pick up {kind}" : $"E — Swap {held} for {kind}";
    }

    public bool CanInteract(NetworkFirstPersonController player) => RoundManager.IsActive && available.Value;

    public void Interact(NetworkFirstPersonController player)
    {
        if (CanInteract(player) && player.TryGetComponent(out PlayerInventory inventory)) inventory.PickUp(this);
    }

    /// <summary>Host: hide while carried or used up.</summary>
    public void Hide()
    {
        if (IsServer) available.Value = false;
    }

    /// <summary>Host: rest on a floor point and show.</summary>
    public void Drop(Vector3 floorPoint)
    {
        if (!IsServer) return;
        Vector3 p = floorPoint + Vector3.up * (transform.lossyScale.y * 0.5f);
        GetComponent<NetworkTransform>().Teleport(p, transform.rotation, transform.localScale);
        available.Value = true;
    }

    public void ResetForRound(System.Random rng)
    {
        if (spots != null && spots.Length > 0) Drop(spots[rng.Next(spots.Length)].position);
        else available.Value = true;
    }
}
