using System.Collections.Generic;

// Pure rules for the ship's acoustics (no Unity types; Editor/Tests/AtmosphereRulesTests.cs). Cosmetic only: none of this reaches
// the creature's logical hearing.

/// <summary>The kinds of space the listener can be in. Each has a tunable acoustic profile in the AudioBank.</summary>
public enum AcousticSpace : byte { Neutral, SmallRoom, LargeMachinery, Corridor, Compartment, Crawlspace }

public static class AcousticRules
{
    /// <summary>
    /// The zone the listener is in: the highest priority among the zones that contain it (a pod inside a room, the crawlspace inside a
    /// wall run), ties to the lower index. -1 when none contains it, so the caller uses its fallback (the corridor profile on the ship).
    /// </summary>
    public static int Select(IList<bool> contains, IList<int> priority, int count)
    {
        int best = -1;
        for (int i = 0; i < count; i++)
            if (contains[i] && (best < 0 || priority[i] > priority[best])) best = i;
        return best;
    }

    /// <summary>
    /// One step of a smooth transition towards a target value: a share dt/seconds of the remaining gap, so it never overshoots and
    /// settles in a few transition times. Zero seconds snaps.
    /// </summary>
    public static float Approach(float current, float target, float dt, float seconds)
    {
        if (seconds <= 0f) return target;
        float k = dt / seconds;
        if (k > 1f) k = 1f;
        if (k < 0f) k = 0f;
        return current + (target - current) * k;
    }
}
