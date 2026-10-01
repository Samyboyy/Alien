using System.Collections.Generic;
using UnityEngine;

/// <summary>The room's box used for overlap checks and audio/reverb zoning, in the prefab's local space.</summary>
public class RoomBounds : MonoBehaviour
{
    public Vector3 center, size;

    public Bounds WorldBounds()
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = center + Vector3.Scale(size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var w = transform.TransformPoint(corner);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        var b = new Bounds();
        b.SetMinMax(min, max);
        return b;
    }
}
