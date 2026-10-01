using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Checks the escape scenario that is actually in the open generated-ship scene, independent of how it was generated. It reads the game objects back
/// (doors, the consoles that operate them, pods, fuse and generator consoles, item pickups and their spots), rebuilds a Scenario from them and runs
/// the same solver and one-player simulation the generator used. A hand edit that strands the player is reported here. Read-only.
/// </summary>
public static class ScenarioSceneValidator
{
    public static void Check(PhysicalReport rep, GeneratedShip ship)
    {
        // Stale or duplicated generated content, and missing scripts.
        if (SceneScope.All<GeneratedShip>().Length != 1) rep.errors.Add($"{SceneScope.All<GeneratedShip>().Length} GeneratedShip roots in the scene (expected exactly 1): rebuild the scene");
        if (SceneScope.All<RoundManager>().Length > 1) rep.errors.Add($"{SceneScope.All<RoundManager>().Length} RoundManagers in the scene (expected 1)");
        int scenarioRoots = ship.transform.Cast<Transform>().Count(t => t.name == "Escape Scenario");
        if (scenarioRoots > 1) rep.errors.Add($"{scenarioRoots} 'Escape Scenario' groups under the ship (expected 1)");
        int missing = 0;
        foreach (var go in SceneScope.All<Transform>().Select(t => t.gameObject))
            missing += UnityEditor.GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
        if (missing > 0) rep.errors.Add($"{missing} missing script reference(s) in the scene (a component whose script no longer exists)");

        var rooms = ship.GetComponentsInChildren<PlacedRoom>(true).OrderBy(r => r.nodeIndex).ToArray();
        if (rooms.Length == 0 || rooms.Length != ship.nodes.Count) return; // the layout checks already reported this
        var graph = ship.ToGraph();
        var doors = SceneScope.All<SlidingDoor>();
        var consoles = SceneScope.All<ShipConsole>();
        var pods = SceneScope.All<EscapePod>();
        var items = SceneScope.All<ScenarioItem>();
        var spawns = SceneScope.All<SpawnPoint>();

        if (SceneScope.First<RoundManager>() == null) rep.errors.Add("scenario: no RoundManager in the scene (no round can start)");
        if (pods.Length == 0) { rep.errors.Add("scenario: no EscapePod in the scene"); return; }
        if (spawns.Length == 0) return;

        int NodeAt(Vector3 p)
        {
            int best = 0;
            float bestD = float.MaxValue;
            foreach (var r in rooms)
            {
                var b = r.room.bounds.WorldBounds();
                float dx = Mathf.Max(b.min.x - p.x, 0f, p.x - b.max.x), dz = Mathf.Max(b.min.z - p.z, 0f, p.z - b.max.z);
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = r.nodeIndex; }
            }
            return best;
        }

        var sc = new Scenario { spawnNode = NodeAt(spawns.OrderBy(s => s.index).First().transform.position) };

        // Doors: which connection each one is in, and how it opens.
        foreach (var door in doors)
        {
            int edge = -1, doorNode = -1;
            for (int i = 0; i < ship.connections.Count && edge < 0; i++)
            {
                var c = ship.connections[i];
                foreach (var (node, socket) in new[] { (c.nodeA, c.socketA), (c.nodeB, c.socketB) })
                {
                    var s = rooms[node].room.sockets[socket];
                    var flat = door.transform.position - s.WorldPosition;
                    flat.y = 0f;
                    if (flat.magnitude < 0.4f) { edge = i; doorNode = node; break; }
                }
            }
            if (edge < 0) { rep.errors.Add($"scenario: door '{door.name}' is not in a doorway of any connection"); rep.markers.Add(door.transform.position); continue; }
            var gate = new DoorGate { edge = edge, doorNode = doorNode };
            foreach (var con in consoles.Where(c => c.door == door))
            {
                bool local = Vector3.Distance(con.transform.position, door.transform.position) < 3f;
                if (!local) gate.options.Add(new GateOption(OptionKind.Remote, NodeAt(con.transform.position)));
                else if (con.requiredItem == ItemKind.Keycard) gate.options.Add(new GateOption(OptionKind.Keycard));
                else if ((con.requiredFlags & ShipFlags.PowerRestored) != 0) gate.options.Add(new GateOption(OptionKind.Power));
                else if (con.holdSeconds > 0f) gate.options.Add(new GateOption(OptionKind.Override));
                else gate.options.Add(new GateOption(OptionKind.Override)); // a plain switch: anyone can use it
            }
            if (gate.options.Count == 0) { rep.errors.Add($"scenario: door '{door.name}' has no control that opens it, anywhere"); rep.markers.Add(door.transform.position); continue; }
            sc.gates.Add(gate);
        }

        // Consoles by what they do.
        foreach (var c in consoles)
        {
            int node = NodeAt(c.transform.position);
            if (c.pod != null) { sc.consoles.Add(new ConsolePlan { role = ConsoleRole.PodLaunch, node = node, podNode = NodeAt(c.pod.transform.position) }); }
            else if (c.requiredItem == ItemKind.Fuse && (c.setFlags & ShipFlags.FuseInstalled) != 0) sc.consoles.Add(new ConsolePlan { role = ConsoleRole.FuseSocket, node = node });
            else if ((c.setFlags & ShipFlags.PowerRestored) != 0) sc.consoles.Add(new ConsolePlan { role = ConsoleRole.Generator, node = node });
        }
        foreach (var pod in pods)
        {
            bool needsPower = (pod.requiredFlags & ShipFlags.PowerRestored) != 0;
            sc.pods.Add(new PodPlan { node = NodeAt(pod.transform.position), status = PodStatus.Operational, mode = needsPower ? LaunchMode.Loud : LaunchMode.ManualQuiet });
            if (!consoles.Any(c => c.pod == pod)) rep.errors.Add($"scenario: pod '{pod.podName}' has no launch console");
        }

