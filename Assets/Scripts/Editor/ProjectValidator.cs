using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Menu: Alien > Validate Project For Play. One read-only check of everything a play session needs: the Player prefab, the networking set-up,
/// the Ship scene and the audio bank. It COMPOSES the project's existing validators (vent network, atmosphere and audio, noisemakers,
/// lockers, furniture hiding places) and adds only what none of them covers. It never repairs, rebuilds or saves anything: it opens the Ship
/// scene additively when it is not already open (and closes it again), leaves the current scenes, selection and dirty state alone, and checks
/// afterwards that the scene was not changed. The composed validators log a great deal when everything is fine; those messages are collected
/// and only warnings, errors (with their context objects, so a click selects the object) and one summary reach the Console.
/// Repairs are separate menus, named in the messages: Alien > Add Escape System, Add Creature Ventilation, Validate And Repair Vent Network,
/// Add Throwable Noisemakers, Add Interactive Lockers, Apply Acoustic Polish, Setup Audio.
/// </summary>
public static partial class ShipBuilder
{
    const string ShipScenePath = "Assets/Scenes/Ship.unity";
    const string AudioBankAssetPath = "Assets/Resources/AudioBank.asset";

    [MenuItem("Alien/Validate Project For Play")]
    static void ValidateProjectMenu() => ValidateProjectForPlay();

    /// <summary>Runs the whole validation. Returns the number of errors (warnings are reported but do not fail it).</summary>
    internal static int ValidateProjectForPlay()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Validate Project For Play does not run in Play Mode."); return -1; }
        if (!File.Exists(ShipScenePath)) { Debug.LogError($"{ShipScenePath} is missing: run Alien > Build Ship Scene."); return 1; }

        var selection = Selection.instanceIDs;
        var ship = SceneManager.GetSceneByPath(ShipScenePath);
        bool openedHere = !ship.isLoaded;
        if (openedHere) ship = EditorSceneManager.OpenScene(ShipScenePath, OpenSceneMode.Additive);
        bool dirtyBefore = ship.isDirty;

        var collector = new ValidationLog(Debug.unityLogger.logHandler);
        Debug.unityLogger.logHandler = collector;
        var sections = new List<(string name, Action run)>
        {
            ("Player prefab", CheckPlayerPrefab),
            ("Networking", CheckNetworking),
            ("Scene: required systems and missing scripts", CheckSceneSystems),
            ("Scene: rooms, hiding places, doors, pods", CheckSceneRooms),
            ("Scene: vent network", () => ValidateVentilation()),
            ("Scene: atmosphere, audio and acoustic zones", () => ValidateAtmosphere()),
            ("Scene: furniture hiding places", () => ValidateSurvival(false)),
            ("Scene: noisemakers", () => ValidateNoisemakers()),
            ("Scene: lockers", () => ValidateLockers()),
            ("Audio bank", CheckAudioBank),
            ("Round reset", CheckRoundReset),
            ("Procedural ship and escape scenario (if built)", CheckProceduralShip),
        };
        var rows = new List<string>();
        try
        {
            foreach (var (name, run) in sections)
            {
                int e0 = collector.Errors, w0 = collector.Warnings;
                try { run(); }
                catch (Exception ex) { collector.Add(LogType.Error, $"{name}: the check itself failed: {ex.GetType().Name}: {ex.Message}", null); }
                rows.Add($"  {name}: {collector.Errors - e0} error(s), {collector.Warnings - w0} warning(s)");
            }
        }
        finally { Debug.unityLogger.logHandler = collector.Inner; }

        if (ship.isDirty != dirtyBefore) collector.Add(LogType.Error, "The validator changed the Ship scene (it must not): please report this.", null);
        foreach (var (type, message, ctx) in collector.Items)
        {
            if (type == LogType.Warning) Debug.LogWarning(message, ctx);
            else if (type != LogType.Log) Debug.LogError(message, ctx);
        }
        if (openedHere) EditorSceneManager.CloseScene(ship, true);
        Selection.instanceIDs = selection;

