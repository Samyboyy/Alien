using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Unity EditMode tests for the physical side of the ship: the prefab builder's output and real Transform maths. They build rooms in memory with
// in-memory materials, so they create no assets and touch no scene. Run them in the Unity Test Runner (EditMode).
public class ShipPlacementAssetTests
{
    readonly List<Object> created = new();
    readonly Dictionary<GreyMaterial, Material> materials = new();

    [SetUp]
    public void SetUp()
    {
        GreyboxLibraryBuilder.MaterialProvider = m =>
        {
            if (!materials.TryGetValue(m, out var mat)) { mat = new Material(Shader.Find("HDRP/Lit") ?? Shader.Find("Standard")); created.Add(mat); materials[m] = mat; }
            return mat;
        };
    }

    [TearDown]
    public void TearDown()
    {
        GreyboxLibraryBuilder.MaterialProvider = null;
        foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
        created.Clear();
        materials.Clear();
    }

    GameObject Room(RoomBlueprint bp)
    {
        var go = GreyboxLibraryBuilder.BuildRoom(bp);
        created.Add(go);
        return go;
    }

    [Test]
    public void ARoomPrefabReportsTheSameTemplateAsItsBlueprint()
    {
        foreach (var bp in GreyboxBlueprints.Create())
        {
            var room = Room(bp).GetComponent<ShipRoomPrefab>();
            var a = bp.ToTemplate();
            var b = room.ToTemplate();
            Assert.AreEqual(a.id, b.id);
            Assert.AreEqual(a.definitionId, b.definitionId, a.id);
            Assert.AreEqual((a.sizeX, a.sizeZ, a.category, a.acoustics), (b.sizeX, b.sizeZ, b.category, b.acoustics), a.id);
            Assert.AreEqual(a.boundsWidth, b.boundsWidth, 1e-4, a.id);
            Assert.AreEqual(a.boundsDepth, b.boundsDepth, 1e-4, a.id);
            CollectionAssert.AreEqual(a.anchorCounts, b.anchorCounts, a.id + " anchors");
            Assert.AreEqual(a.sockets.Count, b.sockets.Count, a.id);
            for (int i = 0; i < a.sockets.Count; i++)
            {
                var x = a.sockets[i];
                var y = b.sockets[i];
                Assert.AreEqual((x.id, x.cell, x.side, x.width, x.height, x.type, x.door, x.support), (y.id, y.cell, y.side, y.width, y.height, y.type, y.door, y.support), $"{a.id} socket {i}");
            }
            Assert.IsEmpty(b.Problems(), a.id);

            // The semantics survive the prefab: every anchor keeps its kind, semantic, rules, volume and room-local position.
            Assert.AreEqual(a.anchors.Count, b.anchors.Count, a.id + " anchor list");
            for (int i = 0; i < a.anchors.Count; i++)
            {
                var x = a.anchors[i];
                var y = b.anchors[i];
                Assert.AreEqual((x.kind, x.semantic, x.id, x.context, x.allowed, x.forbidden, x.canHoldCritical, x.tags, x.requiredFeature),
                    (y.kind, y.semantic, y.id, y.context, y.allowed, y.forbidden, y.canHoldCritical, y.tags, y.requiredFeature), $"{a.id} anchor {i}");
                Assert.AreEqual(x.x, y.x, 1e-3f, $"{a.id}/{x.id} x");
                Assert.AreEqual(x.y, y.y, 1e-3f, $"{a.id}/{x.id} y");
                Assert.AreEqual(x.z, y.z, 1e-3f, $"{a.id}/{x.id} z");
                Assert.AreEqual((x.clearWidth, x.clearHeight, x.clearDepth, x.weight), (y.clearWidth, y.clearHeight, y.clearDepth, y.weight), $"{a.id}/{x.id} volume");
            }
        }
    }

    [Test]
    public void ItemAnchorsKeepTheirSupportingFurnitureInThePrefab()
    {
        var bp = GreyboxBlueprints.Create().First(b => b.definitionId == "security");
        var room = Room(bp).GetComponent<ShipRoomPrefab>();
        var rack = room.anchors.First(a => a.semantic == AnchorSemantic.CardRack);
        Assert.IsNotNull(rack.support, "the card rack anchor knows the collider it rests against");
        Assert.AreEqual(ItemClassMask.Keycard, rack.allowedItems);
        Assert.IsTrue(rack.canHoldCritical);
        // The clearance volume stands free of every collider except its support.
        var centre = rack.transform.position + Vector3.up * rack.clearance.y * 0.5f;
        var hits = Physics.OverlapBox(centre, rack.clearance * 0.5f * 0.98f).Where(h => h != rack.support && h.transform.IsChildOf(room.transform)).ToList();
        Assert.IsEmpty(hits.Select(h => h.name));
    }

