using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The survival/search part of the Ship: hiding furniture, room volumes with search points, and the validation of both
/// against the real player dimensions and the baked NavMesh. Used by a fresh Build Ship Scene and by
/// Alien > Add Survival And Search (which only adds what is missing). Same ASCII map and cell maths as the rest of ShipBuilder.
/// </summary>
public static partial class ShipBuilder
{
    const string FurnitureRoot = "Hiding Furniture", RoomsRoot = "Rooms";
    // Clear height under the furniture. The crouched capsule is 1.0 m; standing needs 1.8 m, so a player crouched under it
    // cannot stand up (the controller's own ceiling check blocks it) and cannot be seen through the top.
    const float HideUnderside = 1.15f;

    // Room rectangles in map cells (inclusive). Corridors deliberately have no volume.
    static readonly (string name, int c0, int r0, int c1, int r1)[] Rooms =
    {
        ("PodA", 1, 1, 5, 6), ("Engine", 7, 1, 13, 6), ("Bridge", 15, 1, 19, 6), ("Security", 21, 1, 27, 6), ("PodB", 29, 1, 33, 6),
        ("Cargo", 1, 10, 13, 16), ("Hub", 15, 10, 19, 16), ("Storage", 21, 10, 33, 16),
        ("Mess", 1, 20, 10, 25), ("Crew", 12, 20, 22, 25), ("Medbay", 24, 20, 33, 25),
    };

    // Which rooms the creature may move on to from each room (doors or short corridor hops). The crawlspace is not listed:
    // the creature cannot use it.
    static readonly (string a, string b)[] Links =
    {
        ("PodA", "Engine"), ("Engine", "Bridge"), ("Engine", "Cargo"), ("Bridge", "Security"), ("Bridge", "Hub"),
        ("Security", "PodB"), ("Security", "Storage"), ("Cargo", "Hub"), ("Cargo", "Mess"), ("Hub", "Storage"),
        ("Hub", "Crew"), ("Storage", "Medbay"), ("Mess", "Crew"), ("Crew", "Medbay"),
    };

    // One hiding place per room (Bridge is the creature's start and has none). alongX = long side runs east-west.
    // The cells were checked against the map: floor, clear of doors/items/consoles/spawns, at least two open sides.
    static readonly (string room, string kind, int c, int r, bool alongX)[] Hiding =
    {
        ("Engine", "table", 13, 4, false), ("Security", "bunk", 22, 5, true), ("Cargo", "bed", 2, 14, true),
        ("Hub", "table", 19, 11, false), ("Storage", "bed", 30, 14, true), ("Mess", "table", 5, 23, true),
        ("Crew", "bed", 17, 24, true), ("Medbay", "table", 28, 23, true),
    };

    /// <summary>Adds rooms and furniture that are missing from the open scene. Returns how many groups were added.</summary>
    internal static int AddHidingAndRooms()
    {
        int added = 0;
        if (GameObject.Find(RoomsRoot) == null) { BuildRooms(); added++; }
        else Debug.Log($"'{RoomsRoot}' already in the scene - left as it is.");
        if (GameObject.Find(FurnitureRoot) == null) { BuildFurniture(); added++; }
        else Debug.Log($"'{FurnitureRoot}' already in the scene - left as it is.");
        LinkHidingToRooms();
        ConfigureHidingVolumes();
        return added;
    }

    // ---------- Concealment volumes ----------

    // Starting values per kind (prototype approximations to tune in the Inspector): cover, and the authored lighting.
    static (float cover, float light) VolumeTuning(string name) =>
        name.Contains(" bunk ") ? (0.8f, 0.8f) : name.Contains(" bed ") ? (0.75f, 0.85f) : (0.65f, 1f);

    /// <summary>
    /// Gives every hiding place that has no configured concealment volume one: the space under the furniture, crouched players only.
    /// Spots already configured are left exactly as they are (hand edits survive); untick "Volume Configured" on one to regenerate it.
    /// Does not touch geometry, so no rebake is needed. Returns how many were configured.
    /// </summary>
    internal static int ConfigureHidingVolumes()
    {
        int done = 0;
        foreach (var spot in Object.FindObjectsByType<HidingSpot>(FindObjectsSortMode.None).OrderBy(x => x.name))
        {
            if (spot.volumeConfigured) continue;
            var (cover, light) = VolumeTuning(spot.name);
            spot.volumeSize = new Vector3(spot.footprint.x, HideUnderside, spot.footprint.y);
            spot.volumeCenter = new Vector3(0f, HideUnderside * 0.5f, 0f);
            spot.requiredStance = HideStance.Crouched;
            spot.bodyInset = 0.2f;
            spot.visualConcealment = cover;
            spot.lightVisibility = light;
            spot.inspectionResidual = 0.25f;
            spot.volumeConfigured = true;
            EditorUtility.SetDirty(spot);
            done++;
            Debug.Log($"{spot.name}: concealment volume configured (cover {cover:0.00}, light {light:0.00}, crouched only).", spot);
        }
        if (done == 0) Debug.Log("Hiding volumes: nothing to configure, every spot already has one (left as it is).");
        return done;
    }

