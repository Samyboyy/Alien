using UnityEngine;

/// <summary>
/// A failing light: long steady stretches, then a short burst of one to a few dips (FlickerSchedule, seeded so it always behaves the
/// same way). Scales the Light's intensity and, if set, the emissive glow of its fixture (through a MaterialPropertyBlock, so the shared
/// material is untouched). No allocations after enable, no network traffic. Off when the light's own switch or the scene's master
/// switch (ShipAtmosphere.flickerEnabled) is off.
/// When the AudioBank has a crackle clip, the start of a burst (not every dip) plays a short piece of it at the light, at most once per
/// flickerCooldown, with the reflections and occlusion of any world sound. Local and cosmetic like the flicker itself: never a noise
/// event, so the creature does not hear it.
/// </summary>
[RequireComponent(typeof(Light))]
public class LightFlicker : MonoBehaviour
{
    public bool flickerEnabled = true;
    [Tooltip("Same seed, same flicker")] public int seed = 1;
    [Tooltip("Steady seconds between flicker bursts (random in the range)")] public float minGap = 5f;
    public float maxGap = 14f;
    [Tooltip("Lowest brightness during a dip (fraction of normal)")] [Range(0f, 1f)] public float minLevel = 0.25f;
    [Tooltip("Typical length of one dip (s)")] public float dipSeconds = 0.07f;
    [Tooltip("Most dips in one burst")] public int maxDips = 3;
    [Tooltip("Optional fixture whose emissive glow follows the light")] public Renderer fixture;

    static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");
    Light lightComponent;
    FlickerSchedule schedule;
    MaterialPropertyBlock block;
    Color baseEmissive;
    float baseIntensity, applied = -1f;
    AudioBank bank;
    SfxPool crackle;
    System.Random crackleRng;
    double nextCrackle;

    void OnEnable()
    {
        lightComponent = GetComponent<Light>();
        baseIntensity = lightComponent.intensity;
        schedule = new FlickerSchedule(seed, minGap, maxGap, minLevel, dipSeconds, maxDips);
        bank = AudioBank.Get();
        if (crackle == null && bank != null && bank.flickerCrackle != null)
        {
            var host = new GameObject("Flicker Audio");
            host.transform.SetParent(transform, false);
            crackle = new SfxPool(host, 1, AudioCategory.World, 1f, bank.flickerMaxDistance, customRolloff: AudioRouting.Rolloff(1.5f, bank.flickerMaxDistance));
            var acoustics = host.AddComponent<EmitterAcoustics>();
            acoustics.ignoreRoot = transform;
            acoustics.maxRange = bank.flickerMaxDistance + 1f;
            acoustics.Attach(crackle);
            crackleRng = new System.Random(seed * 7919 + 13);
        }
        nextCrackle = 0;
        if (fixture != null && fixture.sharedMaterial != null && fixture.sharedMaterial.HasProperty(EmissiveColor))
        {
            block ??= new MaterialPropertyBlock();
            baseEmissive = fixture.sharedMaterial.GetColor(EmissiveColor);
        }
        applied = -1f;
    }

    void OnDisable()
    {
        if (lightComponent != null) lightComponent.intensity = baseIntensity;
        if (fixture != null && block != null) fixture.SetPropertyBlock(null);
        crackle?.StopAll();
    }

    void Update()
    {
        float level = schedule.Tick(Time.deltaTime, flickerEnabled && ShipAtmosphere.FlickerEnabled);
        if (schedule.EventStarted && crackle != null && Time.timeAsDouble >= nextCrackle) Crackle();
        if (Mathf.Approximately(level, applied)) return;
        applied = level;
        lightComponent.intensity = baseIntensity * level;
        if (fixture != null && block != null)
        {
            block.SetColor(EmissiveColor, baseEmissive * level);
            fixture.SetPropertyBlock(block);
        }
    }

    // A short piece from a seeded place in the long electrical recording, so each burst sounds a little different.
    void Crackle()
    {
        var clip = bank.flickerCrackle;
        float length = Mathf.Min(bank.flickerPieceLength, clip.length);
        float start = (float)crackleRng.NextDouble() * Mathf.Max(0f, clip.length - length);
        float pitch = 0.95f + 0.1f * (float)crackleRng.NextDouble();
        crackle.PlaySegment(clip, start, length, bank.flickerVolume, pitch, Mathf.Min(0.15f, length * 0.4f));
        nextCrackle = Time.timeAsDouble + bank.flickerCooldown;
    }
}
