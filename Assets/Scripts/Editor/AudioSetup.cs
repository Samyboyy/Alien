using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Alien > Setup Audio. Finds the clips in Assets/Sounds by file name, fixes their import settings where needed, and
/// creates or refreshes Assets/Resources/AudioBank.asset (the clip list and the measured riser loudness envelope). Safe to re-run: volumes
/// and other tuning values in an existing bank are never touched, no scene or prefab is edited (the audio components are added
/// at runtime), and every import change is logged.
///
/// Import changes: the 3D sounds (footsteps and monster footsteps) become mono; the snarl stays stereo as recorded so they spatialise, and short sounds plus the
/// riser decompress on load (the riser is played backwards and seeked, which needs uncompressed data in memory).
/// </summary>
public static class AudioSetup
{
    const string Folder = "Assets/Sounds", BankPath = "Assets/Resources/AudioBank.asset";

    [MenuItem("Alien/Setup Audio")]
    static void Setup()
    {
        AssetDatabase.Refresh();
        var missing = new List<string>();

        AudioClip Load(string name, bool mono, bool decompress, bool pcm = false, bool rate44 = false)
        {
            string path = $"{Folder}/{name}.wav";
            if (!File.Exists(path)) path = $"{Folder}/{name}.mp3";
            if (!File.Exists(path)) { missing.Add(name); return null; }
            Prepare(path, mono, decompress, pcm, rate44);
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        var bank = AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
        if (bank == null)
        {
            Directory.CreateDirectory("Assets/Resources");
            bank = ScriptableObject.CreateInstance<AudioBank>();
            AssetDatabase.CreateAsset(bank, BankPath);
            Debug.Log($"Created {BankPath}.");
        }

        var steps = new List<AudioClip>();
        for (int i = 1; i <= 64 && File.Exists($"{Folder}/MONSTER_FOOTSTEP_{i}.wav"); i++) steps.Add(Load($"MONSTER_FOOTSTEP_{i}", true, true));

        var so = new SerializedObject(bank);
        Set(so, "doorSound", Load("FX_DOOR_OPEN_01", true, true));
        Set(so, "footstepQuiet", Load("FOOTSTEP_QUIET", true, true));
        Set(so, "footstepRegular1", Load("FOOTSTEP_REGULAR_1", true, true));
        Set(so, "footstepRegular2", Load("FOOTSTEP_REGULAR_2", true, true));
        Set(so, "snarl", Load("MONSTER_SNARL_1", false, true)); // stereo, as recorded
        // The long riser is seeked and cut between places, so it must be uncompressed in memory; 44.1 kHz halves that.
        var riser = Load("TENSION_RISER_2", false, true, true, true);
        Set(so, "tensionRiser", riser);
        var atmos = Load("ATMOS_2", false, false, false, true);
        var atmosProp = so.FindProperty("atmos");
        if (atmos != null && atmosProp.objectReferenceValue != atmos)
        {
            // A different ambience has a different loudness (ATMOS_2 is about 12 dB louder than ATMOS_1): reset its volume once.
            bool had = atmosProp.objectReferenceValue != null;
            atmosProp.objectReferenceValue = atmos;
            if (had) { so.FindProperty("atmosVolume").floatValue = 0.1f; Debug.Log("Atmos clip changed: atmosVolume reset to 0.1 (tune it on the AudioBank)."); }
        }
        // Breathing and heartbeat are flat (2D): mono also lets Unity normalise them, which matters for the very quiet breathing.
        var heart = Load("TENSION_HEARTBEAT_01", true, true, false, true);
        Set(so, "heartbeat", heart);
        Set(so, "scaredBreath", Load("TENSION_SCARED_BREATHING_FEMALE_01", true, false, false, true));
        Set(so, "runBreath", Load("TENSION_RUN_BREATH_FEMALE_01", true, false, false, true));
        if (heart != null)
        {
            float beat = MeasureBeatStart(heart);
            if (beat > 0f) so.FindProperty("heartbeatBeatStart").floatValue = beat;
            else Debug.LogWarning("Could not measure the heartbeat; keeping heartbeatBeatStart.");
        }

        var stepsProp = so.FindProperty("monsterFootsteps");
        stepsProp.arraySize = steps.Count;
        for (int i = 0; i < steps.Count; i++) stepsProp.GetArrayElementAtIndex(i).objectReferenceValue = steps[i];
        if (steps.Count == 0) missing.Add("MONSTER_FOOTSTEP_1..N");

        if (riser != null)
        {
            const float step = 0.1f;
            var env = MeasureEnvelope(riser, step, out float peak);
            if (env != null)
            {
                so.FindProperty("riserPeakSeconds").floatValue = peak;
                so.FindProperty("riserEnvelopeStep").floatValue = step;
                var ep = so.FindProperty("riserEnvelope");
                ep.arraySize = env.Length;
                for (int i = 0; i < env.Length; i++) ep.GetArrayElementAtIndex(i).floatValue = env[i];
            }
            else Debug.LogWarning("Could not measure the riser; keeping its current envelope and peak.");
        }

        AssignVentClips(so);
        MigrateBank(so);
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(bank);
        AssetDatabase.SaveAssets();

        Debug.Log($"Audio bank ready: {steps.Count} monster footsteps, riser peak at {bank.riserPeakSeconds:0.0} s. Volumes and tuning live on {BankPath}.");
        if (missing.Count > 0) Debug.LogError($"Audio clips not found in {Folder}: {string.Join(", ", missing)}");
    }

    // One-off tuning changes, applied once per version. A value is only changed while it still holds the previous default, so your own
    // later edits are never overwritten; every change (and every kept value) is logged. Also used by
    // Alien > Apply Creature Pressure And Audio Tuning. Returns how many values changed.
    internal static int MigrateBank(SerializedObject so)
    {
        var version = so.FindProperty("tuningVersion");
        int changed = 0;
        void FromDefault(string field, float previous, float value, int v)
        {
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"AudioBank has no field '{field}'."); return; }
            if (Mathf.Approximately(p.floatValue, value)) return;
            if (!Mathf.Approximately(p.floatValue, previous))
            {
                Debug.Log($"AudioBank {field}: kept your value {p.floatValue} (tuning version {v} would set {value}).");
                return;
            }
            Debug.Log($"AudioBank {field}: {p.floatValue} -> {value} (tuning version {v})");
            p.floatValue = value;
            changed++;
        }
        if (version.intValue < 2)
        {
            FromDefault("riserVolume", 0.8f, 0.5f, 2);           // quieter; the new riser is also lower in pitch
            FromDefault("scaredBreathVolume", 0.5f, 0.3f, 2);    // player breathing was too loud
            FromDefault("runBreathVolume", 0.6f, 0.35f, 2);
            FromDefault("riserWindow", 1.2f, 8f, 2);             // the new riser is a long sustained drone: longer stretches between cuts,
            FromDefault("riserTolerance", 0.5f, 3f, 2);
            FromDefault("riserCrossfade", 0.45f, 1.5f, 2);       // and slower blends
            version.intValue = 2;
        }
        if (version.intValue < 3)
        {
            FromDefault("scaredBreathVolume", 0.3f, 0.15f, 3);   // breathing still too loud
            FromDefault("runBreathVolume", 0.35f, 0.18f, 3);
            FromDefault("runBreathOnStamina", 0.35f, 0.2f, 3);   // only after a real effort, not every sprint...
            FromDefault("runBreathOffStamina", 0.7f, 0.5f, 3);   // ...and it ends sooner (plus a cooldown between spells)
            version.intValue = 3;
        }
        if (version.intValue < 4)
        {
            // Creature footsteps carried too far: full volume only close, a perceptual fall, silent by about 22 m, fully 3D (a flat
            // share never fades with distance), and a touch quieter.
            FromDefault("monsterStepFullDistance", 6f, 3.5f, 4);
            FromDefault("monsterStepMaxDistance", 34f, 22f, 4);
            FromDefault("monsterStepSpatial", 0.85f, 1f, 4);
            FromDefault("monsterFootstepVolume", 1f, 0.9f, 4);
            version.intValue = 4;
        }
        return changed;
    }

    // Optional dedicated vent clips: any file in Assets/Sounds whose name contains VENT and ENTER / TRAVEL (or RATTLE, DUCT) / WARN / EXIT.
    // Without them the creature uses a short piece of the door sound for the grille and the warning, and the duct stays silent.
    static void AssignVentClips(SerializedObject so)
    {
        var files = Directory.GetFiles(Folder).Where(f => f.EndsWith(".wav") || f.EndsWith(".mp3") || f.EndsWith(".ogg")).Select(f => f.Replace('\\', '/')).ToArray();
        string Find(params string[] words) => files.FirstOrDefault(f =>
        {
            string n = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
            return n.Split('_', '-', ' ').Contains("VENT") && words.Any(n.Contains); // the word VENT, not EVENT
        });
        var found = new List<string>();
        foreach (var (field, words) in new[] { ("ventEnter", new[] { "ENTER" }), ("ventTravel", new[] { "TRAVEL", "RATTLE", "DUCT" }), ("ventWarning", new[] { "WARN" }), ("ventExit", new[] { "EXIT" }) })
        {
            string path = Find(words);
            if (path == null) continue;
            Prepare(path, true, true, false, true);
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            so.FindProperty(field).objectReferenceValue = clip;
            found.Add($"{field} = {Path.GetFileName(path)}");
        }
        if (found.Count > 0) Debug.Log($"Vent clips: {string.Join(", ", found)}.");
        else Debug.Log("No dedicated vent clips in Assets/Sounds (names containing VENT and ENTER, TRAVEL, WARN or EXIT): the grille and the pre-emergence warning use a short piece of the door sound, the duct rattle is silent.");
    }

    static void Set(SerializedObject so, string field, AudioClip clip)
    {
        var p = so.FindProperty(field);
        if (clip != null) p.objectReferenceValue = clip; // a missing optional clip leaves the old reference alone
    }

    // Brings the clip's import settings in line. Logs what it changes.
    static void Prepare(string path, bool mono, bool decompress, bool pcm, bool rate44)
    {
        if (AssetImporter.GetAtPath(path) is not AudioImporter imp)
        {
            AssetDatabase.ImportAsset(path);
            imp = AssetImporter.GetAtPath(path) as AudioImporter;
            if (imp == null) return;
        }
        var s = imp.defaultSampleSettings;
        bool dirty = false;
        if (imp.forceToMono != mono) { imp.forceToMono = mono; dirty = true; }
        if (decompress && s.loadType != AudioClipLoadType.DecompressOnLoad) { s.loadType = AudioClipLoadType.DecompressOnLoad; dirty = true; }
        if (pcm && s.compressionFormat != AudioCompressionFormat.PCM) { s.compressionFormat = AudioCompressionFormat.PCM; dirty = true; }
        // 96 kHz sources are twice the memory for no audible gain on these sounds.
        if (rate44 && (s.sampleRateSetting != AudioSampleRateSetting.OverrideSampleRate || s.sampleRateOverride != 44100))
        {
            s.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
            s.sampleRateOverride = 44100;
            dirty = true;
        }
        if (!dirty) return;
        imp.defaultSampleSettings = s;
        imp.SaveAndReimport();
        Debug.Log($"Audio import updated: {path} (mono {mono}, decompress on load {decompress}, PCM {pcm}).");
    }

    // Start of the first clear beat after the first second (a little before its peak), in seconds.
    static float MeasureBeatStart(AudioClip clip)
    {
        int frames = Mathf.Min(clip.samples, clip.frequency * 6), ch = clip.channels;
        var data = new float[frames * ch];
        if (!clip.GetData(data, 0)) return 0f;
        int hop = Mathf.Max(1, clip.frequency / 200); // 5 ms
        var env = new List<float>();
        for (int start = 0; start + hop <= frames; start += hop)
        {
            float peak = 0f;
            for (int f = start; f < start + hop; f++)
                for (int c = 0; c < ch; c++) peak = Mathf.Max(peak, Mathf.Abs(data[f * ch + c]));
            env.Add(peak);
        }
        float max = 0f;
        foreach (float e in env) max = Mathf.Max(max, e);
        if (max <= 0f) return 0f;
        for (int i = 200; i < env.Count; i++) // skip the first second
            if (env[i] > 0.5f * max) return Mathf.Max(0f, (i - 8) * 0.005f);
        return 0f;
    }

    // Loudness over time: RMS of a 0.2 s window every `step` seconds, normalised to the loudest and square-rooted so equal steps
    // sound like equal steps. Null when the samples cannot be read.
    static float[] MeasureEnvelope(AudioClip clip, float step, out float peakSeconds)
    {
        peakSeconds = 0f;
        var data = new float[clip.samples * clip.channels];
        if (!clip.GetData(data, 0)) return null;
        int frames = clip.samples, ch = clip.channels;
        int window = Mathf.Max(1, (int)(clip.frequency * 0.2f)), hop = Mathf.Max(1, (int)(clip.frequency * step));
        var env = new List<float>();
        for (int start = 0; start < frames; start += hop)
        {
            int end = Mathf.Min(frames, start + window);
            double sum = 0;
            for (int f = start; f < end; f++)
                for (int c = 0; c < ch; c++) { float v = data[f * ch + c]; sum += v * v; }
            env.Add((float)System.Math.Sqrt(sum / Mathf.Max(1, (end - start) * ch)));
        }
        float max = 0f;
        int peak = 0;
        for (int i = 0; i < env.Count; i++) if (env[i] > max) { max = env[i]; peak = i; }
        if (max <= 0f) return null;
        for (int i = 0; i < env.Count; i++) env[i] = Mathf.Sqrt(env[i] / max);
        peakSeconds = (peak + 1f) * step;
        return env.ToArray();
    }
}
