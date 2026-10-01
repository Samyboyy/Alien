using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Interactive lockers in the Ship (menu: Alien > Add Interactive Lockers; also part of fresh Ship builds). Idempotent; nothing is rebuilt.
///  - the placeholder locker prefab is made once (dark metal box, a sliding door with a viewing slit, authored anchors; the visuals are under a
///    "Model" child so final art can replace them) and listed in the network prefab list;
///  - PlayerHiding is added to the Player prefab once;
///  - lockers are placed in the open Ship scene at authored floor cells (backs to a wall, fronts into the room, clear of doors, vents, items,
///    hiding furniture and noisemaker spots) and registered in their room's hiding places, so the creature's existing search considers them
///    like any other place;
///  - everything is validated (anchors, player fit, exit clearance, creature reach, nothing in the way); the scene is saved only when it is clean.
/// Scene lockers are built from scratch (not as prefab instances), so each gets its own network hash.
/// </summary>
public static partial class ShipBuilder
{
    const string LockerRoot = "Lockers";
    internal const string LockerPrefabPath = "Assets/Prefabs/Locker.prefab";

    // Room, the floor cell, and which side the wall is on (N = towards row 0). The front faces the opposite way. Cells were taken from the map:
    // plain floor with exactly one wall beside it, free in front, away from doors, consoles, items, spawns, vents, hiding furniture and pickups.
    static readonly (string room, int c, int r, char wall)[] LockerCells =
    {
        ("Crew", 19, 25, 'S'),
        ("Medbay", 28, 25, 'S'),
        ("Storage", 25, 16, 'S'),
        ("Security", 23, 1, 'N'),
        ("Cargo", 9, 16, 'S'),
        ("Mess", 2, 20, 'N'),
    };

    // ---------- Assets ----------