        // Items: every pickup with its candidate spots; a pickup whose spots all lie in the Equipment Store is optional equipment.
        foreach (var si in items)
        {
            var pickup = si.GetComponent<ItemPickup>();
            if (si.spots == null || si.spots.Length == 0) { rep.errors.Add($"scenario: item '{si.itemId}' has no spots"); continue; }
            var plan = new ItemPlan { id = si.itemId, kind = pickup.kind, cls = pickup.kind == ItemKind.Keycard ? ItemClass.Keycard : ItemClass.Fuse };
            foreach (var spot in si.spots)
            {
                int node = NodeAt(spot.position);
                plan.candidates.Add(new SpotCandidate { node = node, anchorId = spot.name, room = ship.nodes[node].category, rule = "scene" });
            }
            plan.optional = plan.candidates.All(c => c.room == RoomCategory.EquipmentStore);
            plan.critical = !plan.optional;
            sc.items.Add(plan);
            // The spot must be free: nothing but the furniture it rests on is inside the item's own volume.
            if (SceneScope.PhysicsSafe)
            foreach (var spot in si.spots)
            {
                var size = pickup.transform.lossyScale;
                var centre = spot.position + Vector3.up * (size.y * 0.5f + 0.02f);
                var hits = Physics.OverlapBox(centre, size * 0.5f * 0.9f, Quaternion.identity, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    .Where(h => h.transform != pickup.transform && !h.transform.IsChildOf(pickup.transform)).ToArray();
                if (hits.Length > 0) { rep.errors.Add($"scenario: the spot of '{si.itemId}' at {spot.position:F1} is blocked by '{hits[0].name}'"); rep.markers.Add(spot.position); }
            }
        }

        // Requirements that name something the scene does not have.
        if (consoles.Any(c => c.requiredItem == ItemKind.Keycard) && !sc.items.Any(i => i.kind == ItemKind.Keycard && i.critical)) rep.errors.Add("scenario: a door needs the keycard but no keycard item exists");
        bool powerUsed = consoles.Any(c => (c.requiredFlags & ShipFlags.PowerRestored) != 0) || pods.Any(p => (p.requiredFlags & ShipFlags.PowerRestored) != 0);
        if (powerUsed)
        {
            if (!sc.items.Any(i => i.kind == ItemKind.Fuse && i.critical)) rep.errors.Add("scenario: something needs power but no fuse exists");
            if (sc.Console(ConsoleRole.FuseSocket) == null) rep.errors.Add("scenario: something needs power but there is no fuse socket console");
            if (sc.Console(ConsoleRole.Generator) == null) rep.errors.Add("scenario: something needs power but there is no generator console");
        }

        // Solve what is really in the scene, for the first candidate of each item and for several rounds.
        var res = ScenarioSolver.Solve(graph, sc);
        if (!res.solved) { rep.errors.Add("scenario: the scene cannot be escaped: " + string.Join("; ", res.failures.Take(4))); return; }
        for (int round = 0; round < 8; round++)
        {
            var sim = OnePlayerSimulator.CanEscape(graph, sc.ForRound(round * 7919 + 1), 250000);
            if (!sim.escaped) { rep.errors.Add($"scenario: for round seed {round * 7919 + 1} one player with one item slot cannot escape"); break; }
        }
        int noisemakers = SceneScope.All<NoisemakerPickup>().Length;
        rep.info.Add($"scenario in the scene: {sc.gates.Count} door(s), {sc.pods.Count} pod(s), {sc.items.Count(i => i.critical)} critical item(s), {sc.items.Count(i => i.optional)} optional item(s) and {noisemakers} noisemaker pickup(s); {res.stages} progression stage(s), {res.plans.Count} plan(s)");

        // The pressure budget on what is really in the scene (default budget: the scene does not record the settings it was built with).
        var ps = new PressureSettings();
        foreach (var plan in res.plans)
        {
            RoutePressure.Assess(graph, sc, plan, ps);
            string line = $"route to {graph.nodes[plan.podNode].id}: pressure {plan.pressure:0.0} ({string.Join(", ", plan.breakdown.Select(b => $"{b.what} {b.value:0.0}"))})";
            if (plan.pressure < ps.minimum) rep.errors.Add($"scenario: {line} is below the minimum {ps.minimum:0.0} (a trivially direct escape)");
            else rep.info.Add(line);
        }

        // Every in-scene NetworkObject needs a unique non-zero hash (they are computed when the scene is saved).
        var seen = new HashSet<uint>();
        foreach (var no in SceneScope.All<NetworkObject>())
            if (no.PrefabIdHash == 0 || !seen.Add(no.PrefabIdHash)) { rep.errors.Add($"scenario: NetworkObject '{no.name}' has a missing or duplicate hash: rebuild the scene or open and save it"); break; }
    }
}
