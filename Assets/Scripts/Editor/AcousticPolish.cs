using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Alien > Apply Acoustic Polish. Brings an existing project up to the interior acoustic polish without rebuilding anything:
///  - AudioBank: assigns the new clips only where a slot is still empty (ATMOS_1 air tone, the light-flicker crackle, the distant roaming
///    call, the faster snarl for a chase start), fixes their import settings, measures the seamless loop regions of the beds and the separate
///    calls inside the roaming recording (only while those fields are still unset), and adds a tunable profile for every acoustic space,
///    filling the new early-reflection and room-tone values from the built-in defaults where a profile has never been configured;
///  - Ship scene: the two big holds (Cargo, Storage) become LargeOpen when their zones still have the old LargeMachinery value, and the vent
///    mouths' data is repaired. The scene is saved only when something changed.
/// Menu: Alien > Add Motion Tracker. Adds PlayerMotionTracker to the Player prefab once (also done by Add Escape System and fresh builds).
/// Both are safe to re-run; every change is logged and deliberate Inspector edits are left alone.
/// </summary>
public static class AcousticPolish
{
    const string Folder = "Assets/Sounds", BankPath = "Assets/Resources/AudioBank.asset", ScenePath = "Assets/Scenes/Ship.unity";

    [MenuItem("Alien/Apply Acoustic Polish")]
    static void Apply()
    {
        var bank = AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
        if (bank == null) { Debug.LogError("No AudioBank: run Alien > Setup Audio first."); return; }
        int bankChanges = PolishBank(bank);

        if (!File.Exists(ScenePath)) { Debug.LogWarning("Ship scene missing: only the AudioBank was updated."); return; }
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
        int sceneChanges = MigrateZones() + ShipBuilder.RepairVentEntrances();
        ShipBuilder.ValidateAtmosphere();
        if (sceneChanges > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }
        Debug.Log($"Acoustic polish: {bankChanges} AudioBank change(s), {sceneChanges} scene change(s){(sceneChanges > 0 ? ", Ship saved" : ", Ship left untouched")}.");
    }

