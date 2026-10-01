using System.Collections.Generic;

// Pure rules for the enterable locker (no Unity types; Editor/Tests/LockerTests.cs). The host runs all of them. Nothing here is ever given to
// the creature's search: it chooses lockers like any other hiding place, from where they are, never from who is inside.

public enum EntryResult : byte { Ok, NotOwner, NotInPlay, RoundNotActive, Occupied, TooFar, NoLineOfSight, AlreadyHidden, BeingInspected, BadAnchors, TooSoon }

public enum ExitResult : byte { Ok, NotHidden, NotOwner, HeldOpen, Blocked, TooSoon }

public static class LockerRules
{
    /// <summary>The host's checks for an entry request, in a fixed order so the reason is always the first thing wrong.</summary>
    public static EntryResult ValidateEntry(bool senderIsOwner, bool alive, bool roundActive, bool occupied, float distance, float reach,
        bool lineClear, bool alreadyHidden, bool beingInspected, bool anchorsValid, double now, double nextAllowed)
    {
        if (!senderIsOwner) return EntryResult.NotOwner;
        if (!alive) return EntryResult.NotInPlay;
        if (!roundActive) return EntryResult.RoundNotActive;
        if (!anchorsValid) return EntryResult.BadAnchors;
        if (alreadyHidden) return EntryResult.AlreadyHidden;
        if (occupied) return EntryResult.Occupied;
        if (beingInspected) return EntryResult.BeingInspected;
        if (float.IsNaN(distance) || distance > reach) return EntryResult.TooFar;
        if (!lineClear) return EntryResult.NoLineOfSight;
        if (now < nextAllowed) return EntryResult.TooSoon;
        return EntryResult.Ok;
    }

    /// <summary>
    /// The host's checks for an exit request. While the creature is holding the door open on a committed inspection the occupant cannot
    /// leave (the alternative is walking out through the creature). A blocked exit (another player, a solid) is refused with feedback.
    /// </summary>
    public static ExitResult ValidateExit(bool senderIsOwner, bool hidden, bool inspectionHoldsDoor, bool exitClear, double now, double enteredAt, float minHiddenSeconds)
    {
        if (!senderIsOwner) return ExitResult.NotOwner;
        if (!hidden) return ExitResult.NotHidden;
        if (inspectionHoldsDoor) return ExitResult.HeldOpen;
        if (now - enteredAt < minHiddenSeconds) return ExitResult.TooSoon;
        if (!exitClear) return ExitResult.Blocked;
        return ExitResult.Ok;
    }

    public static string Explain(EntryResult r) => r switch
    {
        EntryResult.Occupied => "Someone is already inside.",
        EntryResult.BeingInspected => "It is not safe to get in now.",
        EntryResult.TooFar or EntryResult.NoLineOfSight => "You cannot reach it from here.",
        _ => "",
    };

    public static string Explain(ExitResult r) => r switch
    {
        ExitResult.HeldOpen => "The door is being held open.",
        ExitResult.Blocked => "Something is in the way outside.",
        _ => "",
    };

    /// <summary>A door change sounds only once the locker has existed for a moment: a late joiner's first value is state, never replayed as an event.</summary>
    public static bool DoorSoundWanted(float secondsSinceSpawn) => secondsSinceSpawn >= 0.5f;

    /// <summary>What the hidden state takes away: walking, sprinting, crouching, objective use, throwing and dropping. Looking is limited, not removed.</summary>
    public static bool MayMove(bool hidden) => !hidden;
    public static bool MayInteract(bool hidden) => !hidden;
    public static bool MayThrow(bool alive, bool hidden) => alive && !hidden;

    /// <summary>
    /// Looking while hidden: the requested yaw and pitch (degrees from straight out of the door) clamped to the locker's limits, so the view
    /// stays at the door.
    /// </summary>
    public static (float yaw, float pitch) ClampLook(float yaw, float pitch, float yawLimit, float pitchLimit) =>
        (Clamp(yaw, -yawLimit, yawLimit), Clamp(pitch, -pitchLimit, pitchLimit));

    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}

/// <summary>
/// Who is inside, on the host. One occupant at most; a second request while occupied fails whatever its timing. Ids are non-zero (0 = nobody).
/// </summary>
public sealed class LockerOccupancy
{
    public ulong Occupant { get; private set; }
    public bool Occupied => Occupant != 0;

    public bool TryEnter(ulong id)
    {
        if (id == 0 || Occupant != 0) return false;
        Occupant = id;
        return true;
    }

    /// <summary>Releases the given occupant; false when somebody else (or nobody) is inside, so a stale release cannot evict a newcomer.</summary>
    public bool Release(ulong id)
    {
        if (id == 0 || Occupant != id) return false;
        Occupant = 0;
        return true;
    }

    /// <summary>Clears whoever is inside (round reset, disconnect, despawn). True when somebody was.</summary>
    public bool ForceRelease()
    {
        bool was = Occupant != 0;
        Occupant = 0;
        return was;
    }
}

/// <summary>
/// The hidden player's breath, on the host. Holding drains it; releasing starts a short delay before it recovers; running out forces the
/// release (and a gasp, once). A new hold needs a minimum recovery, and transitions are rate limited, so a modified client cannot hold
/// indefinitely by sending requests.
/// </summary>
public sealed class BreathModel
{
    readonly float holdSeconds, recoverDelay, recoverSeconds, minToHold, minTransitionGap;
    double lastTransition = double.NegativeInfinity;
    bool forced;

    public float Level { get; private set; } = 1f; // 0..1
    public bool Holding { get; private set; }
    public float SinceRelease { get; private set; } = 999f;

