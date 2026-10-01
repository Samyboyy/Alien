using System.Linq;
using Unity.Netcode;
using UnityEngine;

public enum RoundState : byte { Lobby, Active, Over }

/// <summary>
/// Host-authoritative round flow: Lobby -> (host: Start Round) -> Active -> (nobody left in play, or every pod gone) -> Over
/// -> (host: Restart) -> Active. Joining is only approved in the Lobby, so nobody can bypass death by reconnecting mid-round.
/// Reset is in place (no scene reload, connections stay): every IRoundResettable in the scene is reset with a seeded RNG,
/// objective flags and noises are cleared, and players are revived at their own spawn slots.
/// Also replicates the objective flags and draws the lobby, objectives and results screens.
/// </summary>
public class RoundManager : NetworkBehaviour
{
    public static RoundManager Instance { get; private set; }

    [Tooltip("0 = new random seed every round. Set to reproduce item placement.")] public int fixedSeed;

    readonly NetworkVariable<RoundState> state = new(RoundState.Lobby);
    readonly NetworkVariable<ShipFlags> flags = new(ShipFlags.None);
    readonly NetworkVariable<int> seed = new(0);
    EscapePod[] pods = new EscapePod[0];
    GUIStyle center, big, left;

    public RoundState State => state.Value;
    public static bool IsActive => Instance != null && Instance.state.Value == RoundState.Active;
    /// <summary>The replicated seed of the current round (hosts and clients). Scenario items derive their resting place from it, so a round is reproducible from this number.</summary>
    public static int Seed => Instance != null ? Instance.seed.Value : 0;
    public static ShipFlags Flags => Instance != null ? Instance.flags.Value : ShipFlags.None;

    void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy(); // NetworkBehaviour cleanup
    }

    public override void OnNetworkSpawn()
    {
        state.OnValueChanged += OnStateChanged;
        pods = FindObjectsByType<EscapePod>(FindObjectsSortMode.None);
        if (IsServer) state.Value = RoundState.Lobby; // fresh session
    }

    public override void OnNetworkDespawn() => state.OnValueChanged -= OnStateChanged;

    /// <summary>Host only.</summary>
    public void SetFlags(ShipFlags add)
    {
        if (IsServer) flags.Value |= add;
    }

    // Every peer: free the cursor on the end screen (host needs it for the button), recapture on restart.
    void OnStateChanged(RoundState prev, RoundState now)
    {
        var local = NetworkManager != null ? NetworkManager.LocalClient?.PlayerObject : null;
        if (local == null || !local.TryGetComponent(out NetworkFirstPersonController c)) return;
        if (now == RoundState.Over) c.SetCursor(false);
        else if (prev == RoundState.Over) c.SetCursor(true);
    }

    void Update()
    {
        if (!IsServer || state.Value != RoundState.Active) return;

        // Counts connected participants only, so disconnects and solo tests are covered.
        int participants = 0, alive = 0;
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.PlayerObject == null || !client.PlayerObject.TryGetComponent(out PlayerLife life)) continue;
            participants++;
            if (life.IsAlive) alive++;
        }
        int launched = pods.Count(p => p != null && p.State == PodState.Launched);
        if (!EscapeRules.IsRoundOver(participants, alive, pods.Length, launched)) return;

        state.Value = RoundState.Over;
        foreach (var r in Resettables()) r.OnRoundOver();
    }

    // Sorted by name so a fixed seed hands out random numbers in the same order every time.
    static IRoundResettable[] Resettables() =>
        FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None).OfType<IRoundResettable>()
            .OrderBy(r => ((MonoBehaviour)r).name, System.StringComparer.Ordinal).ToArray();

    /// <summary>Host only. Used for both Start (from Lobby) and Restart (from Over).</summary>
    void BeginRound()
    {
        seed.Value = fixedSeed != 0 ? fixedSeed : System.Environment.TickCount;
        var rng = new System.Random(seed.Value);
        flags.Value = ShipFlags.None;
        NoiseSystem.Clear();
        DevChecks.NewRound();
        foreach (var r in Resettables()) r.ResetForRound(rng);
        foreach (var client in NetworkManager.ConnectedClientsList)
        {
            if (client.PlayerObject == null) continue;
            if (client.PlayerObject.TryGetComponent(out PlayerLife life)) life.Revive();
            if (client.PlayerObject.TryGetComponent(out NetworkFirstPersonController c)) c.RespawnRpc();
        }
        state.Value = RoundState.Active;
    }

    void OnGUI()
    {
        ConnectionUI.ExtraPanel = Rect.zero;
        if (!IsSpawned) return;

        center ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
        big ??= new GUIStyle(center) { fontSize = 40 };
        left ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
        float cx = Screen.width * 0.5f;
        var button = new Rect(cx - 100, Screen.height - 110, 200, 40);

        if (state.Value == RoundState.Lobby)
        {
            GUI.Label(new Rect(cx - 250, 10, 500, 25), "Lobby - the creature is asleep until the host starts the round", center);
            if (!IsServer) return;
            GUI.Label(new Rect(cx - 250, Screen.height - 140, 500, 25), "Host: press Esc to free the cursor, then click Start Round", center);
            ConnectionUI.ExtraPanel = button;
            if (GUI.Button(button, "Start Round")) BeginRound();
        }
        else if (state.Value == RoundState.Active)
        {
            if (pods.Length == 0) return; // levels without escape objectives (NetTest)
            var lines = pods.Where(p => p != null).Select(p => p.StatusText + p.LocalSeatText).ToArray();
            var box = new Rect(Screen.width - 610, 10, 600, 30 + 20 * lines.Length);
            GUI.Box(box, GUIContent.none);
            GUI.Label(new Rect(box.x + 8, box.y + 4, box.width - 16, box.height - 8), $"Escape pods   (round seed {seed.Value})\n" + string.Join("\n", lines), left);
        }
        else
        {
            var players = FindObjectsByType<PlayerLife>(FindObjectsSortMode.None).OrderBy(p => p.OwnerClientId).ToArray();
            int escaped = players.Count(p => p.Status == PlayerStatus.Escaped);
            float y = Screen.height * 0.3f;
            GUI.Label(new Rect(cx - 250, y, 500, 60), escaped == 0 ? "Everyone died" : $"{escaped} of {players.Length} escaped", big);
            y += 60;
            foreach (var p in players)
            {
                string outcome = p.Status == PlayerStatus.Escaped ? "Escaped" : p.Status == PlayerStatus.Dead ? "Died" : "Died (left behind)";
                string you = p.OwnerClientId == NetworkManager.LocalClientId ? " (you)" : "";
                GUI.Label(new Rect(cx - 250, y, 500, 22), $"Player {p.OwnerClientId}{you}: {outcome}", center);
                y += 22;
            }
            if (IsServer)
            {
                ConnectionUI.ExtraPanel = button;
                if (GUI.Button(button, "Restart Round")) BeginRound();
            }
            else
                GUI.Label(new Rect(cx - 250, y + 10, 500, 30), "Waiting for host", center);
        }
    }
}
