using Unity.Netcode;
using UnityEngine;

public enum CreatureVocal : byte { None, RoamCall, ChaseSnarl, SearchSnarl }

/// <summary>
/// Restrained, state-aware vocalisations (host decides, every peer plays the same sound at the creature through CreatureAudio):
///  - a short aggressive snarl when a chase starts (not on every re-acquire: its own cooldown);
///  - now and then a snarl while searching around real evidence;
///  - rarely, a distant roaming call while it is alert or searching.
/// Long cooldowns with seeded variation (from the round seed), a minimum gap between any two, none in a chase except its start, none while
/// it is inside a vent, no growl loop. Purely cosmetic: a vocalisation is never a NoiseSystem event, so the creature cannot hear itself and
/// it is never evidence. One small RPC per vocalisation (rare), so late joiners only miss sounds that have already finished.
/// </summary>
public partial class CreatureAI
{
    [Header("Vocalisations (cosmetic; never heard by the creature)")]
    public bool vocalsEnabled = true;
    [Tooltip("Minimum seconds between any two vocalisations")] public float vocalMinGap = 12f;
    [Tooltip("A chase-start snarl at most this often (s)")] public float chaseVocalCooldown = 20f;
    [Tooltip("Seconds between snarls while searching evidence (random in the range)")] public float searchVocalMin = 35f;
    public float searchVocalMax = 70f;
    [Tooltip("Seconds between distant roaming calls (random in the range)")] public float roamCallMin = 70f;
    public float roamCallMax = 140f;
    [Tooltip("Alertness needed for a roaming call while patrolling (searching always counts)")] [Range(0f, 1f)] public float roamCallMinAlertness = 0.25f;

    System.Random vocalRng = new(11);
    double lastVocalAt = double.NegativeInfinity, lastChaseVocalAt = double.NegativeInfinity, nextSearchVocalAt, nextRoamCallAt;
    float vocalTimer;

    void ResetVocals(int seed)
    {
        vocalRng = new System.Random(seed);
        double now = Time.timeAsDouble;
        lastVocalAt = lastChaseVocalAt = double.NegativeInfinity;
        nextSearchVocalAt = now + Mathf.Lerp(searchVocalMin, searchVocalMax, (float)vocalRng.NextDouble());
        nextRoamCallAt = now + Mathf.Lerp(roamCallMin, roamCallMax, (float)vocalRng.NextDouble());
        vocalTimer = 0f;
    }

    // Host: a chase has just started (called from EnterChase when it was not already chasing).
    void OnChaseStarted()
    {
        double now = Time.timeAsDouble;
        if (!vocalsEnabled || now - lastChaseVocalAt < chaseVocalCooldown || now - lastVocalAt < 3.0) return;
        lastChaseVocalAt = now;
        SendVocal(CreatureVocal.ChaseSnarl);
    }

    // Host, about once a second: the occasional search snarl and the rare roaming call.
    void TickVocals()
    {
        if (!vocalsEnabled || (vocalTimer -= Time.deltaTime) > 0f) return;
        vocalTimer = 1f;
        if (ventPhase.Value != VentPhase.None || state.Value is CreatureState.Chase or CreatureState.Bash) return;
        double now = Time.timeAsDouble;
        if (now - lastVocalAt < vocalMinGap) return;

        bool huntingEvidence = evidenceKind != EvidenceKind.None && state.Value is CreatureState.Search or CreatureState.Investigate or CreatureState.Pursue;
        if (huntingEvidence && now >= nextSearchVocalAt)
        {
            nextSearchVocalAt = now + Mathf.Lerp(searchVocalMin, searchVocalMax, (float)vocalRng.NextDouble());
            SendVocal(CreatureVocal.SearchSnarl);
            return;
        }
        bool alert = state.Value != CreatureState.Patrol || alertness >= roamCallMinAlertness;
        if (alert && now >= nextRoamCallAt)
        {
            nextRoamCallAt = now + Mathf.Lerp(roamCallMin, roamCallMax, (float)vocalRng.NextDouble());
            SendVocal(CreatureVocal.RoamCall);
        }
    }

    void SendVocal(CreatureVocal kind)
    {
        lastVocalAt = Time.timeAsDouble;
        VocalRpc((byte)kind, (byte)vocalRng.Next(256));
    }

    [Rpc(SendTo.Everyone)]
    void VocalRpc(byte kind, byte variant)
    {
        if (sound != null) sound.PlayVocal((CreatureVocal)kind, variant);
    }
}
