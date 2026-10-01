using System.Collections.Generic;

// Pure data for the procedural ship graph (no Unity types, so everything here is testable outside the editor: Editor/Tests/ShipGraphTests.cs).
// Layers, kept apart on purpose:
//   1. room definitions   - RoomSpec (pure) / RoomDefinition (ScriptableObject that converts to a RoomSpec)
//   2. the logical graph  - ShipGraph (rooms, connections, the primary route; sectors and decks only, never world coordinates)
//   3. validation         - ShipGraphValidator
//   4. physical placement and 5. challenge population are future layers and live elsewhere.

/// <summary>Ship regions, in order from the bow to the stern. The order matters: the primary route runs through them in this order.</summary>
public enum ShipSector : byte { Forward, Central, Crew, Industrial }

[System.Flags]
public enum SectorMask : byte
{
    None = 0,
    Forward = 1 << ShipSector.Forward,
    Central = 1 << ShipSector.Central,
    Crew = 1 << ShipSector.Crew,
    Industrial = 1 << ShipSector.Industrial,
    Any = Forward | Central | Crew | Industrial,
}

/// <summary>Every kind of room. Values are appended, never renumbered (definition assets store them).</summary>
public enum RoomCategory : byte
{
    // Mandatory
    PlayerStart, Bridge, Security, Medbay, MessHall, CrewQuarters, Engineering, PowerControl, CargoBay, Workshop, EscapePodBay,
    // Optional specialised
    CameraControl, Communications, EquipmentStore, ServerRoom, AirProcessing, CoolantControl, AuxiliaryPower, Quarantine, Laboratory,
    OfficersQuarters, FoodStorage, WasteProcessing, ObservationRoom, SecondaryCargoHold,
    // Generic structural
    SmallStorage, UtilityRoom, MaintenanceRoom, CorridorJunction, MachineryChamber, EmptyOffice, DamagedCompartment, ConnectingAirlock,
    ServiceTunnel, VerticalLobby,
    // Appended later (values are stored in assets: never reorder)
    EscapeAccess,
}

public enum RoomTier : byte { Mandatory, Specialised, Structural }

/// <summary>
/// The part a room may play in the graph, derived from its number of connections: one is a Terminal (a dead end), two a Thoroughfare,
/// three or more a Junction.
/// </summary>
[System.Flags]
public enum GraphRoles : byte
{
    None = 0,
    Terminal = 1,
    Thoroughfare = 2,
    Junction = 4,
    Unrestricted = Terminal | Thoroughfare | Junction,
}

/// <summary>What a room can later carry. Not used by the graph itself; it is for the placement and population layers.</summary>
[System.Flags]
public enum RoomFeatures : byte
{
    None = 0,
    Doors = 1,
    Vents = 2,
    Objectives = 4,
    Cameras = 8,
    Hazards = 16,
    ItemAnchors = 32,
    All = Doors | Vents | Objectives | Cameras | Hazards | ItemAnchors,
}

/// <summary>Where a room likes to hang, used as a scoring preference when side rooms are placed (never a hard rule).</summary>
[System.Flags]
public enum PlacementPreference : byte
{
    None = 0,
    /// <summary>Directly off the primary route (a major transit way).</summary>
    Transit = 1,
    /// <summary>Off the route on a side branch (a useful detour, not on the way).</summary>
    SideBranch = 2,
    /// <summary>Near the middle of the ship rather than at either end.</summary>
    Central = 4,
}

/// <summary>
/// One room definition, as the generator and validator use it. A RoomDefinition asset converts to this, and DefaultRoomCatalogue builds the
/// default set from code (so tests need no assets). -1 means "no limit" for the optional distance limits and "any deck" for the deck.
/// </summary>
public sealed class RoomSpec
{
    public string id = "";
    public string displayName = "";
    public RoomCategory category;
    public RoomTier tier;
    public SectorMask sectors = SectorMask.Any;
    public bool mandatory;
    public int minCount, maxCount = 1;
    public GraphRoles roles = GraphRoles.Unrestricted;
    public int minConnections = 1, maxConnections = 4;
    public RoomCategory[] preferredNeighbours = new RoomCategory[0];
    public RoomCategory[] forbiddenNeighbours = new RoomCategory[0];
    public int minSpawnDistance = -1, maxSpawnDistance = -1;
    public bool mayBeDeadEnd = true;
    public RoomFeatures features = RoomFeatures.Doors | RoomFeatures.Vents | RoomFeatures.ItemAnchors;
    public float weight = 1f;
    public int deck = -1;
    /// <summary>Rooms of these categories should be within <see cref="closeToDistance"/> connections (the nearest one counts).</summary>
    public RoomCategory[] closeTo = new RoomCategory[0];
    public int closeToDistance = 2;
    /// <summary>True: being too far is an error. False: a warning (a preference).</summary>
    public bool closeToStrict;
    /// <summary>Several rooms of this definition go to different sectors where the definition allows it (escape pods, crew quarters).</summary>
    public bool spreadAcrossSectors;
    /// <summary>When not empty, every neighbour of this room must be one of these categories (an escape pod bay only opens onto its access room).</summary>
    public RoomCategory[] onlyNeighbours = new RoomCategory[0];
    /// <summary>This room must have at least one neighbour of each of these categories (an escape access room leads to its pod bay).</summary>
    public RoomCategory[] requiredNeighbours = new RoomCategory[0];
    /// <summary>The room is a functional connection between decks (a lift or stair lobby). Single-deck ships never include it.</summary>
    public bool requiresVerticalConnection;
    public PlacementPreference placement;

