// Pure motion-tracker rules (no Unity types; Editor/Tests/AcousticTrackerTests.cs). The tracker senses MOTION from data every client can
// legitimately see (the creature's replicated position over time, and whether it is inside the vent structure, which is already shown by its
// body disappearing). It never touches the host's AI state: evidence, targets, awareness or plans.

public static class TrackerRules
{
    /// <summary>
    /// Signal strength 0..1 of a motion contact. Nothing below the motion threshold (a still creature, however close, shows nothing) or
    /// beyond range. Nearer is stronger, faster is a little stronger. Inside the vent structure it is weaker and intermittent: when
    /// <paramref name="structureGateOpen"/> is false it shows nothing at all.
    /// </summary>
    public static float Signal(float speed, float distance, float threshold, float range, bool inStructure, float structureFactor, bool structureGateOpen)
    {
        if (range <= 0f || distance > range || speed < threshold) return 0f;
        if (inStructure && !structureGateOpen) return 0f;
        float proximity = 1f - Clamp01(distance / range);
        float motion = Clamp01((speed - threshold) / System.Math.Max(0.05f, threshold * 4f));
        float s = (float)System.Math.Pow(proximity, 0.7) * (0.55f + 0.45f * motion);
        if (inStructure) s *= Clamp01(structureFactor);
        return Clamp01(s);
    }

    /// <summary>
    /// Bearing of a world offset relative to the camera's yaw, in degrees: 0 straight ahead, positive to the right, within (-180, 180].
    /// World yaw follows Unity: 0 along +Z, 90 along +X.
    /// </summary>
    public static float Bearing(float cameraYawDeg, float dx, float dz)
    {
        float world = (float)(System.Math.Atan2(dx, dz) * 180.0 / System.Math.PI);
        float rel = (world - cameraYawDeg) % 360f;
        if (rel > 180f) rel -= 360f;
        if (rel <= -180f) rel += 360f;
        return rel;
    }

    /// <summary>Angular uncertainty (degrees either side) for a contact at this distance: small up close, large at the edge of range.</summary>
    public static float AngleUncertainty(float distance, float range, float nearDeg, float farDeg) =>
        nearDeg + (farDeg - nearDeg) * Clamp01(distance / System.Math.Max(0.01f, range));

    /// <summary>Distance uncertainty as a fraction of the distance, growing with distance.</summary>
    public static float DistanceUncertainty(float distance, float range, float nearFraction, float farFraction) =>
        nearFraction + (farFraction - nearFraction) * Clamp01(distance / System.Math.Max(0.01f, range));

    /// <summary>A smooth, deterministic wobble in [-1, 1] (sum of incommensurate sines), so the display drifts but never flickers randomly.</summary>
    public static float Wobble(double time, float seed)
    {
        double t = time + seed * 17.13;
        double v = 0.55 * System.Math.Sin(t * 1.31) + 0.3 * System.Math.Sin(t * 2.71 + 1.7) + 0.15 * System.Math.Sin(t * 5.03 + 0.4);
        return (float)(v < -1 ? -1 : v > 1 ? 1 : v);
    }

    /// <summary>For motion inside the vent structure: open for a share <paramref name="duty"/> of each period, so the contact comes and goes.</summary>
    public static bool StructureGate(double time, float period, float duty, float phase)
    {
        if (period <= 0f) return true;
        double f = (time + phase) / period;
        f -= System.Math.Floor(f);
        return f < Clamp01(duty);
    }

    /// <summary>
    /// Seconds to the next beep: the idle sweep interval with no contact (0 = no idle beeps, returns 0), otherwise from slow to fast as the
    /// signal strengthens, always within [<paramref name="minInterval"/>, <paramref name="maxInterval"/>].
    /// </summary>
    public static float BeepInterval(float strength, float minInterval, float maxInterval, float idleInterval)
    {
        if (strength <= 0f) return idleInterval <= 0f ? 0f : System.Math.Max(idleInterval, minInterval);
        float t = Clamp01(strength);
        float v = maxInterval + (minInterval - maxInterval) * t;
        return v < minInterval ? minInterval : v > maxInterval ? maxInterval : v;
    }

    /// <summary>The logical noise range of one beep, from the host's configuration, never above the safe maximum.</summary>
    public static float ClampNoiseRange(float configured, float min, float max) => configured < min ? min : configured > max ? max : configured;

    /// <summary>
    /// The host's check for one beep request. The client sends nothing but the request itself; the host decides the position (the owner's
    /// replicated position) and loudness (its own configuration). Rejected unless the sender owns this tracker, is alive, the round is
    /// active, the tracker is raised, and the minimum interval since the last accepted beep has passed.
    /// </summary>
    public static bool AcceptBeep(double now, double lastAccepted, float minInterval, bool senderIsOwner, bool alive, bool roundActive, bool trackerActive) =>
        senderIsOwner && alive && roundActive && trackerActive && now - lastAccepted >= minInterval;

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

/// <summary>A small fixed-size history of blips (position on the dial and when it was taken), oldest overwritten first. No allocations after construction.</summary>
public sealed class BlipHistory
{
    readonly float[] x, y;
    readonly double[] time;
    int next, count;

    public BlipHistory(int capacity)
    {
        capacity = System.Math.Max(1, capacity);
        x = new float[capacity];
        y = new float[capacity];
        time = new double[capacity];
    }

    public int Count => count;
    public int Capacity => x.Length;

    public void Add(float px, float py, double t)
    {
        x[next] = px;
        y[next] = py;
        time[next] = t;
        next = (next + 1) % x.Length;
        if (count < x.Length) count++;
    }

    /// <summary>The i-th most recent entry (0 = newest).</summary>
    public void Get(int i, out float px, out float py, out double t)
    {
        int idx = (next - 1 - i + x.Length * 2) % x.Length;
        px = x[idx];
        py = y[idx];
        t = time[idx];
    }

    public void Clear()
    {
        next = 0;
        count = 0;
    }
}
