using UnityEngine;

/// <summary>
/// The creature and lockers (host only). A locker is just another HidingSpot in its room's list: it is chosen by place, distance, budget and
/// search history exactly like furniture (HidingChoice), and NOTHING in the creature's code reads who is inside one. What is specific to a
/// locker is how it is checked:
///
///   walk to the inspection point, face the door, wind up, OPEN it, look in, close it, carry on.
///
/// The door being open is what lets the existing sight rays reach a player inside (a closed door blocks them, so nobody is ever recognised or
/// caught through it). From the open door the creature looks in with the deliberate-inspection recognition (short, tense, faster still when it
/// watched the player get in). If the locker is empty it simply closes it and the search continues; a visible player interrupts the inspection
/// into a chase (the door is released). While it holds the door open the occupant cannot leave, so they cannot walk out through it.
/// A locker it watched someone enter is checked first and quickly (the existing witnessed-spot memory); a heard sound beside a locker makes
/// that locker a better choice (a nearer place), never a certain one.
/// </summary>
public partial class CreatureAI
{
    [Header("Locker inspection")]
    [Tooltip("Facing the door before opening it (s)")] public float lockerWindup = 1f;
    [Tooltip("...when it watched a player get in: it does not hesitate")] public float lockerWitnessedWindup = 0.3f;
    [Tooltip("The door takes this long to open before it looks in (s)")] public float lockerOpenSeconds = 0.6f;
    [Tooltip("Time looking into the open locker (s)")] public float lockerLookSeconds = 2f;
    [Tooltip("The door closing again (s)")] public float lockerCloseSeconds = 0.5f;
    [Tooltip("Recognition time multiplier from the open door when it watched this player get in (below 1 = faster)")] [Range(0.2f, 1f)] public float witnessedInspectFactor = 0.6f;
    [Tooltip("A heard sound within this distance of a locker makes it a better choice (m)")] public float lockerNoiseRadius = 3f;
    [Tooltip("How much better (score units)")] public float lockerNoiseBonus = 2f;

    readonly LockerInspection lockerInspection = new();
    HideLocker lockerHeld; // the locker whose door this inspection has opened or is about to open
    HideLocker chaseLocker; // a locker the creature recognised somebody through: held open until the chase ends
    bool lockerOpened;

    // Lower is better: place only (distance, hiding place, enclosed, a heard sound beside it). No occupancy exists in this calculation.
    float HidingScore(HidingSpot h) => HidingChoice.Score(Vector3.Distance(evidencePos, h.transform.position), Vector3.Distance(transform.position, h.transform.position),
        h.locker != null, evidenceKind == EvidenceKind.Noise, lockerNoiseRadius, lockerNoiseBonus);

    // Called when the search arrives at a hiding spot that is a locker.
    void BeginLockerInspect(HidingSpot spot)
    {
        lockerHeld = spot.locker;
        lockerOpened = false;
        bool witnessed = witnessedSpot == spot; // the existing observation memory: it saw somebody go in
        lockerInspection.Begin(witnessed ? lockerWitnessedWindup : lockerWindup, lockerOpenSeconds, lockerLookSeconds, lockerCloseSeconds);
        Decision(DecisionReason.LockerInspected, DecisionEffect.None, spot.name + (witnessed ? " (watched entry, quick)" : ""));
    }

    // Per frame while inspecting a locker. True when the inspection is over (the door is released).
    bool UpdateLockerInspect()
    {
        var stage = lockerInspection.Tick(Time.deltaTime);
        if (lockerInspection.DoorShouldBeOpen && !lockerOpened)
        {
            lockerOpened = true;
            lockerHeld.BeginInspection(); // the door opens: from now sight rays can reach inside
        }
        inspectingLow = lockerInspection.Looking; // the deliberate-inspection eye only while it is really looking in
        if (stage == LockerStage.Closing && lockerOpened)
        {
            lockerOpened = false;
            lockerHeld.EndInspection();
        }
        if (stage != LockerStage.Done && stage != LockerStage.None) return false;
        ReleaseLocker();
        return true;
    }

    // Recognised a player through the open door of the locker being inspected: hand that door over to the chase instead of closing it (a closed
    // door would block the very line the capture needs, and the occupant must not slip out). Returns it, or null.
    HideLocker DetachLockerForChase(HidingSpot inspected)
    {
        if (inspected == null || inspected.locker == null || lockerHeld != inspected.locker || !lockerOpened) return null;
        var held = lockerHeld;
        lockerHeld = null;
        lockerOpened = false;
        lockerInspection.Cancel();
        return held;
    }

    // Any interruption (a chase ending, a new search, a reset): every door the creature holds is let go and the inspection forgotten.
    void ReleaseLocker()
    {
        if (chaseLocker != null) { chaseLocker.EndInspection(); chaseLocker = null; }
        if (lockerHeld != null && lockerOpened) lockerHeld.EndInspection();
        lockerHeld = null;
        lockerOpened = false;
        lockerInspection.Cancel();
    }

    string LockerDebugText() => chaseLocker != null ? $"{chaseLocker.name}: held open for the chase" : lockerHeld == null ? "-" : $"{lockerHeld.name}: {lockerInspection.Stage}, {lockerInspection.StageSecondsLeft:0.0}s left, door {(lockerOpened ? "held open" : "closed")}"
        + (witnessedSpot != null && witnessedSpot.locker != null ? $", witnessed {witnessedSpot.name}" : "");
}
