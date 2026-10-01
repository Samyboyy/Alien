using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

public sealed class PhysicalReport
{
    public readonly List<string> errors = new(), warnings = new(), info = new();
    public readonly List<Vector3> markers = new();
    public bool Valid => errors.Count == 0;

    public void LogToConsole()
    {
        foreach (var i in info) Debug.Log($"Procedural ship: {i}");
        foreach (var w in warnings) Debug.LogWarning($"Procedural ship: {w}");
        foreach (var e in errors) Debug.LogError($"Procedural ship: {e}");
        Debug.Log(Valid ? $"Procedural ship validation passed ({warnings.Count} warning(s))." : $"Procedural ship validation FAILED: {errors.Count} error(s), {warnings.Count} warning(s).");
    }
}

/// <summary>
/// Checks a generated ship in the open scene. First the pure layout rules, run on the layout rebuilt from the scene itself (so hand edits and prefab
/// changes are checked, not what was generated): overlaps, socket compatibility and alignment, open holes, walkable connectivity, correspondence of
/// every node with a room, Bridge / Engineering / escape bay placement, anchors and bounds. Then the Unity-side checks: room bounds against the
/// footprint, socket positions in the world, sealed doorways, spawn clearance, camera and listener counts, and (after a bake) NavMesh coverage and
/// reachability of every room from the spawn.
/// </summary>
public static class PhysicalShipValidator
{
    [MenuItem("Alien/Procedural Ship/Validate Procedural Ship")]
    static void Run()
    {
        var ship = SceneScope.First<GeneratedShip>();
        if (ship == null) { Debug.LogError("No generated ship in the open scene. Open Assets/Scenes/ProceduralShipTest.unity (Alien > Procedural Ship > Build Procedural Ship Test Scene)."); return; }
        var lib = AssetDatabase.LoadAssetAtPath<GreyboxLibrary>(GreyboxLibraryBuilder.LibraryPath);
        Validate(ship, lib, true).LogToConsole();
    }

