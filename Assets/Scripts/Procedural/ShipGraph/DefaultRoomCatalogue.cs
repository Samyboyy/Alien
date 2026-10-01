using System.Collections.Generic;

/// <summary>
/// The default room definitions, in code, so the generator and its tests work without any assets. Alien > Procedural Ship > Create Or Repair
/// Room Definitions writes these into RoomDefinition assets once (never overwriting an existing one); from then on the assets are the source
/// and every rule here can be edited there. Ids are stable snake_case strings and must never be renamed once assets exist.
/// </summary>
public static class DefaultRoomCatalogue
{
    const RoomFeatures Basic = RoomFeatures.Doors | RoomFeatures.Vents | RoomFeatures.ItemAnchors;
    const RoomFeatures Rich = Basic | RoomFeatures.Objectives | RoomFeatures.Cameras;
    const GraphRoles Leaf = GraphRoles.Terminal;
    const GraphRoles LeafOrPass = GraphRoles.Terminal | GraphRoles.Thoroughfare;
    const GraphRoles Route = GraphRoles.Thoroughfare | GraphRoles.Junction;
    const GraphRoles Pass = GraphRoles.Thoroughfare;
    const GraphRoles Any = GraphRoles.Unrestricted;

    static RoomCategory[] C(params RoomCategory[] c) => c;

    static RoomSpec Room(string id, string name, RoomCategory cat, RoomTier tier, SectorMask sectors, int min, int max, GraphRoles roles, int minConn, int maxConn,
        float weight = 1f, RoomFeatures features = Basic)
    {
        return new RoomSpec
        {
            id = id, displayName = name, category = cat, tier = tier, sectors = sectors, mandatory = tier == RoomTier.Mandatory,
            minCount = min, maxCount = max, roles = roles, minConnections = minConn, maxConnections = maxConn,
            mayBeDeadEnd = (roles & GraphRoles.Terminal) != 0, weight = weight, features = features, deck = 0,
        };
    }

