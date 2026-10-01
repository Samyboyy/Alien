using System.Text;

// Pure pieces of the creature's decision diagnostics (no Unity types; Editor/Tests/DecisionLogTests.cs). The log is a fixed-size ring: it is
// filled by the host's creature at meaningful decision changes, never per frame, and never sent over the network.

/// <summary>Stable reason codes for a decision change. New codes are appended; none is ever renumbered (they appear in dumped files).</summary>
public enum DecisionReason : byte
{
    Unspecified, Spawned, RoundReset, DirectSight, LostSight, PursuedTrail, HeardPlayer, HeardImpact, HeardDecoy, HeardOther, WatchedHideEntry,
    VentStarted, VentCancelled, VentRerouted, VentEvidenceStored, VentEmerged, VentAbandoned, DoorObstructed, DoorResumed, LockerSelected, LockerInspected,
    DecoyRecognised, DecoyResumed, SearchExpired, PatrolResumed, TargetLeftPlay, PursuitExpired, Captured, RoomHunt,
}

/// <summary>What a decision did to whatever the creature was doing before it.</summary>
public enum DecisionEffect : byte { None, Interrupted, Stored, Rerouted, Ignored }

/// <summary>One recorded decision. Only what the creature itself knows: its stored evidence and its own state, never a player's live position.</summary>
public struct DecisionEvent
{
    public double time;
    public byte from, to;                 // CreatureState values
    public DecisionReason reason;
    public DecisionEffect effect;
    public ulong target;                  // the chase target, ulong.MaxValue = none
    public byte evidenceKind;             // EvidenceKind value
    public float evidenceAge, evidenceStrength;
    public float evidenceX, evidenceZ;
    public byte searchPhase;
    public string room;                   // the room being searched (a name that already exists; nothing is allocated for it)
    public byte ventPhase;
    public sbyte ventEntry, ventExit;
    public string door;                   // door being worked on, or null
    public byte lockerStage;
    public ulong decoyToken;
    public float alertness;
    public string detail;
}

/// <summary>A fixed-capacity ring: the newest entries are kept, the oldest are overwritten. No allocation after construction.</summary>
public sealed class RingBuffer<T>
{
    readonly T[] items;
    int next, count;

    public RingBuffer(int capacity) => items = new T[System.Math.Max(1, capacity)];

    public int Count => count;
    public int Capacity => items.Length;

    public void Add(in T value)
    {
        items[next] = value;
        next = (next + 1) % items.Length;
        if (count < items.Length) count++;
    }

    /// <summary>The i-th most recent entry (0 = newest).</summary>
    public T FromNewest(int i) => items[(next - 1 - i + items.Length * 2) % items.Length];

    public void Clear()
    {
        System.Array.Clear(items, 0, items.Length);
        next = 0;
        count = 0;
    }
}

public static class DecisionFormat
{
    /// <summary>One line for the F3 display and the dumped file. <paramref name="stateName"/> turns a state value into its name.</summary>
    public static void Line(StringBuilder sb, in DecisionEvent e, System.Func<byte, string> stateName, double now)
    {
        sb.Append((now - e.time < 0 ? 0 : now - e.time).ToString("0.0")).Append("s ago  ");
        string from = stateName(e.from), to = stateName(e.to);
        sb.Append(from == to ? to : from + " -> " + to).Append("  ").Append(e.reason);
        if (e.effect != DecisionEffect.None) sb.Append(" [").Append(e.effect).Append(']');
        if (e.target != ulong.MaxValue) sb.Append("  target P").Append(e.target);
        if (e.evidenceKind != 0) sb.Append("  evidence ").Append(e.evidenceKind).Append(' ').Append(e.evidenceAge.ToString("0.0")).Append("s old w").Append(e.evidenceStrength.ToString("0.00"));
        if (!string.IsNullOrEmpty(e.detail)) sb.Append("  ").Append(e.detail);
    }
}
