using System;
using System.Collections.Generic;
using System.Linq;

// Data-driven item placement. A profile says where one kind of item plausibly lies: which room categories, on which kind of furniture
// (AnchorSemantic), with what weight. A "displaced" rule is an authored story ("the officer's card was left on a sickbed"), never random scatter.
// Anchors say what they allow (AnchorInfo.allowed / forbidden / canHoldCritical); a candidate must satisfy both its anchor and a rule.

[Serializable]
public sealed class PlacementRule
{
    public RoomCategory room;
    /// <summary>None = any furniture in that room that allows the item.</summary>
    public AnchorSemantic semantic;
    public float weight = 1f;
    /// <summary>An authored displaced-item narrative.</summary>
    public bool displaced;
    /// <summary>The anchor must carry all of these tags (e.g. Officer).</summary>
    public NarrativeTag requireTags;
    public string note = "";

    public PlacementRule() { }
    public PlacementRule(RoomCategory room, AnchorSemantic semantic, float weight, string note = "", bool displaced = false, NarrativeTag requireTags = NarrativeTag.None)
    {
        this.room = room; this.semantic = semantic; this.weight = weight; this.note = note; this.displaced = displaced; this.requireTags = requireTags;
    }
}

[Serializable]
public sealed class PlacementProfile
{
    public string id = "";
    public ItemClass item;
    /// <summary>The escape depends on it: only anchors that may hold critical items are used.</summary>
    public bool critical;
    /// <summary>Optional equipment that may make something easier but is never needed.</summary>
    public bool optional;
    public int minCandidates = 3, maxCandidates = 5, maxPerRoom = 2;
    public List<PlacementRule> rules = new();
    /// <summary>False: the item class has no mechanic yet, so the director does not place it (the profile is data for later).</summary>
    public bool placedByDirector = true;
}

/// <summary>One concrete place an item may rest this round: a room and one of its item anchors, with the rule that allows it.</summary>
public sealed class SpotCandidate
{
    public int node;
    public string anchorId = "";
    public AnchorSemantic semantic;
    public RoomCategory room;
    public float weight;
    public bool displaced;
    public string rule = "";
}

