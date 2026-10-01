using System.Collections.Generic;

// Pure ventilation rules with no Unity types, so they can be unit tested (Editor/Tests/VentRulesTests.cs).

/// <summary>The replicated stages of a creature vent trip (see CreatureAI.Vent.cs).</summary>
public enum VentPhase : byte { None, Approaching, Entering, Travelling, Preparing, Exiting }

/// <summary>
/// The creature-only duct network as a graph: nodes are vent entrances (indices 0..N-1) and junctions, edges are authored duct
/// runs with a real length in metres. Routes are shortest by length; ties go to the lower node index, so a route is deterministic.
/// </summary>
public sealed class VentGraph
{
    readonly int nodeCount;
    readonly List<(int a, int b, float length)> edges = new();

    public VentGraph(int nodeCount) => this.nodeCount = nodeCount;

    public int NodeCount => nodeCount;
    public int EdgeCount => edges.Count;

    /// <summary>Returns the edge id.</summary>
    public int AddEdge(int a, int b, float length)
    {
        edges.Add((a, b, length));
        return edges.Count - 1;
    }

    public (int a, int b, float length) Edge(int id) => edges[id];

    public int Degree(int node)
    {
        int n = 0;
        foreach (var e in edges) if (e.a == node || e.b == node) n++;
        return n;
    }

    /// <summary>
    /// Shortest route. <paramref name="nodes"/> gets the node sequence and <paramref name="edgeIds"/> the edges walked (cleared
    /// first). False when the nodes are not connected.
    /// </summary>
    public bool Route(int from, int to, List<int> nodes, List<int> edgeIds, out float length)
    {
        length = 0f;
        nodes?.Clear();
        edgeIds?.Clear();
        if (from < 0 || to < 0 || from >= nodeCount || to >= nodeCount) return false;
        var dist = new float[nodeCount];
        var prev = new int[nodeCount];
        var prevEdge = new int[nodeCount];
        var done = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++) { dist[i] = float.PositiveInfinity; prev[i] = -1; prevEdge[i] = -1; }
        dist[from] = 0f;
        for (int step = 0; step < nodeCount; step++)
        {
            int u = -1;
            for (int i = 0; i < nodeCount; i++) // lowest index wins ties
                if (!done[i] && !float.IsPositiveInfinity(dist[i]) && (u < 0 || dist[i] < dist[u])) u = i;
            if (u < 0) break;
            done[u] = true;
            for (int e = 0; e < edges.Count; e++)
            {
                var (a, b, len) = edges[e];
                int v = a == u ? b : b == u ? a : -1;
                if (v < 0 || done[v]) continue;
                float nd = dist[u] + len;
                if (nd < dist[v] - 1e-6f || (System.Math.Abs(nd - dist[v]) <= 1e-6f && u < prev[v])) { dist[v] = nd; prev[v] = u; prevEdge[v] = e; }
            }
        }
        if (float.IsPositiveInfinity(dist[to])) return false;
        length = dist[to];
        var rn = new List<int>();
        var re = new List<int>();
        for (int n = to; n != -1; n = prev[n]) { rn.Add(n); if (prevEdge[n] >= 0) re.Add(prevEdge[n]); }
        rn.Reverse();
        re.Reverse();
        nodes?.AddRange(rn);
        edgeIds?.AddRange(re);
        return true;
    }

    /// <summary>Route length, or -1 when not connected.</summary>
    public float Distance(int from, int to) => Route(from, to, null, null, out float len) ? len : -1f;
}

public static class VentRules
{
    public struct Plan
    {
        public bool found;
        public int entry, exit;
        public float seconds;   // total time by vent, penalties included
        public float advantage; // seconds saved against going on foot
    }

