// Pure, deterministic light-flicker timing (no Unity types; Editor/Tests/AtmosphereRulesTests.cs). Cosmetic only.

/// <summary>
/// A failing light: long steady stretches (between <c>minGap</c> and <c>maxGap</c> seconds), then a short event of one to a few quick
/// dips to between <c>minLevel</c> and about 85% of full, then steady again. Seeded, so a given light always behaves the same way, and
/// never a constant strobe. When disabled it reports full brightness and starts over.
/// </summary>
public sealed class FlickerSchedule
{
    readonly System.Random rng;
    readonly float minGap, maxGap, minLevel, dipSeconds;
    readonly int maxDips;
    float timer, dipLevel;
    int dipsLeft;
    bool inDip;

    public float Level { get; private set; } = 1f;
    /// <summary>True on exactly the tick a new flicker burst begins (for a sound with its own cooldown, not one per dip).</summary>
    public bool EventStarted { get; private set; }

    public FlickerSchedule(int seed, float minGap, float maxGap, float minLevel, float dipSeconds, int maxDips)
    {
        rng = new System.Random(seed);
        this.minGap = System.Math.Max(0.5f, minGap);
        this.maxGap = System.Math.Max(this.minGap, maxGap);
        this.minLevel = minLevel < 0f ? 0f : minLevel > 1f ? 1f : minLevel;
        this.dipSeconds = System.Math.Max(0.02f, dipSeconds);
        this.maxDips = System.Math.Max(1, maxDips);
        timer = NextGap();
    }

    /// <summary>Advances time and returns the brightness multiplier, in [minLevel, 1].</summary>
    public float Tick(float dt, bool enabled)
    {
        EventStarted = false;
        if (!enabled)
        {
            Level = 1f;
            inDip = false;
            dipsLeft = 0;
            return Level;
        }
        timer -= dt;
        while (timer <= 0f)
        {
            if (inDip)
            {
                // A dip ends: either a short bright gap before the next dip of this event, or the long steady stretch.
                inDip = false;
                timer += dipsLeft > 0 ? 0.05f + 0.15f * (float)rng.NextDouble() : NextGap();
            }
            else
            {
                if (dipsLeft == 0) { dipsLeft = 1 + rng.Next(maxDips); EventStarted = true; } // a new event
                dipsLeft--;
                inDip = true;
                dipLevel = minLevel + (0.85f - minLevel) * (float)rng.NextDouble();
                timer += dipSeconds * (0.5f + (float)rng.NextDouble());
            }
        }
        Level = inDip ? System.Math.Max(minLevel, dipLevel) : 1f;
        return Level;
    }

    float NextGap() => minGap + (maxGap - minGap) * (float)rng.NextDouble();
}
