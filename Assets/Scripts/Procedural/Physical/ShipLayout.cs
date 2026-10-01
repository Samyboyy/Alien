using System;
using System.Collections.Generic;
using System.Text;

/// <summary>The tunable limits of physical placement. Kept apart from the logical graph's settings.</summary>
[Serializable]
public sealed class PlacementSettings
{
    public float cellSize = GreyboxScale.Cell;
    /// <summary>The generation boundary: everything placed must fit in a box this many cells wide (X, the ship's length) and deep (Z).</summary>
    public int maxExtentX = 112, maxExtentZ = 64;
    public int maxAttempts = 80;
    /// <summary>Independent walkable loops the built ship must have (the logical graph already has at least this many).</summary>
    public int minSpatialLoops = 3;
    /// <summary>Candidate placements applied (and possibly undone) in one attempt before it gives up.</summary>
    public int maxStepsPerAttempt = 500;
    /// <summary>Corridor length for a connection placed with a corridor, in cells (1 cell = 2 m).</summary>
    public int corridorMinCells = 1, corridorMaxCells = 6;
    /// <summary>Longest corridor a room may be placed with, and how far sideways of its parent's doorway it may be put (cells).</summary>
    public int treeCorridorMaxCells = 22, lateralRange = 8;
    /// <summary>Longest corridor a loop-closing connection may use.</summary>
    public int maxLoopCorridorCells = 60;
    /// <summary>A corridor may cross another one at a Cross module, at this extra cost in cells.</summary>
    public bool allowCrossings = true;
    public float crossingCost = 6f;
    /// <summary>Share of connections that join two rooms directly (no corridor) where both sockets allow it.</summary>
    public float directChance = 0.35f;
    public int candidatesPerRoom = 10;
    /// <summary>Candidates tried and rejected for one room (after the cheap checks) before the placer backtracks.</summary>
    public int maxFailedCandidates = 60;
    /// <summary>A socket counts as usable only if a flood fill from the cell outside it reaches this many free cells (it is not a closed pocket).</summary>
    public int openCells = 40;
    /// <summary>Candidates are penalised for each occupied cell within this many cells of the new room (keeps doorways reachable).</summary>
    public int crowdRadius = 3;
    public float crowdWeight = 0.35f;
    /// <summary>The spine (primary route) only moves aft: each room's centre is at least this many cells aft of the previous room's.</summary>
    public int minSpineAdvance = 3;
    /// <summary>How far (cells) a loop-connector room is searched for around the middle of the rooms it joins.</summary>
    public int multiSearchRadius = 14;
    public float lateralWeight = 1.2f, corridorWeight = 0.8f, jitter = 4f;
    /// <summary>The two escape pod bays' centres must be at least this far apart (cells, Manhattan).</summary>
    public int minPodSeparationCells = 22;
    /// <summary>Bridge and Engineering must lie in the forward / aft part of the ship: this share of the length from the bow / stern.</summary>
    public float endZoneShare = 0.3f;

    public PlacementSettings Clone() => (PlacementSettings)MemberwiseClone();

    /// <summary>
    /// The same settings with a wider, less picky search: used for the later attempts of a seed whose early attempts all failed. It never relaxes
    /// a rule (the boundary, separation and validation stay), only how hard the placer looks.
    /// </summary>
    public PlacementSettings Relaxed()
    {
        var r = Clone();
        r.lateralRange += 4;
        r.treeCorridorMaxCells += 10;
        r.crowdWeight *= 0.3f;
        r.jitter *= 2f;
        r.candidatesPerRoom += 6;
        r.maxFailedCandidates *= 2;
        r.maxStepsPerAttempt *= 2;
        r.openCells = Math.Max(10, r.openCells / 2);
        return r;
    }
}

/// <summary>A room placed on the grid: which template, how it is turned (quarter turns clockwise) and where its minimum corner is.</summary>
public sealed class RoomPlacement
{
    public int node;
    public RoomTemplate template;
    public int rot;
    public Int2 origin;
    public int deck;

    public int SizeX { get { Grid.RotatedSize(template.sizeX, template.sizeZ, rot, out int sx, out _); return sx; } }
    public int SizeZ { get { Grid.RotatedSize(template.sizeX, template.sizeZ, rot, out _, out int sz); return sz; } }
    public Int2 Max => new(origin.x + SizeX - 1, origin.z + SizeZ - 1);
    public Int2 Centre2 => new(origin.x * 2 + SizeX - 1, origin.z * 2 + SizeZ - 1); // twice the centre, to stay in integers

    public bool Contains(Int2 c) => c.x >= origin.x && c.z >= origin.z && c.x < origin.x + SizeX && c.z < origin.z + SizeZ;

    public bool Overlaps(RoomPlacement o) =>
        o.deck == deck && origin.x < o.origin.x + o.SizeX && o.origin.x < origin.x + SizeX && origin.z < o.origin.z + o.SizeZ && o.origin.z < origin.z + SizeZ;

