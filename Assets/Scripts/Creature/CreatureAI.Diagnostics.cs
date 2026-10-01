using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Time = UnityEngine.Time;

/// <summary>
/// Developer diagnostics for the creature's decisions (host only, Editor and Development Builds only: every call here is compiled out of a
/// release build). One structured event is recorded at each meaningful change (a state transition, a vent cancel, re-route or stored evidence,
/// a locker inspection, a door attempt, a recognised decoy...) with a stable reason code, into a fixed ring of 96 entries. Nothing is recorded
/// per frame, nothing is sent over the network, and nothing here reads a player's live position: an event holds only what the creature
/// itself knows (its stored evidence, its state, its own plans). F3 shows the latest entries; F8 (while F3 is on) writes the snapshot and the whole
/// history to a text file under Application.persistentDataPath. The ring is cleared on a round reset and on despawn. The invariant checks at the
/// bottom report impossible states once each, at transitions and every two seconds, never per frame.
/// </summary>
public partial class CreatureAI
{
    const int DecisionCapacity = 96;

    readonly RingBuffer<DecisionEvent> decisions = new(DecisionCapacity);
    byte lastRecordedState;
    DecisionReason lastDecisionCode;
    double lastDecisionTime;
    string lastDumpPath;
    readonly HashSet<string> reportedInvariants = new();
    float invariantTimer;
    int badChaseTarget;

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void Decision(DecisionReason code, DecisionEffect effect = DecisionEffect.None, string detail = null)
    {
        if (!IsServer) return;
        double now = Time.timeAsDouble;
        // The same decision repeating within a moment (every footstep of a followed player re-confirms the same investigation) is one entry, not a flood.
        if (lastRecordedState == (byte)state.Value && code == lastDecisionCode && now - lastDecisionTime < 0.6) return;
        lastDecisionCode = code;
        lastDecisionTime = now;
        decisions.Add(new DecisionEvent
        {
            time = now,
            from = lastRecordedState,
            to = (byte)state.Value,
            reason = code,
            effect = effect,
            target = targetId.Value,
            evidenceKind = (byte)evidenceKind,
            evidenceAge = evidenceKind == EvidenceKind.None ? 0f : (float)(now - evidenceTime),
            evidenceStrength = evidenceStrength,
            evidenceX = evidencePos.x,
            evidenceZ = evidencePos.z,
            searchPhase = (byte)searchPhase,
            room = currentRoom != null ? currentRoom.roomName : null,
            ventPhase = (byte)ventPhase.Value,
            ventEntry = (sbyte)attempt.Entry,
            ventExit = (sbyte)attempt.Exit,
            door = bashDoor != null ? bashDoor.name : null,
            lockerStage = (byte)lockerInspection.Stage,
            decoyToken = evidenceToken,
            alertness = alertness,
            detail = detail,
        });
        lastRecordedState = (byte)state.Value;
        CheckInvariants(code.ToString());
    }

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void ClearDiagnostics()
    {
        decisions.Clear();
        reportedInvariants.Clear();
        badChaseTarget = 0;
        lastRecordedState = (byte)state.Value;
        lastDecisionTime = double.NegativeInfinity;
    }

    // Called once per Update on the host: the F8 dump key, and the low-cadence invariant check.
    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void DiagnosticsTick()
    {
        if (!IsServer) return;
        var kb = Keyboard.current;
        if (showDebugLabel && kb != null && kb.f8Key.wasPressedThisFrame) DumpDiagnostics();
        if ((invariantTimer -= Time.deltaTime) > 0f) return;
        invariantTimer = 2f;
        CheckInvariants("periodic");
    }

    static string StateName(byte s) => ((CreatureState)s).ToString();

    string RecentDecisionText(int lines)
    {
        var sb = new StringBuilder();
        double now = Time.timeAsDouble;
        for (int i = 0; i < lines && i < decisions.Count; i++)
        {
            var e = decisions.FromNewest(i);
            sb.Append("\n  ");
            DecisionFormat.Line(sb, e, StateName, now);
        }
        return sb.Length == 0 ? "\n  -" : sb.ToString();
    }

