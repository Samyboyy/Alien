using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// While the local player is dead or escaped, moves their OWN camera (the only enabled camera and AudioListener)
/// behind a living teammate. Remote cameras, input and listeners stay off. Owner only.
/// </summary>
[RequireComponent(typeof(NetworkFirstPersonController))]
public class LocalSpectator : NetworkBehaviour
{
    public float distance = 3f;
    public float height = 0.8f;

    NetworkFirstPersonController controller;
    PlayerLife life;
    PlayerLife watched;
    GUIStyle big;

    bool Spectating => IsSpawned && IsOwner && life != null && !life.IsAlive;

    void Awake()
    {
        controller = GetComponent<NetworkFirstPersonController>();
        life = GetComponent<PlayerLife>();
    }

    void LateUpdate()
    {
        if (!Spectating) { watched = null; return; }

        // Target died or disconnected (destroyed object): pick another living teammate.
        if (watched == null || !watched.IsSpawned || !watched.IsAlive) watched = Cycle(1, null);

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.rightArrowKey.wasPressedThisFrame) watched = Cycle(1, watched);
            if (kb.leftArrowKey.wasPressedThisFrame) watched = Cycle(-1, watched);
        }

        // With nobody left to watch, look at our own body.
        Transform t = watched != null ? watched.transform : transform;
        Vector3 focus = t.position + Vector3.up * 1.4f;
        Vector3 desired = focus - t.forward * distance + Vector3.up * height;
        if (Physics.Linecast(focus, desired, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            desired = hit.point + hit.normal * 0.2f; // don't put the camera inside walls
        controller.playerCamera.transform.SetPositionAndRotation(desired, Quaternion.LookRotation(focus - desired));
    }

    // Living teammates other than us, in stable client-id order.
    PlayerLife Cycle(int dir, PlayerLife current)
    {
        var living = FindObjectsByType<PlayerLife>(FindObjectsSortMode.None)
            .Where(p => p != life && p.IsSpawned && p.IsAlive)
            .OrderBy(p => p.OwnerClientId).ToList();
        if (living.Count == 0) return null;
        int i = living.IndexOf(current);
        if (i < 0) return dir > 0 ? living[0] : living[^1];
        return living[(i + dir + living.Count) % living.Count];
    }

    void OnGUI()
    {
        if (!Spectating || RoundManager.Instance == null || RoundManager.Instance.State != RoundState.Active) return;
        big ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 28 };
        float cx = Screen.width * 0.5f;
        GUI.Label(new Rect(cx - 250, 30, 500, 45), life.Status == PlayerStatus.Escaped ? "You escaped" : "You died", big);
        if (!string.IsNullOrEmpty(life.DeathTip) && life.Status == PlayerStatus.Dead)
            GUI.Label(new Rect(cx - 250, 125, 500, 25), life.DeathTip, new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Italic });
        string who = watched != null ? $"Spectating: player {watched.OwnerClientId}" : "No living teammate to watch";
        GUI.Label(new Rect(cx - 250, 75, 500, 25), who, new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
        GUI.Label(new Rect(cx - 250, 100, 500, 25), "Left / Right arrow: switch teammate",
            new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
    }
}
