using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// An acoustic area of the ship: an axis-aligned box (centred on this transform) and the kind of space it is. Where zones overlap the
/// higher priority wins (a pod inside a room, the crawlspace). Registered while enabled, so the listener never searches the scene.
/// Built by Alien > Add Ship Atmosphere from the ship map; the corridors, which have no room volume, get zones too.
/// </summary>
public class ShipAcousticZone : MonoBehaviour
{
    public static readonly List<ShipAcousticZone> All = new();

    public AcousticSpace space = AcousticSpace.SmallRoom;
    [Tooltip("Higher wins where zones overlap")] public int priority;
    [Tooltip("Box size in metres, centred on this transform")] public Vector3 size = new(10f, 4.5f, 10f);

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public bool Contains(Vector3 p)
    {
        Vector3 d = p - transform.position;
        return Mathf.Abs(d.x) <= size.x * 0.5f && Mathf.Abs(d.y) <= size.y * 0.5f && Mathf.Abs(d.z) <= size.z * 0.5f;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = space switch
        {
            AcousticSpace.LargeMachinery => new Color(1f, 0.6f, 0.2f, 0.8f),
            AcousticSpace.Corridor => new Color(0.4f, 0.7f, 1f, 0.8f),
            AcousticSpace.Compartment => new Color(0.8f, 0.4f, 1f, 0.8f),
            AcousticSpace.Crawlspace => new Color(1f, 0.3f, 0.3f, 0.8f),
            AcousticSpace.SmallRoom => new Color(0.4f, 1f, 0.6f, 0.8f),
            _ => new Color(0.7f, 0.7f, 0.7f, 0.8f),
        };
        Gizmos.DrawWireCube(transform.position, size);
    }
}
