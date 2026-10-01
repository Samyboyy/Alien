using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>One way to leave the ship: a pod, how it is launched, what it needs, and what it costs.</summary>
public sealed class EscapePlan
{
    public int podNode;
    public LaunchMode mode;
    public PodStatus status;
    public bool needsPower;
    /// <summary>The gated edges on the route with the fewest gates, and the easiest option of each (by noise and effort).</summary>
    public readonly List<(int edge, OptionKind easiest)> gates = new();
    public int hops;
    public bool loud;
    /// <summary>Route pressure (see RoutePressure); 0 until assessed.</summary>
    public float pressure;
    public int loudActions, stages;
    public readonly List<(string what, float value)> breakdown = new();
    public readonly List<string> requirements = new();

    /// <summary>What the plan costs, not where it is: power, noise and the kinds of obstacle on the way. Two plans with the same signature are not a choice.</summary>
    public string Signature => $"{(needsPower ? "needs power" : "no power")}, {(loud ? "loud" : "quiet")}, obstacles [{string.Join("+", gates.Select(x => x.easiest.ToString()).OrderBy(x => x, System.StringComparer.Ordinal))}]";
}

public sealed class SolveResult
{
    public bool solved;
    public readonly List<string> failures = new();
    /// <summary>The stage at which each room first becomes reachable (0 = from the start).</summary>
    public readonly Dictionary<int, int> roomStage = new();
    /// <summary>Stage of each event: "keycard obtainable", "fuse obtainable", "fuse fitted", "power restored", "gate E open".</summary>
    public readonly List<(string what, int stage)> events = new();
    public int stages;
    public readonly List<EscapePlan> plans = new();
    public readonly List<string> choices = new();
    public bool HasChoice => choices.Count > 0;
    public readonly List<int> unreachable = new();
}

/// <summary>
/// The progression model, separate from physical connectivity. It runs a fixpoint: from the start room, which gates can be opened, which items
/// can be fetched (only if every place the item may lie is reachable, so it holds for whichever place a round picks), which flags can be set,
/// until nothing changes. Each pass is a stage, which doubles as the reachability stage report.
///
/// The player carries one item at a time, but doors stay open, items can be dropped anywhere reachable and every obstacle needs at most one item
/// at once, so any order the fixpoint finds can be played one item at a time by one player. Nothing here needs a second player.
/// Optional items are ignored unless asked for: a scenario must be solvable without them.
/// </summary>
public static class ScenarioSolver
{
    public sealed class Options
    {
        public bool useOptionalItems;
        /// <summary>Rooms that may not be entered at all (to prove something optional is not mandatory).</summary>
        public ISet<int> forbiddenRooms;
    }

