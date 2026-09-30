using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public enum PodState : byte { Idle, Counting, Launched }

/// <summary>
/// Escape pod. A ShipConsole calls StartLaunch; when the noisy countdown ends, the first <see cref="capacity"/> living
/// players aboard (in boarding order) escape and the pod is spent. Host-authoritative: clients only read the state,
/// launch time and boarding list for the HUD. <see cref="interior"/> is a child whose scale is the interior box.
/// </summary>
public class EscapePod : NetworkBehaviour, IRoundResettable
{
    public string podName = "Pod A";
    public int capacity = 2;
    public float countdownSeconds = 10f;
    [Tooltip("Loudness emitted every second of the countdown")] public float countdownNoise = 20f;
    public float launchNoise = 25f;
    [Tooltip("Flags needed before a console can start the launch")] public ShipFlags requiredFlags;
    public string lockedHint = "no power";
    public string readyHint = "ready - launch from the console inside";
    public Transform interior;
    public Renderer statusPlate; // tinted by state on every peer

    readonly NetworkVariable<PodState> state = new(PodState.Idle);
    readonly NetworkVariable<double> launchAt = new(0.0); // server time
    NetworkList<ulong> aboard; // replicated boarding order (client ids)
    readonly List<ulong> order = new(), inside = new(); // host scratch
    float pollTimer, noiseTimer;

    public PodState State => state.Value;
    public bool CanLaunch => state.Value == PodState.Idle && EscapeRules.Meets(requiredFlags, RoundManager.Flags, ItemKind.None, ItemKind.None);
    float SecondsLeft => Mathf.Max(0f, (float)(launchAt.Value - NetworkManager.ServerTime.Time));

    void Awake() => aboard = new NetworkList<ulong>(); // NetworkList must be created in Awake

    public override void OnNetworkSpawn()
    {
        state.OnValueChanged += OnStateChanged;
        Tint(state.Value);
    }

    public override void OnNetworkDespawn() => state.OnValueChanged -= OnStateChanged;

    void OnStateChanged(PodState _, PodState s) => Tint(s);

    void Tint(PodState s)
    {
        if (statusPlate == null) return;
        Color c = s == PodState.Counting ? new Color(1f, 0.5f, 0f) : s == PodState.Launched ? Color.gray : Color.green;
        statusPlate.material.SetColor("_BaseColor", c); // HDRP/Lit property
    }

    /// <summary>Host only (console effect). Starts the countdown if the pod is idle and its flags are set.</summary>
    public void StartLaunch()
    {
        if (!IsServer || !CanLaunch) return;
        launchAt.Value = NetworkManager.ServerTime.Time + countdownSeconds;
        state.Value = PodState.Counting;
        noiseTimer = 0f;
    }

    void Update()
    {
        if (!IsServer || !RoundManager.IsActive || state.Value == PodState.Launched) return;

        if ((pollTimer -= Time.deltaTime) <= 0f)
        {
            pollTimer = 0.2f;
            UpdateAboard();
        }
        if (state.Value != PodState.Counting) return;

        if ((noiseTimer -= Time.deltaTime) <= 0f)
        {
            noiseTimer = 1f;
            NoiseSystem.Emit(transform.position, countdownNoise, podName + " countdown");
        }
        if (NetworkManager.ServerTime.Time >= launchAt.Value) Launch();
    }

    // Living players whose body centre is inside the interior box, kept in boarding order.
    void UpdateAboard()
    {
        inside.Clear();
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            var po = client.PlayerObject;
            if (po == null || !po.TryGetComponent(out PlayerLife life) || !life.IsAlive) continue;
            Vector3 p = interior.InverseTransformPoint(po.transform.position + Vector3.up * 0.5f);
            if (Mathf.Abs(p.x) <= 0.5f && Mathf.Abs(p.y) <= 0.5f && Mathf.Abs(p.z) <= 0.5f) inside.Add(client.ClientId);
        }
        EscapeRules.UpdateBoarding(order, inside);

        bool same = aboard.Count == order.Count;
        for (int i = 0; same && i < order.Count; i++) same = aboard[i] == order[i];
        if (same) return;
        aboard.Clear();
        foreach (var id in order) aboard.Add(id);
    }

    void Launch()
    {
        UpdateAboard();
        for (int i = 0; i < order.Count && i < capacity; i++)
            if (NetworkManager.ConnectedClients.TryGetValue(order[i], out var client) && client.PlayerObject != null
                && client.PlayerObject.TryGetComponent(out PlayerLife life))
                life.Escape();
        state.Value = PodState.Launched;
        NoiseSystem.Emit(transform.position, launchNoise, podName + " launch");
        order.Clear();
        aboard.Clear();
    }

    public void ResetForRound(System.Random rng)
    {
        state.Value = PodState.Idle;
        launchAt.Value = 0.0;
        order.Clear();
        aboard.Clear();
        pollTimer = noiseTimer = 0f;
    }

    // ---------- HUD text (any peer) ----------

    public string StatusText
    {
        get
        {
            if (state.Value == PodState.Launched) return $"{podName}: launched";
            if (state.Value == PodState.Idle) return $"{podName}: {(CanLaunch ? readyHint : lockedHint)}";
            string extra = aboard.Count > capacity ? $" (+{aboard.Count - capacity} without a seat)" : "";
            return $"{podName}: launching in {SecondsLeft:0}s - aboard {Mathf.Min(aboard.Count, capacity)}/{capacity}{extra}";
        }
    }

    /// <summary>Whether the local player is aboard and has a seat, for the HUD. Empty when not aboard.</summary>
    public string LocalSeatText
    {
        get
        {
            int i = NetworkManager != null ? aboard.IndexOf(NetworkManager.LocalClientId) : -1;
            if (i < 0) return "";
            return i < capacity ? "  [you have a seat]" : "  [NO SEAT - pod is full]";
        }
    }
}
