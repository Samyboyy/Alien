using System;
using System.Collections.Generic;
using System.Linq;

[Serializable]
public sealed class DirectorSettings
{
    public int maxAttempts = 80;
    public int minGates = 1, maxGates = 3;
    /// <summary>How the primary way through a gated door is chosen.</summary>
    public float weightKeycard = 0.5f, weightPower = 0.25f, weightRemote = 0.25f;
    /// <summary>Chance that a door also has a manual override (loud, slow, needs nothing) next to its keycard, power or remote control.</summary>
    public float overrideAlternative = 0.35f;
    /// <summary>Chance that the second pod is also usable (the first always is).</summary>
    public float secondPodUsable = 0.8f;
    public int noisemakers = 3;
    /// <summary>A scenario with no meaningful route choice is rejected.</summary>
    public bool requireChoice = true;

    public DirectorSettings Clone() => (DirectorSettings)MemberwiseClone();
}

public sealed class DirectorInput
{
    public ShipGraph graph;
    public int layoutSeed;
    public Func<int, RoomTemplate> templateOf;
    public Func<string, RoomSpec> specOf;
    public List<PlacementProfile> profiles = DefaultPlacementProfiles.Create();
}

public sealed class ScenarioResult
{
    public bool success;
    public Scenario scenario;
    public SolveResult solve;
    public int attempts;
    /// <summary>Why each rejected attempt was thrown away (the last attempt's, when none succeeded, tells why generation failed).</summary>
    public readonly List<string> rejections = new();

    public string Summary => success
        ? $"scenario seed {scenario.scenarioSeed}: attempt {attempts}, {scenario.pods.Count(p => p.Usable)} usable pod(s), {scenario.gates.Count} gated door(s), {solve.plans.Count} escape plan(s)"
        : $"scenario FAILED after {attempts} attempt(s). Last: {(rejections.Count > 0 ? rejections[^1] : "-")}";
}

/// <summary>
/// Escape Director V1. For a placed ship it chooses which pods work, gates a few connections with obstacles the game already supports
/// (keycard door, powered door, remote door, manual override, the fuse and generator chain, a noisy pod launch, a quiet slow one), places the
/// keycard and fuse among several plausible anchors, adds optional equipment and a camera console configuration, and then asks the solver whether a
/// single player can escape and whether the player has a real choice. A scenario that fails is thrown away and the next attempt (another random
/// stream from the same seed) starts again; the result keeps every reason.
/// </summary>
public static class EscapeDirector
{
    public static ScenarioResult Generate(DirectorInput input, int scenarioSeed, DirectorSettings settings = null)
    {
        settings ??= new DirectorSettings();
        var result = new ScenarioResult();
        var g = input.graph;
        if (g.FirstOf(RoomCategory.PlayerStart) < 0 || g.AllOf(RoomCategory.EscapePodBay).Count == 0) { result.rejections.Add("the ship has no player start or no escape bay"); return result; }
        for (int attempt = 0; attempt < settings.maxAttempts; attempt++)
        {
            result.attempts = attempt + 1;
            var rng = new ShipRng(ShipRng.Derive(scenarioSeed ^ (input.layoutSeed * 31 + 7), attempt));
            var sc = Build(input, settings, rng, scenarioSeed, attempt, out string why);
            if (sc == null) { result.rejections.Add($"attempt {attempt + 1}: {why}"); continue; }
            var solve = ScenarioSolver.Solve(g, sc);
            if (!solve.solved) { result.rejections.Add($"attempt {attempt + 1}: unsolvable: {string.Join("; ", solve.failures.Take(4))}"); continue; }
            var problems = Validate(input, sc);
            if (settings.requireChoice && !solve.HasChoice) problems.Add("no meaningful route choice (one pod, no alternative way through any door)");
            if (problems.Count > 0) { result.rejections.Add($"attempt {attempt + 1}: {string.Join("; ", problems.Take(3))}"); continue; }
            result.scenario = sc;
            result.solve = solve;
            result.success = true;
            return result;
        }
        return result;
    }

    // ---------- Building one candidate scenario ----------