    public IEnumerable<Int2> Cells()
    {
        for (int x = 0; x < SizeX; x++)
            for (int z = 0; z < SizeZ; z++)
                yield return new Int2(origin.x + x, origin.z + z);
    }

    public Int2 SocketCell(int socket) => origin + template.SocketCell(socket, rot);
    public GridSide SocketSide(int socket) => template.SocketSide(socket, rot);
    /// <summary>The cell just outside the socket: where a corridor begins, or the other room's boundary cell for a direct connection.</summary>
    public Int2 OutsideCell(int socket) => SocketCell(socket) + Grid.Step(SocketSide(socket));
}

/// <summary>One logical connection, built: either two sockets that touch, or a corridor path between the cells outside the two sockets.</summary>
public sealed class PhysicalConnection
{
    public int edge;
    public string edgeId;
    public int nodeA, socketA, nodeB, socketB;
    public ConnectionMode mode;
    /// <summary>The corridor cells from the cell outside socket A to the cell outside socket B (empty for a direct connection).</summary>
    public readonly List<Int2> cells = new();
}

public readonly struct SealedSocket
{
    public readonly int node, socket;
    public SealedSocket(int node, int socket) { this.node = node; this.socket = socket; }
}

public enum CorridorPieceKind : byte { Straight, Corner, Tee, Cross, DeadEnd }

/// <summary>
/// One 1x1-cell corridor module and how it is turned. At rotation 0: Straight opens N and S, Corner N and E, Tee N, E and S, DeadEnd N.
/// Where two corridors cross, the cell is one Cross module shared by both (<see cref="owners"/> is 2).
/// </summary>
public readonly struct CorridorPiece
{
    public readonly Int2 cell;
    public readonly CorridorPieceKind kind;
    public readonly int rot, mask, connection, owners;
    /// <summary>False when the open sides match no module, or a shared cell is not a clean straight-over-straight crossing.</summary>
    public readonly bool valid;
    public CorridorPiece(Int2 cell, CorridorPieceKind kind, int rot, int mask, int connection, int owners, bool valid)
    {
        this.cell = cell; this.kind = kind; this.rot = rot; this.mask = mask; this.connection = connection; this.owners = owners; this.valid = valid;
    }
}

public static class CorridorMath
{
    static readonly int[] BaseMasks =
    {
        Grid.Bit(GridSide.North) | Grid.Bit(GridSide.South),                                                        // Straight
        Grid.Bit(GridSide.North) | Grid.Bit(GridSide.East),                                                         // Corner
        Grid.Bit(GridSide.North) | Grid.Bit(GridSide.East) | Grid.Bit(GridSide.South),                              // Tee
        15,                                                                                                          // Cross
        Grid.Bit(GridSide.North),                                                                                    // DeadEnd
    };

    /// <summary>The module and rotation that open exactly the sides in <paramref name="mask"/>. False for a mask no module has (no sides).</summary>
    public static bool Classify(int mask, out CorridorPieceKind kind, out int rot)
    {
        for (int k = 0; k < BaseMasks.Length; k++)
            for (int r = 0; r < 4; r++)
                if (Grid.RotateMask(BaseMasks[k], r) == mask) { kind = (CorridorPieceKind)k; rot = r; return true; }
        kind = CorridorPieceKind.Straight;
        rot = 0;
        return false;
    }

    public static int OpenMask(CorridorPieceKind kind, int rot) => Grid.RotateMask(BaseMasks[(int)kind], rot);

    /// <summary>The corridor modules of every corridor connection, one per cell, with the sides each one opens (crossings merged into a Cross).</summary>
    public static List<CorridorPiece> Pieces(ShipLayout layout)
    {
        var index = new Dictionary<Int2, int>();
        var cells = new List<Int2>();
        var masks = new List<int>();
        var owners = new List<int>();
        var first = new List<int>();
        var straight = new List<bool>();
        for (int ci = 0; ci < layout.connections.Count; ci++)
        {
            var c = layout.connections[ci];
            for (int i = 0; i < c.cells.Count; i++)
            {
                int mask = 0;
                mask |= i == 0 ? Grid.Bit(Grid.Opposite(layout.rooms[c.nodeA].SocketSide(c.socketA))) : Bit(c.cells[i], c.cells[i - 1]);
                mask |= i == c.cells.Count - 1 ? Grid.Bit(Grid.Opposite(layout.rooms[c.nodeB].SocketSide(c.socketB))) : Bit(c.cells[i], c.cells[i + 1]);
                bool isStraight = mask == 5 || mask == 10;
                if (index.TryGetValue(c.cells[i], out int k))
                {
                    masks[k] |= mask;
                    owners[k]++;
                    straight[k] &= isStraight;
                }
                else
                {
                    index[c.cells[i]] = cells.Count;
                    cells.Add(c.cells[i]);
                    masks.Add(mask);
                    owners.Add(1);
                    first.Add(ci);
                    straight.Add(isStraight);
                }
            }
        }
        var list = new List<CorridorPiece>();
        for (int k = 0; k < cells.Count; k++)
        {
            bool ok = Classify(masks[k], out var kind, out int rot);
            if (owners[k] > 1) ok &= owners[k] == 2 && masks[k] == 15 && straight[k];
            list.Add(new CorridorPiece(cells[k], kind, rot, masks[k], first[k], owners[k], ok));
        }
        return list;
    }

