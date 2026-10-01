using System.Collections.Generic;

// Pure audio rules with no Unity types, so they can be unit tested (Editor/Tests/AudioRulesTests.cs). Cosmetic only:
// nothing here touches the creature's logical hearing (NoiseSystem).

/// <summary>
/// Random clip choice without immediate repeats: every clip is played once per cycle in shuffled order, and the first clip of a
/// new cycle is never the one just played.
/// </summary>
public sealed class NoRepeatBag
{
    readonly int count;
    readonly System.Random rng;
    readonly List<int> bag = new();
    int last = -1;

    public NoRepeatBag(int count, System.Random rng)
    {
        this.count = count;
        this.rng = rng;
    }

    public int Next()
    {
        if (count <= 0) return -1;
        if (bag.Count == 0)
        {
            for (int i = 0; i < count; i++) bag.Add(i);
            for (int i = count - 1; i > 0; i--) { int j = rng.Next(i + 1); (bag[i], bag[j]) = (bag[j], bag[i]); }
            if (count > 1 && bag[^1] == last) { (bag[0], bag[^1]) = (bag[^1], bag[0]); } // the next pop is the LAST element
        }
        last = bag[^1];
        bag.RemoveAt(bag.Count - 1);
        return last;
    }
}

public static class AudioRules
{
    /// <summary>
    /// How near the creature feels, 0..1: 1 at or inside <paramref name="near"/>, 0 at or beyond <paramref name="far"/>, smooth
    /// between. A solid obstacle between them muffles it (multiplies by <paramref name="blockedFactor"/>) but does not silence it.
    /// </summary>
    public static float Proximity(float distance, float near, float far, bool blocked, float blockedFactor)
    {
        float t = 1f - (distance - near) / System.Math.Max(0.01f, far - near);
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        t = t * t * (3f - 2f * t);
        return blocked ? t * Clamp01(blockedFactor) : t;
    }

    /// <summary>
    /// Where on the riser clip the loudness equals <paramref name="level"/> (0..1, perceptual): on the way UP (first point at or
    /// above it before the peak) or on the way DOWN (first point at or below it after the peak). <paramref name="envelope"/> is the
    /// clip's loudness at fixed steps, 0..1. Without an envelope it falls back to a straight line up to the peak.
    /// </summary>
    public static float TimeForLevel(IList<float> envelope, float stepSeconds, float level, bool rising, float fallbackPeakSeconds)
    {
        level = Clamp01(level);
        if (envelope == null || envelope.Count < 3) return rising ? level * fallbackPeakSeconds : fallbackPeakSeconds + (1f - level) * fallbackPeakSeconds;
        int peak = 0;
        for (int i = 1; i < envelope.Count; i++) if (envelope[i] > envelope[peak]) peak = i;
        if (rising)
        {
            for (int i = 0; i <= peak; i++) if (envelope[i] >= level) return i * stepSeconds;
            return peak * stepSeconds;
        }
        for (int i = peak; i < envelope.Count; i++) if (envelope[i] <= level) return i * stepSeconds;
        return (envelope.Count - 1) * stepSeconds;
    }

    /// <summary>
    /// The riser keeps playing forward at normal speed inside a window around the wanted position. When the playhead drifts out of
    /// it (too far behind or ahead) this says where to CUT to, so the caller can crossfade there. Returns false while it is fine.
    /// </summary>
    public static bool CutTarget(float playhead, float wanted, float window, float tolerance, out float newPosition)
    {
        float lo = System.Math.Max(0f, wanted - window * 0.5f), hi = wanted + window * 0.5f;
        newPosition = lo;
        return playhead < lo - tolerance || playhead > hi;
    }

    /// <summary>Equal-power crossfade gains for progress 0..1: the incoming voice rises, the outgoing one falls, total loudness stays level.</summary>
    public static void Crossfade(float progress, out float incoming, out float outgoing)
    {
        float t = Clamp01(progress) * 1.5707964f;
        incoming = (float)System.Math.Sin(t);
        outgoing = (float)System.Math.Cos(t);
    }

    /// <summary>
    /// How long the creature still counts as "engaged" with the listener: refreshed to <paramref name="linger"/> while they share a room or
    /// can see each other, and counting down otherwise. A hiding player is therefore not let off the moment the creature steps out of
    /// view in the same room, and a doorway flicker does not cut the music.
    /// </summary>
    public static float EngageRemaining(bool sameRoomOrInSight, float remaining, float dt, float linger) =>
        sameRoomOrInSight ? linger : System.Math.Max(0f, remaining - dt);

    /// <summary>
    /// Perceptual distance attenuation for important world sounds: full volume inside <paramref name="full"/>, then an inverse-distance
    /// fall that is pulled smoothly to silence at <paramref name="max"/>. Strictly decreasing between the two; 0 at and beyond max.
    /// </summary>
    public static float DistanceAttenuation(float distance, float full, float max)
    {
        if (distance <= full) return 1f;
        if (distance >= max) return 0f;
        float t = (distance - full) / System.Math.Max(0.001f, max - full);
        return full / distance * (1f - t * t);
    }

