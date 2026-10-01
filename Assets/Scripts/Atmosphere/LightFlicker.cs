using UnityEngine;

/// <summary>
/// A failing light: long steady stretches, then a short burst of one to a few dips (FlickerSchedule, seeded so it always behaves the
/// same way). Scales the Light's intensity and, if set, the emissive glow of its fixture (through a MaterialPropertyBlock, so the shared
/// material is untouched). No allocations after enable, no network traffic. Off when the light's own switch or the scene's master
/// switch (ShipAtmosphere.flickerEnabled) is off.
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

    void OnEnable()
    {
        lightComponent = GetComponent<Light>();
        baseIntensity = lightComponent.intensity;
        schedule = new FlickerSchedule(seed, minGap, maxGap, minLevel, dipSeconds, maxDips);
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
    }

    void Update()
    {
        float level = schedule.Tick(Time.deltaTime, flickerEnabled && ShipAtmosphere.FlickerEnabled);
        if (Mathf.Approximately(level, applied)) return;
        applied = level;
        lightComponent.intensity = baseIntensity * level;
        if (fixture != null && block != null)
        {
            block.SetColor(EmissiveColor, baseEmissive * level);
            fixture.SetPropertyBlock(block);
        }
    }
}
