using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Development-only reports of states that must never happen (a locker claiming a player who is gone, a player claiming a locker that does not
/// claim them). Compiled out of release builds entirely (the calls and their arguments disappear). Each distinct message is logged once, and
/// the memory is cleared on every round start, so a repeating fault is a single line, never a flood. Reports only: nothing here changes state.
/// </summary>
public static class DevChecks
{
    static readonly HashSet<string> reported = new();

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    public static void Report(string message, Object context = null)
    {
        if (reported.Add(message)) Debug.LogError($"Invariant violated: {message}", context);
    }

    [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
    public static void NewRound() => reported.Clear();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => reported.Clear();
}
