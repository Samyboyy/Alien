using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// The enclosed greybox ship: a ceiling, a restrained realtime lighting pass with emissive fixtures and a few failing lights, an
/// interior Volume (exposure, indirect light, haze) and the acoustic zones. Built from the same ASCII map as the rest of the ship by a
/// fresh Build Ship Scene and by Alien > Add Ship Atmosphere, which only adds what is missing and never rebuilds the scene.
///
/// Decisions:
///  - Ceiling: ONE slab over the whole hull at the wall tops (no gaps). It keeps its collider, so world sounds are occluded through it
///    (the creature in the overhead ducts) and spectator cameras stay inside, but a NavMeshModifier excludes it from every NavMesh bake.
///    AI sight and hiding rays run below 2 m, so it never touches them. Nothing changes the NavMesh: no rebake.
///  - Lights: downward spot lights near the ceiling, a handful per room by floor area and every ~10 m along the corridors, with cached
///    (render-once) low-resolution shadows so light does not leak through the 2 m walls at little per-frame cost. Colour identity per
///    room, warmer in Engine, a few red emergency accents. Four failing lights flicker (LightFlicker, master switch on ShipAtmosphere).
///  - Volume: its own profile asset (never edits the shared sky profile): auto exposure tuned for dark interiors, reduced sky ambient
///    and reflections (the roof would otherwise let flat sky light flood every room), and a light constant-colour haze.
/// </summary>
public static partial class ShipBuilder
{
    const string CeilingRoot = "Ship Ceiling", LightsRoot = "Ship Lights", AtmosphereRoot = "Ship Atmosphere", AcousticRoot = "Acoustic Zones";
    const string AtmosphereProfilePath = "Assets/Settings/ShipAtmosphereProfile.asset";
    const float LightHeight = 3.75f;
    const int RecommendedLights = 48;

    /// <summary>Adds the ceiling, lights, atmosphere Volume and acoustic zones if missing. Returns how many groups were added.</summary>
    internal static int AddShipAtmosphere()
    {
        int added = 0;
        if (GameObject.Find(CeilingRoot) == null) { BuildCeiling(); added++; } else Debug.Log($"'{CeilingRoot}' already in the scene - left as it is.");
        if (GameObject.Find(LightsRoot) == null) { BuildShipLights(); added++; } else Debug.Log($"'{LightsRoot}' already in the scene - left as it is.");
        if (GameObject.Find(AtmosphereRoot) == null) { BuildAtmosphereVolume(); added++; } else Debug.Log($"'{AtmosphereRoot}' already in the scene - left as it is.");
        if (GameObject.Find(AcousticRoot) == null) { BuildAcousticZones(); added++; } else Debug.Log($"'{AcousticRoot}' already in the scene - left as it is.");
        return added;
    }

    // ---------- Ceiling ----------

    static void BuildCeiling()
    {
        var mat = PrototypeSetup.Mat("Ceiling", new Color(0.3f, 0.3f, 0.32f));
        var root = new GameObject(CeilingRoot);
        root.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // never part of a NavMesh bake (applies to its children)
        PrototypeSetup.Block(root.transform, "Ceiling Slab", new Vector3(0f, WallHeight + 0.1f, 0f), new Vector3(W * Cell, 0.2f, H * Cell), mat);
        Debug.Log("Ship ceiling added: one slab over the whole hull at the wall tops (collider kept for sound and cameras, excluded from the NavMesh).");
    }

    // ---------- Lights ----------

    static (Color color, float lumens, string fixture) RoomLightStyle(string room) => room switch
    {
        "Engine" => (new Color(1f, 0.74f, 0.48f), 2600f, "Warm"),         // machinery: warmer and brighter
        "Security" => (new Color(0.62f, 0.95f, 0.92f), 1500f, "Teal"),
        "Medbay" => (new Color(0.82f, 1f, 0.86f), 1700f, "Green"),
        "PodA" or "PodB" => (new Color(1f, 0.84f, 0.6f), 1300f, "Amber"),
        "Bridge" => (new Color(0.66f, 0.8f, 1f), 1500f, "Cool"),
        "Cargo" or "Storage" => (new Color(0.85f, 0.9f, 1f), 2100f, "Cool"),
        _ => (new Color(0.8f, 0.88f, 1f), 1600f, "Cool"),
    };

