using System.Collections.Generic;

// Pure rules for the ship's acoustics (no Unity types; Editor/Tests/AtmosphereRulesTests.cs). Cosmetic only: none of this reaches
// the creature's logical hearing.

/// <summary>The kinds of space the listener can be in. Each has a tunable acoustic profile in the AudioBank.</summary>
public enum AcousticSpace : byte { Neutral, SmallRoom, LargeMachinery, Corridor, Compartment, Crawlspace, LargeOpen, Duct }

/// <summary>
/// Early-reflection settings for one kind of space, in the units of Unity's AudioReverbFilter (millibels, seconds). Applied per emitter
/// (close-range metallic reflections), separately from the listener's late room reverb. Plain data, so the defaults can be tested.
/// </summary>
public struct ReflectionSettings
{
    public int room, roomHF, reflections, reverb;
    public float decayTime, decayHFRatio, reflectionsDelay, reverbDelay, diffusion, density;

    public ReflectionSettings(int room, int roomHF, float decayTime, float decayHFRatio, int reflections, float reflectionsDelay, int reverb, float reverbDelay, float diffusion, float density)
    {
        this.room = room; this.roomHF = roomHF; this.decayTime = decayTime; this.decayHFRatio = decayHFRatio;
        this.reflections = reflections; this.reflectionsDelay = reflectionsDelay; this.reverb = reverb; this.reverbDelay = reverbDelay;
        this.diffusion = diffusion; this.density = density;
    }
}

/// <summary>Starting values per space; the AudioBank copies them into its tunable profiles (migration) and fresh profiles.</summary>
public static class AcousticDefaults
{
    // Corridors: short, bright, metallic. Machinery: stronger, denser, a little longer. Small rooms: tight. Large open spaces: less
    // immediate reflection (the late tail, from the listener's zone, carries them). Crawlspace and ducts: short and boxy. Neutral: dry.
    public static ReflectionSettings Reflections(AcousticSpace space) => space switch
    {
        AcousticSpace.SmallRoom => new ReflectionSettings(-1100, -500, 0.32f, 0.6f, -300, 0.004f, -2600, 0.006f, 80f, 85f),
        AcousticSpace.Corridor => new ReflectionSettings(-900, -250, 0.4f, 0.7f, 0, 0.006f, -2400, 0.008f, 60f, 70f),
        AcousticSpace.LargeMachinery => new ReflectionSettings(-800, -400, 0.6f, 0.6f, 100, 0.01f, -1800, 0.014f, 90f, 100f),
        AcousticSpace.LargeOpen => new ReflectionSettings(-1300, -500, 0.5f, 0.6f, -900, 0.018f, -2000, 0.025f, 85f, 90f),
        AcousticSpace.Compartment => new ReflectionSettings(-1200, -600, 0.25f, 0.6f, -400, 0.003f, -2800, 0.004f, 85f, 90f),
        AcousticSpace.Crawlspace => new ReflectionSettings(-800, -1100, 0.2f, 0.5f, 200, 0.002f, -3000, 0.003f, 100f, 100f),
        AcousticSpace.Duct => new ReflectionSettings(-900, -700, 0.3f, 0.6f, 0, 0.003f, -2600, 0.004f, 70f, 90f),
        _ => new ReflectionSettings(-10000, -10000, 0.1f, 0.5f, -10000, 0f, -10000, 0f, 100f, 100f),
    };

