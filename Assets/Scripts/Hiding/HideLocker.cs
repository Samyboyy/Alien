using System.Text;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// An enterable locker: much stronger cover than furniture, with risks of its own. Placeholder greybox; the visuals live under a "Model"
/// child so final art can replace them without touching the anchors or this script.
///
/// AUTHORITY. Everything is decided by the host. A client only asks (E on the locker, E to leave, Space for breath). The host owns the
/// occupant (one at most), the door state and the inspection flag; the occupant's player object owns nothing about a locker but a reference.
/// The door is replicated as a small state (closed, open, held open for an inspection) that every peer animates itself, so nothing is sent
/// per frame, and a late joiner simply starts from the current state without any sound being replayed.
///
/// THE CREATURE NEVER READS WHO IS INSIDE. It sees a locker only as a HidingSpot in its room's list (chosen by place, budget and history like
/// any other), and uses this component only to open the door for an inspection (BeginInspection / EndInspection). Occupancy is for networking,
/// prompts and developer diagnostics; nothing under Assets/Scripts/Creature may use it (a test checks the source).
///
/// The closed door has a collider that blocks every sight ray and reach line, so nobody can be recognised or caught through it. The viewing
/// slit is visual only (for the occupant). While the creature holds the door open for a committed inspection the occupant cannot leave: the
/// alternative is walking out through the creature.
/// </summary>
public class HideLocker : NetworkBehaviour, IInteractable, IRoundResettable
{
    [Header("Anchors (children; the final model can move around them)")]
    [Tooltip("Where a player stands to use it (chest height, in front of the door)")] public Transform entryPoint;
    [Tooltip("Feet position of the hidden player (also their facing)")] public Transform hiddenBody;
    [Tooltip("The hidden player's camera (looks out through the door)")] public Transform hiddenCamera;
    [Tooltip("Feet position of the player stepping out")] public Transform exitPoint;
    [Tooltip("The sliding door panel (moves by doorOpenOffset)")] public Transform doorPanel;
    [Tooltip("Colliders of the door: they block sight and reach while it is closed")] public Collider[] doorColliders = new Collider[0];
    [Tooltip("Where the creature stands to inspect it")] public Transform inspectPoint;
    [Tooltip("What the creature looks at")] public Transform lookAt;
    [Tooltip("Spare anchors for future animations (hand, latch, hinge...)")] public Transform[] futureAnchors = new Transform[0];

    [Header("Door")]
    [Tooltip("Local slide of the door when open")] public Vector3 doorOpenOffset = new(0.98f, 0f, 0f);
    public float doorSeconds = 0.4f;
    [Tooltip("After an entry or exit the door closes again after this long")] public float closeDelay = 0.9f;

    [Header("Use")]
    [Tooltip("The player's eye must be this close to the entry point (m), with a clear line")] public float entryReach = 3.75f;
    [Tooltip("The occupant cannot leave in the first seconds (stops flicking in and out)")] public float minHiddenSeconds = 0.8f;
    [Tooltip("A player cannot enter again for this long after leaving any locker (s)")] public float reenterCooldown = 1.5f;
    [Tooltip("Outer range (m) of the door noise when somebody gets in or out")] public float doorNoise = 4f;
    [Tooltip("Exit clearance: radius and height of the capsule that must be free at the exit (m)")] public float exitRadius = 0.3f;
    public float exitHeight = 1.7f;

    [Header("Looking out (degrees from straight ahead)")]
    public float yawLimit = 35f;
    public float pitchLimit = 25f;

    [Header("Safety")]
    [Tooltip("The creature's inspection is ended after this long whatever happens to the creature (s)")] public float maxInspectSeconds = 8f;

    enum Door : byte { Closed, Open, HeldOpen }

    readonly NetworkVariable<ulong> occupant = new(0); // client id + 1, 0 = nobody (networking and diagnostics only)
    readonly NetworkVariable<Door> door = new(Door.Closed);
    readonly LockerOccupancy occupancy = new();
    Vector3 doorClosedLocal;
    float open01, spawnedAt;
    double closeAt, enteredAt, inspectStart, nextCheck;
    bool inspecting;
    SfxPool pool;
    static readonly Collider[] overlap = new Collider[16];
    string exitNote = "-";
    GUIStyle diagStyle;