    static readonly Color CorridorColor = new(0.72f, 0.83f, 1f), EmergencyRed = new(1f, 0.12f, 0.08f);

    // Red accents where they help: beside the power door to Pod A, the Security keycard door, and the crawlspace hatch in Cargo.
    static readonly (int c, int r)[] EmergencyCells = { (5, 4), (27, 4), (2, 10) };

    static void BuildShipLights()
    {
        var root = new GameObject(LightsRoot).transform;
        int count = 0, flickering = 0;
        foreach (var (name, c0, r0, c1, r1) in Rooms)
        {
            var (color, lumens, fixture) = RoomLightStyle(name);
            Vector3 a = World(c0, r0), b = World(c1, r1);
            float minX = a.x - Cell * 0.5f, maxX = b.x + Cell * 0.5f, maxZ = a.z + Cell * 0.5f, minZ = b.z - Cell * 0.5f;
            // About one light per 80 m2 (at most four), laid out along the room's long side; the pod rooms get one.
            float width = maxX - minX, depth = maxZ - minZ;
            int lightsHere = name is "PodA" or "PodB" ? 1 : Mathf.Clamp(Mathf.RoundToInt(width * depth / 80f), 1, 4);
            int nx = lightsHere == 4 ? 2 : width >= depth ? lightsHere : 1, nz = lightsHere == 4 ? 2 : width >= depth ? 1 : lightsHere;
            int k = 0;
            for (int iz = 0; iz < nz; iz++)
                for (int ix = 0; ix < nx; ix++, k++)
                {
                    var pos = new Vector3(Mathf.Lerp(minX, maxX, (ix + 0.5f) / nx), LightHeight, Mathf.Lerp(maxZ, minZ, (iz + 0.5f) / nz));
                    var (light, glow) = CeilingLight(root, $"Light {name} {k}", pos, color, lumens, 9.5f, true, fixture);
                    count++;
                    if ((name == "Medbay" && k == 1) || (name == "Storage" && k == 0)) { Flicker(light, glow, 1000 + count); flickering++; }
                }
        }

        // Corridors: every 10 m along each straight run of corridor cells (they have no room volume).
        int corridorIndex = 0;
        foreach (var run in CorridorRuns(4))
        {
            int cells = run.length, lights = Mathf.Max(1, Mathf.RoundToInt(cells * Cell / 10f));
            for (int i = 0; i < lights; i++)
            {
                float t = (i + 0.5f) / lights * cells - 0.5f;
                Vector3 pos = run.horizontal ? World(run.c, run.r) + Vector3.right * (t * Cell) : World(run.c, run.r) - Vector3.forward * (t * Cell);
                pos.y = LightHeight;
                var (light, glow) = CeilingLight(root, $"Light Corridor {corridorIndex}", pos, CorridorColor, 1100f, 8f, true, "Cool");
                count++;
                if (corridorIndex == 2 || corridorIndex == 9) { Flicker(light, glow, 2000 + corridorIndex); flickering++; }
                corridorIndex++;
            }
        }

        // Emergency accents: small, red, no shadows (short range).
        foreach (var (c, r) in EmergencyCells)
        {
            var pos = World(c, r, 3.2f);
            var go = new GameObject($"Emergency Light {c},{r}");
            go.transform.SetParent(root);
            go.transform.position = pos;
            var hd = go.AddComponent<HDAdditionalLightData>();
            var light = go.GetComponent<Light>();
            light.type = LightType.Point;
            light.range = 4.5f;
            light.color = EmergencyRed;
            light.lightUnit = LightUnit.Lumen;
            light.intensity = 260f;
            light.shadows = LightShadows.None;
            hd.affectsVolumetric = true;
            hd.volumetricDimmer = 0.5f;
            var bulb = RenderOnlyBox(go.transform, "Beacon", pos + Vector3.up * 0.25f, new Vector3(0.25f, 0.12f, 0.25f), FixtureMat("Red", EmergencyRed));
            bulb.shadowCastingMode = ShadowCastingMode.Off;
            count++;
        }
        Debug.Log($"Ship lights added: {count} realtime lights (recommended at most {RecommendedLights}), {flickering} flickering.");
    }

