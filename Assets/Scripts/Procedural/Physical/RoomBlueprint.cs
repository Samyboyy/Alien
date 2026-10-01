using System;
using System.Collections.Generic;

/// <summary>Physical sizes shared by the blueprints, the prefab builder and placement (metres).</summary>
public static class GreyboxScale
{
    public const float Cell = 2f, WallHeight = 4f, WallThickness = 0.3f, DoorHeight = 2.5f, FloorThickness = 0.2f, DeckHeight = 4.6f;
}

/// <summary>Which prototype material a greybox box uses (the builder maps these to the project's prototype materials).</summary>
public enum GreyMaterial : byte { Wall, Floor, Ceiling, Table, Console, Crate, Machinery, Bed, Locker, Shelf, Pod, Hazard }

/// <summary>One axis-aligned box of furniture or machinery: its minimum corner on the floor plane and its size, in metres, room-local.</summary>
public sealed class BoxSpec
{
    public string name;
    public GreyMaterial material;
    public float x0, z0, width, depth, height, y0;
    // Semantic authoring (see AnchorSemantics): what this box is, so an item anchor can be derived from it. None = plain furniture.
    public AnchorSemantic semantic;
    public AnchorContext context;
    public ItemClassMask allowed, forbidden;
    public float weight = 1f;
    public bool critical = true;
    public NarrativeTag tags;

    public bool Overlaps(float x, float z, float r) => x > x0 - r && x < x0 + width + r && z > z0 - r && z < z0 + depth + r;
    public float Top => y0 + height;
}

/// <summary>An anchor of a room blueprint (room-local metres; y is the height above the floor).</summary>
public sealed class AnchorSpec : AnchorInfo
{
    public ItemAnchorCategory item;
    /// <summary>Authoring only: the box the anchor stands on or against.</summary>
    public BoxSpec support;
}

/// <summary>
/// Pure authoring data for one greybox room prefab: footprint, sockets, furniture boxes and anchors. The prefab builder turns it into a prefab;
/// ToTemplate gives the same RoomTemplate the prefab reports, so placement can be tested without assets.
/// </summary>
public sealed class RoomBlueprint
{
    public string variantId = "";
    public string definitionId = "";
    public RoomCategory category;
    public int sizeX, sizeZ;
    public RoomAcoustics acoustics;
    public readonly List<SocketSpec> sockets = new();
    public readonly List<BoxSpec> boxes = new();
    public readonly List<AnchorSpec> anchors = new();

    public float Width => sizeX * GreyboxScale.Cell;
    public float Depth => sizeZ * GreyboxScale.Cell;

    public RoomTemplate ToTemplate()
    {
        var t = new RoomTemplate { id = variantId, definitionId = definitionId, category = category, sizeX = sizeX, sizeZ = sizeZ, acoustics = acoustics, boundsWidth = Width, boundsDepth = Depth };
        foreach (var s in sockets) t.sockets.Add(s.Clone());
        foreach (var a in anchors) { t.anchorCounts[(int)a.kind]++; t.anchors.Add(a.CloneInfo()); }
        return t;
    }

    /// <summary>Room-local metre position of the middle of a socket's doorway, on the wall line.</summary>
    public static void SocketPoint(SocketSpec s, out float x, out float z)
    {
        float c = GreyboxScale.Cell;
        x = (s.cell.x + 0.5f) * c;
        z = (s.cell.z + 0.5f) * c;
        switch (s.side)
        {
            case GridSide.North: z += c * 0.5f; break;
            case GridSide.South: z -= c * 0.5f; break;
            case GridSide.East: x += c * 0.5f; break;
            default: x -= c * 0.5f; break;
        }
    }

    public static float SideYaw(GridSide s) => (int)s * 90f;

    // A 0.25 m raster of where a walker (radius 0.4 m) can stand, flood-filled from the first doorway. Anchors are only placed on reachable floor, so
    // none ends up in a pocket between furniture. The room walkability test in the test suite uses the same model on its own raster.
    const float ReachRes = 0.25f, WalkerRadius = 0.4f;
    bool[,] reach;

