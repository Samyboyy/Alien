using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns an escape scenario into the project's existing gameplay objects in the open generated-ship scene: sliding doors with their keycard,
/// power or manual-override controls, door-control and fuse/generator consoles, escape pods with launch consoles, keycard and fuse pickups with
/// their candidate spots, noisemaker pickups, the camera console placeholder and the RoundManager. Nothing new is invented: doors are SlidingDoor,
/// controls are ShipConsole, pods are EscapePod, items are ItemPickup, so networking, noise, the creature's door handling and round reset are the
/// ones the Ship scene already uses. Every object stands on an anchor of the room it is in.
/// </summary>
public static class ScenarioBuilder
{
    const float Cell = GreyboxScale.Cell;

    internal static void Build(GeneratedShip ship, ShipGraph graph, Scenario sc, ScenarioResult result)
    {
        var rooms = ship.GetComponentsInChildren<PlacedRoom>(true).OrderBy(r => r.nodeIndex).ToArray();
        var root = new GameObject("Escape Scenario");
        root.transform.SetParent(ship.transform, false);
        var doors = Group(root, "Doors").transform;
        var consoles = Group(root, "Consoles").transform;
        var items = Group(root, "Items").transform;
        var spots = Group(root, "Item Spots").transform;
        var pods = Group(root, "Pods").transform;
        var noisemakers = Group(root, "Noisemakers").transform;
        var doorOf = new Dictionary<int, SlidingDoor>();
        var podOf = new Dictionary<int, EscapePod>();

        // Doors and the controls at their jambs.
        foreach (var gate in sc.gates)
        {
            var conn = ship.connections[gate.edge];
            int socketIndex = conn.nodeA == gate.doorNode ? conn.socketA : conn.socketB;
            var socket = rooms[gate.doorNode].room.sockets[socketIndex];
            doorOf[gate.edge] = BuildDoor(doors, graph, gate, socket);
        }

        // Escape pods and their launch consoles.
        foreach (var pod in sc.pods)
        {
            var berth = FindAnchor(rooms[pod.node], AnchorKind.Objective, AnchorSemantic.PodBerth);
            if (!pod.Usable) { BuildWreck(pods, graph.nodes[pod.node].id, berth); continue; }
            var built = BuildPod(pods, pod, graph.nodes[pod.node].id, berth);
            podOf[pod.node] = built;
        }

        // Consoles by role.
        foreach (var c in sc.consoles)
        {
            var anchor = FindAnchor(rooms[c.node], AnchorKind.Objective, c.anchorId);
            string room = graph.nodes[c.node].id;
            switch (c.role)
            {
                case ConsoleRole.FuseSocket:
                {
                    var con = BuildConsole(consoles, $"Fuse Socket {room}", anchor, "Console", new Color(0.2f, 0.7f, 0.7f));
                    con.label = "Fit fuse";
                    con.lockedText = "needs a fuse";
                    con.doneText = "fuse fitted";
                    con.requiredItem = ItemKind.Fuse;
                    con.consumeItem = true;
                    con.setFlags = ShipFlags.FuseInstalled;
                    break;
                }
                case ConsoleRole.Generator:
                {
                    var con = BuildConsole(consoles, $"Generator {room}", anchor, "Console", new Color(0.2f, 0.7f, 0.7f));
                    con.label = "Restart generator";
                    con.lockedText = "no fuse fitted";
                    con.doneText = "running";
                    con.requiredFlags = ShipFlags.FuseInstalled;
                    con.holdSeconds = 6f;
                    con.holdNoise = 16f;
                    con.useNoise = 20f;
                    con.setFlags = ShipFlags.PowerRestored;
                    break;
                }
                case ConsoleRole.PodLaunch:
                {
                    var plan = sc.pods.First(p => p.node == c.podNode);
                    var con = BuildConsole(consoles, $"Pod Launch {room}", anchor, "Console", new Color(0.2f, 0.7f, 0.7f));
                    con.pod = podOf[c.podNode];
                    if (plan.mode == LaunchMode.ManualQuiet && plan.status == PodStatus.Operational)
                    {
                        con.label = "Manual release";
                        con.holdSeconds = 8f;
                        con.holdNoise = 3f;
                        con.useNoise = 3f;
                    }
                    else { con.label = "Launch pod"; con.useNoise = 8f; }
                    break;
                }
                case ConsoleRole.RemoteDoor:
                {
                    var con = BuildConsole(consoles, $"Door Control {room}", anchor, "Console", new Color(0.55f, 0.3f, 0.7f));
                    con.label = "Door control";
                    con.door = doorOf[c.doorEdge];
                    con.useNoise = 2f;
                    break;
                }
                case ConsoleRole.CameraControl:
                {
                    var go = ConsoleObject(consoles, $"Camera Console {room}", anchor, "Console", new Color(0.2f, 0.7f, 0.7f));
                    var cam = go.AddComponent<CameraConsolePlaceholder>();
                    cam.feeds = sc.feeds.Select(f => new CameraConsolePlaceholder.Feed { room = graph.nodes[f.node].displayName, available = f.available }).ToArray();
                    break;
                }
            }
        }

        // Items.
        bool needNoisemakers = sc.items.Any(i => i.cls == ItemClass.Noisemaker && i.candidates.Count > 0);
        if (needNoisemakers) ShipBuilder.AddNoisemakerAssets(); // the device prefab, the player's component and the network prefab list (additive, idempotent)
        foreach (var it in sc.items.Where(i => i.candidates.Count > 0))
        {
            var points = it.candidates.Select(c =>
            {
                var a = FindAnchor(rooms[c.node], AnchorKind.Item, c.anchorId);
                var t = new GameObject($"Spot {it.id} {graph.nodes[c.node].id} {c.anchorId}").transform;
                t.SetParent(spots, false);
                t.position = a.transform.position + (it.kind == ItemKind.None ? Vector3.up * 0.04f : Vector3.zero);
                return t;
            }).ToArray();
            if (it.kind == ItemKind.None) BuildNoisemaker(noisemakers, it.id, points);
            else BuildItem(items, it, points);
        }

        if (Object.FindFirstObjectByType<RoundManager>() == null) RoundSetup.AddRoundManager();

        ship.scenarioSeed = sc.scenarioSeed;
        ship.scenarioFingerprint = sc.Fingerprint();
#if UNITY_EDITOR
        ship.devSolutionReport = ScenarioReport.Describe(graph, result);
#endif
        EditorUtility.SetDirty(ship);
    }

