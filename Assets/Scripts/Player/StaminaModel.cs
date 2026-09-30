/// <summary>
/// Sprint stamina, 0..1, with no Unity types so it can be unit tested (Editor/Tests).
/// Drains only while sprint-moving. Regeneration starts after a delay without sprinting, and any sprint frame restarts
/// that delay, so tapping sprint cannot regenerate. Reaching 0 exhausts: sprinting stays blocked until a fraction has
/// recovered, which also stops rapid start/stop abuse near zero.
/// Owner-authoritative prototype: this runs on the owning client (see NetworkFirstPersonController), so a modified client
/// could ignore it. The host still derives footstep noise from replicated movement speed, so cheating is not silent.
/// </summary>
public sealed class StaminaModel
{
    public float Value { get; private set; } = 1f;
    public bool Exhausted { get; private set; }
    public bool CanSprint => !Exhausted && Value > 0f;

    float sinceSprint;

    public void Reset()
    {
        Value = 1f;
        Exhausted = false;
        sinceSprint = 0f;
    }

    public void Tick(float dt, bool sprintMoving, float drainPerSecond, float regenDelay, float regenPerSecond, float resumeFraction)
    {
        if (sprintMoving)
        {
            sinceSprint = 0f;
            Value -= drainPerSecond * dt;
            if (Value <= 0f)
            {
                Value = 0f;
                Exhausted = true;
            }
        }
        else
        {
            sinceSprint += dt;
            if (sinceSprint >= regenDelay) Value = System.Math.Min(1f, Value + regenPerSecond * dt);
        }
        if (Exhausted && Value >= resumeFraction) Exhausted = false;
    }
}
