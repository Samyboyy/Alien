using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The ship's room connections (RoomLink, with their doors) and the creature-only ventilation network, built from the same ASCII
/// map as the rest of the ship. Used by a fresh Build Ship Scene and by Alien > Add Creature Ventilation, which only adds what is
/// missing and never rebuilds the scene.
///
/// Room links: for each pair in the Links table, the shortest walkable route over the map between the two rooms (not through any
/// other room); the door cells on it become the link's doors, in order from room A to room B.
/// Vents: seven mouths in room walls, joined by overhead duct runs (axis-aligned, above the wall tops) through two junctions. The
/// creature enters a mouth, rises inside the wall to the duct and travels the authored runs. Everything here is render-only
/// greybox with no colliders, so the NavMesh does not change and no rebake is needed.
/// </summary>
public static partial class ShipBuilder
{
    const string LinksRoot = "Room Links", VentRoot = "Creature Ventilation";
    const float VentY = 4.65f; // duct centre height: just above the 4 m wall tops

    // Vent mouths: room, floor cell in front of the mouth, and which way the wall is (column, row direction). Cells were checked against
    // the map: floor, clear of doors, items, consoles, spawns, pods and hiding furniture, with exactly one wall beside them.
    static readonly (string room, int c, int r, int dc, int dr)[] VentSpots =
    {
        ("Engine", 10, 1, 0, -1), ("Bridge", 19, 3, 1, 0), ("PodB", 33, 5, 1, 0), ("Cargo", 13, 14, 1, 0),
        ("Hub", 15, 11, -1, 0), ("Mess", 1, 22, -1, 0), ("Medbay", 33, 22, 1, 0),
    };

    // Junction wall cells (nodes E0..E6 are the mouths above, J0, J1 these).
    static readonly (int c, int r)[] VentJunctionCells = { (14, 9), (20, 17) };

    // Duct runs between nodes ("E#" = mouth, "J#" = junction) with the corner cells in between. Every run is axis-aligned.
    static readonly (string a, string b, (int c, int r)[] via)[] VentEdges =
    {
        ("E0", "J0", new[] { (14, 0) }),
        ("E1", "J0", new[] { (20, 8), (14, 8) }),
        ("E4", "J0", new (int, int)[0]),
        ("E3", "J0", new (int, int)[0]),
        ("J0", "J1", new[] { (14, 17) }),
        ("J1", "E5", new[] { (20, 19), (0, 19) }),
        ("J1", "E6", new[] { (34, 17) }),
        ("E2", "E1", new[] { (34, 0), (20, 0) }),
    };

    /// <summary>Adds the room links and the vent network if missing. Returns how many groups were added.</summary>
    internal static int AddCreatureVentilation()
    {
        int added = 0;
        added += AddRoomLinks();
        if (GameObject.Find(VentRoot) == null) { BuildVentilation(); added++; }
        else Debug.Log($"'{VentRoot}' already in the scene - left as it is.");
        return added;
    }

    // ---------- Room links ----------

