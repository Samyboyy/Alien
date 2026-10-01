using System.Collections.Generic;

// Pure rules for the throwable noisemaker (no Unity types; Editor/Tests/NoisemakerTests.cs). The host runs all of them; a client never
// decides anything about a throw, a pickup, a pulse or what the creature makes of it.

public enum NoisemakerPhase : byte { Thrown, Settling, Armed, Pulsing, Spent }

public enum ThrowResult : byte { Ok, NotOwner, NotInPlay, RoundNotActive, NoneLeft, Cooldown, BadAim, SpawnFailed }

public static class NoisemakerRules
{
    /// <summary>Whether one more noisemaker fits. A full inventory leaves the pickup where it is.</summary>
    public static bool CanAdd(int count, int capacity) => count < capacity;

    /// <summary>The host's checks for a throw request, in a fixed order so the reason is always the first thing wrong.</summary>
    public static ThrowResult ValidateThrow(bool senderIsOwner, bool alive, bool roundActive, int count, double now, double nextAllowed)
    {
        if (!senderIsOwner) return ThrowResult.NotOwner;
        if (!alive) return ThrowResult.NotInPlay;
        if (!roundActive) return ThrowResult.RoundNotActive;
        if (count <= 0) return ThrowResult.NoneLeft;
        if (now < nextAllowed) return ThrowResult.Cooldown;
        return ThrowResult.Ok;
    }

    /// <summary>
    /// The transactional core of a throw: the device is spawned FIRST and only a successful spawn takes one from the count, so a failed
    /// spawn (an unregistered prefab, a refused spawn) never costs the player anything.
    /// </summary>
    public static ThrowResult Commit(ref int count, System.Func<bool> spawn)
    {
        if (count <= 0) return ThrowResult.NoneLeft;
        if (spawn == null || !spawn()) return ThrowResult.SpawnFailed;
        count--;
        return ThrowResult.Ok;
    }

    /// <summary>
    /// Turns the client's aim into a direction the host accepts. Rejects non-finite or zero aims. The yaw may differ from the player's own
    /// facing (host-known) by at most <paramref name="maxYawDeg"/> (lag), the pitch is clamped to [<paramref name="minPitchDeg"/>,
    /// <paramref name="maxPitchDeg"/>]. The result is a unit vector.
    /// </summary>
    public static bool ConstrainAim(float ax, float ay, float az, float facingX, float facingZ, float maxYawDeg, float minPitchDeg, float maxPitchDeg,
        out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (!IsFinite(ax) || !IsFinite(ay) || !IsFinite(az) || !IsFinite(facingX) || !IsFinite(facingZ)) return false;
        double len = System.Math.Sqrt((double)ax * ax + (double)ay * ay + (double)az * az);
        if (len < 1e-4 || (facingX * facingX + facingZ * facingZ) < 1e-6f) return false;

        double facingYaw = System.Math.Atan2(facingX, facingZ) * 180.0 / System.Math.PI;
        double yaw = System.Math.Atan2(ax, az) * 180.0 / System.Math.PI;
        double pitch = System.Math.Asin(System.Math.Max(-1.0, System.Math.Min(1.0, ay / len))) * 180.0 / System.Math.PI;
        if (ax * ax + az * az < 1e-8f) yaw = facingYaw; // straight up or down: no yaw of its own
        double delta = yaw - facingYaw;
        delta -= 360.0 * System.Math.Floor((delta + 180.0) / 360.0); // into [-180, 180)
        delta = System.Math.Max(-maxYawDeg, System.Math.Min(maxYawDeg, delta));
        yaw = facingYaw + delta;
        pitch = System.Math.Max(minPitchDeg, System.Math.Min(maxPitchDeg, pitch));

        double yr = yaw * System.Math.PI / 180.0, pr = pitch * System.Math.PI / 180.0, cp = System.Math.Cos(pr);
        x = (float)(System.Math.Sin(yr) * cp);
        y = (float)System.Math.Sin(pr);
        z = (float)(System.Math.Cos(yr) * cp);
        return true;
    }

    /// <summary>
    /// A collision that deserves a sound and a noise event: hard enough, and not too soon after the last one, so tiny repeated bounces
    /// make nothing.
    /// </summary>
    public static bool AcceptImpact(double now, double lastImpact, float cooldown, float speed, float minSpeed) =>
        IsFinite(speed) && speed >= minSpeed && now - lastImpact >= cooldown;