    public bool Allows(GraphRoles role) => (roles & role) != 0;
    public bool AllowsSector(ShipSector s) => (sectors & ToMask(s)) != 0;
    public bool CanBeOnRoute => (Allows(GraphRoles.Thoroughfare) || Allows(GraphRoles.Junction)) && maxConnections >= 2;
    public bool CanBeLeaf => Allows(GraphRoles.Terminal) && mayBeDeadEnd;

    public static SectorMask ToMask(ShipSector s) => (SectorMask)(1 << (int)s);

    public static GraphRoles RoleForDegree(int degree) =>
        degree <= 1 ? GraphRoles.Terminal : degree == 2 ? GraphRoles.Thoroughfare : GraphRoles.Junction;

    public RoomSpec Clone() => (RoomSpec)MemberwiseClone();
}

/// <summary>Tunable whole-ship rules. Kept outside the room definitions, which only describe single rooms. Serializable so a profile asset can hold it.</summary>
[System.Serializable]
public sealed class ShipGraphSettings
{
    public int minRooms = 24, maxRooms = 30;
    public int deckCount = 1;
    /// <summary>Most connections any room may have (the future room prefabs' doorway sockets).</summary>
    public int maxDegree = 4;
    public int minLoops = 3;
    public int minRouteLength = 10;
    public int minRoutePerSector = 2;
    public int minNonTerminalRooms = 12;
    public int minSideBranches = 4;
    public int maxBranchDepth = 2;
    public int minSpecialised = 4, maxSpecialised = 8;
    public int minEscapePodSeparation = 5;
    public int minBridgeEngineeringDistance = 7;
    /// <summary>The Bridge counts as one of the furthest rooms from the spawn when at most this many rooms are further away.</summary>
    public int bridgeMaxRoomsFurther = 3;
    /// <summary>No single room may cut a whole sector off from the rest of the ship.</summary>
    public bool requireSectorRedundancy = true;
    /// <summary>Loop spans along the primary route, in route steps.</summary>
    public int minLoopSpan = 3, maxLoopSpan = 6;
    /// <summary>Share of each sector's rooms that go on the primary route.</summary>
    public float routeShare = 0.5f;
    public int maxAttempts = 200;

    public ShipGraphSettings Clone() => (ShipGraphSettings)MemberwiseClone();
}

public enum IssueSeverity : byte { Warning, Error }

public enum GraphIssueCode : byte
{
    RoomCount, UnknownDefinition, OccurrenceRange, DuplicateNodeId, BadNodeId, DuplicateEdgeId, BadEdge, SelfEdge, DuplicateEdge, Disconnected,
    RoleNotPermitted, ConnectionRange, MaxDegreeExceeded, DeadEndNotAllowed, ForbiddenNeighbour, SpawnDistance, TooFewLoops, SectorNotPermitted,
    DeckNotPermitted, EscapePodSeparation, EscapePodSameBranch, SpawnNextToEscapePod, BridgeNotForward, EngineeringNotAft, BridgeEngineeringTooClose,
    BridgeNotFurthest, ProximityRequired, ProximityPreferred, SectorCutVertex, RouteInvalid, TooFewSideBranches, TooFewNonTerminal,
    GenericDeadEnds, SectorEmpty, BranchTooDeep,
    // Appended later
    NeighbourNotAllowed, RequiredNeighbourMissing, RequiresMultiDeck,
}

/// <summary>One validation finding, with the nodes it concerns (indices into the graph).</summary>
public sealed class GraphIssue
{
    public IssueSeverity severity;
    public GraphIssueCode code;
    public string message;
    public int[] nodes;

    public override string ToString() => $"{(severity == IssueSeverity.Error ? "ERROR" : "warning")} {code}: {message}";
}

public sealed class GraphValidationReport
{
    public readonly List<GraphIssue> issues = new();
    public int loops, routeLength, sideBranches, nonTerminal, deadEnds, maxDistance;

    public bool Valid
    {
        get { foreach (var i in issues) if (i.severity == IssueSeverity.Error) return false; return true; }
    }

    public int ErrorCount { get { int n = 0; foreach (var i in issues) if (i.severity == IssueSeverity.Error) n++; return n; } }
    public int WarningCount => issues.Count - ErrorCount;

    public bool Has(GraphIssueCode code)
    {
        foreach (var i in issues) if (i.code == code) return true;
        return false;
    }
}
