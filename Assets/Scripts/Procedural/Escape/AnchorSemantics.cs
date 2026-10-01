using System;

// Semantic anchors: what a point in a room IS (a security desk, a card rack, a fuse socket), not where it is. Escape scenario generation and item
// placement choose among anchors by these properties; nothing is inferred from object names. Pure data (no Unity types).

/// <summary>What an anchor physically is. Item-bearing surfaces first, then control consoles.</summary>
public enum AnchorSemantic : byte
{
    None,
    SecurityDesk, SecurityLocker, CardRack, CommandTerminal, MedicalCabinet, BedsideSurface, EngineeringRack, FuseStorage, WorkshopBench,
    EquipmentShelving, CargoShelving, CrewDesk, CrewLocker, EmergencyWallMount, GeneralStorage,
    // Consoles and sockets (objective anchors)
    ObjectiveConsole, FuseSocket, DoorControlConsole, CameraControlConsole, PodLaunchConsole,
    /// <summary>The boarding area of an escape pod: its clearance volume is the interior box the pod counts passengers in.</summary>
    PodBerth,
}

/// <summary>How findable an item at the anchor is.</summary>
public enum AnchorContext : byte
{
    Visible,        // out in the open on a surface or wall mount
    Concealed,      // out of the main sight lines (beside or behind furniture)
    InsideStorage,  // in a locker or cabinet (the greybox shows it standing in the open drawer)
    Exposed,        // on an open work surface in the middle of the room
}

/// <summary>What kind of thing may be put on an anchor. Only Keycard, Fuse and Noisemaker are backed by existing mechanics; the rest are data for later.</summary>
public enum ItemClass : byte { Keycard, Fuse, MedicalSupply, RepairTool, Noisemaker, Electronics, PersonalItem, GeneralEquipment }

[Flags]
public enum ItemClassMask : ushort
{
    None = 0,
    Keycard = 1 << ItemClass.Keycard,
    Fuse = 1 << ItemClass.Fuse,
    MedicalSupply = 1 << ItemClass.MedicalSupply,
    RepairTool = 1 << ItemClass.RepairTool,
    Noisemaker = 1 << ItemClass.Noisemaker,
    Electronics = 1 << ItemClass.Electronics,
    PersonalItem = 1 << ItemClass.PersonalItem,
    GeneralEquipment = 1 << ItemClass.GeneralEquipment,
    All = 0xFF,
}

/// <summary>Optional story flavour of an anchor ("an officer's desk"); placement rules may require or favour tags.</summary>
[Flags]
public enum NarrativeTag : ushort
{
    None = 0, Officer = 1, Security = 2, Medical = 4, Crew = 8, Engineering = 16, Emergency = 32, Cargo = 64, Command = 128,
}

public static class ItemClasses
{
    public static ItemClassMask Mask(ItemClass c) => (ItemClassMask)(1 << (int)c);
}

/// <summary>
/// One anchor of a room template. Positions are room-local metres (origin at the footprint's minimum corner, y above the floor). For item anchors
/// y is where an item's base rests and the clearance volume stands on it; the item must fit inside that volume without touching other geometry.
/// </summary>
public class AnchorInfo
{
    public AnchorKind kind;
    public AnchorSemantic semantic;
    /// <summary>Stable within the room template.</summary>
    public string id = "";
    public float x, y, z, yaw;
    public AnchorContext context;
    public ItemClassMask allowed, forbidden;
    /// <summary>Relative likelihood among the anchors a placement rule accepts.</summary>
    public float weight = 1f;
    /// <summary>Free volume (metres) above and around the point that an item or console needs.</summary>
    public float clearWidth = 0.4f, clearHeight = 0.35f, clearDepth = 0.4f;
    /// <summary>The room definition must support this feature (Objectives, Cameras, ...) for the anchor to be used.</summary>
    public RoomFeatures requiredFeature;
    /// <summary>May hold an item the escape depends on (keycard, fuse). Anchors in obscure spots are for optional items only.</summary>
    public bool canHoldCritical;
    public NarrativeTag tags;
    /// <summary>For a door anchor: its socket.</summary>
    public string socketId = "";

    public bool Allows(ItemClass c) => (allowed & ItemClasses.Mask(c)) != 0 && (forbidden & ItemClasses.Mask(c)) == 0;

    public AnchorInfo CloneInfo() => (AnchorInfo)MemberwiseClone();
}