    public BreathModel(float holdSeconds, float recoverDelay, float recoverSeconds, float minToHold, float minTransitionGap)
    {
        this.holdSeconds = System.Math.Max(0.5f, holdSeconds);
        this.recoverDelay = System.Math.Max(0f, recoverDelay);
        this.recoverSeconds = System.Math.Max(0.5f, recoverSeconds);
        this.minToHold = System.Math.Min(1f, System.Math.Max(0f, minToHold));
        this.minTransitionGap = System.Math.Max(0f, minTransitionGap);
    }

    /// <summary>True when the hold started. Refused while already holding, before the minimum recovery, or too soon after the last change.</summary>
    public bool TryHold(double now)
    {
        if (Holding || Level < minToHold || now - lastTransition < minTransitionGap) return false;
        Holding = true;
        lastTransition = now;
        return true;
    }

    public void Release(double now)
    {
        if (!Holding) return;
        Holding = false;
        SinceRelease = 0f;
        lastTransition = now;
    }

    public void Tick(float dt)
    {
        if (Holding)
        {
            Level -= dt / holdSeconds;
            if (Level <= 0f)
            {
                Level = 0f;
                Holding = false;
                SinceRelease = 0f;
                forced = true; // the breath ran out: released by force
            }
            return;
        }
        SinceRelease += dt;
        if (SinceRelease >= recoverDelay) Level = System.Math.Min(1f, Level + dt / recoverSeconds);
    }

    /// <summary>True exactly once after the breath ran out (the involuntary gasp).</summary>
    public bool ConsumeForcedRelease()
    {
        bool was = forced;
        forced = false;
        return was;
    }

    public byte Quantised => (byte)(Level * 100f + 0.5f);

    public void Reset()
    {
        Level = 1f;
        Holding = false;
        SinceRelease = 999f;
        forced = false;
        lastTransition = double.NegativeInfinity;
    }
}

/// <summary>
/// What the hidden player's breathing makes the creature able to hear. Holding the breath silences ONLY the breathing noise (nothing else is
/// asked of this). Otherwise a hidden player breathes extremely quietly, heavy breathing after a sprint is still more audible, and fear
/// (the creature close by) raises the quiet breathing a little: a fair, physical effect that only matters if the creature is already that near.
/// </summary>
public static class HiddenBreathing
{
    public static float Range(bool holdingBreath, bool heavy, float normalRange, float heavyRange, float hiddenRange, float fearRange, bool creatureClose, bool hidden)
    {
        if (holdingBreath) return 0f;
        if (!hidden) return heavy ? heavyRange : normalRange;
        float quiet = creatureClose ? System.Math.Max(hiddenRange, fearRange) : hiddenRange;
        return heavy ? System.Math.Max(quiet, heavyRange) : quiet;
    }

    /// <summary>The involuntary gasp when the breath runs out: only when the creature is within <paramref name="triggerDistance"/>, and once per exhaustion.</summary>
    public static bool Gasps(bool forcedRelease, float creatureDistance, float triggerDistance) =>
        forcedRelease && creatureDistance <= triggerDistance;
}

public enum LockerStage : byte { None, Windup, Opening, Looking, Closing, Done }

/// <summary>
/// The creature's inspection of a locker as a timeline: face the door and wind up, open it, look in, close it. The door is open from the start
/// of Opening to the end of Looking. Bounded: the whole inspection always ends after the sum of its stages.
/// </summary>
public sealed class LockerInspection
{
    float windup, opening, looking, closing, t;

    public LockerStage Stage { get; private set; } = LockerStage.None;
    public bool DoorShouldBeOpen => Stage is LockerStage.Opening or LockerStage.Looking;
    public bool Looking => Stage == LockerStage.Looking;
    public bool Active => Stage != LockerStage.None && Stage != LockerStage.Done;
    public float TotalSeconds => windup + opening + looking + closing;
    public float StageSecondsLeft => System.Math.Max(0f, StageLength(Stage) - t);

    public void Begin(float windup, float opening, float looking, float closing)
    {
        this.windup = System.Math.Max(0f, windup);
        this.opening = System.Math.Max(0f, opening);
        this.looking = System.Math.Max(0.1f, looking);
        this.closing = System.Math.Max(0f, closing);
        Stage = LockerStage.Windup;
        t = 0f;
        Skip();
    }

    public LockerStage Tick(float dt)
    {
        if (!Active) return Stage;
        t += dt;
        Skip();
        return Stage;
    }

    public void Cancel()
    {
        Stage = LockerStage.None;
        t = 0f;
    }

    float StageLength(LockerStage s) => s switch
    {
        LockerStage.Windup => windup,
        LockerStage.Opening => opening,
        LockerStage.Looking => looking,
        LockerStage.Closing => closing,
        _ => 0f,
    };

    // Moves on through every stage whose time is used up (a zero-length stage is skipped in the same tick).
    void Skip()
    {
        while (Active && t >= StageLength(Stage))
        {
            t -= StageLength(Stage);
            Stage = Stage == LockerStage.Closing ? LockerStage.Done : (LockerStage)((int)Stage + 1);
        }
    }
}

/// <summary>
/// Where the creature looks first. The inputs describe PLACES only (distance, whether it is an enclosed hiding place, whether a sound came from
/// beside it); there is no occupancy among them, so an empty and an occupied locker always score the same.
/// </summary>
public static class HidingChoice
{
    /// <summary>Lower is better. A hiding place is preferred a little; an enclosed one that a heard sound came from beside is preferred more.</summary>
    public static float Score(float distanceToEvidence, float distanceFromCreature, bool enclosed, bool soundEvidence, float evidenceNearRadius, float enclosedSoundBonus)
    {
        float score = distanceToEvidence + 0.3f * distanceFromCreature - 3f;
        if (enclosed && soundEvidence && distanceToEvidence <= evidenceNearRadius) score -= enclosedSoundBonus;
        return score;
    }
}