    static (Light light, Renderer fixture) CeilingLight(Transform root, string name, Vector3 pos, Color color, float lumens, float range, bool shadows, string fixtureKey)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root);
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(90f, 0f, 0f)); // pointing straight down
        var hd = go.AddComponent<HDAdditionalLightData>(); // first, so its defaults do not override the values below
        var light = go.GetComponent<Light>();
        light.type = LightType.Spot;
        light.spotAngle = 150f;
        light.innerSpotAngle = 80f;
        light.range = range;
        light.color = color;
        light.lightUnit = LightUnit.Lumen;
        light.intensity = lumens;
        light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        hd.shadowUpdateMode = ShadowUpdateMode.OnEnable; // static geometry: render the shadow map once, cheap afterwards
        hd.SetShadowResolution(256);
        hd.affectsVolumetric = true;
        hd.volumetricDimmer = 0.35f;
        var fixture = RenderOnlyBox(go.transform, "Fixture", new Vector3(pos.x, WallHeight - 0.04f, pos.z), new Vector3(1.1f, 0.06f, 0.32f), FixtureMat(fixtureKey, color));
        fixture.shadowCastingMode = ShadowCastingMode.Off;
        return (light, fixture);
    }

    static void Flicker(Light light, Renderer fixture, int seed)
    {
        var f = light.gameObject.AddComponent<LightFlicker>();
        f.seed = seed;
        f.fixture = fixture;
    }

    static Renderer RenderOnlyBox(Transform parent, string name, Vector3 center, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, true);
        go.transform.position = center;
        go.transform.localScale = size;
        Object.DestroyImmediate(go.GetComponent<Collider>());
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        return r;
    }

    // An emissive fixture material per colour; existing materials are never overwritten (hand edits survive).
    static Material FixtureMat(string key, Color color)
    {
        string path = $"Assets/Materials/Prototype/Fixture {key}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        m = new Material(Shader.Find("HDRP/Lit"));
        m.SetColor("_BaseColor", Color.Lerp(color, Color.white, 0.5f));
        HDMaterial.SetUseEmissiveIntensity(m, true);
        HDMaterial.SetEmissiveColor(m, color);
        HDMaterial.SetEmissiveIntensity(m, 60f, EmissiveIntensityUnit.Nits);
        HDMaterial.ValidateMaterial(m);
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    // Straight runs of corridor cells (walkable, outside every room, not a door or the crawlspace), horizontal then vertical.
    static List<(int c, int r, int length, bool horizontal)> CorridorRuns(int minLength)
    {
        bool Corridor(int c, int r) => "#LDPKxFGab".IndexOf(At(c, r)) < 0 && RoomNameAt(c, r) == null && c > 0 && r > 0 && c < W - 1 && r < H - 1;
        var runs = new List<(int, int, int, bool)>();
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
            {
                if (!Corridor(c, r)) continue;
                int end = c;
                while (Corridor(end + 1, r)) end++;
                if (end - c + 1 >= minLength) runs.Add((c, r, end - c + 1, true));
                c = end;
            }
        for (int c = 0; c < W; c++)
            for (int r = 0; r < H; r++)
            {
                if (!Corridor(c, r)) continue;
                int end = r;
                while (Corridor(c, end + 1)) end++;
                if (end - r + 1 >= minLength) runs.Add((c, r, end - r + 1, false));
                r = end;
            }
        return runs;
    }

    // ---------- Volume ----------

    static void BuildAtmosphereVolume()
    {
        var root = new GameObject(AtmosphereRoot);
        root.AddComponent<ShipAtmosphere>();
        var volume = root.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 10f; // above the sky and fog Volume; only the overrides below change
        volume.sharedProfile = AtmosphereProfile();
        Debug.Log($"Ship atmosphere Volume added ({AtmosphereProfilePath}).");
    }

    static VolumeProfile AtmosphereProfile()
    {
        var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AtmosphereProfilePath);
        if (p != null) return p; // never overwrite a profile that may have been tuned
        p = ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(p, AtmosphereProfilePath);

        var exposure = p.Add<Exposure>();
        exposure.mode.Override(ExposureMode.AutomaticHistogram);
        exposure.limitMin.Override(0f);
        exposure.limitMax.Override(10f);
        exposure.compensation.Override(-0.5f); // a little darker than neutral: it is a horror ship
        exposure.adaptationSpeedDarkToLight.Override(2f);
        exposure.adaptationSpeedLightToDark.Override(1.2f);

        var indirect = p.Add<IndirectLightingController>();
        indirect.indirectDiffuseLightingMultiplier.Override(0.3f); // the sky would otherwise light every enclosed room evenly
        indirect.reflectionLightingMultiplier.Override(0.4f);

        var fog = p.Add<Fog>();
        fog.enabled.Override(true);
        fog.colorMode.Override(FogColorMode.ConstantColor);
        fog.color.Override(new Color(0.03f, 0.035f, 0.04f));
        fog.meanFreePath.Override(70f);
        fog.baseHeight.Override(0f);
        fog.maximumHeight.Override(6f);
        fog.enableVolumetricFog.Override(true);
        fog.albedo.Override(new Color(0.8f, 0.85f, 0.9f));

        foreach (var component in p.components)
        {
            component.name = component.GetType().Name;
            component.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(component, p);
        }
        EditorUtility.SetDirty(p);
        AssetDatabase.SaveAssets();
        return p;
    }

    // ---------- Acoustic zones ----------

    static AcousticSpace RoomSpace(string room) => room is "Engine" or "Cargo" or "Storage" ? AcousticSpace.LargeMachinery : AcousticSpace.SmallRoom;

    static void BuildAcousticZones()
    {
        var root = new GameObject(AcousticRoot).transform;
        int zones = 0;
        foreach (var (name, c0, r0, c1, r1) in Rooms) { Zone(root, $"Acoustic {name}", c0, r0, c1, r1, 0f, 4.5f, RoomSpace(name), 1); zones++; }
        foreach (var (cells, label) in new[] { ("Aa", "Pod A Interior"), ("Bb", "Pod B Interior") })
            if (CellBounds(cells, out int c0, out int r0, out int c1, out int r1)) { Zone(root, $"Acoustic {label}", c0, r0, c1, r1, 0f, 4.5f, AcousticSpace.Compartment, 2); zones++; }
        if (CellBounds("L", out int lc0, out int lr0, out int lc1, out int lr1)) { Zone(root, "Acoustic Crawlspace", lc0, lr0, lc1, lr1, 0f, CrawlClearance + 0.2f, AcousticSpace.Crawlspace, 3); zones++; }
        int k = 0;
        foreach (var run in CorridorRuns(3))
        {
            int c1 = run.horizontal ? run.c + run.length - 1 : run.c, r1 = run.horizontal ? run.r : run.r + run.length - 1;
            Zone(root, $"Acoustic Corridor {k++}", run.c, run.r, c1, r1, 0f, 4.5f, AcousticSpace.Corridor, 0);
            zones++;
        }
        Debug.Log($"Acoustic zones added: {zones} (rooms, pod interiors, crawlspace, corridors). Anything outside them uses the AudioBank fallback (corridor).");
    }

    static void Zone(Transform root, string name, int c0, int r0, int c1, int r1, float bottom, float height, AcousticSpace space, int priority)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root);
        Vector3 a = World(c0, r0), b = World(c1, r1);
        go.transform.position = new Vector3((a.x + b.x) * 0.5f, bottom + height * 0.5f, (a.z + b.z) * 0.5f);
        var z = go.AddComponent<ShipAcousticZone>();
        z.space = space;
        z.priority = priority;
        z.size = new Vector3((Mathf.Abs(c1 - c0) + 1) * Cell, height, (Mathf.Abs(r1 - r0) + 1) * Cell);
    }

    static bool CellBounds(string chars, out int c0, out int r0, out int c1, out int r1)
    {
        c0 = r0 = int.MaxValue;
        c1 = r1 = int.MinValue;
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
                if (chars.IndexOf(At(c, r)) >= 0) { c0 = Mathf.Min(c0, c); r0 = Mathf.Min(r0, r); c1 = Mathf.Max(c1, c); r1 = Mathf.Max(r1, r); }
        return c1 >= c0;
    }

    // ---------- Validation ----------

    /// <summary>Checks the atmosphere and audio setup of the open Ship scene. Logs a summary; problems as errors, gaps as warnings.</summary>
    internal static void ValidateAtmosphere()
    {
        int problems = 0, warnings = 0;

        // Ceiling coverage over every walkable cell, and nothing low enough to touch players, the creature, doors or pods.
        var ceiling = GameObject.Find(CeilingRoot);
        if (ceiling == null) { problems++; Debug.LogError("No ship ceiling: run Alien > Add Ship Atmosphere."); }
        else
        {
            var renderers = ceiling.GetComponentsInChildren<Renderer>();
            int uncovered = 0;
            float lowest = float.MaxValue;
            foreach (var rr in renderers) lowest = Mathf.Min(lowest, rr.bounds.min.y);
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                {
                    if (At(c, r) == '#') continue;
                    Vector3 p = World(c, r);
                    if (!renderers.Any(rr => p.x >= rr.bounds.min.x && p.x <= rr.bounds.max.x && p.z >= rr.bounds.min.z && p.z <= rr.bounds.max.z)) uncovered++;
                }
            if (uncovered > 0) { problems++; Debug.LogError($"Ceiling: {uncovered} walkable cells have no ceiling above them."); }
            if (lowest < WallHeight - 0.05f) { problems++; Debug.LogError($"Ceiling: geometry reaches down to {lowest:0.00} m, below the {WallHeight} m wall tops."); }
            if (ceiling.GetComponent<NavMeshModifier>() == null) { warnings++; Debug.LogWarning("Ceiling: no NavMeshModifier; a future bake could put a NavMesh on the roof."); }
        }

        // Acoustic zones: every walkable cell is in a zone or uses the fallback; every space has a profile.
        var bank = AssetDatabase.LoadAssetAtPath<AudioBank>("Assets/Resources/AudioBank.asset");
        var zones = Object.FindObjectsByType<ShipAcousticZone>(FindObjectsSortMode.None);
        if (zones.Length == 0) { problems++; Debug.LogError("No acoustic zones: run Alien > Add Ship Atmosphere."); }
        else
        {
            int fallbackCells = 0;
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                {
                    if (At(c, r) == '#') continue;
                    Vector3 p = World(c, r, 1.2f);
                    if (!zones.Any(z => z.Contains(p))) fallbackCells++;
                }
            Debug.Log($"Acoustic zones: {zones.Length}; {fallbackCells} walkable cells (doorways, short stubs) use the fallback profile.");
            if (bank != null)
                foreach (var space in zones.Select(z => z.space).Distinct().Append(bank.fallbackSpace))
                    if (bank.Profile(space) == null) { problems++; Debug.LogError($"AudioBank has no acoustic profile for {space}."); }
        }

        // Lights: within the documented budget.
        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l.type != LightType.Directional).ToArray();
        var settings = Object.FindFirstObjectByType<ShipAtmosphere>();
        int budget = settings != null ? settings.recommendedMaxLights : RecommendedLights;
        if (lights.Length > budget) { warnings++; Debug.LogWarning($"Lights: {lights.Length} realtime lights, above the recommended {budget} for a four-player HDRP game."); }

        // Vents: unique ids that match their node index, and something audible at each exit before emerging.
        var net = Object.FindFirstObjectByType<CreatureVentNetwork>();
        if (net != null)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < net.entrances.Length; i++)
            {
                var e = net.entrances[i];
                if (e == null) { problems++; Debug.LogError($"Vent network: entrance slot {i} is empty."); continue; }
                if (!seen.Add(e.id)) { problems++; Debug.LogError($"Vent network: duplicate entrance id {e.id} ({e.name}).", e); }
                if (e.id != i) { problems++; Debug.LogError($"Vent network: {e.name} has id {e.id} but is node {i}; ids must match their position.", e); }
            }
            if (bank != null && bank.ventWarning == null && bank.doorSound == null) { problems++; Debug.LogError("Vent exits have no warning sound: neither a ventWarning clip nor the door clip (its fallback) is set."); }
            else if (bank != null && bank.ventWarning == null) Debug.Log("Vent warning uses the door-clip fallback (no dedicated clip yet).");
        }

        // One AudioListener per local player (the prefab), and at most one other (the lobby camera) in the scene.
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        int prefabListeners = prefab != null ? prefab.GetComponentsInChildren<AudioListener>(true).Length : 0;
        if (prefabListeners != 1) { problems++; Debug.LogError($"Player prefab has {prefabListeners} AudioListeners; it needs exactly one."); }
        int sceneListeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        if (sceneListeners > 1) { warnings++; Debug.LogWarning($"The scene has {sceneListeners} AudioListeners; only the lobby camera should have one."); }

        // Clips: warnings only (audio components skip missing clips at runtime).
        if (bank == null) { warnings++; Debug.LogWarning("No AudioBank: run Alien > Setup Audio."); }
        else
        {
            var missing = new List<string>();
            if (bank.monsterFootsteps == null || bank.monsterFootsteps.Length == 0) missing.Add("monster footsteps");
            if (bank.doorSound == null) missing.Add("door");
            if (bank.snarl == null) missing.Add("snarl");
            if (bank.atmos == null) missing.Add("atmos");
            if (bank.tensionRiser == null) missing.Add("tension riser");
            if (bank.heartbeat == null) missing.Add("heartbeat");
            if (bank.ventEnter == null) missing.Add("vent enter (door fallback used)");
            if (bank.ventTravel == null) missing.Add("vent travel (silent)");
            if (bank.ventWarning == null) missing.Add("vent warning (door fallback used)");
            if (bank.ventExit == null) missing.Add("vent exit (door fallback used)");
            if (missing.Count > 0) { warnings++; Debug.LogWarning($"Audio clips not set: {string.Join(", ", missing)}."); }
        }

        Debug.Log($"Atmosphere validation: {lights.Length} realtime lights (budget {budget}), {problems} problems, {warnings} warnings.");
    }
}

/// <summary>Menu: Alien > Add Ship Atmosphere. Updates the existing Ship scene; nothing is rebuilt and no NavMesh rebake is needed.</summary>
public static class AtmosphereSetup
{
    const string ScenePath = "Assets/Scenes/Ship.unity";

    [MenuItem("Alien/Add Ship Atmosphere")]
    static void Run()
    {
        if (!File.Exists(ScenePath)) { Debug.LogError("Ship scene missing. Run Alien > Build Ship Scene first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int added = ShipBuilder.AddShipAtmosphere();
        ShipBuilder.ValidateAtmosphere();
        if (added == 0) return; // nothing changed: leave the scene untouched
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"Ship atmosphere: {added} group(s) added and Ship saved. No NavMesh rebake is needed (the ceiling is excluded from it).");
    }
}
