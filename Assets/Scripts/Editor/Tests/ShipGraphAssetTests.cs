using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Unity EditMode tests for the asset side of the ship graph (they create ScriptableObjects in memory, so they run in the Unity Test Runner).
public class ShipGraphAssetTests
{
    [Test]
    public void ARoomDefinitionAssetCarriesEveryValueOfItsSpec()
    {
        foreach (var spec in DefaultRoomCatalogue.Create())
        {
            var asset = ScriptableObject.CreateInstance<RoomDefinition>();
            try
            {
                asset.CopyFrom(spec);
                var back = asset.ToSpec();
                Assert.AreEqual(spec.id, back.id);
                Assert.AreEqual(spec.category, back.category, spec.id);
                Assert.AreEqual((spec.sectors, spec.roles, spec.minCount, spec.maxCount, spec.minConnections, spec.maxConnections), (back.sectors, back.roles, back.minCount, back.maxCount, back.minConnections, back.maxConnections), spec.id);
                Assert.AreEqual((spec.minSpawnDistance, spec.maxSpawnDistance, spec.mayBeDeadEnd, spec.deck, spec.weight, spec.features), (back.minSpawnDistance, back.maxSpawnDistance, back.mayBeDeadEnd, back.deck, back.weight, back.features), spec.id);
                CollectionAssert.AreEqual(spec.forbiddenNeighbours, back.forbiddenNeighbours, spec.id);
                CollectionAssert.AreEqual(spec.preferredNeighbours, back.preferredNeighbours, spec.id);
                CollectionAssert.AreEqual(spec.closeTo, back.closeTo, spec.id);
                Assert.AreEqual((spec.closeToDistance, spec.closeToStrict, spec.spreadAcrossSectors, spec.tier, spec.mandatory), (back.closeToDistance, back.closeToStrict, back.spreadAcrossSectors, back.tier, back.mandatory), spec.id);
            }
            finally { Object.DestroyImmediate(asset); }
        }
    }

    [Test]
    public void AProfileBuiltFromTheDefaultsGeneratesTheSameShipAsTheCodeCatalogue()
    {
        var profile = ScriptableObject.CreateInstance<ShipGenerationProfile>();
        var assets = DefaultRoomCatalogue.Create().Select(s => { var a = ScriptableObject.CreateInstance<RoomDefinition>(); a.CopyFrom(s); return a; }).ToList();
        try
        {
            profile.rooms.AddRange(assets);
            for (int seed = 0; seed < 10; seed++)
                Assert.AreEqual(new ShipGraphGenerator(DefaultRoomCatalogue.Create()).Generate(seed).graph.Canonical(), profile.CreateGenerator().Generate(seed).graph.Canonical(), $"seed {seed}");
        }
        finally
        {
            foreach (var a in assets) Object.DestroyImmediate(a);
            Object.DestroyImmediate(profile);
        }
    }
}
