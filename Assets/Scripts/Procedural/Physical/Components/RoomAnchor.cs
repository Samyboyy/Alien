using System.Collections.Generic;
using UnityEngine;

/// <summary>A semantic point in a room that a future system will use: items, objectives, hiding places, creature search and patrol points, cameras, hazards, vents, player-safe spawns.</summary>
public class RoomAnchor : MonoBehaviour
{
    public AnchorKind kind;
    [Tooltip("For item anchors: what may be placed here.")]
    public ItemAnchorCategory itemCategory;
    public string anchorId = "";
    [Tooltip("For a door anchor: the connection socket it belongs to.")]
    public string socketId = "";
    [Tooltip("Free space around the anchor (metres).")]
    public float radius = 0.5f;

    [Header("Semantics (item and console anchors)")]
    public AnchorSemantic semantic;
    public AnchorContext context;
    public ItemClassMask allowedItems, forbiddenItems;
    [Tooltip("Relative likelihood among the anchors a placement rule accepts.")]
    public float weight = 1f;
    [Tooltip("Free volume (metres) standing on the anchor point: width, height, depth.")]
    public Vector3 clearance = new(0.4f, 0.35f, 0.4f);
    public RoomFeatures requiredFeature;
    public bool canHoldCritical;
    public NarrativeTag narrativeTags;
    [Tooltip("The furniture collider this anchor rests on or against (ignored when checking the clearance volume).")]
    public Collider support;

    /// <summary>The pure description of this anchor, with its position relative to the room prefab's root.</summary>
    public AnchorInfo ToInfo(Transform roomRoot)
    {
        var p = roomRoot.InverseTransformPoint(transform.position);
        return new AnchorInfo
        {
            kind = kind, semantic = semantic, id = anchorId, x = p.x, y = p.y, z = p.z, yaw = (Quaternion.Inverse(roomRoot.rotation) * transform.rotation).eulerAngles.y,
            context = context, allowed = allowedItems, forbidden = forbiddenItems, weight = weight, clearWidth = clearance.x, clearHeight = clearance.y, clearDepth = clearance.z,
            requiredFeature = requiredFeature, canHoldCritical = canHoldCritical, tags = narrativeTags, socketId = socketId,
        };
    }
}