    /// <summary>Outer range (m) of an impact's noise: <paramref name="minRange"/> at the threshold speed up to <paramref name="maxRange"/> at <paramref name="maxSpeed"/>.</summary>
    public static float ImpactRange(float speed, float minSpeed, float maxSpeed, float minRange, float maxRange)
    {
        if (!IsFinite(speed) || speed < minSpeed) return 0f;
        float t = maxSpeed <= minSpeed ? 1f : (speed - minSpeed) / (maxSpeed - minSpeed);
        t = t < 0f ? 0f : t > 1f ? 1f : t;
        return minRange + (maxRange - minRange) * t;
    }

    // The impact counter carries its strength in the same value (sequence * 256 + level), so a remote peer never reads a stale level.
    public static int PackImpact(int sequence, byte level) => sequence * 256 + level;
    public static int ImpactSequence(int packed) => packed >> 8;
    public static byte ImpactLevel(int packed) => (byte)(packed & 255);

    /// <summary>The pickup (and spawn spot) chosen for a round: the same seed always gives the same choice.</summary>
    public static int PickSpot(System.Random rng, int spotCount) => spotCount <= 0 ? -1 : rng.Next(spotCount);

    static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}

/// <summary>
/// One device's life on the host: Thrown (in the air) -> Settling (first real impact, counting down to arming) -> Armed (a moment of
/// blinking) -> Pulsing (a fixed number of pulses, one at a time) -> Spent (a brief prop) -> expired. At most ONE pulse is reported per
/// Tick however late the tick is, so a hitch can never turn into a burst of noise events.
/// </summary>
public sealed class NoisemakerTimeline
{
    readonly float armDelay, armedSeconds, pulseSeconds, pulseInterval, spentSeconds, flightTimeout;
    double phaseStart, armAt, nextPulse;

    public NoisemakerPhase Phase { get; private set; } = NoisemakerPhase.Thrown;
    public int PulsesFired { get; private set; }
    public bool Expired { get; private set; }
    public double NextPulseTime => Phase == NoisemakerPhase.Pulsing ? nextPulse : double.PositiveInfinity;

    public NoisemakerTimeline(float armDelay, float armedSeconds, float pulseSeconds, float pulseInterval, float spentSeconds, float flightTimeout)
    {
        this.armDelay = System.Math.Max(0f, armDelay);
        this.armedSeconds = System.Math.Max(0f, armedSeconds);
        this.pulseSeconds = System.Math.Max(0.1f, pulseSeconds);
        this.pulseInterval = System.Math.Max(0.2f, pulseInterval);
        this.spentSeconds = System.Math.Max(0f, spentSeconds);
        this.flightTimeout = System.Math.Max(0.5f, flightTimeout);
    }

    public void Begin(double now)
    {
        Phase = NoisemakerPhase.Thrown;
        phaseStart = now;
        PulsesFired = 0;
        Expired = false;
    }

    /// <summary>The first meaningful impact starts the arming countdown; later ones change nothing here.</summary>
    public void OnImpact(double now)
    {
        if (Phase != NoisemakerPhase.Thrown) return;
        Phase = NoisemakerPhase.Settling;
        phaseStart = now;
        armAt = now + armDelay;
    }

    /// <summary>Ends the device's life at once (a round reset or a despawn): no further pulse is ever reported.</summary>
    public void Halt()
    {
        Phase = NoisemakerPhase.Spent;
        Expired = true;
    }

    /// <summary>Advances to <paramref name="now"/>. True when a pulse is due on this tick (never more than one).</summary>
    public bool Tick(double now)
    {
        bool pulse = false;
        if (Expired) return false;
        if (Phase == NoisemakerPhase.Thrown && now - phaseStart >= flightTimeout) OnImpact(now); // never lands (stuck or lost): arm anyway
        if (Phase == NoisemakerPhase.Settling && now >= armAt) { Phase = NoisemakerPhase.Armed; phaseStart = now; }
        if (Phase == NoisemakerPhase.Armed && now - phaseStart >= armedSeconds)
        {
            Phase = NoisemakerPhase.Pulsing;
            phaseStart = now;
            nextPulse = now; // the first pulse is due at once
        }
        if (Phase == NoisemakerPhase.Pulsing)
        {
            if (now >= phaseStart + pulseSeconds) { Phase = NoisemakerPhase.Spent; phaseStart = now; }
            else if (now >= nextPulse)
            {
                pulse = true;
                PulsesFired++;
                nextPulse = System.Math.Max(nextPulse + pulseInterval, now + pulseInterval * 0.5); // never a catch-up burst
            }
        }
        if (Phase == NoisemakerPhase.Spent && now - phaseStart >= spentSeconds) Expired = true;
        return pulse;
    }
}