    [Test]
    public void ScenarioItemDoesNothingOutsideARoundOnTheHostOnly()
    {
        var go = new GameObject("item");
        created.Add(go);
        var spot = new GameObject("spot").transform;
        created.Add(spot.gameObject);
        var si = go.AddComponent<ScenarioItem>();
        si.itemId = "keycard";
        si.spots = new[] { spot };
        Assert.DoesNotThrow(() => ((IRoundResettable)si).ResetForRound(new System.Random(1)), "a client (or an unspawned object) ignores the reset");
    }

    [Test]
    public void SocketsAlignInTheWorldUnderEveryRotation()
    {
        // Real Transform maths: a room placed with LayoutWorld's pivot and yaw must have every socket exactly on the middle of its cell edge, facing out.
        const float cell = GreyboxScale.Cell;
        foreach (var bp in GreyboxBlueprints.Create().Where(b => b.sockets.Count > 0))
            for (int rot = 0; rot < 4; rot++)
            {
                var template = bp.ToTemplate();
                var placement = new RoomPlacement { node = 0, template = template, rot = rot, origin = new Int2(5, -3), deck = 0 };
                var go = Room(bp);
                go.transform.SetPositionAndRotation(LayoutWorld.RoomPivot(placement, cell), LayoutWorld.RoomRotation(placement));
                var room = go.GetComponent<ShipRoomPrefab>();

                var box = LayoutWorld.RoomBox(placement, cell);
                var bounds = room.bounds.WorldBounds();
                Assert.That(Vector3.Distance(new Vector3(box.min.x, 0, box.min.z), new Vector3(bounds.min.x, 0, bounds.min.z)), Is.LessThan(1e-3f), $"{bp.variantId} rot {rot} bounds min");
                Assert.That(Vector3.Distance(new Vector3(box.max.x, 0, box.max.z), new Vector3(bounds.max.x, 0, bounds.max.z)), Is.LessThan(1e-3f), $"{bp.variantId} rot {rot} bounds max");

                for (int s = 0; s < template.sockets.Count; s++)
                {
                    var socket = room.sockets[s];
                    var step = Grid.Step(placement.SocketSide(s));
                    var expected = LayoutWorld.CellCentre(placement.SocketCell(s), 0, cell) + new Vector3(step.x, 0f, step.z) * cell * 0.5f;
                    Assert.That(Vector3.Distance(socket.WorldPosition, expected), Is.LessThan(1e-3f), $"{bp.variantId} rot {rot} socket {socket.socketId} position");
                    Assert.That(Vector3.Distance(socket.WorldForward, new Vector3(step.x, 0f, step.z)), Is.LessThan(1e-3f), $"{bp.variantId} rot {rot} socket {socket.socketId} facing");
                }
                foreach (var a in room.anchors)
                    Assert.IsTrue(box.Contains(new Vector3(a.transform.position.x, box.center.y, a.transform.position.z)), $"{bp.variantId} rot {rot}: {a.kind} anchor {a.anchorId} is outside the placed footprint");
                Object.DestroyImmediate(go);
            }
    }

