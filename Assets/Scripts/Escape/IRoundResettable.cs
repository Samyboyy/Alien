/// <summary>
/// Host-side hooks RoundManager calls on every scene object implementing this (doors, items, pods, creature,
/// inventories). The RNG is seeded per round, so a fixed seed reproduces item placement.
/// </summary>
public interface IRoundResettable
{
    void ResetForRound(System.Random rng);

    void OnRoundOver() { }
}
