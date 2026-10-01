using System;
using System.Collections.Generic;
using System.Linq;

// The escape scenario as plain data: which pods work, which connections are gated and how they open, where the consoles are, and where each item
// may lie. It is built on the mechanics the project already has (keycard, fuse, power flag, doors, hold interactions, noise, pod countdown) and is
// separate from the physical layout: the solver reasons about progression, the layout only says which rooms touch.

public enum PodStatus : byte { Operational, Damaged, Unusable }

/// <summary>
/// Loud: the pod needs ship power and counts down noisily (like Pod A). ManualQuiet: a slow, quiet hold-to-release that needs no power (like Pod B).
/// A damaged pod always needs power, whatever its mode.
/// </summary>
public enum LaunchMode : byte { Loud, ManualQuiet }

/// <summary>One way to open a gated door. A door may offer several (the player chooses).</summary>
public enum OptionKind : byte
{
    Keycard,   // a console at the jamb that needs the security keycard carried (quick, quiet)
    Power,     // a console that needs the ship's power restored
    Remote,    // no control at the door; a door-control console in another room opens it
    Override,  // a hold-to-operate manual override at the jamb: needs nothing, takes time and is loud
}

public sealed class GateOption
{
    public OptionKind kind;
    /// <summary>For Remote: the room holding the door-control console.</summary>
    public int controlNode = -1;
    public GateOption() { }
    public GateOption(OptionKind kind, int controlNode = -1) { this.kind = kind; this.controlNode = controlNode; }
    public override string ToString() => kind == OptionKind.Remote ? $"Remote(control room {controlNode})" : kind.ToString();
}

/// <summary>A door on one logical connection. The gate is passable when any one of its options is satisfied.</summary>
public sealed class DoorGate
{
    public int edge;
    /// <summary>The room whose doorway holds the door slab.</summary>
    public int doorNode;
    public readonly List<GateOption> options = new();
    public string story = "";
}

public enum ConsoleRole : byte { FuseSocket, Generator, PodLaunch, RemoteDoor, CameraControl }

public sealed class ConsolePlan
{
    public ConsoleRole role;
    public int node;
    public string anchorId = "";
    /// <summary>RemoteDoor: the edge of the door it operates.</summary>
    public int doorEdge = -1;
    /// <summary>PodLaunch: the pod's room.</summary>
    public int podNode = -1;
}

public sealed class PodPlan
{
    public int node;
    public PodStatus status;
    public LaunchMode mode;
    public bool Usable => status != PodStatus.Unusable;
    public bool NeedsPower => status == PodStatus.Damaged || mode == LaunchMode.Loud;
}

public sealed class ItemPlan
{
    public string id = "";
    public string profile = "";
    public ItemKind kind;
    public ItemClass cls;
    public bool critical, optional;
    public readonly List<SpotCandidate> candidates = new();
}

public sealed class CameraFeed
{
    public int node;
    public string anchorId = "";
    public bool available;
}

public sealed class Scenario
{
    public int layoutSeed, scenarioSeed, attempt;
    public int spawnNode;
    public readonly List<PodPlan> pods = new();
    public readonly List<DoorGate> gates = new();
    public readonly List<ConsolePlan> consoles = new();
    public readonly List<ItemPlan> items = new();
    public readonly List<CameraFeed> feeds = new();

    /// <summary>A canonical text of the whole scenario (equal strings mean equal scenarios).</summary>
    public string Canonical()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"L{layoutSeed}S{scenarioSeed};spawn{spawnNode};");
        foreach (var p in pods) sb.Append($"pod{p.node}:{p.status}:{p.mode};");
        foreach (var g in gates) { sb.Append($"gate{g.edge}@{g.doorNode}:"); foreach (var o in g.options) sb.Append($"{o.kind}{o.controlNode},"); sb.Append(';'); }
        foreach (var c in consoles) sb.Append($"con{c.role}:{c.node}:{c.anchorId}:{c.doorEdge}:{c.podNode};");
        foreach (var i in items) { sb.Append($"item{i.id}:{i.kind}:{i.critical}:"); foreach (var c in i.candidates) sb.Append($"{c.node}/{c.anchorId},"); sb.Append(';'); }
        foreach (var f in feeds) sb.Append($"feed{f.node}:{f.anchorId}:{f.available};");
        return sb.ToString();
    }

    public string Fingerprint()
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in Canonical()) unchecked { h = (h ^ ch) * 1099511628211UL; }
        return h.ToString("x16");
    }

    public DoorGate GateOn(int edge) => gates.FirstOrDefault(g => g.edge == edge);
    public ConsolePlan Console(ConsoleRole role) => consoles.FirstOrDefault(c => c.role == role);
    public bool UsesPower => pods.Any(p => p.Usable && p.NeedsPower) || gates.Any(g => g.options.Any(o => o.kind == OptionKind.Power));
    public bool UsesKeycard => gates.Any(g => g.options.Any(o => o.kind == OptionKind.Keycard));

    /// <summary>A copy in which every item has exactly the one candidate chosen for a round (see ScenarioRound).</summary>
    public Scenario ForRound(int roundSeed)
    {
        var s = new Scenario { layoutSeed = layoutSeed, scenarioSeed = scenarioSeed, attempt = attempt, spawnNode = spawnNode };
        s.pods.AddRange(pods);
        s.gates.AddRange(gates);
        s.consoles.AddRange(consoles);
        s.feeds.AddRange(feeds);
        foreach (var it in items)
        {
            var copy = new ItemPlan { id = it.id, profile = it.profile, kind = it.kind, cls = it.cls, critical = it.critical, optional = it.optional };
            if (it.candidates.Count > 0) copy.candidates.Add(it.candidates[ScenarioRound.PickIndex(roundSeed, it.id, it.candidates.Count)]);
            s.items.Add(copy);
        }
        return s;
    }
}

/// <summary>
/// Per-round randomness for items. Each item picks its resting place from a stream derived from (round seed, item id) only, so the choice does not
/// depend on how many other objects drew random numbers first, and a dev can reproduce a round from the three seeds alone.
/// </summary>
public static class ScenarioRound
{
    public static int PickIndex(int roundSeed, string itemId, int count)
    {
        if (count <= 1) return 0;
        ulong h = 14695981039346656037UL;
        foreach (char c in itemId) unchecked { h = (h ^ c) * 1099511628211UL; }
        var rng = new ShipRng(ShipRng.Derive(roundSeed, (int)(h & 0x7FFFFFFF)));
        return rng.Range(0, count - 1);
    }
}
