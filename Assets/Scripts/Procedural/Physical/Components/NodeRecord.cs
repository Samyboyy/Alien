using System;
using System.Collections.Generic;
using UnityEngine;

// What the generated ship leaves in a scene: one component per placed room, corridor module and wall seal, and a GeneratedShip on the root that
// records the logical graph and the physical layout, so validation and the debug views can work from the scene itself.

[Serializable]
public class NodeRecord
{
    public int index;
    public string id, definitionId, displayName;
    public RoomCategory category;
    public ShipSector sector;
    public int deck;
}