    bool WalkFree(int ix, int iz)
    {
        float x = (ix + 0.5f) * ReachRes, z = (iz + 0.5f) * ReachRes, edge = GreyboxScale.WallThickness + 0.2f;
        if (x < edge || z < edge || x > Width - edge || z > Depth - edge) return false;
        foreach (var b in boxes) if (b.y0 < 1.8f && b.height >= 0.5f && b.Overlaps(x, z, WalkerRadius)) return false;
        return true;
    }

    public void ComputeReach()
    {
        int nx = (int)(Width / ReachRes), nz = (int)(Depth / ReachRes);
        reach = new bool[nx, nz];
        if (sockets.Count == 0) { for (int i = 0; i < nx; i++) for (int j = 0; j < nz; j++) reach[i, j] = WalkFree(i, j); return; }
        SocketPoint(sockets[0], out float sx, out float sz);
        float ix = sockets[0].side == GridSide.East ? sx - 1f : sockets[0].side == GridSide.West ? sx + 1f : sx;
        float iz = sockets[0].side == GridSide.North ? sz - 1f : sockets[0].side == GridSide.South ? sz + 1f : sz;
        int cx = (int)(ix / ReachRes), cz = (int)(iz / ReachRes);
        if (cx < 0 || cz < 0 || cx >= nx || cz >= nz || !WalkFree(cx, cz)) return;
        var queue = new Queue<(int, int)>();
        queue.Enqueue((cx, cz));
        reach[cx, cz] = true;
        while (queue.Count > 0)
        {
            var (x, z) = queue.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                int ax = x + (d == 0 ? 1 : d == 1 ? -1 : 0), az = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (ax < 0 || az < 0 || ax >= nx || az >= nz || reach[ax, az] || !WalkFree(ax, az)) continue;
                reach[ax, az] = true;
                queue.Enqueue((ax, az));
            }
        }
    }

    /// <summary>True when a walker can stand at the point and walk to the first doorway (call <see cref="ComputeReach"/> after the furniture is final).</summary>
    public bool Reachable(float x, float z)
    {
        if (reach == null) return true;
        int ix = (int)(x / ReachRes), iz = (int)(z / ReachRes);
        return ix >= 0 && iz >= 0 && ix < reach.GetLength(0) && iz < reach.GetLength(1) && reach[ix, iz];
    }

    /// <summary>True when the point (with the given clearance) is inside the room, off every solid box, and outside every doorway cell.</summary>
    public bool IsFree(float x, float z, float clearance, float minBoxHeight = 0.4f)
    {
        float c = GreyboxScale.Cell, edge = GreyboxScale.WallThickness + clearance;
        if (x < edge || z < edge || x > Width - edge || z > Depth - edge) return false;
        foreach (var b in boxes) if (b.height >= minBoxHeight && b.y0 < 1.8f && b.Overlaps(x, z, clearance)) return false;
        foreach (var s in sockets)
            if ((int)(x / c) == s.cell.x && (int)(z / c) == s.cell.z) return false;
        return true;
    }

    public int Count(AnchorKind k)
    {
        int n = 0;
        foreach (var a in anchors) if (a.kind == k) n++;
        return n;
    }
}

/// <summary>A small fluent helper for writing blueprints (see GreyboxBlueprints).</summary>
public sealed class BlueprintWriter
{
    public readonly RoomBlueprint bp = new();

    public BlueprintWriter(string definitionId, RoomCategory category, string variant, int sx, int sz, RoomAcoustics acoustics)
    {
        bp.definitionId = definitionId;
        bp.variantId = $"{definitionId}.{variant}";
        bp.category = category;
        bp.sizeX = sx;
        bp.sizeZ = sz;
        bp.acoustics = acoustics;
    }

