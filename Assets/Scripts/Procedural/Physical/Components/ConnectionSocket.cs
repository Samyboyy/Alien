using System.Collections.Generic;
using UnityEngine;

// The components of a physical room prefab. Placement, validation and the future gameplay systems read these (explicit references and enums),
// never names: a designer may rename any child without breaking anything.

/// <summary>A doorway in a room's outer wall: the standard connection-socket contract (see SocketTypes). Sits on the wall line, facing outwards (its forward vector points out of the room).</summary>
public class ConnectionSocket : MonoBehaviour
{
    [Tooltip("Stable within the room prefab; never changes once placed rooms exist.")]
    public string socketId = "";
    [Tooltip("The footprint cell the socket opens from (prefab-local cells, origin at the footprint's minimum corner).")]
    public Vector2Int cell;
    public GridSide side;
    public SocketWidth width;
    public SocketHeight height;
    public SocketConnectionType connectionType;
    public DoorPolicy doorPolicy = DoorPolicy.Permitted;
    public SocketSupport support = SocketSupport.Corridor | SocketSupport.Direct;

    [Header("Generated ship state")]
    public bool occupied;
    [Tooltip("Not connected to anything and closed with a wall module.")]
    public bool sealedOff;
    public string connectedRoom = "", connectedSocket = "";
    public ConnectionMode connectionMode;

    public SocketSpec ToSpec() => new()
    {
        id = socketId, cell = new Int2(cell.x, cell.y), side = side, width = width, height = height, type = connectionType, door = doorPolicy, support = support,
    };

    /// <summary>The doorway's middle in world space (on the wall line, at floor height).</summary>
    public Vector3 WorldPosition => transform.position;
    public Vector3 WorldForward => transform.forward;
}