    /// <summary>Networking and developer diagnostics ONLY. The creature never reads this.</summary>
    public ulong OccupantId => occupant.Value;
    public bool IsOccupied => occupant.Value != 0;
    public bool InspectionHoldsDoor => inspecting;
    public bool DoorIsOpen => door.Value != Door.Closed;

    public bool AnchorsValid => entryPoint != null && hiddenBody != null && hiddenCamera != null && exitPoint != null && doorPanel != null && inspectPoint != null && lookAt != null;

    void Awake()
    {
        if (doorPanel != null) doorClosedLocal = doorPanel.localPosition;
    }

    public override void OnNetworkSpawn()
    {
        spawnedAt = Time.time;
        door.OnValueChanged += OnDoor;
        ApplyDoorImmediately();
    }

    public override void OnNetworkDespawn()
    {
        door.OnValueChanged -= OnDoor;
        if (IsServer && occupancy.Occupied && !NetworkManager.ShutdownInProgress) ReleaseOccupant(); // never leave a player held in a locker that is going away
        if (pool != null) pool.StopAll();
        pool = null;
    }

    // ---------- Interaction (host decides) ----------

    public string GetPrompt()
    {
        if (!RoundManager.IsActive) return null;
        if (PlayerHidingLocal()) return null;
        return IsOccupied ? "Locker occupied" : "E — Hide in the locker";
    }

    static bool PlayerHidingLocal()
    {
        var nm = NetworkManager.Singleton;
        var local = nm != null && nm.IsListening ? nm.LocalClient?.PlayerObject : null;
        return local != null && local.TryGetComponent(out PlayerHiding h) && h.IsHidden;
    }

    public bool CanInteract(NetworkFirstPersonController player) => CheckEntry(player) == EntryResult.Ok;

    public void Interact(NetworkFirstPersonController player)
    {
        if (!IsServer || CheckEntry(player) != EntryResult.Ok) return;
        if (!player.TryGetComponent(out PlayerHiding hiding) || !occupancy.TryEnter(player.OwnerClientId + 1)) return; // two at once: the second fails here
        occupant.Value = occupancy.Occupant;
        enteredAt = Time.timeAsDouble;
        door.Value = Door.Open;
        closeAt = enteredAt + closeDelay;
        hiding.ServerEnter(this);
        NoiseSystem.Emit(transform.position, doorNoise, "locker", SoundKind.Door);
    }

