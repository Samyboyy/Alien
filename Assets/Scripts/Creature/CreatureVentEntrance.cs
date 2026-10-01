using UnityEngine;

/// <summary>
/// A creature-only vent mouth in a room's wall. The creature walks to <see cref="approach"/> (a NavMesh point on the floor in front of
/// it), then travels face -> inside -> top. <see cref="inside"/> is inside the wall, out of sight: it is where it waits and warns before
/// emerging. <see cref="top"/> is the entrance's node in the duct network. Players cannot use these.
/// </summary>
public class CreatureVentEntrance : MonoBehaviour
{
    [Tooltip("Stable identifier (also its node index in the network)")] public int id;
    public RoomVolume room;
    [Tooltip("NavMesh floor point in front of the vent: where the creature enters and where it emerges")] public Transform approach;
    [Tooltip("On the wall surface, at the mouth")] public Transform face;
    [Tooltip("Inside the wall, hidden: the pre-exit point")] public Transform inside;
    [Tooltip("The entrance's node at duct level")] public Transform top;
    [Tooltip("Untick to take this entrance out of use")] public bool usable = true;

    void OnDrawGizmos()
    {
        if (approach == null || face == null || inside == null || top == null) return;
        Gizmos.color = usable ? new Color(0.2f, 0.9f, 1f) : Color.gray;
        Gizmos.DrawWireSphere(approach.position + Vector3.up * 0.1f, 0.3f);
        Gizmos.DrawLine(approach.position + Vector3.up * 0.1f, face.position);
        Gizmos.DrawLine(face.position, inside.position);
        Gizmos.DrawLine(inside.position, top.position);
        Gizmos.DrawWireCube(face.position, new Vector3(0.5f, 0.5f, 0.5f));
    }
}
