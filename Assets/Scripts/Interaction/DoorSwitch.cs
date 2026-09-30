using UnityEngine;

/// <summary>Wall switch that toggles one door. Needs a NetworkObject and a collider on the same object.</summary>
public class DoorSwitch : MonoBehaviour, IInteractable
{
    public SlidingDoor door;

    public string GetPrompt() => door.IsOpen ? "E — Close door" : "E — Open door";

    public void Interact(NetworkFirstPersonController player) => door.TryToggle();
}
