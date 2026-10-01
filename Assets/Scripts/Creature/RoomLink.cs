using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One connection between two rooms: where to stand on each side, the door(s) in between (in order from room A to room B), and
/// whether the creature may use it. Registered while enabled, so the creature never searches the scene for connections.
/// A connection is usable only if every door on it can be opened by the creature (a reinforced door, creatureCanOpen = false,
/// makes it impassable). Built by Alien > Add Creature Ventilation from the ship map.
/// </summary>
public class RoomLink : MonoBehaviour
{
    public static readonly List<RoomLink> All = new();

    [Tooltip("Stable identifier: ties between equal choices go to the lower id")] public int id;
    public RoomVolume roomA, roomB;
    [Tooltip("Reachable point on room A's side, before the first door")] public Transform pointA;
    [Tooltip("Reachable point on room B's side, past the last door")] public Transform pointB;
    [Tooltip("Doors on this connection, in order from room A to room B (empty for an open doorway)")] public SlidingDoor[] doors = new SlidingDoor[0];
    [Tooltip("Untick to forbid the creature from using this connection")] public bool creatureAllowed = true;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>The creature may use it: permitted, both rooms set, and every door can be opened by it.</summary>
    public bool Allowed
    {
        get
        {
            if (!creatureAllowed || roomA == null || roomB == null) return false;
            foreach (var d in doors) if (d != null && !d.creatureCanOpen) return false;
            return true;
        }
    }

    public RoomVolume Other(RoomVolume room) => room == roomA ? roomB : roomA;

    void OnDrawGizmos()
    {
        if (pointA == null || pointB == null) return;
        Gizmos.color = Allowed ? new Color(0.3f, 1f, 0.4f, 0.8f) : new Color(1f, 0.3f, 0.3f, 0.8f);
        Gizmos.DrawLine(pointA.position + Vector3.up * 0.3f, pointB.position + Vector3.up * 0.3f);
        Gizmos.DrawWireSphere(pointA.position + Vector3.up * 0.3f, 0.2f);
        Gizmos.DrawWireSphere(pointB.position + Vector3.up * 0.3f, 0.2f);
        foreach (var d in doors) if (d != null) Gizmos.DrawWireCube(d.transform.position, d.transform.lossyScale * 1.05f);
    }
}