    static int AddRoomLinks()
    {
        if (GameObject.Find(LinksRoot) != null) { Debug.Log($"'{LinksRoot}' already in the scene - left as it is."); return 0; }
        var rooms = Object.FindObjectsByType<RoomVolume>(FindObjectsSortMode.None).ToDictionary(r => r.roomName);
        if (rooms.Count == 0) { Debug.LogError("No room volumes in the scene: run Alien > Add Survival And Search first."); return 0; }
        var doors = Object.FindObjectsByType<SlidingDoor>(FindObjectsSortMode.None);

        var root = new GameObject(LinksRoot).transform;
        int made = 0;
        for (int i = 0; i < Links.Length; i++)
        {
            var (a, b) = Links[i];
            var path = RoomCellPath(a, b);
            if (path == null || !rooms.ContainsKey(a) || !rooms.ContainsKey(b)) { Debug.LogError($"Room link {a}-{b}: no walkable route on the map."); continue; }

            var go = new GameObject($"Link {a}-{b}");
            go.transform.SetParent(root);
            var link = go.AddComponent<RoomLink>();
            link.id = i + 1;
            link.roomA = rooms[a];
            link.roomB = rooms[b];

            int lastA = 0;
            for (int k = 0; k < path.Count; k++) if (RoomNameAt(path[k].x, path[k].y) == a) lastA = k;
            link.pointA = Marker(go.transform, $"Side {a}", World(path[lastA].x, path[lastA].y));
            link.pointB = Marker(go.transform, $"Side {b}", World(path[^1].x, path[^1].y));

            var onRoute = new List<SlidingDoor>();
            foreach (var cell in path)
            {
                if ("DPK".IndexOf(At(cell.x, cell.y)) < 0) continue;
                Vector3 p = World(cell.x, cell.y);
                var door = doors.OrderBy(d => Flat2(d.transform.position - p)).FirstOrDefault();
                if (door == null || Flat2(door.transform.position - p) > 2.2f) Debug.LogWarning($"Room link {a}-{b}: no door object near cell ({cell.x},{cell.y}).");
                else if (!onRoute.Contains(door)) onRoute.Add(door);
            }
            link.doors = onRoute.ToArray();
            made++;
        }
        Debug.Log($"Room links: {made} of {Links.Length} created.");
        return made > 0 ? 1 : 0;
    }

    static float Flat2(Vector3 v) => new Vector2(v.x, v.z).magnitude;

