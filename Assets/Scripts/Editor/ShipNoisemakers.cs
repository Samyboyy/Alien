using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Throwable noisemakers in the Ship (menu: Alien > Add Throwable Noisemakers; also part of fresh Ship builds). Idempotent: each step only
/// adds or repairs what is missing and leaves everything that exists alone. In this order:
///  1. the placeholder device prefab is created (or its components repaired) and saved;
///  2. that exact asset is listed once in the default network prefab list (duplicates are removed);
///  3. the Player prefab gets PlayerNoisemakers, pointing at that same asset (a wrong reference is repaired);
///  4. the NetworkManager in the open scene is given that prefab list. A manager only registers the prefabs in the lists it references, so
///     the entry in the asset alone registers nothing; this is saved in the scene, before any Host or Client starts;
///  5. the pickups are added at authored floor points if missing;
///  6. everything is validated: the registration, the prefab and its reference, and every spot's placement.
/// The scene is saved only when it changed AND validation found no problems; the serialized result is then read back from the files.
/// Nothing is rebuilt and no NavMesh rebake is needed.
/// </summary>
public static partial class ShipBuilder
{
    const string NoisemakerRoot = "Noisemakers", NoisemakerSpotsRoot = "Noisemaker Spots";
    internal const string DevicePrefabPath = "Assets/Prefabs/Noisemaker Device.prefab";
    internal const string NetworkPrefabsPath = "Assets/DefaultNetworkPrefabs.asset";

    // Pickups: a room and its candidate floor cells. The cells were chosen from the map: plain floor, no door, console, item, spawn, vent mouth
    // or hiding furniture within reach, spread over rooms away from the player spawns (the Crew room). Each was checked against the saved
    // scene's colliders: floor underneath, nothing solid within half a metre.
    static readonly (string room, (int c, int r)[] cells)[] NoisemakerSpawns =
    {
        ("Engine", new[] { (8, 4), (10, 5), (12, 6) }),
        ("Cargo", new[] { (4, 12), (9, 14), (6, 16) }),
        ("Storage", new[] { (26, 12), (23, 14), (32, 13) }),
        ("Security", new[] { (23, 3), (25, 5), (26, 3) }),
        ("Medbay", new[] { (26, 22), (30, 21), (25, 24) }),
    };

    const float PlacementTolerance = 0.01f, PlacementReach = 0.15f;

    // ---------- 1-3: assets ----------

