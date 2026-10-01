using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Scene-wide settings for the greybox ship atmosphere (added by Alien > Add Ship Atmosphere). Cosmetic only, no network traffic.
/// The master flicker switch is here so failing lights can be turned off for players sensitive to flicker (accessibility).
/// </summary>
public class ShipAtmosphere : MonoBehaviour
{
    static ShipAtmosphere instance;

    [Tooltip("Master switch for every flickering light. Off: all lights stay steady (accessibility).")] public bool flickerEnabled = true;
    [Tooltip("Documented budget for realtime lights in the ship; the editor validation warns above it")] public int recommendedMaxLights = 48;

    /// <summary>True unless a ShipAtmosphere in the scene has flicker switched off.</summary>
    public static bool FlickerEnabled => instance == null || instance.flickerEnabled;

    void OnEnable() => instance = this;

    void OnDisable()
    {
        if (instance == this) instance = null;
    }
}

