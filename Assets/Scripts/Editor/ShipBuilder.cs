using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Alien > Build Ship Scene. Generates Assets/Scenes/Ship.unity (greybox) from the ASCII map below:
/// geometry, doors + switches, crawlspace, items, consoles, pods, spawns, creature + patrol, network, round manager,
/// NavMesh. Rerunning asks before replacing the scene (manual edits to it would be lost). NetTest is not touched.
/// Menu: Alien > Add Escape System. Adds the player components this milestone needs to the Player prefab (rerun-safe).
/// </summary>
public static partial class ShipBuilder
{
    const string ScenePath = "Assets/Scenes/Ship.unity";
    const string NavMeshFolder = "Assets/Scenes/Ship"; // where the NavMesh bake stores its asset
    const float Cell = 2f, WallHeight = 4f, DoorHeight = 2.5f, CrawlClearance = 1.4f;

    // One char = one 2x2 m cell; row 0 is north (+Z).
    //   #  wall                 .  floor               L  crawlspace (1.4 m ceiling: crouch only, too low for the creature)
    //   D  door + two switches  P  door, needs power   K  door, needs the keycard
    //   x  crate (cover)        S  player spawn        C  creature start          0-9  creature patrol, in order
    //   k  keycard spot         f  fuse spot           F  fuse socket             G  generator (hold, loud)
    //   A  Pod A interior       a  Pod A launch console (needs power)
    //   B  Pod B interior       b  Pod B manual launch (hold, quiet, slow)
    // Validate edits with the same rules: doors need walls on exactly one axis and open cells on the other.
    static readonly string[] Map =
    {
        "###################################",
        "#aA#..#.......#k....#.......#..#Bb#",
        "#AA#..#.F...G.#..C..#.......#..#BB#",
        "#.....#.......#.....#...x...#.....#",
        "#.....P.......#..0..#.......K.....#",
        "#.....#..x.x..#.....#.......#.....#",
        "#.....#.......#.....#.......#.....#",
        "##L#######D######.######D##########",
        "##L###..1......................6..#",
        "##L######D######...########D#######",
        "#.............#.....#............f#",
        "#...xx....x.f.#.....#..xx.....x...#",
        "#.....2.......#..#..#.............#",
        "#......xx...........D.....xx......#",
        "#...x.........#.....#...........x.#",
        "#..........x..#.4...#..x.....x....#",
        "#k...x........#.....#..........k..#",
        "#######.########...########D#######",
        "#..3...........................5..#",
        "#####D###########D##########D######",
        "#.........k#...........#..........#",
        "#..........#.S.......S.#..x....x..#",
        "#..xx..xx..D...x...x...D..........#",
        "#..........#...........#..........#",
        "#..xx..xx..#.S.......S.#..x....x..#",
        "#f.........#...........#.........k#",
        "###################################",
    };

    static int W => Map[0].Length;
    static int H => Map.Length;
    static char At(int c, int r) => c < 0 || r < 0 || c >= W || r >= H ? '#' : Map[r][c];
    static Vector3 World(int c, int r, float y = 0f) => new((c - (W - 1) * 0.5f) * Cell, y, ((H - 1) * 0.5f - r) * Cell);

