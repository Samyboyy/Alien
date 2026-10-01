using System.Collections.Generic;

// Pure placement rules for a noisemaker lying on the floor (no Unity types; Editor/Tests/NoisemakerTests.cs). The editor setup gathers the
// colliders around a candidate spot into PlacementBox values and asks these rules what is supporting the pickup and what is in its way.

/// <summary>One collider's world bounds, as the editor setup sees it.</summary>
public struct PlacementBox
{
    public string name, kind;
    public float cx, cy, cz, hx, hy, hz; // centre and half extents
    public bool trigger;
    public bool own; // the candidate's own marker or the pickup itself
    public int layer;

    public float Top => cy + hy;
    public float Bottom => cy - hy;
}

public sealed class PlacementResult
{
    public bool supported;
    public string support = "none";
    public readonly List<PlacementBox> blockers = new();
    public readonly List<string> ignored = new();
    public bool Valid => supported && blockers.Count == 0;
}

public static class NoisemakerPlacement
{
    /// <summary>
    /// <paramref name="px"/>, <paramref name="pz"/>: the pickup's centre in plan; <paramref name="bottom"/> and <paramref name="top"/>: the
    /// height range it occupies; <paramref name="halfX"/>, <paramref name="halfZ"/>: its half size in plan. <paramref name="tolerance"/>
    /// is the skin (m): contact inside it is not an overlap. A surface whose top is no higher than the pickup's bottom plus the tolerance, and
    /// no deeper than <paramref name="reach"/> below it, is the SUPPORT (the floor): it is required, and it never counts as blocking.
    /// Triggers and the candidate's own hierarchy are ignored. Everything else that overlaps the pickup's volume is a blocker.
    /// </summary>
    public static PlacementResult Evaluate(float px, float pz, float halfX, float halfZ, float bottom, float top, float tolerance, float reach, IList<PlacementBox> boxes)
    {
        var r = new PlacementResult();
        foreach (var b in boxes)
        {
            if (b.trigger) { r.ignored.Add($"{b.name} (trigger)"); continue; }
            if (b.own) { r.ignored.Add($"{b.name} (own)"); continue; }
            bool coversPlan = System.Math.Abs(b.cx - px) <= b.hx && System.Math.Abs(b.cz - pz) <= b.hz;
            if (coversPlan && b.Top <= bottom + tolerance && b.Top >= bottom - reach)
            {
                if (!r.supported) { r.supported = true; r.support = b.name; }
                continue; // the floor under it: contact is not an obstruction
            }
            bool overlaps = System.Math.Abs(b.cx - px) < b.hx + halfX - tolerance && System.Math.Abs(b.cz - pz) < b.hz + halfZ - tolerance
                && b.Top > bottom + tolerance && b.Bottom < top - tolerance;
            if (overlaps) r.blockers.Add(b);
        }
        return r;
    }

    /// <summary>One line naming a blocker precisely, for the setup log.</summary>
    public static string Describe(PlacementBox b) =>
        $"'{b.name}' ({b.kind}, layer {b.layer}, trigger {b.trigger}) at ({b.cx:0.00}, {b.cy:0.00}, {b.cz:0.00}) size ({b.hx * 2:0.00}, {b.hy * 2:0.00}, {b.hz * 2:0.00})";
}