    static GameObject Group(GameObject root, string name)
    {
        var g = new GameObject(name);
        g.transform.SetParent(root.transform, false);
        return g;
    }

    internal static RoomAnchor FindAnchor(PlacedRoom room, AnchorKind kind, string idOrSemantic)
    {
        var a = room.room.anchors.FirstOrDefault(x => x != null && x.kind == kind && x.anchorId == idOrSemantic);
        return a ?? room.room.anchors.First(x => x != null && x.kind == kind && x.semantic.ToString() == idOrSemantic);
    }

    internal static RoomAnchor FindAnchor(PlacedRoom room, AnchorKind kind, AnchorSemantic semantic) =>
        room.room.anchors.First(x => x != null && x.kind == kind && x.semantic == semantic);

    // ---------- Doors ----------

    static SlidingDoor BuildDoor(Transform parent, ShipGraph graph, DoorGate gate, ConnectionSocket socket)
    {
        var primary = gate.options.Select(o => o.kind).First(k => k != OptionKind.Override);
        var matColor = primary == OptionKind.Keycard ? new Color(0.2f, 0.6f, 0.7f) : primary == OptionKind.Power ? new Color(0.7f, 0.2f, 0.2f) : new Color(0.55f, 0.3f, 0.7f);
        string matName = primary == OptionKind.Keycard ? "KeycardDoor" : primary == OptionKind.Power ? "PowerDoor" : "RemoteDoor";
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"Door {graph.edges[gate.edge].id}";
        go.transform.SetParent(parent, false);
        // In the doorway, inside the wall line, as wide as the opening. It slides up into the lintel.
        go.transform.SetPositionAndRotation(socket.transform.position - socket.transform.forward * (GreyboxScale.WallThickness * 0.5f) + Vector3.up * (GreyboxScale.DoorHeight * 0.5f), socket.transform.rotation);
        go.transform.localScale = new Vector3(Cell, GreyboxScale.DoorHeight, GreyboxScale.WallThickness);
        go.GetComponent<Renderer>().sharedMaterial = PrototypeSetup.Mat(matName, matColor);
        go.AddComponent<NetworkObject>();
        go.AddComponent<NetworkTransform>(); // server authority (default)
        var door = go.AddComponent<SlidingDoor>();
        door.openOffset = Vector3.up * (GreyboxScale.DoorHeight + 0.1f);
        CreatureSetup.AddDoorNavigation(go);

        var switchMat = PrototypeSetup.Mat("Switch", new Color(0.9f, 0.8f, 0.1f));
        int row = 0;
        foreach (var opt in gate.options)
        {
            if (opt.kind == OptionKind.Remote) { Indicator(go.transform, socket, switchMat); continue; }
            foreach (float side in new[] { -1f, 1f }) // one control on each side of the door
            {
                var sw = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sw.name = $"Door Control {opt.kind} {(side < 0 ? "inside" : "outside")}";
                sw.transform.SetParent(parent, false);
                sw.transform.SetPositionAndRotation(socket.transform.position + socket.transform.right * (Cell * 0.5f - 0.07f) + socket.transform.forward * (0.55f * side) + Vector3.up * (1.5f - row * 0.32f), socket.transform.rotation);
                sw.transform.localScale = new Vector3(0.12f, 0.25f, 0.25f);
                sw.GetComponent<Renderer>().sharedMaterial = switchMat;
                sw.AddComponent<NetworkObject>();
                var con = sw.AddComponent<ShipConsole>();
                con.door = door;
                con.useNoise = 0f; // the door makes the noise
                switch (opt.kind)
                {
                    case OptionKind.Keycard: con.label = "Door"; con.requiredItem = ItemKind.Keycard; con.lockedText = "needs keycard"; break;
                    case OptionKind.Power: con.label = "Door"; con.requiredFlags = ShipFlags.PowerRestored; con.lockedText = "no power"; break;
                    default: con.label = "Manual override"; con.holdSeconds = 6f; con.holdNoise = 14f; con.lockedText = "stuck"; break;
                }
            }
            row++;
        }
        return door;
    }

