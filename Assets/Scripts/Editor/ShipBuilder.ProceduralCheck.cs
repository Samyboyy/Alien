using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The unified project validator's section for the generated ship: when Assets/Scenes/ProceduralShipTest.unity exists it is opened additively
/// (and closed again), validated through the same PhysicalShipValidator Alien > Procedural Ship > Validate Procedural Ship uses, read-only, and
/// scoped to its own scene so the Ship scene next to it does not confuse it. Physics and NavMesh checks need the scene alone, so they are left to
/// the procedural menu (which says so).
/// </summary>
public static partial class ShipBuilder
{
    const string ProceduralScenePath = "Assets/Scenes/ProceduralShipTest.unity";

    static void CheckProceduralShip()
    {
        if (!File.Exists(ProceduralScenePath)) { Debug.Log("Procedural ship: no ProceduralShipTest scene yet (Alien > Procedural Ship > Build Procedural Ship Test Scene); nothing to check."); return; }
        var scene = SceneManager.GetSceneByPath(ProceduralScenePath);
        bool openedHere = !scene.isLoaded;
        if (openedHere) scene = EditorSceneManager.OpenScene(ProceduralScenePath, OpenSceneMode.Additive);
        try
        {
            var ship = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<GeneratedShip>(true)).FirstOrDefault();
            if (ship == null) { Warn("ProceduralShipTest has no GeneratedShip: rebuild it."); return; }
            SceneScope.Current = scene;
            var lib = AssetDatabase.LoadAssetAtPath<GreyboxLibrary>(GreyboxLibraryBuilder.LibraryPath);
            var report = PhysicalShipValidator.Validate(ship, lib, true, record: false);
            foreach (var e in report.errors) Err($"Procedural ship: {e}");
            foreach (var w in report.warnings) Warn($"Procedural ship: {w}");
            foreach (var i in report.info) Debug.Log($"Procedural ship: {i}");
            if (!SceneScope.PhysicsSafe) Debug.Log("Procedural ship: physics, spawn-clearance and NavMesh checks were skipped (another scene is loaded); run Alien > Procedural Ship > Validate Procedural Ship with that scene open alone.");
        }
        finally
        {
            SceneScope.Current = default;
            if (openedHere) EditorSceneManager.CloseScene(scene, true);
        }
    }
}
