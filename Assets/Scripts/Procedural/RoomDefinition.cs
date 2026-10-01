using UnityEngine;

/// <summary>
/// One room definition as an editable asset. Holds design data only: the generator never reads it directly but through <see cref="ToSpec"/>,
/// so the pure generator and its tests need no assets. The id is the stable identity (generated room ids are built from it): never rename it
/// once graphs or content refer to it. Created by Alien > Procedural Ship > Create Or Repair Room Definitions.
/// </summary>
[CreateAssetMenu(menuName = "Alien/Procedural Ship/Room Definition", fileName = "RoomDefinition")]
public class RoomDefinition : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Stable identifier (lower case, digits and underscores). Never rename once used.")] public string id;
    public string displayName;
    public RoomCategory category;
    public RoomTier tier;

    [Header("Occurrence")]
    [Tooltip("Mandatory rooms always appear at least once")] public bool mandatory;
    [Min(0)] public int minCount;
    [Min(0)] public int maxCount = 1;
    [Tooltip("Relative chance when optional rooms are picked")] [Min(0f)] public float weight = 1f;

    [Header("Placement")]
    public SectorMask sectors = SectorMask.Any;
    [Tooltip("Deck index, or -1 for any deck. Version 1 generates a single deck (0).")] public int deck;
    [Tooltip("Several rooms of this definition go to different sectors where allowed")] public bool spreadAcrossSectors;

    [Header("Graph")]
    [Tooltip("Terminal = 1 connection, Thoroughfare = 2, Junction = 3 or more")] public GraphRoles permittedRoles = GraphRoles.Unrestricted;
    [Min(1)] public int minConnections = 1;
    [Min(1)] public int maxConnections = 4;
    public bool mayBeDeadEnd = true;
    public RoomCategory[] preferredNeighbours = new RoomCategory[0];
    public RoomCategory[] forbiddenNeighbours = new RoomCategory[0];
    [Tooltip("Connections from the player spawn; -1 = no limit")] public int minSpawnDistance = -1;
    public int maxSpawnDistance = -1;
    [Tooltip("Should be within closeToDistance connections of one of these")] public RoomCategory[] closeTo = new RoomCategory[0];
    [Min(1)] public int closeToDistance = 2;
    [Tooltip("On: being further is an error. Off: a warning.")] public bool closeToStrict;
    [Tooltip("When not empty, every neighbour must be one of these (an escape pod bay only opens onto its escape access)")] public RoomCategory[] onlyNeighbours = new RoomCategory[0];
    [Tooltip("Must have at least one neighbour of each of these (an escape access leads to its pod bay)")] public RoomCategory[] requiredNeighbours = new RoomCategory[0];
    [Tooltip("Where the room likes to hang: a scoring preference, never a hard rule")] public PlacementPreference placement;

    [Header("Decks")]
    [Tooltip("A functional connection between decks (lift or stair lobby). Never generated on a single-deck ship.")] public bool requiresVerticalConnection;

    [Header("Capabilities (for the future placement and population layers)")]
    public RoomFeatures features = RoomFeatures.Doors | RoomFeatures.Vents | RoomFeatures.ItemAnchors;

    [Header("Future")]
    [Tooltip("The modular room prefab, once it exists. Not used by the logical generator.")] public GameObject roomPrefab;

    public RoomSpec ToSpec() => new()
    {
        id = id ?? "", displayName = string.IsNullOrEmpty(displayName) ? id : displayName, category = category, tier = tier, sectors = sectors,
        mandatory = mandatory, minCount = minCount, maxCount = maxCount, roles = permittedRoles, minConnections = minConnections, maxConnections = maxConnections,
        preferredNeighbours = preferredNeighbours ?? new RoomCategory[0], forbiddenNeighbours = forbiddenNeighbours ?? new RoomCategory[0],
        minSpawnDistance = minSpawnDistance, maxSpawnDistance = maxSpawnDistance, mayBeDeadEnd = mayBeDeadEnd, features = features, weight = weight, deck = deck,
        closeTo = closeTo ?? new RoomCategory[0], closeToDistance = closeToDistance, closeToStrict = closeToStrict, spreadAcrossSectors = spreadAcrossSectors,
        onlyNeighbours = onlyNeighbours ?? new RoomCategory[0], requiredNeighbours = requiredNeighbours ?? new RoomCategory[0], placement = placement,
        requiresVerticalConnection = requiresVerticalConnection,
    };

    /// <summary>Copies every value of a spec into this asset (used only when the asset is first created).</summary>
    public void CopyFrom(RoomSpec s)
    {
        id = s.id; displayName = s.displayName; category = s.category; tier = s.tier; mandatory = s.mandatory; minCount = s.minCount; maxCount = s.maxCount;
        weight = s.weight; sectors = s.sectors; deck = s.deck; spreadAcrossSectors = s.spreadAcrossSectors; permittedRoles = s.roles;
        minConnections = s.minConnections; maxConnections = s.maxConnections; mayBeDeadEnd = s.mayBeDeadEnd;
        preferredNeighbours = (RoomCategory[])s.preferredNeighbours.Clone(); forbiddenNeighbours = (RoomCategory[])s.forbiddenNeighbours.Clone();
        minSpawnDistance = s.minSpawnDistance; maxSpawnDistance = s.maxSpawnDistance; closeTo = (RoomCategory[])s.closeTo.Clone();
        closeToDistance = s.closeToDistance; closeToStrict = s.closeToStrict; features = s.features;
        CopyRulesFrom(s);
    }

    /// <summary>Copies only the relationship and capability rules (used by Alien > Procedural Ship > Apply Default Rule Updates To Room Definitions).</summary>
    public void CopyRulesFrom(RoomSpec s)
    {
        preferredNeighbours = (RoomCategory[])s.preferredNeighbours.Clone(); forbiddenNeighbours = (RoomCategory[])s.forbiddenNeighbours.Clone();
        closeTo = (RoomCategory[])s.closeTo.Clone(); closeToDistance = s.closeToDistance; closeToStrict = s.closeToStrict;
        onlyNeighbours = (RoomCategory[])s.onlyNeighbours.Clone(); requiredNeighbours = (RoomCategory[])s.requiredNeighbours.Clone();
        placement = s.placement; requiresVerticalConnection = s.requiresVerticalConnection;
    }
}
