using UnityEngine;

/// <summary>
/// Reusable objective control: optional requirements (ship flags, a carried item), optional hold time and noise,
/// and effects on use (set ship flags, toggle a door, start a pod launch). Effects run on the host only.
/// Stateless: progress lives in RoundManager flags, the door and the pod. Needs a NetworkObject and a collider.
/// </summary>
public class ShipConsole : MonoBehaviour, IInteractable
{
    public string label = "Use console";
    [Tooltip("Shown after the action while requirements are missing")] public string lockedText = "locked";
    [Tooltip("Shown once this console's flags are set")] public string doneText = "done";

    [Header("Requirements")]
    public ShipFlags requiredFlags;
    public ItemKind requiredItem;
    public bool consumeItem;

    [Header("Use")]
    public float holdSeconds;
    [Tooltip("Loudness emitted each second while held")] public float holdNoise;
    [Tooltip("Loudness emitted when used")] public float useNoise = 5f;

    [Header("Effects")]
    public ShipFlags setFlags;
    public SlidingDoor door;
    public EscapePod pod;

    public float HoldSeconds => holdSeconds;
    public float HoldNoise => holdNoise;

    bool Done => setFlags != ShipFlags.None && (RoundManager.Flags & setFlags) == setFlags;
    string Action => door == null ? label : door.IsOpen ? "Close door" : "Open door";

    public string GetPrompt()
    {
        if (!RoundManager.IsActive) return null; // unusable in the lobby / after the round
        if (pod != null && pod.State != PodState.Idle) return pod.StatusText;
        if (Done) return $"{label}: {doneText}";
        if (!EscapeRules.Meets(requiredFlags, RoundManager.Flags, requiredItem, PlayerInventory.LocalItem)) return $"{Action}: {lockedText}";
        if (pod != null && !pod.CanLaunch) return pod.StatusText;
        return $"{(holdSeconds > 0f ? "Hold E" : "E")} — {Action}";
    }

    public bool CanInteract(NetworkFirstPersonController player) =>
        RoundManager.IsActive && !Done && (pod == null || pod.CanLaunch)
        && EscapeRules.Meets(requiredFlags, RoundManager.Flags, requiredItem,
            player.TryGetComponent(out PlayerInventory inv) ? inv.Item : ItemKind.None);

    public void Interact(NetworkFirstPersonController player)
    {
        if (!CanInteract(player)) return;
        if (door != null && !door.TryToggle()) return; // moving or blocked: nothing else happens
        if (consumeItem && player.TryGetComponent(out PlayerInventory inv)) inv.Consume();
        if (setFlags != ShipFlags.None) RoundManager.Instance.SetFlags(setFlags);
        if (pod != null) pod.StartLaunch();
        NoiseSystem.Emit(transform.position, useNoise, label);
    }
}