    /// <summary>The device prefab, its entry in the network prefab list and the Player prefab component. Returns how many assets changed.</summary>
    internal static int AddNoisemakerAssets()
    {
        int changed = 0;
        Directory.CreateDirectory("Assets/Prefabs");

        // 1. the device prefab
        var device = AssetDatabase.LoadAssetAtPath<GameObject>(DevicePrefabPath);
        if (device == null)
        {
            var go = new GameObject("Noisemaker Device");
            try
            {
                var light = BuildNoisemakerMesh(go.transform);
                go.AddComponent<BoxCollider>().size = new Vector3(0.17f, 0.07f, 0.17f);
                EnsureDeviceComponents(go);
                go.GetComponent<ThrownNoisemaker>().statusLight = light;
                PrefabUtility.SaveAsPrefabAsset(go, DevicePrefabPath);
                changed++;
                Debug.Log($"Noisemaker device prefab created: {DevicePrefabPath}");
            }
            finally { Object.DestroyImmediate(go); }
            AssetDatabase.ImportAsset(DevicePrefabPath);
        }
        else
        {
            var contents = PrefabUtility.LoadPrefabContents(DevicePrefabPath);
            try
            {
                if (EnsureDeviceComponents(contents))
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, DevicePrefabPath);
                    changed++;
                    Debug.Log("Noisemaker device prefab: missing components repaired.");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        device = AssetDatabase.LoadAssetAtPath<GameObject>(DevicePrefabPath);

        // 2. the default network prefab list: this exact asset, once
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsPath);
        if (list == null) Debug.LogError($"{NetworkPrefabsPath} not found: the device cannot be registered.");
        else if (device != null)
        {
            if (EnsureListed(list, device))
            {
                EditorUtility.SetDirty(list);
                AssetDatabase.SaveAssets();
                changed++;
                Debug.Log($"Noisemaker device registered in {NetworkPrefabsPath}.");
            }
        }

        // 3. the Player prefab
        var root = PrefabUtility.LoadPrefabContents(PrototypeSetup.PrefabPath);
        try
        {
            var inv = root.GetComponent<PlayerNoisemakers>();
            bool dirty = false;
            if (inv == null) { inv = root.AddComponent<PlayerNoisemakers>(); dirty = true; }
            if (device != null && inv.devicePrefab != device) { inv.devicePrefab = device; dirty = true; }
            if (dirty)
            {
                PrefabUtility.SaveAsPrefabAsset(root, PrototypeSetup.PrefabPath);
                changed++;
                Debug.Log("Player prefab: noisemaker inventory added or its device reference repaired.");
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return changed;
    }

    /// <summary>Makes sure the list holds this exact prefab once: adds it, or removes repeats. False when it was already right.</summary>
    internal static bool EnsureListed(NetworkPrefabsList list, GameObject prefab)
    {
        var entries = list.PrefabList.Where(p => p != null && p.Prefab == prefab).ToList();
        if (entries.Count == 1) return false;
        foreach (var extra in entries.Skip(1)) list.Remove(extra); // keep the first, drop repeats
        if (entries.Count == 0) list.Add(new NetworkPrefab { Prefab = prefab });
        return true;
    }

    // Everything the device needs on its single root; true when something had to be added.
    static bool EnsureDeviceComponents(GameObject go)
    {
        bool changed = false;
        T Need<T>() where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) return c;
            changed = true;
            return go.AddComponent<T>();
        }
        if (go.GetComponent<Rigidbody>() == null)
        {
            var body = go.AddComponent<Rigidbody>();
            body.mass = 0.3f;
            body.linearDamping = 0.05f;
            body.angularDamping = 0.2f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            changed = true;
        }
        Need<BoxCollider>();
        Need<NetworkObject>();
        Need<NetworkTransform>(); // server authority (default)
        Need<NetworkRigidbody>(); // clients' copies are kinematic: only the host simulates
        var nav = Need<NavMeshModifier>();
        if (!nav.ignoreFromBuild) { nav.ignoreFromBuild = true; changed = true; }
        Need<ThrownNoisemaker>();
        return changed;
    }