    static Scenario Build(DirectorInput input, DirectorSettings s, ShipRng rng, int scenarioSeed, int attempt, out string why)
    {
        why = null;
        var g = input.graph;
        var sc = new Scenario { layoutSeed = input.layoutSeed, scenarioSeed = scenarioSeed, attempt = attempt, spawnNode = g.FirstOf(RoomCategory.PlayerStart) };
        var bays = g.AllOf(RoomCategory.EscapePodBay);

        // 1. Pods: at least one works. The other may work, be damaged (needs power) or be wrecked.
        int first = rng.Range(0, bays.Count - 1);
        for (int i = 0; i < bays.Count; i++)
        {
            var pod = new PodPlan { node = bays[i] };
            if (i == first) pod.status = PodStatus.Operational;
            else if (rng.Chance(s.secondPodUsable)) pod.status = rng.Chance(0.3) ? PodStatus.Damaged : PodStatus.Operational;
            else pod.status = PodStatus.Unusable;
            pod.mode = rng.Chance(0.5) ? LaunchMode.Loud : LaunchMode.ManualQuiet;
            sc.pods.Add(pod);
        }
        var usable = sc.pods.Where(p => p.Usable).ToList();
        // When two pods work they should differ, so the choice is real: one loud and powered, one quiet and slow.
        if (usable.Count >= 2 && usable[0].mode == usable[1].mode) usable[1].mode = usable[0].mode == LaunchMode.Loud ? LaunchMode.ManualQuiet : LaunchMode.Loud;
        foreach (var pod in sc.pods)
            if (pod.Usable)
                sc.consoles.Add(new ConsolePlan { role = ConsoleRole.PodLaunch, node = pod.node, anchorId = AnchorOf(input, pod.node, AnchorSemantic.PodLaunchConsole), podNode = pod.node });
        if (sc.consoles.Any(c => string.IsNullOrEmpty(c.anchorId))) { why = "an escape bay has no launch console anchor"; return null; }

        // 2. Gates on connections, preferring the ones on the way to a pod and on loops.
        var dist = g.Distances(sc.spawnNode);
        var pathEdges = new HashSet<int>();
        foreach (var pod in usable) foreach (int e in ShortestPathEdges(g, sc.spawnNode, pod.node)) pathEdges.Add(e);
        int gateCount = rng.Range(s.minGates, s.maxGates);
        var taken = new HashSet<int>();
        bool remoteUsed = false;
        int security = g.FirstOf(RoomCategory.Security);
        for (int k = 0; k < gateCount; k++)
        {
            var pool = new List<int>();
            var weights = new List<float>();
            for (int e = 0; e < g.EdgeCount; e++)
            {
                if (taken.Contains(e) || g.edges[e].a == sc.spawnNode || g.edges[e].b == sc.spawnNode) continue;
                pool.Add(e);
                bool onPath = pathEdges.Contains(e), bypass = ScenarioSolver.HasBypass(g, e);
                // The first gate must really block the way to a pod (no way round), so there is something to find or fix before escaping.
                if (k == 0 && (!onPath || bypass)) { pool.RemoveAt(pool.Count - 1); continue; }
                weights.Add(onPath ? (bypass ? 1.5f : 5f) : (bypass ? 1f : 1.5f));
            }
            int pick = rng.Weighted(weights);
            if (pick < 0) break;
            int edge = pool[pick];
            taken.Add(edge);
            var gate = new DoorGate { edge = edge, doorNode = dist[g.edges[edge].a] >= dist[g.edges[edge].b] ? g.edges[edge].a : g.edges[edge].b };
            bool canRemote = !remoteUsed && security >= 0 && !string.IsNullOrEmpty(AnchorOf(input, security, AnchorSemantic.DoorControlConsole));
            int roll = rng.Weighted(new List<float> { s.weightKeycard, s.weightPower, canRemote ? s.weightRemote : 0f });
            var kind = roll == 0 ? OptionKind.Keycard : roll == 1 ? OptionKind.Power : OptionKind.Remote;
            if (kind == OptionKind.Remote)
            {
                remoteUsed = true;
                gate.options.Add(new GateOption(OptionKind.Remote, security));
                sc.consoles.Add(new ConsolePlan { role = ConsoleRole.RemoteDoor, node = security, anchorId = AnchorOf(input, security, AnchorSemantic.DoorControlConsole), doorEdge = edge });
                gate.story = "a security lockdown door, released from the security station";
            }
            else
            {
                gate.options.Add(new GateOption(kind));
                gate.story = kind == OptionKind.Keycard ? "a keycard door" : "a door that needs ship power";
            }
            if (k > 0 && rng.Chance(s.overrideAlternative)) { gate.options.Add(new GateOption(OptionKind.Override)); gate.story += ", with a loud manual override"; }
            sc.gates.Add(gate);
        }
        if (sc.gates.Count == 0) { why = "no connection could be gated"; return null; }

        // 3. The power chain (fuse -> socket -> generator) when anything needs power.
        if (sc.UsesPower)
        {
            int power = g.FirstOf(RoomCategory.PowerControl), eng = g.FirstOf(RoomCategory.Engineering);
            if (power < 0 || eng < 0) { why = "power is needed but there is no Power Control or Engineering"; return null; }
            string socket = AnchorOf(input, power, AnchorSemantic.FuseSocket), generator = AnchorOf(input, eng, AnchorSemantic.ObjectiveConsole);
            if (string.IsNullOrEmpty(socket) || string.IsNullOrEmpty(generator)) { why = "the fuse socket or generator console anchor is missing"; return null; }
            sc.consoles.Add(new ConsolePlan { role = ConsoleRole.FuseSocket, node = power, anchorId = socket });
            sc.consoles.Add(new ConsolePlan { role = ConsoleRole.Generator, node = eng, anchorId = generator });
        }

        // 4. Items. Preferred rooms: reachable with no obstacle in the way (a key found first need not be fetched through a door that needs it).
        var open = ReachableWithoutObstacles(g, sc);
        var usedAnchors = new HashSet<string>();
        foreach (var profile in input.profiles.Where(p => p.placedByDirector && !p.optional))
        {
            if (profile.item == ItemClass.Keycard && !sc.UsesKeycard) continue;
            if (profile.item == ItemClass.Fuse && !sc.UsesPower) continue;
            if (!AddItem(input, sc, profile, profile.id, rng, open, usedAnchors, out why)) return null;
        }

        // 5. Optional equipment. Never needed: the solver ignores it, and the director refuses to put anything critical in the store.
        foreach (var profile in input.profiles.Where(p => p.placedByDirector && p.optional))
        {
            if (profile.item == ItemClass.Fuse && !sc.UsesPower) continue;
            int copies = profile.item == ItemClass.Noisemaker ? s.noisemakers : 1;
            for (int i = 0; i < copies; i++)
                if (!AddItem(input, sc, profile, copies > 1 ? $"{profile.id}_{i}" : profile.id, rng, null, usedAnchors, out _)) break;
        }

        // 6. Camera control: which feeds exist and which are down (data only for now; see CameraConsolePlaceholder).
        int cam = g.FirstOf(RoomCategory.CameraControl);
        if (cam >= 0)
        {
            string anchor = AnchorOf(input, cam, AnchorSemantic.CameraControlConsole);
            if (!string.IsNullOrEmpty(anchor))
            {
                sc.consoles.Add(new ConsolePlan { role = ConsoleRole.CameraControl, node = cam, anchorId = anchor });
                var mounts = new List<(int node, string id)>();
                foreach (var n in g.nodes)
                    if (n.index != cam)
                        foreach (var a in input.templateOf(n.index).anchors.Where(a => a.kind == AnchorKind.CameraMount)) mounts.Add((n.index, a.id));
                for (int i = 0; i < 4 && mounts.Count > 0; i++)
                {
                    int m = rng.Range(0, mounts.Count - 1);
                    sc.feeds.Add(new CameraFeed { node = mounts[m].node, anchorId = mounts[m].id, available = rng.Chance(0.7) });
                    mounts.RemoveAll(x => x.node == mounts[m].node);
                }
                if (sc.feeds.Count >= 3 && sc.feeds.All(f => f.available)) sc.feeds[^1].available = false;
                if (sc.feeds.Count > 0 && sc.feeds.All(f => !f.available)) sc.feeds[0].available = true;
            }
        }
        return sc;
    }