    /// <summary>
    /// How occluded a sound is, 0..1, from the number of solid things (walls, closed doors) on the straight line to the listener.
    /// Each one takes the same share of what is left, so it rises quickly but never reaches 1.
    /// </summary>
    public static float OcclusionAmount(int obstacles, float perObstacle)
    {
        if (obstacles <= 0) return 0f;
        float p = perObstacle < 0f ? 0f : perObstacle > 0.95f ? 0.95f : perObstacle;
        return 1f - (float)System.Math.Pow(1f - p, obstacles);
    }

    /// <summary>Volume multiplier for an occlusion amount: 1 when clear, never below <paramref name="occludedGain"/> (so never silent).</summary>
    public static float OcclusionGain(float amount, float occludedGain)
    {
        float g = occludedGain < 0.05f ? 0.05f : occludedGain > 1f ? 1f : occludedGain;
        return 1f - Clamp01(amount) * (1f - g);
    }

    /// <summary>Low-pass cutoff for an occlusion amount, interpolated in octaves between the clear and the fully occluded cutoff.</summary>
    public static float OcclusionCutoff(float amount, float clearHz, float occludedHz) =>
        clearHz * (float)System.Math.Pow(occludedHz / System.Math.Max(10f, clearHz), Clamp01(amount));

    /// <summary>Seconds between heartbeats: slow when the creature is barely near, fast when it is on top of you.</summary>
    public static float HeartbeatInterval(float level, float slowSeconds, float fastSeconds) =>
        slowSeconds + (fastSeconds - slowSeconds) * Clamp01(level);

    /// <summary>
    /// "Low on stamina" with a dead band so the breathing does not flicker: starts below <paramref name="on"/> (or when exhausted),
    /// and only ends once stamina has recovered to <paramref name="off"/>.
    /// </summary>
    public static bool LowStamina(bool current, float stamina01, bool exhausted, float on, float off)
    {
        if (exhausted || stamina01 < on) return true;
        return current && stamina01 < off;
    }

    /// <summary>
    /// Whether an out-of-breath spell is on. A spell that is already running carries on while stamina is low; a NEW one may only
    /// start after the cooldown (<paramref name="readyAt"/>), unless the player is actually exhausted. So repeated short sprints do
    /// not each trigger the sound.
    /// </summary>
    public static bool RunEpisode(bool episode, bool rawLow, bool exhausted, double now, double readyAt) =>
        rawLow && (episode || exhausted || now >= readyAt);

    /// <summary>One voice at a time: physical exhaustion comes before fear.</summary>
    public static BreathKind DesiredBreath(bool lowStamina, bool scared) =>
        lowStamina ? BreathKind.Run : scared ? BreathKind.Scared : BreathKind.None;

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

public enum BreathKind : byte { None, Scared, Run }

/// <summary>
/// Makes sure only ONE breathing voice is ever sounding. A change of voice always goes through silence: the current voice fades
/// out completely, and only then does the next one start and fade in. Nothing overlaps, so two breathing sounds can never play together.
/// </summary>
public sealed class BreathFader
{
    public BreathKind Current { get; private set; }
    /// <summary>0..1 gain of the current voice.</summary>
    public float Gain { get; private set; }

    public void Tick(BreathKind desired, float dt, float fadeInSeconds, float fadeOutSeconds)
    {
        if (Current != desired)
        {
            if (Current != BreathKind.None)
            {
                Gain -= dt / System.Math.Max(0.01f, fadeOutSeconds);
                if (Gain > 0f) return;
                Gain = 0f;
                Current = BreathKind.None; // fully silent: now free to change voice
            }
            if (desired == BreathKind.None) return;
            Current = desired;
            Gain = 0f;
            return;
        }
        if (Current != BreathKind.None) Gain = System.Math.Min(1f, Gain + dt / System.Math.Max(0.01f, fadeInSeconds));
    }

    public void Clear()
    {
        Current = BreathKind.None;
        Gain = 0f;
    }
}

/// <summary>Timing of the vent cues for a player who joins in the middle of one.</summary>
public static class VentCueRules
{
    /// <summary>
    /// Where in a cue a late joiner should start: the seconds already elapsed since the phase began (scaled by the playback pitch), or -1
    /// when the cue has (nearly) finished, so an expired warning is never replayed. <paramref name="minRemaining"/> is the shortest tail
    /// still worth playing, in real seconds.
    /// </summary>
    public static float LateJoinOffset(float elapsedSeconds, float clipLength, float pitch, float minRemaining)
    {
        if (elapsedSeconds < 0f) elapsedSeconds = 0f;
        float p = pitch <= 0.01f ? 1f : pitch;
        float offset = elapsedSeconds * p;
        float remainingRealSeconds = (clipLength - offset) / p;
        return remainingRealSeconds >= minRemaining ? offset : -1f;
    }
}