    /// <param name="record">Write the findings into the GeneratedShip (for the debug view). False keeps the validation strictly read-only.</param>
    public static PhysicalReport Validate(GeneratedShip ship, GreyboxLibrary library, bool checkNavMesh, bool record = true)
    {
        var rep = new PhysicalReport();
        if (ship == null) { rep.errors.Add("There is no GeneratedShip in the scene."); return rep; }
        float cell = ship.cellSize;
        var profile = AssetDatabase.LoadAssetAtPath<ShipGenerationProfile>(RoomDefinitionSetup.ProfilePath);
        var catalogue = profile != null ? profile.BuildCatalogue() : DefaultRoomCatalogue.Create();

        // ---- Library ----
        if (library != null) foreach (var p in library.Problems(catalogue)) rep.errors.Add($"greybox library: {p}");
        else rep.warnings.Add("no Greybox Library asset found; prefab-level checks use only what is in the scene");

        // ---- Rooms in the scene against the logical nodes ----
        var rooms = ship.GetComponentsInChildren<PlacedRoom>(true);
        var graph = ship.ToGraph();
        if (rooms.Length != ship.nodes.Count) rep.errors.Add($"{rooms.Length} placed rooms for {ship.nodes.Count} logical rooms");
        var byIndex = new Dictionary<int, PlacedRoom>();
        foreach (var r in rooms)
        {
            if (byIndex.ContainsKey(r.nodeIndex)) rep.errors.Add($"two rooms claim logical node {r.nodeId}");
            byIndex[r.nodeIndex] = r;
            if (r.room == null) rep.errors.Add($"{r.name}: no ShipRoomPrefab component (missing metadata)");
            else if (r.room.bounds == null) rep.errors.Add($"{r.nodeId}: no RoomBounds");
        }
        foreach (var n in ship.nodes)
            if (!byIndex.ContainsKey(n.index)) { rep.errors.Add($"logical room {n.id} has no physical room"); }
        if (!rep.Valid && (rooms.Any(r => r.room == null) || byIndex.Count != ship.nodes.Count)) return rep; // the rebuild below needs every room

        // ---- The layout rebuilt from the scene, through the pure validator ----
        var layout = new ShipLayout { seed = ship.seed, graph = graph, settings = ship.placementSettings };
        for (int i = 0; i < ship.nodes.Count; i++)
        {
            var pr = byIndex[i];
            layout.rooms.Add(new RoomPlacement { node = i, template = pr.room.ToTemplate(), rot = pr.rotation, origin = new Int2(pr.originCell.x, pr.originCell.y), deck = pr.deck });
        }
        foreach (var c in ship.connections)
        {
            var pc = new PhysicalConnection { edge = c.edge, edgeId = c.edgeId, nodeA = c.nodeA, socketA = c.socketA, nodeB = c.nodeB, socketB = c.socketB, mode = c.mode };
            pc.cells.AddRange(c.cells.Select(v => new Int2(v.x, v.y)));
            layout.connections.Add(pc);
        }
        for (int i = 0; i < ship.nodes.Count; i++)
            for (int s = 0; s < byIndex[i].room.sockets.Length; s++)
                if (byIndex[i].room.sockets[s].sealedOff) layout.sealedSockets.Add(new SealedSocket(i, s));
        // The logical rules on the graph the scene records: required rooms, Crew/Mess distance, escape-access predecessors, terminal pods,
        // single-deck filtering, Bridge and Engineering separation, connectivity and loops.
        var graphSettings = profile != null && profile.settings != null ? profile.settings : new ShipGraphSettings();
        foreach (var issue in new ShipGraphValidator(catalogue, graphSettings).Validate(graph).issues)
            (issue.severity == IssueSeverity.Error ? rep.errors : rep.warnings).Add($"graph {issue.code}: {issue.message}");

        var pure = new ShipLayoutValidator(layout.settings).Validate(layout, catalogue);
        foreach (var issue in pure.issues)
        {
            (issue.severity == IssueSeverity.Error ? rep.errors : rep.warnings).Add($"{issue.code}: {issue.message}");
            foreach (var c in issue.cells) rep.markers.Add(LayoutWorld.CellCentre(c, 0, cell));
            foreach (int n in issue.nodes) if (byIndex.TryGetValue(n, out var pr) && pr.room != null) rep.markers.Add(pr.room.bounds.WorldBounds().center);
        }
        // ---- Room bounds against the footprint, overlaps in the world ----
        Physics.SyncTransforms();
        var boxes = new List<(string name, Bounds b)>();
        foreach (var l in layout.rooms)
        {
            var pr = byIndex[l.node];
            var expected = LayoutWorld.RoomBox(l, cell);
            var actual = pr.room.bounds.WorldBounds();
            if (Vector3.Distance(new Vector3(expected.min.x, 0, expected.min.z), new Vector3(actual.min.x, 0, actual.min.z)) > 0.05f ||
                Vector3.Distance(new Vector3(expected.max.x, 0, expected.max.z), new Vector3(actual.max.x, 0, actual.max.z)) > 0.05f)
            {
                rep.errors.Add($"{pr.nodeId}: its RoomBounds {actual.min:F1}..{actual.max:F1} do not match the placed footprint {expected.min:F1}..{expected.max:F1}");
                rep.markers.Add(actual.center);
            }
            boxes.Add((pr.nodeId, Shrink(actual)));
        }
        foreach (var pc in ship.GetComponentsInChildren<PlacedCorridor>(true))
            boxes.Add(($"corridor {pc.cell}", Shrink(LayoutWorld.CellBox(new Int2(pc.cell.x, pc.cell.y), pc.deck, cell))));
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
                if (boxes[i].b.Intersects(boxes[j].b))
                {
                    rep.errors.Add($"geometry overlap in the scene: {boxes[i].name} and {boxes[j].name}");
                    rep.markers.Add(boxes[i].b.center);
                }

        // ---- Sockets in world space ----
        foreach (var c in ship.connections)
        {
            var a = byIndex[c.nodeA].room.sockets[c.socketA];
            var b = byIndex[c.nodeB].room.sockets[c.socketB];
            if (!a.occupied || !b.occupied) rep.errors.Add($"{c.edgeId}: a connected socket is not marked occupied");
            if (c.mode == ConnectionMode.Direct)
            {
                if (Vector3.Distance(a.WorldPosition, b.WorldPosition) > 0.05f || Vector3.Dot(a.WorldForward, b.WorldForward) > -0.99f)
                { rep.errors.Add($"{c.edgeId}: the two sockets do not meet in the world (distance {Vector3.Distance(a.WorldPosition, b.WorldPosition):F2} m)"); rep.markers.Add(a.WorldPosition); }
            }
            else if (c.cells.Length > 0)
            {
                var first = LayoutWorld.ToVector(c.cells[0], byIndex[c.nodeA].deck, cell);
                var last = LayoutWorld.ToVector(c.cells[^1], byIndex[c.nodeB].deck, cell);
                if (Vector3.Distance(first, a.WorldPosition + a.WorldForward * cell * 0.5f) > 0.05f)
                { rep.errors.Add($"{c.edgeId}: the corridor does not start at the doorway of {byIndex[c.nodeA].nodeId}"); rep.markers.Add(a.WorldPosition); }
                if (Vector3.Distance(last, b.WorldPosition + b.WorldForward * cell * 0.5f) > 0.05f)
                { rep.errors.Add($"{c.edgeId}: the corridor does not end at the doorway of {byIndex[c.nodeB].nodeId}"); rep.markers.Add(b.WorldPosition); }
            }
        }
        var seals = ship.GetComponentsInChildren<PlacedSeal>(true);
        foreach (var r in rooms)
            foreach (var s in r.room.sockets)
            {
                if (!s.occupied && !s.sealedOff) { rep.errors.Add($"{r.nodeId} socket {s.socketId} is open: neither connected nor sealed"); rep.markers.Add(s.WorldPosition); }
                if (s.sealedOff && !seals.Any(x => x.nodeId == r.nodeId && x.socketId == s.socketId && Vector3.Distance(x.transform.position, s.WorldPosition) < 0.05f))
                { rep.errors.Add($"{r.nodeId} socket {s.socketId} is marked sealed but has no wall module in its doorway"); rep.markers.Add(s.WorldPosition); }
            }

        // ---- Corridor modules against the layout ----
        var instances = ship.GetComponentsInChildren<PlacedCorridor>(true).ToDictionary(c => new Int2(c.cell.x, c.cell.y));
        var pieces = CorridorMath.Pieces(layout);
        if (instances.Count != pieces.Count) rep.errors.Add($"{instances.Count} corridor modules in the scene for {pieces.Count} corridor cells");
        foreach (var piece in pieces)
            if (!instances.TryGetValue(piece.cell, out var pc) || pc.kind != piece.kind || pc.rotation != piece.rot)
            { rep.errors.Add($"the corridor module at {piece.cell} is missing or the wrong kind or turn"); rep.markers.Add(LayoutWorld.CellCentre(piece.cell, 0, cell)); }

        // ---- Anchors inside their rooms ----
        foreach (var r in rooms)
        {
            var b = r.room.bounds.WorldBounds();
            b.Expand(0.1f);
            foreach (var a in r.room.anchors)
                if (a != null && !b.Contains(a.transform.position)) { rep.errors.Add($"{r.nodeId}: the {a.kind} anchor '{a.anchorId}' lies outside the room's bounds"); rep.markers.Add(a.transform.position); }
        }

        // ---- Spawn points ----
        bool physics = SceneScope.PhysicsSafe;
        var spawns = SceneScope.All<SpawnPoint>().OrderBy(s => s.index).ToArray();
        if (spawns.Length < 1 || spawns.Length > 4) rep.errors.Add($"{spawns.Length} spawn points (expected 1 to 4)");
        else if (spawns.Select(s => s.index).Distinct().Count() != spawns.Length) rep.errors.Add("two spawn points share a slot index");
        foreach (var sp in spawns)
        {
            var p = sp.transform.position;
            // The capsule of a standing player, lifted above the floor so only furniture and walls count.
            if (physics && Physics.CheckCapsule(p + Vector3.up * 0.5f, p + Vector3.up * 1.4f, 0.4f)) { rep.errors.Add($"spawn point {sp.index} is blocked by geometry"); rep.markers.Add(p); }
            if (physics && !Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, 1.0f)) { rep.errors.Add($"spawn point {sp.index} has no floor under it"); rep.markers.Add(p); }
        }

