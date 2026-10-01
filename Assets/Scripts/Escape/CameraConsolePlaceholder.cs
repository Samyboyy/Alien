using UnityEngine;

/// <summary>
/// PLACEHOLDER for the Camera Control console. The Escape Director decides which camera mounts are linked to the console and which feeds are
/// offline; this component carries that configuration and shows a short world prompt. It does NOT show live camera views, does not make the user
/// vulnerable while viewing and gives no creature information: that is a later milestone. Needs a NetworkObject and a collider.
/// Stateless, so nothing to reset.
/// </summary>
public class CameraConsolePlaceholder : MonoBehaviour, IInteractable
{
    [System.Serializable]
    public struct Feed
    {
        public string room;
        public bool available;
    }

    [Tooltip("Configured feeds (placeholder data: no live view yet)")] public Feed[] feeds = new Feed[0];
    public float useNoise = 2f;

    public string GetPrompt()
    {
        if (!RoundManager.IsActive) return null;
        int up = 0;
        foreach (var f in feeds) if (f.available) up++;
        return $"E — Camera console ({up}/{feeds.Length} feeds online) - live view not implemented yet";
    }

    public bool CanInteract(NetworkFirstPersonController player) => RoundManager.IsActive;

    public void Interact(NetworkFirstPersonController player) => NoiseSystem.Emit(transform.position, useNoise, "camera console");
}
