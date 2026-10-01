using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Alien > Add Survival And Search. Adds the stamina tuning, hiding furniture, room volumes and search points to the
/// EXISTING Ship (it is not rebuilt), then rebakes the NavMesh and validates the result.
///
/// Rerun behaviour: furniture, rooms and markers are added only if their group is missing (no duplicates, hand edits kept).
/// Tuning IS reapplied every run: the listed player and creature tuning fields are reset to the current script defaults
/// (nothing else is touched), so run it once after updating and tune in the Inspector afterwards.
/// </summary>
public static class SurvivalSetup
{
    const string ScenePath = "Assets/Scenes/Ship.unity";

    // Movement and stamina tuning that an existing Player prefab still carries old serialized values for.
    static readonly string[] PlayerTunedFields =
    {
        "walkSpeed", "sprintSpeed", "crouchSpeed", "sprintSeconds", "regenDelay", "fullRecoverySeconds", "exhaustResume",
    };

    [MenuItem("Alien/Add Survival And Search")]
    static void Setup()
    {
        if (!File.Exists(ScenePath) || !File.Exists(PrototypeSetup.PrefabPath))
        {
            Debug.LogError("Ship scene or Player prefab missing. Run Alien > Setup Multiplayer Prototype and Alien > Build Ship Scene first.");
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        ShipBuilder.AddEscapeSystem(); // makes sure the prefab has every component
        TunePlayerPrefab();

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var surface = Object.FindFirstObjectByType<NavMeshSurface>();
        if (surface == null) { Debug.LogError("No NavMeshSurface in Ship. Run Alien > Build Ship Scene first."); return; }

        int added = ShipBuilder.AddHidingAndRooms();
        int tuned = CreatureSetup.ApplyTuningToOpenScene();
        Debug.Log($"Survival setup: {added} group(s) added, {tuned} creature/door tuning value(s) changed.");

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        if (added > 0)
        {
            Debug.Log("Rebaking navigation for the new furniture. Wait for 'NavMesh baked and Ship saved' and the validation summary.");
            CreatureSetup.Bake(surface, scene, ShipBuilder.ValidateWorld);
        }
        else
        {
            ShipBuilder.ValidateWorld();
            EditorSceneManager.SaveScene(scene);
        }
    }

    /// <summary>
    /// Menu: Alien > Update Hiding Volumes. Configures the concealment volume of every existing hiding place that has none, in the
    /// Ship scene. Nothing is rebuilt or rebaked; configured spots and all other objects are left untouched.
    /// </summary>
    [MenuItem("Alien/Update Hiding Volumes")]
    static void UpdateHidingVolumes()
    {
        if (!File.Exists(ScenePath)) { Debug.LogError("Ship scene missing. Run Alien > Build Ship Scene first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int done = ShipBuilder.ConfigureHidingVolumes();
        if (done == 0) return; // nothing changed: leave the scene untouched
        ShipBuilder.ValidateSurvival();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"Hiding volumes: {done} configured and Ship saved.");
    }

    /// <summary>
    /// Menu: Alien > Add Threat Visuals. Adds the ThreatVisuals component to the Player prefab if it is missing. Nothing else is
    /// touched: an existing component keeps its Inspector values, and scenes, Volume profiles and materials are not modified.
    /// </summary>
    [MenuItem("Alien/Add Threat Visuals")]
    static void AddThreatVisuals()
    {
        if (!File.Exists(PrototypeSetup.PrefabPath)) { Debug.LogError("Player prefab missing. Run Alien > Setup Multiplayer Prototype first."); return; }
        var root = PrefabUtility.LoadPrefabContents(PrototypeSetup.PrefabPath);
        try
        {
            if (root.GetComponent<ThreatVisuals>() != null) { Debug.Log("Player prefab already has ThreatVisuals - left as it is."); return; }
            root.AddComponent<ThreatVisuals>();
            PrefabUtility.SaveAsPrefabAsset(root, PrototypeSetup.PrefabPath);
            Debug.Log("Player prefab: added ThreatVisuals (defaults: strength 1, vignette 0.4, distortion 0.2).");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // Fields the threat-visual rebalance changed; the tuning menu resets exactly these.
    static readonly string[] ThreatTunedFields = { "maxVignette", "maxDistortion", "coverRelief", "chaseBonus", "facingInfluence" };

    /// <summary>
    /// Menu: Alien > Apply Threat Visuals Tuning. Sets ONLY maxVignette (0.22), maxDistortion (0.12), coverRelief (0.25),
    /// chaseBonus (0.1) and facingInfluence (0.25) on the Player prefab's ThreatVisuals to the current script defaults, logging each change. Strength, fades,
    /// distances, enable and preview settings, and everything else are left as they are.
    /// </summary>
    [MenuItem("Alien/Apply Threat Visuals Tuning")]
    static void ApplyThreatTuning()
    {
        if (!File.Exists(PrototypeSetup.PrefabPath)) { Debug.LogError("Player prefab missing. Run Alien > Setup Multiplayer Prototype first."); return; }
        var root = PrefabUtility.LoadPrefabContents(PrototypeSetup.PrefabPath);
        var holder = new GameObject("tuning-defaults") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            if (root.GetComponent<ThreatVisuals>() == null) { Debug.LogError("Player prefab has no ThreatVisuals. Run Alien > Add Threat Visuals first."); return; }
            var defaults = new SerializedObject(holder.AddComponent<ThreatVisuals>());
            var so = new SerializedObject(root.GetComponent<ThreatVisuals>());
            int changed = 0;
            foreach (var field in ThreatTunedFields)
            {
                var src = defaults.FindProperty(field);
                var dst = so.FindProperty(field);
                if (src == null || dst == null) { Debug.LogWarning($"ThreatVisuals has no field '{field}'."); continue; }
                if (Mathf.Approximately(src.floatValue, dst.floatValue)) continue;
                Debug.Log($"ThreatVisuals {field}: {dst.floatValue} -> {src.floatValue}");
                dst.floatValue = src.floatValue;
                changed++;
            }
            if (changed > 0)
            {
                so.ApplyModifiedProperties();
                PrefabUtility.SaveAsPrefabAsset(root, PrototypeSetup.PrefabPath);
            }
            Debug.Log($"Threat visuals tuning applied: {changed} values changed.");
        }
        finally
        {
            Object.DestroyImmediate(holder);
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // Copies the current script defaults of the listed fields onto the prefab's controller. Logs every change.
    static void TunePlayerPrefab()
    {
        var root = PrefabUtility.LoadPrefabContents(PrototypeSetup.PrefabPath);
        var holder = new GameObject("tuning-defaults") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var defaults = new SerializedObject(holder.AddComponent<NetworkFirstPersonController>());
            var so = new SerializedObject(root.GetComponent<NetworkFirstPersonController>());
            int changed = 0;
            foreach (var field in PlayerTunedFields)
            {
                var src = defaults.FindProperty(field);
                var dst = so.FindProperty(field);
                if (src == null || dst == null) { Debug.LogWarning($"NetworkFirstPersonController has no field '{field}'."); continue; }
                if (Mathf.Approximately(src.floatValue, dst.floatValue)) continue;
                Debug.Log($"Player prefab {field}: {dst.floatValue} -> {src.floatValue}");
                dst.floatValue = src.floatValue;
                changed++;
            }
            if (changed == 0) return;
            so.ApplyModifiedProperties();
            PrefabUtility.SaveAsPrefabAsset(root, PrototypeSetup.PrefabPath);
        }
        finally
        {
            Object.DestroyImmediate(holder);
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
