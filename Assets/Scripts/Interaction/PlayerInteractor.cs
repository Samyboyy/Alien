using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Local owner raycasts from the camera, shows crosshair, prompt and hold progress, and asks the host to interact on E.
/// The host re-checks alive, round state, cooldown, distance and line of sight itself and never trusts the client's raycast.
/// Holds (IInteractable.HoldSeconds > 0) run on the host and cancel on release, look-away, leaving range,
/// losing line of sight, death, or the target becoming unusable.
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class PlayerInteractor : NetworkBehaviour
{
    public float interactRange = 3f;
    [Tooltip("Extra host-side distance allowed for network lag")] public float rangeTolerance = 0.75f;
    [Tooltip("Host ignores requests closer together than this (spam guard)")] public float requestCooldown = 0.2f;

    // Host writes, owner draws the bar. 0 = no hold running.
    readonly NetworkVariable<float> holdProgress = new(0f);

    NetworkFirstPersonController player;
    PlayerLife life;
    NetworkObject targetObject; // what the local ray is looking at
    IInteractable targetInteractable;
    NetworkObject localHold; // owner: object E is being held on
    string prompt;
    GUIStyle promptStyle;

    // Host-side hold state.
    NetworkObject holdObject;
    IInteractable holdTarget;
    float holdTime, holdNoiseTimer, nextRequestTime;

    void Awake()
    {
        player = GetComponent<NetworkFirstPersonController>();
        life = GetComponent<PlayerLife>();
    }

    bool Dead => life != null && !life.IsAlive;
    static bool RoundOver => RoundManager.Instance != null && RoundManager.Instance.State == RoundState.Over;

    void Update()
    {
        if (IsServer) UpdateHold();
        if (IsSpawned && IsOwner) UpdateLocal();
    }

    void UpdateLocal()
    {
        targetObject = null;
        targetInteractable = null;
        prompt = null;
        // No interaction while the cursor is free (menu in use) or when out of play.
        if (!Dead && Cursor.lockState == CursorLockMode.Locked)
        {
            var cam = player.playerCamera.transform;
            if (Physics.Raycast(cam.position, cam.forward, out var hit, interactRange,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                var interactable = hit.collider.GetComponentInParent<IInteractable>();
                var obj = hit.collider.GetComponentInParent<NetworkObject>();
                if (interactable != null && obj != null && obj.IsSpawned)
                {
                    targetObject = obj;
                    targetInteractable = interactable;
                    prompt = interactable.GetPrompt();
                }
            }
        }

        var kb = Keyboard.current;
        if (localHold != null && (kb == null || !kb.eKey.isPressed || targetObject != localHold))
        {
            localHold = null;
            ReleaseRpc();
        }
        if (targetObject != null && kb != null && kb.eKey.wasPressedThisFrame)
        {
            InteractRpc(targetObject);
            if (targetInteractable.HoldSeconds > 0f) localHold = targetObject;
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void InteractRpc(NetworkObjectReference target, RpcParams rpcParams = default)
    {
        // InvokePermission already limits senders to this player's owner; check anyway.
        if (rpcParams.Receive.SenderClientId != OwnerClientId || Dead || RoundOver) return; // dead players cannot interact
        if (Time.time < nextRequestTime) return;
        nextRequestTime = Time.time + requestCooldown;
        if (!target.TryGet(out var obj, NetworkManager) || !obj.TryGetComponent(out IInteractable interactable)) return;
        if (!InReach(obj) || !interactable.CanInteract(player)) return;

        if (interactable.HoldSeconds <= 0f)
        {
            interactable.Interact(player);
            return;
        }
        holdObject = obj;
        holdTarget = interactable;
        holdTime = 0f;
        holdNoiseTimer = 0f;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void ReleaseRpc() => CancelHold();

    // Host: advance the running hold, re-validating every frame.
    void UpdateHold()
    {
        if (holdObject == null) return;
        if (Dead || RoundOver || !holdObject.IsSpawned || !InReach(holdObject) || !holdTarget.CanInteract(player))
        {
            CancelHold();
            return;
        }

        holdTime += Time.deltaTime;
        if ((holdNoiseTimer -= Time.deltaTime) <= 0f)
        {
            holdNoiseTimer = 1f;
            NoiseSystem.Emit(holdObject.transform.position, holdTarget.HoldNoise, "hold");
        }
        if (holdTime < holdTarget.HoldSeconds)
        {
            holdProgress.Value = holdTime / holdTarget.HoldSeconds;
            return;
        }

        var done = holdTarget;
        CancelHold();
        done.Interact(player);
    }

    void CancelHold()
    {
        holdObject = null;
        holdTarget = null;
        if (holdProgress.Value != 0f) holdProgress.Value = 0f;
    }

    // Host: close enough to the target's collider and nothing else in between.
    bool InReach(NetworkObject obj)
    {
        var col = obj.GetComponentInChildren<Collider>();
        if (col == null || !col.enabled) return false;
        Vector3 eye = player.playerCamera.transform.position;
        Vector3 point = col.ClosestPoint(eye);
        if (Vector3.Distance(eye, point) > interactRange + rangeTolerance) return false;

        // Line of sight: the first thing hit on the way must be the target itself.
        return !Physics.Linecast(eye, point, out var block, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            || block.collider.GetComponentInParent<NetworkObject>() == obj;
    }

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || Dead || Cursor.lockState != CursorLockMode.Locked) return;

        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        GUI.DrawTexture(new Rect(cx - 2, cy - 2, 4, 4), Texture2D.whiteTexture);

        if (holdProgress.Value > 0f)
        {
            GUI.Box(new Rect(cx - 100, cy + 16, 200, 14), GUIContent.none);
            GUI.DrawTexture(new Rect(cx - 98, cy + 18, 196 * holdProgress.Value, 10), Texture2D.whiteTexture);
        }

        if (string.IsNullOrEmpty(prompt)) return;
        promptStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 18 };
        GUI.Label(new Rect(cx - 400, cy + 36, 800, 50), prompt, promptStyle);
    }
}
