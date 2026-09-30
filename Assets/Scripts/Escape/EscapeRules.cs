using System.Collections.Generic;

// Pure game rules with no Unity types, so they can be unit tested (Editor/Tests/EscapeRulesTests.cs).

/// <summary>Round-wide objective progress, replicated by RoundManager. Reset at every round start.</summary>
[System.Flags]
public enum ShipFlags
{
    None = 0,
    FuseInstalled = 1 << 0,
    PowerRestored = 1 << 1,
}

public enum ItemKind : byte { None, Keycard, Fuse }

public enum PlayerStatus : byte { Alive, Dead, Escaped }

public static class EscapeRules
{
    // ---------- Hearing ----------

    /// <summary>0..1: full strength at the source, 0 at <paramref name="range"/> (measured as effective distance).</summary>
    public static float SoundStrength(float range, float effectiveDistance) =>
        range <= 0f ? 0f : System.Math.Max(0f, System.Math.Min(1f, 1f - effectiveDistance / range));

    /// <summary>Straight distance plus a fixed penalty per occluder. Occlusion muffles a sound; it never makes it inaudible by itself.</summary>
    public static float EffectiveDistance(float straight, int walls, int doors, int obstacles,
        float wallPenalty, float doorPenalty, float obstaclePenalty) =>
        straight + walls * wallPenalty + doors * doorPenalty + obstacles * obstaclePenalty;

    /// <summary>How far from the true spot a sound of this strength may place the evidence: exact up close, vague at the edge.</summary>
    public static float PositionError(float strength, float maxError) =>
        (1f - System.Math.Max(0f, System.Math.Min(1f, strength))) * maxError;

    /// <summary>A noise event is acted on once: newer than what was already handled, and not stale.</summary>
    public static bool IsFresh(ulong id, ulong handledUpTo, float ageSeconds, float staleSeconds) =>
        id > handledUpTo && ageSeconds >= 0f && ageSeconds < staleSeconds;

    // ---------- Evidence ----------

    /// <summary>Weight of remembered evidence: its strength, fading linearly to 0 over <paramref name="fadeSeconds"/>.</summary>
    public static float EvidenceScore(float strength, float ageSeconds, float fadeSeconds) =>
        fadeSeconds <= 0f ? 0f : strength * System.Math.Max(0f, 1f - ageSeconds / fadeSeconds);

    /// <summary>
    /// Whether a heard noise replaces the remembered evidence.
    /// Fresh footsteps/breathing of the player being followed always update the trail. Incidental sounds (doors, impacts,
    /// consoles) never erase a still-weighted player trail. Anything else must beat the current evidence by
    /// <paramref name="switchMargin"/> and wait out the switch cooldown, so two moving players cannot flip the target every tick.
    /// </summary>
    public static bool AcceptNoise(float currentScore, bool currentIsPlayerTrail, bool fromPursuedPlayer, bool incidental,
        float candidateStrength, float switchMargin, bool cooldownOver)
    {
        if (candidateStrength <= 0f) return false;
        if (currentScore <= 0f) return true; // nothing (left) to protect
        if (fromPursuedPlayer) return true;
        if (incidental && currentIsPlayerTrail) return false;
        return cooldownOver && candidateStrength > currentScore * switchMargin;
    }

    // ---------- Doors ----------

    /// <summary>
    /// A closed door can be the one blocking a route if it is within <paramref name="radius"/> of where we stopped and we and the
    /// destination lie on opposite sides of its plane (normal = walking direction through it). Returns the detour length
    /// (here -> door -> destination) for ranking, or -1 when the door cannot be the blocker.
    /// </summary>
    public static float DoorDetour(float hereX, float hereZ, float destX, float destZ,
        float doorX, float doorZ, float normalX, float normalZ, float radius)
    {
        float near = Dist(hereX, hereZ, doorX, doorZ);
        if (near > radius) return -1f;
        float sideHere = (hereX - doorX) * normalX + (hereZ - doorZ) * normalZ;
        float sideDest = (destX - doorX) * normalX + (destZ - doorZ) * normalZ;
        if (sideHere * sideDest >= 0f) return -1f; // same side (or exactly in the plane): not in the way
        return near + Dist(doorX, doorZ, destX, destZ);
    }

    static float Dist(float ax, float az, float bx, float bz) =>
        (float)System.Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    // ---------- Objectives ----------

    /// <summary>Keeps boarding order: drops players no longer inside, then appends new arrivals.</summary>
    public static void UpdateBoarding(List<ulong> order, ICollection<ulong> insideNow)
    {
        order.RemoveAll(id => !insideNow.Contains(id));
        foreach (var id in insideNow)
            if (!order.Contains(id)) order.Add(id);
    }

    /// <summary>Only the first <paramref name="capacity"/> players aboard launch.</summary>
    public static bool HasSeat(IList<ulong> order, ulong id, int capacity)
    {
        int i = order.IndexOf(id);
        return i >= 0 && i < capacity;
    }

    /// <summary>All required flags set, and the required item carried (None = no item needed).</summary>
    public static bool Meets(ShipFlags required, ShipFlags have, ItemKind requiredItem, ItemKind held) =>
        (have & required) == required && (requiredItem == ItemKind.None || held == requiredItem);

    /// <summary>
    /// Round ends when nobody is still in play, or, in a level with pods, once every pod has gone
    /// (anyone still aboard the ship is left behind).
    /// </summary>
    public static bool IsRoundOver(int participants, int alive, int pods, int podsLaunched) =>
        participants > 0 && (alive == 0 || (pods > 0 && podsLaunched == pods));
}