    /// <summary>
    /// Picks the entry and exit that get the creature to its destination soonest, or none when the vent is not meaningfully better
    /// than walking. Times: walk to the entry, ride the duct, walk from the exit to the destination, plus per-entrance penalties
    /// (recent reuse, fairness). Needs to save at least <paramref name="minAdvantage"/> seconds AND finish within
    /// <paramref name="maxFraction"/> of the walking time. Distances &lt; 0 mean unreachable; an infinite penalty rejects that entrance.
    /// Nothing about any player's position goes in except through those penalties.
    /// </summary>
    public static Plan Choose(VentGraph graph, IList<float> entryMetres, IList<float> exitMetres, IList<float> entryPenalty, IList<float> exitPenalty,
        float groundSeconds, float walkSpeed, float ventSpeed, float minAdvantage, float maxFraction)
    {
        var best = new Plan { seconds = float.PositiveInfinity };
        int n = entryMetres.Count;
        for (int i = 0; i < n; i++)
        {
            if (entryMetres[i] < 0f || float.IsPositiveInfinity(entryPenalty[i])) continue;
            for (int j = 0; j < n; j++)
            {
                if (i == j || exitMetres[j] < 0f || float.IsPositiveInfinity(exitPenalty[j])) continue;
                float route = graph.Distance(i, j);
                if (route < 0f) continue;
                float seconds = entryMetres[i] / walkSpeed + entryPenalty[i] + route / ventSpeed + exitMetres[j] / walkSpeed + exitPenalty[j];
                if (seconds < best.seconds - 1e-3f) best = new Plan { found = true, entry = i, exit = j, seconds = seconds };
                // equal within a millisecond: the first found (lowest entry, then exit) stays
            }
        }
        if (!best.found) return default;
        best.advantage = groundSeconds - best.seconds;
        if (best.advantage < minAdvantage || best.seconds > groundSeconds * maxFraction) return default;
        return best;
    }

    /// <summary>
    /// The exit that is best from a node the creature is already at (a junction), excluding some exits. Used to re-plan in the duct
    /// when new evidence arrives, or when the chosen exit is blocked. -1 when none.
    /// </summary>
    public static int BestExitFrom(VentGraph graph, int fromNode, IList<float> exitMetres, IList<float> exitPenalty, float walkSpeed, float ventSpeed,
        ICollection<int> excluded)
    {
        int best = -1;
        float bestSeconds = float.PositiveInfinity;
        for (int j = 0; j < exitMetres.Count; j++)
        {
            if (exitMetres[j] < 0f || float.IsPositiveInfinity(exitPenalty[j]) || (excluded != null && excluded.Contains(j))) continue;
            float route = fromNode == j ? 0f : graph.Distance(fromNode, j);
            if (route < 0f) continue;
            float seconds = route / ventSpeed + exitMetres[j] / walkSpeed + exitPenalty[j];
            if (seconds < bestSeconds - 1e-3f) { bestSeconds = seconds; best = j; }
        }
        return best;
    }

    /// <summary>
    /// Fairness of emerging at an exit. Infinity (reject) when a living player is closer than <paramref name="minSafe"/>; a penalty when
    /// one is close enough to have it in direct view, unless the creature itself was already hunting evidence at that exit.
    /// </summary>
    public static float ExitPenalty(float nearestPlayerDistance, bool playerHasView, bool pursuingHere, float minSafe, float viewDistance, float viewPenaltySeconds)
    {
        if (nearestPlayerDistance < minSafe) return float.PositiveInfinity;
        if (playerHasView && nearestPlayerDistance < viewDistance && !pursuingHere) return viewPenaltySeconds;
        return 0f;
    }

    /// <summary>
    /// Penalty for using an entrance that was used recently (most recent last): heavier the more recent, so the same entrance or
    /// exit is not picked straight away again.
    /// </summary>
    public static float ReusePenalty(int entrance, IList<int> recent, float perUseSeconds)
    {
        float total = 0f;
        for (int k = recent.Count - 1, age = 0; k >= 0; k--, age++)
            if (recent[k] == entrance) total += perUseSeconds / (1f + age);
        return total;
    }

    public static bool CooldownReady(double now, double lastEnd, double cooldown) => now - lastEnd >= cooldown;

