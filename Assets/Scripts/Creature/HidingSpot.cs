using System.Collections.Generic;
using UnityEngine;

public enum HideStance : byte { Any, Crouched }

/// <summary>
/// Hiding furniture (table, raised bed). The furniture itself is ordinary collider geometry that blocks sight like any solid.
/// On top of that it carries an authored concealment volume: a player whose body is inside it, in the required stance, is
/// harder to RECOGNISE (slower visual awareness). Concealment never hides anyone from a clear look and never sees through or
/// past a collider: the creature's body-sample rays are unchanged. It does nothing for hearing.
///
/// The creature's search-point choice never asks whether anyone is inside: occupancy is only evaluated per player, on the host,
/// by the creature's sight code (Qualifies / Resolve), and nothing here stores it.
/// </summary>
public class HidingSpot : MonoBehaviour
{
    public static readonly List<HidingSpot> All = new();

    public RoomVolume room;
    [Tooltip("Reachable (NavMesh) point just outside the furniture")] public Transform investigationPoint;
    [Tooltip("Reachable points at the openings to look in from; includes the investigation point")] public Transform[] inspectPoints = new Transform[0];
    [Tooltip("Centre of the space under the furniture, at floor level")] public Transform lookAt;
    [Tooltip("Floor footprint (x, z) of the furniture in metres, used to recognise a player seen crawling under it")] public Vector2 footprint = new(1.8f, 1.3f);

    [Header("Concealment volume (local space, drawn in the Scene view)")]
    [Tooltip("Set by Alien > Update Hiding Volumes. Untick it to have the volume regenerated from the footprint.")] public bool volumeConfigured;
    public Vector3 volumeCenter = new(0f, 0.575f, 0f);
    public Vector3 volumeSize = new(1.8f, 1.15f, 1.3f);
    [Tooltip("Stance the player needs while inside the volume")] public HideStance requiredStance = HideStance.Crouched;
    [Tooltip("The player's body centre must be this far inside the volume's sides to count as hiding here")] public float bodyInset = 0.2f;

    [Header("Effect on recognition")]
    [Tooltip("0 = no cover, 0.9 = very hard to recognise. Slows visual awareness; never blocks a clear, sustained look.")] [Range(0f, 0.9f)] public float visualConcealment = 0.7f;
    [Tooltip("Authored lighting approximation: below 1 is dim (slower recognition), above 1 is bright. No real light is sampled.")] [Range(0.25f, 1.25f)] public float lightVisibility = 1f;
    [Tooltip("Share of the concealment that is left while the creature deliberately inspects this spot from an opening")] [Range(0f, 1f)] public float inspectionResidual = 0.25f;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public Vector3 LookPoint => lookAt != null ? lookAt.position : transform.position;

    // The authored volume, or one derived from the footprint while it has not been configured.
    Vector3 Centre => volumeConfigured ? volumeCenter : new Vector3(0f, 0.575f, 0f);
    Vector3 Size => volumeConfigured ? volumeSize : new Vector3(footprint.x, 1.15f, footprint.y);

    /// <summary>True when the point lies over the furniture's footprint (plus margin), in the furniture's own orientation.</summary>
    public bool ContainsFootprint(Vector3 worldPoint, float margin)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint);
        return Mathf.Abs(local.x) <= footprint.x * 0.5f + margin && Mathf.Abs(local.z) <= footprint.y * 0.5f + margin;
    }

    /// <summary>
    /// Does a player with feet at <paramref name="feet"/> and this capsule height qualify as hiding here? The body must be well
    /// inside the volume (not beside it, not touching its edge) and in the required stance.
    /// </summary>
    public bool Qualifies(Vector3 feet, float height, bool crouched)
    {
        if (requiredStance == HideStance.Crouched && !crouched) return false;
        Vector3 local = transform.InverseTransformPoint(feet) - Centre;
        Vector3 half = Size * 0.5f;
        return SightRules.InsideVolume(local.x, local.z, half.x, half.z, bodyInset, local.y + Centre.y, local.y + Centre.y + height, Centre.y - half.y, Centre.y + half.y);
    }

    /// <summary>
    /// The one volume that applies where a player is: overlapping volumes never stack, the strongest wins, ties go to the lower
    /// instance id. Null when none qualifies. Evaluated per player by the host only.
    /// </summary>
    public static HidingSpot Resolve(Vector3 feet, float height, bool crouched)
    {
        eligible.Clear();
        strengths.Clear();
        keys.Clear();
        foreach (var h in All)
        {
            if (!h.Qualifies(feet, height, crouched)) continue;
            eligible.Add(h);
            strengths.Add(h.visualConcealment);
            keys.Add(h.GetInstanceID());
        }
        int i = SightRules.PickStrongest(strengths, keys);
        return i < 0 ? null : eligible[i];
    }

    static readonly List<HidingSpot> eligible = new();
    static readonly List<float> strengths = new();
    static readonly List<int> keys = new();

    /// <summary>The opening to look in from that is nearest to <paramref name="from"/> (the investigation point when none is listed).</summary>
    public Vector3 NearestOpening(Vector3 from)
    {
        Transform best = investigationPoint;
        float bestD = best != null ? Vector3.Distance(from, best.position) : float.MaxValue;
        foreach (var t in inspectPoints)
        {
            if (t == null) continue;
            float d = Vector3.Distance(from, t.position);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best != null ? best.position : transform.position;
    }

    /// <summary>True when <paramref name="pos"/> is physically at one of the openings (horizontal distance).</summary>
    public bool AtOpening(Vector3 pos, float tolerance)
    {
        Vector3 p = NearestOpening(pos);
        p.y = pos.y;
        return Vector3.Distance(pos, p) <= tolerance;
    }

    void OnDrawGizmos() => DrawVolume(0.25f);

    void OnDrawGizmosSelected()
    {
        DrawVolume(1f);
        Gizmos.color = Color.cyan;
        foreach (var t in inspectPoints) if (t != null) Gizmos.DrawWireSphere(t.position, 0.2f);
    }

    void DrawVolume(float alpha)
    {
        var old = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0f, 1f, alpha);
        Gizmos.DrawWireCube(Centre, Size);
        Gizmos.color = new Color(1f, 0f, 1f, alpha * 0.12f);
        Gizmos.DrawCube(Centre, Size);
        Gizmos.matrix = old;
    }
}
