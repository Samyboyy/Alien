using UnityEngine;

/// <summary>
/// Implement on a MonoBehaviour that sits on (or above) a collider, with a NetworkObject on the same object or a parent.
/// The local player's <see cref="PlayerInteractor"/> finds it with a raycast; the host validates and runs Interact.
/// </summary>
public interface IInteractable
{
    /// <summary>Prompt text shown on the local client, e.g. "E — Open door".</summary>
    string GetPrompt();

    /// <summary>Runs on the HOST only, after range and line-of-sight checks passed.</summary>
    void Interact(NetworkFirstPersonController player);

    /// <summary>Seconds E must be held before Interact runs (0 = single press). The host tracks progress.</summary>
    float HoldSeconds => 0f;

    /// <summary>Loudness the host emits about once per second while this is being held.</summary>
    float HoldNoise => 0f;

    /// <summary>HOST: whether this player may use it now. Checked on press and every frame of a hold.</summary>
    bool CanInteract(NetworkFirstPersonController player) => true;
}