public static class PlacementEngine
{
    /// <summary>Every (room, anchor) pair the profile allows, in a fixed order (node, then anchor order). Weighted by rule x anchor.</summary>
    public static List<SpotCandidate> Candidates(PlacementProfile profile, ShipGraph graph, Func<int, RoomTemplate> templateOf, Func<string, RoomSpec> specOf)
    {
        var list = new List<SpotCandidate>();
        foreach (var node in graph.nodes)
        {
            var tpl = templateOf(node.index);
            var spec = specOf(node.definitionId);
            foreach (var a in tpl.anchors)
            {
                if (a.kind != AnchorKind.Item || !a.Allows(profile.item)) continue;
                if (profile.critical && !a.canHoldCritical) continue;
                if (a.requiredFeature != RoomFeatures.None && (spec == null || (spec.features & a.requiredFeature) != a.requiredFeature)) continue;
                PlacementRule best = null;
                foreach (var r in profile.rules)
                {
                    if (r.room != node.category) continue;
                    if (r.semantic != AnchorSemantic.None && r.semantic != a.semantic) continue;
                    if ((a.tags & r.requireTags) != r.requireTags) continue;
                    if (best == null || r.weight > best.weight) best = r;
                }
                if (best == null || best.weight <= 0f) continue;
                list.Add(new SpotCandidate
                {
                    node = node.index, anchorId = a.id, semantic = a.semantic, room = node.category, weight = best.weight * a.weight, displaced = best.displaced,
                    rule = $"{node.category}/{(best.semantic == AnchorSemantic.None ? "any" : best.semantic.ToString())}{(best.displaced ? " (displaced)" : "")}: {best.note}",
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Picks between min and max distinct anchors by weight, no more than maxPerRoom from one room and, when the pool allows, from at least two rooms.
    /// Prefers anchors in <paramref name="preferred"/> rooms (reachable without any obstacle) when that still leaves enough.
    /// </summary>
    public static List<SpotCandidate> Choose(List<SpotCandidate> pool, PlacementProfile profile, ShipRng rng, ISet<int> preferred = null)
    {
        var work = pool;
        if (preferred != null)
        {
            var near = pool.Where(c => preferred.Contains(c.node)).ToList();
            if (near.Select(c => c.node).Distinct().Count() >= 2 && near.Count >= profile.minCandidates) work = near;
        }
        int want = rng.Range(profile.minCandidates, profile.maxCandidates);
        var left = new List<SpotCandidate>(work);
        var chosen = new List<SpotCandidate>();
        var perRoom = new Dictionary<int, int>();
        while (chosen.Count < want && left.Count > 0)
        {
            int i = rng.Weighted(left.Select(c => c.weight * (perRoom.ContainsKey(c.node) ? 0.5f : 1f)).ToList());
            if (i < 0) break;
            var c = left[i];
            left.RemoveAt(i);
            perRoom.TryGetValue(c.node, out int n);
            if (n >= profile.maxPerRoom) continue;
            perRoom[c.node] = n + 1;
            chosen.Add(c);
        }
        return chosen;
    }
}

/// <summary>The default placement profiles. Editable data: a ScenarioProfile asset holds a copy that designers can change.</summary>
public static class DefaultPlacementProfiles
{
    const NarrativeTag Officer = NarrativeTag.Officer;

    public static List<PlacementProfile> Create()
    {
        var list = new List<PlacementProfile>();
        PlacementProfile P(string id, ItemClass item, bool critical, bool optional, int min, int max, int perRoom, params PlacementRule[] rules)
        {
            var p = new PlacementProfile { id = id, item = item, critical = critical, optional = optional, minCandidates = min, maxCandidates = max, maxPerRoom = perRoom };
            p.rules.AddRange(rules);
            list.Add(p);
            return p;
        }
        var S = AnchorSemantic.None;

        // The security keycard: where a security officer would keep it, then a few authored displaced stories.
        P("keycard", ItemClass.Keycard, true, false, 3, 5, 2,
            new PlacementRule(RoomCategory.Security, AnchorSemantic.CardRack, 3f, "kept on the card rack"),
            new PlacementRule(RoomCategory.Security, AnchorSemantic.SecurityDesk, 2f, "left on a security desk"),
            new PlacementRule(RoomCategory.Security, AnchorSemantic.SecurityLocker, 2f, "locked away in the security lockers"),
            new PlacementRule(RoomCategory.Bridge, AnchorSemantic.CommandTerminal, 1.2f, "an officer's card left at a command terminal", requireTags: Officer),
            new PlacementRule(RoomCategory.OfficersQuarters, S, 1.6f, "an officer's personal quarters", requireTags: Officer),
            new PlacementRule(RoomCategory.Medbay, AnchorSemantic.BedsideSurface, 0.6f, "the officer was taken to the sickbay with the card still on them", displaced: true),
            new PlacementRule(RoomCategory.Medbay, AnchorSemantic.MedicalCabinet, 0.4f, "handed in with a patient's effects", displaced: true),
            new PlacementRule(RoomCategory.CrewQuarters, AnchorSemantic.CrewLocker, 0.7f, "the officer went off shift and dropped the card in a crew locker", displaced: true),
            new PlacementRule(RoomCategory.CrewQuarters, AnchorSemantic.BedsideSurface, 0.5f, "left on a bunk", displaced: true),
            new PlacementRule(RoomCategory.MessHall, AnchorSemantic.CrewDesk, 0.3f, "forgotten at a mess table", displaced: true));

        // The fuse for the generator: engineering stores and benches.
        P("fuse", ItemClass.Fuse, true, false, 3, 5, 2,
            new PlacementRule(RoomCategory.PowerControl, AnchorSemantic.EngineeringRack, 3f, "spares kept in the switchgear"),
            new PlacementRule(RoomCategory.PowerControl, AnchorSemantic.FuseStorage, 3f, "the fuse tray at the power console"),
            new PlacementRule(RoomCategory.Engineering, AnchorSemantic.EngineeringRack, 2.5f, "an engineering rack"),
            new PlacementRule(RoomCategory.Workshop, AnchorSemantic.WorkshopBench, 2f, "left on a workbench mid-repair"),
            new PlacementRule(RoomCategory.Workshop, AnchorSemantic.EquipmentShelving, 1.5f, "workshop tool shelving"),
            new PlacementRule(RoomCategory.AuxiliaryPower, AnchorSemantic.EngineeringRack, 1.5f, "auxiliary power spares"),
            new PlacementRule(RoomCategory.MaintenanceRoom, AnchorSemantic.WorkshopBench, 1f, "a maintenance bench"),
            new PlacementRule(RoomCategory.MaintenanceRoom, AnchorSemantic.GeneralStorage, 0.7f, "maintenance spares"),
            new PlacementRule(RoomCategory.UtilityRoom, AnchorSemantic.EngineeringRack, 1f, "a utility fuse panel"),
            new PlacementRule(RoomCategory.CargoBay, AnchorSemantic.CargoShelving, 0.5f, "stocked as cargo spares", displaced: true));

        // Optional: a spare fuse in the equipment store, an easier way to a fuse. Never needed.
        P("spare_fuse", ItemClass.Fuse, false, true, 1, 2, 2,
            new PlacementRule(RoomCategory.EquipmentStore, AnchorSemantic.EquipmentShelving, 1f, "boxed spares in the equipment store"));

        // Noisemakers: electronics and equipment rooms.
        P("noisemaker", ItemClass.Noisemaker, false, true, 3, 4, 1,
            new PlacementRule(RoomCategory.EquipmentStore, AnchorSemantic.EquipmentShelving, 3f, "stocked in the equipment store"),
            new PlacementRule(RoomCategory.Security, AnchorSemantic.SecurityLocker, 1.5f, "security issue"),
            new PlacementRule(RoomCategory.Workshop, AnchorSemantic.WorkshopBench, 2f, "built on a workbench"),
            new PlacementRule(RoomCategory.Workshop, AnchorSemantic.EquipmentShelving, 1.5f, "workshop shelving"),
            new PlacementRule(RoomCategory.CargoBay, AnchorSemantic.CargoShelving, 1.5f, "cargo equipment"),
            new PlacementRule(RoomCategory.SecondaryCargoHold, AnchorSemantic.CargoShelving, 1.2f, "cargo equipment"),
            new PlacementRule(RoomCategory.CargoBay, AnchorSemantic.EmergencyWallMount, 1f, "emergency kit"));

        // Item classes with no mechanic yet: data only (the director does not place them).
        P("medical", ItemClass.MedicalSupply, false, true, 2, 4, 2,
            new PlacementRule(RoomCategory.Medbay, AnchorSemantic.MedicalCabinet, 3f, "the medbay cabinets"),
            new PlacementRule(RoomCategory.Medbay, AnchorSemantic.BedsideSurface, 1f, "beside a bed"),
            new PlacementRule(RoomCategory.Laboratory, AnchorSemantic.MedicalCabinet, 1.5f, "lab sample stores"),
            new PlacementRule(RoomCategory.CorridorJunction, AnchorSemantic.EmergencyWallMount, 1f, "emergency kit")).placedByDirector = false;
        P("repair_tool", ItemClass.RepairTool, false, true, 2, 4, 2,
            new PlacementRule(RoomCategory.Workshop, AnchorSemantic.WorkshopBench, 3f, "workshop"),
            new PlacementRule(RoomCategory.Engineering, AnchorSemantic.EngineeringRack, 2f, "engineering"),
            new PlacementRule(RoomCategory.MaintenanceRoom, AnchorSemantic.WorkshopBench, 2f, "maintenance")).placedByDirector = false;
        P("personal", ItemClass.PersonalItem, false, true, 2, 4, 2,
            new PlacementRule(RoomCategory.CrewQuarters, AnchorSemantic.CrewLocker, 3f, "crew lockers"),
            new PlacementRule(RoomCategory.CrewQuarters, AnchorSemantic.BedsideSurface, 2f, "bunks"),
            new PlacementRule(RoomCategory.EmptyOffice, AnchorSemantic.CrewDesk, 1.5f, "an office desk"),
            new PlacementRule(RoomCategory.OfficersQuarters, S, 2f, "officer's quarters")).placedByDirector = false;
        P("general", ItemClass.GeneralEquipment, false, true, 2, 4, 2,
            new PlacementRule(RoomCategory.CargoBay, S, 2f, "cargo"),
            new PlacementRule(RoomCategory.SmallStorage, S, 2f, "storage"),
            new PlacementRule(RoomCategory.EquipmentStore, S, 2f, "equipment store")).placedByDirector = false;
        return list;
    }
}
