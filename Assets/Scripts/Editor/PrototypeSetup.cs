using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

/// <summary>
/// Menu: Alien > Setup Multiplayer Prototype. Builds the test scene and player prefab.
/// Rerun-safe: materials are reused, and existing scene/prefab are only replaced after a confirm dialog.
/// </summary>
public static class PrototypeSetup
{
    internal const string PrefabPath = "Assets/Prefabs/Player.prefab";
    const string ScenePath = "Assets/Scenes/NetTest.unity";
    const string MatDir = "Assets/Materials/Prototype";
    const string SkyProfilePath = "Assets/Settings/SkyandFogSettingsProfile.asset";

    [MenuItem("Alien/Setup Multiplayer Prototype")]
    static void Setup()
    {
        bool exists = File.Exists(PrefabPath) || File.Exists(ScenePath);
        if (exists && !EditorUtility.DisplayDialog("Setup Multiplayer Prototype",
                $"{PrefabPath} and/or {ScenePath} already exist.\nReplace them? Manual edits to them will be lost.",
                "Replace", "Cancel"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        Directory.CreateDirectory("Assets/Prefabs");
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory(MatDir);

        var floorMat = Mat("Floor", new Color(0.35f, 0.35f, 0.38f));
        var wallMat = Mat("Wall", new Color(0.6f, 0.6f, 0.62f));
        var obstacleMat = Mat("Obstacle", new Color(0.7f, 0.45f, 0.2f));
        var playerMat = Mat("Player", new Color(0.2f, 0.7f, 0.3f));

        var prefab = BuildPlayerPrefab(playerMat);
        BuildScene(prefab, floorMat, wallMat, obstacleMat);
        Debug.Log($"Prototype setup done. Open {ScenePath} and press Play (host/join from the panel).");
    }

    internal static Material Mat(string name, Color color)
    {
        string path = $"{MatDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m; // never overwrite user-edited materials
        m = new Material(Shader.Find("HDRP/Lit"));
        m.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static GameObject BuildPlayerPrefab(Material bodyMat)
    {
        var root = new GameObject("Player");
        root.AddComponent<NetworkObject>();
        root.AddComponent<CharacterController>();
        var nt = root.AddComponent<NetworkTransform>();
        nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner; // owner-authoritative movement
        nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
        nt.SyncRotAngleX = nt.SyncRotAngleZ = false; // body yaw only

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        Object.DestroyImmediate(body.GetComponent<Collider>()); // CharacterController is the collider
        body.GetComponent<Renderer>().sharedMaterial = bodyMat;

        var camGo = new GameObject("Camera");
        camGo.transform.SetParent(root.transform, false);
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        cam.enabled = false;
        var listener = camGo.AddComponent<AudioListener>();
        listener.enabled = false;

        var ctrl = root.AddComponent<NetworkFirstPersonController>();
        ctrl.playerCamera = cam;
        ctrl.audioListener = listener;
        ctrl.body = body.transform;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static void BuildScene(GameObject playerPrefab, Material floorMat, Material wallMat, Material obstacleMat)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AddLighting();

        // Level geometry.
        var level = new GameObject("Level").transform;
        Block(level, "Floor", new Vector3(0, -0.1f, 0), new Vector3(30, 0.2f, 30), floorMat);
        Block(level, "Wall N", new Vector3(0, 2, 15), new Vector3(30, 4, 0.4f), wallMat);
        Block(level, "Wall S", new Vector3(0, 2, -15), new Vector3(30, 4, 0.4f), wallMat);
        Block(level, "Wall E", new Vector3(15, 2, 0), new Vector3(0.4f, 4, 30), wallMat);
        Block(level, "Wall W", new Vector3(-15, 2, 0), new Vector3(0.4f, 4, 30), wallMat);
        Block(level, "Crate 1", new Vector3(3, 0.75f, 2), new Vector3(1.5f, 1.5f, 1.5f), obstacleMat);
        Block(level, "Crate 2", new Vector3(-4, 0.5f, -3), new Vector3(2f, 1f, 1f), obstacleMat);
        Block(level, "Pillar", new Vector3(-2, 2, 5), new Vector3(1, 4, 1), obstacleMat);
        // Low ceiling: gap of 1.4m. Crouch (1.0m) fits, standing (1.8m) does not.
        Block(level, "Low Ceiling", new Vector3(0, 1.9f, -8), new Vector3(4, 1, 6), obstacleMat);
        Block(level, "Low Ceiling Post L", new Vector3(-2.2f, 0.7f, -8), new Vector3(0.4f, 1.4f, 6), obstacleMat);
        Block(level, "Low Ceiling Post R", new Vector3(2.2f, 0.7f, -8), new Vector3(0.4f, 1.4f, 6), obstacleMat);

        // Four spawn points, one per corner, facing the centre.
        var spawns = new GameObject("Spawn Points").transform;
        Vector3[] pos = { new(-11, 0.05f, -11), new(11, 0.05f, -11), new(11, 0.05f, 11), new(-11, 0.05f, 11) };
        for (int i = 0; i < 4; i++)
        {
            var sp = new GameObject($"Spawn {i}");
            sp.transform.SetParent(spawns);
            sp.transform.position = pos[i];
            sp.transform.rotation = Quaternion.LookRotation(new Vector3(-pos[i].x, 0, -pos[i].z));
            sp.AddComponent<SpawnPoint>().index = i;
        }

        AddNetwork(playerPrefab, new Vector3(0, 9, -13), Quaternion.Euler(30f, 0, 0));

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddToBuildSettings(ScenePath);
    }

    // One sun plus the project's existing sky/fog/exposure profile.
    internal static void AddLighting()
    {
        var sun = new GameObject("Sun");
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var light = sun.AddComponent<Light>();
        light.type = LightType.Directional;
        var hdLight = sun.AddComponent<HDAdditionalLightData>();
        hdLight.SetIntensity(10000f, LightUnit.Lux);

        var volGo = new GameObject("Global Volume");
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(SkyProfilePath);
    }

    // NetworkManager + transport + connection panel, and the camera shown before a local player exists.
    internal static void AddNetwork(GameObject playerPrefab, Vector3 lobbyCamPos, Quaternion lobbyCamRot)
    {
        var lobbyCam = new GameObject("Lobby Camera", typeof(Camera), typeof(AudioListener));
        lobbyCam.tag = "MainCamera";
        lobbyCam.transform.SetPositionAndRotation(lobbyCamPos, lobbyCamRot);

        var net = new GameObject("Network");
        var nm = net.AddComponent<NetworkManager>();
        var utp = net.AddComponent<UnityTransport>();
        nm.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = utp,
            PlayerPrefab = playerPrefab,
            ConnectionApproval = true,
        };
        var ui = net.AddComponent<ConnectionUI>();
        ui.networkManager = nm;
        ui.lobbyCamera = lobbyCam;
    }

    internal static void Block(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent);
        go.transform.position = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
    }

    // Append only; existing build scenes stay. Needed for standalone builds and NGO in-scene object sync.
    internal static void AddToBuildSettings(string path)
    {
        var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (list.Exists(s => s.path == path)) return;
        list.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = list.ToArray();
    }
}