    EntryResult CheckEntry(NetworkFirstPersonController player)
    {
        if (!IsServer || player == null || !player.TryGetComponent(out PlayerHiding hiding)) return EntryResult.NotInPlay;
        var life = player.GetComponent<PlayerLife>();
        float distance = float.NaN;
        bool clear = false;
        if (entryPoint != null && player.playerCamera != null)
        {
            Vector3 eye = player.playerCamera.transform.position;
            distance = Vector3.Distance(eye, entryPoint.position);
            clear = !Physics.Linecast(eye, entryPoint.position, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || hit.collider.transform.IsChildOf(transform) || hit.collider.GetComponentInParent<NetworkFirstPersonController>() == player;
        }
        return LockerRules.ValidateEntry(true, life == null || life.IsAlive, RoundManager.IsActive, occupancy.Occupied, distance, entryReach, clear,
            hiding.IsHidden, inspecting, AnchorsValid, Time.timeAsDouble, hiding.NextEntryAt);
    }

    /// <summary>Host: the occupant leaves (validated by PlayerHiding). Opens the door, makes a small noise, frees the locker.</summary>
    public void ServerExit(PlayerHiding who)
    {
        if (!IsServer || !occupancy.Release(who.OwnerClientId + 1)) return;
        occupant.Value = 0;
        door.Value = Door.Open;
        closeAt = Time.timeAsDouble + closeDelay;
        NoiseSystem.Emit(transform.position, doorNoise, "locker", SoundKind.Door);
        who.ServerLeave(reenterCooldown);
    }

    /// <summary>Host: whatever is inside is released at once (death, escape, disconnect, round end, reset, despawn).</summary>
    public void ReleaseOccupant()
    {
        if (!IsServer) return;
        ulong id = occupancy.Occupant;
        if (!occupancy.ForceRelease()) return;
        occupant.Value = 0;
        if (id != 0 && NetworkManager != null && NetworkManager.ConnectedClients.TryGetValue(id - 1, out var client) && client.PlayerObject != null
            && client.PlayerObject.TryGetComponent(out PlayerHiding hiding))
            hiding.ServerLeave(0f);
        if (!inspecting && door.Value != Door.Closed) { door.Value = Door.Closed; closeAt = 0; }
    }

    /// <summary>Host: is the exit free of other players and solids? The reason is kept for the diagnostics and the player's message.</summary>
    public bool ExitClear(Transform ignorePlayer)
    {
        if (exitPoint == null) { exitNote = "no exit anchor"; return false; }
        Vector3 p = exitPoint.position;
        int n = Physics.OverlapCapsuleNonAlloc(p + Vector3.up * (exitRadius + 0.05f), p + Vector3.up * (exitHeight - exitRadius), exitRadius, overlap,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            var c = overlap[i];
            if (c.transform.IsChildOf(transform) || (ignorePlayer != null && c.transform.IsChildOf(ignorePlayer))) continue;
            exitNote = $"blocked by {c.name}";
            return false;
        }
        exitNote = "clear";
        return true;
    }

    // ---------- The creature's inspection (it never learns who is inside) ----------

    /// <summary>Host: the creature opens the door and holds it open. The occupant (if any) cannot leave and nobody can get in until it ends.</summary>
    public void BeginInspection()
    {
        if (!IsServer) return;
        inspecting = true;
        inspectStart = Time.timeAsDouble;
        door.Value = Door.HeldOpen;
    }

    /// <summary>Host: the creature has finished (or was interrupted): the door closes.</summary>
    public void EndInspection()
    {
        if (!IsServer || !inspecting) return;
        inspecting = false;
        door.Value = Door.Closed;
    }

    // ---------- Round ----------

    public void ResetForRound(System.Random rng)
    {
        if (!IsServer) return;
        inspecting = false;
        ReleaseOccupant();
        door.Value = Door.Closed;
    }

    public void OnRoundOver() => ReleaseOccupant();

    // ---------- Door (every peer animates the replicated state) ----------

    void Update()
    {
        if (IsSpawned && IsServer) ServerTick();
        float target = door.Value != Door.Closed ? 1f : 0f;
        if (!Mathf.Approximately(open01, target) && doorPanel != null)
        {
            open01 = Mathf.MoveTowards(open01, target, Time.deltaTime / Mathf.Max(0.05f, doorSeconds));
            doorPanel.localPosition = doorClosedLocal + doorOpenOffset * (open01 * open01 * (3f - 2f * open01));
        }
    }

    void ServerTick()
    {
        double now = Time.timeAsDouble;
        if (door.Value == Door.Open && now >= closeAt) door.Value = Door.Closed;
        if (inspecting && now - inspectStart > maxInspectSeconds) EndInspection(); // the creature went away mid-inspection
        if (now < nextCheck) return;
        nextCheck = now + 0.25;
        if (!occupancy.Occupied) return;
        // The occupant must still be a connected living player in a running round, else they are released.
        bool exists = NetworkManager.ConnectedClients.TryGetValue(occupancy.Occupant - 1, out var c) && c.PlayerObject != null;
        bool ok = RoundManager.IsActive && exists && c.PlayerObject.TryGetComponent(out PlayerLife life) && life.IsAlive;
        if (!exists) DevChecks.Report($"{name} claimed occupant player {occupancy.Occupant - 1}, who is not connected (their despawn should have released it)", this);
        if (!ok) ReleaseOccupant();
    }

    void OnDoor(Door previous, Door now)
    {
        ApplyColliders(now);
        if (!LockerRules.DoorSoundWanted(Time.time - spawnedAt)) return; // a late joiner's first value is state, not an event: no sound
        PlayDoorSound(now != Door.Closed);
    }

    void ApplyDoorImmediately()
    {
        open01 = door.Value != Door.Closed ? 1f : 0f;
        if (doorPanel != null) doorPanel.localPosition = doorClosedLocal + doorOpenOffset * open01;
        ApplyColliders(door.Value);
    }

    // Closed: the door blocks every sight ray and reach line. Open: it does not (and a player stepping in or out is never caught on it).
    void ApplyColliders(Door state)
    {
        foreach (var c in doorColliders) if (c != null) c.enabled = state == Door.Closed;
    }

    void PlayDoorSound(bool opening)
    {
        var bank = AudioBank.Get();
        if (bank == null || bank.doorSound == null) return;
        if (pool == null)
        {
            pool = new SfxPool(gameObject, 1, AudioCategory.World, 1f, bank.doorMaxDistance, customRolloff: AudioRouting.Rolloff(bank.doorFullDistance, bank.doorMaxDistance));
            var acoustics = gameObject.AddComponent<EmitterAcoustics>();
            acoustics.ignoreRoot = transform;
            acoustics.maxRange = bank.doorMaxDistance + 2f;
            acoustics.Attach(pool);
        }
        pool.Play(bank.doorSound, bank.doorVolume * 0.6f, opening ? 1.25f : 1.15f); // a lighter, higher metal door than the ship's doors
    }

    // ---------- Diagnostics (developer only; never read by the creature) ----------

    public void Describe(StringBuilder sb)
    {
        sb.Append(name).Append(": ").Append(door.Value).Append(inspecting ? " (inspection)" : "").Append(", occupant ")
            .Append(occupant.Value == 0 ? "none" : $"player {occupant.Value - 1}").Append(", exit ").Append(exitNote);
    }

    void OnGUI()
    {
        if (!IsSpawned || !IsServer || !ShipAcoustics.DiagnosticsVisible || Camera.allCamerasCount == 0) return;
        var cam = Camera.allCameras[0];
        Vector3 sp = cam.WorldToScreenPoint(transform.position + Vector3.up * 2.3f);
        if (sp.z <= 0f || sp.z > 25f) return;
        diagStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(1f, 0.9f, 0.5f) } };
        var sb = new StringBuilder();
        Describe(sb);
        GUI.Label(new Rect(sp.x - 160, Screen.height - sp.y, 330, 40), sb.ToString(), diagStyle);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        if (entryPoint != null) Gizmos.DrawWireSphere(entryPoint.position, 0.2f);
        Gizmos.color = Color.green;
        if (hiddenBody != null) Gizmos.DrawWireCube(hiddenBody.position + Vector3.up * 0.9f, new Vector3(0.7f, 1.8f, 0.7f));
        if (hiddenCamera != null)
        {
            Gizmos.DrawWireSphere(hiddenCamera.position, 0.1f);
            Vector3 f = hiddenCamera.forward;
            foreach (float yaw in new[] { -yawLimit, yawLimit })
                Gizmos.DrawLine(hiddenCamera.position, hiddenCamera.position + Quaternion.AngleAxis(yaw, hiddenCamera.up) * f * 1.5f);
        }
        Gizmos.color = Color.yellow;
        if (exitPoint != null)
        {
            Gizmos.DrawWireSphere(exitPoint.position + Vector3.up * exitRadius, exitRadius);
            Gizmos.DrawWireSphere(exitPoint.position + Vector3.up * (exitHeight - exitRadius), exitRadius);
        }
        Gizmos.color = Color.magenta;
        if (inspectPoint != null) Gizmos.DrawWireSphere(inspectPoint.position, 0.25f);
        if (lookAt != null) Gizmos.DrawWireSphere(lookAt.position, 0.1f);
        Gizmos.color = Color.white;
        foreach (var a in futureAnchors) if (a != null) Gizmos.DrawWireCube(a.position, Vector3.one * 0.06f);
    }
}
