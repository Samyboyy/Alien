using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The ship generator's configuration as an asset: the whole-ship rules and the room definitions to use. Editing values here changes what the
/// generator builds, without touching code. Alien > Procedural Ship > Create Or Repair Room Definitions creates it with every default definition.
/// </summary>
[CreateAssetMenu(menuName = "Alien/Procedural Ship/Generation Profile", fileName = "ShipGenerationProfile")]
public class ShipGenerationProfile : ScriptableObject
{
    public ShipGraphSettings settings = new();
    public List<RoomDefinition> rooms = new();

    /// <summary>The definitions as pure specs (empty slots skipped). The generator sorts them by id, so list order does not matter.</summary>
    public List<RoomSpec> BuildCatalogue()
    {
        var list = new List<RoomSpec>();
        foreach (var r in rooms) if (r != null) list.Add(r.ToSpec());
        return list;
    }

    public ShipGraphGenerator CreateGenerator() => new(BuildCatalogue(), (settings ?? new ShipGraphSettings()).Clone());
}
