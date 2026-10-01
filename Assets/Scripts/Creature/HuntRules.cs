using System.Collections.Generic;

// Pure rules for where a calm, hunting creature looks next (no Unity types; Editor/Tests/HuntRulesTests.cs).
// Only the ship's rooms and what the creature itself has experienced go in: how long since it last visited a room, and where it
// recently saw or heard players ("heat"). Nothing here knows where any player is.

public static class HuntRules
{
    /// <summary>
    /// How attractive a room is to search next: rooms not visited for a long time (up to <paramref name="staleCap"/> seconds count),
    /// rooms where players were recently seen or heard (heat), and a small penalty for distance.
    /// </summary>
    public static float Score(float secondsSinceVisited, float heat, float metres, float staleCap, float heatCap, float maxMetres,
        float staleWeight, float heatWeight, float distanceWeight)
    {
        float stale = System.Math.Min(System.Math.Max(0f, secondsSinceVisited), staleCap) / System.Math.Max(0.01f, staleCap);
        float h = System.Math.Min(System.Math.Max(0f, heat), heatCap) / System.Math.Max(0.01f, heatCap);
        float d = System.Math.Min(System.Math.Max(0f, metres), maxMetres) / System.Math.Max(0.01f, maxMetres);
        return staleWeight * stale + heatWeight * h - distanceWeight * d;
    }

    /// <summary>Highest score among the eligible rooms; ties go to the lower index. -1 when none is eligible.</summary>
    public static int Pick(IList<float> scores, IList<bool> eligible)
    {
        int best = -1;
        for (int i = 0; i < scores.Count; i++)
            if (eligible[i] && (best < 0 || scores[i] > scores[best] + 1e-6f)) best = i;
        return best;
    }

    /// <summary>Heat fades by half every <paramref name="halfLifeSeconds"/>.</summary>
    public static float Decay(float heat, float seconds, float halfLifeSeconds) =>
        halfLifeSeconds <= 0f ? 0f : heat * (float)System.Math.Pow(0.5, System.Math.Max(0f, seconds) / halfLifeSeconds);
}
