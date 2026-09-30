using System.IO;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Alien > Add Door And Switch. Adds only the door room, door, switch and the PlayerInteractor
/// component to the existing NetTest scene and Player prefab. Rerun-safe: skips whatever already exists.
/// </summary>
public static class DoorSetup
{
    const string ScenePath = "Assets/Scenes/NetTest.unity";
    const string PrefabPath = "Assets/Prefabs/Player.prefab";
    const string RoomName = "Door Room";

    [MenuItem("Alien/Add Door And Switch")]
    static void Setup()
    {
        if (!File.Exists(ScenePath) || !File.Exists(PrefabPath))
        {
            Debug.LogError("Run Alien > Setup Multiplayer Prototype first (NetTest scene / Player prefab missing).");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        AddInteractorToPlayer();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        if (GameObject.Find(RoomName) != null)
        {
            Debug.Log($"'{RoomName}' already in NetTest - scene left unchanged.");
            return;
        }
        BuildRoom();
        EditorSceneManager.SaveScene(scene);
        Debug.Log("Door room, door and switch added to NetTest.");
    }

    static void AddInteractorToPlayer()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            if (root.GetComponent<PlayerInteractor>() != null) return;
            root.AddComponent<PlayerInteractor>();
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("Added PlayerInteractor to Player prefab.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // Small 9x6 room on the east side. Door sits in its west wall; the switch is on the outside wall next to it.
    static void BuildRoom()
    {
        var wallMat = PrototypeSetup.Mat("Wall", new Color(0.6f, 0.6f, 0.62f));
        var doorMat = PrototypeSetup.Mat("Door", new Color(0.25f, 0.4f, 0.7f));
        var switchMat = PrototypeSetup.Mat("Switch", new Color(0.9f, 0.8f, 0.1f));

        var room = new GameObject(RoomName).transform;
        PrototypeSetup.Block(room, "Wall North", new Vector3(8, 2, 4.5f), new Vector3(6.4f, 4, 0.4f), wallMat);
        PrototypeSetup.Block(room, "Wall South", new Vector3(8, 2, -4.5f), new Vector3(6.4f, 4, 0.4f), wallMat);
        PrototypeSetup.Block(room, "Wall East", new Vector3(11, 2, 0), new Vector3(0.4f, 4, 9.4f), wallMat);
        PrototypeSetup.Block(room, "Wall West Left", new Vector3(5, 2, -3), new Vector3(0.4f, 4, 3), wallMat);
        PrototypeSetup.Block(room, "Wall West Right", new Vector3(5, 2, 3), new Vector3(0.4f, 4, 3), wallMat);
        PrototypeSetup.Block(room, "Lintel", new Vector3(5, 3.25f, 0), new Vector3(0.4f, 1.5f, 3), wallMat);

        // Door: 3m wide gap, slides +Z into the right wall segment.
        var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
        door.name = "Door";
        door.transform.SetParent(room);
        door.transform.position = new Vector3(5, 1.25f, 0);
        door.transform.localScale = new Vector3(0.3f, 2.5f, 3f);
        door.GetComponent<Renderer>().sharedMaterial = doorMat;
        door.AddComponent<NetworkObject>();
        door.AddComponent<NetworkTransform>(); // default AuthorityMode = Server
        var sliding = door.AddComponent<SlidingDoor>();
        sliding.openOffset = new Vector3(0, 0, 3f);

        // Switch on the outside face of the west wall (face at x = 4.8).
        var sw = GameObject.CreatePrimitive(PrimitiveType.Cube);
        sw.name = "Door Switch";
        sw.transform.SetParent(room);
        sw.transform.position = new Vector3(4.7f, 1.3f, -2.25f);
        sw.transform.localScale = new Vector3(0.2f, 0.3f, 0.3f);
        sw.GetComponent<Renderer>().sharedMaterial = switchMat;
        sw.AddComponent<NetworkObject>();
        sw.AddComponent<DoorSwitch>().door = sliding;
    }
}