    /// <summary>A socket on <paramref name="side"/> at cell <paramref name="index"/> along that side (x for north/south, z for east/west).</summary>
    public BlueprintWriter Socket(GridSide side, int index, DoorPolicy door = DoorPolicy.Permitted, SocketSupport support = SocketSupport.Corridor | SocketSupport.Direct,
        SocketConnectionType type = SocketConnectionType.Standard)
    {
        var cell = side switch
        {
            GridSide.North => new Int2(index, bp.sizeZ - 1),
            GridSide.South => new Int2(index, 0),
            GridSide.East => new Int2(bp.sizeX - 1, index),
            _ => new Int2(0, index),
        };
        bp.sockets.Add(new SocketSpec { id = $"{char.ToLowerInvariant(side.ToString()[0])}{index}", cell = cell, side = side, door = door, support = support, type = type });
        return this;
    }

    public BlueprintWriter Box(GreyMaterial m, string name, float x0, float z0, float w, float d, float h, float y0 = 0f)
    {
        bp.boxes.Add(new BoxSpec { name = name, material = m, x0 = x0, z0 = z0, width = w, depth = d, height = h, y0 = y0 });
        return this;
    }

    /// <summary>A row of identical boxes along X (<paramref name="alongX"/>) or Z.</summary>
    public BlueprintWriter Row(GreyMaterial m, string name, float x0, float z0, int count, float w, float d, float h, float stepX, float stepZ, float y0 = 0f)
    {
        for (int i = 0; i < count; i++) Box(m, $"{name} {i + 1}", x0 + stepX * i, z0 + stepZ * i, w, d, h, y0);
        return this;
    }

    /// <summary>
    /// Marks the last <paramref name="boxes"/> boxes as item-bearing furniture of the given semantic: Finish derives an item anchor from each (on top,
    /// at the front face, or beside it). This is authoring data, not name matching.
    /// </summary>
    public BlueprintWriter Sem(AnchorSemantic semantic, ItemClassMask allowed, AnchorContext context = AnchorContext.Exposed, float weight = 1f, bool critical = true,
        NarrativeTag tags = NarrativeTag.None, ItemClassMask forbidden = ItemClassMask.None, int boxes = 1)
    {
        for (int i = bp.boxes.Count - boxes; i < bp.boxes.Count; i++)
        {
            var b = bp.boxes[i];
            b.semantic = semantic; b.allowed = allowed; b.forbidden = forbidden; b.context = context; b.weight = weight; b.critical = critical; b.tags = tags;
        }
        return this;
    }

    /// <summary>An explicit console anchor (a console or socket object stands here later): clearance for a 0.8 m console.</summary>
    public BlueprintWriter Console(AnchorSemantic semantic, float x, float z, float yaw = 0f)
    {
        bp.anchors.Add(ConsoleAnchor(semantic, x, z, yaw));
        return this;
    }

    static AnchorSpec ConsoleAnchor(AnchorSemantic semantic, float x, float z, float yaw) => new()
    {
        kind = AnchorKind.Objective, semantic = semantic, x = x, y = 0.05f, z = z, yaw = yaw, id = semantic.ToString().ToLowerInvariant(),
        clearWidth = semantic == AnchorSemantic.PodBerth ? 6f : 0.9f, clearHeight = semantic == AnchorSemantic.PodBerth ? 2.4f : 1.3f,
        clearDepth = semantic == AnchorSemantic.PodBerth ? 3.2f : 0.9f, requiredFeature = RoomFeatures.Objectives, weight = 1f,
    };

    public BlueprintWriter Anchor(AnchorKind kind, float x, float z, float y = 0.05f, float yaw = 0f, ItemAnchorCategory item = ItemAnchorCategory.General)
    {
        bp.anchors.Add(new AnchorSpec { kind = kind, x = x, y = y, z = z, yaw = yaw, item = item, id = $"{kind}{bp.Count(kind)}".ToLowerInvariant() });
        return this;
    }