    public static List<RoomSpec> Create()
    {
        const RoomTier M = RoomTier.Mandatory, S = RoomTier.Specialised, X = RoomTier.Structural;
        var list = new List<RoomSpec>();

        // ---------- Mandatory ----------
        var start = Room("player_start", "Player Start", RoomCategory.PlayerStart, M, SectorMask.Crew, 1, 1, Route, 2, 4);
        start.forbiddenNeighbours = C(RoomCategory.EscapePodBay);
        start.preferredNeighbours = C(RoomCategory.CrewQuarters, RoomCategory.MessHall);
        list.Add(start);

        var bridge = Room("bridge", "Bridge", RoomCategory.Bridge, M, SectorMask.Forward, 1, 1, Leaf, 1, 1, 1f, RoomFeatures.All);
        bridge.minSpawnDistance = 5;
        bridge.preferredNeighbours = C(RoomCategory.Security, RoomCategory.Communications);
        list.Add(bridge);

        var security = Room("security", "Security", RoomCategory.Security, M, SectorMask.Forward | SectorMask.Central, 1, 1, Any, 1, 4, 1f, Rich);
        security.preferredNeighbours = C(RoomCategory.Bridge, RoomCategory.CameraControl);
        security.closeTo = C(RoomCategory.Bridge);
        security.closeToDistance = 3;
        list.Add(security);

        var medbay = Room("medbay", "Medbay", RoomCategory.Medbay, M, SectorMask.Central | SectorMask.Crew, 1, 1, LeafOrPass, 1, 2, 1f, Rich);
        medbay.preferredNeighbours = C(RoomCategory.Quarantine, RoomCategory.Laboratory, RoomCategory.CrewQuarters);
        medbay.forbiddenNeighbours = C(RoomCategory.WasteProcessing);
        list.Add(medbay);

        var mess = Room("mess_hall", "Mess Hall", RoomCategory.MessHall, M, SectorMask.Crew, 1, 1, Route, 2, 4);
        mess.preferredNeighbours = C(RoomCategory.CrewQuarters, RoomCategory.FoodStorage);
        mess.forbiddenNeighbours = C(RoomCategory.WasteProcessing);
        mess.closeTo = C(RoomCategory.CrewQuarters);
        mess.closeToDistance = 2;
        list.Add(mess);

        var quarters = Room("crew_quarters", "Crew Quarters", RoomCategory.CrewQuarters, M, SectorMask.Crew, 1, 2, LeafOrPass, 1, 2);
        quarters.preferredNeighbours = C(RoomCategory.MessHall, RoomCategory.OfficersQuarters);
        quarters.forbiddenNeighbours = C(RoomCategory.WasteProcessing);
        list.Add(quarters);

        var engineering = Room("engineering", "Engineering", RoomCategory.Engineering, M, SectorMask.Industrial, 1, 1, LeafOrPass, 1, 2, 1f, RoomFeatures.All);
        engineering.minSpawnDistance = 3;
        engineering.preferredNeighbours = C(RoomCategory.PowerControl, RoomCategory.CoolantControl);
        engineering.forbiddenNeighbours = C(RoomCategory.PlayerStart);
        list.Add(engineering);

        var power = Room("power_control", "Power Control", RoomCategory.PowerControl, M, SectorMask.Industrial, 1, 1, Any, 1, 4, 1f, Rich);
        power.preferredNeighbours = C(RoomCategory.Engineering, RoomCategory.AuxiliaryPower);
        power.closeTo = C(RoomCategory.Engineering);
        power.closeToDistance = 2;
        power.closeToStrict = true;
        list.Add(power);

        var cargo = Room("cargo_bay", "Cargo Bay", RoomCategory.CargoBay, M, SectorMask.Industrial, 1, 1, Route, 2, 4, 1f, Rich);
        cargo.preferredNeighbours = C(RoomCategory.Workshop, RoomCategory.SecondaryCargoHold, RoomCategory.EquipmentStore);
        list.Add(cargo);

        var workshop = Room("workshop", "Workshop", RoomCategory.Workshop, M, SectorMask.Industrial, 1, 1, Any, 1, 3);
        workshop.preferredNeighbours = C(RoomCategory.CargoBay, RoomCategory.Engineering, RoomCategory.MaintenanceRoom);
        workshop.closeTo = C(RoomCategory.CargoBay, RoomCategory.Engineering, RoomCategory.MaintenanceRoom);
        workshop.closeToDistance = 2;
        workshop.closeToStrict = true;
        list.Add(workshop);

        var pod = Room("escape_pod_bay", "Escape Pod Bay", RoomCategory.EscapePodBay, M, SectorMask.Central | SectorMask.Industrial, 2, 2, Leaf, 1, 1, 1f,
            RoomFeatures.Doors | RoomFeatures.Objectives | RoomFeatures.ItemAnchors);
        pod.minSpawnDistance = 3;
        pod.spreadAcrossSectors = true;
        pod.forbiddenNeighbours = C(RoomCategory.PlayerStart, RoomCategory.EscapePodBay);
        list.Add(pod);

        // ---------- Optional specialised ----------
        RoomSpec Spec(string id, string name, RoomCategory cat, SectorMask sectors, GraphRoles roles, int maxConn, float weight, params RoomCategory[] preferred)
        {
            var r = Room(id, name, cat, S, sectors, 0, 1, roles, 1, maxConn, weight, Rich);
            r.preferredNeighbours = preferred;
            list.Add(r);
            return r;
        }
        Spec("camera_control", "Camera Control", RoomCategory.CameraControl, SectorMask.Forward | SectorMask.Central, LeafOrPass, 2, 1f, RoomCategory.Security);
        Spec("communications", "Communications", RoomCategory.Communications, SectorMask.Forward, LeafOrPass, 2, 1f, RoomCategory.Bridge, RoomCategory.ServerRoom);
        Spec("equipment_store", "Equipment Store", RoomCategory.EquipmentStore, SectorMask.Central | SectorMask.Industrial, Leaf, 1, 1f, RoomCategory.CargoBay, RoomCategory.Workshop);
        Spec("server_room", "Server Room", RoomCategory.ServerRoom, SectorMask.Forward | SectorMask.Central, Leaf, 1, 0.8f, RoomCategory.Communications, RoomCategory.CameraControl);
        Spec("air_processing", "Air Processing", RoomCategory.AirProcessing, SectorMask.Central | SectorMask.Crew | SectorMask.Industrial, LeafOrPass, 2, 1f, RoomCategory.MaintenanceRoom);
        Spec("coolant_control", "Coolant Control", RoomCategory.CoolantControl, SectorMask.Industrial, LeafOrPass, 2, 0.9f, RoomCategory.Engineering, RoomCategory.MachineryChamber);
        Spec("auxiliary_power", "Auxiliary Power", RoomCategory.AuxiliaryPower, SectorMask.Central | SectorMask.Industrial, LeafOrPass, 2, 0.9f, RoomCategory.PowerControl);
        var quarantine = Spec("quarantine", "Quarantine", RoomCategory.Quarantine, SectorMask.Central | SectorMask.Crew, Leaf, 1, 0.7f, RoomCategory.Medbay, RoomCategory.Laboratory);
        quarantine.forbiddenNeighbours = C(RoomCategory.MessHall, RoomCategory.FoodStorage);
        Spec("laboratory", "Laboratory", RoomCategory.Laboratory, SectorMask.Forward | SectorMask.Central, LeafOrPass, 2, 0.9f, RoomCategory.Medbay, RoomCategory.Quarantine);
        Spec("officers_quarters", "Officer's Quarters", RoomCategory.OfficersQuarters, SectorMask.Forward | SectorMask.Crew, Leaf, 1, 0.8f, RoomCategory.CrewQuarters, RoomCategory.Bridge);
        var food = Spec("food_storage", "Food Storage", RoomCategory.FoodStorage, SectorMask.Crew, LeafOrPass, 2, 0.9f, RoomCategory.MessHall);
        food.forbiddenNeighbours = C(RoomCategory.WasteProcessing, RoomCategory.Quarantine);
        var waste = Spec("waste_processing", "Waste Processing", RoomCategory.WasteProcessing, SectorMask.Industrial, LeafOrPass, 2, 0.8f, RoomCategory.MachineryChamber);
        waste.forbiddenNeighbours = C(RoomCategory.FoodStorage, RoomCategory.MessHall, RoomCategory.Medbay, RoomCategory.CrewQuarters);
        var observation = Spec("observation_room", "Observation Room", RoomCategory.ObservationRoom, SectorMask.Forward | SectorMask.Crew, Leaf, 1, 0.7f);
        observation.minSpawnDistance = 2;
        Spec("secondary_cargo_hold", "Secondary Cargo Hold", RoomCategory.SecondaryCargoHold, SectorMask.Central | SectorMask.Industrial, Any, 3, 0.8f, RoomCategory.CargoBay);

        // ---------- Generic structural ----------
        RoomSpec Filler(string id, string name, RoomCategory cat, SectorMask sectors, GraphRoles roles, int minConn, int maxConn, int max, float weight)
        {
            var r = Room(id, name, cat, X, sectors, 0, max, roles, minConn, maxConn, weight);
            list.Add(r);
            return r;
        }
        Filler("small_storage", "Small Storage", RoomCategory.SmallStorage, SectorMask.Any, Leaf, 1, 1, 3, 0.8f);
        Filler("utility_room", "Utility Room", RoomCategory.UtilityRoom, SectorMask.Any, LeafOrPass, 1, 2, 3, 1f);
        Filler("maintenance_room", "Maintenance Room", RoomCategory.MaintenanceRoom, SectorMask.Any, Any, 1, 3, 3, 1.2f);
        Filler("corridor_junction", "Corridor Junction", RoomCategory.CorridorJunction, SectorMask.Any, Route, 2, 4, 6, 2f);
        Filler("machinery_chamber", "Machinery Chamber", RoomCategory.MachineryChamber, SectorMask.Central | SectorMask.Industrial, LeafOrPass, 1, 2, 2, 0.8f);
        Filler("empty_office", "Empty Office", RoomCategory.EmptyOffice, SectorMask.Forward | SectorMask.Central | SectorMask.Crew, LeafOrPass, 1, 2, 2, 0.7f);
        var damaged = Filler("damaged_compartment", "Damaged Compartment", RoomCategory.DamagedCompartment, SectorMask.Any, LeafOrPass, 1, 2, 2, 0.5f);
        damaged.features |= RoomFeatures.Hazards;
        Filler("connecting_airlock", "Connecting Airlock", RoomCategory.ConnectingAirlock, SectorMask.Any, Pass, 2, 2, 2, 0.6f);
        Filler("service_tunnel", "Service Tunnel", RoomCategory.ServiceTunnel, SectorMask.Any, Pass, 2, 2, 3, 1f);
        var lobby = Filler("vertical_lobby", "Future Vertical-Connection Lobby", RoomCategory.VerticalLobby, SectorMask.Central | SectorMask.Crew, Route, 2, 4, 1, 0.3f);
        lobby.deck = -1; // a future lift or stair lobby may sit on any deck

        return list;
    }
}