    /// <summary>
    /// Fairness inputs from EVERY living player, each judged on its own: the nearest distance is the minimum over all of them, and
    /// the exit counts as watched if ANY player inside <paramref name="viewDistance"/> has an unobstructed view of it. A closer player
    /// looking away does not hide a farther one who is looking. Used only to reject or penalise an exit, never as evidence.
    /// </summary>
    public static void AggregateWatch(IList<float> distances, IList<bool> views, int count, float viewDistance, out float nearest, out bool watched)
    {
        nearest = float.MaxValue;
        watched = false;
        for (int i = 0; i < count; i++)
        {
            if (distances[i] < nearest) nearest = distances[i];
            if (views[i] && distances[i] < viewDistance) watched = true;
        }
    }

    public static float RemainingSeconds(float totalMetres, float travelledMetres, float speed) =>
        speed <= 0f ? float.PositiveInfinity : System.Math.Max(0f, totalMetres - travelledMetres) / speed;
}

/// <summary>
/// When the creature WANTS to use a vent (host only, seeded). After each trip (and at the round start) one random value u in [0, 1)
/// is drawn from the round's seed; the time until vent desire reaches 1 is then a point in a range chosen by how alert it is
/// (calm hunting is slower, heightened or searching is quicker) and shortened by frustration, never below an absolute minimum.
/// Desire only ENCOURAGES the creature to consider a vent: the route, fairness and chase rules still decide.
/// </summary>
public static class VentCadence
{
    /// <summary>Seconds after the previous trip (or the round start) at which desire reaches 1.</summary>
    public static float Interval(bool firstOfRound, float alert, float u, float firstMin, float firstMax, float calmMin, float calmMax,
        float alertMin, float alertMax, float frustration, float frustratedFactor, float minInterval)
    {
        u = Clamp01(u);
        if (firstOfRound) return firstMin + (firstMax - firstMin) * u;
        float calm = calmMin + (calmMax - calmMin) * u;
        float high = alertMin + (alertMax - alertMin) * u;
        float t = calm + (high - calm) * Clamp01(alert);
        t *= 1f - Clamp01(frustration) * (1f - Clamp01(frustratedFactor));
        return System.Math.Max(minInterval, t);
    }

    /// <summary>0 straight after a trip, 1 when the interval has passed, above 1 the longer it has waited.</summary>
    public static float Desire(double secondsSinceTrip, float interval) =>
        interval <= 0f ? 1f : (float)System.Math.Max(0.0, secondsSinceTrip) / interval;

    /// <summary>The hard gates no desire can override: never with a confirmed sighting, never in a chase, never inside the minimum interval.</summary>
    public static bool MayConsider(bool confirmedSight, bool chasing, double now, double lastTripEnd, float minInterval) =>
        !confirmedSight && !chasing && now - lastTripEnd >= minInterval;

    /// <summary>Seconds a WANTED trip may be slower than walking and still be taken: none until desire reaches 1, up to twice the bias later.</summary>
    public static float Bias(float desire, float biasSeconds) => desire < 1f ? 0f : biasSeconds * System.Math.Min(2f, desire);

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

/// <summary>
/// The best accepted evidence heard while travelling in a duct, held until the next junction (where a turn is possible). At most
/// one is kept: a higher-priority offer (a stronger sound, or the pursued player's own trail) replaces a lower one, and an expired
/// one is replaced by anything. Taking it clears it.
/// </summary>
public sealed class PendingEvidence<T>
{
    public bool Has { get; private set; }
    public T Value { get; private set; }
    public float Priority { get; private set; }
    public double Time { get; private set; }

    /// <summary>Returns true when the offer was kept.</summary>
    public bool Offer(T value, float priority, double time, double now, double maxAge)
    {
        if (Has && now - Time <= maxAge && priority < Priority) return false;
        Has = true;
        Value = value;
        Priority = priority;
        Time = time;
        return true;
    }

    public bool Expired(double now, double maxAge) => Has && now - Time > maxAge;

    /// <summary>The pending evidence if it is still fresh enough; it is cleared either way.</summary>
    public bool TryTake(double now, double maxAge, out T value)
    {
        bool ok = Has && now - Time <= maxAge;
        value = ok ? Value : default;
        Clear();
        return ok;
    }

