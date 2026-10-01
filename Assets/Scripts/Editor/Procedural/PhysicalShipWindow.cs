using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Alien > Procedural Ship > Physical Ship. Generates the logical graph for a seed, places it on the grid with the greybox library's rooms,
/// and shows the result as a top-down map: rooms in their sector colour with ids, corridors (crossings marked), logical edges, sockets (green:
/// connected, grey: sealed, red: a hole) and every validation finding. A preview needs no scene; "Build Scene" writes ProceduralShipTest.unity.
/// </summary>
public class PhysicalShipWindow : EditorWindow
{
    ShipGenerationProfile profile;
    GreyboxLibrary library;
    int seed = 1, scenarioSeed = 1, roundSeed = 1, batchSize = 50;
    readonly DirectorSettings director = new();
    readonly PlacementSettings settings = new();
    PhysicalShipBuilder.Generated generated;
    string report = "", batchNote = "";
    bool showEdges = true, showSockets = true, showLabels = true, showSettings;
    Vector2 scroll;
    GUIStyle label;

    [MenuItem("Alien/Procedural Ship/Physical Ship")]
    static void Open() => GetWindow<PhysicalShipWindow>("Physical Ship");

    [MenuItem("Alien/Procedural Ship/Build Procedural Ship Test Scene")]
    static void BuildFromMenu()
    {
        var w = GetWindow<PhysicalShipWindow>("Physical Ship");
        w.Build();
    }

    void OnEnable()
    {
        profile ??= AssetDatabase.LoadAssetAtPath<ShipGenerationProfile>(RoomDefinitionSetup.ProfilePath);
        library ??= AssetDatabase.LoadAssetAtPath<GreyboxLibrary>(GreyboxLibraryBuilder.LibraryPath);
    }

    PhysicalShipBuilder.Request Request() => new() { seed = seed, scenarioSeed = scenarioSeed, director = director.Clone(), profile = profile, library = library, placement = settings.Clone() };

    void Generate()
    {
        var req = Request();
        if (library == null)
        {
            // A preview does not need prefabs: the blueprints give the same templates the default prefabs report.
            var gg = req.profile != null ? req.profile.CreateGenerator() : new ShipGraphGenerator(DefaultRoomCatalogue.Create());
            generated = new PhysicalShipBuilder.Generated { graphGenerator = gg, catalogue = gg.Specs.ToList(), graph = gg.Generate(seed) };
            if (generated.graph.success)
            {
                generated.placement = new ShipPlacer(TemplateLibrary.FromBlueprints(), generated.catalogue, req.placement).Place(generated.graph.graph);
                if (!generated.placement.success) generated.error = generated.placement.Summary;
                else
                {
                    var layout = generated.placement.layout;
                    var specs = generated.catalogue.ToDictionary(x => x.id);
                    generated.directorInput = new DirectorInput { graph = generated.graph.graph, layoutSeed = seed, templateOf = i => layout.rooms[i].template, specOf = id => specs[id] };
                    generated.scenario = EscapeDirector.Generate(generated.directorInput, scenarioSeed, req.director);
                }
            }
            else generated.error = generated.graph.Summary;
        }
        else generated = PhysicalShipBuilder.Generate(req);
        report = Describe();
    }

    string Describe()
    {
        if (generated == null) return "";
        var sb = new System.Text.StringBuilder();
        if (generated.graph != null) sb.AppendLine(generated.graph.Summary);
        if (generated.error != null) sb.AppendLine(generated.error);
        if (generated.placement != null && generated.placement.success)
        {
            var l = generated.placement.layout;
            sb.AppendLine(generated.placement.Summary);
            sb.AppendLine($"{l.WidthCells * l.settings.cellSize:0} x {l.DepthCells * l.settings.cellSize:0} m; {l.rooms.Count} rooms; {l.connections.Count(c => c.mode == ConnectionMode.Direct)} direct connections, {l.connections.Count(c => c.mode == ConnectionMode.Corridor)} corridors; spine about {l.SpineLengthMetres():0} m");
            foreach (var r in l.rooms) sb.AppendLine($"  {l.graph.nodes[r.node].id,-26} {r.template.id,-24} cell {r.origin} turn {r.rot * 90} deg  {r.SizeX * l.settings.cellSize:0}x{r.SizeZ * l.settings.cellSize:0} m");
            foreach (var i in generated.placement.report.issues) sb.AppendLine(i.ToString());
        }
        if (generated.scenario != null)
        {
            sb.AppendLine();
            sb.AppendLine(ScenarioReport.Describe(generated.graph.graph, generated.scenario));
            if (generated.scenario.success)
            {
                sb.AppendLine($"Round seed {roundSeed}: where each item rests this round (RoundManager.fixedSeed = {roundSeed} reproduces it)");
                foreach (var it in generated.scenario.scenario.items.Where(i => i.candidates.Count > 0))
                {
                    var c = it.candidates[ScenarioRound.PickIndex(roundSeed, it.id, it.candidates.Count)];
                    sb.AppendLine($"  {it.id}: {generated.graph.graph.nodes[c.node].id} / {c.anchorId} ({c.semantic})");
                }
            }
        }
        return sb.ToString();
    }

