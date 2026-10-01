using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Menu: Alien > Procedural Ship > Build Greybox Library. Turns the pure room blueprints (GreyboxBlueprints) into placeholder prefabs made of
/// Unity primitives and the project's prototype materials, plus the corridor modules and the wall seal, and registers them in a GreyboxLibrary asset.
/// Rerun-safe: an existing prefab is never touched (hand edits survive); only missing prefabs are created and missing library entries added.
/// Menu: Alien > Procedural Ship > Rebuild Greybox Library (replace prefabs) recreates every prefab after a confirmation.
/// Nothing here touches a scene.
/// </summary>
public static class GreyboxLibraryBuilder
{
    internal const string Root = "Assets/Procedural/Greybox", RoomsDir = Root + "/Rooms", ModulesDir = Root + "/Modules", LibraryPath = Root + "/GreyboxLibrary.asset";
    const float Cell = GreyboxScale.Cell, Wall = GreyboxScale.WallHeight, Thick = GreyboxScale.WallThickness;

    [MenuItem("Alien/Procedural Ship/Build Greybox Library")]
    static void Build() => ReportRun(Run(false));

    [MenuItem("Alien/Procedural Ship/Rebuild Greybox Library (replace prefabs)")]
    static void Rebuild()
    {
        if (!EditorUtility.DisplayDialog("Rebuild Greybox Library",
                $"Recreate every prefab under {Root} from the blueprints?\nManual edits to those prefabs will be lost. Scenes already generated keep their own copies of the rooms.", "Rebuild", "Cancel"))
            return;
        ReportRun(Run(true));
    }

    [MenuItem("Alien/Procedural Ship/Validate Greybox Library")]
    static void Validate()
    {
        var lib = AssetDatabase.LoadAssetAtPath<GreyboxLibrary>(LibraryPath);
        if (lib == null) { Debug.LogError("No Greybox Library yet: run Alien > Procedural Ship > Build Greybox Library."); return; }
        var problems = lib.Problems(DefaultRoomCatalogue.Create());
        foreach (var p in problems) Debug.LogError($"Greybox library: {p}");
        Debug.Log(problems.Count == 0 ? $"Greybox library OK: {lib.rooms.Count} room prefabs, every definition covered, every room has its sockets, anchors and bounds." : $"Greybox library: {problems.Count} problem(s).");
    }

    static void ReportRun(int changes) =>
        Debug.Log(changes == 0 ? "Greybox library: everything was already in place; nothing changed." : $"Greybox library: {changes} change(s) made and saved.");