    /// <summary>
    /// Adds every anchor the definition requires that the author did not place, at free spots, plus one door anchor per socket. Deterministic.
    /// </summary>
    public RoomBlueprint Finish()
    {
        DefaultRoomCatalogueLookup.TryGet(bp.definitionId, out var spec);
        spec ??= new RoomSpec { id = bp.definitionId, category = bp.category };

        if ((spec.features & RoomFeatures.Doors) != 0)
            foreach (var s in bp.sockets)
            {
                if (s.door == DoorPolicy.Forbidden) continue;
                RoomBlueprint.SocketPoint(s, out float x, out float z);
                bp.anchors.Add(new AnchorSpec { kind = AnchorKind.DoorSocket, x = x, y = 0f, z = z, yaw = RoomBlueprint.SideYaw(s.side), socketId = s.id, id = $"door_{s.id}" });
            }

        bp.ComputeReach();
        AddSemanticItemAnchors();
        AddRoleConsoles();
        var template = bp.ToTemplate();
        for (int k = 0; k < RoomTemplate.AnchorKindCount; k++)
        {
            var kind = (AnchorKind)k;
            if (kind == AnchorKind.DoorSocket) continue;
            int need = AnchorRules.Required(spec, template, kind);
            while (bp.Count(kind) < need) AddAuto(kind);
        }
        foreach (var a in bp.anchors)
            if (a.kind == AnchorKind.Objective && a.semantic == AnchorSemantic.None)
            {
                a.semantic = AnchorSemantic.ObjectiveConsole; a.clearWidth = 0.9f; a.clearHeight = 1.3f; a.clearDepth = 0.9f; a.requiredFeature = RoomFeatures.Objectives;
            }
        return bp;
    }

    static readonly Dictionary<string, AnchorSemantic[]> RoleConsoles = new()
    {
        ["power_control"] = new[] { AnchorSemantic.FuseSocket, AnchorSemantic.ObjectiveConsole },
        ["engineering"] = new[] { AnchorSemantic.ObjectiveConsole },
        ["security"] = new[] { AnchorSemantic.DoorControlConsole },
        ["camera_control"] = new[] { AnchorSemantic.CameraControlConsole },
        ["escape_pod_bay"] = new[] { AnchorSemantic.PodLaunchConsole },
    };

    // The consoles a room has by role (fuse socket, generator, door control...), at free spots near its machinery unless the author placed them.
    void AddRoleConsoles()
    {
        if (!RoleConsoles.TryGetValue(bp.definitionId, out var list)) return;
        foreach (var sem in list)
        {
            if (bp.anchors.Exists(a => a.kind == AnchorKind.Objective && a.semantic == sem)) continue;
            float x = 0, z = 0;
            bool found = false;
            foreach (var b in bp.boxes)
                if ((b.material == GreyMaterial.Console || b.material == GreyMaterial.Machinery) && FreeNear(b, out x, out z, 0.85f)) { found = true; break; }
            if (!found) FarthestFreeSpot(AnchorKind.Objective, 0.85f, out x, out z);
            var a2 = ConsoleAnchor(sem, x, z, 0f);
            if (sem == AnchorSemantic.ObjectiveConsole) a2.id = "generator_console";
            bp.anchors.Add(a2);
        }
    }

    // One item anchor per tagged box (two on a long one): on its top if low, else at the face that looks into the room (or away from it for a
    // concealed spot). A point is used only if its clearance volume touches nothing else and a walker can stand within reach of it.
    void AddSemanticItemAnchors()
    {
        var counts = new Dictionary<AnchorSemantic, int>();
        foreach (var b in bp.boxes)
        {
            if (b.semantic == AnchorSemantic.None) continue;
            bool longBox = Math.Max(b.width, b.depth) >= 2.8f;
            for (int k = 0; k < (longBox ? 2 : 1); k++)
            {
                float f = longBox ? (k == 0 ? 0.25f : 0.75f) : 0.5f;
                if (!TryItemPoint(b, f, out var a)) continue;
                counts.TryGetValue(b.semantic, out int n);
                counts[b.semantic] = n + 1;
                a.id = (b.semantic.ToString() + "_" + n).ToLowerInvariant();
                bp.anchors.Add(a);
            }
        }
    }