    static Transform Marker(Transform parent, string name, Vector3 position)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent);
        t.position = position;
        return t;
    }

    static string RoomNameAt(int c, int r)
    {
        foreach (var (name, c0, r0, c1, r1) in Rooms) if (c >= c0 && c <= c1 && r >= r0 && r <= r1) return name;
        return null;
    }

    // Shortest walk between two rooms on the map, passing through no other room. Doors are open cells; crates, consoles, the
    // generator and the crawlspace are not. Deterministic: fixed neighbour order.
    static List<Vector2Int> RoomCellPath(string a, string b)
    {
        bool Open(int c, int r)
        {
            char ch = At(c, r);
            if ("#xFGabL".IndexOf(ch) >= 0) return false;
            string room = RoomNameAt(c, r);
            return room == null || room == a || room == b;
        }
        var prev = new Dictionary<Vector2Int, Vector2Int>();
        var queue = new Queue<Vector2Int>();
        var (_, c0, r0, c1, r1) = Rooms.First(x => x.name == a);
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
                if (Open(c, r)) { var s = new Vector2Int(c, r); prev[s] = s; queue.Enqueue(s); }
        var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (RoomNameAt(cur.x, cur.y) == b)
            {
                var path = new List<Vector2Int> { cur };
                while (prev[path[^1]] != path[^1]) path.Add(prev[path[^1]]);
                path.Reverse();
                return path;
            }
            foreach (var d in dirs)
            {
                var n = cur + d;
                if (prev.ContainsKey(n) || !Open(n.x, n.y)) continue;
                prev[n] = cur;
                queue.Enqueue(n);
            }
        }
        return null;
    }

    // ---------- Vents ----------

    static void BuildVentilation()
    {
        var mat = PrototypeSetup.Mat("Vent", new Color(0.1f, 0.1f, 0.12f));
        var root = new GameObject(VentRoot).transform;
        var net = root.gameObject.AddComponent<CreatureVentNetwork>();
        var rooms = Object.FindObjectsByType<RoomVolume>(FindObjectsSortMode.None);

        var entrances = new CreatureVentEntrance[VentSpots.Length];
        var nodes = new Dictionary<string, Transform>();
        for (int i = 0; i < VentSpots.Length; i++)
        {
            var (room, c, r, dc, dr) = VentSpots[i];
            Vector3 floor = World(c, r), dir = new Vector3(dc, 0f, -dr); // row 0 is north (+Z)
            var go = new GameObject($"Vent Entrance {i} {room}");
            go.transform.SetParent(root);
            go.transform.position = floor;
            var e = go.AddComponent<CreatureVentEntrance>();
            e.id = i;
            e.room = rooms.FirstOrDefault(x => x.roomName == room);
            e.approach = Marker(go.transform, "Approach", floor + Vector3.up * 0.05f);
            e.face = Marker(go.transform, "Face", floor + dir * 1f + Vector3.up * 0.55f);
            e.inside = Marker(go.transform, "Inside", floor + dir * 2f + Vector3.up * 0.55f);
            e.top = Marker(go.transform, "Top", floor + dir * 2f + Vector3.up * VentY);
            // The grille on the wall face, and the riser that shows above the wall top. Both are render-only.
            Box(go.transform, "Mouth", e.face.position, dc != 0 ? new Vector3(0.2f, 0.9f, 1.2f) : new Vector3(1.2f, 0.9f, 0.2f), mat);
            Box(go.transform, "Riser", new Vector3(e.top.position.x, 4.52f, e.top.position.z), new Vector3(0.8f, 1.05f, 0.8f), mat);
            entrances[i] = e;
            nodes[$"E{i}"] = e.top;
        }

        var junctions = new Transform[VentJunctionCells.Length];
        for (int j = 0; j < junctions.Length; j++)
        {
            var (c, r) = VentJunctionCells[j];
            junctions[j] = Marker(root, $"Junction {j}", World(c, r, VentY));
            Box(junctions[j], "Box", junctions[j].position, new Vector3(1.1f, 1.1f, 1.1f), mat);
            nodes[$"J{j}"] = junctions[j];
        }

        var edges = new List<CreatureVentNetwork.Edge>();
        foreach (var (a, b, via) in VentEdges)
        {
            var group = new GameObject($"Duct {a}-{b}").transform;
            group.SetParent(root);
            var points = new List<Vector3> { nodes[a].position };
            var viaT = new List<Transform>();
            for (int k = 0; k < via.Length; k++)
            {
                var t = Marker(group, $"Corner {k}", World(via[k].c, via[k].r, VentY));
                viaT.Add(t);
                points.Add(t.position);
            }
            points.Add(nodes[b].position);
            for (int k = 1; k < points.Count; k++) DuctBox(group, $"Run {k}", points[k - 1], points[k], mat);
            edges.Add(new CreatureVentNetwork.Edge { from = nodes[a], to = nodes[b], via = viaT.ToArray() });
        }

        net.entrances = entrances;
        net.junctions = junctions;
        net.edges = edges.ToArray();
        Debug.Log($"Creature ventilation built: {entrances.Length} mouths, {junctions.Length} junctions, {edges.Count} duct runs.");
    }

    static void Box(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, true);
        go.transform.position = center;
        go.transform.localScale = size;
        Object.DestroyImmediate(go.GetComponent<Collider>()); // render-only: the NavMesh and physics never see it
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // An axis-aligned duct segment, slightly longer than the run so corners meet.
    static void DuctBox(Transform parent, string name, Vector3 a, Vector3 b, Material mat)
    {
        Vector3 d = b - a;
        bool alongX = Mathf.Abs(d.x) >= Mathf.Abs(d.z);
        Box(parent, name, (a + b) * 0.5f, alongX ? new Vector3(Mathf.Abs(d.x) + 0.8f, 0.8f, 0.8f) : new Vector3(0.8f, 0.8f, Mathf.Abs(d.z) + 0.8f), mat);
    }

    // ---------- Validation (needs the baked NavMesh) ----------

    internal static void ValidateWorld()
    {
        ValidateSurvival();
        ValidateVentilation();
    }

    /// <summary>Checks the room links and the vent network. Logs a summary; no per-frame output.</summary>
    internal static void ValidateVentilation()
    {
        int problems = 0;
        // Doors are closed (carved) in the saved scene, so a complete route from the start is not expected for every room: this checks
        // that the point is on the NavMesh at all. Whether the creature can reach it depends on doors at run time.
        bool OnMesh(Vector3 p, float radius) => NavMesh.SamplePosition(p, out _, radius, NavMesh.AllAreas);

        // Room links: every room is reachable through allowed links, and every link's points are on the NavMesh.
        var links = Object.FindObjectsByType<RoomLink>(FindObjectsSortMode.None).OrderBy(l => l.id).ToList();
        var roomList = Object.FindObjectsByType<RoomVolume>(FindObjectsSortMode.None).OrderBy(r => r.roomName).ToList();
        int withDoors = 0;
        foreach (var l in links)
        {
            if (l.pointA == null || l.pointB == null || l.roomA == null || l.roomB == null) { problems++; Debug.LogError($"{l.name}: incomplete.", l); continue; }
            if (!NavMesh.SamplePosition(l.pointA.position, out _, 0.6f, NavMesh.AllAreas) || !NavMesh.SamplePosition(l.pointB.position, out _, 0.6f, NavMesh.AllAreas))
            { problems++; Debug.LogError($"{l.name}: a side point is off the NavMesh.", l); }
            if (l.doors.Length > 0) withDoors++;
        }
        var linkStructs = links.Select(l => new RoomRoutes.Link { id = l.id, a = roomList.IndexOf(l.roomA), b = roomList.IndexOf(l.roomB), allowed = true }).ToList();
        for (int i = 1; i < roomList.Count; i++)
            if (RoomRoutes.Path(linkStructs, roomList.Count, 0, i, null) == null)
            { problems++; Debug.LogError($"Room {roomList[i].roomName} is not reachable through the room links."); }

        // Vents.
        var net = Object.FindFirstObjectByType<CreatureVentNetwork>();
        if (net == null) { Debug.Log($"Room links: {links.Count} ({withDoors} with doors), {problems} problems. No ventilation network in the scene yet."); return; }
        net.Build();
        int junctionCount = 0;
        for (int n = net.EntranceCount; n < net.Graph.NodeCount; n++) if (net.Graph.Degree(n) >= 3) junctionCount++;
        if (junctionCount < 1) { problems++; Debug.LogError("Vent network has no junction."); }
        for (int i = 0; i < net.EntranceCount; i++)
        {
            var e = net.entrances[i];
            if (e == null || e.approach == null) { problems++; continue; }
            if (!OnMesh(e.approach.position, 0.5f)) { problems++; Debug.LogError($"{e.name}: its approach point is off the NavMesh.", e); }
            Vector3 p = e.approach.position;
            if (Physics.CheckCapsule(p + Vector3.up * 0.55f, p + Vector3.up * 1.45f, 0.4f, ~0, QueryTriggerInteraction.Ignore))
            { problems++; Debug.LogError($"{e.name}: something solid is in the way of the exit.", e); }
            for (int j = i + 1; j < net.EntranceCount; j++)
                if (net.Graph.Distance(i, j) < 0f) { problems++; Debug.LogError($"Vent entrances {i} and {j} are not connected."); }
        }
        float total = 0f;
        for (int e = 0; e < net.Graph.EdgeCount; e++) total += net.Graph.Edge(e).length;
        Debug.Log($"Ventilation validation: {links.Count} room links ({withDoors} with doors), {net.EntranceCount} vent mouths, {junctionCount} junction(s), " +
                  $"{net.Graph.EdgeCount} duct runs ({total:0} m in all), {problems} problems.");
    }
}

/// <summary>Menu: Alien > Add Creature Ventilation. Updates the existing Ship scene; nothing is rebuilt and no rebake is needed.</summary>
public static class VentilationSetup
{
    const string ScenePath = "Assets/Scenes/Ship.unity";

    [MenuItem("Alien/Add Creature Ventilation")]
    static void Run()
    {
        if (!File.Exists(ScenePath)) { Debug.LogError("Ship scene missing. Run Alien > Build Ship Scene first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int added = ShipBuilder.AddCreatureVentilation();
        ShipBuilder.ValidateVentilation();
        if (added == 0) return; // nothing changed: leave the scene untouched
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"Creature ventilation: {added} group(s) added and Ship saved. No NavMesh rebake is needed (render-only geometry).");
    }
}