    public void Clear()
    {
        Has = false;
        Value = default;
        Priority = 0f;
        Time = 0;
    }
}

/// <summary>
/// Structural checks on the vent network, pure (Editor/Tests/VentRulesTests.cs). Edges are undirected, as the graph treats them: a duct can
/// be travelled either way. The check never changes anything, and the same input always gives the same report.
/// </summary>
public static class VentValidation
{
    public sealed class Report
    {
        public int nodes, entrances, edges, validEdges, components;
        public readonly List<string> problems = new();
        public bool Valid => problems.Count == 0;
    }

    /// <summary>
    /// <paramref name="present"/>: which node slots hold a real node (an entrance with its component and top point, a junction). Each edge
    /// is a pair of node indices, -1 for an end that is missing or not a registered node. Entrances are nodes 0..entranceCount-1.
    /// <paramref name="ids"/> (optional) are the entrances' stable ids, which must equal their position and be unique.
    /// </summary>
    public static Report Check(IList<bool> present, int entranceCount, IList<(int a, int b)> edges, IList<int> ids = null)
    {
        var r = new Report { nodes = present.Count, entrances = entranceCount, edges = edges.Count };
        for (int i = 0; i < present.Count; i++)
            if (!present[i]) r.problems.Add($"node {i} ({(i < entranceCount ? "entrance" : "junction")}) is missing");

        if (ids != null)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < ids.Count; i++)
            {
                if (!seen.Add(ids[i])) r.problems.Add($"entrance id {ids[i]} is used more than once");
                else if (ids[i] != i) r.problems.Add($"entrance at position {i} has id {ids[i]}; ids must match their position");
            }
        }

        var parent = new int[present.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var degree = new int[present.Count];
        var pairs = new HashSet<(int, int)>();
        for (int k = 0; k < edges.Count; k++)
        {
            var (a, b) = edges[k];
            bool aOk = a >= 0 && a < present.Count && present[a], bOk = b >= 0 && b < present.Count && present[b];
            if (!aOk) r.problems.Add($"edge {k}: start is {(a < 0 ? "missing or not a registered node" : $"node {a}, which is missing")}");
            if (!bOk) r.problems.Add($"edge {k}: end is {(b < 0 ? "missing or not a registered node" : $"node {b}, which is missing")}");
            if (!aOk || !bOk) continue;
            if (a == b) { r.problems.Add($"edge {k}: starts and ends at node {a}"); continue; }
            if (!pairs.Add((System.Math.Min(a, b), System.Math.Max(a, b)))) { r.problems.Add($"edge {k}: duplicates the run between nodes {System.Math.Min(a, b)} and {System.Math.Max(a, b)}"); continue; }
            r.validEdges++;
            degree[a]++;
            degree[b]++;
            parent[Find(a)] = Find(b);
        }

        var roots = new HashSet<int>();
        for (int i = 0; i < present.Count; i++) if (present[i]) roots.Add(Find(i));
        r.components = roots.Count;
        if (roots.Count > 1) r.problems.Add($"the network is split into {roots.Count} separate parts");
        for (int i = 0; i < entranceCount && i < present.Count; i++)
        {
            if (!present[i]) continue;
            if (degree[i] == 0) { r.problems.Add($"entrance {i} has no duct"); continue; }
            bool otherExit = false;
            for (int j = 0; j < entranceCount && j < present.Count && !otherExit; j++)
                otherExit = j != i && present[j] && Find(j) == Find(i);
            if (!otherExit) r.problems.Add($"entrance {i} has no other entrance to reach");
        }
        return r;
    }
}

// ---------- Vent commitment and evidence priority (pure; Editor/Tests/VentPriorityTests.cs) ----------

/// <summary>
/// How committed a vent trip is. THE COMMIT POINT is CommitVent(): the moment the agent and collider go off and the creature walks into the
/// wall. Before it (approaching the mouth, the short wind-up at it) the trip is a plan and any important evidence cancels it; after it the
/// creature is physically in the duct and can only change its route; once it starts to come out it finishes coming out.
/// </summary>
public enum VentCommitment : byte { None, Approaching, PreEntry, InDuct, Emerging }

