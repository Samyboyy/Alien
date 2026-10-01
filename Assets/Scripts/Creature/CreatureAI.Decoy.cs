using UnityEngine;

/// <summary>
/// What the creature makes of thrown noisemakers (host only). A decoy is an ordinary incidental sound with a SOURCE TOKEN. It is heard,
/// weighted and blurred like any other sound (never given a position it could not hear), and it is accepted only under DecoyRules: never
/// while the creature can see a player (the chase state does not listen at all, and sight is checked before sound), never over a
/// still-weighted trail of a player, and not once the creature has recognised that device or its interest in decoys has run out.
/// It walks to where the sound came from. Standing at the device for a short time it works out what it is: that device is ignored from then
/// on, and the creature goes back to what it was doing (its earlier evidence while that is still worth anything, otherwise a heightened
/// patrol). Other devices stay fully valid. Nothing here learns who threw anything.
/// </summary>
public partial class CreatureAI
{
    [Header("Decoys (thrown noisemakers)")]
    [Tooltip("It examines a device once it is this close to where the sound came from (m)")] public float decoyInspectRadius = 3f;
    [Tooltip("Seconds standing near it before it recognises the device as a decoy")] public float decoyInspectSeconds = 2.5f;
    [Tooltip("Total seconds it may spend following decoys before it gets bored of them")] public float decoyInterestSeconds = 30f;
    [Tooltip("...and ignores them for this long")] public float decoyRefractorySeconds = 45f;

    struct EvidenceSnapshot
    {
        public EvidenceKind kind;
        public Vector3 pos;
        public float strength;
        public ulong emitter;
        public bool crouched;
        public double time;
    }

    readonly DecoyMemory decoys = new();
    ulong evidenceToken; // the device the current evidence came from, 0 if none
    EvidenceSnapshot beforeDecoy; // what it was following when the first decoy took over
    float decoyCleanTimer;
    int decoyAccepted, decoyRejected;
    string decoyVerdict = "-";

    EvidenceSnapshot Snapshot() => new()
    {
        kind = evidenceKind, pos = evidencePos, strength = evidenceStrength, emitter = evidenceEmitter, crouched = evidenceCrouched, time = evidenceTime,
    };

    // Called by Hear when a sound with a source token wins: remember what it was following, and which device it now heads for.
    void BeginDecoy(ulong token, EvidenceSnapshot previous, bool alreadyFollowingDecoy)
    {
        if (!alreadyFollowingDecoy) beforeDecoy = previous; // a repeated pulse or a second device never overwrites what came before the first
        evidenceToken = token;
    }

    // Host: the verdict on one decoy sound, for the debug display. The rule itself is DecoyRules.Accept.
    bool DecoyHeard(ulong token, float currentScore, bool trail, float strength, bool cooldownOver, double now)
    {
        bool ok = DecoyRules.Accept(decoys.IsIgnored(token), decoys.InterestLeft(now), currentScore, trail, token == evidenceToken, strength, switchMargin, cooldownOver);
        if (ok) decoyAccepted++; else decoyRejected++;
        decoyVerdict = ok ? "accepted" : decoys.IsIgnored(token) ? "rejected (recognised)" : !decoys.InterestLeft(now) ? "rejected (bored)" : trail && currentScore > 0f ? "rejected (player trail)" : "rejected (weaker than current evidence)";
        return ok;
    }

    // 10 Hz while it is following a decoy: spend interest, and recognise the device after standing near it for a moment.
    bool TickDecoy()
    {
        if (evidenceToken == 0) return false;
        double now = Time.timeAsDouble;
        if ((decoyCleanTimer -= 0.1f) <= 0f) { decoyCleanTimer = 2f; decoys.Prune(ThrownNoisemaker.IsLive); } // devices that are gone are forgotten
        decoys.Spend(0.1f, now, decoyInterestSeconds, decoyRefractorySeconds);
        if (!decoys.InterestLeft(now)) { ResumeAfterDecoy("lost interest in the decoys"); return true; }
        if (!ThrownNoisemaker.IsLive(evidenceToken)) { ResumeAfterDecoy("the device is gone"); return true; }
        if (Flat(evidencePos - transform.position).magnitude > decoyInspectRadius) return false;
        if (!decoys.Examine(evidenceToken, 0.1f, decoyInspectSeconds)) return false;
        ResumeAfterDecoy("recognised the decoy");
        return true;
    }

    // Back to what it was doing: the evidence from before the decoy while that still counts for something, else a heightened patrol.
    void ResumeAfterDecoy(string why)
    {
        evidenceToken = 0;
        var s = beforeDecoy;
        beforeDecoy = default;
        decoyVerdict = why;
        if (s.kind != EvidenceKind.None && EscapeRules.EvidenceScore(s.strength, (float)(Time.timeAsDouble - s.time), evidenceFadeSeconds) > 0f)
        {
            evidenceKind = s.kind;
            evidencePos = s.pos;
            evidenceStrength = s.strength;
            evidenceEmitter = s.emitter;
            evidenceCrouched = s.crouched;
            evidenceTime = s.time; // its age is kept: old evidence is not refreshed by having been interrupted
            EnterSearch(CreatureState.Search, why + ", resuming earlier evidence", DecisionReason.DecoyRecognised);
            return;
        }
        alertness = Mathf.Max(alertness, 0.5f);
        EnterPatrol(why, DecisionReason.DecoyRecognised);
    }

    void ResetDecoys()
    {
        decoys.Clear();
        evidenceToken = 0;
        beforeDecoy = default;
        decoyCleanTimer = 0f;
        decoyAccepted = decoyRejected = 0;
        decoyVerdict = "-";
    }

    /// <summary>Diagnostics: what this creature thinks of one device.</summary>
    public string DecoyNote(ulong token) =>
        decoys.IsIgnored(token) ? "recognised, ignored" : token == evidenceToken ? $"heading for it ({decoyVerdict})" : $"last verdict {decoyVerdict}";

    string DecoyDebugText() => $"{(evidenceToken != 0 ? $"following device {evidenceToken}" : "none")}, {decoys.IgnoredCount} recognised, accepted {decoyAccepted}, rejected {decoyRejected}, last: {decoyVerdict}";
}
