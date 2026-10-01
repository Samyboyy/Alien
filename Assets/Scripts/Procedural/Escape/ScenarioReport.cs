using System.Linq;
using System.Text;

/// <summary>
/// The developer-only solution report: everything the generator decided and why. It spoils the whole scenario, so it is only ever produced by editor
/// tools and stored in editor-only fields; no player-facing code reads it.
/// </summary>
public static class ScenarioReport
{
    public static string Describe(ShipGraph g, ScenarioResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DEVELOPER SOLUTION REPORT (spoilers: never shown to players)");
        if (!r.success)
        {
            sb.AppendLine(r.Summary);
            foreach (var x in r.rejections.TakeLast(8)) sb.AppendLine("  " + x);
            return sb.ToString();
        }
        var sc = r.scenario;
        string N(int i) => g.nodes[i].id;
        sb.AppendLine($"layout seed {sc.layoutSeed}, scenario seed {sc.scenarioSeed}, attempt {sc.attempt + 1}; start in {N(sc.spawnNode)}");
        sb.AppendLine();
        sb.AppendLine("Escape pods");
        foreach (var p in sc.pods)
            sb.AppendLine($"  {N(p.node)}: {p.status}" + (p.Usable ? $", {(p.mode == LaunchMode.Loud ? "loud countdown launch" : "quiet slow manual release")}{(p.NeedsPower ? ", needs ship power" : "")}" : " (wrecked: no launch console)"));
        sb.AppendLine();
        sb.AppendLine("Gated doors");
        foreach (var gate in sc.gates)
            sb.AppendLine($"  {g.edges[gate.edge].id} (door in {N(gate.doorNode)}): {string.Join(" OR ", gate.options)}  - {gate.story}");
        sb.AppendLine();
        sb.AppendLine("Consoles");
        foreach (var c in sc.consoles)
            sb.AppendLine($"  {c.role}: {N(c.node)} / anchor {c.anchorId}{(c.doorEdge >= 0 ? $" (operates the door on {g.edges[c.doorEdge].id})" : "")}");
        sb.AppendLine();
        sb.AppendLine("Item placements (one candidate is picked each round from the round seed)");
        foreach (var it in sc.items)
        {
            sb.AppendLine($"  {it.id} [{(it.critical ? "CRITICAL" : "optional")}] {it.cls}:");
            foreach (var c in it.candidates) sb.AppendLine($"    {N(c.node)} / {c.anchorId} ({c.semantic}) weight {c.weight:0.0#}  - {c.rule}");
        }
        if (sc.feeds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Camera feeds (placeholder: configuration only)");
            foreach (var f in sc.feeds) sb.AppendLine($"  {N(f.node)} / {f.anchorId}: {(f.available ? "available" : "offline")}");
        }
        sb.AppendLine();
        sb.AppendLine($"Reachability stages (stage 0 = reachable from the start; {r.solve.stages} passes)");
        foreach (var grp in r.solve.roomStage.GroupBy(kv => kv.Value).OrderBy(x => x.Key))
            sb.AppendLine($"  stage {grp.Key}: {string.Join(", ", grp.Select(kv => N(kv.Key)))}");
        if (r.solve.unreachable.Count > 0) sb.AppendLine("  never reachable: " + string.Join(", ", r.solve.unreachable.Select(N)));
        sb.AppendLine("Events");
        foreach (var (what, stage) in r.solve.events) sb.AppendLine($"  stage {stage}: {what}");
        sb.AppendLine();
        sb.AppendLine("Intended solutions");
        foreach (var p in r.solve.plans)
        {
            sb.AppendLine($"  Route to {N(p.podNode)} ({p.mode}{(p.status == PodStatus.Damaged ? ", damaged" : "")}): pressure {p.pressure:0.0}; {p.hops} rooms; {p.stages} step(s); {p.loudActions} loud action(s); gates: " +
                          (p.gates.Count == 0 ? "none" : string.Join(", ", p.gates.Select(x => $"{g.edges[x.edge].id} via {x.easiest}"))));
            if (p.requirements.Count > 0) sb.AppendLine("    requires: " + string.Join("; ", p.requirements));
            if (p.breakdown.Count > 0) sb.AppendLine("    pressure: " + string.Join(", ", p.breakdown.Select(b => $"{b.what} {b.value:0.0}")));
        }
        sb.AppendLine("Route choices");
        foreach (var c in r.solve.choices) sb.AppendLine("  " + c);
        if (r.attempts > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"Rejected before this ({r.rejections.Count}):");
            foreach (var x in r.rejections.Take(10)) sb.AppendLine("  " + x);
        }
        return sb.ToString();
    }
}
