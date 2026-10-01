using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Turns a placed ship layout into a walkable greybox in Assets/Scenes/ProceduralShipTest.unity: room prefabs, corridor modules, wall seals,
/// player spawns, reserved creature anchors, the project's multiplayer set-up (NetworkManager, lobby camera) and a baked NavMesh.
/// Editor-time only: nothing is generated at run time. The scene is owned by this tool: rerunning replaces it after a confirmation, and Ship and
/// NetTest are never touched.
/// </summary>
public static class PhysicalShipBuilder
{
    internal const string ScenePath = "Assets/Scenes/ProceduralShipTest.unity";
    const string NavMeshFolder = "Assets/Scenes/ProceduralShipTest"; // where the NavMesh bake stores its asset
    const string AtmosphereProfilePath = "Assets/Settings/ShipAtmosphereProfile.asset";

    /// <summary>Everything one build needs, as chosen in the window.</summary>
    public sealed class Request
    {
        /// <summary>The layout seed (which ship).</summary>
        public int seed;
        /// <summary>The scenario seed (which escape scenario on that ship). Both seeds reproduce a problem exactly.</summary>
        public int scenarioSeed = 1;
        public DirectorSettings director = new();
        public ShipGenerationProfile profile;
        public GreyboxLibrary library;
        public PlacementSettings placement = new();
    }

    public sealed class Generated
    {
        public ShipGraphGenerator graphGenerator;
        public ShipGraphResult graph;
        public ShipPlacementResult placement;
        public ScenarioResult scenario;
        public DirectorInput directorInput;
        public List<RoomSpec> catalogue;
        public string error;
        public bool Ok => error == null && graph != null && graph.success && placement != null && placement.success && scenario != null && scenario.success;
    }

    /// <summary>Generates the logical graph and places it with the library's templates. No scene or asset is touched.</summary>
    internal static Generated Generate(Request r)
    {
        var g = new Generated();
        g.graphGenerator = r.profile != null ? r.profile.CreateGenerator() : new ShipGraphGenerator(DefaultRoomCatalogue.Create());
        g.catalogue = g.graphGenerator.Specs.ToList();
        g.graph = g.graphGenerator.Generate(r.seed);
        if (!g.graph.success) { g.error = $"The logical graph for seed {r.seed} failed: {g.graph.Summary}"; return g; }
        if (r.library == null) { g.error = "No Greybox Library: run Alien > Procedural Ship > Build Greybox Library first."; return g; }
        var templates = r.library.BuildTemplates();
        var problems = templates.Problems(g.catalogue);
        if (problems.Count > 0) { g.error = "The greybox library has problems:\n  " + string.Join("\n  ", problems.Take(12)); return g; }
        g.placement = new ShipPlacer(templates, g.catalogue, r.placement).Place(g.graph.graph);
        if (!g.placement.success)
        {
            g.error = $"Placement failed for seed {r.seed}.\n{string.Join("\n", g.placement.attemptFailures.TakeLast(3))}\nFurthest layout:\n{g.placement.failureMap}";
            return g;
        }

        // The escape scenario for this layout: its own seed, rejected and regenerated until the solver accepts it.
        var layout = g.placement.layout;
        var specs = g.catalogue.ToDictionary(x => x.id);
        g.directorInput = new DirectorInput { graph = g.graph.graph, layoutSeed = r.seed, templateOf = i => layout.rooms[i].template, specOf = id => specs[id] };
        g.scenario = EscapeDirector.Generate(g.directorInput, r.scenarioSeed, r.director);
        if (!g.scenario.success) g.error = $"No valid escape scenario for layout seed {r.seed}, scenario seed {r.scenarioSeed}.\n{ScenarioReport.Describe(g.graph.graph, g.scenario)}";
        return g;
    }

    // ---------- Building the scene ----------

    internal static bool BuildScene(Request r)
    {
        var gen = Generate(r);
        if (!gen.Ok) { Debug.LogError(gen.error ?? "Generation did not finish (no error recorded): please report this."); return false; }
        if (!File.Exists(PrototypeSetup.PrefabPath)) { Debug.LogError("Run Alien > Setup Multiplayer Prototype first (the Player prefab is missing)."); return false; }

        bool exists = File.Exists(ScenePath);
        if (exists && !EditorUtility.DisplayDialog("Build Procedural Ship Test Scene",
                $"{ScenePath} already exists.\nReplace it with a new ship for seed {r.seed}? Manual edits to that scene and its baked NavMesh will be lost.\n(Ship.unity and NetTest.unity are not touched.)", "Replace", "Cancel"))
            return false;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;

        if (exists && AssetDatabase.IsValidFolder(NavMeshFolder))
            foreach (var guid in AssetDatabase.FindAssets("t:NavMeshData", new[] { NavMeshFolder }))
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, ScenePath); // a path first, so the NavMesh asset can be stored beside it
        PrototypeSetup.AddToBuildSettings(ScenePath);