/// <summary>What the creature makes of a noise that comes from a noisemaker. Direct sight, a witnessed hiding place and a fresh trail of the player it follows always outrank it.</summary>
public static class DecoyRules
{
    /// <summary>
    /// Whether a decoy sound replaces the creature's evidence. Never when that device is already recognised, while the creature's interest
    /// in decoys is used up, or while it holds a still-weighted trail of a player (a fresh sighting or footstep trail). The device it is
    /// already heading for keeps refreshing the evidence; any other sound has to be clearly stronger than what it has (the normal margin
    /// and switch cooldown), so two devices cannot flip it back and forth.
    /// </summary>
    public static bool Accept(bool recognised, bool interestLeft, float currentScore, bool currentIsPlayerTrail, bool sameSourceAsCurrent,
        float candidateStrength, float switchMargin, bool cooldownOver)
    {
        if (recognised || !interestLeft || candidateStrength <= 0f) return false;
        if (currentScore > 0f && currentIsPlayerTrail) return false;
        if (currentScore <= 0f || sameSourceAsCurrent) return true;
        return EscapeRules.AcceptNoise(currentScore, false, false, true, candidateStrength, switchMargin, cooldownOver);
    }
}

/// <summary>
/// The creature's memory about decoys, by source token: which devices it has recognised (and ignores from then on), how long it has
/// examined the one it is at, and a budget of interest so a run of devices cannot hold it forever. Nothing here is shared between
/// devices: recognising one says nothing about another.
/// </summary>
public sealed class DecoyMemory
{
    readonly HashSet<ulong> ignored = new();
    readonly Dictionary<ulong, float> examined = new();
    readonly List<ulong> scratch = new();
    float interestUsed;
    double refractoryUntil = double.NegativeInfinity;

    public int IgnoredCount => ignored.Count;
    public bool IsIgnored(ulong token) => ignored.Contains(token);

    /// <summary>False while the creature is bored of decoys (its budget ran out and the quiet period is not over).</summary>
    public bool InterestLeft(double now) => now >= refractoryUntil;

    /// <summary>Time spent chasing a decoy. When the budget is used up, decoys are ignored for <paramref name="refractory"/> seconds.</summary>
    public void Spend(float dt, double now, float budget, float refractory)
    {
        interestUsed += dt;
        if (interestUsed < budget) return;
        interestUsed = 0f;
        refractoryUntil = now + refractory;
    }

    /// <summary>Examining the device it is standing at. True on the call that completes the examination: from then on that device is ignored.</summary>
    public bool Examine(ulong token, float dt, float needSeconds)
    {
        if (ignored.Contains(token)) return false;
        examined.TryGetValue(token, out float t);
        t += dt;
        if (t < needSeconds) { examined[token] = t; return false; }
        examined.Remove(token);
        ignored.Add(token);
        return true;
    }

    /// <summary>Forgets devices that no longer exist, so the memory never grows.</summary>
    public void Prune(System.Func<ulong, bool> isLive)
    {
        scratch.Clear();
        foreach (var t in ignored) if (!isLive(t)) scratch.Add(t);
        foreach (var t in examined.Keys) if (!isLive(t)) scratch.Add(t);
        foreach (var t in scratch) { ignored.Remove(t); examined.Remove(t); }
    }

    public void Clear()
    {
        ignored.Clear();
        examined.Clear();
        interestUsed = 0f;
        refractoryUntil = double.NegativeInfinity;
    }
}

/// <summary>
/// Decides which sound events a peer plays, so each one is heard exactly once. The host plays an event DIRECTLY the moment it makes it; a
/// remote peer plays it when the replicated counter arrives. The host's own replicated callback therefore never plays it a second time, an
/// event number is never played twice, and a peer that joins late starts from the counter's current value instead of replaying history.
/// </summary>
public sealed class NoisemakerEventGate
{
    int heard;

    /// <summary>The counter's value when this peer got the device (nothing before it is ever played).</summary>
    public void Begin(int current) => heard = current;

    /// <summary>The host made event <paramref name="sequence"/> itself: true when it should be played now.</summary>
    public bool Direct(int sequence) => Take(sequence);

    /// <summary>The replicated counter changed. The host already played its own events, so only a remote peer plays this.</summary>
    public bool Replicated(int sequence, bool isServer) => !isServer && Take(sequence);

    bool Take(int sequence)
    {
        if (sequence <= heard) return false;
        heard = sequence;
        return true;
    }
}
