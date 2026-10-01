using System.Linq;
using NUnit.Framework;
using UnityEngine;

// Unity EditMode tests for the locker geometry (real colliders, so they run in the Unity Test Runner, not in the pure harness). Each builds a
// locker of its own on a floor and destroys it again.
public class LockerSetupTests
{
    GameObject floor, locker;
    HideLocker hide;

    [SetUp]
    public void SetUp()
    {
        floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Test Floor";
        floor.transform.position = new Vector3(0f, -0.1f, 0f);
        floor.transform.localScale = new Vector3(20f, 0.2f, 20f);
        locker = ShipBuilder.BuildLocker("Test Locker", Vector3.zero, Quaternion.identity, null); // front towards +Z
        hide = locker.GetComponent<HideLocker>();
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(locker);
        Object.DestroyImmediate(floor);
    }

    [Test]
    public void TheLockerHasEveryAnchor() => Assert.IsTrue(hide.AnchorsValid);

    [Test]
    public void ThePlayerFitsAtTheHiddenBodyAnchorAndTheExitIsClear()
    {
        Vector3 b = hide.hiddenBody.position;
        Assert.IsFalse(Physics.CheckCapsule(b + Vector3.up * 0.37f, b + Vector3.up * 1.43f, 0.34f, ~0, QueryTriggerInteraction.Ignore), "a player capsule fits inside");
        Assert.IsTrue(hide.ExitClear(null), "nothing stands at the exit");
    }

    [Test]
    public void AClosedDoorBlocksTheLineIntoTheLockerAndAnOpenOneDoesNot()
    {
        Vector3 from = hide.inspectPoint.position + Vector3.up * 1.2f, to = hide.hiddenBody.position + Vector3.up * 1.2f;
        foreach (var c in hide.doorColliders) c.enabled = true;
        Physics.SyncTransforms();
        Assert.IsTrue(Physics.Linecast(from, to, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), "closed: nothing can see or reach in");
        StringAssert.Contains("Door", hit.collider.name);
        foreach (var c in hide.doorColliders) c.enabled = false;
        Physics.SyncTransforms();
        Assert.IsFalse(Physics.Linecast(from, to, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), "open: a clear line to the player inside");
    }

    [Test]
    public void AnExitBlockedByAnotherPlayersBodyIsReportedAndOrdinaryEntryIsBlockedByTheBox()
    {
        var other = new GameObject("Other player");
        other.transform.position = hide.exitPoint.position + Vector3.up * 0.9f;
        var cap = other.AddComponent<CapsuleCollider>();
        cap.height = 1.8f;
        cap.radius = 0.35f;
        Physics.SyncTransforms();
        Assert.IsFalse(hide.ExitClear(null), "a body at the exit blocks it");
        Object.DestroyImmediate(other);
        Physics.SyncTransforms();
        Assert.IsTrue(hide.ExitClear(null));
        // The walls and the closed door stop anyone simply walking in: the interior is surrounded by colliders on every side but the open front.
        Assert.GreaterOrEqual(locker.GetComponentsInChildren<Collider>().Count(c => c.enabled), 5);
    }

    [Test]
    public void TheModelCanBeReplacedWithoutTouchingAnchorsOrColliders()
    {
        var model = locker.transform.Find("Model");
        Assert.IsNotNull(model, "visuals live under their own child");
        Assert.AreEqual(0, model.GetComponentsInChildren<Collider>().Length, "the model carries no gameplay colliders");
        Assert.IsNotNull(locker.transform.Find("Anchors/Hidden Camera"));
    }
}
