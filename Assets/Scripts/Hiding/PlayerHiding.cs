using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A player's hidden state (on the player prefab). The HOST holds the truth: which locker the player is in (a replicated reference), whether
/// they hold their breath and how much breath is left. Everyone reads it: movement, interaction, throwing and dropping refuse while hidden,
/// the footstep noise stops, and breathing noise follows HiddenBreathing.
///
/// Movement is owner-authoritative in this game, so the OWNER snaps itself to the locker's anchors when the host says it is hidden (and back
/// out when released). The host never believes a position for this: hidden is the replicated reference, and a client that stays outside is
/// simply visible outside. While hidden the owner keeps its one camera and listener: the camera is blended to the locker's camera anchor and may
/// look around only within the locker's limits. Input is sent as transitions (E to leave, Space down and up), never per frame.
///
/// Breath: holding drains it (BreathModel, host side), running out forces a release, and a recovered breath can be held again. Holding silences
/// only the breathing noise. If the breath runs out with the creature close, one involuntary gasp is made: a normal logical breathing sound at
/// the player's real position, which the creature may hear like any other and which tells it nothing about lockers.
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class PlayerHiding : NetworkBehaviour, IRoundResettable
{
    [Header("Breath")]
    [Tooltip("Seconds of continuous breath holding from full")] public float holdSeconds = 5f;
    [Tooltip("Seconds after releasing before it starts to recover")] public float recoverDelay = 1.2f;
    [Tooltip("Seconds to recover from empty to full")] public float recoverSeconds = 7f;
    [Tooltip("A new hold needs at least this much breath (0..1)")] [Range(0f, 1f)] public float minToHold = 0.3f;
    [Tooltip("Hold and release requests closer than this are ignored (s)")] public float minTransitionGap = 0.25f;

    [Header("Gasp (breath runs out with the creature close)")]
    [Tooltip("The creature must be this close for the gasp to happen (m)")] public float gaspTriggerDistance = 10f;
    [Tooltip("Outer hearing range of the gasp (m)")] public float gaspRange = 6f;

    [Header("Camera")]
    [Tooltip("Seconds to blend the camera into the locker and back out")] public float cameraBlendSeconds = 0.35f;

    readonly NetworkVariable<ulong> lockerRef = new(0); // locker NetworkObjectId + 1, 0 = not hidden
    readonly NetworkVariable<bool> holding = new(false);
    readonly NetworkVariable<byte> breath = new(100);

    NetworkFirstPersonController controller;
    PlayerLife life;
    BreathModel model;
    CreatureAI creature;
    double enteredAt, nextCheck, nextBreathSend, changedAt = double.NegativeInfinity;
    float findTimer;
    byte sentBreath = 100;
    bool wasHolding;

    // Owner presentation.
    float lookYaw, lookPitch, blend = 1f, messageUntil;
    Vector3 blendFromPos;
    Quaternion blendFromRot;
    string message;
    GUIStyle hintStyle, messageStyle;
    static Texture2D vignette;

    public double NextEntryAt { get; private set; }
    public bool IsHidden => lockerRef.Value != 0;
    public bool HoldingBreath => holding.Value;
    public float Breath01 => breath.Value / 100f;
    public HideLocker Locker => Resolve(lockerRef.Value);

    /// <summary>True while hidden and for a moment after a snap in or out, so the jump of the teleport is never taken for walking.</summary>
    public bool SuppressFootsteps => IsHidden || Time.timeAsDouble - changedAt < 1.0;

    void Awake()
    {
        controller = GetComponent<NetworkFirstPersonController>();
        life = GetComponent<PlayerLife>();
        model = new BreathModel(holdSeconds, recoverDelay, recoverSeconds, minToHold, minTransitionGap);
    }

    public override void OnNetworkSpawn()
    {
        lockerRef.OnValueChanged += OnLockerChanged;
        holding.OnValueChanged += OnHoldingChanged;
        wasHolding = holding.Value;
    }

    public override void OnNetworkDespawn()
    {
        lockerRef.OnValueChanged -= OnLockerChanged;
        holding.OnValueChanged -= OnHoldingChanged;
        // A disconnecting player must not leave a locker occupied (skipped when the whole session shuts down).
        if (IsServer && !NetworkManager.ShutdownInProgress) Resolve(lockerRef.Value)?.ReleaseOccupant();
    }

    HideLocker Resolve(ulong id)
    {
        if (id == 0 || NetworkManager == null || !NetworkManager.IsListening) return null;
        return NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(id - 1, out var o) ? o.GetComponent<HideLocker>() : null;
    }

    bool Alive => life == null || life.IsAlive;

    // ---------- Host ----------

    /// <summary>Host (called by the locker): this player is now inside it.</summary>
    public void ServerEnter(HideLocker locker)
    {
        if (!IsServer) return;
        enteredAt = Time.timeAsDouble;
        model.Release(enteredAt);
        if (holding.Value) holding.Value = false;
        lockerRef.Value = locker.NetworkObjectId + 1;
        NextEntryAt = enteredAt + 0.5;
    }

    /// <summary>Host (called by the locker): this player is out. The cooldown stops them hopping into the next locker at once.</summary>
    public void ServerLeave(float reenterCooldown)
    {
        if (!IsServer) return;
        model.Release(Time.timeAsDouble);
        if (holding.Value) holding.Value = false;
        if (lockerRef.Value != 0) lockerRef.Value = 0;
        NextEntryAt = Time.timeAsDouble + reenterCooldown;
    }

    public void ResetForRound(System.Random rng)
    {
        if (!IsServer) return;
        Resolve(lockerRef.Value)?.ReleaseOccupant();
        ServerLeave(0f);
        model.Reset();
        breath.Value = 100;
        sentBreath = 100;
        NextEntryAt = 0;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsServer) ServerTick();
        if (IsOwner) OwnerTick();
    }

    void ServerTick()
    {
        double now = Time.timeAsDouble;
        model.Tick(Time.deltaTime);

        if (model.ConsumeForcedRelease())
        {
            if (holding.Value) holding.Value = false;
            if (IsHidden && HiddenBreathing.Gasps(true, CreatureDistance(), gaspTriggerDistance))
                NoiseSystem.Emit(transform.position, gaspRange, "gasp", SoundKind.HeavyBreathing, OwnerClientId); // once, from the player's real place
            if (IsOwner) ShowMessage("Out of breath"); else OutOfBreathRpc();
        }

        // Replicated about five times a second, and only when it moved enough to show.
        if (now >= nextBreathSend)
        {
            nextBreathSend = now + 0.2;
            byte q = model.Quantised;
            if (Mathf.Abs(q - sentBreath) >= 3 || (q != sentBreath && (q == 0 || q == 100)))
            {
                sentBreath = q;
                breath.Value = q;
            }
        }

        if (now < nextCheck) return;
        nextCheck = now + 0.25;
        // Death, escape and the end of the round release the player from the locker (and its occupancy).
        if (IsHidden && (!Alive || !RoundManager.IsActive))
        {
            Resolve(lockerRef.Value)?.ReleaseOccupant();
            ServerLeave(0f);
        }
    }

    // Only for the gasp: a distance, read on the host, from the creature to this player. Never an occupancy.
    float CreatureDistance()
    {
        if (creature == null && (findTimer -= Time.deltaTime) <= 0f) { findTimer = 2f; creature = FindFirstObjectByType<CreatureAI>(); }
        return creature == null ? float.MaxValue : Vector3.Distance(creature.transform.position, transform.position);
    }

    /// <summary>Host (for the breathing noise): is the creature close enough for fear to quicken the breathing?</summary>
    public bool CreatureClose(float distance) => CreatureDistance() <= distance;

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void ExitRpc(RpcParams rpcParams = default)
    {
        var locker = Resolve(lockerRef.Value);
        bool clear = locker != null && locker.ExitClear(transform);
        var result = LockerRules.ValidateExit(rpcParams.Receive.SenderClientId == OwnerClientId, locker != null && IsHidden, locker != null && locker.InspectionHoldsDoor,
            clear, Time.timeAsDouble, enteredAt, locker != null ? locker.minHiddenSeconds : 0f);
        if (result == ExitResult.Ok) locker.ServerExit(this);
        else if (result is ExitResult.HeldOpen or ExitResult.Blocked) ExitDeniedRpc((byte)result);
    }

    [Rpc(SendTo.Owner)]
    void ExitDeniedRpc(byte result) => ShowMessage(LockerRules.Explain((ExitResult)result));

    [Rpc(SendTo.Owner)]
    void OutOfBreathRpc() => ShowMessage("Out of breath");

    // One request per press and per release. The host decides everything; a refused hold simply does nothing.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void BreathRpc(bool hold, RpcParams rpcParams = default)
    {
        if (rpcParams.Receive.SenderClientId != OwnerClientId || !IsHidden) return;
        double now = Time.timeAsDouble;
        if (hold) { if (model.TryHold(now)) holding.Value = true; }
        else { model.Release(now); if (holding.Value) holding.Value = false; }
    }

    // ---------- Every peer: reactions to the replicated state ----------

    void OnLockerChanged(ulong previous, ulong now)
    {
        changedAt = Time.timeAsDouble;
        if (!IsOwner) return;
        if (now != 0)
        {
            var locker = Resolve(now);
            if (locker == null || !locker.AnchorsValid) return;
            blendFromPos = controller.playerCamera.transform.position; // before the snap: the camera glides in from where it was
            blendFromRot = controller.playerCamera.transform.rotation;
            controller.TeleportTo(locker.hiddenBody.position, locker.hiddenBody.rotation);
            lookYaw = lookPitch = 0f;
            blend = 0f;
            return;
        }
        // Out: step to the exit, but only if the round goes on for this player (after death or a restart the usual rules place them).
        var left = Resolve(previous);
        if (left != null && left.exitPoint != null && Alive && RoundManager.IsActive)
            controller.TeleportTo(left.exitPoint.position, left.exitPoint.rotation);
        blend = 1f;
    }

    void OnHoldingChanged(bool previous, bool now) => wasHolding = now;

    // ---------- Owner ----------

    void OwnerTick()
    {
        if (!IsHidden) return;
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null) return;
        if (kb.eKey.wasPressedThisFrame) ExitRpc();
        if (kb.spaceKey.wasPressedThisFrame) BreathRpc(true);
        if (kb.spaceKey.wasReleasedThisFrame) BreathRpc(false);
        if (mouse != null && controller.CursorCaptured)
        {
            Vector2 d = mouse.delta.ReadValue() * controller.mouseSensitivity;
            var locker = Locker;
            float yawLimit = locker != null ? locker.yawLimit : 35f, pitchLimit = locker != null ? locker.pitchLimit : 25f;
            (lookYaw, lookPitch) = LockerRules.ClampLook(lookYaw + d.x, lookPitch - d.y, yawLimit, pitchLimit);
        }
        blend = Mathf.Min(1f, blend + Time.deltaTime / Mathf.Max(0.05f, cameraBlendSeconds));
    }

    // The one local camera, posed at the locker's camera anchor (like the spectator does for its view); no second camera or listener.
    void LateUpdate()
    {
        if (!IsSpawned || !IsOwner || !IsHidden) return;
        var locker = Locker;
        if (locker == null || locker.hiddenCamera == null) return;
        Quaternion target = locker.hiddenCamera.rotation * Quaternion.Euler(lookPitch, lookYaw, 0f);
        float s = blend * blend * (3f - 2f * blend);
        controller.playerCamera.transform.SetPositionAndRotation(Vector3.Lerp(blendFromPos, locker.hiddenCamera.position, s), Quaternion.Slerp(blendFromRot, target, s));
    }

    void ShowMessage(string text)
    {
        message = text;
        messageUntil = Time.time + 2.5f;
    }

    GUIStyle diagStyle;

    // F4, host only: what the host knows about this player's hiding (developer information; the creature never reads any of it).
    void DrawDiagnostics()
    {
        diagStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 0.9f, 0.6f) } };
        var noise = GetComponent<FootstepNoise>();
        string breathing = noise == null ? "-" : noise.LastBreathRange <= 0f ? "none (held)" : $"{noise.LastBreathRange:0.0} m, {Time.timeAsDouble - noise.LastBreathAt:0.0}s ago";
        var locker = Locker;
        GUI.Label(new Rect(20, 500 + 18 * (int)OwnerClientId, 1100, 20),
            $"HIDING player {OwnerClientId}: {(IsHidden ? $"in {(locker != null ? locker.name : "?")}" : "not hidden")}, breath {breath.Value}%{(holding.Value ? " HOLDING" : "")}, last breathing noise {breathing}", diagStyle);
    }

    void OnGUI()
    {
        if (IsSpawned && IsServer && ShipAcoustics.DiagnosticsVisible) DrawDiagnostics();
        if (!IsSpawned || !IsOwner || !IsHidden || !Alive) return;
        vignette ??= MakeVignette();
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.75f * Mathf.SmoothStep(0f, 1f, blend));
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), vignette, ScaleMode.StretchToFill);
        GUI.color = old;

        hintStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, normal = { textColor = new Color(0.85f, 0.9f, 0.85f, 0.9f) } };
        float cx = Screen.width * 0.5f, y = Screen.height - 70f;
        GUI.Label(new Rect(cx - 250, y, 500, 28), "E — Exit        Space — Hold breath", hintStyle);

        // The breath bar only while holding or still recovering.
        if (holding.Value || breath.Value < 100)
        {
            GUI.Box(new Rect(cx - 90, y - 24, 180, 12), GUIContent.none);
            GUI.color = holding.Value ? new Color(0.6f, 0.9f, 1f) : breath.Value < 30 ? new Color(1f, 0.55f, 0.4f) : new Color(0.7f, 0.8f, 0.85f);
            GUI.DrawTexture(new Rect(cx - 88, y - 22, 176f * Breath01, 8), Texture2D.whiteTexture);
            GUI.color = old;
        }
        if (Time.time < messageUntil && !string.IsNullOrEmpty(message))
        {
            messageStyle ??= new GUIStyle(hintStyle) { fontSize = 18, fontStyle = FontStyle.Italic };
            GUI.Label(new Rect(cx - 250, y - 56, 500, 28), message, messageStyle);
        }
    }

    // A soft dark edge, clear in the middle: restrained, the view stays readable. Made once.
    static Texture2D MakeVignette()
    {
        const int Size = 128;
        var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
        var px = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = (x + 0.5f) / Size * 2f - 1f, dy = (y + 0.5f) / Size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.SmoothStep(0.35f, 1.15f, d) * 0.85f;
                px[y * Size + x] = new Color(0f, 0f, 0f, a);
            }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return tex;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => vignette = null;
}
