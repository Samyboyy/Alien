using Unity.Netcode;

/// <summary>
/// Replicated Alive / Dead / Escaped status. Only the host changes it; each change from Alive applies once,
/// and there is no revival mid-round. Leaving play drops the carried item where the player was.
/// A kill also sends the victim (only) one short tip about how the creature found them; it is cleared on revival.
/// </summary>
public class PlayerLife : NetworkBehaviour
{
    readonly NetworkVariable<PlayerStatus> status = new(PlayerStatus.Alive);

    public PlayerStatus Status => status.Value;
    public bool IsAlive => status.Value == PlayerStatus.Alive;

    /// <summary>Owner only: the tip for the latest death this round, or null.</summary>
    public string DeathTip { get; private set; }

    public override void OnNetworkSpawn() => status.OnValueChanged += OnStatusChanged;

    public override void OnNetworkDespawn() => status.OnValueChanged -= OnStatusChanged;

    void OnStatusChanged(PlayerStatus _, PlayerStatus now)
    {
        if (now == PlayerStatus.Alive) DeathTip = null; // new round
    }

    /// <summary>Host only. Ignored unless alive. The reason only picks the tip text.</summary>
    public void Kill(DetectionReason reason = DetectionReason.Unknown)
    {
        if (!IsServer || !IsAlive) return;
        Leave(PlayerStatus.Dead);
        DeathTipRpc((byte)reason);
    }

    [Rpc(SendTo.Owner)]
    void DeathTipRpc(byte reason) => DeathTip = SightRules.Tip((DetectionReason)reason);

    /// <summary>Host only. Called by an escape pod at launch. Ignored unless alive.</summary>
    public void Escape() => Leave(PlayerStatus.Escaped);

    /// <summary>Host only. Used by RoundManager at round start / restart.</summary>
    public void Revive()
    {
        if (IsServer) status.Value = PlayerStatus.Alive;
    }

    void Leave(PlayerStatus outcome)
    {
        if (!IsServer || !IsAlive) return;
        if (TryGetComponent(out PlayerInventory inventory)) inventory.DropHeld(transform.position);
        status.Value = outcome;
    }
}