        PrototypeSetup.AddLighting();
        AddSharedAtmosphere();
        var layout = gen.placement.layout;
        float cell = layout.settings.cellSize;
        var centre = new Vector3(layout.WidthCells * cell * 0.5f, 0f, layout.DepthCells * cell * 0.5f);
        var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        PrototypeSetup.AddNetwork(playerPrefab, centre + new Vector3(0f, layout.WidthCells * cell * 0.45f, -layout.DepthCells * cell * 0.9f), Quaternion.Euler(60f, 0f, 0f));

        var ship = BuildShip(gen, r);
        ScenarioBuilder.Build(ship, layout.graph, gen.scenario.scenario, gen.scenario);
        CreatureSetup.GetOrCreateSurface();
        EditorSceneManager.SaveScene(scene);

        // Reopen so every in-scene NetworkObject hash is computed from its saved file ID, then save that (as Alien > Build Ship Scene does).
        scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ScenarioBuilder.EnsureNetworkObjectHashes();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"Procedural ship built: {layout.Summary()}; {gen.scenario.Summary}. Baking the NavMesh; wait for 'NavMesh baked and ProceduralShipTest saved' before pressing Play.");
        CreatureSetup.Bake(Object.FindFirstObjectByType<NavMeshSurface>(), scene, () =>
        {
            var report = PhysicalShipValidator.Validate(Object.FindFirstObjectByType<GeneratedShip>(), r.library, true);
            report.LogToConsole();
        });
        return true;
    }

    // The ship's atmosphere (fog, exposure) is shared with the Ship scene when its profile exists; it is only referenced, never changed.
    static void AddSharedAtmosphere()
    {
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AtmosphereProfilePath);
        if (profile == null) { Debug.Log($"{AtmosphereProfilePath} not found (run Alien > Build Ship Scene once to create it): the scene uses the plain sky and fog profile."); return; }
        var go = new GameObject("Ship Atmosphere");
        var vol = go.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.priority = 10f;
        vol.sharedProfile = profile;
    }

    /// <summary>Instantiates the layout under a new "Generated Ship" root and returns it. Call with the new scene active.</summary>
    internal static GeneratedShip BuildShip(Generated gen, Request r)
    {
        var layout = gen.placement.layout;
        var graph = layout.graph;
        float cell = layout.settings.cellSize;

        var root = new GameObject("Generated Ship");
        var ship = root.AddComponent<GeneratedShip>();
        root.AddComponent<ShipDebugView>();
        ship.seed = r.seed;
        ship.graphFingerprint = graph.Fingerprint();
        ship.layoutFingerprint = layout.Fingerprint();
        ship.summary = layout.Summary();
        ship.cellSize = cell;
        ship.placementSettings = layout.settings.Clone();
        ship.route.AddRange(graph.route);
        foreach (var n in graph.nodes)
            ship.nodes.Add(new NodeRecord { index = n.index, id = n.id, definitionId = n.definitionId, displayName = n.displayName, category = n.category, sector = n.sector, deck = n.deck });
        foreach (var c in layout.connections)
            ship.connections.Add(new ConnectionRecord
            {
                edge = c.edge, edgeId = c.edgeId, nodeA = c.nodeA, socketA = c.socketA, nodeB = c.nodeB, socketB = c.socketB, mode = c.mode,
                cells = c.cells.Select(x => new Vector2Int(x.x, x.z)).ToArray(),
            });

        var roomsGroup = Group(root, "Rooms");
        var corridorsGroup = Group(root, "Corridors");
        var sealsGroup = Group(root, "Wall Seals");

        // Rooms
        var instances = new PlacedRoom[layout.rooms.Count];
        foreach (var p in layout.rooms)
        {
            var prefab = r.library.Find(p.template.id);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject, roomsGroup.transform);
            go.name = graph.nodes[p.node].id;
            go.transform.SetPositionAndRotation(LayoutWorld.RoomPivot(p, cell), LayoutWorld.RoomRotation(p));
            var pr = go.AddComponent<PlacedRoom>();
            var node = graph.nodes[p.node];
            pr.nodeIndex = node.index;
            pr.nodeId = node.id;
            pr.definitionId = node.definitionId;
            pr.variantId = p.template.id;
            pr.category = node.category;
            pr.sector = node.sector;
            pr.deck = p.deck;
            pr.rotation = p.rot;
            pr.originCell = new Vector2Int(p.origin.x, p.origin.z);
            pr.room = go.GetComponent<ShipRoomPrefab>();
            instances[p.node] = pr;
        }

        // Connected and sealed sockets
        foreach (var c in layout.connections)
        {
            var a = instances[c.nodeA].room.sockets[c.socketA];
            var b = instances[c.nodeB].room.sockets[c.socketB];
            a.occupied = b.occupied = true;
            a.connectedRoom = instances[c.nodeB].nodeId; a.connectedSocket = b.socketId; a.connectionMode = c.mode;
            b.connectedRoom = instances[c.nodeA].nodeId; b.connectedSocket = a.socketId; b.connectionMode = c.mode;
        }
        var wallSeal = r.library.wallSeal;
        foreach (var s in layout.sealedSockets)
        {
            var socket = instances[s.node].room.sockets[s.socket];
            socket.sealedOff = true;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(wallSeal, sealsGroup.transform);
            go.name = $"Seal {instances[s.node].nodeId} {socket.socketId}";
            go.transform.SetPositionAndRotation(socket.transform.position, socket.transform.rotation);
            var ps = go.AddComponent<PlacedSeal>();
            ps.nodeId = instances[s.node].nodeId;
            ps.socketId = socket.socketId;
        }

        // Corridor modules, one per cell (crossings are a single Cross module)
        int index = 0;
        foreach (var piece in CorridorMath.Pieces(layout))
        {
            var prefab = r.library.Corridor(piece.kind);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, corridorsGroup.transform);
            go.name = $"{piece.kind} {piece.cell.x},{piece.cell.z}";
            go.transform.SetPositionAndRotation(LayoutWorld.CellCentre(piece.cell, 0, cell), Quaternion.Euler(0f, 90f * piece.rot, 0f));
            var pc = go.AddComponent<PlacedCorridor>();
            pc.cell = new Vector2Int(piece.cell.x, piece.cell.z);
            pc.kind = piece.kind;
            pc.rotation = piece.rot;
            pc.owners = piece.owners;
            pc.edgeId = layout.connections[piece.connection].edgeId;
            // One light in three keeps the corridors dim and the light count down.
            if (index++ % 3 != 0) { var light = go.transform.Find("Light"); if (light != null) light.gameObject.SetActive(false); }
        }

        AddSpawnPoints(root, instances, graph);
        AddCreatureAnchors(root, instances, graph);
        return ship;
    }

    static GameObject Group(GameObject root, string name)
    {
        var g = new GameObject(name);
        g.transform.SetParent(root.transform, false);
        return g;
    }

    // Player slots 0-3 take the spawn anchors of the Player Start room, which has four clear, spread-out ones.
    static void AddSpawnPoints(GameObject root, PlacedRoom[] rooms, ShipGraph graph)
    {
        var group = Group(root, "Spawn Points").transform;
        var start = rooms[graph.FirstOf(RoomCategory.PlayerStart)];
        int i = 0;
        foreach (var a in start.room.Anchors(AnchorKind.PlayerSpawn).OrderBy(a => a.anchorId, System.StringComparer.Ordinal).Take(4))
        {
            var go = new GameObject($"Spawn {i}");
            go.transform.SetParent(group, false);
            go.transform.SetPositionAndRotation(a.transform.position, a.transform.rotation);
            go.AddComponent<SpawnPoint>().index = i++;
        }
    }

    // The creature is not in this scene yet. Its anchors are: a reserved spawn in the room furthest (by connections) from the player start, away
    // from the escape bays; every room already carries patrol and search points as RoomAnchors.
    static void AddCreatureAnchors(GameObject root, PlacedRoom[] rooms, ShipGraph graph)
    {
        var group = Group(root, "Creature Anchors").transform;
        int spawn = graph.FirstOf(RoomCategory.PlayerStart);
        var dist = graph.Distances(spawn);
        int best = -1;
        foreach (var n in graph.nodes)
        {
            if (n.category == RoomCategory.EscapePodBay || n.category == RoomCategory.PlayerStart) continue;
            if (best < 0 || dist[n.index] > dist[best]) best = n.index;
        }
        var patrol = rooms[best].room.Anchors(AnchorKind.PatrolPoint).First();
        var go = new GameObject($"Creature Spawn (reserved, {rooms[best].nodeId})");
        go.transform.SetParent(group, false);
        go.transform.SetPositionAndRotation(patrol.transform.position, patrol.transform.rotation);
        var anchor = go.AddComponent<RoomAnchor>();
        anchor.kind = AnchorKind.CreatureSpawn;
        anchor.anchorId = "creature_spawn";
    }
}
