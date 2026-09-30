using System.Collections.Generic;

// Pure visual-recognition rules with no Unity types, so they can be unit tested outside Unity (Editor/Tests/SightRulesTests.cs).

/// <summary>Why the creature found a player. Recorded on the host at the kill and shown to the victim as one tip.</summary>
public enum DetectionReason : byte { Unknown, SawEnterSpot, FootstepsLedHere, VisibleThroughOpening, FoundWhileInspecting, SeenInOpen }

/// <summary>How attentive the creature is, derived from its behaviour and alertness (never from any player's awareness).</summary>
public enum AttentionLevel : byte { Calm, Heightened, Searching, Inspecting }

public static class SightRules
{
    /// <summary>Concealment can never reach 1: a sustained clear view always reveals the player eventually.</summary>
    public const float MaxConcealment = 0.9f;

    /// <summary>
    /// Awareness gained per second. Zero without any unobstructed body sample. Otherwise: more exposed, closer and more central
    /// is faster; concealment and dim light are slower. A clearly exposed close player takes about <paramref name="recogniseSeconds"/>.
    /// </summary>
    public static float Rate(float exposure, float distance, float angleDeg, float sightDistance, float fieldOfView, float closeSense,
        float concealment, float light, float recogniseSeconds, float farFactor, float peripheralFactor)
    {
        if (exposure <= 0f || recogniseSeconds <= 0f) return 0f;
        bool close = distance <= closeSense;
        float far = close ? 0f : Clamp01((distance - closeSense) / System.Math.Max(0.01f, sightDistance - closeSense));
        float side = close ? 0f : Clamp01(angleDeg / System.Math.Max(1f, fieldOfView * 0.5f));
        float distanceFactor = 1f + (farFactor - 1f) * far;
        float angleFactor = 1f + (peripheralFactor - 1f) * side;
        float cover = 1f - System.Math.Min(MaxConcealment, System.Math.Max(0f, concealment));
        return Clamp01(exposure) * distanceFactor * angleFactor * cover * System.Math.Max(0f, light) / recogniseSeconds;
    }

    /// <summary>
    /// Base recognition time for a player hiding in an eligible volume, before distance, angle and exposure. The attention level
    /// sets it for a reference (standard) hiding place; better cover takes longer, poorer cover less. It never drops below
    /// <paramref name="exposedSeconds"/>, so hiding is never worse than standing in the open.
    /// </summary>
    public static float ConcealedSeconds(float attentionSeconds, float cover, float referenceCover, float exposedSeconds)
    {
        float c = System.Math.Min(MaxConcealment, System.Math.Max(0f, cover));
        float r = System.Math.Min(MaxConcealment, System.Math.Max(0f, referenceCover));
        return System.Math.Max(exposedSeconds, attentionSeconds * (1f - r) / System.Math.Max(0.05f, 1f - c));
    }

    /// <summary>
    /// Attention from what the creature is doing: a deliberate inspection, an active search or pursuit, a noise investigation or
    /// patrol that is still alert (heightened), or an unalerted patrol (calm). A sighting's own awareness is not an input, so a weak
    /// glimpse cannot raise its own recognition speed.
    /// </summary>
    public static AttentionLevel AttentionFor(bool inspecting, bool activelySearching, bool investigating, float alertness, float heightenedAt) =>
        inspecting ? AttentionLevel.Inspecting
        : activelySearching ? AttentionLevel.Searching
        : investigating || alertness >= heightenedAt ? AttentionLevel.Heightened
        : AttentionLevel.Calm;

    /// <summary>
    /// Is a player of this size inside an axis-aligned local volume (given as half extents and vertical range), with the body
    /// centre at least <paramref name="inset"/> inside the sides? Standing beside or barely touching the volume fails.
    /// </summary>
    public static bool InsideVolume(float localX, float localZ, float halfX, float halfZ, float inset, float feetY, float headY, float bottomY, float topY)
    {
        if (System.Math.Abs(localX) > halfX - inset || System.Math.Abs(localZ) > halfZ - inset) return false;
        return feetY >= bottomY - 0.3f && headY <= topY;
    }

    /// <summary>
    /// Overlapping volumes never stack: the single strongest one wins, ties go to the smaller key (a stable id), so the same
    /// position always gives the same answer. Returns the index, or -1 for none.
    /// </summary>
    public static int PickStrongest(IList<float> concealment, IList<int> tieKey)
    {
        int best = -1;
        for (int i = 0; i < concealment.Count; i++)
            if (best < 0 || concealment[i] > concealment[best] || (concealment[i] == concealment[best] && tieKey[i] < tieKey[best])) best = i;
        return best;
    }

    public static string Tip(DetectionReason reason) => reason switch
    {
        DetectionReason.SawEnterSpot => "It saw you enter this hiding spot.",
        DetectionReason.FootstepsLedHere => "Your footsteps led it towards you.",
        DetectionReason.VisibleThroughOpening => "You were visible through the opening.",
        DetectionReason.FoundWhileInspecting => "It found you while inspecting this spot.",
        DetectionReason.SeenInOpen => "It spotted you out in the open.",
        _ => "It caught you.",
    };

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

/// <summary>
/// One player's awareness in the creature's mind, 0..1 (host only). It grows only from real unobstructed samples, holds briefly
/// when they flicker out (edge jitter), then decays. It never says WHERE the player is: the destination of a chase is only
/// ever written from a confirmed sighting, which needs Recognised AND a sample this very tick.
/// </summary>
public sealed class AwarenessState
{
    public float Value { get; private set; }
    public float SinceSample { get; private set; } = 999f;

    public bool Recognised => Value >= 1f;

    public void Tick(float dt, float rate, bool hasSamples, float hold, float decaySeconds)
    {
        if (hasSamples)
        {
            SinceSample = 0f;
            Value = System.Math.Min(1f, Value + rate * dt);
            return;
        }
        SinceSample += dt;
        if (SinceSample > hold) Value = System.Math.Max(0f, Value - dt / System.Math.Max(0.01f, decaySeconds));
    }

    /// <summary>Recognised and still within the flicker hold: keeps a chase going without confirming a position.</summary>
    public bool Holding(float hold) => Recognised && SinceSample <= hold;

    public void Clear()
    {
        Value = 0f;
        SinceSample = 999f;
    }
}