    [MenuItem("Alien/Build Ship Scene")]
    static void Build()
    {
        if (!File.Exists(PrototypeSetup.PrefabPath)) { Debug.LogError("Run Alien > Setup Multiplayer Prototype first (Player prefab missing)."); return; }
        bool rebuild = File.Exists(ScenePath);
        if (rebuild && !EditorUtility.DisplayDialog("Build Ship Scene",
                $"{ScenePath} already exists.\nRebuild it from the map? Manual edits to that scene and its baked NavMesh will be replaced.", "Rebuild", "Cancel"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        AddEscapeSystem();
        // The old bake would otherwise linger as "NavMesh-NavMesh Surface 1.asset" etc.
        if (rebuild && AssetDatabase.IsValidFolder(NavMeshFolder))
            foreach (var guid in AssetDatabase.FindAssets("t:NavMeshData", new[] { NavMeshFolder }))
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(guid));

        // Save the empty scene first so it has a path, and register it: NGO syncs in-scene objects by build scene.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, ScenePath);
        PrototypeSetup.AddToBuildSettings(ScenePath);

        PrototypeSetup.AddLighting();
        var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        PrototypeSetup.AddNetwork(playerPrefab, new Vector3(0, 60, -40), Quaternion.Euler(56f, 0f, 0f));
        RoundSetup.AddRoundManager();
        BuildFromMap();
        AddHidingAndRooms(); // survival additions: furniture, room volumes, search points (ShipSurvival.cs)
        AddCreatureVentilation(); // room connections with their doors, and the creature vent network (ShipVentilation.cs)
        AddShipAtmosphere(); // ceiling, lights, interior Volume and acoustic zones (ShipAtmosphereBuilder.cs)
        CreatureSetup.GetOrCreateSurface();
        EditorSceneManager.SaveScene(scene);

        // Reopen so every in-scene NetworkObject hash is computed from its saved file ID, then save that.
        scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        CheckNetworkObjectHashes();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        CreatureSetup.Bake(Object.FindFirstObjectByType<NavMeshSurface>(), scene, ValidateWorld);
        Debug.Log("Ship scene built. Wait for 'NavMesh baked and Ship saved' before pressing Play.");
    }

    [MenuItem("Alien/Add Escape System")]
    internal static void AddEscapeSystem()
    {
        var root = PrefabUtility.LoadPrefabContents(PrototypeSetup.PrefabPath);
        try
        {
            bool changed = Ensure<PlayerInteractor>(root) | Ensure<PlayerLife>(root) | Ensure<LocalSpectator>(root)
                | Ensure<PlayerInventory>(root) | Ensure<FootstepNoise>(root) | Ensure<ThreatVisuals>(root) | Ensure<PlayerMotionTracker>(root);
            if (!changed) return;
            PrefabUtility.SaveAsPrefabAsset(root, PrototypeSetup.PrefabPath);
            Debug.Log("Player prefab: added escape-system components.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static bool Ensure<T>(GameObject go) where T : Component
    {
        if (go.GetComponent<T>() != null) return false;
        go.AddComponent<T>();
        return true;
    }

    // NetworkObject.OnValidate (internal) derives the hash from the saved GlobalObjectId; run it explicitly
    // rather than relying on load order, then verify every in-scene object has a unique non-zero hash.
    static void CheckNetworkObjectHashes()
    {
        var validate = typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var seen = new HashSet<uint>();
        int bad = 0;
        foreach (var no in Object.FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
        {
            validate?.Invoke(no, null);
            if (no.PrefabIdHash == 0 || !seen.Add(no.PrefabIdHash)) bad++;
        }
        if (bad > 0) Debug.LogError($"Ship: {bad} NetworkObject(s) have a missing or duplicate GlobalObjectIdHash. Reopen Ship.unity, save it, and run the build again.");
        else Debug.Log($"Ship: {seen.Count} in-scene NetworkObjects have unique hashes.");
    }

    static void BuildFromMap()
    {
        var wallMat = PrototypeSetup.Mat("Wall", new Color(0.6f, 0.6f, 0.62f));
        var floorMat = PrototypeSetup.Mat("Floor", new Color(0.35f, 0.35f, 0.38f));
        var crateMat = PrototypeSetup.Mat("Obstacle", new Color(0.7f, 0.45f, 0.2f));
        var ceilingMat = PrototypeSetup.Mat("Ceiling", new Color(0.3f, 0.3f, 0.32f));
        var tableMat = PrototypeSetup.Mat("Table", new Color(0.45f, 0.35f, 0.25f));
        var consoleMat = PrototypeSetup.Mat("Console", new Color(0.2f, 0.7f, 0.7f));

        var geo = new GameObject("Ship Geometry").transform;
        var doors = new GameObject("Doors").transform;
        var consoles = new GameObject("Consoles").transform;
        var spotGroup = new GameObject("Item Spots").transform;
        var spawns = new GameObject("Spawn Points").transform;
        var patrolGroup = new GameObject("Patrol Points").transform;

        PrototypeSetup.Block(geo, "Floor", new Vector3(0, -0.1f, 0), new Vector3(W * Cell, 0.2f, H * Cell), floorMat);

        // Walls: one block per horizontal run of '#'.
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
            {
                if (At(c, r) != '#') continue;
                int end = c;
                while (end + 1 < W && At(end + 1, r) == '#') end++;
                Vector3 mid = (World(c, r) + World(end, r)) * 0.5f;
                PrototypeSetup.Block(geo, $"Wall r{r} c{c}-{end}", mid + Vector3.up * WallHeight * 0.5f,
                    new Vector3((end - c + 1) * Cell, WallHeight, Cell), wallMat);
                c = end;
            }

        var podA = BuildPod('A', 'a', "Pod A");
        podA.requiredFlags = ShipFlags.PowerRestored;
        podA.countdownSeconds = 10f;
        podA.countdownNoise = 20f;
        podA.lockedHint = "no power - fit a fuse + restart the engine generator (loud)";
        podA.readyHint = "power restored - launch from the console inside (loud)";
        var podB = BuildPod('B', 'b', "Pod B");
        podB.countdownSeconds = 20f;
        podB.countdownNoise = 8f;
        podB.readyHint = "behind the Security keycard door - slow, quiet manual launch";

        var keycardSpots = new List<Transform>();
        var fuseSpots = new List<Transform>();
        var patrol = new SortedDictionary<char, Transform>();
        Vector3 creaturePos = Vector3.zero;
        int spawnIndex = 0;

        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
            {
                char ch = At(c, r);
                Vector3 p = World(c, r);
                switch (ch)
                {
                    case 'D': case 'P': case 'K':
                        BuildDoor(doors, geo, c, r, ch, wallMat);
                        break;
                    case 'L':
                        PrototypeSetup.Block(geo, "Crawl Ceiling", p + Vector3.up * (CrawlClearance + WallHeight) * 0.5f,
                            new Vector3(Cell, WallHeight - CrawlClearance, Cell), ceilingMat);
                        break;
                    case 'x':
                        PrototypeSetup.Block(geo, "Crate", p + Vector3.up * 0.8f, Vector3.one * 1.6f, crateMat);
                        break;
                    case 'k':
                    case 'f':
                        PrototypeSetup.Block(geo, "Table", p + Vector3.up * 0.4f, new Vector3(1f, 0.8f, 1f), tableMat);
                        var spot = new GameObject(ch == 'k' ? "Keycard Spot" : "Fuse Spot").transform;
                        spot.SetParent(spotGroup);
                        spot.position = p + Vector3.up * 0.8f; // table top
                        (ch == 'k' ? keycardSpots : fuseSpots).Add(spot);
                        break;
                    case 'S':
                        var sp = new GameObject($"Spawn {spawnIndex}");
                        sp.transform.SetParent(spawns);
                        sp.transform.position = p + Vector3.up * 0.05f; // facing north, towards the quarters door
                        sp.AddComponent<SpawnPoint>().index = spawnIndex++;
                        break;
                    case 'C':
                        creaturePos = p;
                        break;
                    case 'F':
                        var socket = Console(consoles, "Fuse Socket", p, consoleMat);
                        socket.label = "Fit fuse";
                        socket.lockedText = "needs a fuse";
                        socket.doneText = "fuse fitted";
                        socket.requiredItem = ItemKind.Fuse;
                        socket.consumeItem = true;
                        socket.setFlags = ShipFlags.FuseInstalled;
                        break;
                    case 'G':
                        var generator = Console(consoles, "Generator", p, consoleMat);
                        generator.label = "Restart generator";
                        generator.lockedText = "no fuse fitted";
                        generator.doneText = "running";
                        generator.requiredFlags = ShipFlags.FuseInstalled;
                        generator.holdSeconds = 6f;
                        generator.holdNoise = 16f;
                        generator.useNoise = 20f;
                        generator.setFlags = ShipFlags.PowerRestored;
                        break;
                    case 'a':
                        var launchA = Console(consoles, "Pod A Console", p, consoleMat);
                        launchA.label = "Launch Pod A";
                        launchA.useNoise = 8f;
                        launchA.pod = podA;
                        break;
                    case 'b':
                        var launchB = Console(consoles, "Pod B Manual Release", p, consoleMat);
                        launchB.label = "Manual launch Pod B";
                        launchB.holdSeconds = 8f;
                        launchB.holdNoise = 3f;
                        launchB.useNoise = 3f;
                        launchB.pod = podB;
                        break;
                    default:
                        if (!char.IsDigit(ch)) break;
                        var point = new GameObject($"Patrol {ch}").transform;
                        point.SetParent(patrolGroup);
                        point.position = p;
                        patrol[ch] = point;
                        break;
                }
            }

        var items = new GameObject("Items").transform;
        Item(items, ItemKind.Keycard, keycardSpots, PrototypeSetup.Mat("Keycard", new Color(0.1f, 0.9f, 1f)), new Vector3(0.4f, 0.06f, 0.28f));
        Item(items, ItemKind.Fuse, fuseSpots, PrototypeSetup.Mat("Fuse", new Color(1f, 0.5f, 0.1f)), new Vector3(0.22f, 0.3f, 0.22f));
        CreatureSetup.AddCreature(patrol.Values.ToArray(), creaturePos);
    }

    // Door slab + lintel + one switch on the jamb at each side. The slab slides along its width into the wall.
    static void BuildDoor(Transform doors, Transform geo, int c, int r, char kind, Material wallMat)
    {
        bool spanX = At(c - 1, r) == '#' && At(c + 1, r) == '#';
        Vector3 span = spanX ? Vector3.right : Vector3.forward; // width direction, also the slide direction
        Vector3 across = spanX ? Vector3.forward : Vector3.right; // walking direction
        Vector3 p = World(c, r);

        PrototypeSetup.Block(geo, "Lintel", p + Vector3.up * (DoorHeight + WallHeight) * 0.5f,
            new Vector3(Cell, WallHeight - DoorHeight, Cell), wallMat);

        var doorMat = kind == 'P' ? PrototypeSetup.Mat("PowerDoor", new Color(0.7f, 0.2f, 0.2f))
            : kind == 'K' ? PrototypeSetup.Mat("KeycardDoor", new Color(0.2f, 0.6f, 0.7f))
            : PrototypeSetup.Mat("Door", new Color(0.25f, 0.4f, 0.7f));
        var switchMat = PrototypeSetup.Mat("Switch", new Color(0.9f, 0.8f, 0.1f));

        var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
        door.name = $"Door {c},{r}";
        door.transform.SetParent(doors);
        door.transform.position = p + Vector3.up * DoorHeight * 0.5f;
        door.transform.localScale = span * Cell + across * 0.3f + Vector3.up * DoorHeight;
        door.GetComponent<Renderer>().sharedMaterial = doorMat;
        door.AddComponent<NetworkObject>();
        door.AddComponent<NetworkTransform>(); // server authority (default)
        var sliding = door.AddComponent<SlidingDoor>();
        sliding.openOffset = span * Cell;
        CreatureSetup.AddDoorNavigation(door);

        foreach (float side in new[] { -1f, 1f })
        {
            var sw = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sw.name = $"Switch {c},{r} {(side < 0 ? "A" : "B")}";
            sw.transform.SetParent(doors);
            sw.transform.position = p - span * (Cell * 0.5f - 0.06f) + across * (0.6f * side) + Vector3.up * 1.3f;
            sw.transform.localScale = span * 0.12f + across * 0.25f + Vector3.up * 0.25f;
            sw.GetComponent<Renderer>().sharedMaterial = switchMat;
            sw.AddComponent<NetworkObject>();
            if (kind == 'D')
            {
                sw.AddComponent<DoorSwitch>().door = sliding;
                continue;
            }
            var console = sw.AddComponent<ShipConsole>();
            console.door = sliding;
            console.useNoise = 0f; // the door makes the noise
            if (kind == 'P') { console.requiredFlags = ShipFlags.PowerRestored; console.lockedText = "no power"; }
            else { console.requiredItem = ItemKind.Keycard; console.lockedText = "needs keycard"; }
        }
    }

    static ShipConsole Console(Transform parent, string name, Vector3 cell, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent);
        go.transform.position = cell + Vector3.up * 0.6f;
        go.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.AddComponent<NetworkObject>();
        return go.AddComponent<ShipConsole>();
    }

    // Pod object at the centre of its cells; "Interior" is the boarding box, "Status Plate" shows the state.
    static EscapePod BuildPod(char interiorCell, char consoleCell, string name)
    {
        int minC = int.MaxValue, maxC = int.MinValue, minR = int.MaxValue, maxR = int.MinValue;
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
                if (At(c, r) == interiorCell || At(c, r) == consoleCell)
                {
                    minC = Mathf.Min(minC, c); maxC = Mathf.Max(maxC, c);
                    minR = Mathf.Min(minR, r); maxR = Mathf.Max(maxR, r);
                }
        var size = new Vector3((maxC - minC + 1) * Cell, 3f, (maxR - minR + 1) * Cell);

        var go = new GameObject(name);
        go.transform.position = (World(minC, minR) + World(maxC, maxR)) * 0.5f;
        go.AddComponent<NetworkObject>();
        var pod = go.AddComponent<EscapePod>();
        pod.podName = name;

        pod.interior = new GameObject("Interior").transform;
        pod.interior.SetParent(go.transform, false);
        pod.interior.localPosition = Vector3.up * size.y * 0.5f;
        pod.interior.localScale = size;

        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = "Status Plate";
        Object.DestroyImmediate(plate.GetComponent<Collider>());
        plate.transform.SetParent(go.transform, false);
        plate.transform.localPosition = Vector3.up * 0.03f;
        plate.transform.localScale = new Vector3(size.x - 0.4f, 0.05f, size.z - 0.4f);
        plate.GetComponent<Renderer>().sharedMaterial = PrototypeSetup.Mat("PodFloor", new Color(0.2f, 0.8f, 0.3f));
        pod.statusPlate = plate.GetComponent<Renderer>();
        return pod;
    }

    // One scene object per item; it starts on the first spot and the host re-rolls the spot every round.
    static void Item(Transform parent, ItemKind kind, List<Transform> spots, Material mat, Vector3 size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = kind.ToString();
        go.transform.SetParent(parent);
        go.transform.localScale = size;
        go.transform.position = spots[0].position + Vector3.up * size.y * 0.5f;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.AddComponent<NetworkObject>();
        go.AddComponent<NetworkTransform>(); // server authority (default)
        go.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // moves, so never baked
        var item = go.AddComponent<ItemPickup>();
        item.kind = kind;
        item.spots = spots.ToArray();
    }
}
