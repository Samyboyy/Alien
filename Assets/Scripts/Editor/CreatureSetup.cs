using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using Unity.AI.Navigation.Editor;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Menu: Alien > Add Creature (adds NavMesh surface, test walls, patrol points, creature, door nav components)
/// and Alien > Rebake Navigation. Rerun-safe: each group is created only if its named root is missing.
/// </summary>
public static class CreatureSetup
{
    const string ScenePath = "Assets/Scenes/NetTest.unity";

    [MenuItem("Alien/Add Creature")]
    static void Setup()
    {
        if (!File.Exists(ScenePath)) { Debug.LogError("Run Alien > Setup Multiplayer Prototype first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var door = Object.FindFirstObjectByType<SlidingDoor>();
        if (door != null) AddDoorNavigation(door.gameObject);
        else Debug.LogWarning("No door in scene (run Alien > Add Door And Switch); door navigation skipped.");
        var surface = GetOrCreateSurface();
        AddTestWalls();
        var points = AddPatrolPoints();
        AddCreature(points, new Vector3(0, 0, 9));

        EditorSceneManager.SaveScene(scene);
        Bake(surface, scene);
    }

    // One-off correction for scenes built while objective doors were auto-sealed. Power/keycard requirements restrict
    // PLAYERS only; the creature forcing such a door open only moves the physical door (it sets no flag, gives no item).
    // Touches only doors operated by a console that has a requirement. Every other door, including any you sealed by
    // hand, is left as it is. creatureCanOpen itself stays an explicit per-door Inspector setting.
    [MenuItem("Alien/Allow Creature Through Objective Doors")]
    static void AllowObjectiveDoors()
    {
        var consoles = Object.FindObjectsByType<ShipConsole>(FindObjectsSortMode.None);
        int changed = 0;
        foreach (var door in Object.FindObjectsByType<SlidingDoor>(FindObjectsSortMode.None))
        {
            bool objectiveDoor = consoles.Any(c => c.door == door && (c.requiredFlags != ShipFlags.None || c.requiredItem != ItemKind.None));
            if (!objectiveDoor || door.creatureCanOpen) continue;
            Undo.RecordObject(door, "Allow creature to open objective door");
            door.creatureCanOpen = true;
            EditorUtility.SetDirty(door);
            changed++;
            Debug.Log($"'{door.name}': creature may now force it open.", door);
        }
        if (changed > 0) EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log($"Objective doors updated in '{SceneManager.GetActiveScene().name}': {changed} changed.");
    }

    // Perception / pursuit / door-timing fields that scenes may still carry old serialized values for. Applying copies the
    // CURRENT script defaults of exactly these fields onto every creature in the open scene (other fields, references and patrol
    // points are untouched). Re-running resets them again, so run it once after updating, then tune in the Inspector.
    static readonly string[] TunedCreatureFields =
    {
        "chaseSpeed", "pursueGrace", "pursueResumeStrength", "minStrength", "soundStaleSeconds", "wallPenalty", "doorPenalty",
        "obstaclePenalty", "maxPositionError", "switchMargin", "switchCooldown", "evidenceFadeSeconds", "investigateSpeed",
        "doorWindupPursuit", "doorWindupInvestigate", "doorSearchRadius", "doorProgressMemory", "minOpenFraction", "doorSettleTimeout",
        "searchSpeed", "searchRadius", "localSearchSeconds", "nearbySearchSeconds", "alertSeconds", "inspectPause", "hidingInspectSeconds",
        "inspectEyeHeight", "pointTimeout", "alertPatrolSpeedBonus",
        "recogniseSeconds", "farRecogniseFactor", "peripheralFactor", "awarenessHold", "awarenessDecaySeconds", "watchThreshold",
        "inspectReach", "inspectOpeningTolerance",
        "concealedCalmSeconds", "concealedHeightenedSeconds", "concealedSearchSeconds", "concealedInspectSeconds", "referenceCover", "heightenedAlertness",
    };

    // Only the concealed-recognition timing model. Nothing else on the creature is touched.
    static readonly string[] ConcealedFields =
    {
        "concealedCalmSeconds", "concealedHeightenedSeconds", "concealedSearchSeconds", "concealedInspectSeconds", "referenceCover", "heightenedAlertness",
    };

    /// <summary>
    /// Menu: Alien > Apply Concealed Recognition Tuning. Sets exactly these CreatureAI fields to the current script defaults in the
    /// open scene: concealedCalmSeconds (6), concealedHeightenedSeconds (4), concealedSearchSeconds (2.5), concealedInspectSeconds (0.5),
    /// referenceCover (0.65), heightenedAlertness (0.2). Each change is logged. References, furniture, hiding-spot values (cover,
    /// light) and every other Inspector edit stay as they are. Re-running resets those six again.
    /// </summary>
    [MenuItem("Alien/Apply Concealed Recognition Tuning")]
    static void ApplyConcealedTuning()
    {
        int changed = ApplyTuningToOpenScene(ConcealedFields);
        if (changed > 0) EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log($"Concealed recognition tuning applied to '{SceneManager.GetActiveScene().name}': {changed} values changed.");
    }

    [MenuItem("Alien/Apply Perception Tuning")]
    static void ApplyPerceptionTuning()
    {
        int changed = ApplyTuningToOpenScene();
        if (changed > 0) EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log($"Perception tuning applied to '{SceneManager.GetActiveScene().name}': {changed} values changed.");
    }

    // Also used by Alien > Add Survival And Search. Does not save.
    internal static int ApplyTuningToOpenScene() => ApplyTuningToOpenScene(TunedCreatureFields, true);

    static int ApplyTuningToOpenScene(string[] fields, bool includeDoors = false)
    {
        int changed = 0;
        var holder = new GameObject("tuning-defaults") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var defaults = new SerializedObject(holder.AddComponent<CreatureAI>());
            foreach (var ai in Object.FindObjectsByType<CreatureAI>(FindObjectsSortMode.None))
            {
                var so = new SerializedObject(ai);
                foreach (var field in fields)
                {
                    var src = defaults.FindProperty(field);
                    var dst = so.FindProperty(field);
                    if (src == null || dst == null) { Debug.LogWarning($"CreatureAI has no field '{field}'."); continue; }
                    if (Mathf.Approximately(src.floatValue, dst.floatValue)) continue;
                    Debug.Log($"'{ai.name}'.{field}: {dst.floatValue} -> {src.floatValue}", ai);
                    dst.floatValue = src.floatValue;
                    changed++;
                }
                so.ApplyModifiedProperties();
            }
        }
        finally { Object.DestroyImmediate(holder); }

        if (!includeDoors) return changed;

        // Door noise range: only doors still at the old default (8 m) move to the new one (10 m).
        foreach (var door in Object.FindObjectsByType<SlidingDoor>(FindObjectsSortMode.None))
        {
            var so = new SerializedObject(door);
            var p = so.FindProperty("noiseLoudness");
            if (!Mathf.Approximately(p.floatValue, 8f)) continue;
            p.floatValue = 10f;
            so.ApplyModifiedProperties();
            changed++;
            Debug.Log($"'{door.name}'.noiseLoudness: 8 -> 10", door);
        }
        return changed;
    }

    // Works on whichever scene is open (NetTest or Ship).
    [MenuItem("Alien/Rebake Navigation")]
    static void Rebake()
    {
        var surface = Object.FindFirstObjectByType<NavMeshSurface>();
        if (surface == null) { Debug.LogError("No NavMeshSurface in the open scene. Run Alien > Add Creature (NetTest) or Alien > Build Ship Scene first."); return; }
        Bake(surface, surface.gameObject.scene);
    }

    // afterBake runs once the new NavMesh is in place (still before the final save), e.g. to validate against it.
    internal static void Bake(NavMeshSurface surface, Scene scene, System.Action afterBake = null)
    {
        NavMeshAssetManager.instance.StartBakingSurfaces(new Object[] { surface });
        // The bake is async and stores the NavMesh asset on completion; save the scene once it is done.
        EditorApplication.CallbackFunction waitThenSave = null;
        waitThenSave = () =>
        {
            if (NavMeshAssetManager.instance.IsSurfaceBaking(surface)) return;
            EditorApplication.update -= waitThenSave;
            afterBake?.Invoke();
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"NavMesh baked and {scene.name} saved.");
        };
        EditorApplication.update += waitThenSave;
    }

