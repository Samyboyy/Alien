using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class ConnectionRecord
{
    public int edge;
    public string edgeId;
    public int nodeA, socketA, nodeB, socketB;
    public ConnectionMode mode;
    public Vector2Int[] cells = new Vector2Int[0];
}
