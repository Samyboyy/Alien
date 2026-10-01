using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// A batch audit of the procedural pipeline over many seeds: graph generation (success, attempts, why attempts were rejected, topology
/// metrics), optionally physical placement and the escape scenario (stages, plans, route pressure). Pure, deterministic and cheap enough for
/// hundreds of seeds. <see cref="Summary"/> is a few lines for the Console; <see cref="Report"/> is the exportable detail (one row per seed).
/// </summary>
public sealed class ShipAudit
{
    public sealed class Options
    {
        public int firstSeed, count = 500;
        public bool place, scenario;
        public int scenarioSeed = 1;
        public IEnumerable<RoomSpec> catalogue;
        public ShipGraphSettings graphSettings;
        public TemplateLibrary templates;
        public PlacementSettings placement;
        public DirectorSettings director;
        /// <summary>Called after each seed with (done, total); return false to stop early (the editor's cancel button).</summary>
        public Func<int, int, bool> progress;
    }

    public sealed class Row
    {
        public int seed;
        public bool graphOk, placed, scenarioOk;
        public int attempts, rooms, edges, loops, deadEnds, route, maxDistance;
        public int crewMess = -1, bridgeSpawn = -1, engBridge = -1, bridgeFurther = -1;
        public string podPredecessors = "", podHosts = "";
        public bool verticalLobby;
        public int stages = -1, plans = -1, scenarioAttempts = -1;
        public string pressure = "";
        public float minPressure = -1;
        public int preferredMet, preferredTotal;
        public string fingerprint = "";
        public string failure = "";
    }

    public readonly List<Row> rows = new();
    public readonly Dictionary<string, int> rejections = new();
    public readonly Dictionary<string, int> podPredecessorCategories = new();
    public long milliseconds;

    static readonly Regex RoomId = new(@"[a-z_]+\.\d+");
    static readonly Regex Number = new(@"-?\d+(\.\d+)?");

    /// <summary>A rejection reason with the seed-specific parts (room ids, numbers) removed, so equal causes count together.</summary>
    public static string Category(string failure)
    {
        int colon = failure.IndexOf(':');
        string s = colon >= 0 && failure.StartsWith("attempt") ? failure.Substring(colon + 1).Trim() : failure;
        s = RoomId.Replace(s, "#room");
        s = Number.Replace(s, "#");
        return s.Length > 120 ? s.Substring(0, 120) : s;
    }