    /// <summary>The locker prefab, its network prefab entry and the Player prefab component. Returns how many assets changed.</summary>
    internal static int AddLockerAssets()
    {
        int changed = 0;
        Directory.CreateDirectory("Assets/Prefabs");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LockerPrefabPath);
        if (prefab == null)
        {
            var go = BuildLocker("Locker", Vector3.zero, Quaternion.identity, null);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(go, LockerPrefabPath);
                changed++;
                Debug.Log($"Locker prefab created: {LockerPrefabPath}");
            }
            finally { Object.DestroyImmediate(go); }
            AssetDatabase.ImportAsset(LockerPrefabPath);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LockerPrefabPath);
        }

        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsPath);
        if (list != null && prefab != null && EnsureListed(list, prefab))
        {
            EditorUtility.SetDirty(list);
            AssetDatabase.SaveAssets();
            changed++;
            Debug.Log("Locker prefab registered in the default network prefab list.");
        }

        var root = PrefabUtility.LoadPrefabContents(PrototypeSetup.PrefabPath);
        try
        {
            if (root.GetComponent<PlayerHiding>() == null)
            {
                root.AddComponent<PlayerHiding>();
                PrefabUtility.SaveAsPrefabAsset(root, PrototypeSetup.PrefabPath);
                changed++;
                Debug.Log("Player prefab: PlayerHiding added.");
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return changed;
    }

    // ---------- The placeholder locker ----------

    // Local space: origin at the floor in the middle, +Z is the front (the door side). 1 m wide, 1 m deep, 2 m tall.
    internal static GameObject BuildLocker(string name, Vector3 position, Quaternion rotation, Transform parent)
    {
        var metal = PrototypeSetup.Mat("Locker Metal", new Color(0.11f, 0.12f, 0.13f));
        var doorMat = PrototypeSetup.Mat("Locker Door", new Color(0.17f, 0.19f, 0.21f));
        var trim = PrototypeSetup.Mat("Locker Trim", new Color(0.55f, 0.3f, 0.1f));

        var root = new GameObject(name);
        if (parent != null) root.transform.SetParent(parent);
        root.transform.SetPositionAndRotation(position, rotation);

        // Visuals only: the final model replaces this child; the anchors and colliders stay.
        var model = new GameObject("Model").transform;
        model.SetParent(root.transform, false);
        Visual(model, "Left", new Vector3(-0.48f, 1f, 0f), new Vector3(0.04f, 2f, 1f), metal);
        Visual(model, "Right", new Vector3(0.48f, 1f, 0f), new Vector3(0.04f, 2f, 1f), metal);
        Visual(model, "Back", new Vector3(0f, 1f, -0.48f), new Vector3(0.92f, 2f, 0.04f), metal);
        Visual(model, "Top", new Vector3(0f, 1.99f, 0f), new Vector3(1f, 0.04f, 1f), metal);
        Visual(model, "Floor", new Vector3(0f, 0.01f, 0f), new Vector3(1f, 0.02f, 1f), metal);
        Visual(model, "Trim", new Vector3(0f, 1.9f, 0.505f), new Vector3(0.9f, 0.05f, 0.01f), trim);

        // Colliders: the box that stops ordinary entry. The door's own collider is on the door.
        var colliders = new GameObject("Colliders").transform;
        colliders.SetParent(root.transform, false);
        Collider(colliders, "Left", new Vector3(-0.48f, 1f, 0f), new Vector3(0.04f, 2f, 1f));
        Collider(colliders, "Right", new Vector3(0.48f, 1f, 0f), new Vector3(0.04f, 2f, 1f));
        Collider(colliders, "Back", new Vector3(0f, 1f, -0.48f), new Vector3(0.92f, 2f, 0.04f));
        Collider(colliders, "Top", new Vector3(0f, 1.99f, 0f), new Vector3(1f, 0.04f, 1f));

        // The sliding door: a lower and an upper panel with a narrow viewing slit between (visual; the collider is solid so nobody sees or
        // reaches through a closed door).
        var door = new GameObject("Door").transform;
        door.SetParent(root.transform, false);
        door.localPosition = new Vector3(0f, 0f, 0.485f);
        Visual(door, "Lower Panel", new Vector3(0f, 0.77f, 0f), new Vector3(0.96f, 1.5f, 0.03f), doorMat);
        Visual(door, "Upper Panel", new Vector3(0f, 1.81f, 0f), new Vector3(0.96f, 0.34f, 0.03f), doorMat);
        Visual(door, "Handle", new Vector3(0.36f, 1.0f, 0.03f), new Vector3(0.03f, 0.2f, 0.03f), trim);
        var doorCollider = Collider(door, "Door Collider", new Vector3(0f, 1f, 0f), new Vector3(0.96f, 1.96f, 0.04f));

        // Anchors.
        var anchors = new GameObject("Anchors").transform;
        anchors.SetParent(root.transform, false);
        var entry = Anchor(anchors, "Entry", new Vector3(0f, 1f, 1f));
        var body = Anchor(anchors, "Hidden Body", new Vector3(0f, 0.02f, 0f));
        var cam = Anchor(anchors, "Hidden Camera", new Vector3(0f, 1.62f, 0.15f));
        var exit = Anchor(anchors, "Exit", new Vector3(0f, 0f, 1.25f));
        var inspect = Anchor(anchors, "Inspect", new Vector3(0f, 0f, 1.55f));
        var look = Anchor(anchors, "Look At", new Vector3(0f, 1.1f, 0f));
        var future = new[] { Anchor(anchors, "Latch", new Vector3(0.36f, 1f, 0.52f)), Anchor(anchors, "Hinge Rail", new Vector3(-0.48f, 1f, 0.5f)) };

        root.AddComponent<NetworkObject>();
        var locker = root.AddComponent<HideLocker>();
        locker.entryPoint = entry;
        locker.hiddenBody = body;
        locker.hiddenCamera = cam;
        locker.exitPoint = exit;
        locker.doorPanel = door;
        locker.doorColliders = new[] { doorCollider };
        locker.inspectPoint = inspect;
        locker.lookAt = look;
        locker.futureAnchors = future;

        // Enclosed hiding spot for the creature's search: the same component as furniture, with the locker's door attached.
        var spot = root.AddComponent<HidingSpot>();
        spot.locker = locker;
        spot.investigationPoint = inspect;
        spot.inspectPoints = new[] { inspect };
        spot.lookAt = look;
        spot.footprint = new Vector2(1f, 1f);
        spot.volumeConfigured = true;
        spot.volumeCenter = new Vector3(0f, 1f, 0f);
        spot.volumeSize = new Vector3(0.9f, 2f, 0.9f);
        spot.requiredStance = HideStance.Any;
        spot.bodyInset = 0.05f;
        spot.visualConcealment = 0.85f;
        spot.lightVisibility = 0.75f;
        spot.inspectionResidual = 0.25f;

        // The locker is solid to the creature's navigation too (the NavMesh was baked without it): carve its footprint.
        var obstacle = root.AddComponent<NavMeshObstacle>();
        obstacle.shape = NavMeshObstacleShape.Box;
        obstacle.center = new Vector3(0f, 1f, 0f);
        obstacle.size = new Vector3(1f, 2f, 1f);
        obstacle.carving = true;
        root.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
        return root;
    }

    static void Visual(Transform parent, string name, Vector3 localPos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    static BoxCollider Collider(Transform parent, string name, Vector3 localPos, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var c = go.AddComponent<BoxCollider>();
        c.size = size;
        return c;
    }

    static Transform Anchor(Transform parent, string name, Vector3 localPos)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        return t;
    }

    // ---------- Scene ----------

    /// <summary>Assets, then the lockers in the open scene. Returns 1 when the SCENE changed (lockers added or a room list updated), else 0.</summary>
    internal static int AddLockers()
    {
        AddLockerAssets();
        int changed = 0;
        if (GameObject.Find(LockerRoot) != null) Debug.Log($"'{LockerRoot}' already in the scene - left as it is.");
        else
        {
            var root = new GameObject(LockerRoot).transform;
            var rooms = Object.FindObjectsByType<RoomVolume>(FindObjectsSortMode.None);
            for (int i = 0; i < LockerCells.Length; i++)
            {
                var (room, c, r, wall) = LockerCells[i];
                // The back against the wall: from the cell centre half a metre towards it; the front faces the other way.
                Vector3 toWall = wall switch { 'N' => Vector3.forward, 'S' => Vector3.back, 'W' => Vector3.left, _ => Vector3.right };
                var locker = BuildLocker($"Locker {i} {room}", World(c, r) + toWall * 0.48f, Quaternion.LookRotation(-toWall), root);
                var spot = locker.GetComponent<HidingSpot>();
                var volume = rooms.FirstOrDefault(v => v.roomName == room);
                spot.room = volume;
                if (volume != null && !volume.hidingSpots.Contains(spot))
                {
                    volume.hidingSpots = volume.hidingSpots.Append(spot).ToArray();
                    EditorUtility.SetDirty(volume);
                }
            }
            changed++;
            Debug.Log($"Lockers added: {LockerCells.Length}, registered as hiding places in their rooms. No NavMesh rebake is needed (their footprint is carved at run time).");
        }
        Physics.SyncTransforms();
        return changed;
    }

    // ---------- Validation ----------

    /// <summary>Checks every locker: anchors, room registration, player fit, exit clearance, the creature's way to it, nothing in the way.</summary>
    internal static int ValidateLockers()
    {
        int problems = 0;
        var lockers = Object.FindObjectsByType<HideLocker>(FindObjectsSortMode.None);
        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        if (player == null || player.GetComponent<PlayerHiding>() == null) { problems++; Debug.LogError("Player prefab has no PlayerHiding: run Alien > Add Interactive Lockers."); }
        if (lockers.Length == 0) { Debug.LogWarning("No lockers in the scene: run Alien > Add Interactive Lockers."); return problems + 1; }

        Physics.SyncTransforms();
        var avoid = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
            .Where(m => m is SlidingDoor or ShipConsole or ItemPickup or CreatureVentEntrance or SpawnPoint or EscapePod or NoisemakerPickup)
            .Select(m => m.transform.position).ToList();
        avoid.AddRange(Object.FindObjectsByType<CreatureVentEntrance>(FindObjectsSortMode.None).Where(e => e.approach != null).Select(e => e.approach.position));
        avoid.AddRange(Object.FindObjectsByType<NoisemakerPickup>(FindObjectsSortMode.None).Where(p => p.spots != null).SelectMany(p => p.spots).Where(s => s != null).Select(s => s.position));
        var furniture = HidingSpot.All.Where(h => h.locker == null).Select(h => h.transform.position).ToList();

        foreach (var l in lockers)
        {
            string at = CreatureVentNetwork.PathOf(l.transform);
            if (!l.AnchorsValid) { problems++; Debug.LogError($"{at}: an anchor is missing.", l); continue; }
            var spot = l.GetComponent<HidingSpot>();
            if (spot == null || spot.locker != l || spot.room == null || !spot.room.hidingSpots.Contains(spot)) { problems++; Debug.LogError($"{at}: not registered as a hiding place in a room.", l); }

            // The player fits: a capsule at the hidden body anchor touches nothing (the walls are 0.1 m away).
            Vector3 b = l.hiddenBody.position;
            if (Physics.CheckCapsule(b + Vector3.up * 0.37f, b + Vector3.up * 1.43f, 0.34f, ~0, QueryTriggerInteraction.Ignore)) { problems++; Debug.LogError($"{at}: the hidden body anchor does not fit a player.", l); }
            if (l.exitPoint.position.y > 0.3f || !l.ExitClear(null)) { problems++; Debug.LogError($"{at}: the exit is not clear ({(l.exitPoint.position.y > 0.3f ? "above the floor" : "see the Console note")}).", l); }

            // The locker body itself stands in empty floor (nothing else solid inside its box).
            foreach (var c in Physics.OverlapBox(l.transform.position + Vector3.up, new Vector3(0.5f, 1f, 0.5f) - Vector3.one * 0.03f, l.transform.rotation, ~0, QueryTriggerInteraction.Ignore))
                if (!c.transform.IsChildOf(l.transform) && c.GetComponentInParent<HideLocker>() == null && c.name != "Floor" && c.bounds.max.y > 0.1f)
                { problems++; Debug.LogError($"{at}: overlaps '{CreatureVentNetwork.PathOf(c.transform)}'.", l); break; }

            // Not in the way of doors, consoles, items, spawns, vent mouths or hiding furniture.
            foreach (var a in avoid.Concat(furniture))
                if (Vector2.Distance(new Vector2(a.x, a.z), new Vector2(l.transform.position.x, l.transform.position.z)) < 1.3f)
                { problems++; Debug.LogError($"{at}: too close to something that must stay clear ({a.x:0.0}, {a.z:0.0}).", l); break; }

            // The creature can stand at the inspection point: on the NavMesh, and a path from the room's own search points.
            if (!NavMesh.SamplePosition(l.inspectPoint.position, out var hit, 0.8f, NavMesh.AllAreas)) { problems++; Debug.LogError($"{at}: the inspection point is off the NavMesh.", l); }
            else if (spot != null && spot.room != null && spot.room.searchPoints.Length > 0 && spot.room.searchPoints[0] != null)
            {
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(spot.room.searchPoints[0].position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                { problems++; Debug.LogError($"{at}: no complete path from {spot.room.roomName}'s search point to the inspection point.", l); }
            }
        }
        Debug.Log($"Locker validation: {lockers.Length} lockers, {problems} problems.");
        return problems;
    }
}

/// <summary>Menu: Alien > Add Interactive Lockers. Updates the existing Ship scene; nothing is rebuilt. A second run changes nothing.</summary>
public static class LockerSetup
{
    const string ScenePath = "Assets/Scenes/Ship.unity";

    [MenuItem("Alien/Add Interactive Lockers")]
    static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Not in Play Mode."); return; }
        if (!File.Exists(ScenePath)) { Debug.LogError("Ship scene missing. Run Alien > Build Ship Scene first."); return; }
        if (!File.Exists(PrototypeSetup.PrefabPath)) { Debug.LogError("Run Alien > Setup Multiplayer Prototype first (Player prefab missing)."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int changed = ShipBuilder.AddLockers();
        int problems = ShipBuilder.ValidateLockers();
        if (changed == 0)
        {
            Debug.Log(problems == 0 ? "Lockers: everything is already set up; nothing changed." : $"Lockers: nothing to change, but {problems} problem(s) remain (listed above).");
            return;
        }
        if (problems > 0)
        {
            Debug.LogError($"Lockers: {problems} problem(s) found, so the Ship scene was NOT saved. Fix them, then run this again.");
            return;
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("Lockers: Ship saved. No NavMesh rebake is needed.");
    }
}