    internal static int Run(bool replace)
    {
        int changes = 0;
        EnsureFolder("Assets", "Procedural");
        EnsureFolder("Assets/Procedural", "Greybox");
        EnsureFolder(Root, "Rooms");
        EnsureFolder(Root, "Modules");
        EnsureFolder("Assets", "Materials");
        EnsureFolder("Assets/Materials", "Prototype");

        foreach (var bp in GreyboxBlueprints.Create())
        {
            string path = $"{RoomsDir}/{bp.variantId}.prefab";
            if (!replace && File.Exists(path)) continue;
            var go = BuildRoom(bp);
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            changes++;
        }

        foreach (CorridorPieceKind kind in System.Enum.GetValues(typeof(CorridorPieceKind)))
        {
            string path = $"{ModulesDir}/Corridor {kind}.prefab";
            if (!replace && File.Exists(path)) continue;
            var go = BuildCorridor(kind);
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            changes++;
        }
        string sealPath = $"{ModulesDir}/Wall Seal.prefab";
        if (replace || !File.Exists(sealPath))
        {
            var go = BuildSeal();
            PrefabUtility.SaveAsPrefabAsset(go, sealPath);
            Object.DestroyImmediate(go);
            changes++;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // The library asset: created if missing, otherwise only gaps are filled (entries a designer swapped stay).
        var lib = AssetDatabase.LoadAssetAtPath<GreyboxLibrary>(LibraryPath);
        bool created = lib == null;
        if (created) lib = ScriptableObject.CreateInstance<GreyboxLibrary>();
        bool dirty = created;
        dirty |= lib.rooms.RemoveAll(r => r == null) > 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoomsDir }).OrderBy(g => AssetDatabase.GUIDToAssetPath(g), System.StringComparer.Ordinal))
        {
            var room = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid))?.GetComponent<ShipRoomPrefab>();
            if (room == null || lib.rooms.Any(r => r.variantId == room.variantId)) continue;
            lib.rooms.Add(room);
            dirty = true;
        }
        dirty |= Fill(ref lib.corridorStraight, "Corridor Straight") | Fill(ref lib.corridorCorner, "Corridor Corner") | Fill(ref lib.corridorTee, "Corridor Tee")
                 | Fill(ref lib.corridorCross, "Corridor Cross") | Fill(ref lib.corridorDeadEnd, "Corridor DeadEnd") | Fill(ref lib.wallSeal, "Wall Seal");
        if (replace)
        {
            // Rebuilt prefabs keep their GUIDs, so the references stay valid; nothing to rewrite.
        }
        if (created) AssetDatabase.CreateAsset(lib, LibraryPath);
        if (dirty) { EditorUtility.SetDirty(lib); changes++; }
        AssetDatabase.SaveAssets();

        var problems = lib.Problems(DefaultRoomCatalogue.Create());
        foreach (var p in problems) Debug.LogWarning($"Greybox library: {p}");
        return changes;
    }

    static bool Fill(ref GameObject slot, string name)
    {
        if (slot != null) return false;
        slot = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModulesDir}/{name}.prefab");
        return slot != null;
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder($"{parent}/{name}")) AssetDatabase.CreateFolder(parent, name);
    }

    // ---------- Materials (the project's prototype materials; new ones are created only if missing) ----------

    /// <summary>Tests set this to get in-memory materials instead of creating material assets.</summary>
    internal static System.Func<GreyMaterial, Material> MaterialProvider;

    internal static Material Mat(GreyMaterial m) => MaterialProvider != null ? MaterialProvider(m) : m switch
    {
        GreyMaterial.Wall => PrototypeSetup.Mat("Wall", new Color(0.6f, 0.6f, 0.62f)),
        GreyMaterial.Floor => PrototypeSetup.Mat("Floor", new Color(0.35f, 0.35f, 0.38f)),
        GreyMaterial.Ceiling => PrototypeSetup.Mat("Ceiling", new Color(0.3f, 0.3f, 0.32f)),
        GreyMaterial.Table => PrototypeSetup.Mat("Table", new Color(0.45f, 0.35f, 0.25f)),
        GreyMaterial.Console => PrototypeSetup.Mat("Console", new Color(0.2f, 0.7f, 0.7f)),
        GreyMaterial.Crate => PrototypeSetup.Mat("Obstacle", new Color(0.7f, 0.45f, 0.2f)),
        GreyMaterial.Machinery => PrototypeSetup.Mat("Machinery", new Color(0.32f, 0.36f, 0.42f)),
        GreyMaterial.Bed => PrototypeSetup.Mat("Bed", new Color(0.55f, 0.6f, 0.75f)),
        GreyMaterial.Locker => PrototypeSetup.Mat("Locker", new Color(0.35f, 0.42f, 0.35f)),
        GreyMaterial.Shelf => PrototypeSetup.Mat("Shelf", new Color(0.5f, 0.4f, 0.3f)),
        GreyMaterial.Pod => PrototypeSetup.Mat("PodShell", new Color(0.8f, 0.8f, 0.85f)),
        _ => PrototypeSetup.Mat("HazardStripe", new Color(0.85f, 0.7f, 0.1f)),
    };

    static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, GreyMaterial material, bool collider = true)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = Mat(material);
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
        return go;
    }

    static Transform Group(Transform parent, string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    // The ceiling is never part of a NavMesh bake (the creature and players walk on the floor), but keeps its collider for sound and cameras.
    static void Ceiling(Transform parent, Vector3 center, Vector3 size)
    {
        var c = Box(parent, "Ceiling", center, size, GreyMaterial.Ceiling);
        c.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
    }

    static void AddLight(Transform parent, Vector3 pos, float lumens, float range)
    {
        var go = new GameObject("Light");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var hd = go.AddComponent<HDAdditionalLightData>(); // first, so its defaults do not override the values below
        var light = go.GetComponent<Light>();
        light.type = LightType.Spot;
        light.spotAngle = 150f;
        light.innerSpotAngle = 80f;
        light.range = range;
        light.color = new Color(0.8f, 0.88f, 1f);
        light.lightUnit = LightUnit.Lumen;
        light.intensity = lumens;
        light.shadows = LightShadows.None;
        hd.affectsVolumetric = false;
    }

    // ---------- Rooms ----------

    internal static GameObject BuildRoom(RoomBlueprint bp)
    {
        float W = bp.Width, D = bp.Depth;
        var root = new GameObject(bp.variantId);
        var geo = Group(root.transform, "Geometry");
        var anchorsGroup = Group(root.transform, "Anchors");
        var socketsGroup = Group(root.transform, "Sockets");
        var lights = Group(root.transform, "Lights");

        Box(geo, "Floor", new Vector3(W * 0.5f, -GreyboxScale.FloorThickness * 0.5f, D * 0.5f), new Vector3(W, GreyboxScale.FloorThickness, D), GreyMaterial.Floor);
        Ceiling(geo, new Vector3(W * 0.5f, Wall + 0.1f, D * 0.5f), new Vector3(W, 0.2f, D));
        BuildWalls(bp, geo);
        var colliders = new Dictionary<BoxSpec, Collider>();
        foreach (var b in bp.boxes)
            colliders[b] = Box(geo, b.name, new Vector3(b.x0 + b.width * 0.5f, b.y0 + b.height * 0.5f, b.z0 + b.depth * 0.5f), new Vector3(b.width, b.height, b.depth), b.material).GetComponent<Collider>();

        var sockets = new List<ConnectionSocket>();
        foreach (var s in bp.sockets)
        {
            RoomBlueprint.SocketPoint(s, out float x, out float z);
            var go = new GameObject($"Socket {s.id}");
            go.transform.SetParent(socketsGroup, false);
            go.transform.localPosition = new Vector3(x, 0f, z);
            go.transform.localRotation = Quaternion.Euler(0f, RoomBlueprint.SideYaw(s.side), 0f);
            var cs = go.AddComponent<ConnectionSocket>();
            cs.socketId = s.id;
            cs.cell = new Vector2Int(s.cell.x, s.cell.z);
            cs.side = s.side;
            cs.width = s.width;
            cs.height = s.height;
            cs.connectionType = s.type;
            cs.doorPolicy = s.door;
            cs.support = s.support;
            sockets.Add(cs);
        }

        var anchors = new List<RoomAnchor>();
        var kindGroups = new Dictionary<AnchorKind, Transform>();
        foreach (var a in bp.anchors)
        {
            if (!kindGroups.TryGetValue(a.kind, out var grp)) kindGroups[a.kind] = grp = Group(anchorsGroup, a.kind.ToString());
            var go = new GameObject($"{a.kind} {a.id}");
            go.transform.SetParent(grp, false);
            go.transform.localPosition = new Vector3(a.x, a.y, a.z);
            go.transform.localRotation = Quaternion.Euler(0f, a.yaw, 0f);
            var ra = go.AddComponent<RoomAnchor>();
            ra.kind = a.kind;
            ra.itemCategory = a.item;
            ra.anchorId = a.id;
            ra.socketId = a.socketId ?? "";
            ra.radius = a.kind == AnchorKind.PlayerSpawn ? 0.6f : 0.4f;
            ra.semantic = a.semantic;
            ra.context = a.context;
            ra.allowedItems = a.allowed;
            ra.forbiddenItems = a.forbidden;
            ra.weight = a.weight;
            ra.clearance = new Vector3(a.clearWidth, a.clearHeight, a.clearDepth);
            ra.requiredFeature = a.requiredFeature;
            ra.canHoldCritical = a.canHoldCritical;
            ra.narrativeTags = a.tags;
            if (a.support != null && colliders.TryGetValue(a.support, out var supportCollider)) ra.support = supportCollider;
            anchors.Add(ra);
            if (a.kind == AnchorKind.VentEntrance)
                Box(geo, "Vent Grille", new Vector3(a.x - 0.05f, a.y, a.z), new Vector3(0.1f, 0.5f, 0.7f), GreyMaterial.Hazard, collider: false);
        }

        int lightCount = Mathf.Max(1, Mathf.RoundToInt(W * D / 70f));
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(lightCount * W / D)));
        int rows = Mathf.Max(1, Mathf.CeilToInt((float)lightCount / cols));
        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
                AddLight(lights, new Vector3(W * (i + 0.5f) / cols, Wall - 0.3f, D * (j + 0.5f) / rows), 1800f, Mathf.Max(W / cols, D / rows) * 0.9f);

        var boundsGo = new GameObject("Room Bounds");
        boundsGo.transform.SetParent(root.transform, false);
        var rb = boundsGo.AddComponent<RoomBounds>();
        rb.center = new Vector3(W * 0.5f, Wall * 0.5f, D * 0.5f);
        rb.size = new Vector3(W, Wall, D);

        var room = root.AddComponent<ShipRoomPrefab>();
        room.variantId = bp.variantId;
        room.definitionId = bp.definitionId;
        room.category = bp.category;
        room.footprint = new Vector2Int(bp.sizeX, bp.sizeZ);
        room.cellSize = Cell;
        room.height = Wall;
        room.acoustics = bp.acoustics;
        room.bounds = rb;
        room.sockets = sockets.ToArray();
        room.anchors = anchors.ToArray();
        return root;
    }

    // Perimeter walls inside the footprint, in runs between the doorway cells; each doorway gets a lintel over a clear opening.
    static void BuildWalls(RoomBlueprint bp, Transform geo)
    {
        float W = bp.Width, D = bp.Depth;
        foreach (GridSide side in System.Enum.GetValues(typeof(GridSide)))
        {
            bool alongX = side == GridSide.North || side == GridSide.South;
            int cells = alongX ? bp.sizeX : bp.sizeZ;
            var doors = new HashSet<int>(bp.sockets.Where(s => s.side == side).Select(s => alongX ? s.cell.x : s.cell.z));
            int i = 0;
            while (i < cells)
            {
                if (doors.Contains(i))
                {
                    WallRun(bp, geo, side, i, i + 1, true);
                    i++;
                    continue;
                }
                int j = i;
                while (j + 1 < cells && !doors.Contains(j + 1)) j++;
                WallRun(bp, geo, side, i, j + 1, false);
                i = j + 1;
            }
        }
    }

    // A wall (or, over a doorway, a lintel) along one side from cell index a to b (exclusive).
    static void WallRun(RoomBlueprint bp, Transform geo, GridSide side, int a, int b, bool lintel)
    {
        float W = bp.Width, D = bp.Depth;
        bool alongX = side == GridSide.North || side == GridSide.South;
        float lo = a * Cell, hi = b * Cell;
        float height = lintel ? Wall - GreyboxScale.DoorHeight : Wall, y = lintel ? GreyboxScale.DoorHeight + height * 0.5f : height * 0.5f;
        if (!alongX) { lo = Mathf.Max(lo, Thick); hi = Mathf.Min(hi, D - Thick); } // the north and south walls own the corners
        if (hi <= lo) return;
        Vector3 center, size;
        switch (side)
        {
            case GridSide.North: center = new Vector3((lo + hi) * 0.5f, y, D - Thick * 0.5f); size = new Vector3(hi - lo, height, Thick); break;
            case GridSide.South: center = new Vector3((lo + hi) * 0.5f, y, Thick * 0.5f); size = new Vector3(hi - lo, height, Thick); break;
            case GridSide.East: center = new Vector3(W - Thick * 0.5f, y, (lo + hi) * 0.5f); size = new Vector3(Thick, height, hi - lo); break;
            default: center = new Vector3(Thick * 0.5f, y, (lo + hi) * 0.5f); size = new Vector3(Thick, height, hi - lo); break;
        }
        Box(geo, lintel ? $"Lintel {side} {a}" : $"Wall {side} {a}-{b - 1}", center, size, GreyMaterial.Wall);
    }

    // ---------- Corridor modules and the wall seal ----------

    internal static GameObject BuildCorridor(CorridorPieceKind kind)
    {
        var root = new GameObject($"Corridor {kind}");
        root.AddComponent<CorridorModule>().kind = kind;
        float h = Cell * 0.5f;
        Box(root.transform, "Floor", new Vector3(0f, -GreyboxScale.FloorThickness * 0.5f, 0f), new Vector3(Cell, GreyboxScale.FloorThickness, Cell), GreyMaterial.Floor);
        Ceiling(root.transform, new Vector3(0f, Wall + 0.1f, 0f), new Vector3(Cell, 0.2f, Cell));
        int open = CorridorMath.OpenMask(kind, 0);
        foreach (GridSide side in System.Enum.GetValues(typeof(GridSide)))
        {
            if ((open & Grid.Bit(side)) != 0) continue;
            var step = Grid.Step(side);
            var center = new Vector3(step.x * (h - Thick * 0.5f), Wall * 0.5f, step.z * (h - Thick * 0.5f));
            var size = step.x != 0 ? new Vector3(Thick, Wall, Cell) : new Vector3(Cell, Wall, Thick);
            Box(root.transform, $"Wall {side}", center, size, GreyMaterial.Wall);
        }
        if (kind == CorridorPieceKind.Cross)
            foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                Box(root.transform, "Post", new Vector3(sx * (h - Thick * 0.5f), Wall * 0.5f, sz * (h - Thick * 0.5f)), new Vector3(Thick, Wall, Thick), GreyMaterial.Wall);
        AddLight(root.transform, new Vector3(0f, Wall - 0.3f, 0f), 700f, 7f);
        return root;
    }

    internal static GameObject BuildSeal()
    {
        var root = new GameObject("Wall Seal");
        root.AddComponent<WallSealModule>();
        // The socket's origin is on the wall line, facing out; the wall sits inside the footprint, so towards -Z.
        Box(root.transform, "Seal Wall", new Vector3(0f, Wall * 0.5f, -Thick * 0.5f), new Vector3(Cell, Wall, Thick), GreyMaterial.Wall);
        return root;
    }
}