        // ---- Network, cameras and listeners ----
        CheckPlayerSetup(rep);

        // ---- NavMesh ----
        if (checkNavMesh && physics) CheckNavMesh(rep, ship, layout, rooms, spawns, cell);

        ScenarioSceneValidator.Check(rep, ship);

        rep.info.Add($"{ship.summary}");
        rep.info.Add($"{rep.errors.Count} error(s), {rep.warnings.Count} warning(s); seed {ship.seed}, graph {ship.graphFingerprint}, layout {ship.layoutFingerprint}");
        if (record)
        {
            ship.issues = rep.errors.Select(e => "ERROR " + e).Concat(rep.warnings.Select(w => "warning " + w)).ToList();
            ship.issueMarkers = rep.markers.Take(200).ToList();
            EditorUtility.SetDirty(ship);
        }
        return rep;
    }

    static Bounds Shrink(Bounds b)
    {
        b.Expand(-0.1f);
        return b;
    }

    static void CheckPlayerSetup(PhysicalReport rep)
    {
        var nm = SceneScope.All<NetworkManager>();
        if (nm.Length != 1) rep.errors.Add($"{nm.Length} NetworkManagers in the scene (expected 1)");
        else if (nm[0].NetworkConfig.PlayerPrefab == null) rep.errors.Add("the NetworkManager has no player prefab");
        // Scene-view and preview cameras are hidden editor objects, not part of the scene.
        var cams = SceneScope.All<Camera>().Where(c => c.gameObject.scene.IsValid() && c.gameObject.hideFlags == HideFlags.None).ToArray();
        var listeners = SceneScope.All<AudioListener>().Where(l => l.gameObject.scene.IsValid() && l.gameObject.hideFlags == HideFlags.None).ToArray();
        if (cams.Length != 1) rep.errors.Add($"{cams.Length} cameras in the scene before any player exists (expected the one lobby camera)");
        if (listeners.Length != 1) rep.errors.Add($"{listeners.Length} AudioListeners in the scene before any player exists (expected 1)");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        if (prefab == null) { rep.errors.Add("the Player prefab is missing"); return; }
        var pcams = prefab.GetComponentsInChildren<Camera>(true);
        var plisteners = prefab.GetComponentsInChildren<AudioListener>(true);
        if (pcams.Length != 1 || plisteners.Length != 1) rep.errors.Add($"the Player prefab has {pcams.Length} cameras and {plisteners.Length} AudioListeners (expected 1 and 1, enabled only for the owner)");
        else if (pcams[0].enabled || plisteners[0].enabled) rep.warnings.Add("the Player prefab's camera or AudioListener starts enabled; the controller is expected to enable them for the owner only");
    }

    static void CheckNavMesh(PhysicalReport rep, GeneratedShip ship, ShipLayout layout, PlacedRoom[] rooms, SpawnPoint[] spawns, float cell)
    {
        var tri = NavMesh.CalculateTriangulation();
        if (tri.vertices.Length == 0) { rep.errors.Add("there is no baked NavMesh (run Alien > Rebake Navigation)"); return; }

        double area = 0;
        for (int i = 0; i + 2 < tri.indices.Length; i += 3)
        {
            var a = tri.vertices[tri.indices[i]];
            var b = tri.vertices[tri.indices[i + 1]];
            var c = tri.vertices[tri.indices[i + 2]];
            area += System.Math.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5;
        }
        double floor = layout.rooms.Sum(r => r.SizeX * r.SizeZ) * (double)cell * cell + layout.CorridorCellCount * (double)cell * cell;
        double coverage = area / floor;
        rep.info.Add($"NavMesh covers {area:F0} m2 of {floor:F0} m2 of floor ({coverage:P0}; furniture and wall margins take the rest)");
        // The agent radius (0.5 m) erodes every wall and piece of furniture, and a 1.4 m clear corridor keeps only a 0.4 m strip, so 35-50% is normal.
        if (coverage < 0.2) rep.errors.Add($"the NavMesh covers only {coverage:P0} of the floor");
        else if (coverage < 0.3) rep.warnings.Add($"the NavMesh covers only {coverage:P0} of the floor");

        if (spawns.Length == 0) return;
        var from = spawns[0].transform.position;
        if (!NavMesh.SamplePosition(from, out var start, 1.5f, NavMesh.AllAreas)) { rep.errors.Add("the first spawn point is not on the NavMesh"); return; }
        var path = new NavMeshPath();

        // Closed doors carve the NavMesh (the doors are NavMeshObstacles), so a room whose only ways in are gated doors is not walkable until a door
        // opens. That is the scenario working as designed: such a room is reported as information when the logical graph without the gated
        // connections really cuts it off, and as an error only when something else is wrong.
        var doors = SceneScope.All<SlidingDoor>();
        var gatedEdges = new HashSet<int>();
        foreach (var c in ship.connections)
            foreach (var (node, socket) in new[] { (c.nodeA, c.socketA), (c.nodeB, c.socketB) })
            {
                var sp = rooms.First(x => x.nodeIndex == node).room.sockets[socket].WorldPosition;
                if (doors.Any(d => new Vector2(d.transform.position.x - sp.x, d.transform.position.z - sp.z).magnitude < 0.4f)) gatedEdges.Add(c.edge);
            }
        var graph = ship.ToGraph();
        int startNode = rooms.OrderBy(x => Vector3.Distance(x.room.bounds.WorldBounds().ClosestPoint(from), from)).First().nodeIndex;
        var open = new HashSet<int> { startNode };
        var stack = new Stack<int>();
        stack.Push(startNode);
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            for (int ei = 0; ei < graph.EdgeCount; ei++)
            {
                var e = graph.edges[ei];
                if (gatedEdges.Contains(ei) || (e.a != u && e.b != u)) continue;
                if (open.Add(e.Other(u))) stack.Push(e.Other(u));
            }
        }
        foreach (var r in rooms)
        {
            foreach (var kind in new[] { AnchorKind.PatrolPoint, AnchorKind.SearchPoint })
                foreach (var a in r.room.Anchors(kind))
                    if (!NavMesh.SamplePosition(a.transform.position, out _, 1.5f, NavMesh.AllAreas)) { rep.errors.Add($"{r.nodeId}: the {kind} anchor '{a.anchorId}' is not on the NavMesh"); rep.markers.Add(a.transform.position); }
            var target = r.room.Anchors(AnchorKind.PatrolPoint).FirstOrDefault();
            if (target == null || !NavMesh.SamplePosition(target.transform.position, out var end, 1.5f, NavMesh.AllAreas)) continue;
            if (!NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
            {
                if (!open.Contains(r.nodeIndex)) rep.info.Add($"{r.nodeId} is behind a gated door: the NavMesh reaches it only once a door opens (expected)");
                else { rep.errors.Add($"{r.nodeId} cannot be walked to from the first spawn point (disconnected walkable section)"); rep.markers.Add(end.position); }
            }
        }
        // Escape bays and the Bridge/Engineering ends were also reached above (they are rooms); report their walking distances as information.
        foreach (var cat in new[] { RoomCategory.Bridge, RoomCategory.Engineering, RoomCategory.EscapePodBay })
            foreach (int n in layout.graph.AllOf(cat))
            {
                var target = rooms.First(r => r.nodeIndex == n).room.Anchors(AnchorKind.PatrolPoint).FirstOrDefault();
                if (target != null && NavMesh.SamplePosition(target.transform.position, out var end, 1.5f, NavMesh.AllAreas) && NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                    rep.info.Add($"walking distance from the first spawn to {layout.graph.nodes[n].id}: {PathLength(path):F0} m");
            }
    }

    static float PathLength(NavMeshPath p)
    {
        float len = 0f;
        for (int i = 1; i < p.corners.Length; i++) len += Vector3.Distance(p.corners[i - 1], p.corners[i]);
        return len;
    }
}
