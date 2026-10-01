using System.Collections.Generic;
using static GreyMaterial;

/// <summary>
/// The default greybox room prefabs, as pure data: footprints (1 cell = 2 m), sockets, furniture boxes and key anchors. Everything else a
/// definition requires (patrol and search points, vents, items, cameras, hazards, hiding places, door anchors) is added by
/// BlueprintWriter.Finish at free spots. Alien > Procedural Ship > Build Greybox Library turns these into prefabs.
/// Layout rule: no box may cover a socket's boundary cell, so every doorway is clear; a test checks this and the walkability of every room.
/// </summary>
public static class GreyboxBlueprints
{
    const GridSide N = GridSide.North, E = GridSide.East, S = GridSide.South, W = GridSide.West;
    const RoomAcoustics Small = RoomAcoustics.SmallRoom, Medium = RoomAcoustics.MediumRoom, Large = RoomAcoustics.LargeHall, Mach = RoomAcoustics.Machinery;

    const ItemClassMask cK = ItemClassMask.Keycard, cF = ItemClassMask.Fuse, cM = ItemClassMask.MedicalSupply, cT = ItemClassMask.RepairTool, cN = ItemClassMask.Noisemaker,
        cE = ItemClassMask.Electronics, cP = ItemClassMask.PersonalItem, cG = ItemClassMask.GeneralEquipment;
    const AnchorContext Vis = AnchorContext.Visible, Hid = AnchorContext.Concealed, Sto = AnchorContext.InsideStorage, Exp = AnchorContext.Exposed;
    const NarrativeTag TSec = NarrativeTag.Security, TOff = NarrativeTag.Officer, TMed = NarrativeTag.Medical, TCrew = NarrativeTag.Crew, TEng = NarrativeTag.Engineering,
        TEmg = NarrativeTag.Emergency, TCargo = NarrativeTag.Cargo, TCmd = NarrativeTag.Command;

    static BlueprintWriter B(string def, RoomCategory cat, string variant, int sx, int sz, RoomAcoustics ac) => new(def, cat, variant, sx, sz, ac);

