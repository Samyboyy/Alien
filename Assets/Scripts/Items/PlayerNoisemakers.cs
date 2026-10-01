using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A player's small utility inventory of throwable noisemakers (separate from the one carried objective item). The host owns the count;
/// clients only display it and ask. F throws one: the owner sends one request with its aim, and the host checks the sender, that the
/// player is alive and the round is active, that one is left and the cooldown has passed, takes the aim only as a direction (constrained
/// to the player's own facing and a sane pitch), derives the spawn point from the player's authoritative position (never through a wall),
/// consumes one, spawns the device and gives it its velocity. Unused ones vanish with the player and are reset every round.
/// The handling noise at the player is a small incidental noise, like dropping an item.
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class PlayerNoisemakers : NetworkBehaviour, IRoundResettable
{
    [Header("Supply")]
    [Tooltip("Most a player can carry")] public int capacity = 2;
    [Tooltip("How many each player starts a round with")] public int startingCount;

    [Header("Throw (host)")]
    public GameObject devicePrefab;
    [Tooltip("Seconds between throws")] public float cooldown = 1.2f;
    [Tooltip("Launch speed (m/s)")] public float throwSpeed = 9f;
    [Tooltip("Extra upward share added to the aim, so a flat aim still arcs a little")] [Range(0f, 0.5f)] public float upwardBias = 0.12f;
    [Tooltip("How far in front of the hand it appears (m)")] public float spawnForward = 0.5f;
    [Tooltip("The aim's yaw may differ from the player's facing by this much (degrees, covers lag)")] public float maxYawError = 25f;
    public float minPitch = -50f;
    public float maxPitch = 60f;
    [Tooltip("Outer range of the handling noise at the thrower (m)")] public float handlingNoise = 3f;
    [Tooltip("No more than this many devices exist at once; further throws are refused")] public int maxActiveDevices = 8;

    static readonly List<PlayerNoisemakers> all = new();
    public static IReadOnlyList<PlayerNoisemakers> All => all;

    readonly NetworkVariable<byte> count = new(0);
    PlayerLife life;
    PlayerHiding hiding;
    NetworkFirstPersonController controller;
    CharacterController cc;
    double nextThrow, nextLocalThrow;
    bool loggedSpawnFailure;
    GameObject audioHost;
    SfxPool pool;
    GUIStyle style, diagStyle;
    readonly StringBuilder diag = new();
    string diagText = "";
    float diagTimer;

    public int Count => count.Value;
    public ThrowResult LastResult { get; private set; }

    /// <summary>The local player's own count (for pickup prompts).</summary>
    public static int LocalCount
    {
        get
        {
            var nm = NetworkManager.Singleton;
            var local = nm != null && nm.IsListening ? nm.LocalClient?.PlayerObject : null;
            return local != null && local.TryGetComponent(out PlayerNoisemakers n) ? n.count.Value : 0;
        }
    }

    public static int LocalCapacity
    {
        get
        {
            var nm = NetworkManager.Singleton;
            var local = nm != null && nm.IsListening ? nm.LocalClient?.PlayerObject : null;
            return local != null && local.TryGetComponent(out PlayerNoisemakers n) ? n.capacity : 2;
        }
    }

    void Awake()
    {
        life = GetComponent<PlayerLife>();
        hiding = GetComponent<PlayerHiding>();
        controller = GetComponent<NetworkFirstPersonController>();
        cc = GetComponent<CharacterController>();
    }

    bool Alive => life == null || life.IsAlive;

    public override void OnNetworkSpawn()
    {
        all.Add(this);
        count.OnValueChanged += OnCount;
        if (IsServer) count.Value = (byte)Mathf.Clamp(startingCount, 0, capacity);
    }

    // The owner's own feedback, from the confirmed count (never from the key press): a quiet click when one is picked up, the handling sound
    // when one is thrown. A reset (lobby, restart) happens while the round is not active, so it makes no sound.
    void OnCount(byte previous, byte now)
    {
        if (!IsOwner || !RoundManager.IsActive || now == previous) return;
        PlayLocal(now > previous ? NoisemakerSound.Pickup : NoisemakerSound.Throw);
    }

    public override void OnNetworkDespawn()
    {
        count.OnValueChanged -= OnCount;
        all.Remove(this);
        if (pool != null) pool.StopAll();
        if (audioHost != null) Destroy(audioHost);
        audioHost = null;
        pool = null;
    }

    public override void OnDestroy()
    {
        all.Remove(this);
        if (audioHost != null) Destroy(audioHost);
        base.OnDestroy();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    public void ResetForRound(System.Random rng)
    {
        if (!IsServer) return;
        count.Value = (byte)Mathf.Clamp(startingCount, 0, capacity);
        nextThrow = 0;
    }

    /// <summary>Host: one more noisemaker, if there is room. False (and nothing changes) when full.</summary>
    public bool TryAdd()
    {
        if (!IsServer || !NoisemakerRules.CanAdd(count.Value, capacity)) return false;
        count.Value++;
        return true;
    }

    // ---------- Owner ----------

    void Update()
    {
        if (!IsSpawned || !IsOwner || count.Value == 0 || !Alive || !RoundManager.IsActive || !controller.CursorCaptured || (hiding != null && hiding.IsHidden)) return;
        var kb = Keyboard.current;
        if (kb == null || !kb.fKey.wasPressedThisFrame || Time.timeAsDouble < nextLocalThrow) return;
        nextLocalThrow = Time.timeAsDouble + cooldown;
        ThrowRpc(controller.playerCamera.transform.forward);
    }

    // ---------- Host ----------

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    void ThrowRpc(Vector3 aim, RpcParams rpcParams = default)
    {
        double now = Time.timeAsDouble;
        var result = NoisemakerRules.ValidateThrow(rpcParams.Receive.SenderClientId == OwnerClientId, LockerRules.MayThrow(Alive, hiding != null && hiding.IsHidden), RoundManager.IsActive, count.Value, now, nextThrow);
        if (result == ThrowResult.Ok && devicePrefab == null) result = ThrowResult.BadAim;
        if (result == ThrowResult.Ok && ThrownNoisemaker.ActiveCount >= maxActiveDevices) result = ThrowResult.Cooldown;
        Vector3 forward = transform.forward;
        float dx = 0f, dy = 0f, dz = 0f;
        if (result == ThrowResult.Ok && !NoisemakerRules.ConstrainAim(aim.x, aim.y, aim.z, forward.x, forward.z, maxYawError, minPitch, maxPitch, out dx, out dy, out dz))
            result = ThrowResult.BadAim;
        if (result == ThrowResult.Ok)
        {
            // From the AUTHORITATIVE player: the hand is a fixed height above their feet, never wherever the client says.
            Vector3 dir = new Vector3(dx, dy + upwardBias, dz).normalized;
            Vector3 origin = transform.position + Vector3.up * (cc != null ? cc.height * 0.8f : 1.4f);
            if (!SpawnPoint(origin, dir, out Vector3 at)) result = ThrowResult.BadAim;
            else
            {
                nextThrow = now + cooldown; // a failed attempt still waits out the cooldown, so it cannot be retried every frame
                // Transactional: the device is spawned first and only a successful spawn takes one from the count.
                int remaining = count.Value;
                result = NoisemakerRules.Commit(ref remaining, () => SpawnDevice(at, dir * throwSpeed));
                if (result == ThrowResult.Ok)
                {
                    count.Value = (byte)remaining;
                    NoiseSystem.Emit(transform.position, handlingNoise, "noisemaker throw", SoundKind.Impact, OwnerClientId);
                    ThrowFxRpc();
                }
            }
        }
        LastResult = result;
    }

    // Host: spawns and launches one device. False (nothing left behind, nothing consumed) when the prefab is not usable.
    bool SpawnDevice(Vector3 at, Vector3 velocity)
    {
        var nm = NetworkManager;
        if (nm == null || !nm.IsServer || devicePrefab == null) return SpawnFailed("there is no running host or no device prefab assigned on the Player prefab");
        if (!nm.NetworkConfig.Prefabs.Contains(devicePrefab))
            return SpawnFailed($"the device prefab '{devicePrefab.name}' is not in the NetworkManager's prefab lists. Run Alien > Add Throwable Noisemakers (it attaches Assets/DefaultNetworkPrefabs.asset to the NetworkManager) and save the Ship scene");
        var spawned = NetworkObject.InstantiateAndSpawn(devicePrefab, nm, NetworkManager.ServerClientId, true, false, false, at, Quaternion.identity);
        if (spawned == null) return SpawnFailed("the spawn was refused");
        if (!spawned.TryGetComponent(out ThrownNoisemaker device))
        {
            if (spawned.IsSpawned) spawned.Despawn(true);
            else Destroy(spawned.gameObject);
            return SpawnFailed($"'{devicePrefab.name}' has no ThrownNoisemaker component");
        }
        device.Launch(velocity, cc);
        return true;
    }

    bool SpawnFailed(string why)
    {
        if (!loggedSpawnFailure) Debug.LogError($"Noisemaker throw failed (nothing was consumed): {why}.", this); // once: not every attempt
        loggedSpawnFailure = true;
        return false;
    }

    // The device appears in front of the hand, but stops short of a wall; and not at all when the hand itself is inside something solid.
    bool SpawnPoint(Vector3 origin, Vector3 dir, out Vector3 at)
    {
        const float Radius = 0.12f;
        at = origin;
        if (Physics.CheckSphere(origin, Radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            // Only the thrower's own body or a trigger can be here legitimately; anything else is a wall at the hand.
            foreach (var c in Physics.OverlapSphere(origin, Radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (c.transform.root != transform.root) return false;
        }
        if (Physics.SphereCast(origin, Radius, dir, out var hit, spawnForward, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && hit.collider.transform.root != transform.root)
            at = origin + dir * Mathf.Max(0f, hit.distance - 0.02f);
        else
            at = origin + dir * spawnForward;
        return true;
    }

    // Everyone but the thrower hears the handling at the thrower (the thrower played it when pressing F).
    [Rpc(SendTo.NotOwner)]
    void ThrowFxRpc() => PlayLocal(NoisemakerSound.Throw);

    // Pickup and handling sounds come from a person: flat and light for the owner (own body), 3D for everyone else. Works without an AudioBank.
    void PlayLocal(NoisemakerSound kind)
    {
        var bank = AudioBank.Get();
        if (pool == null)
        {
            audioHost = new GameObject("Noisemaker Handling Audio");
            audioHost.transform.SetParent(transform, false);
            audioHost.transform.localPosition = new Vector3(0.25f, 1.2f, 0.3f);
            EmitterAcoustics acoustics;
            if (IsOwner)
            {
                pool = new SfxPool(audioHost, 2, AudioCategory.OwnBody, 0f, 10f);
                acoustics = audioHost.AddComponent<EmitterAcoustics>();
                acoustics.occlude = false;
                acoustics.reflectionWeight = bank != null ? bank.ownBodyReflectionWeight : 0.35f;
            }
            else
            {
                pool = new SfxPool(audioHost, 2, AudioCategory.Players, 1f, 20f, customRolloff: AudioRouting.Rolloff(2f, 20f));
                acoustics = audioHost.AddComponent<EmitterAcoustics>();
                acoustics.ignoreRoot = transform;
                acoustics.maxRange = 22f;
            }
            acoustics.Attach(pool);
        }
        pool.Play(NoisemakerSounds.Resolve(bank, kind, out _), NoisemakerSounds.Volume(bank, kind), 1f);
    }

    // ---------- HUD and diagnostics ----------

    void OnGUI()
    {
        if (!IsSpawned || !IsOwner || !Alive) return;
        if (count.Value > 0 && RoundManager.IsActive)
        {
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 16 };
            GUI.Label(new Rect(20, Screen.height - 108, 420, 28), $"Noisemakers: {count.Value}/{capacity}    F: throw", style);
        }
        if (ShipAcoustics.DiagnosticsVisible && IsServer) DrawDiagnostics();
    }

    // F4, host only: what the host knows. Rebuilt twice a second, not every frame.
    void DrawDiagnostics()
    {
        if ((diagTimer -= Time.unscaledDeltaTime) <= 0f)
        {
            diagTimer = 0.5f;
            diag.Clear();
            diag.Append("NOISEMAKERS (host)  ");
            foreach (var p in all) diag.Append("player ").Append(p.OwnerClientId).Append(": ").Append(p.count.Value).Append("  ");
            diag.Append("\n");
            var creature = FindFirstObjectByType<CreatureAI>();
            foreach (var d in ThrownNoisemaker.All) d.Describe(diag, creature);
            diagText = diag.ToString();
        }
        diagStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 0.85f, 0.6f) } };
        GUI.Label(new Rect(20, 330, 1000, 160), diagText, diagStyle);
    }
}