    // A remote door has no control of its own: a small dead panel shows that something else operates it.
    static void Indicator(Transform door, ConnectionSocket socket, Material mat)
    {
        var p = GameObject.CreatePrimitive(PrimitiveType.Cube);
        p.name = "Remote Door Panel";
        Object.DestroyImmediate(p.GetComponent<Collider>());
        p.transform.SetParent(door.parent, false);
        p.transform.SetPositionAndRotation(socket.transform.position + socket.transform.right * (Cell * 0.5f - 0.07f) + socket.transform.forward * 0.55f + Vector3.up * 1.5f, socket.transform.rotation);
        p.transform.localScale = new Vector3(0.1f, 0.2f, 0.2f);
        p.GetComponent<Renderer>().sharedMaterial = PrototypeSetup.Mat("RemotePanel", new Color(0.6f, 0.15f, 0.15f));
    }

    // ---------- Consoles and pods ----------

    static GameObject ConsoleObject(Transform parent, string name, RoomAnchor anchor, string matName, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(anchor.transform.position + Vector3.up * 0.6f, anchor.transform.rotation);
        go.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
        go.GetComponent<Renderer>().sharedMaterial = PrototypeSetup.Mat(matName, color);
        go.AddComponent<NetworkObject>();
        return go;
    }

    static ShipConsole BuildConsole(Transform parent, string name, RoomAnchor anchor, string matName, Color color) =>
        ConsoleObject(parent, name, anchor, matName, color).AddComponent<ShipConsole>();

    static EscapePod BuildPod(Transform parent, PodPlan plan, string room, RoomAnchor berth)
    {
        var go = new GameObject($"Pod {room}");
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(berth.transform.position, berth.transform.rotation);
        go.AddComponent<NetworkObject>();
        var pod = go.AddComponent<EscapePod>();
        pod.podName = $"Pod {room}";
        var size = berth.clearance;
        pod.interior = new GameObject("Interior").transform;
        pod.interior.SetParent(go.transform, false);
        pod.interior.localPosition = Vector3.up * size.y * 0.5f;
        pod.interior.localScale = size;
        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = "Status Plate";
        Object.DestroyImmediate(plate.GetComponent<Collider>());
        plate.transform.SetParent(go.transform, false);
        plate.transform.localPosition = Vector3.up * 0.09f;
        plate.transform.localScale = new Vector3(size.x - 0.4f, 0.04f, size.z - 0.4f);
        plate.GetComponent<Renderer>().sharedMaterial = PrototypeSetup.Mat("PodFloor", new Color(0.2f, 0.8f, 0.3f));
        pod.statusPlate = plate.GetComponent<Renderer>();
        if (plan.status == PodStatus.Damaged)
        {
            pod.requiredFlags = ShipFlags.PowerRestored;
            pod.countdownSeconds = 25f;
            pod.countdownNoise = 25f;
            pod.lockedHint = "damaged - needs full power";
            pod.readyHint = "power restored - launch from the console (damaged: slow and loud)";
        }
        else if (plan.mode == LaunchMode.Loud)
        {
            pod.requiredFlags = ShipFlags.PowerRestored;
            pod.countdownSeconds = 10f;
            pod.countdownNoise = 20f;
            pod.lockedHint = "no power - fit a fuse and restart the generator (loud)";
            pod.readyHint = "power restored - launch from the console inside (loud)";
        }
        else
        {
            pod.countdownSeconds = 20f;
            pod.countdownNoise = 8f;
            pod.readyHint = "manual release inside - slow and quiet";
        }
        return pod;
    }