    public static ShipAudit Run(Options o)
    {
        var audit = new ShipAudit();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var catalogue = (o.catalogue ?? DefaultRoomCatalogue.Create()).ToList();
        var specById = catalogue.GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First());
        var gen = new ShipGraphGenerator(catalogue, o.graphSettings);
        var placer = o.place ? new ShipPlacer(o.templates ?? TemplateLibrary.FromBlueprints(), catalogue, o.placement) : null;
        for (int seed = o.firstSeed; seed < o.firstSeed + o.count; seed++)
        {
            if (o.progress != null && !o.progress(seed - o.firstSeed, o.count)) break;
            var row = new Row { seed = seed };
            audit.rows.Add(row);
            var r = gen.Generate(seed);
            row.attempts = r.attempts;
            foreach (var f in r.attemptFailures) audit.Count(audit.rejections, Category(f));
            if (!r.success) { row.failure = r.attemptFailures.Count > 0 ? r.attemptFailures[^1] : "no graph"; continue; }
            row.graphOk = true;
            Measure(r.graph, r.report, specById, row);
            foreach (var p in row.podPredecessors.Split(',', StringSplitOptions.RemoveEmptyEntries)) audit.Count(audit.podPredecessorCategories, p);
            if (placer == null) continue;
            var p2 = placer.Place(r.graph);
            row.placed = p2.success;
            if (!p2.success) { row.failure = "placement: " + (p2.attemptFailures.Count > 0 ? p2.attemptFailures[^1] : ""); continue; }
            if (!o.scenario) continue;
            var input = new DirectorInput { graph = r.graph, layoutSeed = seed, templateOf = i => p2.layout.rooms[i].template, specOf = id => specById[id] };
            var sc = EscapeDirector.Generate(input, o.scenarioSeed, o.director);
            row.scenarioAttempts = sc.attempts;
            foreach (var f in sc.rejections) audit.Count(audit.rejections, "scenario: " + Category(f));
            row.scenarioOk = sc.success;
            if (!sc.success) { row.failure = "scenario: " + (sc.rejections.Count > 0 ? sc.rejections[^1] : ""); continue; }
            row.stages = sc.solve.stages;
            row.plans = sc.solve.plans.Count;
            if (sc.solve.plans.Count > 0)
            {
                row.minPressure = sc.solve.plans.Min(p => p.pressure);
                row.pressure = string.Join("/", sc.solve.plans.Select(p => p.pressure.ToString("0.#", CultureInfo.InvariantCulture)));
            }
        }
        audit.milliseconds = sw.ElapsedMilliseconds;
        return audit;
    }

    void Count(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    /// <summary>The topology metrics of one graph (also used by tests).</summary>
    public static void Measure(ShipGraph g, GraphValidationReport rep, Dictionary<string, RoomSpec> specs, Row row)
    {
        row.rooms = g.NodeCount;
        row.edges = g.EdgeCount;
        row.loops = rep.loops;
        row.deadEnds = rep.deadEnds;
        row.route = rep.routeLength;
        row.maxDistance = rep.maxDistance;
        row.fingerprint = g.Fingerprint();
        int spawn = g.FirstOf(RoomCategory.PlayerStart), bridge = g.FirstOf(RoomCategory.Bridge), eng = g.FirstOf(RoomCategory.Engineering), mess = g.FirstOf(RoomCategory.MessHall);
        var dist = g.Distances(spawn);
        if (bridge >= 0) { row.bridgeSpawn = dist[bridge]; row.bridgeFurther = dist.Count(d => d > dist[bridge]); }
        if (bridge >= 0 && eng >= 0) row.engBridge = g.Distance(bridge, eng);
        var crews = g.AllOf(RoomCategory.CrewQuarters);
        if (mess >= 0 && crews.Count > 0) { var dm = g.Distances(mess); row.crewMess = crews.Min(c => dm[c]); }
        var pods = g.AllOf(RoomCategory.EscapePodBay);
        row.podPredecessors = string.Join(",", pods.Select(p => g.nodes[p].neighbours.Count > 0 ? g.nodes[g.nodes[p].neighbours[0]].category.ToString() : "none"));
        row.verticalLobby = g.nodes.Any(n => n.category == RoomCategory.VerticalLobby);
        (row.preferredMet, row.preferredTotal) = PreferredRelationships(g, specs);
    }

    /// <summary>
    /// How many rooms that declare preferred neighbours have at least one of them within two connections (met) out of how many such rooms have
    /// a preferred category present in the ship at all (total).
    /// </summary>
    public static (int met, int total) PreferredRelationships(ShipGraph g, Dictionary<string, RoomSpec> specs)
    {
        int met = 0, total = 0;
        foreach (var n in g.nodes)
        {
            if (!specs.TryGetValue(n.definitionId, out var spec) || spec.preferredNeighbours.Length == 0) continue;
            var targets = g.nodes.Where(o => o.index != n.index && spec.preferredNeighbours.Contains(o.category)).Select(o => o.index).ToList();
            if (targets.Count == 0) continue;
            total++;
            var d = g.Distances(n.index);
            if (targets.Any(t => d[t] >= 0 && d[t] <= 2)) met++;
        }
        return (met, total);
    }

    // ---------- Output ----------

    static string Stat(IEnumerable<int> values)
    {
        var v = values.ToList();
        if (v.Count == 0) return "-";
        return string.Format(CultureInfo.InvariantCulture, "min {0} mean {1:0.0} max {2}", v.Min(), v.Average(), v.Max());
    }

    static string StatF(IEnumerable<float> values)
    {
        var v = values.ToList();
        if (v.Count == 0) return "-";
        return string.Format(CultureInfo.InvariantCulture, "min {0:0.0} mean {1:0.0} max {2:0.0}", v.Min(), v.Average(), v.Max());
    }

    /// <summary>A compact summary (a dozen lines) for the Console.</summary>
    public string Summary()
    {
        var ok = rows.Where(r => r.graphOk).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Procedural audit: seeds {rows.FirstOrDefault()?.seed}..{rows.LastOrDefault()?.seed} ({rows.Count}) in {milliseconds} ms");
        sb.AppendLine($"  graphs valid {ok.Count}/{rows.Count}; attempts {Stat(ok.Select(r => r.attempts))}; rejected attempts {rejections.Where(k => !k.Key.StartsWith("scenario")).Sum(k => k.Value)}");
        sb.AppendLine($"  rooms {Stat(ok.Select(r => r.rooms))}; connections {Stat(ok.Select(r => r.edges))}; loops {Stat(ok.Select(r => r.loops))}; dead ends {Stat(ok.Select(r => r.deadEnds))}");
        sb.AppendLine($"  route {Stat(ok.Select(r => r.route))}; furthest from spawn {Stat(ok.Select(r => r.maxDistance))}");
        sb.AppendLine($"  bridge-spawn {Stat(ok.Select(r => r.bridgeSpawn))}; engineering-bridge {Stat(ok.Select(r => r.engBridge))}; crew-mess {Stat(ok.Where(r => r.crewMess >= 0).Select(r => r.crewMess))} (over 2: {ok.Count(r => r.crewMess > 2)})");
        int met = ok.Sum(r => r.preferredMet), tot = ok.Sum(r => r.preferredTotal);
        sb.AppendLine($"  preferred neighbours within 2: {met}/{tot} ({(tot > 0 ? 100.0 * met / tot : 0):0}%); ships with a vertical lobby: {ok.Count(r => r.verticalLobby)}; distinct fingerprints {ok.Select(r => r.fingerprint).Distinct().Count()}");
        sb.AppendLine($"  pod predecessors: {string.Join(", ", podPredecessorCategories.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(8).Select(k => $"{k.Key} {k.Value}"))}");
        if (rows.Any(r => r.placed || r.scenarioOk || r.stages >= 0))
        {
            var sc = rows.Where(r => r.scenarioOk).ToList();
            sb.AppendLine($"  placed {rows.Count(r => r.placed)}/{ok.Count}; scenarios solved {sc.Count}; stages {Stat(sc.Select(r => r.stages))}; plans {Stat(sc.Select(r => r.plans))}; scenario attempts {Stat(sc.Select(r => r.scenarioAttempts))}; lowest route pressure {StatF(sc.Select(r => r.minPressure))}");
        }
        sb.AppendLine("  top rejection causes:");
        foreach (var kv in rejections.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(8)) sb.AppendLine($"    {kv.Value,5}  {kv.Key}");
        return sb.ToString();
    }

    /// <summary>The full exportable report: the summary, every rejection cause, and one tab-separated row per seed.</summary>
    public string Report()
    {
        var sb = new StringBuilder(Summary());
        sb.AppendLine();
        sb.AppendLine("All rejection causes:");
        foreach (var kv in rejections.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)) sb.AppendLine($"  {kv.Value,5}  {kv.Key}");
        sb.AppendLine();
        sb.AppendLine("seed\tgraph\tattempts\trooms\tedges\tloops\tdeadEnds\troute\tmaxDist\tcrewMess\tbridgeSpawn\tengBridge\tpodPredecessors\tlobby\tpreferred\tplaced\tscenario\tstages\tplans\tpressure\tfingerprint\tfailure");
        foreach (var r in rows)
            sb.AppendLine(string.Join("\t", r.seed, r.graphOk ? "ok" : "FAIL", r.attempts, r.rooms, r.edges, r.loops, r.deadEnds, r.route, r.maxDistance, r.crewMess, r.bridgeSpawn, r.engBridge,
                r.podPredecessors, r.verticalLobby ? "yes" : "no", $"{r.preferredMet}/{r.preferredTotal}", r.placed ? "ok" : "-", r.scenarioOk ? "ok" : "-", r.stages, r.plans, r.pressure, r.fingerprint, r.failure));
        return sb.ToString();
    }
}