    bool TryItemPoint(BoxSpec b, float f, out AnchorSpec a)
    {
        a = null;
        float W = bp.Width, D = bp.Depth;
        float cx = b.x0 + b.width * 0.5f, cz = b.z0 + b.depth * 0.5f;
        var candidates = new List<(float x, float y, float z, float yaw)>();
        bool surface = b.Top <= 1.3f && b.context != AnchorContext.InsideStorage && b.context != AnchorContext.Concealed;
        if (surface) candidates.Add((b.x0 + b.width * (b.width >= b.depth ? f : 0.5f), b.Top, b.z0 + b.depth * (b.width >= b.depth ? 0.5f : f), 0f));
        // Faces ordered by how much they look into the room (or away from it when concealed).
        var faces = new List<(float score, int side)>();
        float toX = W * 0.5f - cx, toZ = D * 0.5f - cz;
        faces.Add((toZ, 0)); faces.Add((toX, 1)); faces.Add((-toZ, 2)); faces.Add((-toX, 3));
        bool concealed = b.context == AnchorContext.Concealed;
        faces.Sort((p, q) => concealed ? p.score.CompareTo(q.score) : q.score.CompareTo(p.score));
        float y = concealed ? 0.05f : b.context == AnchorContext.InsideStorage ? Math.Min(1.2f, b.Top - 0.5f) : Math.Min(1.1f, b.Top - 0.3f);
        foreach (var face in faces)
        {
            int side = face.side;
            float px = side == 1 ? b.x0 + b.width + 0.25f : side == 3 ? b.x0 - 0.25f : b.x0 + b.width * f;
            float pz = side == 0 ? b.z0 + b.depth + 0.25f : side == 2 ? b.z0 - 0.25f : b.z0 + b.depth * f;
            candidates.Add((px, Math.Max(y, 0.05f), pz, side * 90f));
        }
        foreach (var c in candidates)
        {
            var t = new AnchorSpec
            {
                kind = AnchorKind.Item, semantic = b.semantic, x = c.x, y = c.y, z = c.z, yaw = c.yaw, context = b.context, allowed = b.allowed, forbidden = b.forbidden,
                weight = b.weight, canHoldCritical = b.critical, tags = b.tags, support = b,
            };
            if (ItemSpotValid(t, b)) { a = t; return true; }
        }
        return false;
    }

    bool ItemSpotValid(AnchorSpec a, BoxSpec support)
    {
        float edge = GreyboxScale.WallThickness + a.clearWidth * 0.5f + 0.05f;
        if (a.x < edge || a.z < edge || a.x > bp.Width - edge || a.z > bp.Depth - edge) return false;
        if (VolumeBlocked(bp, a, support)) return false;
        float cell = GreyboxScale.Cell;
        foreach (var s in bp.sockets) if ((int)(a.x / cell) == s.cell.x && (int)(a.z / cell) == s.cell.z) return false;
        // A walker must be able to stand within reach (about a metre) of it.
        for (int i = 0; i < 8; i++)
            foreach (float r in new[] { 0.8f, 1.1f })
            {
                float ang = i * (float)Math.PI / 4f;
                if (bp.Reachable(a.x + (float)Math.Cos(ang) * r, a.z + (float)Math.Sin(ang) * r)) return true;
            }
        return false;
    }

    /// <summary>True when the anchor clearance volume overlaps any box other than the one it rests on.</summary>
    public static bool VolumeBlocked(RoomBlueprint bp, AnchorInfo a, BoxSpec support)
    {
        float hx = a.clearWidth * 0.5f, hz = a.clearDepth * 0.5f, y0 = a.y, y1 = a.y + a.clearHeight;
        foreach (var o in bp.boxes)
        {
            if (o == support || o.height < 0.1f) continue;
            if (o.y0 >= y1 || o.Top <= y0 + 0.001f) continue;
            if (a.x + hx > o.x0 + 0.001f && a.x - hx < o.x0 + o.width - 0.001f && a.z + hz > o.z0 + 0.001f && a.z - hz < o.z0 + o.depth - 0.001f) return true;
        }
        return false;
    }