    // ---------- Rooms ----------

    static void BuildRooms()
    {
        var root = new GameObject(RoomsRoot).transform;
        var byName = new Dictionary<string, RoomVolume>();
        foreach (var (name, c0, r0, c1, r1) in Rooms)
        {
            var go = new GameObject($"Room {name}");
            go.transform.SetParent(root);
            go.transform.position = (World(c0, r0) + World(c1, r1)) * 0.5f;
            var room = go.AddComponent<RoomVolume>();
            room.roomName = name;
            room.size = new Vector3((c1 - c0 + 1) * Cell, WallHeight, (r1 - r0 + 1) * Cell);

            var points = new List<Transform>();
            foreach (var cell in SearchCells(c0, r0, c1, r1))
            {
                var p = new GameObject($"Search {name} {points.Count}").transform;
                p.SetParent(go.transform);
                p.position = World(cell.x, cell.y);
                points.Add(p);
            }
            room.searchPoints = points.ToArray();
            byName[name] = room;
        }
        foreach (var (a, b) in Links)
        {
            byName[a].neighbours = byName[a].neighbours.Append(byName[b]).ToArray();
            byName[b].neighbours = byName[b].neighbours.Append(byName[a]).ToArray();
        }
    }

    // Floor cells well away from doors, objectives, spawns and the hiding furniture, spread at least 4 cells apart.
    static List<Vector2Int> SearchCells(int c0, int r0, int c1, int r1)
    {
        var hidingCells = new HashSet<Vector2Int>(Hiding.Select(h => new Vector2Int(h.c, h.r)));
        var chosen = new List<Vector2Int>();
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                if (At(c, r) != '.' || hidingCells.Contains(new Vector2Int(c, r))) continue;
                bool blockedNearby = false;
                for (int dc = -1; dc <= 1; dc++)
                    for (int dr = -1; dr <= 1; dr++)
                        if ("DPKFGabABSCkf".IndexOf(At(c + dc, r + dr)) >= 0 || hidingCells.Contains(new Vector2Int(c + dc, r + dr)))
                            blockedNearby = true;
                if (blockedNearby) continue;
                if (chosen.All(o => Mathf.Abs(o.x - c) + Mathf.Abs(o.y - r) >= 4)) chosen.Add(new Vector2Int(c, r));
                if (chosen.Count == 6) return chosen;
            }
        return chosen;
    }

    // ---------- Furniture ----------

    static void BuildFurniture()
    {
        var root = new GameObject(FurnitureRoot).transform;
        var tableMat = PrototypeSetup.Mat("Table", new Color(0.45f, 0.35f, 0.25f));
        var bedMat = PrototypeSetup.Mat("Bed", new Color(0.55f, 0.6f, 0.7f));
        foreach (var h in Hiding) BuildOneHidingPlace(root, h, h.kind == "table" ? tableMat : bedMat);
    }

    // A top slab on four thin legs (plus a headboard for a bunk). Ordinary colliders: the player crouches under it with the normal
    // controller, it blocks sight like any solid, and the NavMesh bake leaves the space under it unwalkable for the tall creature.
    // Legs are inset so 1.0 m of clear width remains between them: the crouched capsule (0.7 m plus skin) fits.
    static void BuildOneHidingPlace(Transform root, (string room, string kind, int c, int r, bool alongX) h, Material mat)
    {
        bool bed = h.kind != "table";
        float len = bed ? 2.0f : 1.8f, depth = 1.3f, thick = bed ? 0.25f : 0.1f;
        Vector3 center = World(h.c, h.r);
        bool alongX = h.alongX;

        Vector3 Offset(float u, float v, float y) => center + (alongX ? new Vector3(u, y, v) : new Vector3(v, y, u)); // u = along length, v = along depth
        Vector3 Size(float l, float y, float d) => alongX ? new Vector3(l, y, d) : new Vector3(d, y, l);

        var go = new GameObject($"Hide {h.kind} {h.room}");
        go.transform.SetParent(root);
        go.transform.position = center;

        PrototypeSetup.Block(go.transform, "Top", Offset(0f, 0f, HideUnderside + thick * 0.5f), Size(len, thick, depth), mat);
        foreach (float su in new[] { -1f, 1f })
            foreach (float sv in new[] { -1f, 1f })
                PrototypeSetup.Block(go.transform, "Leg", Offset(su * (len * 0.5f - 0.1f), sv * (depth * 0.5f - 0.1f), HideUnderside * 0.5f),
                    Size(0.1f, HideUnderside, 0.1f), mat);
        if (h.kind == "bunk")
            PrototypeSetup.Block(go.transform, "Headboard", Offset(-(len * 0.5f - 0.05f), 0f, HideUnderside * 0.5f), Size(0.1f, HideUnderside, depth), mat);

        var spot = go.AddComponent<HidingSpot>();
        spot.footprint = alongX ? new Vector2(len, depth) : new Vector2(depth, len);
        var look = new GameObject("Look At").transform;
        look.SetParent(go.transform);
        look.position = center + Vector3.up * 0.05f;
        spot.lookAt = look;

        // Candidate openings on all four sides; validation keeps the ones that are reachable and really open.
        var points = new List<Transform>();
        foreach (var (name, u, v) in new[] { ("Inspect +V", 0f, depth * 0.5f + 0.9f), ("Inspect -V", 0f, -(depth * 0.5f + 0.9f)),
                     ("Inspect +U", len * 0.5f + 0.9f, 0f), ("Inspect -U", -(len * 0.5f + 0.9f), 0f) })
        {
            var p = new GameObject(name).transform;
            p.SetParent(go.transform);
            p.position = Offset(u, v, 0.05f);
            points.Add(p);
        }
        spot.inspectPoints = points.ToArray();
        spot.investigationPoint = points[0];
    }

    /// <summary>
    /// Gives every furniture hiding place that has lost its inspection points the four candidate openings again (the same ones
    /// BuildOneHidingPlace makes, derived from its footprint), so the usual validation can keep the usable ones. Spots that still have
    /// points are untouched. Returns how many spots were repaired.
    /// </summary>
    internal static int RepairInspectionPoints()
    {
        int repaired = 0;
        foreach (var spot in Object.FindObjectsByType<HidingSpot>(FindObjectsSortMode.None).Where(h => h.locker == null).OrderBy(h => h.name))
        {
            if (spot.inspectPoints != null && spot.inspectPoints.Length > 0 && spot.inspectPoints.All(t => t != null)) continue;
            bool alongX = spot.footprint.x >= spot.footprint.y;
            float len = Mathf.Max(spot.footprint.x, spot.footprint.y), depth = Mathf.Min(spot.footprint.x, spot.footprint.y);
            Vector3 center = spot.transform.position;
            Vector3 Offset(float u, float v) => center + (alongX ? new Vector3(u, 0.05f, v) : new Vector3(v, 0.05f, u));
            var points = new List<Transform>();
            foreach (var (name, u, v) in new[] { ("Inspect +V", 0f, depth * 0.5f + 0.9f), ("Inspect -V", 0f, -(depth * 0.5f + 0.9f)),
                         ("Inspect +U", len * 0.5f + 0.9f, 0f), ("Inspect -U", -(len * 0.5f + 0.9f), 0f) })
            {
                var p = new GameObject(name).transform;
                p.SetParent(spot.transform);
                p.position = Offset(u, v);
                points.Add(p);
            }
            spot.inspectPoints = points.ToArray();
            spot.investigationPoint = points[0];
            EditorUtility.SetDirty(spot);
            repaired++;
            Debug.Log($"{spot.name}: inspection openings restored (it had none).", spot);
        }
        return repaired;
    }

    // Each hiding place belongs to the room that contains it, and each room lists its hiding places.
    static void LinkHidingToRooms()
    {
        var rooms = Object.FindObjectsByType<RoomVolume>(FindObjectsSortMode.None);
        foreach (var spot in Object.FindObjectsByType<HidingSpot>(FindObjectsSortMode.None))
        {
            if (spot.room == null) spot.room = rooms.FirstOrDefault(r => r.Contains(spot.transform.position));
            if (spot.room == null || spot.room.hidingSpots.Contains(spot)) continue;
            spot.room.hidingSpots = spot.room.hidingSpots.Append(spot).ToArray();
            EditorUtility.SetDirty(spot.room);
        }
    }

    // ---------- Validation (needs the baked NavMesh and the Player prefab) ----------

    /// <summary>
    /// Checks every furniture hiding place against the real player dimensions and the NavMesh, removes inspection points that cannot be used
    /// (only when <paramref name="trim"/> is true: Alien > Validate Project For Play passes false and changes nothing), and confirms existing
    /// routes still work. Lockers are skipped: they are enclosed, not low furniture, and ValidateLockers checks them (this check used to judge
    /// them as tables and would have removed their inspection anchor). Logs a summary; no per-frame output.
    /// </summary>
    internal static void ValidateSurvival(bool trim = true)
    {
        Physics.SyncTransforms();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        var ctrl = prefab.GetComponent<NetworkFirstPersonController>();
        float skin = prefab.GetComponent<CharacterController>().skinWidth;
        float r = ctrl.radius + skin, crouchH = ctrl.crouchHeight, standH = ctrl.standHeight;

        var creature = Object.FindFirstObjectByType<CreatureAI>();
        Vector3 start = creature != null ? creature.transform.position : Vector3.zero;
        var scratch = new NavMeshPath();
        int ok = 0, problems = 0;

        foreach (var spot in Object.FindObjectsByType<HidingSpot>(FindObjectsSortMode.None).Where(s => s.locker == null).OrderBy(s => s.name))
        {
            Vector3 c = spot.LookPoint;
            c.y = 0f;
            var issues = new List<string>();

            if (HideUnderside < crouchH + 0.1f) issues.Add($"clearance {HideUnderside:0.00} m is under crouch height {crouchH:0.00} m plus margin");
            if (HideUnderside >= standH) issues.Add("a player could stand up under it");
            if (Physics.CheckCapsule(Bottom(c, r), Top(c, crouchH, r), r, ~0, QueryTriggerInteraction.Ignore)) issues.Add("a crouched player does not fit under it");
            if (!Physics.CheckCapsule(Bottom(c, r), Top(c, standH, r), r, ~0, QueryTriggerInteraction.Ignore)) issues.Add("a standing player would fit (no ceiling)");

            // Keep openings that the creature can reach on the NavMesh AND a crouched player can actually slide in through.
            var keep = new List<Transform>();
            foreach (var t in spot.inspectPoints)
            {
                if (t == null) continue;
                Vector3 to = c - new Vector3(t.position.x, 0f, t.position.z);
                // Reachable = on the NavMesh and connected to the spot's OWN room. (It used to be a complete path from the creature's start, which
                // fails for every room behind a closed, carved door: the validator then DELETED those rooms' inspection points.)
                Vector3 origin = spot.room != null && spot.room.searchPoints.Length > 0 && spot.room.searchPoints[0] != null ? spot.room.searchPoints[0].position : start;
                bool onMesh = NavMesh.SamplePosition(t.position, out var hit, 0.4f, NavMesh.AllAreas)
                    && NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, scratch) && scratch.status == NavMeshPathStatus.PathComplete;
                Vector3 from = new Vector3(t.position.x, 0f, t.position.z);
                bool open = !Physics.CapsuleCast(Bottom(from, r), Top(from, crouchH, r), r, to.normalized, to.magnitude, ~0, QueryTriggerInteraction.Ignore);
                if (onMesh && open) keep.Add(t);
                else if (trim) Object.DestroyImmediate(t.gameObject);
            }
            if (trim)
            {
                spot.inspectPoints = keep.ToArray();
                spot.investigationPoint = keep.FirstOrDefault();
            }
            if (keep.Count == 0) issues.Add("no reachable, open inspection point");
            if (!spot.Qualifies(c, crouchH, true)) issues.Add("a crouched player at the centre does not qualify for the concealment volume");
            if (spot.Qualifies(c, standH, false)) issues.Add("a standing player would qualify for the concealment volume");
            if (trim) EditorUtility.SetDirty(spot);

            if (issues.Count == 0) { ok++; Debug.Log($"{spot.name}: fits a crouched player, blocks standing, {keep.Count} usable opening(s).", spot); }
            else { problems++; Debug.LogError($"{spot.name}: {string.Join("; ", issues)}", spot); }
        }

        // Existing routes: the creature must still reach every patrol point, and every item spot must still touch the NavMesh.
        int routeProblems = 0;
        var patrolRoot = GameObject.Find("Patrol Points");
        if (patrolRoot != null)
            foreach (Transform p in patrolRoot.transform)
            {
                bool reach = NavMesh.SamplePosition(p.position, out var hit, 1f, NavMesh.AllAreas)
                    && NavMesh.CalculatePath(start, hit.position, NavMesh.AllAreas, scratch) && scratch.status == NavMeshPathStatus.PathComplete;
                if (!reach) { routeProblems++; Debug.LogError($"Patrol point {p.name} is no longer reachable by the creature.", p); }
            }
        foreach (var item in Object.FindObjectsByType<ItemPickup>(FindObjectsSortMode.None))
            foreach (var spotT in item.spots)
                if (spotT != null && !NavMesh.SamplePosition(spotT.position, out _, 1.5f, NavMesh.AllAreas))
                { routeProblems++; Debug.LogError($"{item.name} spot {spotT.name} is no longer on the NavMesh.", spotT); }

        Debug.Log($"Survival validation: {ok} hiding places OK, {problems} with problems, {routeProblems} route problems.");
    }

    static Vector3 Bottom(Vector3 p, float r) => p + Vector3.up * (r + 0.03f);
    static Vector3 Top(Vector3 p, float height, float r) => p + Vector3.up * (height - r);
}