    /// <summary>How much of each room-tone layer a space uses (air handling, machinery, ducts), 0..1.</summary>
    public static (float air, float machinery, float duct) ToneLevels(AcousticSpace space) => space switch
    {
        AcousticSpace.Corridor => (1f, 0.15f, 0f),
        AcousticSpace.SmallRoom => (0.7f, 0f, 0f),
        AcousticSpace.Compartment => (0.4f, 0f, 0f),
        AcousticSpace.LargeMachinery => (0.4f, 1f, 0f),
        AcousticSpace.LargeOpen => (0.8f, 0.35f, 0f),
        AcousticSpace.Crawlspace => (0.2f, 0f, 1f),
        AcousticSpace.Duct => (0f, 0f, 1f),
        _ => (0f, 0f, 0f),
    };
}

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
    /// Whether a category gets early reflections and how much: world sounds fully, your own body lightly, internal/ambience/UI never
    /// (they stay dry). Category ids follow AudioCategory: 0 World, 1 Creature, 2 Players, 3 OwnBody, 4 Ambience, 5 Internal, 6 UI.
    /// </summary>
    public static float CategoryReflectionWeight(int category, float ownBodyWeight) => category switch
    {
        0 or 1 or 2 => 1f,
        3 => System.Math.Max(0f, System.Math.Min(1f, ownBodyWeight)),
        _ => 0f,
    };

    /// <summary>
    /// Early reflections matter most up close, where the direct sound dominates: full weight inside <paramref name="near"/>, easing down to
    /// <paramref name="farWeight"/> by <paramref name="far"/> (the listener's late reverb carries distant sounds). Never zero up close.
    /// </summary>
    public static float ReflectionDistanceWeight(float distance, float near, float far, float farWeight)
    {
        if (distance <= near) return 1f;
        if (distance >= far) return farWeight;
        float t = (distance - near) / System.Math.Max(0.01f, far - near);
        t = t * t * (3f - 2f * t);
        return 1f + (farWeight - 1f) * t;
    }

    /// <summary>A linear weight as a level offset in millibels (Unity reverb units); 0 for full weight, very low for (near) zero.</summary>
    public static int WeightToMillibels(float weight) =>
        weight >= 1f ? 0 : weight <= 0.001f ? -6000 : (int)System.Math.Round(2000.0 * System.Math.Log10(weight));

    /// <summary>
    /// The wet levels actually sent to the filter, bounded so a tuning mistake or a stack of offsets can never make the reflections louder
    /// than intended: overall room at most <paramref name="maxRoom"/>, early reflections at most <paramref name="maxReflections"/>, the
    /// emitter's own late part at most <paramref name="maxReverb"/> (the listener's zone provides the real late tail).
    /// </summary>
    public static ReflectionSettings Bounded(ReflectionSettings s, int offsetMb, int maxRoom, int maxReflections, int maxReverb)
    {
        s.room = Clamp(s.room + offsetMb, -10000, maxRoom);
        s.reflections = Clamp(s.reflections, -10000, maxReflections);
        s.reverb = Clamp(s.reverb, -10000, maxReverb);
        return s;
    }

    /// <summary>A rough loudness of the early reflections (linear), for ordering and diagnostics: overall wet times (1 + early level).</summary>
    public static float ReflectionEnergy(ReflectionSettings s) =>
        (float)(System.Math.Pow(10.0, s.room / 2000.0) * (1.0 + System.Math.Pow(10.0, s.reflections / 2000.0)));

    /// <summary>
    /// The one place a source's volume is composed: clip volume x category volume (master included) x occlusion. Applying a new occlusion
    /// gain always starts from the uncombined base, so nothing is ever multiplied twice.
    /// </summary>
    public static float Compose(float clipVolume, float categoryVolume, float occlusionGain) =>
        Clamp01(clipVolume) * Clamp01(categoryVolume) * Clamp01(occlusionGain);

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

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

/// <summary>
/// A fixed number of early-reflection slots shared by every emitter: an emitter that starts a sound takes one if one is free; when all
/// are in use the sound simply plays without its reflections (dry), never with a new object. Released when the emitter falls silent.
/// </summary>
public sealed class ReflectionBudget
{
    int inUse;

    public int Capacity { get; set; }
    public int InUse => inUse;

    public ReflectionBudget(int capacity) => Capacity = capacity;

    public bool TryAcquire()
    {
        if (inUse >= Capacity) return false;
        inUse++;
        return true;
    }

    public void Release()
    {
        if (inUse > 0) inUse--;
    }

    public void Clear() => inUse = 0;
}

/// <summary>
/// Measurements of long recordings from their loudness over time (decibels per fixed step), used by Alien > Apply Acoustic Polish: the
/// seamless loop region of a bed that fades in and out, and the separate calls inside a long recording.
/// </summary>
public static class ClipRegions
{
    /// <summary>
    /// The part of a bed to loop: from the first step to the last step that is within <paramref name="dropDb"/> of the median level, pulled in
    /// by <paramref name="marginSeconds"/> each side. (0, 0) when the result would be shorter than <paramref name="minSeconds"/>.
    /// </summary>
    public static (float start, float end) LoopRegion(IList<float> db, float step, float dropDb, float marginSeconds, float minSeconds)
    {
        if (db == null || db.Count < 3) return (0f, 0f);
        float median = Median(db), floor = median - dropDb;
        int first = 0, last = db.Count - 1;
        while (first < last && db[first] < floor) first++;
        while (last > first && db[last] < floor) last--;
        float start = first * step + marginSeconds, end = (last + 1) * step - marginSeconds;
        return end - start >= minSeconds ? (start, end) : (0f, 0f);
    }

    /// <summary>
    /// Separate calls in a long recording: the loudest moments at least <paramref name="aboveMedianDb"/> over the median and at least
    /// <paramref name="minGapSeconds"/> apart, each as (start, length) from <paramref name="preSeconds"/> before its peak, kept only when the
    /// whole piece lies inside the recording. In time order.
    /// </summary>
    public static List<(float start, float length)> CallSegments(IList<float> db, float step, float aboveMedianDb, float minGapSeconds, float preSeconds, float lengthSeconds, int maxCount)
    {
        var result = new List<(float, float)>();
        if (db == null || db.Count < 3) return result;
        float threshold = Median(db) + aboveMedianDb, total = db.Count * step;
        var order = new List<int>();
        for (int i = 0; i < db.Count; i++) if (db[i] >= threshold) order.Add(i);
        order.Sort((a, b) => db[b].CompareTo(db[a]));
        var peaks = new List<int>();
        foreach (int i in order)
        {
            if (peaks.Count >= maxCount) break;
            bool apart = true;
            foreach (int p in peaks) if (System.Math.Abs(p - i) * step < minGapSeconds) { apart = false; break; }
            float start = i * step - preSeconds;
            if (apart && start >= 0f && start + lengthSeconds <= total) peaks.Add(i);
        }
        peaks.Sort();
        foreach (int p in peaks) result.Add((p * step - preSeconds, lengthSeconds));
        return result;
    }

    static float Median(IList<float> v)
    {
        var copy = new List<float>(v);
        copy.Sort();
        return copy[copy.Count / 2];
    }
}
