using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>On a corridor module instance.</summary>
public class PlacedCorridor : MonoBehaviour
{
    public Vector2Int cell;
    public CorridorPieceKind kind;
    public int rotation;
    public string edgeId = "";
    [Tooltip("2 where two corridors cross at a Cross module.")]
    public int owners = 1;
    public int deck;
}
