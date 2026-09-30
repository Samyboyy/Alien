using System.IO;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Alien > Add Round System. Adds PlayerLife + LocalSpectator to the Player prefab and a Round Manager
/// object to NetTest. Rerun-safe: only missing components/objects are added; existing ones are left alone.
/// </summary>
public static class RoundSetup
{
    const string ScenePath = "Assets/Scenes/NetTest.unity";
    const string PrefabPath = "Assets/Prefabs/Player.prefab";

    [MenuItem("Alien/Add Round System")]
    static void Setup()
    {
        if (!File.Exists(ScenePath) || !File.Exists(PrefabPath)) { Debug.LogError("Run Alien > Setup Multiplayer Prototype first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        AddToPlayer();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        if (Object.FindFirstObjectByType<RoundManager>() == null) AddRoundManager();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("Round system ready in NetTest.");
    }

    // RoundManager finds doors, creature, items and pods itself (IRoundResettable), so nothing else to wire.
    internal static void AddRoundManager()
    {
        var go = new GameObject("Round Manager");
        go.AddComponent<NetworkObject>();
        go.AddComponent<RoundManager>();
    }

    static void AddToPlayer()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            bool changed = false;
            if (root.GetComponent<PlayerLife>() == null) { root.AddComponent<PlayerLife>(); changed = true; }
            if (root.GetComponent<LocalSpectator>() == null) { root.AddComponent<LocalSpectator>(); changed = true; }
            if (changed) PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
