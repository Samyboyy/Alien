using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: Alien > Apply Creature Pressure And Audio Tuning. Brings an existing Ship scene and AudioBank up to this milestone's defaults
/// without rebuilding anything:
///  - Creature (Ship scene): each listed field changes only while it still holds its PREVIOUS default (a value you tuned is kept and
///    logged). Fields that are new in this milestone already start at their defaults and are listed for reference.
///  - AudioBank: the versioned migration (AudioSetup.MigrateBank), with the same keep-your-value rule.
/// References, authored routes and every other value are left alone. Safe to rerun: a second run changes nothing.
/// </summary>
public static class PressureTuning
{
    const string ScenePath = "Assets/Scenes/Ship.unity", BankPath = "Assets/Resources/AudioBank.asset";

    // (field, previous default, new value)
    static readonly (string field, float previous, float value)[] CreatureChanges =
    {
        ("ventCooldown", 45f, 18f), // now the ABSOLUTE minimum between trips; the seeded vent desire decides the usual cadence
    };

    static readonly string[] NewCreatureFields =
    {
        "ventFirstMin", "ventFirstMax", "ventCalmMin", "ventCalmMax", "ventAlertMin", "ventAlertMax", "ventFrustratedFactor",
        "ventDesireBias", "ventDesireCheckSeconds", "ventDesireMinGroundMetres", "ventHearInterval", "ventPendingMaxAge",
    };

    [MenuItem("Alien/Apply Creature Pressure And Audio Tuning")]
    static void Run()
    {
        if (!File.Exists(ScenePath)) { Debug.LogError("Ship scene missing. Run Alien > Build Ship Scene first."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        int creatureChanges = 0;
        foreach (var ai in Object.FindObjectsByType<CreatureAI>(FindObjectsSortMode.None))
        {
            var so = new SerializedObject(ai);
            foreach (var (field, previous, value) in CreatureChanges)
            {
                var p = so.FindProperty(field);
                if (p == null) { Debug.LogWarning($"CreatureAI has no field '{field}'."); continue; }
                if (Mathf.Approximately(p.floatValue, value)) continue;
                if (!Mathf.Approximately(p.floatValue, previous)) { Debug.Log($"'{ai.name}'.{field}: kept your value {p.floatValue} (new default {value}).", ai); continue; }
                Debug.Log($"'{ai.name}'.{field}: {p.floatValue} -> {value}", ai);
                p.floatValue = value;
                creatureChanges++;
            }
            so.ApplyModifiedProperties();
            var listing = new System.Text.StringBuilder();
            foreach (var field in NewCreatureFields)
            {
                var p = so.FindProperty(field);
                if (p != null) listing.Append($"{field} {p.floatValue}, ");
            }
            Debug.Log($"'{ai.name}' vent cadence (new fields, at their defaults unless you changed them): {listing.ToString().TrimEnd(',', ' ')}", ai);
        }
        if (creatureChanges > 0)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        int bankChanges = 0;
        var bank = AssetDatabase.LoadAssetAtPath<AudioBank>(BankPath);
        if (bank == null) Debug.LogWarning("No AudioBank yet: run Alien > Setup Audio (it applies the same audio migration).");
        else
        {
            var so = new SerializedObject(bank);
            int before = so.FindProperty("tuningVersion").intValue;
            bankChanges = AudioSetup.MigrateBank(so);
            bool versionMoved = so.FindProperty("tuningVersion").intValue != before;
            so.ApplyModifiedProperties();
            if (bankChanges > 0 || versionMoved)
            {
                EditorUtility.SetDirty(bank);
                AssetDatabase.SaveAssets();
            }
        }
        Debug.Log($"Creature pressure and audio tuning: {creatureChanges} creature value(s) and {bankChanges} audio value(s) changed. " +
                  "Run Alien > Setup Audio as well to pick up dedicated vent clips and import settings.");
    }
}