    // A wrecked pod is scenery: no EscapePod, no console. It cannot be launched, and the round does not wait for it.
    static void BuildWreck(Transform parent, string room, RoomAnchor berth)
    {
        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = $"Wrecked Pod {room}";
        Object.DestroyImmediate(plate.GetComponent<Collider>());
        plate.transform.SetParent(parent, false);
        plate.transform.SetPositionAndRotation(berth.transform.position + Vector3.up * 0.09f, berth.transform.rotation);
        plate.transform.localScale = new Vector3(berth.clearance.x - 0.4f, 0.04f, berth.clearance.z - 0.4f);
        plate.GetComponent<Renderer>().sharedMaterial = PrototypeSetup.Mat("PodWrecked", new Color(0.35f, 0.1f, 0.1f));
    }

    // ---------- Items ----------

    static void BuildItem(Transform parent, ItemPlan it, Transform[] points)
    {
        var size = it.kind == ItemKind.Keycard ? new Vector3(0.4f, 0.06f, 0.28f) : new Vector3(0.22f, 0.3f, 0.22f);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"Item {it.id}";
        go.transform.SetParent(parent, false);
        go.transform.localScale = size;
        go.transform.position = points[0].position + Vector3.up * size.y * 0.5f;
        go.GetComponent<Renderer>().sharedMaterial = it.kind == ItemKind.Keycard ? PrototypeSetup.Mat("Keycard", new Color(0.1f, 0.9f, 1f)) : PrototypeSetup.Mat("Fuse", new Color(1f, 0.5f, 0.1f));
        go.AddComponent<NetworkObject>();
        go.AddComponent<NetworkTransform>(); // server authority (default)
        go.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // moves, so never baked
        go.AddComponent<ItemPickup>().kind = it.kind;
        var scenarioItem = go.AddComponent<ScenarioItem>();
        scenarioItem.itemId = it.id;
        scenarioItem.spots = points;
    }

    // The same placeholder the Ship scene uses for its noisemaker pickups (see ShipBuilder.BuildNoisemakerPickups), standing on its candidate spots.
    static void BuildNoisemaker(Transform parent, string id, Transform[] points)
    {
        var go = new GameObject($"Noisemaker Pickup {id}");
        go.transform.SetParent(parent, false);
        go.transform.position = points[0].position;
        Part(go.transform, "Body", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.17f, 0.03f, 0.17f), PrototypeSetup.Mat("Noisemaker Body", new Color(0.07f, 0.07f, 0.08f)));
        Part(go.transform, "Band", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.178f, 0.009f, 0.178f), PrototypeSetup.Mat("Noisemaker Band", new Color(0.62f, 0.33f, 0.1f)));
        Part(go.transform, "Status Light", PrimitiveType.Sphere, new Vector3(0f, 0.034f, 0.045f), new Vector3(0.028f, 0.018f, 0.028f), PrototypeSetup.Mat("Noisemaker Light", new Color(1f, 0.45f, 0.1f)));
        go.AddComponent<BoxCollider>().size = new Vector3(0.17f, 0.07f, 0.17f);
        go.AddComponent<NetworkObject>();
        go.AddComponent<NetworkTransform>();
        go.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
        go.AddComponent<NoisemakerPickup>().spots = points;
    }

    static void Part(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // ---------- In-scene NetworkObjects ----------

    /// <summary>
    /// NetworkObject.OnValidate (internal) derives the hash from the saved GlobalObjectId: run it on every in-scene NetworkObject of the reopened
    /// scene, then verify each has a unique non-zero hash (the same check Alien > Build Ship Scene makes). Returns the number of bad ones.
    /// </summary>
    internal static int EnsureNetworkObjectHashes()
    {
        var validate = typeof(NetworkObject).GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var seen = new HashSet<uint>();
        int bad = 0, count = 0;
        foreach (var no in Object.FindObjectsByType<NetworkObject>(FindObjectsSortMode.None))
        {
            validate?.Invoke(no, null);
            count++;
            if (no.PrefabIdHash == 0 || !seen.Add(no.PrefabIdHash)) bad++;
        }
        if (bad > 0) Debug.LogError($"Procedural ship: {bad} NetworkObject(s) have a missing or duplicate GlobalObjectIdHash. Reopen the scene, save it, and run the build again.");
        else Debug.Log($"Procedural ship: {count} in-scene NetworkObjects have unique hashes.");
        return bad;
    }
}