    // A batch audit of seeds from Seed (graph, placement and scenario) with the window's profile and library; the full report goes to Logs.
    void Batch()
    {
        var audit = ProceduralAuditMenu.RunAudit(seed, batchSize, true, scenarioSeed);
        batchNote = audit.Summary() + $"Full report: {ProceduralAuditMenu.ReportPath}";
    }

    void Build()
    {
        if (library == null) { Debug.LogError("Run Alien > Procedural Ship > Build Greybox Library first."); return; }
        // Not inside OnGUI: replacing the open scene while a layout group is open is what raised "EndLayoutGroup: BeginLayoutGroup must be called first".
        var request = Request();
        EditorApplication.delayCall += () => PhysicalShipBuilder.BuildScene(request);
        GUIUtility.ExitGUI();
    }

    void OnGUI()
    {
        label ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.black }, fontSize = 9 };
        profile = (ShipGenerationProfile)EditorGUILayout.ObjectField("Room profile", profile, typeof(ShipGenerationProfile), false);
        library = (GreyboxLibrary)EditorGUILayout.ObjectField("Greybox library", library, typeof(GreyboxLibrary), false);
        if (library == null) EditorGUILayout.HelpBox("No Greybox Library: previews use the built-in blueprints. Run Alien > Procedural Ship > Build Greybox Library to create the prefabs before building the scene.", MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        seed = EditorGUILayout.IntField("Layout seed", seed);
        scenarioSeed = EditorGUILayout.IntField("Scenario seed", scenarioSeed);
        roundSeed = EditorGUILayout.IntField("Round seed", roundSeed);
        if (GUILayout.Button("Generate", GUILayout.Width(80))) Generate();
        if (GUILayout.Button("Random", GUILayout.Width(65))) { seed = new System.Random(System.Environment.TickCount).Next(); Generate(); }
        if (GUILayout.Button("<", GUILayout.Width(28))) { seed--; Generate(); }
        if (GUILayout.Button(">", GUILayout.Width(28))) { seed++; Generate(); }
        if (GUILayout.Button("Next scenario", GUILayout.Width(100))) { scenarioSeed++; Generate(); }
        EditorGUILayout.EndHorizontal();

        showSettings = EditorGUILayout.Foldout(showSettings, "Placement settings", true);
        if (showSettings)
        {
            settings.maxExtentX = EditorGUILayout.IntField("Boundary X (cells)", settings.maxExtentX);
            settings.maxExtentZ = EditorGUILayout.IntField("Boundary Z (cells)", settings.maxExtentZ);
            settings.maxAttempts = EditorGUILayout.IntField("Max attempts", settings.maxAttempts);
            settings.maxStepsPerAttempt = EditorGUILayout.IntField("Steps per attempt", settings.maxStepsPerAttempt);
            settings.corridorMinCells = EditorGUILayout.IntField("Corridor min (cells)", settings.corridorMinCells);
            settings.corridorMaxCells = EditorGUILayout.IntField("Corridor preferred max (cells)", settings.corridorMaxCells);
            settings.directChance = EditorGUILayout.Slider("Direct connection chance", settings.directChance, 0f, 1f);
            settings.minPodSeparationCells = EditorGUILayout.IntField("Escape bay separation (cells)", settings.minPodSeparationCells);
            settings.allowCrossings = EditorGUILayout.Toggle("Allow corridor crossings", settings.allowCrossings);
        }

        EditorGUILayout.BeginHorizontal();
        batchSize = Mathf.Clamp(EditorGUILayout.IntField("Batch check", batchSize), 1, 1000);
        if (GUILayout.Button($"Audit {batchSize} seeds from Seed", GUILayout.Width(190))) Batch();
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(batchNote)) EditorGUILayout.HelpBox(batchNote, MessageType.None);

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(library == null);
        if (GUILayout.Button("Build ProceduralShipTest Scene...")) Build();
        EditorGUI.EndDisabledGroup();
        if (GUILayout.Button("Validate Open Scene")) PhysicalShipValidator.Validate(Object.FindFirstObjectByType<GeneratedShip>(), library, true).LogToConsole();
        if (GUILayout.Button("Copy Report")) EditorGUIUtility.systemCopyBuffer = report;
        if (GUILayout.Button("Export Report..."))
        {
            string path = EditorUtility.SaveFilePanel("Export ship and scenario report", "", $"ship-{seed}-scenario-{scenarioSeed}.txt", "txt");
            if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, report);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        showEdges = GUILayout.Toggle(showEdges, "Graph edges", GUILayout.Width(100));
        showSockets = GUILayout.Toggle(showSockets, "Sockets", GUILayout.Width(80));
        showLabels = GUILayout.Toggle(showLabels, "Labels", GUILayout.Width(80));
        EditorGUILayout.EndHorizontal();

        if (generated == null) { EditorGUILayout.HelpBox("Enter a seed and press Generate.", MessageType.Info); return; }
        if (generated.error != null) EditorGUILayout.HelpBox(generated.error, MessageType.Error);
        if (generated.placement != null && generated.placement.success)
        {
            var area = GUILayoutUtility.GetRect(position.width - 10, 360, GUILayout.ExpandWidth(true));
            DrawMap(area, generated.placement.layout, generated.placement.report, generated.scenario != null && generated.scenario.success ? generated.scenario.scenario : null);
        }
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.SelectableLabel(report, EditorStyles.textArea, GUILayout.ExpandHeight(true), GUILayout.MinHeight(14f * (report.Count(c => c == '\n') + 2)));
        EditorGUILayout.EndScrollView();
    }

    void DrawMap(Rect area, ShipLayout l, LayoutReport rep, Scenario sc)
    {
        EditorGUI.DrawRect(area, new Color(0.12f, 0.12f, 0.13f));
        float s = Mathf.Min((area.width - 12) / l.WidthCells, (area.height - 12) / l.DepthCells);
        Rect Cell(Int2 c, int w = 1, int d = 1) => new(area.x + 6 + (c.x - l.Min.x) * s, area.yMax - 6 - (c.z - l.Min.z + d) * s, w * s, d * s);
        Vector2 Centre(Int2 c2) => new(area.x + 6 + (c2.x * 0.5f + 0.5f - l.Min.x) * s, area.yMax - 6 - (c2.z * 0.5f + 0.5f - l.Min.z) * s);

        var pieces = CorridorMath.Pieces(l);
        foreach (var p in pieces) EditorGUI.DrawRect(Cell(p.cell), p.owners > 1 ? new Color(1f, 0.9f, 0.4f) : new Color(0.62f, 0.64f, 0.7f));
        var issueNodes = new HashSet<int>(rep.issues.SelectMany(i => i.nodes));
        foreach (var r in l.rooms)
        {
            var rect = Cell(r.origin, r.SizeX, r.SizeZ);
            var node = l.graph.nodes[r.node];
            EditorGUI.DrawRect(rect, ShipDebugView.SectorColours[(int)node.sector] * 0.85f);
            if (issueNodes.Contains(r.node)) Handles.DrawSolidRectangleWithOutline(rect, Color.clear, Color.red);
            if (showLabels && rect.width > 26) GUI.Label(rect, new GUIContent(node.displayName, $"{node.id} ({r.template.id}), {node.sector}"), label);
        }
        if (showSockets)
            foreach (var r in l.rooms)
                for (int sk = 0; sk < r.template.sockets.Count; sk++)
                {
                    bool used = l.SocketUsed(r.node, sk);
                    var cellRect = Cell(r.SocketCell(sk));
                    var mid = cellRect.center + new Vector2(Grid.Step(r.SocketSide(sk)).x, -Grid.Step(r.SocketSide(sk)).z) * s * 0.5f;
                    EditorGUI.DrawRect(new Rect(mid.x - 2, mid.y - 2, 4, 4), used ? Color.green : Color.gray);
                }
        if (sc != null)
        {
            // Escape scenario overlay: gated doors in orange, working pods outlined green and wrecked ones red, rooms where an item may lie get a dot.
            Handles.BeginGUI();
            foreach (var gate in sc.gates)
            {
                var e = l.graph.edges[gate.edge];
                Handles.color = new Color(1f, 0.55f, 0.1f);
                Handles.DrawAAPolyLine(4f, Centre(l.rooms[e.a].Centre2), Centre(l.rooms[e.b].Centre2));
            }
            foreach (var pod in sc.pods)
                Handles.DrawSolidRectangleWithOutline(Cell(l.rooms[pod.node].origin, l.rooms[pod.node].SizeX, l.rooms[pod.node].SizeZ), Color.clear, pod.Usable ? Color.green : Color.red);
            Handles.EndGUI();
            foreach (var it in sc.items.Where(i => i.critical))
                foreach (var c in it.candidates)
                {
                    var p = Centre(l.rooms[c.node].Centre2) + new Vector2(it.kind == ItemKind.Keycard ? -6f : 6f, 8f);
                    EditorGUI.DrawRect(new Rect(p.x - 3, p.y - 3, 6, 6), it.kind == ItemKind.Keycard ? new Color(0.1f, 0.9f, 1f) : new Color(1f, 0.5f, 0.1f));
                }
        }
        if (showEdges)
        {
            Handles.BeginGUI();
            Handles.color = new Color(1f, 1f, 1f, 0.7f);
            foreach (var e in l.graph.edges) Handles.DrawLine(Centre(l.rooms[e.a].Centre2), Centre(l.rooms[e.b].Centre2));
            Handles.EndGUI();
        }
    }
}