    // The side of cell a on which the neighbouring cell b lies (0 when they are not neighbours).
    static int Bit(Int2 a, Int2 b)
    {
        var d = b - a;
        if (d.x == 0 && d.z == 1) return Grid.Bit(GridSide.North);
        if (d.x == 1 && d.z == 0) return Grid.Bit(GridSide.East);
        if (d.x == 0 && d.z == -1) return Grid.Bit(GridSide.South);
        if (d.x == -1 && d.z == 0) return Grid.Bit(GridSide.West);
        return 0;
    }
}

/// <summary>A placed ship: where every room of the logical graph is, how every connection is built and which sockets are sealed.</summary>
public sealed class ShipLayout
{
    public int seed;
    public ShipGraph graph;
    public PlacementSettings settings;
    /// <summary>Indexed by logical node index.</summary>
    public readonly List<RoomPlacement> rooms = new();
    /// <summary>Indexed by logical edge index (same order as graph.edges).</summary>
    public readonly List<PhysicalConnection> connections = new();
    public readonly List<SealedSocket> sealedSockets = new();

    public Int2 Min { get; private set; }
    public Int2 Max { get; private set; }
    public int WidthCells => Max.x - Min.x + 1;
    public int DepthCells => Max.z - Min.z + 1;

    public void ComputeBounds()
    {
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        void Add(Int2 c) { x0 = Math.Min(x0, c.x); z0 = Math.Min(z0, c.z); x1 = Math.Max(x1, c.x); z1 = Math.Max(z1, c.z); }
        foreach (var r in rooms) { Add(r.origin); Add(r.Max); }
        foreach (var c in connections) foreach (var cell in c.cells) Add(cell);
        Min = new Int2(x0, z0);
        Max = new Int2(x1, z1);
    }

    public bool SocketUsed(int node, int socket)
    {
        foreach (var c in connections)
            if ((c.nodeA == node && c.socketA == socket) || (c.nodeB == node && c.socketB == socket)) return true;
        return false;
    }

    public int CorridorCellCount { get { int n = 0; foreach (var c in connections) n += c.cells.Count; return n; } }

    /// <summary>
    /// Every occupied cell mapped to its owner: the node index for a room cell, or -(edge index + 1) for a corridor cell (the first corridor when
    /// two cross). <paramref name="clashes"/> lists cells claimed by a room and anything else; shared corridor cells are checked as pieces instead.
    /// </summary>
    public Dictionary<Int2, int> Occupancy(out List<Int2> clashes)
    {
        var map = new Dictionary<Int2, int>();
        clashes = new List<Int2>();
        foreach (var r in rooms) foreach (var c in r.Cells()) if (!map.TryAdd(c, r.node)) clashes.Add(c);
        for (int i = 0; i < connections.Count; i++)
            foreach (var c in connections[i].cells)
                if (!map.TryAdd(c, -(connections[i].edge + 1)) && map[c] >= 0) clashes.Add(c);
        return map;
    }

    /// <summary>Walking length along the logical primary route (corridors and room crossings), in metres: a rough size measure for the ship.</summary>
    public float SpineLengthMetres()
    {
        float total = 0f;
        var route = graph.route;
        for (int i = 0; i + 1 < route.Count; i++)
        {
            var a = rooms[route[i]];
            var b = rooms[route[i + 1]];
            var pa = a.Centre2;
            var pb = b.Centre2;
            total += (Math.Abs(pa.x - pb.x) + Math.Abs(pa.z - pb.z)) * 0.5f * settings.cellSize;
        }
        return total;
    }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"seed {seed}: {rooms.Count} rooms, {connections.Count} connections ({CorridorCellCount} corridor cells), {sealedSockets.Count} sealed sockets, ");
        sb.Append($"{WidthCells * settings.cellSize:0} x {DepthCells * settings.cellSize:0} m, spine about {SpineLengthMetres():0} m");
        return sb.ToString();
    }

    /// <summary>A canonical text of the whole layout; equal strings mean equal layouts (determinism checks).</summary>
    public string Canonical()
    {
        var sb = new StringBuilder();
        foreach (var r in rooms) sb.Append(r.template.id).Append('@').Append(r.origin.x).Append(',').Append(r.origin.z).Append('r').Append(r.rot).Append('d').Append(r.deck).Append(';');
        foreach (var c in connections)
        {
            sb.Append(c.edgeId).Append(':').Append(c.socketA).Append('/').Append(c.socketB).Append((int)c.mode);
            foreach (var cell in c.cells) sb.Append('>').Append(cell.x).Append(',').Append(cell.z);
            sb.Append(';');
        }
        return sb.ToString();
    }

    public string Fingerprint()
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in Canonical()) unchecked { h = (h ^ ch) * 1099511628211UL; }
        return h.ToString("x16");
    }
}