    [MenuItem("Alien/Add Motion Tracker")]
    static void AddMotionTracker()
    {
        ShipBuilder.AddEscapeSystem(); // adds every missing player component, PlayerMotionTracker included; saves only on change
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrototypeSetup.PrefabPath);
        Debug.Log(prefab != null && prefab.GetComponent<PlayerMotionTracker>() != null
            ? "Motion tracker: on the Player prefab (Q toggles it in play)."
            : "Motion tracker: could not add it to the Player prefab.");
    }

    // ---------- AudioBank ----------

    /// <summary>Clips, measured regions and profiles. Also run by Alien > Setup Audio. Returns how many things changed.</summary>
    internal static int PolishBank(AudioBank bank)
    {
        int changed = 0;
        var so = new SerializedObject(bank);
        // Beds and long recordings stay compressed in memory (they are minutes long); the crackle and the call are mono (and so normalised:
        // the crackle recording is very quiet) because they are positioned in 3D.
        changed += Assign(so, "airTone", "ATMOS_1", false, AudioClipLoadType.CompressedInMemory);
        changed += Assign(so, "flickerCrackle", "FX_LIGHTFLICKER_01", true, AudioClipLoadType.CompressedInMemory);
        changed += Assign(so, "roamCall", "MONSTER_WHISTLE_FAR_01", true, AudioClipLoadType.CompressedInMemory);
        changed += Assign(so, "chaseSnarl", "MONSTER_SNARL_1_FASTER", false, AudioClipLoadType.DecompressOnLoad);

        changed += MeasureLoop(so, "atmosLoop", "atmos");
        changed += MeasureLoop(so, "airToneLoop", "airTone");
        changed += MeasureCalls(so);
        so.ApplyModifiedPropertiesWithoutUndo();

        changed += PolishProfiles(bank);
        if (changed > 0)
        {
            EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
        }
        return changed;
    }

    static int Assign(SerializedObject so, string field, string file, bool mono, AudioClipLoadType loadType)
    {
        var p = so.FindProperty(field);
        if (p.objectReferenceValue != null) return 0; // your choice stays
        string path = $"{Folder}/{file}.wav";
        if (!File.Exists(path)) { Debug.Log($"AudioBank {field}: {file} not in {Folder}; left empty."); return 0; }
        Prepare(path, mono, loadType);
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        if (clip == null) return 0;
        p.objectReferenceValue = clip;
        Debug.Log($"AudioBank {field} = {file}.");
        return 1;
    }

    static void Prepare(string path, bool mono, AudioClipLoadType loadType)
    {
        if (AssetImporter.GetAtPath(path) is not AudioImporter imp) return;
        var s = imp.defaultSampleSettings;
        bool dirty = false;
        if (imp.forceToMono != mono) { imp.forceToMono = mono; dirty = true; }
        if (s.loadType != loadType) { s.loadType = loadType; dirty = true; }
        if (s.sampleRateSetting != AudioSampleRateSetting.OverrideSampleRate || s.sampleRateOverride != 44100)
        {
            s.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
            s.sampleRateOverride = 44100;
            dirty = true;
        }
        if (!dirty) return;
        imp.defaultSampleSettings = s;
        imp.SaveAndReimport();
        Debug.Log($"Audio import updated: {path} (mono {mono}, {loadType}, 44.1 kHz).");
    }

    // The seamless part of a bed (the ATMOS_2 recording fades in and out), measured from the file once while the field is unset.
    static int MeasureLoop(SerializedObject so, string field, string clipField)
    {
        var p = so.FindProperty(field);
        var clip = so.FindProperty(clipField).objectReferenceValue as AudioClip;
        if (clip == null || p.vector2Value != Vector2.zero) return 0;
        var db = Loudness(AssetDatabase.GetAssetPath(clip), 0.1f, 10);
        if (db == null) { Debug.LogWarning($"AudioBank {field}: could not read {clip.name} (only WAV files are measured); the whole clip loops."); return 0; }
        var (start, end) = ClipRegions.LoopRegion(db, 0.1f, 8f, 0.5f, 10f);
        if (end <= start) { Debug.LogWarning($"AudioBank {field}: no steady region found in {clip.name}; the whole clip loops."); return 0; }
        p.vector2Value = new Vector2(start, end);
        Debug.Log($"AudioBank {field}: loops {start:0.0}-{end:0.0} s of {clip.name}.");
        return 1;
    }

    // The separate calls inside the long roaming recording, measured once while none are set.
    static int MeasureCalls(SerializedObject so)
    {
        var p = so.FindProperty("roamCallSegments");
        var clip = so.FindProperty("roamCall").objectReferenceValue as AudioClip;
        if (clip == null || p.arraySize > 0) return 0;
        var db = Loudness(AssetDatabase.GetAssetPath(clip), 0.1f, 5);
        if (db == null) { Debug.LogWarning($"Roaming call: could not read {clip.name}; the first seconds are used."); return 0; }
        var segments = ClipRegions.CallSegments(db, 0.1f, 7f, 8f, 1.6f, 6f, 8);
        if (segments.Count == 0) { Debug.LogWarning($"Roaming call: no distinct calls found in {clip.name}; the first seconds are used."); return 0; }
        p.arraySize = segments.Count;
        for (int i = 0; i < segments.Count; i++) p.GetArrayElementAtIndex(i).vector2Value = new Vector2(segments[i].start, segments[i].length);
        Debug.Log($"Roaming call: {segments.Count} calls found in {clip.name} (starts {string.Join(", ", segments.ConvertAll(s => s.start.ToString("0.0")))} s).");
        return 1;
    }

    // A profile for every space; the new reflection and room-tone values from the built-in defaults where never configured.
    static int PolishProfiles(AudioBank bank)
    {
        int changed = 0;
        var list = new List<AudioBank.AcousticProfile>(bank.acousticProfiles ?? new AudioBank.AcousticProfile[0]);
        foreach (AcousticSpace space in System.Enum.GetValues(typeof(AcousticSpace)))
        {
            if (bank.HasOwnProfile(space)) continue;
            var p = AudioBank.AcousticProfile.BaseDefault(space);
            if (p == null) continue;
            p.ApplyPolishDefaults();
            list.Add(p);
            changed++;
            Debug.Log($"AudioBank: added a tunable acoustic profile for {space}.");
        }
        foreach (var p in list)
        {
            if (p == null || p.polishConfigured) continue;
            p.ApplyPolishDefaults();
            changed++;
            Debug.Log($"AudioBank {p.space}: early reflections and room-tone levels filled from the defaults (tune them there).");
        }
        if (changed > 0) bank.acousticProfiles = list.ToArray(); // saved by the caller
        return changed;
    }

    // ---------- Ship scene ----------

    // The big holds were machinery rooms acoustically; they are large open spaces now. Only the untouched old value is changed.
    static int MigrateZones()
    {
        int changed = 0;
        foreach (var z in Object.FindObjectsByType<ShipAcousticZone>(FindObjectsSortMode.None))
        {
            string room = z.name.StartsWith("Acoustic ") ? z.name.Substring("Acoustic ".Length) : null;
            if (room == null || ShipBuilder.RoomSpace(room) != AcousticSpace.LargeOpen) continue;
            if (z.space != AcousticSpace.LargeMachinery) continue; // already migrated, or deliberately set: kept
            Undo.RecordObject(z, "Acoustic polish");
            z.space = AcousticSpace.LargeOpen;
            EditorUtility.SetDirty(z);
            changed++;
            Debug.Log($"{z.name}: LargeMachinery -> LargeOpen.", z);
        }
        return changed;
    }

    // ---------- WAV measurement ----------

    /// <summary>
    /// Loudness (dB, channels mixed) every <paramref name="step"/> seconds of a PCM or float WAV, averaged over <paramref name="smooth"/>
    /// steps. Read straight from the file, so the clip's import settings do not matter. Null for anything that is not a readable WAV.
    /// </summary>
    static List<float> Loudness(string path, float step, int smooth)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (new string(r.ReadChars(4)) != "RIFF") return null;
            r.ReadInt32();
            if (new string(r.ReadChars(4)) != "WAVE") return null;
            int format = 0, channels = 0, rate = 0, bits = 0;
            while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
            {
                string id = new string(r.ReadChars(4));
                long size = r.ReadUInt32();
                long next = r.BaseStream.Position + size + (size & 1);
                if (id == "fmt ")
                {
                    format = r.ReadUInt16();
                    channels = r.ReadUInt16();
                    rate = r.ReadInt32();
                    r.ReadInt32();
                    r.ReadUInt16();
                    bits = r.ReadUInt16();
                    if (format == 0xFFFE && size >= 26) { r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt32(); format = r.ReadUInt16(); }
                }
                else if (id == "data")
                {
                    if (channels <= 0 || rate <= 0 || (format != 1 && format != 3)) return null;
                    return Measure(r, size, format, channels, rate, bits, step, smooth);
                }
                r.BaseStream.Position = next;
            }
        }
        catch (IOException e) { Debug.LogWarning($"Could not read {path}: {e.Message}"); }
        return null;
    }

    static List<float> Measure(BinaryReader r, long size, int format, int channels, int rate, int bits, float step, int smooth)
    {
        int bytes = bits / 8, frameBytes = bytes * channels;
        if (bytes < 2 || bytes > 4 || (format == 3 && bytes != 4)) return null;
        long frames = size / frameBytes;
        int hop = Mathf.Max(1, (int)(rate * step));
        var raw = new List<float>();
        var buffer = new byte[frameBytes * 4096];
        double sum = 0;
        int inHop = 0;
        for (long done = 0; done < frames;)
        {
            int want = (int)System.Math.Min(4096, frames - done);
            int got = r.Read(buffer, 0, want * frameBytes) / frameBytes;
            if (got <= 0) break;
            for (int f = 0; f < got; f++)
            {
                float mix = 0f;
                for (int c = 0; c < channels; c++) mix += Sample(buffer, (f * channels + c) * bytes, bytes, format);
                mix /= channels;
                sum += mix * mix;
                if (++inHop == hop) { raw.Add((float)(10.0 * System.Math.Log10(sum / hop + 1e-12))); sum = 0; inHop = 0; }
            }
            done += got;
        }
        var db = new List<float>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            int a = Mathf.Max(0, i - smooth / 2), b = Mathf.Min(raw.Count - 1, i + smooth / 2);
            float s = 0f;
            for (int k = a; k <= b; k++) s += raw[k];
            db.Add(s / (b - a + 1));
        }
        return db;
    }

    static float Sample(byte[] b, int i, int bytes, int format)
    {
        if (format == 3) return System.BitConverter.ToSingle(b, i);
        return bytes switch
        {
            2 => (short)(b[i] | b[i + 1] << 8) / 32768f,
            3 => ((b[i] << 8 | b[i + 1] << 16 | b[i + 2] << 24) >> 8) / 8388608f,
            _ => System.BitConverter.ToInt32(b, i) / 2147483648f,
        };
    }
}
