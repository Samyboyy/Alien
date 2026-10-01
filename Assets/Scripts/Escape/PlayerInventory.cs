using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// One carried item per player. The host owns the slot: pick-up (ItemPickup), drop (G, host-validated),
/// use-up (ShipConsole), and automatic drops on death, escape and disconnect. Clients only display it.
/// </summary>
public class PlayerInventory : NetworkBehaviour, IRoundResettable
{
    [Tooltip("Outer range (m) of the impact noise when an item is dropped")] public float dropNoise = 4f;
    public float dropCooldown = 0.5f;

    readonly NetworkVariable<ItemKind> item = new(ItemKind.None);
    ItemPickup held; // host only
    PlayerLife life;
    float nextDropTime;
    GUIStyle style;

    public ItemKind Item => item.Value;

    /// <summary>Item carried by this client's own player, for prompts.</summary>
    public static ItemKind LocalItem
    {
        get
        {
            var nm = NetworkManager.Singleton;
            var local = nm != null && nm.IsListening ? nm.LocalClient?.PlayerObject : null;
            return local != null && local.TryGetComponent(out PlayerInventory inv) ? inv.Item : ItemKind.None;
        }
    }

    void Awake() => life = GetComponent<PlayerLife>();

    bool Alive => life == null || life.IsAlive;

    void Update()
    {
        if (!IsSpawned || !IsOwner || item.Value == ItemKind.None || !Alive || Cursor.lockState != CursorLockMode.Locked) return;
        var kb = Keyboard.current;
        if (kb != null && kb.gKey.wasPressedThisFrame) DropRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void DropRpc(RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId || !Alive || held == null || Time.time < nextDropTime) return;
        if (TryGetComponent(out PlayerHiding hiding) && hiding.IsHidden) return; // objective items stay put while hiding
        nextDropTime = Time.time + dropCooldown;
        DropHeld(DropPoint());
        NoiseSystem.Emit(transform.position, dropNoise, "item drop", SoundKind.Impact, OwnerClientId);
    }

    // In front of the feet unless a wall is in the way, snapped down to the floor.
    Vector3 DropPoint()
    {
        Vector3 p = Physics.Raycast(transform.position + Vector3.up, transform.forward, 1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? transform.position
            : transform.position + transform.forward * 0.7f;
        return Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var hit, 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            ? hit.point
            : p;
    }

    /// <summary>Host: take the pickup. An item already carried is left where the new one was.</summary>
    public void PickUp(ItemPickup pickup)
    {
        if (!IsServer) return;
        if (held != null) held.Drop(pickup.FloorPoint);
        held = pickup;
        item.Value = pickup.kind;
        pickup.Hide();
    }

    /// <summary>Host: leave the carried item at a floor point (G, death, escape).</summary>
    public void DropHeld(Vector3 floorPoint)
    {
        if (!IsServer || held == null) return;
        held.Drop(floorPoint);
        held = null;
        item.Value = ItemKind.None;
    }

    /// <summary>Host: the carried item is used up (e.g. a fuse fitted). The pickup stays hidden until the next round.</summary>
    public void Consume()
    {
        if (!IsServer || held == null) return;
        held = null;
        item.Value = ItemKind.None;
    }

    public void ResetForRound(System.Random rng)
    {
        held = null; // pickups reset their own placement
        item.Value = ItemKind.None;
    }

    public override void OnNetworkDespawn()
    {
        // A disconnecting player's item stays in the world (skipped during a full shutdown).
        if (IsServer && held != null && !NetworkManager.ShutdownInProgress) held.Drop(transform.position);
        held = null;
    }

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || item.Value == ItemKind.None || !Alive) return;
        style ??= new GUIStyle(GUI.skin.label) { fontSize = 16 };
        GUI.Label(new Rect(20, Screen.height - 40, 400, 28), $"Carrying: {item.Value}    G: drop", style);
    }
}
