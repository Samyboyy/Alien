using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A room for the creature's search: a floor rectangle (centred on this transform, size in metres), its ordinary search
/// points, its hiding places, and the rooms directly connected to it. Not a world model: corridors have no volume and are
/// handled by "nearest room". Registered while enabled, so no scene scan is needed.
/// </summary>
public class RoomVolume : MonoBehaviour
{
    public static readonly List<RoomVolume> All = new();

    public string roomName;
    [Tooltip("Floor footprint (x, z) in metres; height is ignored")] public Vector3 size = new(10f, 4f, 10f);
    public RoomVolume[] neighbours = new RoomVolume[0];
    public Transform[] searchPoints = new Transform[0];
    public HidingSpot[] hidingSpots = new HidingSpot[0];

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public bool Contains(Vector3 p) => DistanceTo(p) <= 0f;

    /// <summary>Horizontal distance from the point to the rectangle (0 when inside).</summary>
    public float DistanceTo(Vector3 p)
    {
        Vector3 d = p - transform.position;
        float dx = Mathf.Max(0f, Mathf.Abs(d.x) - size.x * 0.5f);
        float dz = Mathf.Max(0f, Mathf.Abs(d.z) - size.z * 0.5f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * 0.1f, new Vector3(size.x, 0.2f, size.z));
        Gizmos.color = Color.white;
        foreach (var t in searchPoints) if (t != null) Gizmos.DrawWireSphere(t.position, 0.25f);
        Gizmos.color = Color.cyan;
        foreach (var n in neighbours) if (n != null) Gizmos.DrawLine(transform.position + Vector3.up, n.transform.position + Vector3.up);
    }
}
