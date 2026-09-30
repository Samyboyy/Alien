// Pure math for the local threat vignette (no Unity types, unit tested in Editor/Tests/ThreatMathTests.cs).
// Purely cosmetic: nothing here feeds back into detection, hearing or concealment.

public static class ThreatMath
{
    /// <summary>
    /// Stress 0..1 from credible, locally observable threat. Zero without an unobstructed view between creature and player
    /// (<paramref name="exposure"/> = share of the player's body the creature could see, 0 behind walls and furniture), so a wall
    /// can never produce a warning. PROXIMITY is the main driver: a close creature is stressful whichever way it looks. Facing only
    /// trims it (by up to <paramref name="facingInfluence"/>), poor cover raises it and good cover lowers it a little. A confirmed
    /// chase of THIS player adds a little, still only with a clear view. Awareness, destinations and search plans are not inputs.
    /// </summary>
    public static float Pressure(float exposure, float distance, float facing, float cover, bool chasingMe,
        float nearDistance, float farDistance, float coverRelief, float chaseBonus, float facingInfluence)
    {
        float expo = Clamp01(exposure);
        if (expo <= 0f) return 0f;
        float near = Smooth(1f - (distance - nearDistance) / System.Math.Max(0.01f, farDistance - nearDistance));
        float look = 1f - Clamp01(facingInfluence) * (1f - Clamp01(facing)); // looking away only trims, never removes
        float watch = expo * near * look * (1f - Clamp01(coverRelief) * Clamp01(cover));
        float chase = chasingMe ? chaseBonus * expo * near : 0f;
        return Clamp01(watch + chase);
    }

    /// <summary>1 when the creature looks straight at the player, 0 at or beyond half its field of view (dot = cosine of the angle).</summary>
    public static float Facing(float dot, float fieldOfViewDeg)
    {
        float edge = (float)System.Math.Cos(System.Math.Min(179f, System.Math.Max(1f, fieldOfViewDeg)) * 0.5f * System.Math.PI / 180.0);
        return Smooth((dot - edge) / System.Math.Max(0.01f, 1f - edge));
    }

    /// <summary>Moves toward the target at a fade-in or fade-out speed (units per second). Never overshoots, always within 0..1.</summary>
    public static float Approach(float current, float target, float dt, float fadeInPerSecond, float fadeOutPerSecond)
    {
        float speed = target > current ? fadeInPerSecond : fadeOutPerSecond;
        float step = System.Math.Max(0f, speed) * System.Math.Max(0f, dt);
        float v = target > current ? System.Math.Min(target, current + step) : System.Math.Max(target, current - step);
        return Clamp01(v);
    }

    static float Smooth(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