    static bool AddItem(DirectorInput input, Scenario sc, PlacementProfile profile, string id, ShipRng rng, ISet<int> preferred, HashSet<string> usedAnchors, out string why)
    {
        why = null;
        var pool = PlacementEngine.Candidates(profile, input.graph, input.templateOf, input.specOf)
            .Where(c => !usedAnchors.Contains($"{c.node}:{c.anchorId}")).ToList();
        var chosen = PlacementEngine.Choose(pool, profile, rng, preferred);
        if (chosen.Count < Math.Min(profile.minCandidates, 2))
        {
            if (profile.critical) { why = $"too few plausible places for the {profile.id} ({chosen.Count} of at least {profile.minCandidates})"; return false; }
            return true; // optional: simply not placed
        }
        var plan = new ItemPlan
        {
            id = id, profile = profile.id, cls = profile.item, critical = profile.critical, optional = profile.optional,
            kind = profile.item == ItemClass.Keycard ? ItemKind.Keycard : profile.item == ItemClass.Fuse ? ItemKind.Fuse : ItemKind.None,
        };
        plan.candidates.AddRange(chosen);
        foreach (var c in chosen) usedAnchors.Add($"{c.node}:{c.anchorId}");
        sc.items.Add(plan);
        return true;
    }

    static string AnchorOf(DirectorInput input, int node, AnchorSemantic semantic) =>
        input.templateOf(node).anchors.FirstOrDefault(a => a.kind == AnchorKind.Objective && a.semantic == semantic)?.id ?? "";

