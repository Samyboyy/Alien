using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>On a room instance: the logical node it realises and where it was placed.</summary>
public class PlacedRoom : MonoBehaviour
{
    public int nodeIndex;
    public string nodeId = "", definitionId = "", variantId = "";
    public RoomCategory category;
    public ShipSector sector;
    public int deck;
    public int rotation;
    public Vector2Int originCell;
    public ShipRoomPrefab room;
}