    public static List<RoomBlueprint> Create()
    {
        var l = new List<RoomBlueprint>();
        void Add(BlueprintWriter w) => l.Add(w.Finish());

        // ---------- Mandatory ----------

        // Player Start: hypersleep bay. Pods along the north wall, lockers on the south wall, an open floor with four spawn spots.
        Add(B("player_start", RoomCategory.PlayerStart, "a", 6, 4, Medium).Socket(W, 1).Socket(E, 2).Socket(N, 4).Socket(S, 1)
            .Row(Pod, "Sleep Pod", 1.0f, 5.6f, 3, 1.6f, 2.0f, 1.1f, 2.4f, 0f)
            .Row(Locker, "Locker", 5.0f, 0.5f, 3, 0.6f, 0.6f, 1.9f, 1.2f, 0f)
            .Anchor(AnchorKind.PlayerSpawn, 3f, 4f, 0.05f, 90f).Anchor(AnchorKind.PlayerSpawn, 5f, 4f, 0.05f, 90f)
            .Anchor(AnchorKind.PlayerSpawn, 7f, 4f, 0.05f, 90f).Anchor(AnchorKind.PlayerSpawn, 9f, 4f, 0.05f, 90f));

        // Bridge: a wide command room with one entrance, a console arc under the forward windows and two side stations.
        Add(B("bridge", RoomCategory.Bridge, "a", 8, 6, Large).Socket(S, 3)
            .Row(Console, "Command Console", 2.0f, 9.6f, 5, 2.0f, 1.0f, 1.1f, 2.5f, 0f).Sem(AnchorSemantic.CommandTerminal, cK | cE, Exp, 1f, true, TCmd | TOff, ItemClassMask.None, 5)
            .Row(Console, "Port Station", 0.5f, 3.0f, 3, 1.0f, 1.6f, 1.1f, 0f, 2.4f)
            .Row(Console, "Starboard Station", 14.5f, 3.0f, 3, 1.0f, 1.6f, 1.1f, 0f, 2.4f)
            .Box(Table, "Command Chair", 7.4f, 6.0f, 1.2f, 1.2f, 0.6f));

        // Security: a counter that splits the room, desks, a control area and weapon lockers.
        Add(B("security", RoomCategory.Security, "a", 6, 5, Medium).Socket(W, 2).Socket(E, 2).Socket(N, 1).Socket(S, 4)
            .Box(Table, "Security Counter", 2.5f, 4.2f, 6.0f, 0.8f, 1.1f).Sem(AnchorSemantic.SecurityDesk, cK | cE | cN, Exp, 1.5f, true, TSec)
            .Row(Table, "Desk", 4.5f, 7.0f, 2, 1.4f, 0.8f, 0.8f, 2.6f, 0f).Sem(AnchorSemantic.SecurityDesk, cK | cE | cN, Exp, 1.2f, true, TSec | TOff, ItemClassMask.None, 2)
            .Box(Console, "Control Station", 9.0f, 7.4f, 2.0f, 1.2f, 1.3f)
            .Row(Locker, "Weapon Locker", 1.0f, 0.5f, 3, 0.9f, 0.6f, 2.0f, 1.0f, 0f).Sem(AnchorSemantic.SecurityLocker, cK | cN | cE, Sto, 1f, true, TSec, ItemClassMask.None, 3)
            .Box(Locker, "Card Rack", 0.4f, 7.0f, 0.25f, 1.0f, 1.0f, 0.9f).Sem(AnchorSemantic.CardRack, cK, Vis, 2f, true, TSec));

        // Medbay: beds in a row, cabinets, a treatment table.
        Add(B("medbay", RoomCategory.Medbay, "a", 6, 4, Medium).Socket(W, 1).Socket(E, 2)
            .Row(Bed, "Bed", 2.0f, 5.8f, 4, 1.0f, 2.0f, 0.6f, 2.0f, 0f).Sem(AnchorSemantic.BedsideSurface, cP | cM | cK, Vis, 0.8f, true, TMed | TCrew, ItemClassMask.None, 4)
            .Row(Locker, "Cabinet", 2.0f, 0.5f, 4, 1.2f, 0.6f, 1.8f, 1.8f, 0f).Sem(AnchorSemantic.MedicalCabinet, cM | cE | cK, Sto, 1.5f, true, TMed, ItemClassMask.None, 4)
            .Box(Table, "Treatment Table", 5.0f, 3.2f, 1.6f, 1.0f, 0.9f));

        // Mess Hall: an open room with two rows of tables and a serving counter.
        Add(B("mess_hall", RoomCategory.MessHall, "a", 7, 5, Medium).Socket(W, 2).Socket(E, 2).Socket(N, 3).Socket(S, 3)
            .Row(Table, "Table", 2.5f, 3.0f, 3, 2.4f, 1.0f, 0.8f, 3.0f, 0f).Sem(AnchorSemantic.CrewDesk, cP | cG, Exp, 0.6f, false, TCrew, ItemClassMask.None, 3)
            .Row(Table, "Table", 2.5f, 6.0f, 3, 2.4f, 1.0f, 0.8f, 3.0f, 0f).Sem(AnchorSemantic.CrewDesk, cP | cG, Exp, 0.6f, false, TCrew, ItemClassMask.None, 3)
            .Box(Table, "Serving Counter", 11.0f, 0.5f, 2.4f, 0.8f, 1.0f).Sem(AnchorSemantic.GeneralStorage, cG | cN, Exp, 0.6f, false, TCrew));

        // Crew Quarters: two layouts. A has bunks on both long walls; B is a squarer room with a locker wall.
        Add(B("crew_quarters", RoomCategory.CrewQuarters, "a", 6, 4, Small).Socket(W, 1).Socket(E, 2)
            .Row(Bed, "Bunk", 2.4f, 0.5f, 3, 1.0f, 2.0f, 0.9f, 2.4f, 0f).Sem(AnchorSemantic.BedsideSurface, cP | cK, Vis, 1f, true, TCrew, ItemClassMask.None, 3)
            .Row(Bed, "Bunk", 2.4f, 5.5f, 3, 1.0f, 2.0f, 0.9f, 2.4f, 0f).Sem(AnchorSemantic.BedsideSurface, cP | cK, Vis, 1f, true, TCrew, ItemClassMask.None, 3)
            .Row(Locker, "Locker", 11.0f, 0.6f, 3, 0.8f, 0.9f, 1.9f, 0f, 1.0f).Sem(AnchorSemantic.CrewLocker, cP | cK | cG, Sto, 1.2f, true, TCrew, ItemClassMask.None, 3));
        Add(B("crew_quarters", RoomCategory.CrewQuarters, "b", 5, 5, Small).Socket(W, 2).Socket(S, 2)
            .Row(Bed, "Bunk", 2.6f, 7.0f, 3, 2.0f, 1.0f, 0.9f, 2.4f, 0f).Sem(AnchorSemantic.BedsideSurface, cP | cK, Vis, 1f, true, TCrew, ItemClassMask.None, 3)
            .Row(Locker, "Locker", 9.0f, 1.0f, 4, 0.6f, 0.8f, 1.9f, 0f, 1.0f).Sem(AnchorSemantic.CrewLocker, cP | cK | cG, Sto, 1.2f, true, TCrew, ItemClassMask.None, 4)
            .Box(Table, "Shared Table", 4.0f, 3.0f, 1.8f, 1.0f, 0.75f).Sem(AnchorSemantic.CrewDesk, cP | cG | cK, Exp, 0.8f, true, TCrew));

        // Engineering: a large machinery room: a tall central reactor, columns, generators and a console bank.
        Add(B("engineering", RoomCategory.Engineering, "a", 9, 7, Mach).Socket(W, 3).Socket(N, 2)
            .Box(Machinery, "Reactor Core", 6.5f, 5.5f, 4.0f, 3.0f, 3.5f)
            .Box(Machinery, "Column", 1.0f, 1.0f, 0.9f, 0.9f, 3.8f).Box(Machinery, "Column", 16.0f, 1.0f, 0.9f, 0.9f, 3.8f)
            .Box(Machinery, "Column", 16.0f, 12.0f, 0.9f, 0.9f, 3.8f).Box(Machinery, "Column", 1.0f, 12.0f, 0.9f, 0.9f, 3.8f)
            .Box(Machinery, "Generator", 12.0f, 2.0f, 3.0f, 2.0f, 2.2f).Box(Machinery, "Generator", 12.0f, 10.0f, 3.0f, 2.0f, 2.2f).Sem(AnchorSemantic.EngineeringRack, cF | cT | cE, Vis, 1.2f, true, TEng, ItemClassMask.None, 2)
            .Box(Console, "Console Bank", 16.3f, 5.0f, 1.2f, 4.0f, 1.2f));

        // Power Control: electrical cabinets and a control console.
        Add(B("power_control", RoomCategory.PowerControl, "a", 5, 4, Mach).Socket(W, 1).Socket(E, 1).Socket(N, 0).Socket(S, 3)
            .Row(Machinery, "Switchgear", 2.6f, 6.0f, 3, 1.8f, 1.2f, 2.4f, 2.4f, 0f).Sem(AnchorSemantic.EngineeringRack, cF | cT | cE, Vis, 1.4f, true, TEng, ItemClassMask.None, 3)
            .Box(Console, "Power Console", 4.0f, 3.0f, 3.0f, 0.9f, 1.1f).Sem(AnchorSemantic.FuseStorage, cF | cT, Exp, 1f, true, TEng));

        // Cargo Bay: a big open hold with shelving islands and crates that form loops to run around.
        Add(B("cargo_bay", RoomCategory.CargoBay, "a", 10, 8, Large).Socket(W, 3).Socket(E, 4).Socket(N, 5).Socket(S, 2)
            .Box(Shelf, "Shelf A", 5.0f, 4.5f, 3.0f, 0.8f, 2.4f).Box(Shelf, "Shelf B", 5.0f, 10.5f, 3.0f, 0.8f, 2.4f)
            .Box(Shelf, "Shelf C", 12.0f, 4.5f, 3.0f, 0.8f, 2.4f).Box(Shelf, "Shelf D", 12.0f, 10.5f, 3.0f, 0.8f, 2.4f)
            .Sem(AnchorSemantic.CargoShelving, cG | cE | cT | cN | cF, Vis, 1.5f, true, TCargo, ItemClassMask.None, 4)
            .Box(Crate, "Crate Stack", 9.0f, 7.0f, 2.0f, 2.0f, 1.6f).Box(Crate, "Crate", 11.5f, 9.0f, 1.2f, 1.2f, 1.2f)
            .Box(Crate, "Crate", 1.0f, 1.0f, 1.6f, 1.6f, 1.5f).Box(Crate, "Crate", 17.0f, 13.0f, 1.6f, 1.6f, 1.5f)
            .Sem(AnchorSemantic.GeneralStorage, cG | cN | cT, Hid, 1f, true, TCargo, ItemClassMask.None, 4)
            .Box(Hazard, "Emergency Kit", 0.4f, 12.0f, 0.25f, 1.2f, 1.0f, 0.9f).Sem(AnchorSemantic.EmergencyWallMount, cN | cM | cT, Vis, 1f, false, TEmg));

        // Workshop: benches, tool shelving and a machine tool.
        Add(B("workshop", RoomCategory.Workshop, "a", 5, 4, Medium).Socket(W, 1).Socket(E, 1).Socket(N, 0)
            .Box(Table, "Workbench", 2.6f, 5.6f, 5.2f, 1.0f, 0.95f).Sem(AnchorSemantic.WorkshopBench, cF | cT | cE | cN | cG, Exp, 1.5f, true, TEng)
            .Row(Shelf, "Tool Shelf", 2.0f, 0.5f, 3, 1.6f, 0.6f, 2.0f, 2.0f, 0f).Sem(AnchorSemantic.EquipmentShelving, cT | cE | cN | cG | cF, Vis, 1f, true, TEng, ItemClassMask.None, 3)
            .Box(Machinery, "Lathe", 8.4f, 6.0f, 1.2f, 1.2f, 1.2f));

        // Escape Pod Bay: two visible pod berths and a launch-control console, one entrance.
        Add(B("escape_pod_bay", RoomCategory.EscapePodBay, "a", 6, 5, Large).Socket(S, 2)
            .Box(Pod, "Pod Hull", 1.5f, 7.2f, 9.0f, 1.4f, 2.6f)
            .Box(Pod, "Boarding Plate", 3.0f, 3.2f, 6.0f, 3.2f, 0.05f) // the pod's open interior: walkable, the EscapePod boarding box stands on it
            .Console(AnchorSemantic.PodLaunchConsole, 9.6f, 5.0f).Console(AnchorSemantic.PodBerth, 6.0f, 4.8f));

        // ---------- Optional specialised ----------

        Add(B("camera_control", RoomCategory.CameraControl, "a", 4, 3, Small).Socket(W, 1).Socket(E, 1)
            .Row(Console, "Monitor Wall", 1.2f, 4.4f, 3, 1.6f, 0.9f, 1.4f, 2.2f, 0f).Box(Table, "Operator Chair", 3.4f, 2.4f, 0.8f, 0.8f, 0.5f));
        Add(B("communications", RoomCategory.Communications, "a", 4, 3, Small).Socket(W, 1).Socket(E, 1)
            .Box(Machinery, "Radio Rack", 2.0f, 4.4f, 1.0f, 1.0f, 2.4f).Box(Machinery, "Radio Rack", 5.0f, 4.4f, 1.0f, 1.0f, 2.4f)
            .Box(Console, "Comms Console", 3.0f, 2.0f, 2.0f, 0.9f, 1.1f));
        Add(B("equipment_store", RoomCategory.EquipmentStore, "a", 3, 3, Small).Socket(S, 1)
            .Box(Shelf, "Shelf", 0.6f, 2.8f, 4.8f, 0.6f, 2.0f).Box(Shelf, "Shelf", 0.6f, 4.6f, 4.8f, 0.6f, 2.0f).Sem(AnchorSemantic.EquipmentShelving, cN | cE | cT | cG | cF, Vis, 1.5f, false, TEng | TEmg, ItemClassMask.None, 2));
        Add(B("server_room", RoomCategory.ServerRoom, "a", 3, 3, Mach).Socket(S, 1)
            .Row(Machinery, "Server Rack", 0.7f, 4.4f, 4, 0.9f, 0.8f, 2.2f, 1.2f, 0f).Box(Console, "Terminal", 2.0f, 2.4f, 1.2f, 0.7f, 1.1f));
        Add(B("air_processing", RoomCategory.AirProcessing, "a", 4, 4, Mach).Socket(W, 1).Socket(E, 2)
            .Box(Machinery, "Fan Unit", 3.0f, 3.0f, 2.4f, 2.4f, 3.0f).Box(Machinery, "Duct Column", 0.8f, 6.0f, 0.8f, 0.8f, 3.8f)
            .Box(Machinery, "Duct Column", 6.4f, 0.8f, 0.8f, 0.8f, 3.8f));
        Add(B("coolant_control", RoomCategory.CoolantControl, "a", 4, 4, Mach).Socket(W, 1).Socket(N, 2)
            .Box(Machinery, "Coolant Tank", 4.8f, 3.6f, 2.0f, 2.0f, 3.2f).Box(Machinery, "Coolant Tank", 1.0f, 5.6f, 1.2f, 1.2f, 2.8f)
            .Box(Console, "Coolant Console", 1.0f, 0.8f, 2.4f, 0.8f, 1.1f));
        Add(B("auxiliary_power", RoomCategory.AuxiliaryPower, "a", 4, 3, Mach).Socket(W, 1).Socket(E, 1)
            .Row(Machinery, "Battery Bank", 2.4f, 4.2f, 2, 2.0f, 1.2f, 2.2f, 2.8f, 0f).Sem(AnchorSemantic.EngineeringRack, cF | cT | cE, Vis, 1f, true, TEng, ItemClassMask.None, 2).Box(Console, "Aux Console", 3.0f, 0.8f, 2.0f, 0.8f, 1.1f));
        Add(B("quarantine", RoomCategory.Quarantine, "a", 4, 3, Small).Socket(W, 1)
            .Box(Bed, "Isolation Bed", 4.0f, 0.8f, 1.0f, 2.0f, 0.6f).Box(Hazard, "Isolation Screen", 3.0f, 0.6f, 0.2f, 3.0f, 2.4f)
            .Box(Console, "Monitor", 6.4f, 4.0f, 1.0f, 1.0f, 1.2f));
        Add(B("laboratory", RoomCategory.Laboratory, "a", 5, 4, Medium).Socket(W, 1).Socket(E, 2)
            .Row(Table, "Lab Bench", 2.6f, 6.0f, 3, 1.8f, 1.0f, 0.95f, 2.4f, 0f).Sem(AnchorSemantic.GeneralStorage, cE | cM | cG, Exp, 0.8f, false, TMed, ItemClassMask.None, 3).Box(Console, "Analyser", 3.0f, 1.0f, 2.0f, 0.8f, 1.2f)
            .Row(Locker, "Sample Cabinet", 7.0f, 0.6f, 2, 1.2f, 0.6f, 1.8f, 1.4f, 0f).Sem(AnchorSemantic.MedicalCabinet, cM | cE, Sto, 1f, false, TMed, ItemClassMask.None, 2));
        Add(B("officers_quarters", RoomCategory.OfficersQuarters, "a", 4, 3, Small).Socket(W, 1)
            .Box(Bed, "Bed", 5.5f, 0.8f, 1.0f, 2.0f, 0.6f).Sem(AnchorSemantic.BedsideSurface, cP | cK, Vis, 2f, true, TOff | TCrew)
            .Box(Table, "Desk", 2.4f, 4.2f, 2.0f, 0.9f, 0.8f).Sem(AnchorSemantic.CrewDesk, cP | cK | cE, Exp, 2f, true, TOff)
            .Box(Locker, "Wardrobe", 0.6f, 0.6f, 0.8f, 0.8f, 2.0f).Sem(AnchorSemantic.CrewLocker, cP | cK, Sto, 2f, true, TOff));
        Add(B("food_storage", RoomCategory.FoodStorage, "a", 4, 3, Small).Socket(W, 1).Socket(E, 1)
            .Row(Shelf, "Food Shelf", 1.0f, 4.4f, 2, 2.6f, 0.6f, 2.0f, 3.2f, 0f).Sem(AnchorSemantic.GeneralStorage, cG, Vis, 0.6f, false, TCrew, ItemClassMask.None, 2)
            .Row(Crate, "Ration Crate", 2.4f, 0.6f, 3, 0.9f, 0.9f, 0.9f, 1.4f, 0f).Sem(AnchorSemantic.GeneralStorage, cG, Hid, 0.5f, false, TCrew, ItemClassMask.None, 3));
        Add(B("waste_processing", RoomCategory.WasteProcessing, "a", 4, 4, Mach).Socket(W, 1).Socket(E, 1)
            .Box(Machinery, "Incinerator", 3.0f, 5.0f, 2.4f, 2.0f, 2.0f).Box(Hazard, "Waste Chute", 1.0f, 0.8f, 1.2f, 1.2f, 1.0f)
            .Box(Crate, "Waste Bin", 4.0f, 0.5f, 1.5f, 1.5f, 1.2f));
        Add(B("observation_room", RoomCategory.ObservationRoom, "a", 4, 4, Small).Socket(S, 1)
            .Box(Console, "Window Rail", 1.5f, 6.3f, 5.0f, 0.7f, 1.0f).Box(Table, "Bench", 3.0f, 3.0f, 2.0f, 1.0f, 0.5f));
        Add(B("secondary_cargo_hold", RoomCategory.SecondaryCargoHold, "a", 6, 5, Medium).Socket(W, 2).Socket(E, 2).Socket(N, 3)
            .Box(Shelf, "Shelf", 3.0f, 6.5f, 6.0f, 0.8f, 2.2f).Sem(AnchorSemantic.CargoShelving, cG | cE | cT | cN | cF, Vis, 1.2f, true, TCargo)
            .Box(Crate, "Crate", 4.0f, 2.0f, 2.0f, 2.0f, 1.6f).Box(Crate, "Crate", 8.0f, 2.5f, 1.6f, 1.6f, 1.2f).Box(Crate, "Crate", 9.5f, 6.0f, 1.6f, 1.6f, 1.4f)
            .Sem(AnchorSemantic.GeneralStorage, cG | cN | cT, Hid, 0.8f, true, TCargo, ItemClassMask.None, 3));

        // ---------- Generic structural ----------

        Add(B("small_storage", RoomCategory.SmallStorage, "a", 3, 3, Small).Socket(S, 1)
            .Box(Shelf, "Shelf", 0.6f, 3.8f, 4.8f, 0.6f, 2.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT | cN | cE | cF, Vis, 1f, true, NarrativeTag.None)
            .Box(Crate, "Crate", 0.7f, 0.7f, 1.2f, 1.2f, 1.2f).Sem(AnchorSemantic.GeneralStorage, cG | cT | cN, Hid, 0.8f, true, NarrativeTag.None));
        Add(B("small_storage", RoomCategory.SmallStorage, "b", 2, 3, Small).Socket(W, 1)
            .Box(Shelf, "Shelf", 2.6f, 0.6f, 0.6f, 4.8f, 2.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT | cN | cE | cF, Vis, 1f, true, NarrativeTag.None));
        Add(B("utility_room", RoomCategory.UtilityRoom, "a", 3, 3, Mach).Socket(W, 1).Socket(E, 1)
            .Box(Machinery, "Pump", 2.4f, 3.6f, 1.2f, 1.2f, 1.5f).Box(Crate, "Spares", 0.7f, 0.7f, 1.0f, 1.0f, 1.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT, Hid, 0.8f, true, TEng));
        Add(B("utility_room", RoomCategory.UtilityRoom, "b", 4, 3, Mach).Socket(W, 1).Socket(S, 2)
            .Box(Machinery, "Fuse Panel", 0.6f, 5.0f, 3.0f, 0.5f, 2.2f).Sem(AnchorSemantic.EngineeringRack, cF | cT | cE, Vis, 1f, true, TEng)
            .Box(Crate, "Spares", 6.4f, 0.7f, 1.2f, 1.2f, 1.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT, Hid, 0.8f, true, TEng));
        Add(B("maintenance_room", RoomCategory.MaintenanceRoom, "a", 4, 3, Medium).Socket(W, 1).Socket(E, 1).Socket(N, 2)
            .Box(Table, "Workbench", 3.0f, 0.6f, 2.0f, 0.8f, 0.9f).Sem(AnchorSemantic.WorkshopBench, cT | cF | cE | cG | cN, Exp, 1f, true, TEng)
            .Row(Locker, "Tool Locker", 0.6f, 4.6f, 2, 0.8f, 0.8f, 1.9f, 1.0f, 0f).Sem(AnchorSemantic.GeneralStorage, cT | cG | cF, Sto, 1f, true, TEng, ItemClassMask.None, 2)
            .Box(Crate, "Spares", 6.0f, 0.7f, 1.2f, 1.2f, 1.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT, Hid, 0.8f, true, TEng));
        Add(B("maintenance_room", RoomCategory.MaintenanceRoom, "b", 3, 4, Medium).Socket(W, 1).Socket(S, 1).Socket(E, 2)
            .Box(Table, "Workbench", 1.0f, 5.8f, 2.4f, 0.8f, 0.9f).Sem(AnchorSemantic.WorkshopBench, cT | cF | cE | cG | cN, Exp, 1f, true, TEng)
            .Box(Locker, "Tool Locker", 4.8f, 0.6f, 0.8f, 0.8f, 1.9f).Sem(AnchorSemantic.GeneralStorage, cT | cG | cF, Sto, 1f, true, TEng));
        Add(B("corridor_junction", RoomCategory.CorridorJunction, "a", 3, 3, Small).Socket(W, 1).Socket(E, 1).Socket(N, 1)
            .Box(Console, "Wall Panel", 0.5f, 0.5f, 0.6f, 0.6f, 1.2f).Sem(AnchorSemantic.EmergencyWallMount, cN | cM | cT, Vis, 1f, false, TEmg));
        Add(B("corridor_junction", RoomCategory.CorridorJunction, "b", 3, 3, Small).Socket(W, 1).Socket(E, 1).Socket(N, 1).Socket(S, 1)
            .Box(Console, "Wall Panel", 0.5f, 0.5f, 0.6f, 0.6f, 1.2f).Sem(AnchorSemantic.EmergencyWallMount, cN | cM | cT, Vis, 1f, false, TEmg).Box(Crate, "Supply Box", 4.9f, 4.9f, 0.7f, 0.7f, 0.8f));
        Add(B("machinery_chamber", RoomCategory.MachineryChamber, "a", 4, 4, Mach).Socket(W, 1).Socket(E, 2)
            .Box(Machinery, "Turbine", 3.0f, 3.0f, 2.0f, 2.0f, 3.5f).Box(Machinery, "Pipe Run", 0.6f, 6.8f, 6.8f, 0.5f, 0.5f, 2.6f));
        Add(B("empty_office", RoomCategory.EmptyOffice, "a", 4, 3, Small).Socket(W, 1).Socket(E, 1)
            .Box(Table, "Desk", 2.6f, 0.8f, 1.6f, 0.8f, 0.8f).Box(Table, "Desk", 2.6f, 4.2f, 1.6f, 0.8f, 0.8f).Sem(AnchorSemantic.CrewDesk, cP | cK | cE, Exp, 1f, true, TCrew | TOff, ItemClassMask.None, 2));
        Add(B("empty_office", RoomCategory.EmptyOffice, "b", 3, 3, Small).Socket(W, 1).Socket(S, 1)
            .Box(Table, "Desk", 2.6f, 4.0f, 2.4f, 0.8f, 0.8f).Sem(AnchorSemantic.CrewDesk, cP | cK | cE, Exp, 1f, true, TCrew | TOff)
            .Box(Locker, "Filing Cabinet", 4.9f, 0.6f, 0.8f, 0.8f, 1.4f).Sem(AnchorSemantic.CrewLocker, cP | cK, Sto, 1f, true, TCrew));
        Add(B("damaged_compartment", RoomCategory.DamagedCompartment, "a", 4, 4, Medium).Socket(W, 1).Socket(E, 2)
            .Box(Hazard, "Fallen Beam", 3.0f, 5.0f, 2.0f, 1.0f, 1.0f).Box(Crate, "Debris", 3.4f, 1.4f, 1.4f, 1.4f, 1.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT, Hid, 0.6f, false, TEmg)
            .Box(Machinery, "Wrecked Unit", 0.8f, 6.0f, 1.2f, 1.2f, 1.8f));
        Add(B("connecting_airlock", RoomCategory.ConnectingAirlock, "a", 2, 3, Small).Socket(S, 1, DoorPolicy.Required, SocketSupport.All).Socket(N, 1, DoorPolicy.Required, SocketSupport.All)
            .Box(Console, "Airlock Panel", 0.4f, 2.6f, 0.4f, 0.8f, 1.1f).Box(Console, "Airlock Panel", 3.2f, 2.6f, 0.4f, 0.8f, 1.1f));
        Add(B("service_tunnel", RoomCategory.ServiceTunnel, "a", 4, 2, RoomAcoustics.Corridor).Socket(W, 0).Socket(E, 0)
            .Box(Machinery, "Overhead Pipe", 0.4f, 3.0f, 7.2f, 0.6f, 0.6f, 2.6f).Box(Crate, "Cable Drum", 3.2f, 2.3f, 1.2f, 1.0f, 1.0f).Sem(AnchorSemantic.GeneralStorage, cG | cT, Hid, 0.6f, false, TEng));
        Add(B("vertical_lobby", RoomCategory.VerticalLobby, "a", 3, 3, Medium).Socket(W, 1).Socket(E, 1).Socket(N, 1).Socket(S, 1)
            .Box(Machinery, "Lift Shaft", 0.5f, 4.2f, 1.2f, 1.2f, 3.5f));
        return l;
    }
}