    // The door is left out of the bake and instead carves a moving hole, so the gap only opens as the door slides.
    internal static void AddDoorNavigation(GameObject door)
    {
        if (!door.TryGetComponent<NavMeshModifier>(out var mod)) mod = door.AddComponent<NavMeshModifier>();
        mod.ignoreFromBuild = true;

        if (!door.TryGetComponent<NavMeshObstacle>(out var obs))
        {
            obs = door.AddComponent<NavMeshObstacle>();
            obs.shape = NavMeshObstacleShape.Box;
            obs.size = Vector3.one; // scaled by the door's transform
            obs.carving = true;
            obs.carveOnlyStationary = false; // keep carving while the door moves
        }
    }

    internal static NavMeshSurface GetOrCreateSurface()
    {
        var existing = Object.FindFirstObjectByType<NavMeshSurface>();
        if (existing != null) return existing;
        var go = new GameObject("NavMesh Surface");
        var s = go.AddComponent<NavMeshSurface>();
        s.collectObjects = CollectObjects.All;
        s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        s.ignoreNavMeshAgent = true; // creature is not baked in
        s.ignoreNavMeshObstacle = true; // door is handled by its carving obstacle
        return s;
    }

    static void AddTestWalls()
    {
        if (GameObject.Find("Creature Test Walls") != null) return;
        var mat = PrototypeSetup.Mat("Wall", new Color(0.6f, 0.6f, 0.62f));
        var group = new GameObject("Creature Test Walls").transform;
        PrototypeSetup.Block(group, "Cover Wall A", new Vector3(-6, 2, 0), new Vector3(0.4f, 4, 8), mat);
        PrototypeSetup.Block(group, "Cover Wall B", new Vector3(-9, 2, 5), new Vector3(6, 4, 0.4f), mat);
    }