    void AddAuto(AnchorKind kind)
    {
        float W = bp.Width, D = bp.Depth;
        switch (kind)
        {
            case AnchorKind.PatrolPoint:
            case AnchorKind.SearchPoint:
            case AnchorKind.PlayerSpawn:
            case AnchorKind.Hazard:
            case AnchorKind.CreatureSpawn:
            {
                FarthestFreeSpot(kind, kind == AnchorKind.PatrolPoint ? 0.7f : 0.6f, out float x, out float z);
                bp.anchors.Add(new AnchorSpec { kind = kind, x = x, y = 0.05f, z = z, yaw = 0f, id = $"{kind}{bp.Count(kind)}".ToLowerInvariant() });
                break;
            }
            case AnchorKind.Hiding:
            {
                // Beside the tallest furniture: a place to crouch out of the room's sight lines.
                BoxSpec best = null;
                foreach (var b in bp.boxes) if (b.height >= 1.0f && (best == null || b.width * b.depth > best.width * best.depth)) best = b;
                float x, z;
                if (best == null || !FreeNear(best, out x, out z)) FarthestFreeSpot(kind, 0.5f, out x, out z);
                bp.anchors.Add(new AnchorSpec { kind = kind, x = x, y = 0.05f, z = z, id = $"hiding{bp.Count(kind)}" });
                break;
            }
            case AnchorKind.Item:
            {
                // On the top of furniture of about table height, else on the floor.
                var tops = new List<BoxSpec>();
                foreach (var b in bp.boxes) if (b.Top >= 0.6f && b.Top <= 1.4f && b.width >= 0.5f && b.depth >= 0.5f) tops.Add(b);
                int n = bp.Count(kind);
                if (tops.Count > 0)
                {
                    var b = tops[n % tops.Count];
                    bp.anchors.Add(new AnchorSpec { kind = kind, x = b.x0 + b.width * 0.5f, z = b.z0 + b.depth * 0.5f, y = b.Top, id = $"item{n}", item = (ItemAnchorCategory)(n % 7) });
                }
                else
                {
                    FarthestFreeSpot(kind, 0.4f, out float x, out float z);
                    bp.anchors.Add(new AnchorSpec { kind = kind, x = x, z = z, y = 0.05f, id = $"item{n}", item = (ItemAnchorCategory)(n % 7) });
                }
                break;
            }
            case AnchorKind.Objective:
            {
                BoxSpec console = null;
                foreach (var b in bp.boxes) if (b.material == GreyMaterial.Console || b.material == GreyMaterial.Machinery) { console = b; break; }
                if (console != null && FreeNear(console, out float x, out float z))
                    bp.anchors.Add(new AnchorSpec { kind = kind, x = x, z = z, y = 0.05f, id = $"objective{bp.Count(kind)}" });
                else
                {
                    FarthestFreeSpot(kind, 0.6f, out x, out z);
                    bp.anchors.Add(new AnchorSpec { kind = kind, x = x, z = z, y = 0.05f, id = $"objective{bp.Count(kind)}" });
                }
                break;
            }
            case AnchorKind.CameraMount:
                bp.anchors.Add(new AnchorSpec { kind = kind, x = 0.6f, z = 0.6f, y = GreyboxScale.WallHeight - 0.4f, yaw = 45f, id = $"camera{bp.Count(kind)}" });
                break;
            case AnchorKind.VentEntrance:
            case AnchorKind.VentNode:
            {
                // A low grille on a wall, away from the doorways, and a routing node above it.
                float x = 0.35f, z = 0f;
                bool found = false;
                for (float t = 1f; t < D - 1f && !found; t += 1f)
                {
                    int cz = (int)(t / GreyboxScale.Cell);
                    found = true;
                    foreach (var s in bp.sockets) if (s.side == GridSide.West && s.cell.z == cz) found = false;
                    foreach (var b in bp.boxes) if (b.height >= 0.4f && b.y0 < 1.8f && b.Overlaps(0.8f, t, 0.4f)) found = false;
                    if (found) z = t;
                }
                if (!found) { x = 0.35f; z = D * 0.5f; }
                bp.anchors.Add(new AnchorSpec { kind = AnchorKind.VentEntrance, x = x, z = z, y = 0.45f, yaw = 90f, id = $"vent_entrance{bp.Count(AnchorKind.VentEntrance)}" });
                bp.anchors.Add(new AnchorSpec { kind = AnchorKind.VentNode, x = x + 0.8f, z = z, y = GreyboxScale.WallHeight - 0.5f, id = $"vent_node{bp.Count(AnchorKind.VentNode)}" });
                break;
            }
            default:
                throw new InvalidOperationException($"no automatic placement for {kind}");
        }
    }

