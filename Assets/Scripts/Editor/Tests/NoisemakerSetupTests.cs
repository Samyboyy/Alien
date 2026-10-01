using System.Linq;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;

// Unity EditMode tests for the noisemaker setup (they use real colliders and objects, so they run in the Unity Test Runner, not in the pure
// harness). Each builds a tiny scene of its own and destroys it again.
public class NoisemakerSetupTests
{
    GameObject root;

    [SetUp]
    public void SetUp() => root = new GameObject("noisemaker test root");

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(root);

    GameObject Cube(string name, Vector3 centre, Vector3 size, bool trigger = false)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(root.transform);
        go.transform.position = centre;
        go.transform.localScale = size;
        go.GetComponent<Collider>().isTrigger = trigger;
        return go;
    }

    // A floor whose top is at y = 0, a pickup (the real size) resting on it at the spot, and the spot itself.
    (NoisemakerPickup pickup, BoxCollider box, Transform spot) Scene(Vector3 floorPoint)
    {
        Cube("Floor", new Vector3(0f, -0.1f, 0f), new Vector3(40f, 0.2f, 40f));
        var go = new GameObject("Noisemaker Pickup 0 Test");
        go.transform.SetParent(root.transform);
        var spot = new GameObject("Noisemaker Spot Test 0").transform;
        spot.SetParent(root.transform);
        spot.position = floorPoint + Vector3.up * 0.04f;
        go.transform.position = spot.position; // the pickup starts on its first spot, exactly as built by the setup
        var box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(0.17f, 0.07f, 0.17f);
        var pickup = go.AddComponent<NoisemakerPickup>();
        pickup.spots = new[] { spot };
        Physics.SyncTransforms();
        return (pickup, box, spot);
    }

    [Test]
    public void AFloorSupportedPickupWithItsOwnColliderIsAccepted()
    {
        var (pickup, box, spot) = Scene(new Vector3(3f, 0f, 2f));
        Assert.IsEmpty(ShipBuilder.SpotProblems(pickup, box, spot), "the floor under it and its own collider are not obstructions");
    }

    [Test]
    public void TriggerVolumesAreIgnored()
    {
        var (pickup, box, spot) = Scene(new Vector3(3f, 0f, 2f));
        Cube("Room volume", new Vector3(3f, 2f, 2f), new Vector3(8f, 4f, 8f), trigger: true);
        Cube("Acoustic zone", new Vector3(3f, 2f, 2f), new Vector3(6f, 4.5f, 6f), trigger: true);
        Physics.SyncTransforms();
        Assert.IsEmpty(ShipBuilder.SpotProblems(pickup, box, spot));
    }

    [Test]
    public void AWallThroughThePickupIsRejectedAndNamed()
    {
        var (pickup, box, spot) = Scene(new Vector3(3f, 0f, 2f));
        Cube("Crate", new Vector3(3.1f, 0.5f, 2f), new Vector3(0.6f, 1f, 0.6f));
        Physics.SyncTransforms();
        var issues = ShipBuilder.SpotProblems(pickup, box, spot);
        Assert.AreEqual(1, issues.Count);
        StringAssert.Contains("Crate", issues[0], "the blocking collider is named");
        StringAssert.Contains("BoxCollider", issues[0], "and its type");
    }

    [Test]
    public void ASpotOverNothingHasNoSupport()
    {
        var (pickup, box, spot) = Scene(new Vector3(3f, 0f, 2f));
        spot.position += Vector3.up * 1f; // floating
        Physics.SyncTransforms();
        var issues = ShipBuilder.SpotProblems(pickup, box, spot);
        Assert.IsTrue(issues.Any(i => i.Contains("no supporting floor")));
    }

    [Test]
    public void ARepeatedRegistrationAddsThePrefabOnlyOnce()
    {
        var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
        var prefab = Cube("device", Vector3.zero, Vector3.one * 0.1f);
        try
        {
            Assert.IsTrue(ShipBuilder.EnsureListed(list, prefab), "first run adds it");
            Assert.IsFalse(ShipBuilder.EnsureListed(list, prefab), "second run changes nothing");
            Assert.AreEqual(1, list.PrefabList.Count(p => p.Prefab == prefab));
            list.Add(new NetworkPrefab { Prefab = prefab });
            Assert.IsTrue(ShipBuilder.EnsureListed(list, prefab), "a repeated entry is repaired");
            Assert.AreEqual(1, list.PrefabList.Count(p => p.Prefab == prefab));
        }
        finally { Object.DestroyImmediate(list); }
    }

    [Test]
    public void StandInSoundsExistWithNoBankAndAreMadeOnce()
    {
        int before = NoisemakerSounds.GeneratedCount;
        foreach (NoisemakerSound kind in System.Enum.GetValues(typeof(NoisemakerSound)))
        {
            var first = NoisemakerSounds.Resolve(null, kind, out bool fallback);
            Assert.IsNotNull(first, $"{kind} is audible without any AudioBank");
            Assert.IsTrue(fallback);
            Assert.AreSame(first, NoisemakerSounds.Resolve(null, kind, out _), $"{kind} is cached, not remade");
            Assert.Greater(NoisemakerSounds.Volume(null, kind), 0f);
        }
        Assert.LessOrEqual(NoisemakerSounds.GeneratedCount - before, NoisemakerSynth.SoundCount, "at most one clip per kind, ever");
    }

    [Test]
    public void UnsetBankClipsNeverPreventTheStandIn()
    {
        var bank = ScriptableObject.CreateInstance<AudioBank>();
        try
        {
            foreach (NoisemakerSound kind in System.Enum.GetValues(typeof(NoisemakerSound)))
            {
                var clip = NoisemakerSounds.Resolve(bank, kind, out bool fallback);
                Assert.IsNotNull(clip);
                Assert.IsTrue(fallback, "an unset field means the stand-in");
            }
            bank.noisemakerPulse = NoisemakerSounds.Resolve(null, NoisemakerSound.Spent, out _); // any assigned clip wins
            var chosen = NoisemakerSounds.Resolve(bank, NoisemakerSound.Pulse, out bool fb);
            Assert.IsFalse(fb);
            Assert.AreSame(bank.noisemakerPulse, chosen);
        }
        finally { Object.DestroyImmediate(bank); }
    }
}