    // The visible placeholder (no colliders: the root carries one). Returns the status light renderer.
    static Renderer BuildNoisemakerMesh(Transform parent)
    {
        Part(parent, "Body", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.17f, 0.03f, 0.17f), PrototypeSetup.Mat("Noisemaker Body", new Color(0.07f, 0.07f, 0.08f)));
        Part(parent, "Band", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.178f, 0.009f, 0.178f), PrototypeSetup.Mat("Noisemaker Band", new Color(0.62f, 0.33f, 0.1f)));
        var light = Part(parent, "Status Light", PrimitiveType.Sphere, new Vector3(0f, 0.034f, 0.045f), new Vector3(0.028f, 0.018f, 0.028f), NoisemakerLightMat());
        return light.GetComponent<Renderer>();
    }

    static GameObject Part(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }

    static Material NoisemakerLightMat()
    {
        const string path = "Assets/Materials/Prototype/Noisemaker Light.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        var color = new Color(1f, 0.45f, 0.1f);
        m = new Material(Shader.Find("HDRP/Lit"));
        m.SetColor("_BaseColor", color);
        HDMaterial.SetUseEmissiveIntensity(m, true);
        HDMaterial.SetEmissiveColor(m, color);
        HDMaterial.SetEmissiveIntensity(m, 20f, EmissiveIntensityUnit.Nits);
        HDMaterial.ValidateMaterial(m);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    // ---------- 4-5: scene ----------

    /// <summary>
    /// Assets first, then the open scene: the manager's prefab list and the pickups. Returns how many things in the SCENE changed (0 = the
    /// scene is already right and must not be saved).
    /// </summary>
    internal static int AddNoisemakers()
    {
        AddNoisemakerAssets();
        int changed = 0;

        var nm = Object.FindFirstObjectByType<NetworkManager>();
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsPath);
        if (nm == null) Debug.LogError("No NetworkManager in the open scene: the device prefab cannot be registered.");
        else if (list != null && !nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(list))
        {
            Undo.RecordObject(nm, "Register network prefab list");
            nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);
            EditorUtility.SetDirty(nm);
            changed++;
            Debug.Log($"NetworkManager: now uses the prefab list {NetworkPrefabsPath} (it had none, so no prefab outside the player was registered).");
        }

        if (GameObject.Find(NoisemakerRoot) != null) Debug.Log($"'{NoisemakerRoot}' already in the scene - left as it is.");
        else if (AssetDatabase.LoadAssetAtPath<GameObject>(DevicePrefabPath) == null) Debug.LogError("Noisemaker device prefab missing: no pickups added.");
        else
        {
            BuildNoisemakerPickups();
            changed++;
        }
        Physics.SyncTransforms(); // the objects above were just created or moved: the queries below must see them
        return changed;
    }

    static void BuildNoisemakerPickups()
    {
        var root = new GameObject(NoisemakerRoot).transform;
        var spotRoot = new GameObject(NoisemakerSpotsRoot).transform;
        for (int i = 0; i < NoisemakerSpawns.Length; i++)
        {
            var (room, cells) = NoisemakerSpawns[i];
            var spots = new List<Transform>();
            for (int k = 0; k < cells.Length; k++)
            {
                var spot = new GameObject($"Noisemaker Spot {room} {k}").transform;
                spot.SetParent(spotRoot);
                spot.position = World(cells[k].c, cells[k].r, 0.04f); // the pickup's centre: resting on the floor
                spots.Add(spot);
            }
            var go = new GameObject($"Noisemaker Pickup {i} {room}");
            go.transform.SetParent(root);
            go.transform.position = spots[0].position;
            BuildNoisemakerMesh(go.transform);
            go.AddComponent<BoxCollider>().size = new Vector3(0.17f, 0.07f, 0.17f);
            go.AddComponent<NetworkObject>();
            go.AddComponent<NetworkTransform>();
            go.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // moves, so never baked
            go.AddComponent<NoisemakerPickup>().spots = spots.ToArray();
        }
        Debug.Log($"Noisemakers added: {NoisemakerSpawns.Length} pickups, 3 candidate spots each. No NavMesh rebake is needed.");
    }

    // ---------- 6: validation ----------

    /// <summary>
    /// Checks that a throw can work: the device prefab is one valid asset with a single root NetworkObject, the Player prefab points at it, and
    /// the open scene's NetworkManager actually lists it (exact asset, once, with distinct non-zero hashes). Logs each problem; returns the count.
    /// </summary>
    internal static int ValidateNoisemakerRegistration(bool log = true)
    {
        var problems = new List<string>();
        var device = AssetDatabase.LoadAssetAtPath<GameObject>(DevicePrefabPath);
        if (device == null) problems.Add($"{DevicePrefabPath} is missing");
        else
        {
            if (PrefabUtility.GetPrefabAssetType(device) != PrefabAssetType.Regular) problems.Add("the device is not a regular prefab asset (a variant or a scene object)");
            var objects = device.GetComponentsInChildren<NetworkObject>(true);
            if (objects.Length != 1 || device.GetComponent<NetworkObject>() == null) problems.Add($"the device has {objects.Length} NetworkObjects; it needs exactly one, on its root");
            else if (Hash(objects[0]) == 0) problems.Add("the device's NetworkObject has a zero GlobalObjectIdHash (reimport the prefab)");
            if (device.GetComponent<ThrownNoisemaker>() == null) problems.Add("the device has no ThrownNoisemaker on its root");
        }

        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        var inv = player != null ? player.GetComponent<PlayerNoisemakers>() : null;
        if (inv == null) problems.Add("the Player prefab has no PlayerNoisemakers");
        else if (inv.devicePrefab == null) problems.Add("PlayerNoisemakers has no device prefab assigned");
        else if (inv.devicePrefab != device) problems.Add($"PlayerNoisemakers points at '{AssetDatabase.GetAssetPath(inv.devicePrefab)}', not at {DevicePrefabPath}");

        var nm = Object.FindFirstObjectByType<NetworkManager>();
        if (nm == null) problems.Add("no NetworkManager in the open scene");
        else if (device != null)
        {
            var lists = nm.NetworkConfig.Prefabs.NetworkPrefabsLists;
            if (lists.Any(l => l == null)) problems.Add("the NetworkManager has an empty prefab-list slot");
            var entries = lists.Where(l => l != null).SelectMany(l => l.PrefabList.Where(p => p != null && p.Prefab != null)).ToList();
            int registered = entries.Count(p => p.Prefab == device);
            if (registered == 0)
                problems.Add($"the NetworkManager's prefab lists ({(lists.Count == 0 ? "none attached" : string.Join(", ", lists.Where(l => l != null).Select(AssetDatabase.GetAssetPath)))}) do not contain {DevicePrefabPath}");
            else if (registered > 1) problems.Add($"{DevicePrefabPath} is listed {registered} times");
            var hashes = new Dictionary<uint, string>();
            foreach (var p in entries.Select(e => e.Prefab).Distinct())
            {
                var no = p.GetComponent<NetworkObject>();
                if (no == null) continue;
                if (Hash(no) == 0) problems.Add($"'{AssetDatabase.GetAssetPath(p)}' has a zero prefab hash");
                else if (hashes.TryGetValue(Hash(no), out var other)) problems.Add($"prefabs '{AssetDatabase.GetAssetPath(p)}' and '{other}' share the hash {Hash(no)}");
                else hashes[Hash(no)] = AssetDatabase.GetAssetPath(p);
            }
            if (log && problems.Count == 0)
                Debug.Log($"Noisemaker registration ok: {DevicePrefabPath} (hash {Hash(device.GetComponent<NetworkObject>())}) is in {string.Join(", ", lists.Select(AssetDatabase.GetAssetPath))}, used by '{nm.name}'.");
        }
        if (log) foreach (var p in problems) Debug.LogError($"Noisemaker setup: {p}.");
        return problems.Count;
    }

    /// <summary>
    /// Checks that the device can be heard: its components, sane distances, world-audio routing that is not muted, and that every temporary
    /// stand-in sound can be generated (so the prototype is audible with every optional AudioBank clip unset). Returns the problem count.
    /// </summary>
    internal static int ValidateNoisemakerAudio()
    {
        var problems = new List<string>();
        var device = AssetDatabase.LoadAssetAtPath<GameObject>(DevicePrefabPath);
        if (device != null)
        {
            var thrown = device.GetComponent<ThrownNoisemaker>();
            if (thrown == null) problems.Add("the device has no ThrownNoisemaker (it creates and plays its own audio sources)");
            else
            {
                if (device.GetComponent<Rigidbody>() == null || device.GetComponent<Collider>() == null) problems.Add("the device needs a Rigidbody and a Collider on its root");
                if (thrown.statusLight == null) Debug.LogWarning("Noisemaker device: no status light assigned (it will not blink).");
                if (thrown.pulseRange <= 0f) problems.Add("the device's pulse range is not positive");
                if (device.transform.lossyScale.x <= 0f || device.transform.lossyScale.y <= 0f || device.transform.lossyScale.z <= 0f) problems.Add("the device root has a zero or negative scale (a broken emitter transform)");
            }
        }
        var bank = AssetDatabase.LoadAssetAtPath<AudioBank>("Assets/Resources/AudioBank.asset");
        if (bank == null) Debug.LogWarning("No AudioBank: the noisemaker still plays its stand-in sounds, at the default volumes and distances.");
        else
        {
            if (bank.noisemakerFullDistance <= 0f || bank.noisemakerMaxDistance <= bank.noisemakerFullDistance) problems.Add($"AudioBank noisemaker distances are not sensible (full {bank.noisemakerFullDistance}, max {bank.noisemakerMaxDistance})");
            var thrown = device != null ? device.GetComponent<ThrownNoisemaker>() : null;
            if (thrown != null && bank.noisemakerMaxDistance < thrown.pulseRange) problems.Add($"AudioBank noisemaker max distance ({bank.noisemakerMaxDistance} m) is below the pulse's logical range ({thrown.pulseRange} m): the creature would hear it from where players cannot");
            if (bank.noisemakerPulseVolume <= 0f) Debug.LogWarning("AudioBank noisemakerPulseVolume is 0: pulses are muted.");
            if (bank.worldVolume <= 0f || bank.masterVolume <= 0f) problems.Add("the AudioBank's master or World volume is 0: no world sound, including the noisemaker, can be heard");
        }
        foreach (NoisemakerSound kind in System.Enum.GetValues(typeof(NoisemakerSound)))
        {
            var data = NoisemakerSynth.Generate(kind, 44100);
            float peak = 0f;
            foreach (float v in data) peak = Mathf.Max(peak, Mathf.Abs(v));
            if (data.Length == 0 || float.IsNaN(peak) || peak < 0.05f || peak > 1f) problems.Add($"the temporary {kind} sound cannot be generated correctly (peak {peak})");
        }
        foreach (var p in problems) Debug.LogError($"Noisemaker audio: {p}.");
        if (problems.Count == 0) Debug.Log("Noisemaker audio ok: components present, distances sane, world routing audible, all five stand-in sounds generate.");
        return problems.Count;
    }

    /// <summary>Checks every pickup spot: a supporting floor under it and nothing solid in the pickup's own volume. Logs each blocker precisely.</summary>
    internal static int ValidateNoisemakers()
    {
        int problems = ValidateNoisemakerRegistration() + ValidateNoisemakerAudio();
        var pickups = Object.FindObjectsByType<NoisemakerPickup>(FindObjectsSortMode.None);
        if (pickups.Length == 0) { Debug.LogWarning("No noisemaker pickups in the scene: run Alien > Add Throwable Noisemakers."); return problems + 1; }

        Physics.SyncTransforms();
        var avoid = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
            .Where(m => m is SlidingDoor or ShipConsole or ItemPickup or HidingSpot or CreatureVentEntrance or SpawnPoint or EscapePod)
            .Select(m => m.transform.position).ToList();
        var rooms = new HashSet<string>();
        int spots = 0;
        foreach (var p in pickups)
        {
            var box = p.GetComponent<BoxCollider>();
            if (box == null) { problems++; Debug.LogError($"{PathOf(p.transform)} has no BoxCollider.", p); continue; }
            if (p.spots == null || p.spots.Length == 0) { problems++; Debug.LogError($"{PathOf(p.transform)} has no spots.", p); continue; }
            foreach (var s in p.spots)
            {
                if (s == null) { problems++; Debug.LogError($"{PathOf(p.transform)} has an empty spot.", p); continue; }
                spots++;
                foreach (var issue in SpotProblems(p, box, s)) { problems++; Debug.LogError(issue, s); }
                foreach (var a in avoid)
                    if (Vector2.Distance(new Vector2(a.x, a.z), new Vector2(s.position.x, s.position.z)) < 1.4f)
                    { problems++; Debug.LogError($"{PathOf(s)}: too close to a door, console, item, vent mouth, hiding place or spawn at ({a.x:0.0}, {a.z:0.0}).", s); break; }
                var room = RoomVolume.At(s.position);
                if (room != null) rooms.Add(room.roomName);
            }
        }
        Debug.Log($"Noisemaker validation: {pickups.Length} pickups, {spots} spots over {rooms.Count} room(s), {problems} problems.");
        return problems;
    }

    // One spot: the pickup's real collider volume at the spot's position, tested with the pure placement rules. Returns one message per problem.
    internal static List<string> SpotProblems(NoisemakerPickup pickup, BoxCollider box, Transform spot)
    {
        Vector3 scale = pickup.transform.lossyScale;
        Vector3 half = Vector3.Scale(box.size, scale) * 0.5f;
        Vector3 centre = spot.position + Vector3.Scale(box.center, scale);
        float bottom = centre.y - half.y, top = centre.y + half.y;

        // The volume plus a margin round it and down to where a floor could be, triggers included (the rules decide what they mean).
        Vector3 queryCentre = new(centre.x, centre.y - PlacementReach * 0.5f, centre.z);
        Vector3 queryHalf = new(half.x + 0.05f, half.y + PlacementReach * 0.5f + 0.02f, half.z + 0.05f);
        var boxes = new List<PlacementBox>();
        foreach (var c in Physics.OverlapBox(queryCentre, queryHalf, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
        {
            var b = c.bounds;
            boxes.Add(new PlacementBox
            {
                name = PathOf(c.transform), kind = c.GetType().Name, layer = c.gameObject.layer, trigger = c.isTrigger,
                own = c.transform.IsChildOf(spot) || c.transform.IsChildOf(pickup.transform),
                cx = b.center.x, cy = b.center.y, cz = b.center.z, hx = b.extents.x, hy = b.extents.y, hz = b.extents.z,
            });
        }
        var result = NoisemakerPlacement.Evaluate(centre.x, centre.z, half.x, half.z, bottom, top, PlacementTolerance, PlacementReach, boxes);
        var issues = new List<string>();
        if (!result.supported)
            issues.Add($"{PathOf(spot)} at ({centre.x:0.00}, {bottom:0.00}, {centre.z:0.00}): no supporting floor within {PlacementReach} m under the pickup.");
        foreach (var b in result.blockers)
            issues.Add($"{PathOf(spot)} at ({centre.x:0.00}, {centre.y:0.00}, {centre.z:0.00}) is blocked by {NoisemakerPlacement.Describe(b)}.");
        return issues;
    }

    static string PathOf(Transform t) => CreatureVentNetwork.PathOf(t);

    // The prefab's registration hash, read from its serialized field (NetworkObject keeps it internal).
    static uint Hash(NetworkObject no) => new SerializedObject(no).FindProperty("GlobalObjectIdHash")?.uintValue ?? 0;

    /// <summary>Reads the saved files back: the scene's NetworkManager references the list, and the list references the device. Logs the result.</summary>
    internal static bool VerifyNoisemakerSerialized(string scenePath)
    {
        string listGuid = AssetDatabase.AssetPathToGUID(NetworkPrefabsPath), deviceGuid = AssetDatabase.AssetPathToGUID(DevicePrefabPath);
        string scene = File.ReadAllText(scenePath), list = File.ReadAllText(NetworkPrefabsPath);
        bool inScene = Regex.IsMatch(scene, @"NetworkPrefabsLists:\s*\r?\n(\s*- \{fileID: \d+, guid: [0-9a-f]+, type: 2\}\s*\r?\n)*?\s*- \{fileID: 11400000, guid: " + listGuid);
        bool inList = Regex.Matches(list, "guid: " + deviceGuid).Count == 1;
        Debug.Log($"Saved result: {scenePath} {(inScene ? "references" : "DOES NOT reference")} {NetworkPrefabsPath}; the list {(inList ? "contains" : "DOES NOT contain exactly one entry for")} {DevicePrefabPath}.");
        return inScene && inList;
    }
}

/// <summary>Menu: Alien > Add Throwable Noisemakers. Updates the existing Ship scene; nothing is rebuilt. Safe to run again: a second run changes nothing.</summary>
public static class NoisemakerSetup
{
    const string ScenePath = "Assets/Scenes/Ship.unity";

    [MenuItem("Alien/Add Throwable Noisemakers")]
    static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Not in Play Mode."); return; }
        if (!File.Exists(ScenePath)) { Debug.LogError("Ship scene missing. Run Alien > Build Ship Scene first."); return; }
        if (!File.Exists(PrototypeSetup.PrefabPath)) { Debug.LogError("Run Alien > Setup Multiplayer Prototype first (Player prefab missing)."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int changed = ShipBuilder.AddNoisemakers();
        int problems = ShipBuilder.ValidateNoisemakers();
        if (changed == 0)
        {
            Debug.Log(problems == 0 ? "Noisemakers: everything is already set up; nothing changed." : $"Noisemakers: nothing to change, but {problems} problem(s) remain (listed above).");
            return;
        }
        if (problems > 0)
        {
            Debug.LogError($"Noisemakers: {problems} problem(s) found, so the Ship scene was NOT saved. Fix them, then run this again.");
            return;
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        bool ok = ShipBuilder.VerifyNoisemakerSerialized(ScenePath);
        Debug.Log(ok ? "Noisemakers: Ship saved and the saved files verified. No NavMesh rebake is needed." : "Noisemakers: Ship saved, but the saved files did not verify (see above).");
    }
}

/// <summary>
/// Before Play Mode starts in a scene that has noisemaker pickups, checks that the device prefab is registered with the NetworkManager, and
/// stops Play Mode with a clear error if it is not (the alternative is a failed throw in the middle of a test).
/// </summary>
[InitializeOnLoad]
static class NoisemakerPlayGuard
{
    static NoisemakerPlayGuard() => EditorApplication.playModeStateChanged += OnState;

    static void OnState(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.ExitingEditMode) return;
        if (Object.FindFirstObjectByType<NoisemakerPickup>() == null || Object.FindFirstObjectByType<NetworkManager>() == null) return;
        if (ShipBuilder.ValidateNoisemakerRegistration(false) == 0) return;
        ShipBuilder.ValidateNoisemakerRegistration(true); // log the reasons
        Debug.LogError("Play Mode stopped: the noisemaker device prefab is not registered. Run Alien > Add Throwable Noisemakers, then press Play again.");
        EditorApplication.isPlaying = false;
    }
}