    static Transform[] AddPatrolPoints()
    {
        Vector3[] pos = { new(-9, 0, 9), new(3, 0, 9), new(9, 0, -9), new(-4, 0, -11), new(8, 0, 0), new(-9, 0, -3) };
        var existing = GameObject.Find("Patrol Points");
        if (existing != null)
        {
            var list = new Transform[existing.transform.childCount];
            for (int i = 0; i < list.Length; i++) list[i] = existing.transform.GetChild(i);
            return list;
        }
        var root = new GameObject("Patrol Points").transform;
        var pts = new Transform[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            var p = new GameObject($"Patrol {i}").transform; // point 4 is inside the door room on purpose
            p.SetParent(root);
            p.position = pos[i];
            pts[i] = p;
        }
        return pts;
    }

    internal static void AddCreature(Transform[] points, Vector3 position)
    {
        if (GameObject.Find("Creature") != null) return;
        var bodyMat = PrototypeSetup.Mat("Creature", Color.green);
        var noseMat = PrototypeSetup.Mat("CreatureNose", Color.white);

        var root = new GameObject("Creature");
        root.transform.position = position;
        var col = root.AddComponent<CapsuleCollider>();
        col.center = new Vector3(0, 1, 0);
        col.radius = 0.4f;
        col.height = 2f;
        root.AddComponent<NetworkObject>();
        var nt = root.AddComponent<NetworkTransform>(); // server authority (default)
        nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
        nt.SyncRotAngleX = nt.SyncRotAngleZ = false;

        var agent = root.AddComponent<NavMeshAgent>();
        agent.radius = 0.4f;
        agent.height = 2f;
        agent.acceleration = 8f;
        agent.angularSpeed = 240f;
        agent.enabled = false; // the host enables it at spawn; clients never do

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0, 1, 0);
        body.transform.localScale = new Vector3(0.8f, 1, 0.8f);
        Object.DestroyImmediate(body.GetComponent<Collider>());
        body.GetComponent<Renderer>().sharedMaterial = bodyMat;

        // Nose block shows the facing direction (+Z).
        var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        nose.name = "Nose";
        nose.transform.SetParent(root.transform, false);
        nose.transform.localPosition = new Vector3(0, 1.5f, 0.5f);
        nose.transform.localScale = new Vector3(0.3f, 0.3f, 0.6f);
        Object.DestroyImmediate(nose.GetComponent<Collider>());
        nose.GetComponent<Renderer>().sharedMaterial = noseMat;

        var ai = root.AddComponent<CreatureAI>();
        ai.patrolPoints = points;
        ai.bodyRenderer = body.GetComponent<Renderer>();
    }
}