/// <summary>What a piece of evidence is, for the vent decision (the host classifies each heard sound; sight is classified by its own code).</summary>
public enum VentEvidenceKind : byte { Incidental, Decoy, PlayerWalk, PlayerSprint, PlayerBreath, PlayerHeavyBreath, PlayerImpact, PursuedTrail, WitnessedHiding, DirectSight }

public enum VentEvidenceAction : byte { Ignore, Store, Reroute, Cancel }

public struct VentEvidenceTuning
{
    public float cancelSprintDistance, cancelWalkDistance, cancelBreathDistance, cancelImpactStrength, decoyCancelStrength, minStoreStrength, minCancelStrength, maxAgeSeconds;

    public static VentEvidenceTuning Default => new()
    {
        cancelSprintDistance = 16f, cancelWalkDistance = 8f, cancelBreathDistance = 4f, cancelImpactStrength = 0.5f, decoyCancelStrength = 0.6f,
        minStoreStrength = 0.15f, minCancelStrength = 0.25f, maxAgeSeconds = 1.5f,
    };
}

public static class VentEvidenceRules
{
    /// <summary>
    /// The one rule for what evidence does to a vent trip. Before the commit point: direct sight, a watched hiding-place entry and the fresh
    /// trail of the player being pursued ALWAYS cancel it; a player's sprint nearby, close walking while hunting, close heavy breathing and a
    /// strong player-made impact cancel it; a noisemaker only when no player evidence is held and it is strong; weak or distant sounds, doors,
    /// machinery and spam never do (the stronger of those is kept for after the trip). In the duct the same evidence changes the intended exit
    /// instead (Reroute) at the next legitimate decision. While emerging it is only stored. Old evidence is ignored.
    /// </summary>
    public static VentEvidenceAction Decide(VentCommitment commit, VentEvidenceKind kind, float strength, float ageSeconds, float distance, bool hunting,
        bool playerEvidenceHeld, in VentEvidenceTuning t)
    {
        if (commit == VentCommitment.None) return VentEvidenceAction.Ignore;
        if (ageSeconds > t.maxAgeSeconds || ageSeconds < 0f) return VentEvidenceAction.Ignore;
        bool pre = commit is VentCommitment.Approaching or VentCommitment.PreEntry;

        // Sight and its direct consequences outrank any plan before the commit point, whatever their strength or margin.
        if (kind is VentEvidenceKind.DirectSight or VentEvidenceKind.WitnessedHiding or VentEvidenceKind.PursuedTrail)
            return pre ? VentEvidenceAction.Cancel : commit == VentCommitment.InDuct ? VentEvidenceAction.Reroute : VentEvidenceAction.Store;

        if (kind == VentEvidenceKind.Incidental || strength < t.minStoreStrength) return VentEvidenceAction.Ignore;

        if (pre)
        {
            bool cancel = kind switch
            {
                VentEvidenceKind.PlayerSprint => distance <= t.cancelSprintDistance && strength >= t.minCancelStrength,
                VentEvidenceKind.PlayerWalk => hunting && distance <= t.cancelWalkDistance && strength >= t.minCancelStrength,
                VentEvidenceKind.PlayerHeavyBreath => distance <= t.cancelBreathDistance && strength >= t.minCancelStrength,
                VentEvidenceKind.PlayerImpact => strength >= t.cancelImpactStrength,
                VentEvidenceKind.Decoy => !playerEvidenceHeld && strength >= t.decoyCancelStrength,
                _ => false,
            };
            if (cancel) return VentEvidenceAction.Cancel;
            // Not enough to give up the trip: keep it for later unless it is hardly anything.
            return kind == VentEvidenceKind.PlayerBreath || (kind == VentEvidenceKind.Decoy && playerEvidenceHeld) ? VentEvidenceAction.Ignore : VentEvidenceAction.Store;
        }

        if (commit == VentCommitment.InDuct)
            return kind == VentEvidenceKind.Decoy && playerEvidenceHeld ? VentEvidenceAction.Store
                : kind == VentEvidenceKind.PlayerBreath && distance > t.cancelBreathDistance ? VentEvidenceAction.Store
                : VentEvidenceAction.Reroute;
        return VentEvidenceAction.Store; // emerging: finish coming out, then act on it
    }

