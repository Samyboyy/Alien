using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Minimal IMGUI host/join panel (LAN/localhost only: no Relay, so no internet play).
/// Also enforces the player cap through connection approval.
/// </summary>
public class ConnectionUI : MonoBehaviour
{
    public NetworkManager networkManager;
    public GameObject lobbyCamera; // active only while no local player exists
    public string address = "127.0.0.1";
    public ushort port = 7777;
    public int maxPlayers = 4;

    static Rect panelRect;
    public static Rect ExtraPanel; // other on-screen buttons (RoundManager) that must not recapture the cursor
    string status = "Not connected";
    bool resetPending;

    public static bool IsPointerOverPanel()
    {
        var m = Mouse.current;
        if (m == null) return false;
        Vector2 p = m.position.ReadValue();
        var gui = new Vector2(p.x, Screen.height - p.y);
        return panelRect.Contains(gui) || ExtraPanel.Contains(gui);
    }

    void Awake()
    {
        if (networkManager == null) networkManager = NetworkManager.Singleton;
        Application.runInBackground = true; // keep host alive when another window has focus
        networkManager.NetworkConfig.ConnectionApproval = true;
        networkManager.ConnectionApprovalCallback = Approve;
        var utp = networkManager.GetComponent<UnityTransport>();
        utp.MaxConnectAttempts = 4; // fail in a few seconds on a bad address instead of ~60s
        utp.ConnectTimeoutMS = 1000;
    }

    void OnEnable()
    {
        networkManager.OnClientDisconnectCallback += OnDisconnected;
        networkManager.OnServerStopped += OnServerStopped;
    }

    void OnDisable()
    {
        if (networkManager == null) return;
        networkManager.OnClientDisconnectCallback -= OnDisconnected;
        networkManager.OnServerStopped -= OnServerStopped;
    }

    void Approve(NetworkManager.ConnectionApprovalRequest req, NetworkManager.ConnectionApprovalResponse res)
    {
        // Join only in the lobby, so reconnecting cannot bring a dead player back to life mid-round.
        if (req.ClientNetworkId != NetworkManager.ServerClientId && RoundManager.Instance != null
            && RoundManager.Instance.State != RoundState.Lobby)
        {
            res.Approved = false;
            res.CreatePlayerObject = false;
            res.Reason = "Round in progress - join again from the lobby";
            return;
        }
        bool full = networkManager.ConnectedClientsIds.Count >= maxPlayers;
        res.Approved = !full;
        res.CreatePlayerObject = !full;
        if (full) res.Reason = $"Server full ({maxPlayers} players)";
    }

    void OnDisconnected(ulong clientId)
    {
        if (networkManager.IsServer) return; // host side: a remote client left, nothing to reset
        string reason = networkManager.DisconnectReason;
        status = string.IsNullOrEmpty(reason) ? $"Connection failed or lost ({address}:{port})" : $"Disconnected: {reason}";
        resetPending = true;
    }

    void OnServerStopped(bool wasHost) => status = "Host stopped";

    void Update()
    {
        // Shut down from Update, not from inside the disconnect callback.
        if (resetPending)
        {
            resetPending = false;
            if (networkManager.IsListening && !networkManager.ShutdownInProgress) networkManager.Shutdown();
        }
        if (lobbyCamera != null)
            lobbyCamera.SetActive(!networkManager.IsListening || networkManager.SpawnManager?.GetLocalPlayerObject() == null);
    }

    void OnGUI()
    {
        panelRect = Rect.zero;
        if (Cursor.lockState == CursorLockMode.Locked) return; // playing: hide menu

        panelRect = new Rect(10, 10, 300, 170);
        GUILayout.BeginArea(panelRect, GUI.skin.box);
        var nm = networkManager;
        if (!nm.IsListening)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Address", GUILayout.Width(60));
            address = GUILayout.TextField(address);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Host")) Host();
            if (GUILayout.Button("Join")) Join();
        }
        else
        {
            if (GUILayout.Button("Disconnect")) { status = "Disconnected"; nm.Shutdown(); }
            int n = FindObjectsByType<NetworkFirstPersonController>(FindObjectsSortMode.None).Length;
            GUILayout.Label(nm.IsHost ? $"Hosting - players {n}/{maxPlayers}" : nm.IsConnectedClient ? $"Connected - players {n}/{maxPlayers}" : "Connecting...");
        }
        GUILayout.Label(status);
        GUILayout.Label("Esc: free cursor   Click: recapture");
        GUILayout.EndArea();
    }

    void Host()
    {
        var utp = networkManager.GetComponent<UnityTransport>();
        utp.SetConnectionData("127.0.0.1", port, "0.0.0.0"); // listen on all interfaces for LAN
        status = networkManager.StartHost() ? "Hosting" : "Failed to start host (port in use?)";
    }

    void Join()
    {
        var utp = networkManager.GetComponent<UnityTransport>();
        utp.SetConnectionData(address.Trim(), port);
        status = networkManager.StartClient() ? $"Connecting to {address}:{port}..." : "Failed to start client";
    }
}