    // The free point (on a 0.5 m lattice) furthest from every anchor already placed of the same kind, so repeated anchors spread out.
    void FarthestFreeSpot(AnchorKind kind, float clearance, out float bx, out float bz)
    {
        bx = bp.Width * 0.5f;
        bz = bp.Depth * 0.5f;
        float best = -1f;
        for (float x = 1f; x < bp.Width; x += 0.5f)
            for (float z = 1f; z < bp.Depth; z += 0.5f)
            {
                if (!bp.IsFree(x, z, clearance) || !bp.Reachable(x, z)) continue;
                float nearest = float.MaxValue;
                foreach (var a in bp.anchors)
                {
                    if (a.kind != kind && !(kind == AnchorKind.PlayerSpawn || kind == AnchorKind.PatrolPoint || kind == AnchorKind.SearchPoint) ) continue;
                    if (a.kind == AnchorKind.DoorSocket || a.kind == AnchorKind.VentEntrance || a.kind == AnchorKind.VentNode) continue;
                    float d = (a.x - x) * (a.x - x) + (a.z - z) * (a.z - z);
                    if (a.kind != kind) d += 4f; // different kinds may sit nearer, but prefer apart
                    if (d < nearest) nearest = d;
                }
                if (nearest == float.MaxValue) nearest = 0f;
                // A tiny bias towards the middle of the room keeps first anchors off the corners.
                float score = nearest - 0.01f * ((x - bp.Width * 0.5f) * (x - bp.Width * 0.5f) + (z - bp.Depth * 0.5f) * (z - bp.Depth * 0.5f));
                if (score > best) { best = score; bx = x; bz = z; }
            }
    }

    // A free point on the side of a box that faces the room's middle.
    bool FreeNear(BoxSpec b, out float x, out float z, float clearance = 0.45f)
    {
        float cx = b.x0 + b.width * 0.5f, cz = b.z0 + b.depth * 0.5f;
        float[] dx = { 0, 0, b.width * 0.5f + 0.8f, -(b.width * 0.5f + 0.8f) };
        float[] dz = { b.depth * 0.5f + 0.8f, -(b.depth * 0.5f + 0.8f), 0, 0 };
        float bestD = float.MaxValue;
        x = z = 0f;
        bool found = false;
        for (int i = 0; i < 4; i++)
        {
            float px = cx + dx[i], pz = cz + dz[i];
            if (!bp.IsFree(px, pz, clearance) || !bp.Reachable(px, pz)) continue;
            float d = (px - bp.Width * 0.5f) * (px - bp.Width * 0.5f) + (pz - bp.Depth * 0.5f) * (pz - bp.Depth * 0.5f);
            if (d < bestD) { bestD = d; x = px; z = pz; found = true; }
        }
        return found;
    }
}

/// <summary>Looks definitions up in the default catalogue by id (blueprints take their feature flags from it).</summary>
public static class DefaultRoomCatalogueLookup
{
    static Dictionary<string, RoomSpec> map;

    public static bool TryGet(string id, out RoomSpec spec)
    {
        if (map == null)
        {
            var m = new Dictionary<string, RoomSpec>();
            foreach (var s in DefaultRoomCatalogue.Create()) m[s.id] = s;
            map = m;
        }
        return map.TryGetValue(id, out spec);
    }
}