        string verdict = collector.Errors > 0 ? "FAILED" : collector.Warnings > 0 ? "passed with warnings" : "passed";
        Debug.Log($"Validate Project For Play {verdict}: {collector.Errors} error(s), {collector.Warnings} warning(s).\n" + string.Join("\n", rows)
            + (collector.Errors + collector.Warnings > 0 ? "\n  Repairs are separate menus under Alien (see the messages above); this check changes nothing." : ""));
        return collector.Errors;
    }

    // Collects everything the composed validators log, so only problems and one summary are shown.
    sealed class ValidationLog : ILogHandler
    {
        public readonly ILogHandler Inner;
        public readonly List<(LogType type, string message, Object context)> Items = new();
        public int Errors, Warnings;

        public ValidationLog(ILogHandler inner) => Inner = inner;

        public void Add(LogType type, string message, Object context)
        {
            if (type == LogType.Warning) Warnings++;
            else if (type != LogType.Log) Errors++;
            if (type != LogType.Log) Items.Add((type, message, context));
        }

        public void LogFormat(LogType logType, Object context, string format, params object[] args) =>
            Add(logType, args == null || args.Length == 0 ? format : string.Format(format, args), context);

        public void LogException(Exception exception, Object context) => Add(LogType.Exception, exception.ToString(), context);
    }

    static void Err(string message, Object context = null) => Debug.LogError(message, context);
    static void Warn(string message, Object context = null) => Debug.LogWarning(message, context);

    // ---------- Player prefab ----------

    static void CheckPlayerPrefab()
    {
        const string Fix = "Run Alien > Add Escape System (and Add Throwable Noisemakers / Add Interactive Lockers)";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        if (prefab == null) { Err($"{PrototypeSetup.PrefabPath} is missing: run Alien > Setup Multiplayer Prototype."); return; }
        var objects = prefab.GetComponentsInChildren<NetworkObject>(true);
        if (objects.Length != 1 || prefab.GetComponent<NetworkObject>() == null) Err($"Player prefab: {objects.Length} NetworkObjects; it needs exactly one, on its root.", prefab);
        int missing = prefab.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
        if (missing > 0) Err($"Player prefab: {missing} missing script(s).", prefab);

        var required = new[]
        {
            typeof(NetworkFirstPersonController), typeof(CharacterController), typeof(NetworkTransform), typeof(PlayerLife), typeof(PlayerInteractor), typeof(PlayerInventory),
            typeof(FootstepNoise), typeof(ThreatVisuals), typeof(LocalSpectator), typeof(PlayerNoisemakers), typeof(PlayerHiding), typeof(PlayerMotionTracker),
        };
        foreach (var t in required) if (prefab.GetComponent(t) == null) Err($"Player prefab: no {t.Name}. {Fix}.", prefab);

        // The camera and listener are owner-only at run time (the controller switches them on for the owner); the prefab must have exactly one of each.
        int cams = prefab.GetComponentsInChildren<Camera>(true).Length, listeners = prefab.GetComponentsInChildren<AudioListener>(true).Length;
        if (cams != 1 || listeners != 1) Err($"Player prefab: {cams} camera(s) and {listeners} audio listener(s); it needs exactly one of each (they are enabled for the owner only).", prefab);
        var ctrl = prefab.GetComponent<NetworkFirstPersonController>();
        if (ctrl != null && (ctrl.playerCamera == null || ctrl.audioListener == null || ctrl.body == null)) Err("Player prefab: the controller's camera, listener or body reference is empty.", prefab);

        var inv = prefab.GetComponent<PlayerNoisemakers>();
        var device = AssetDatabase.LoadAssetAtPath<GameObject>(DevicePrefabPath);
        if (inv != null && (inv.devicePrefab == null || inv.devicePrefab != device)) Err($"Player prefab: PlayerNoisemakers does not point at {DevicePrefabPath}. Run Alien > Add Throwable Noisemakers.", prefab);
    }

    // ---------- Networking ----------

    static void CheckNetworking()
    {
        var nm = Object.FindFirstObjectByType<NetworkManager>();
        if (nm == null) { Err("The Ship scene has no NetworkManager (run Alien > Build Ship Scene)."); return; }
        var player = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        if (nm.NetworkConfig.PlayerPrefab != player) Err($"The NetworkManager's PlayerPrefab is not {PrototypeSetup.PrefabPath}.", nm);
        var lists = nm.NetworkConfig.Prefabs.NetworkPrefabsLists;
        if (lists.Count == 0) Err($"The NetworkManager has no network prefab list attached: nothing spawned at run time is registered. Run Alien > Add Throwable Noisemakers (it attaches {NetworkPrefabsPath}).", nm);
        if (lists.Any(l => l == null)) Err("The NetworkManager has an empty prefab-list slot.", nm);

        var entries = lists.Where(l => l != null).SelectMany(l => l.PrefabList).ToList();
        if (entries.Any(e => e == null || e.Prefab == null)) Err("A network prefab list has an entry with no prefab (a deleted asset?).", lists.FirstOrDefault(l => l != null));
        var prefabs = entries.Where(e => e != null && e.Prefab != null).Select(e => e.Prefab).ToList();
        foreach (var dup in prefabs.GroupBy(p => p).Where(g => g.Count() > 1)) Err($"'{AssetDatabase.GetAssetPath(dup.Key)}' is registered {dup.Count()} times.", dup.Key);
        var hashes = new Dictionary<uint, string>();
        foreach (var p in prefabs.Distinct())
        {
            var no = p.GetComponent<NetworkObject>();
            if (no == null) { Err($"'{AssetDatabase.GetAssetPath(p)}' is in a network prefab list but has no NetworkObject on its root.", p); continue; }
            uint h = Hash(no);
            if (h == 0) Err($"'{AssetDatabase.GetAssetPath(p)}' has a zero prefab hash (reimport it).", p);
            else if (hashes.TryGetValue(h, out var other)) Err($"'{AssetDatabase.GetAssetPath(p)}' and '{other}' share the prefab hash {h}.", p);
            else hashes[h] = AssetDatabase.GetAssetPath(p);
        }
        // Everything the game spawns at run time must be listed: the thrown device and the locker prefab. (The player is registered as the PlayerPrefab.)
        foreach (var path in new[] { DevicePrefabPath, LockerPrefabPath })
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) Err($"{path} is missing.");
            else if (!prefabs.Contains(asset)) Err($"{path} is not in the NetworkManager's prefab lists. Run Alien > {(path == DevicePrefabPath ? "Add Throwable Noisemakers" : "Add Interactive Lockers")}.", asset);
        }
    }

    // ---------- Scene ----------

    static void CheckSceneSystems()
    {
        int missingTotal = 0;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            int m = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
            if (m == 0) continue;
            missingTotal += m;
            Err($"Missing script on '{CreatureVentNetwork.PathOf(t)}' ({m}). For vent entrances run Alien > Validate And Repair Vent Network.", t.gameObject);
        }
        void One<T>(string what, int min = 1, int max = 1) where T : Object
        {
            int n = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            if (n < min || n > max) Err($"The Ship scene has {n} {what}; expected {(min == max ? min.ToString() : $"{min} to {max}")}.");
        }
        One<RoundManager>("RoundManager");
        One<CreatureAI>("creature (CreatureAI)");
        One<CreatureVentNetwork>("vent network");
        One<NetworkManager>("NetworkManager");
        One<ConnectionUI>("connection UI");
        One<Unity.AI.Navigation.NavMeshSurface>("NavMeshSurface", 1, 4);
        One<ShipAtmosphere>("ShipAtmosphere settings");
        One<EscapePod>("escape pods", 1, 8);
        One<SpawnPoint>("player spawn points", 1, 16);
        var creature = Object.FindFirstObjectByType<CreatureAI>();
        if (creature != null)
        {
            if (!NavMesh.SamplePosition(creature.transform.position, out _, 3f, NavMesh.AllAreas)) Err("The creature starts off the NavMesh (run Alien > Rebake Navigation).", creature);
            if (creature.GetComponent<NetworkObject>() == null) Err("The creature has no NetworkObject.", creature);
        }
        // Every in-scene NetworkObject needs a non-zero hash, unique in the scene.
        var seen = new Dictionary<uint, NetworkObject>();
        foreach (var no in Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            uint h = Hash(no);
            if (h == 0) Err($"'{CreatureVentNetwork.PathOf(no.transform)}' has a zero NetworkObject hash (save the scene, or rebuild it).", no);
            else if (seen.TryGetValue(h, out var other)) Err($"'{CreatureVentNetwork.PathOf(no.transform)}' and '{CreatureVentNetwork.PathOf(other.transform)}' share the NetworkObject hash {h}.", no);
            else seen[h] = no;
        }
        if (missingTotal == 0 && seen.Count == 0) Warn("The Ship scene has no NetworkObjects at all.");
    }

    static void CheckSceneRooms()
    {
        var rooms = Object.FindObjectsByType<RoomVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var dup in rooms.GroupBy(r => r.roomName).Where(g => string.IsNullOrEmpty(g.Key) || g.Count() > 1))
            Err(string.IsNullOrEmpty(dup.Key) ? "A room volume has no name." : $"Room name '{dup.Key}' is used {dup.Count()} times: room identity must be unique.", dup.First());
        foreach (var r in rooms)
        {
            foreach (var n in r.neighbours)
            {
                if (n == null) Err($"Room {r.roomName} has an empty neighbour slot.", r);
                else if (n == r) Err($"Room {r.roomName} lists itself as a neighbour.", r);
                else if (!rooms.Contains(n)) Err($"Room {r.roomName} has a neighbour that is not in the scene.", r);
                else if (!n.neighbours.Contains(r)) Warn($"Room {r.roomName} lists {n.roomName} as a neighbour but not the other way round.", r);
            }
            foreach (var p in r.searchPoints) if (p == null) Err($"Room {r.roomName} has an empty search-point slot.", r);
            foreach (var h in r.hidingSpots) if (h == null) Err($"Room {r.roomName} has an empty hiding-place slot.", r);
        }
        foreach (var l in Object.FindObjectsByType<RoomLink>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (l.roomA == null || l.roomB == null || l.pointA == null || l.pointB == null) Err($"{l.name}: incomplete room link (a room or side point is empty).", l);
            foreach (var d in l.doors) if (d == null) Err($"{l.name} lists an empty door.", l);
        }
        foreach (var dup in Object.FindObjectsByType<RoomLink>(FindObjectsInactive.Include, FindObjectsSortMode.None).GroupBy(l => l.id).Where(g => g.Count() > 1))
            Err($"Room link id {dup.Key} is used {dup.Count()} times.", dup.First());

        // Hiding places: a room that lists them, and a usable way for the creature to look in.
        foreach (var h in Object.FindObjectsByType<HidingSpot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (h.room == null) { Err($"{h.name}: not in a room (no room reference).", h); continue; }
            if (!h.room.hidingSpots.Contains(h)) Err($"{h.name}: room {h.room.roomName} does not list it, so the creature never searches it.", h);
            if (h.locker == null && (h.inspectPoints == null || h.inspectPoints.Length == 0 || h.inspectPoints.Any(t => t == null)))
                Err($"{h.name}: no inspection point, so the creature cannot look under it properly. Run Alien > Update Hiding Volumes (it restores them).", h);
            if (h.locker != null && (h.locker.inspectPoint == null || !h.inspectPoints.Contains(h.locker.inspectPoint))) Err($"{h.name}: the locker's inspection point is not one of its inspect points.", h);
        }

        foreach (var s in Object.FindObjectsByType<DoorSwitch>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (s.door == null) Err($"Door switch '{CreatureVentNetwork.PathOf(s.transform)}' has no door.", s);
        var doors = Object.FindObjectsByType<SlidingDoor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var controlled = new HashSet<SlidingDoor>(Object.FindObjectsByType<DoorSwitch>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(s => s.door));
        foreach (var c in Object.FindObjectsByType<ShipConsole>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (c.door != null) controlled.Add(c.door);
        foreach (var d in doors)
        {
            if (d.GetComponent<NetworkObject>() == null) Err($"Door '{d.name}' has no NetworkObject.", d);
            if (!controlled.Contains(d)) Warn($"Door '{d.name}' has no switch or console that operates it.", d);
        }

        var pods = Object.FindObjectsByType<EscapePod>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var p in pods)
        {
            if (p.interior == null) Err($"Pod '{p.podName}' has no interior volume.", p);
            if (p.capacity < 1) Err($"Pod '{p.podName}' has no capacity.", p);
        }
        var consoles = Object.FindObjectsByType<ShipConsole>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var p in pods) if (!consoles.Any(c => c.pod == p)) Err($"Pod '{p.podName}' has no console that launches it.", p);
        foreach (var c in consoles) if (c.pod != null && !pods.Contains(c.pod)) Err($"Console '{c.name}' points at a pod outside the scene.", c);
        foreach (var f in new[] { ShipFlags.FuseInstalled, ShipFlags.PowerRestored })
            if (!consoles.Any(c => (c.setFlags & f) != 0)) Warn($"No console sets the objective flag {f}.");
        foreach (var item in Object.FindObjectsByType<ItemPickup>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (item.spots == null || item.spots.Length == 0 || item.spots.Any(t => t == null)) Err($"Item '{item.name}' has no (or an empty) spawn spot.", item);
    }

    // ---------- Audio bank ----------

    static void CheckAudioBank()
    {
        var bank = AssetDatabase.LoadAssetAtPath<AudioBank>(AudioBankAssetPath);
        if (bank == null) { Err($"{AudioBankAssetPath} is missing: run Alien > Setup Audio."); return; }
        // A slot is assigned, empty (the optional ones - vent, tracker and noisemaker clips, duct tones - use a stand-in or silence), or a reference to a file this checkout lacks.
        string text = File.ReadAllText(AudioBankAssetPath);
        var missingFiles = new List<string>();
        foreach (Match m in Regex.Matches(text, @"^  (\w+): \{fileID: -?\d+, guid: ([0-9a-f]{32}), type: \d\}$", RegexOptions.Multiline))
            if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(m.Groups[2].Value))) missingFiles.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"^  - \{fileID: -?\d+, guid: ([0-9a-f]{32}), type: \d\}$", RegexOptions.Multiline))
            if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(m.Groups[1].Value))) missingFiles.Add("monsterFootsteps[]");
        if (missingFiles.Count > 0)
            Warn($"AudioBank references audio files that are not in this checkout ({string.Join(", ", missingFiles.Distinct())}). Those slots play nothing or a stand-in. The recordings are kept outside the repository: see Documentation/ExternalAssets.md.", bank);
        var required = new (string name, bool present)[]
        {
            ("monster footsteps", bank.monsterFootsteps != null && bank.monsterFootsteps.Length > 0 && bank.monsterFootsteps.All(c => c != null)),
            ("door sound", bank.doorSound != null), ("player footsteps", bank.footstepRegular1 != null), ("atmos bed", bank.atmos != null),
        };
        foreach (var (name, present) in required) if (!present) Warn($"AudioBank: no {name} (audio for it is silent). Run Alien > Setup Audio with the recordings in Assets/Sounds.", bank);
        if (bank.masterVolume <= 0f || bank.worldVolume <= 0f) Err("AudioBank: master or world volume is 0: no world sound can be heard.", bank);
        foreach (AcousticSpace space in Enum.GetValues(typeof(AcousticSpace)))
            if (bank.Profile(space) == null) Err($"AudioBank has no acoustic profile for {space}. Run Alien > Apply Acoustic Polish.", bank);
    }

    // ---------- Round reset ----------

    // Stateful per-round systems must implement IRoundResettable. Types that deliberately do not are listed with the reason, so a NEW
    // networked class with replicated state that is on neither list is reported for a decision.
    static readonly (Type type, string reason)[] ResetExemptions =
    {
        (typeof(RoundManager), "it owns the reset"),
        (typeof(PlayerLife), "RoundManager revives every player at the start of a round"),
        (typeof(NetworkFirstPersonController), "RespawnRpc resets position, look and stamina"),
        (typeof(PlayerMotionTracker), "it lowers itself whenever the round is not active"),
        (typeof(PlayerInteractor), "a hold cancels when the round is over"),
        (typeof(LocalSpectator), "local view only; follows the player's status"),
        (typeof(ThreatVisuals), "local view only; clears when the round is not active"),
    };

    static void CheckRoundReset()
    {
        var expected = new[] { typeof(CreatureAI), typeof(SlidingDoor), typeof(ItemPickup), typeof(EscapePod), typeof(FootstepNoise), typeof(PlayerInventory),
            typeof(PlayerHiding), typeof(PlayerNoisemakers), typeof(HideLocker), typeof(NoisemakerPickup), typeof(ThrownNoisemaker), typeof(ScenarioItem) };
        foreach (var t in expected)
            if (!typeof(IRoundResettable).IsAssignableFrom(t)) Err($"{t.Name} holds per-round state but does not implement IRoundResettable.");
        var exempt = new HashSet<Type>(ResetExemptions.Select(e => e.type));
        foreach (var t in typeof(CreatureAI).Assembly.GetTypes().Where(t => typeof(NetworkBehaviour).IsAssignableFrom(t) && !t.IsAbstract))
        {
            if (typeof(IRoundResettable).IsAssignableFrom(t) || exempt.Contains(t)) continue;
            bool replicated = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Any(f => typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType));
            if (replicated) Warn($"{t.Name} has replicated state but does not implement IRoundResettable and is not on the exemption list (ProjectValidator.ResetExemptions): decide whether a round must reset it.");
        }
    }
}
