using System.Linq;
using System.Text;

/// <summary>A concise, copyable text description of a generated graph and its validation (the editor tool's report).</summary>
public static class ShipGraphText
{
    public static string Describe(ShipGraphResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Ship graph, seed ").Append(result.seed).Append(": ").Append(result.success ? "VALID" : "FAILED").Append('\n');
        var g = result.graph;
        if (g == null)
        {
            sb.Append("No graph was produced.\n");
            foreach (var f in result.attemptFailures) sb.Append("  ").Append(f).Append('\n');
            return sb.ToString();
        }
        var r = result.report;
        sb.Append($"{g.NodeCount} rooms, {g.EdgeCount} connections, {r.loops} loops, route {r.routeLength}, side branches {r.sideBranches}, dead ends {r.deadEnds}, ")
          .Append($"furthest {r.maxDistance} from spawn, attempt {g.attempt + 1}, fingerprint {g.Fingerprint()}\n");

        int spawn = g.FirstOf(RoomCategory.PlayerStart);
        var dist = spawn >= 0 ? g.Distances(spawn) : new int[g.NodeCount];
        var onRoute = g.route.ToList();
        for (int sec = 0; sec < 4; sec++)
        {
            var sector = (ShipSector)sec;
            sb.Append('\n').Append(sector).Append(" sector\n");
            foreach (var n in g.nodes.Where(n => n.sector == sector).OrderBy(n => onRoute.IndexOf(n.index) < 0 ? 1000 + n.index : onRoute.IndexOf(n.index)))
            {
                int pos = onRoute.IndexOf(n.index);
                sb.Append(pos >= 0 ? $"  [route {pos,2}] " : "             ")
                  .Append($"{n.id,-26} {n.displayName,-32} {RoomSpec.RoleForDegree(n.Degree),-12} spawn {dist[n.index],2}  -> ")
                  .Append(string.Join(", ", n.neighbours.Select(v => g.nodes[v].id)))
                  .Append('\n');
            }
        }
        sb.Append("\nPrimary route: ").Append(string.Join(" > ", g.route.Select(i => g.nodes[i].id))).Append('\n');
        if (r.issues.Count > 0)
        {
            sb.Append("\nValidation:\n");
            foreach (var i in r.issues) sb.Append("  ").Append(i).Append('\n');
        }
        if (result.attemptFailures.Count > 0)
        {
            sb.Append("\nEarlier attempts:\n");
            foreach (var f in result.attemptFailures) sb.Append("  ").Append(f).Append('\n');
        }
        return sb.ToString();
    }
}