    public static SolveResult Solve(ShipGraph g, Scenario sc, Options opt = null)
    {
        opt ??= new Options();
        var res = new SolveResult();
        int n = g.NodeCount;
        var reach = new bool[n];
        var stageOf = new int[n];
        for (int i = 0; i < n; i++) stageOf[i] = -1;
        bool keycard = false, fuseFetched = false, fuseFitted = false, power = false;
        var openGate = new HashSet<int>();
        var itemOk = new HashSet<string>();
        int stage = 0;

        bool Enterable(int node) => opt.forbiddenRooms == null || !opt.forbiddenRooms.Contains(node);

        bool Passable(int edge)
        {
            var gate = sc.GateOn(edge);
            if (gate == null) return true;
            if (openGate.Contains(edge)) return true;
            foreach (var o in gate.options)
                switch (o.kind)
                {
                    case OptionKind.Override: return true;
                    case OptionKind.Keycard: if (keycard) return true; break;
                    case OptionKind.Power: if (power) return true; break;
                    case OptionKind.Remote: if (o.controlNode >= 0 && reach[o.controlNode]) return true; break;
                }
            return false;
        }

        void Flood()
        {
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++) if (reach[i]) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                foreach (var e in g.edges)
                {
                    if (e.a != u && e.b != u) continue;
                    int v = e.Other(u);
                    if (reach[v] || !Enterable(v) || !Passable(g.edges.IndexOf(e))) continue;
                    reach[v] = true;
                    stageOf[v] = stage;
                    queue.Enqueue(v);
                }
            }
        }

        bool AllReachable(ItemPlan it) => it.candidates.Count > 0 && it.candidates.All(c => reach[c.node]);
        ItemPlan Find(ItemKind kind) => sc.items.FirstOrDefault(i => i.kind == kind && i.critical);
        bool NodeOf(ConsoleRole role, out int node) { var c = sc.Console(role); node = c?.node ?? -1; return c != null; }

        reach[sc.spawnNode] = true;
        stageOf[sc.spawnNode] = 0;
        Flood();
        bool changed = true;
        while (changed && stage < 50)
        {
            changed = false;
            stage++;
            // Everything this pass may do depends only on the state at its start, so the pass count is the real depth of the dependency chain
            // (fetch the fuse, fit it, restart the generator, open the powered door...), which is what the stage report shows.
            bool keycard0 = keycard, fuseFetched0 = fuseFetched, fuseFitted0 = fuseFitted;
            foreach (var gate in sc.gates)
                if (!openGate.Contains(gate.edge) && Passable(gate.edge)) { openGate.Add(gate.edge); changed = true; res.events.Add(($"door on edge {gate.edge} ({g.edges[gate.edge].id}) can be opened", stage)); }
            var keyPlan = Find(ItemKind.Keycard);
            if (!keycard0 && keyPlan != null && AllReachable(keyPlan)) { keycard = true; changed = true; res.events.Add(("keycard obtainable", stage)); }
            var fusePlan = Find(ItemKind.Fuse);
            if (!fuseFetched0 && fusePlan != null && AllReachable(fusePlan)) { fuseFetched = true; changed = true; res.events.Add(("fuse obtainable", stage)); }
            if (fuseFetched0 && !fuseFitted0 && NodeOf(ConsoleRole.FuseSocket, out int socket) && reach[socket]) { fuseFitted = true; changed = true; res.events.Add(("fuse fitted (power chain step 1)", stage)); }
            if (fuseFitted0 && !power && NodeOf(ConsoleRole.Generator, out int gen) && reach[gen]) { power = true; changed = true; res.events.Add(("power restored (generator running)", stage)); }
            if (opt.useOptionalItems)
                foreach (var it in sc.items.Where(i => i.optional && !itemOk.Contains(i.id)))
                    if (AllReachable(it)) { itemOk.Add(it.id); changed = true; res.events.Add(($"optional {it.id} obtainable", stage)); }
            int before = reach.Count(r => r);
            Flood();
            if (reach.Count(r => r) != before) changed = true;
        }
        res.stages = System.Math.Max(0, stage - 1); // the last pass changed nothing
        for (int i = 0; i < n; i++)
        {
            if (stageOf[i] >= 0) res.roomStage[i] = stageOf[i];
            else res.unreachable.Add(i);
        }

        // Which pods can be launched, and the plans they give.
        foreach (var pod in sc.pods)
        {
            if (!pod.Usable) continue;
            var console = sc.consoles.FirstOrDefault(c => c.role == ConsoleRole.PodLaunch && c.podNode == pod.node);
            if (console == null) { res.failures.Add($"pod in room {g.nodes[pod.node].id} is usable but has no launch console"); continue; }
            if (!reach[pod.node]) { res.failures.Add($"pod room {g.nodes[pod.node].id} can never be reached"); continue; }
            if (pod.NeedsPower && !power) { res.failures.Add($"pod room {g.nodes[pod.node].id} needs power, which cannot be restored"); continue; }
            res.plans.Add(MakePlan(g, sc, pod));
        }
        res.solved = res.plans.Count > 0;
        if (!res.solved)
        {
            res.failures.Add("no pod can be launched");
            foreach (var gate in sc.gates.Where(x => !openGate.Contains(x.edge)))
                res.failures.Add($"stuck door on edge {gate.edge} ({g.edges[gate.edge].id}): " + string.Join(" or ", gate.options.Select(o => Describe(o, g, sc, keycard, power, reach))));
            var keyPlan = Find(ItemKind.Keycard);
            if (keyPlan != null && !keycard) res.failures.Add("the keycard cannot be fetched: candidate rooms " + string.Join(", ", keyPlan.candidates.Where(c => !reach[c.node]).Select(c => g.nodes[c.node].id).Distinct()) + " are never reachable (a key behind its own lock, or an unreachable room)");
            var fusePlan = Find(ItemKind.Fuse);
            if (fusePlan != null && !fuseFetched) res.failures.Add("the fuse cannot be fetched: candidate rooms " + string.Join(", ", fusePlan.candidates.Where(c => !reach[c.node]).Select(c => g.nodes[c.node].id).Distinct()) + " are never reachable");
            if (sc.UsesPower && !power) res.failures.Add("power is never restored" + (fuseFetched ? (fuseFitted ? ": the generator room is unreachable" : ": the fuse socket room is unreachable") : ""));
        }
        else FindChoices(g, sc, res);
        return res;
    }

    static string Describe(GateOption o, ShipGraph g, Scenario sc, bool keycard, bool power, bool[] reach) => o.kind switch
    {
        OptionKind.Keycard => keycard ? "keycard (obtainable)" : "keycard (never obtainable)",
        OptionKind.Power => power ? "power (restored)" : "power (never restored)",
        OptionKind.Remote => o.controlNode >= 0 && reach[o.controlNode] ? "remote console (reachable)" : $"remote console in {(o.controlNode >= 0 ? g.nodes[o.controlNode].id : "?")} (unreachable)",
        _ => "manual override",
    };

    // Effort ranking of the options on a door, easiest first: the override is quickest but loud, the keycard quiet once you have it.
    static int Effort(OptionKind k) => k switch { OptionKind.Keycard => 1, OptionKind.Override => 2, OptionKind.Remote => 3, _ => 4 };

    static EscapePlan MakePlan(ShipGraph g, Scenario sc, PodPlan pod)
    {
        var plan = new EscapePlan { podNode = pod.node, mode = pod.mode, status = pod.status, needsPower = pod.NeedsPower, loud = pod.NeedsPower || pod.mode == LaunchMode.Loud };
        // The route with the fewest gates (then fewest rooms): a 0-1 BFS over the graph.
        int n = g.NodeCount;
        var best = new (int gates, int hops)[n];
        var prev = new int[n];
        for (int i = 0; i < n; i++) { best[i] = (int.MaxValue, int.MaxValue); prev[i] = -1; }
        best[sc.spawnNode] = (0, 0);
        var done = new bool[n];
        for (int it = 0; it < n; it++)
        {
            int u = -1;
            for (int i = 0; i < n; i++) if (!done[i] && best[i].gates != int.MaxValue && (u < 0 || best[i].CompareTo(best[u]) < 0)) u = i;
            if (u < 0) break;
            done[u] = true;
            for (int ei = 0; ei < g.edges.Count; ei++)
            {
                var e = g.edges[ei];
                if (e.a != u && e.b != u) continue;
                int v = e.Other(u);
                var cand = (best[u].gates + (sc.GateOn(ei) != null ? 1 : 0), best[u].hops + 1);
                if (cand.CompareTo(best[v]) < 0) { best[v] = cand; prev[v] = u; }
            }
        }
        plan.hops = best[pod.node].hops;
        for (int v = pod.node; v >= 0 && prev[v] >= 0; v = prev[v])
        {
            int ei = g.edges.FindIndex(e => (e.a == v && e.b == prev[v]) || (e.b == v && e.a == prev[v]));
            var gate = sc.GateOn(ei);
            if (gate != null) plan.gates.Add((ei, gate.options.OrderBy(o => Effort(o.kind)).First().kind));
        }
        plan.gates.Reverse();
        return plan;
    }

    // A meaningful choice exists when the player can pick between different costs: two usable pods that differ in noise or power needs, a door that
    // opens in more than one way, or a gated door with a way round it.
    static void FindChoices(ShipGraph g, Scenario sc, SolveResult res)
    {
        var sigs = res.plans.Select(p => p.Signature).Distinct().ToList();
        if (sigs.Count >= 2) res.choices.Add($"two pods with different costs: {string.Join(" / ", sigs)}");
        foreach (var gate in sc.gates)
        {
            var kinds = gate.options.Select(o => o.kind).Distinct().ToList();
            if (kinds.Count >= 2) res.choices.Add($"the door on {g.edges[gate.edge].id} opens by {string.Join(" or ", kinds)}");
            if (HasBypass(g, gate.edge)) res.choices.Add($"the door on {g.edges[gate.edge].id} can be avoided by a longer route");
        }
    }

    /// <summary>True when the two rooms of an edge stay connected without it (the edge lies on a loop).</summary>
    public static bool HasBypass(ShipGraph g, int edge)
    {
        var e = g.edges[edge];
        var seen = new bool[g.NodeCount];
        var stack = new Stack<int>();
        stack.Push(e.a);
        seen[e.a] = true;
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            for (int i = 0; i < g.edges.Count; i++)
            {
                if (i == edge) continue;
                var f = g.edges[i];
                if (f.a != u && f.b != u) continue;
                int v = f.Other(u);
                if (!seen[v]) { seen[v] = true; stack.Push(v); }
            }
        }
        return seen[e.b];
    }
}
