using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>What made a logical noise event. Only used by the creature's hearing and the debug display.</summary>
public enum SoundKind : byte { Other, SprintStep, WalkStep, CrouchStep, Breathing, HeavyBreathing, Door, Impact }

/// <summary>
/// Host-only record of recent LOGICAL noise events for AI perception (nothing here is audible to players; there are no
/// audio assets yet). Emitters call Emit; the creature reads Recent and handles each event at most once (by id).
/// Loudness is the outer audible range in metres: 0 strength at that distance, full strength at the source.
/// A noise is a snapshot of where it was made; nothing follows the source afterwards.
/// </summary>
public static class NoiseSystem
{
    public const ulong NoEmitter = ulong.MaxValue;
    /// <summary>Emitter id for sounds the creature makes itself; its own hearing ignores them.</summary>
    public const ulong CreatureEmitter = ulong.MaxValue - 1;

    public struct Noise
    {
        public ulong id; // increases with every Emit, so "handled up to id N" is exact
        public Vector3 position;
        public float loudness; // outer range in metres
        public double time; // host Time.timeAsDouble
        public string source;
        public SoundKind kind;
        public ulong emitter; // owning client id of the player who made it, or NoEmitter

        /// <summary>Footsteps and breathing of a player: a trail of that player. Doors, impacts and consoles are incidental.</summary>
        public bool IsPlayerSound => emitter != NoEmitter && emitter != CreatureEmitter
            && kind is SoundKind.SprintStep or SoundKind.WalkStep or SoundKind.CrouchStep or SoundKind.Breathing or SoundKind.HeavyBreathing;
    }

    const int MaxNoises = 64; // ponytail: fixed ring; old entries are dropped first. Raise if debug gizmos must show longer history.
    static readonly List<Noise> recent = new();
    static ulong lastId;

    public static IReadOnlyList<Noise> Recent => recent;
    public static ulong LastId => lastId;

    /// <summary>Host only; ignored on clients and for loudness &lt;= 0.</summary>
    public static void Emit(Vector3 position, float loudness, string source, SoundKind kind = SoundKind.Other, ulong emitter = NoEmitter)
    {
        var nm = NetworkManager.Singleton;
        if (loudness <= 0f || nm == null || !nm.IsServer) return;
        if (recent.Count == MaxNoises) recent.RemoveAt(0);
        recent.Add(new Noise
        {
            id = ++lastId,
            position = position,
            loudness = loudness,
            time = Time.timeAsDouble,
            source = source,
            kind = kind,
            emitter = emitter,
        });
    }

    /// <summary>Forget history (round reset). Ids keep counting so a listener's "handled up to" stays valid.</summary>
    public static void Clear() => recent.Clear();

    // Survive "Enter Play Mode" without a domain reload.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        recent.Clear();
        lastId = 0;
    }
}
