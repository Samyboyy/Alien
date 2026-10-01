using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Alien > Audio > Acoustic Preview. A development aid for tuning the acoustic profiles in Play Mode: forces the listener's space (late
/// reverb and room tone) and plays a test sound in front of the listener with that space's early reflections, at a chosen distance.
/// Nothing here exists in a build, and it does nothing until you press its buttons; leaving Play Mode or closing the window ends the preview.
/// </summary>
public class AcousticPreviewWindow : EditorWindow
{
    AcousticSpace space = AcousticSpace.Corridor;
    bool forceListener;
    float distance = 3f;
    int sound;
    GameObject emitter;
    SfxPool pool;
    EmitterAcoustics acoustics;

    static readonly string[] Sounds = { "Creature footstep", "Door", "Snarl", "Tracker beep" };

    [MenuItem("Alien/Audio/Acoustic Preview")]
    static void Open() => GetWindow<AcousticPreviewWindow>("Acoustic Preview");

    void OnDisable() => End();

    void End()
    {
        ShipAcoustics.PreviewActive = false;
        if (emitter != null) DestroyImmediate(emitter);
        emitter = null;
        pool = null;
    }

    void OnGUI()
    {
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter Play Mode (and spawn a player) to preview the acoustic profiles.", MessageType.Info);
            End();
            return;
        }
        space = (AcousticSpace)EditorGUILayout.EnumPopup("Space", space);
        forceListener = EditorGUILayout.Toggle("Force listener space", forceListener);
        ShipAcoustics.PreviewActive = forceListener;
        ShipAcoustics.PreviewSpace = space;
        distance = EditorGUILayout.Slider("Distance (m)", distance, 0.5f, 25f);
        sound = EditorGUILayout.Popup("Sound", sound, Sounds);
        if (GUILayout.Button("Play")) Play();
        if (acoustics != null)
            EditorGUILayout.LabelField("Reflections", acoustics.HasReflectionSlot ? $"{acoustics.ReflectionNow:0.00} in {acoustics.Space}" : "none (dry or no slot)");
        EditorGUILayout.LabelField("Listener space", ShipAcoustics.CurrentSpace.ToString());
        if (GUILayout.Button("Stop preview")) { forceListener = false; End(); }
    }

    void Play()
    {
        var bank = AudioBank.Get();
        var listener = AudioListenerLocator.Current;
        if (bank == null || listener == null) { Debug.LogWarning("Acoustic preview: needs the AudioBank and an active AudioListener."); return; }
        if (emitter == null)
        {
            emitter = new GameObject("Acoustic Preview Emitter") { hideFlags = HideFlags.DontSave };
            pool = new SfxPool(emitter, 2, AudioCategory.World, 1f, 40f, customRolloff: AudioRouting.Rolloff(bank.monsterStepFullDistance, 40f));
            acoustics = emitter.AddComponent<EmitterAcoustics>();
            acoustics.occlude = false;
            acoustics.maxRange = 45f;
            acoustics.Attach(pool);
        }
        acoustics.useOverrideSpace = true;
        acoustics.overrideSpace = space;
        emitter.transform.position = listener.transform.position + listener.transform.forward * distance;
        AudioClip clip = sound switch
        {
            0 => bank.monsterFootsteps != null && bank.monsterFootsteps.Length > 0 ? bank.monsterFootsteps[0] : null,
            1 => bank.doorSound,
            2 => bank.snarl,
            _ => bank.trackerBeep,
        };
        if (clip == null) { Debug.LogWarning($"Acoustic preview: no clip for {Sounds[sound]}."); return; }
        pool.Play(clip, 0.9f, 1f);
    }

    void Update()
    {
        if (!Application.isPlaying && emitter != null) End();
    }
}