    // The current snapshot and the whole history, oldest first, as text. Returns the path (also kept for the F3 label).
    string DumpDiagnostics()
    {
        try
        {
            var sb = new StringBuilder();
            double now = Time.timeAsDouble;
            sb.Append("Creature diagnostics, host time ").Append(now.ToString("0.0")).Append("s, ").Append(DateTime.Now.ToString("s")).Append('\n');
            sb.Append("state ").Append(state.Value).Append(", why: ").Append(reason).Append(", alertness ").Append(alertness.ToString("0.00")).Append('\n');
            sb.Append("evidence: ").Append(EvidenceNote()).Append('\n');
            sb.Append("target: ").Append(targetId.Value == ulong.MaxValue ? "-" : "P" + targetId.Value).Append('\n');
            sb.Append("search: ").Append(SearchDebugText()).Append('\n');
            sb.Append("vent: ").Append(VentDebugText()).Append('\n');
            sb.Append("door: ").Append(bashDoor == null ? "-" : bashDoor.name).Append(", locker: ").Append(LockerDebugText()).Append(", decoy: ").Append(DecoyDebugText()).Append("\n\n");
            sb.Append("History (oldest first, ").Append(decisions.Count).Append(" of at most ").Append(DecisionCapacity).Append("):\n");
            for (int i = decisions.Count - 1; i >= 0; i--)
            {
                DecisionFormat.Line(sb, decisions.FromNewest(i), StateName, now);
                sb.Append('\n');
            }
            string path = Path.Combine(UnityEngine.Application.persistentDataPath, $"creature-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            lastDumpPath = path;
            Debug.Log($"Creature diagnostics written to {path}");
            return path;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Could not write the creature diagnostics file: {e.Message}");
            return null;
        }
    }

    // ---------- Invariants (impossible states; reported once each, never per frame) ----------

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void CheckInvariants(string where)
    {
        if (!IsServer || !IsSpawned || agent == null) return;
        var vent = ventPhase.Value;
        Invariant(!(ventCommitted && agent.enabled), "it is committed to the duct but its NavMeshAgent is on (navigating the floor and travelling in the vent at once)", where);
        Invariant(!(vent is VentPhase.Travelling or VentPhase.Preparing or VentPhase.Exiting && agent.enabled), $"vent phase {vent} but its NavMeshAgent is on", where);
        Invariant(!(state.Value != CreatureState.Vent && attempt.Active), $"a vent attempt is active while the state is {state.Value}", where);
        Invariant(!(state.Value != CreatureState.Bash && bashDoor != null), $"a door attempt on {bashDoor?.name} is still set while the state is {state.Value}", where);
        Invariant(!(lockerHeld != null && state.Value is CreatureState.Chase or CreatureState.Patrol or CreatureState.Vent), $"a locker inspection is set while the state is {state.Value}", where);

        // A dead, escaped or disconnected player must not stay the chase target (one tick of grace: the chase code drops it on its next pass).
        bool badTarget = state.Value == CreatureState.Chase && (target == null || !target.IsSpawned || !Alive(target));
        badChaseTarget = badTarget ? badChaseTarget + 1 : 0;
        Invariant(badChaseTarget < 3, "the chase target is dead, escaped or disconnected but still the target", where);
    }

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void Invariant(bool ok, string message, string where)
    {
        if (ok || !reportedInvariants.Add(message)) return;
        Debug.LogError($"Creature invariant violated ({where}): {message}. Recent decisions:{RecentDecisionText(6)}", this);
    }

    // After a round reset nothing transient may survive.
    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    void CheckResetLeftovers()
    {
        if (!IsServer) return;
        Invariant(bashDoor == null && lockerHeld == null && chaseLocker == null && !attempt.Active && ventPhase.Value == VentPhase.None && !ventPending.Has,
            "a door attempt, locker inspection, vent attempt or held vent evidence survived the round reset", "reset");
        Invariant(!lockerInspection.Active && evidenceToken == 0 && decoys.IgnoredCount == 0, "locker or decoy state survived the round reset", "reset");
    }
}