    [Test]
    public void ARealPlacementMeetsAtEverySocketAndEveryCorridorStartsAtItsDoorway()
    {
        var specs = DefaultRoomCatalogue.Create();
        var library = TemplateLibrary.FromBlueprints();
        var blueprints = GreyboxBlueprints.Create().ToDictionary(b => b.variantId);
        const float cell = GreyboxScale.Cell;
        foreach (int seed in new[] { 2, 3, 4 })
        {
            var g = new ShipGraphGenerator(specs).Generate(seed).graph;
            var placed = new ShipPlacer(library, specs).Place(g);
            Assert.IsTrue(placed.success, $"seed {seed}: {placed.Summary}");
            var l = placed.layout;
            var rooms = new List<ShipRoomPrefab>();
            foreach (var r in l.rooms)
            {
                var go = Room(blueprints[r.template.id]);
                go.transform.SetPositionAndRotation(LayoutWorld.RoomPivot(r, cell), LayoutWorld.RoomRotation(r));
                rooms.Add(go.GetComponent<ShipRoomPrefab>());
            }
            foreach (var c in l.connections)
            {
                var a = rooms[c.nodeA].sockets[c.socketA];
                var b = rooms[c.nodeB].sockets[c.socketB];
                if (c.mode == ConnectionMode.Direct)
                {
                    Assert.That(Vector3.Distance(a.WorldPosition, b.WorldPosition), Is.LessThan(1e-3f), $"seed {seed} {c.edgeId}: direct sockets do not meet");
                    Assert.That(Vector3.Dot(a.WorldForward, b.WorldForward), Is.LessThan(-0.999f), $"seed {seed} {c.edgeId}: direct sockets do not face each other");
                }
                else
                {
                    var first = LayoutWorld.CellCentre(c.cells[0], 0, cell);
                    var last = LayoutWorld.CellCentre(c.cells[^1], 0, cell);
                    Assert.That(Vector3.Distance(first, a.WorldPosition + a.WorldForward * cell * 0.5f), Is.LessThan(1e-3f), $"seed {seed} {c.edgeId}: corridor start");
                    Assert.That(Vector3.Distance(last, b.WorldPosition + b.WorldForward * cell * 0.5f), Is.LessThan(1e-3f), $"seed {seed} {c.edgeId}: corridor end");
                }
            }
            // No two rooms' world bounds overlap.
            var boxes = rooms.Select(r => r.bounds.WorldBounds()).ToList();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var bi = boxes[i]; bi.Expand(-0.1f);
                    Assert.IsFalse(bi.Intersects(boxes[j]), $"seed {seed}: {l.graph.nodes[i].id} and {l.graph.nodes[j].id} overlap in the world");
                }
        }
    }

    [Test]
    public void CorridorModulesWallOffExactlyTheirClosedSides()
    {
        foreach (CorridorPieceKind kind in System.Enum.GetValues(typeof(CorridorPieceKind)))
            for (int rot = 0; rot < 4; rot++)
            {
                var go = GreyboxLibraryBuilder.BuildCorridor(kind);
                created.Add(go);
                go.transform.rotation = Quaternion.Euler(0f, 90f * rot, 0f);
                int open = CorridorMath.OpenMask(kind, rot);
                foreach (GridSide side in System.Enum.GetValues(typeof(GridSide)))
                {
                    var step = Grid.Step(side);
                    var probe = go.transform.position + new Vector3(step.x, 0f, step.z) * (GreyboxScale.Cell * 0.5f - GreyboxScale.WallThickness * 0.5f) + Vector3.up * 2f;
                    bool walled = go.GetComponentsInChildren<Renderer>().Any(r => r.name.StartsWith("Wall") && r.bounds.Contains(probe));
                    Assert.AreEqual((open & Grid.Bit(side)) == 0, walled, $"{kind} rot {rot} side {side}");
                }
                Object.DestroyImmediate(go);
            }
    }

    [Test]
    public void TheWallSealClosesADoorwayAndEveryDoorwayHasAClearOpeningAndALintel()
    {
        var seal = GreyboxLibraryBuilder.BuildSeal();
        created.Add(seal);
        var wall = seal.GetComponentInChildren<Renderer>().bounds;
        Assert.That(wall.size.x, Is.EqualTo(GreyboxScale.Cell).Within(1e-3f), "as wide as a doorway cell");
        Assert.That(wall.size.y, Is.EqualTo(GreyboxScale.WallHeight).Within(1e-3f), "as tall as a wall");

        var bp = GreyboxBlueprints.Create().First(b => b.variantId == "mess_hall.a");
        var room = Room(bp).GetComponent<ShipRoomPrefab>();
        foreach (var s in room.sockets)
        {
            // Walking through the doorway at head height 2 m is free; above the door height there is a lintel.
            var low = s.transform.position + s.transform.forward * -0.15f + Vector3.up * 1.0f;
            var high = s.transform.position + s.transform.forward * -0.15f + Vector3.up * 3.2f;
            var renderers = room.GetComponentsInChildren<Renderer>().Where(r => r.name.StartsWith("Wall") || r.name.StartsWith("Lintel")).ToList();
            Assert.IsFalse(renderers.Any(r => r.bounds.Contains(low)), $"socket {s.socketId} is blocked at walking height");
            Assert.IsTrue(renderers.Any(r => r.name.StartsWith("Lintel") && r.bounds.Contains(high)), $"socket {s.socketId} has no lintel");
        }
    }

    [Test]
    public void GreyboxLibraryProblemsReportEmptySlotsAndMissingModules()
    {
        var lib = ScriptableObject.CreateInstance<GreyboxLibrary>();
        created.Add(lib);
        lib.rooms.Add(null);
        var problems = lib.Problems(DefaultRoomCatalogue.Create());
        Assert.IsTrue(problems.Any(p => p.Contains("empty room slot")), string.Join("\n", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("no corridor module for Cross")));
        Assert.IsTrue(problems.Any(p => p.Contains("no wall seal module")));
        Assert.IsTrue(problems.Any(p => p.Contains("no prefab variant for room definition 'bridge'")));
    }

    [Test]
    public void TheGeneratedShipRebuildsItsGraphFromTheSceneRecords()
    {
        var specs = DefaultRoomCatalogue.Create();
        var g = new ShipGraphGenerator(specs).Generate(5).graph;
        var go = new GameObject("ship");
        created.Add(go);
        var ship = go.AddComponent<GeneratedShip>();
        ship.seed = 5;
        foreach (var n in g.nodes) ship.nodes.Add(new NodeRecord { index = n.index, id = n.id, definitionId = n.definitionId, displayName = n.displayName, category = n.category, sector = n.sector, deck = n.deck });
        foreach (var e in g.edges) ship.connections.Add(new ConnectionRecord { edge = ship.connections.Count, edgeId = e.id, nodeA = e.a, nodeB = e.b });
        ship.route.AddRange(g.route);
        var back = ship.ToGraph();
        Assert.AreEqual(g.Canonical(), back.Canonical(), "same rooms, same connection ids, same route");
    }
}
