using System.Collections.Generic;

/// <summary>
/// The ship generator's own random numbers: SplitMix64, fully defined here, so the same seed gives the same sequence on every machine, runtime
/// and Unity version. Never touches UnityEngine.Random or System.Random.
/// </summary>
public sealed class ShipRng
{
    ulong state;

    public ShipRng(ulong seed) => state = seed;

    /// <summary>A well-mixed seed for attempt <paramref name="attempt"/> of seed <paramref name="seed"/> (each retry gets its own stream).</summary>
    public static ulong Derive(int seed, int attempt)
    {
        ulong z = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + (ulong)(uint)attempt * 0xD1B54A32D192ED03UL + 0x632BE59BD9B4E019UL);
        return Mix(z);
    }

    static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public ulong NextULong()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            return Mix(state);
        }
    }

    /// <summary>[0, 1)</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>An integer in [min, max] (both inclusive).</summary>
    public int Range(int min, int max)
    {
        if (max <= min) return min;
        ulong span = (ulong)(max - min) + 1UL;
        return min + (int)(NextULong() % span); // the modulo bias is negligible for spans this small
    }

    public bool Chance(double p) => NextDouble() < p;

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Range(0, i);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>An index chosen in proportion to the weights (zero and negative weights are never chosen); -1 when none is positive.</summary>
    public int Weighted(IList<float> weights)
    {
        double total = 0;
        foreach (float w in weights) if (w > 0f) total += w;
        if (total <= 0) return -1;
        double r = NextDouble() * total;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0f) continue;
            r -= weights[i];
            if (r < 0) return i;
        }
        for (int i = weights.Count - 1; i >= 0; i--) if (weights[i] > 0f) return i;
        return -1;
    }
}