    static HashSet<int> ReachableWithoutObstacles(ShipGraph g, Scenario sc)
    {
        var seen = new HashSet<int> { sc.spawnNode };
        var queue = new Queue<int>();
        queue.Enqueue(sc.spawnNode);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            for (int i = 0; i < g.EdgeCount; i++)
            {
                var e = g.edges[i];
                if (e.a != u && e.b != u) continue;
                int v = e.Other(u);
                if (sc.GateOn(i) != null || !seen.Add(v)) continue;
                queue.Enqueue(v);
            }
        }
        return seen;
    }

    static List<int> ShortestPathEdges(ShipGraph g, int from, int to)
    {
        var prev = new int[g.NodeCount];
        var prevEdge = new int[g.NodeCount];
        for (int i = 0; i < prev.Length; i++) prev[i] = -2;
        prev[from] = -1;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            for (int i = 0; i < g.EdgeCount; i++)
            {
                var e = g.edges[i];
                if (e.a != u && e.b != u) continue;
                int v = e.Other(u);
                if (prev[v] != -2) continue;
                prev[v] = u;
                prevEdge[v] = i;
                queue.Enqueue(v);
            }
        }
        var list = new List<int>();
        for (int v = to; v >= 0 && prev[v] >= 0; v = prev[v]) list.Add(prevEdge[v]);
        return list;
    }

    // ---------- Validation of a finished scenario (everything except solvability, which the solver reports) ----------

    /// <summary>Static checks on a scenario: allowed anchors, no critical item in the equipment store, consoles on real anchors, gates on real edges.</summary>
    public static List<string> Validate(DirectorInput input, Scenario sc)
    {
        var problems = new List<string>();
        var g = input.graph;
        var seenAnchors = new HashSet<string>();
        foreach (var it in sc.items)
        {
            if (it.candidates.Count == 0) problems.Add($"item {it.id} has no candidate place");
            foreach (var c in it.candidates)
            {
                var a = input.templateOf(c.node).anchors.FirstOrDefault(x => x.id == c.anchorId && x.kind == AnchorKind.Item);
                if (a == null) { problems.Add($"item {it.id}: anchor {c.anchorId} does not exist in {g.nodes[c.node].id}"); continue; }
                if (!a.Allows(it.cls)) problems.Add($"item {it.id}: anchor {c.anchorId} ({a.semantic}) does not allow {it.cls}");
                if (it.critical && !a.canHoldCritical) problems.Add($"critical item {it.id} is on {a.semantic} anchor {c.anchorId}, which may not hold critical items");
                if (it.critical && g.nodes[c.node].category == RoomCategory.EquipmentStore) problems.Add($"critical item {it.id} is in the optional Equipment Store");
                if (string.IsNullOrEmpty(c.rule)) problems.Add($"item {it.id}: candidate {c.anchorId} has no placement rule");
                if (!seenAnchors.Add($"{c.node}:{c.anchorId}")) problems.Add($"two item candidates share the anchor {c.anchorId} in {g.nodes[c.node].id}");
            }
        }
        foreach (var gate in sc.gates)
        {
            if (gate.edge < 0 || gate.edge >= g.EdgeCount) { problems.Add($"a gate names edge {gate.edge}, which does not exist"); continue; }
            if (gate.options.Count == 0) problems.Add($"the door on {g.edges[gate.edge].id} has no way to open");
            if (gate.options.Any(o => o.kind == OptionKind.Remote && !sc.consoles.Any(c => c.role == ConsoleRole.RemoteDoor && c.doorEdge == gate.edge)))
                problems.Add($"the remote door on {g.edges[gate.edge].id} has no control console");
        }
        foreach (var c in sc.consoles)
            if (!input.templateOf(c.node).anchors.Any(a => a.id == c.anchorId && a.kind == AnchorKind.Objective)) problems.Add($"{c.role} console: anchor {c.anchorId} is missing in {g.nodes[c.node].id}");
        if (sc.pods.All(p => !p.Usable)) problems.Add("no usable pod");
        return problems;
    }
}