    /// <summary>Held evidence from a player is only usable while that player is still in play (alive, not escaped, connected); other sounds have no such condition.</summary>
    public static bool Usable(bool fromPlayer, bool playerInPlay) => !fromPlayer || playerInPlay;

    /// <summary>Maps the replicated phase (and whether the creature has committed) onto the commitment level.</summary>
    public static VentCommitment CommitmentOf(VentPhase phase, bool committed) => phase switch
    {
        VentPhase.Approaching => VentCommitment.Approaching,
        VentPhase.Entering => committed ? VentCommitment.InDuct : VentCommitment.PreEntry,
        VentPhase.Travelling or VentPhase.Preparing => VentCommitment.InDuct,
        VentPhase.Exiting => VentCommitment.Emerging,
        _ => VentCommitment.None,
    };
}

/// <summary>
/// One trip as a small state machine, so its transitions are checked in one place: it can be cancelled only before the commit point, a
/// cancelled attempt starts a retry cooldown (so the same vent is not picked again at once) and never counts as a trip, a late callback
/// cannot commit a trip that was cancelled, and a committed trip can only change its exit (a bounded number of times).
/// </summary>
public sealed class VentAttempt
{
    public VentPhase Phase { get; private set; }
    public bool Committed { get; private set; }
    public int Entry { get; private set; } = -1;
    public int Exit { get; private set; } = -1;
    public int Reroutes { get; private set; }
    public double RetryUntil { get; private set; } = double.NegativeInfinity;
    public int Cancelled { get; private set; }

    public bool Active => Entry >= 0;
    public VentCommitment Commitment => VentEvidenceRules.CommitmentOf(Phase, Committed);
    public bool Interruptible => Commitment is VentCommitment.Approaching or VentCommitment.PreEntry;

    public bool MayBegin(double now) => !Active && now >= RetryUntil;

    public bool Begin(int entry, int exit, double now)
    {
        if (!MayBegin(now) || entry < 0 || exit < 0) return false;
        Entry = entry;
        Exit = exit;
        Committed = false;
        Reroutes = 0;
        Phase = VentPhase.Approaching;
        return true;
    }

    public void SetPhase(VentPhase phase) => Phase = phase;

    /// <summary>The commit point. False when the attempt is not at the mouth any more (cancelled, reset, or already committed): a stale call does nothing.</summary>
    public bool Commit()
    {
        if (!Active || Committed || Phase != VentPhase.Entering) return false;
        Committed = true;
        return true;
    }

    /// <summary>Cancels a trip that has not committed. False (nothing changes) once it has.</summary>
    public bool Cancel(double now, float retrySeconds)
    {
        if (!Active || Committed) return false;
        Clear();
        RetryUntil = now + System.Math.Max(0f, retrySeconds);
        Cancelled++;
        return true;
    }

    /// <summary>Changes the intended exit of a committed trip, at most <paramref name="maxReroutes"/> times. False when not allowed or not different.</summary>
    public bool Reroute(int newExit, int maxReroutes)
    {
        if (!Active || !Committed || newExit < 0 || newExit == Exit || Reroutes >= maxReroutes) return false;
        Exit = newExit;
        Reroutes++;
        return true;
    }

    /// <summary>A completed trip (no retry cooldown: the ordinary cadence applies).</summary>
    public void Finish() => Clear();

    /// <summary>Round reset: everything, including the retry cooldown.</summary>
    public void Reset()
    {
        Clear();
        RetryUntil = double.NegativeInfinity;
        Cancelled = 0;
    }

    void Clear()
    {
        Phase = VentPhase.None;
        Committed = false;
        Entry = Exit = -1;
        Reroutes = 0;
    }
}
