using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Host-authoritative sliding door. The host owns the open/closed target and moves the door;
/// a server-authority NetworkTransform replicates the position (late joiners get the current pose).
/// Clients only read IsOpen. Needs NetworkObject + NetworkTransform + BoxCollider on the same object.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class SlidingDoor : NetworkBehaviour, IRoundResettable
{
    [Tooltip("World-space slide from the closed position")] public Vector3 openOffset = new(0f, 0f, 3f);
    public float speed = 2f;
    public float blockMargin = 0.1f;
    [Tooltip("Outer range (m) of the door-movement noise event, emitted when toggled")] public float noiseLoudness = 10f;
    [Tooltip("Creature may force this door open. Turn off only for a deliberately reinforced barrier; power/keycard requirements do not imply it.")] public bool creatureCanOpen = true;

    readonly NetworkVariable<bool> isOpen = new(false);
    static readonly Collider[] overlap = new Collider[16];
    BoxCollider box;
    Vector3 closedPos;

    public bool IsOpen => isOpen.Value;
    /// <summary>Host only: still sliding towards its open/closed position.</summary>
    public bool IsMoving => transform.position != Target;
    public Vector3 ClosedPosition => closedPos;
    /// <summary>Host: 0 = closed, 1 = fully open (distance slid along its opening travel). Used to tell when the gap is wide enough.</summary>
    public float OpenFraction => openOffset.sqrMagnitude < 0.0001f ? 1f : Mathf.Clamp01(Vector3.Distance(transform.position, closedPos) / openOffset.magnitude);
    /// <summary>Horizontal direction through the thin side of the slab (walking direction). Used to tell which side of the door a point is on.</summary>
    public Vector3 Normal => transform.lossyScale.x <= transform.lossyScale.z ? transform.right : transform.forward;
    Vector3 Target => closedPos + (isOpen.Value ? openOffset : Vector3.zero);

    void Awake()
    {
        box = GetComponent<BoxCollider>();
        closedPos = transform.position; // scene position = closed
    }

    DoorAudio doorAudio;

    public override void OnNetworkSpawn()
    {
        // Sound on every peer, from the replicated open/closed change (cosmetic; only when an AudioBank exists).
        if (AudioBank.Get() != null)
        {
            doorAudio = GetComponent<DoorAudio>() ?? gameObject.AddComponent<DoorAudio>();
            isOpen.OnValueChanged += OnOpenChanged;
        }
        if (!IsServer) return;
        isOpen.Value = false; // fresh session starts closed
        transform.position = closedPos;
    }

    public override void OnNetworkDespawn() => isOpen.OnValueChanged -= OnOpenChanged;

    // Not during a round reset or in the lobby, so a restart does not slam every door at once.
    void OnOpenChanged(bool was, bool now)
    {
        if (doorAudio == null || (RoundManager.Instance != null && !RoundManager.IsActive)) return;
        doorAudio.Play(now);
    }

    /// <summary>Host only. Snap closed for a round reset.</summary>
    public void ResetDoor()
    {
        if (!IsServer) return;
        isOpen.Value = false;
        transform.position = closedPos;
        GetComponent<NetworkTransform>().Teleport(closedPos, transform.rotation, transform.localScale);
    }

    void IRoundResettable.ResetForRound(System.Random rng) => ResetDoor();

    /// <summary>Host only. Refuses while moving or when a player stands where the door would close.</summary>
    public bool TryToggle()
    {
        if (!IsServer || IsMoving) return false;
        if (isOpen.Value && PlayerInBox(closedPos)) return false;
        isOpen.Value = !isOpen.Value;
        NoiseSystem.Emit(transform.position, noiseLoudness, isOpen.Value ? "door opened" : "door closed", SoundKind.Door);
        return true;
    }

    /// <summary>
    /// Host only. The creature forces the door open. Not permanent: players can close it again. Silent, so the creature
    /// does not chase its own noise. Refused (false) only for doors set to creatureCanOpen = false. Only moves the door: it sets no ship flag, grants no item and satisfies no pod requirement. Ignores the switch, never the obstruction checks
    /// that stop a door closing on someone.
    /// </summary>
    public bool ForceOpen()
    {
        if (!IsServer || !creatureCanOpen) return false;
        isOpen.Value = true;
        return true;
    }

    void Update()
    {
        if (!IsServer) return;
        // A player stepped into the closing door: reverse instead of crushing them.
        if (!isOpen.Value && transform.position != Target && PlayerInBox(transform.position))
            isOpen.Value = true;
        transform.position = Vector3.MoveTowards(transform.position, Target, speed * Time.deltaTime);
    }

    // Box computed from the transform, not collider.bounds, so it is never a frame stale.
    bool PlayerInBox(Vector3 doorPos)
    {
        Vector3 center = doorPos + transform.rotation * Vector3.Scale(box.center, transform.lossyScale);
        Vector3 half = Vector3.Scale(box.size * 0.5f, transform.lossyScale) + Vector3.one * blockMargin;
        int n = Physics.OverlapBoxNonAlloc(center, half, overlap, transform.rotation, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (overlap[i].GetComponentInParent<NetworkFirstPersonController>() != null
                || overlap[i].GetComponentInParent<CreatureAI>() != null) return true; // players and the creature
        return false;
    }
}
