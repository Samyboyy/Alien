using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Alien > Procedural Ship > Graph Generator. Generates the logical ship graph for a seed from the profile asset (or the built-in default
/// catalogue when there is none), validates it, draws it, and gives a copyable text report. Purely an editor view: it creates no scene objects
/// and changes no asset.
/// </summary>
public class ShipGraphGeneratorWindow : EditorWindow
{
    ShipGenerationProfile profile;
    int seed = 1;
    int batchSize = 200;
    ShipGraphResult result;
    string report = "", previousFingerprint, determinismNote, batchNote;
    Vector2 scroll;
    GUIStyle nodeStyle, mono;

    static readonly Color[] SectorColours =
    {
        new(0.35f, 0.6f, 0.95f), new(0.45f, 0.8f, 0.55f), new(0.95f, 0.75f, 0.35f), new(0.9f, 0.45f, 0.4f),
    };

    [MenuItem("Alien/Procedural Ship/Graph Generator")]
    static void Open() => GetWindow<ShipGraphGeneratorWindow>("Ship Graph");

    void OnEnable()
    {
        if (profile == null) profile = AssetDatabase.LoadAssetAtPath<ShipGenerationProfile>(RoomDefinitionSetup.ProfilePath);
    }

    ShipGraphGenerator Generator() => profile != null ? profile.CreateGenerator() : new ShipGraphGenerator(DefaultRoomCatalogue.Create(), new ShipGraphSettings());

    void Generate(bool sameSeedCheck)
    {
        var gen = Generator();
        string before = result != null && result.seed == seed && result.graph != null ? result.graph.Fingerprint() : null;
        result = gen.Generate(seed);
        report = ShipGraphText.Describe(result);
        string now = result.graph?.Fingerprint();
        determinismNote = sameSeedCheck && before != null
            ? (before == now ? $"Regenerated: identical to the previous run ({now})." : $"Regenerated: DIFFERENT from the previous run ({before} -> {now}). Was the profile edited in between?")
            : null;
        previousFingerprint = now;
    }

    void Batch()
    {
        var gen = Generator();
        int ok = 0, worst = 0, worstSeed = 0;
        var failed = new List<int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < batchSize; i++)
        {
            var r = gen.Generate(seed + i);
            if (r.success) { ok++; if (r.attempts > worst) { worst = r.attempts; worstSeed = seed + i; } }
            else failed.Add(seed + i);
        }
        batchNote = $"Seeds {seed}..{seed + batchSize - 1}: {ok}/{batchSize} valid in {sw.ElapsedMilliseconds} ms; most attempts {worst} (seed {worstSeed})"
                    + (failed.Count > 0 ? $"; FAILED: {string.Join(", ", failed.Take(20))}" : "");
    }

    void OnGUI()
    {
        nodeStyle ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.black }, fontSize = 9 };
        mono ??= new GUIStyle(EditorStyles.textArea) { font = Font.CreateDynamicFontFromOSFont("Consolas", 11), wordWrap = false, richText = false };

        EditorGUILayout.BeginHorizontal();
        profile = (ShipGenerationProfile)EditorGUILayout.ObjectField("Profile", profile, typeof(ShipGenerationProfile), false);
        if (profile == null) EditorGUILayout.LabelField("(none: built-in defaults)", GUILayout.Width(170));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        seed = EditorGUILayout.IntField("Seed", seed);
        if (GUILayout.Button("Generate", GUILayout.Width(80))) Generate(false);
        if (GUILayout.Button("Regenerate", GUILayout.Width(85))) Generate(true);
        if (GUILayout.Button("Random Seed", GUILayout.Width(95)))
        {
            seed = new System.Random(System.Environment.TickCount).Next(); // the editor picks a seed; the generator never uses this generator
            Generate(false);
        }
        if (GUILayout.Button("Previous", GUILayout.Width(70))) { seed--; Generate(false); }
        if (GUILayout.Button("Next", GUILayout.Width(50))) { seed++; Generate(false); }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        batchSize = Mathf.Clamp(EditorGUILayout.IntField("Batch check", batchSize), 1, 5000);
        if (GUILayout.Button($"Check {batchSize} seeds from Seed", GUILayout.Width(200))) Batch();
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(batchNote)) EditorGUILayout.HelpBox(batchNote, MessageType.None);

        if (result == null) { EditorGUILayout.HelpBox("Enter a seed and press Generate.", MessageType.Info); return; }

        EditorGUILayout.HelpBox(result.Summary, result.success ? MessageType.Info : MessageType.Error);
        if (determinismNote != null) EditorGUILayout.HelpBox(determinismNote, determinismNote.Contains("DIFFERENT") ? MessageType.Error : MessageType.None);
        if (result.report != null)
            foreach (var i in result.report.issues.Take(12))
                EditorGUILayout.HelpBox(i.ToString(), i.severity == IssueSeverity.Error ? MessageType.Error : MessageType.Warning);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Copy Report")) EditorGUIUtility.systemCopyBuffer = report;
        if (GUILayout.Button("Export Report..."))
        {
            string path = EditorUtility.SaveFilePanel("Export ship graph report", "", $"ship-graph-{seed}.txt", "txt");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, report);
        }
        EditorGUILayout.EndHorizontal();

        if (result.graph != null)
        {
            var area = GUILayoutUtility.GetRect(position.width - 10, 330, GUILayout.ExpandWidth(true));
            DrawGraph(area, result.graph);
        }
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.SelectableLabel(report, mono, GUILayout.ExpandHeight(true), GUILayout.MinHeight(mono.lineHeight * (report.Count(c => c == '\n') + 2)));
        EditorGUILayout.EndScrollView();
    }

    // The route runs left (bow) to right (stern); side rooms stack above and below the route room they hang from, loop connectors sit between
    // the two route rooms they join. A display layout only: the graph has no coordinates.
    void DrawGraph(Rect area, ShipGraph g)
    {
        EditorGUI.DrawRect(area, new Color(0.13f, 0.13f, 0.14f));
        if (g.route.Count == 0) return;
        var pos = new Dictionary<int, Vector2>();
        float colW = (area.width - 40) / Mathf.Max(1, g.route.Count - 1);
        float midY = area.y + area.height * 0.5f, rowH = 46f;
        for (int k = 0; k < g.route.Count; k++) pos[g.route[k]] = new Vector2(area.x + 20 + k * colW, midY);

        // Breadth-first from the route: each side room is placed relative to the room it was reached from.
        var slots = new Dictionary<(int, int), int>();
        var queue = new Queue<int>(g.route);
        var depth = new Dictionary<int, int>();
        foreach (int r in g.route) depth[r] = 0;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            foreach (int v in g.nodes[u].neighbours.OrderBy(x => x))
            {
                if (depth.ContainsKey(v)) continue;
                depth[v] = depth[u] + 1;
                var routeNbrs = g.nodes[v].neighbours.Where(pos.ContainsKey).Where(x => depth.TryGetValue(x, out int dd) && dd == 0).ToList();
                float x = routeNbrs.Count >= 2 && depth[u] == 0 ? routeNbrs.Average(n => pos[n].x) : pos[u].x;
                int col = Mathf.RoundToInt((x - area.x) / Mathf.Max(1f, colW * 0.5f));
                int band = depth[v];
                var key = (col, band);
                slots.TryGetValue(key, out int used);
                slots[key] = used + 1;
                float sign = used % 2 == 0 ? -1f : 1f; // alternate above and below the route
                float y = midY + sign * rowH * (band + used / 2);
                pos[v] = new Vector2(x + (used / 2) * 10f, Mathf.Clamp(y, area.y + 14, area.yMax - 14));
                queue.Enqueue(v);
            }
        }

        var onRoute = new HashSet<int>(g.route);
        Handles.BeginGUI();
        foreach (var e in g.edges)
        {
            if (!pos.TryGetValue(e.a, out var a) || !pos.TryGetValue(e.b, out var b)) continue;
            bool routeEdge = onRoute.Contains(e.a) && onRoute.Contains(e.b) && System.Math.Abs(g.route.IndexOf(e.a) - g.route.IndexOf(e.b)) == 1;
            Handles.color = routeEdge ? new Color(1f, 1f, 1f, 0.9f) : new Color(0.7f, 0.7f, 0.7f, 0.6f);
            Handles.DrawAAPolyLine(routeEdge ? 4f : 2f, a, b);
        }
        Handles.EndGUI();
        foreach (var n in g.nodes)
        {
            if (!pos.TryGetValue(n.index, out var p)) continue;
            var r = new Rect(p.x - 34, p.y - 12, 68, 24);
            EditorGUI.DrawRect(r, SectorColours[(int)n.sector] * (onRoute.Contains(n.index) ? 1f : 0.8f));
            GUI.Label(r, new GUIContent(n.displayName, $"{n.id}\n{n.sector}, {n.Degree} connection(s)"), nodeStyle);
        }
        GUI.Label(new Rect(area.x + 6, area.y + 2, 400, 16), "Forward (blue)  Central (green)  Crew (yellow)  Industrial (red)   - route: bright", EditorStyles.whiteMiniLabel);
    }
}
