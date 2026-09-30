using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>
/// First-person player. Movement is OWNER-AUTHORITATIVE for this prototype: the owning client
/// moves its own CharacterController and a NetworkTransform (AuthorityMode = Owner) replicates it.
/// The host does not validate movement. Sprint stamina is part of that: it is computed on the owning client, so a modified
/// client could ignore it (the host still hears its sprint footsteps from the replicated speed). Shared world state and
/// creature AI are host-authoritative.
/// Input is read directly from Keyboard/Mouse (Input System), so no action asset wiring is needed.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class NetworkFirstPersonController : NetworkBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 2.8f;
    public float sprintSpeed = 5.5f;
    public float crouchSpeed = 1.4f;
    public float gravity = -20f;

    [Header("Stamina (sprint only)")]
    [Tooltip("Seconds of continuous sprinting from full stamina")] public float sprintSeconds = 5f;
    [Tooltip("Seconds without sprinting before stamina starts to come back")] public float regenDelay = 2f;
    [Tooltip("Seconds from empty to full once regeneration has started")] public float fullRecoverySeconds = 8f;
    [Tooltip("After exhaustion, sprint is blocked until this fraction has recovered")] [Range(0.05f, 1f)] public float exhaustResume = 0.25f;

    [Header("Look")]
    [Tooltip("Degrees per mouse pixel")] public float mouseSensitivity = 0.1f;
    public float maxPitch = 89f;

    [Header("Body")]
    public float standHeight = 1.8f;
    public float crouchHeight = 1.0f;
    public float radius = 0.35f;
    public float eyeOffset = 0.15f;
    public float heightLerpSpeed = 10f;

    [Header("References")]
    public Camera playerCamera;
    public AudioListener audioListener;
    public Transform body;

    // Owner writes crouch, everyone reads. Server writes the spawn slot.
    readonly NetworkVariable<bool> crouched = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    readonly NetworkVariable<int> slot = new(-1);

    readonly StaminaModel stamina = new();
    CharacterController cc;
    PlayerLife life; // null-tolerant: prefab may predate the round system
    float pitch, velocityY, height;
    bool cursorCaptured, needsTeleport;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        life = GetComponent<PlayerLife>();
        height = standHeight;
        ApplyHeight(true);
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer) slot.Value = FreeSlot();

        // Audio (cosmetic, only when Alien > Setup Audio has created the AudioBank): footsteps for everyone, ambience for the owner.
        if (AudioBank.Get() != null)
        {
            if (GetComponent<FootstepAudio>() == null) gameObject.AddComponent<FootstepAudio>().Begin(IsOwner);
            if (IsOwner && GetComponent<LocalAmbience>() == null) gameObject.AddComponent<LocalAmbience>().Begin();
        }

        // Camera, listener and input only for the local owner.
        playerCamera.enabled = IsOwner;
        audioListener.enabled = IsOwner;
        if (IsOwner)
        {
            needsTeleport = true; // done in Update so NetworkTransform is fully spawned
            SetCursor(true);
            // Own body only casts shadows, so it never blocks our own view.
            foreach (var r in body.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner) SetCursor(false);
    }

    // Lowest slot index that no other player uses. Server only.
    int FreeSlot()
    {
        var used = new HashSet<int>();
        foreach (var p in FindObjectsByType<NetworkFirstPersonController>(FindObjectsSortMode.None))
            if (p != this) used.Add(p.slot.Value);
        int i = 0;
        while (used.Contains(i)) i++;
        return i;
    }

    void Teleport()
    {
        foreach (var sp in FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None))
        {
            if (sp.index != slot.Value) continue;
            cc.enabled = false;
            transform.SetPositionAndRotation(sp.transform.position, sp.transform.rotation);
            GetComponent<NetworkTransform>().Teleport(sp.transform.position, sp.transform.rotation, transform.localScale);
            cc.enabled = true;
            return;
        }
        Debug.LogWarning($"No SpawnPoint with index {slot.Value} in scene.");
    }

    // Dead or escaped: out of play (no movement, no collider).
    bool Dead => life != null && !life.IsAlive;

    public bool IsCrouched => crouched.Value;

    /// <summary>Sprint stamina 0..1. Only meaningful on the owning client (used by the cosmetic breathing audio).</summary>
    public float Stamina01 => stamina.Value;

    public bool StaminaExhausted => stamina.Exhausted;

    /// <summary>Host tells the owner to return to its spawn slot (round start / restart).</summary>
    [Rpc(SendTo.Owner)]
    public void RespawnRpc()
    {
        needsTeleport = true;
        velocityY = 0f;
        pitch = 0f;
        stamina.Reset(); // new round: full stamina, not exhausted
    }

    void Update()
    {
        // A dead body is a non-blocking placeholder: no collider, so it cannot block doors or players.
        if (cc.enabled == Dead && !needsTeleport) cc.enabled = !Dead;
        // Escaped players left in a pod: hide the body on every peer.
        bool escaped = life != null && life.Status == PlayerStatus.Escaped;
        if (body.gameObject.activeSelf == escaped) body.gameObject.SetActive(!escaped);

        if (IsSpawned && IsOwner)
        {
            if (needsTeleport && slot.Value >= 0) { Teleport(); needsTeleport = false; }
            HandleCursor();
            HandleMovement();
        }
        ApplyHeight(false);
    }

    void HandleCursor()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (cursorCaptured && kb != null && kb.escapeKey.wasPressedThisFrame)
            SetCursor(false);
        else if (!cursorCaptured && mouse != null && mouse.leftButton.wasPressedThisFrame && !ConnectionUI.IsPointerOverPanel())
            SetCursor(true); // deliberate click outside the menu
    }

    public void SetCursor(bool capture)
    {
        cursorCaptured = capture;
        Cursor.lockState = capture ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !capture;
    }

    void HandleMovement()
    {
        if (Dead) return; // dead players cannot move; LocalSpectator drives the camera
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        Vector3 input = Vector3.zero;
        bool sprintKey = false, wantCrouch = false;

        if (cursorCaptured && kb != null && mouse != null)
        {
            Vector2 look = mouse.delta.ReadValue() * mouseSensitivity;
            transform.Rotate(0f, look.x, 0f);
            pitch = Mathf.Clamp(pitch - look.y, -maxPitch, maxPitch);
            playerCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            input = new Vector3(kb.dKey.ReadValue() - kb.aKey.ReadValue(), 0f, kb.wKey.ReadValue() - kb.sKey.ReadValue());
            input = Vector3.ClampMagnitude(input, 1f);
            wantCrouch = kb.leftCtrlKey.isPressed || kb.cKey.isPressed;
            sprintKey = kb.leftShiftKey.isPressed && input.z > 0f;
        }

        // Cannot stand up under a low ceiling.
        if (!wantCrouch && crouched.Value && CeilingBlocked()) wantCrouch = true;
        if (crouched.Value != wantCrouch) crouched.Value = wantCrouch;

        // Sprint needs the key, forward movement, not crouching, and stamina (not exhausted).
        bool sprinting = sprintKey && !crouched.Value && stamina.CanSprint;
        float speed = crouched.Value ? crouchSpeed : sprinting ? sprintSpeed : walkSpeed;
        if (cc.isGrounded && velocityY < 0f) velocityY = -2f;
        velocityY += gravity * Time.deltaTime;
        Vector3 move = transform.TransformDirection(input) * speed + Vector3.up * velocityY;
        cc.Move(move * Time.deltaTime);

        // Drain only while actually sprint-moving: pushing into a wall (no real speed) costs nothing.
        Vector3 flatVelocity = cc.velocity;
        flatVelocity.y = 0f;
        bool sprintMoving = sprinting && flatVelocity.magnitude > walkSpeed * 0.75f;
        stamina.Tick(Time.deltaTime, sprintMoving, 1f / Mathf.Max(0.1f, sprintSeconds), regenDelay,
            1f / Mathf.Max(0.1f, fullRecoverySeconds), exhaustResume);
    }

    GUIStyle staminaStyle;

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || Dead) return;
        float y = Screen.height - 72f;
        GUI.Box(new Rect(20, y, 164, 14), GUIContent.none);
        Color old = GUI.color;
        GUI.color = stamina.Exhausted ? new Color(1f, 0.3f, 0.2f) : Color.white;
        GUI.DrawTexture(new Rect(22, y + 2, 160 * stamina.Value, 10), Texture2D.whiteTexture);
        GUI.color = old;
        if (stamina.Exhausted)
        {
            staminaStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.35f, 0.25f) } };
            GUI.Label(new Rect(20, y - 24, 200, 24), "EXHAUSTED", staminaStyle);
        }
    }

    bool CeilingBlocked()
    {
        Vector3 origin = transform.position + Vector3.up * (crouchHeight - radius);
        return Physics.SphereCast(origin, radius * 0.9f, Vector3.up, out _, standHeight - crouchHeight,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
    }

    // Runs on every peer so remote crouch looks right and the collider matches.
    void ApplyHeight(bool snap)
    {
        if (Dead)
        {
            // Body lies on the floor (placeholder death indicator).
            body.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            body.SetLocalPositionAndRotation(new Vector3(0f, radius, 0f), Quaternion.Euler(0f, 0f, 90f));
            return;
        }
        body.localRotation = Quaternion.identity;

        float target = crouched.Value ? crouchHeight : standHeight;
        height = snap ? target : Mathf.MoveTowards(height, target, heightLerpSpeed * Time.deltaTime);

        cc.radius = radius;
        cc.height = height;
        cc.center = new Vector3(0f, height * 0.5f, 0f);
        if (body != null)
        {
            // Body is the built-in 2m capsule mesh, pivot at its centre.
            body.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            body.localPosition = new Vector3(0f, height * 0.5f, 0f);
        }
        if (playerCamera != null)
            playerCamera.transform.localPosition = new Vector3(0f, height - eyeOffset, 0f);
    }
}
