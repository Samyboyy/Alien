using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Alien > Procedural Ship > Create Or Repair Room Definitions. Writes the default room catalogue (DefaultRoomCatalogue) into assets:
///  - one RoomDefinition per default id under Assets/Procedural/Rooms, only when no RoomDefinition with that id exists anywhere in the project
///    (an existing asset is never overwritten, whatever its values);
///  - the profile Assets/Procedural/ShipGenerationProfile.asset if missing; otherwise any definition it lacks is appended and empty slots are
///    removed (its order and settings are kept).
/// Duplicate ids and definitions with no id are reported, not changed. Nothing is saved when nothing changed. Touches no scene.
/// </summary>
public static class RoomDefinitionSetup
{
    internal const string Folder = "Assets/Procedural", RoomsFolder = "Assets/Procedural/Rooms", ProfilePath = "Assets/Procedural/ShipGenerationProfile.asset";

    [MenuItem("Alien/Procedural Ship/Create Or Repair Room Definitions")]
    static void Run()
    {
        int created = CreateOrRepair(out var notes);
        foreach (var n in notes) Debug.Log(n);
        Debug.Log(created == 0 ? "Room definitions: everything was already in place; nothing changed." : $"Room definitions: {created} change(s) made and saved.");
        var stale = RuleDifferences();
        if (stale.Count > 0)
            Debug.LogWarning($"Room definitions: {stale.Count} existing definition(s) differ from the current default relationship rules (they were not changed): " +
                             string.Join("; ", stale.Take(6).Select(x => x.summary)) + (stale.Count > 6 ? "; ..." : "") +
                             ". Review and apply them with Alien > Procedural Ship > Apply Default Rule Updates To Room Definitions.");
    }

    /// <summary>
    /// Menu: Alien > Procedural Ship > Apply Default Rule Updates To Room Definitions. For definitions whose id is in the default catalogue, lists
    /// every difference in the relationship and capability rules only (preferred, forbidden, only and required neighbours, "close to", placement
    /// preference, vertical connection) and, after a confirmation, copies the defaults over those fields. Counts, sectors, weights, roles and
    /// every other field stay as they are. Nothing happens without the confirmation.
    /// </summary>
    [MenuItem("Alien/Procedural Ship/Apply Default Rule Updates To Room Definitions")]
    static void ApplyRuleUpdates()
    {
        var diffs = RuleDifferences();
        if (diffs.Count == 0) { Debug.Log("Room definitions: every definition already has the default relationship rules."); return; }
        string list = string.Join("\n", diffs.Take(20).Select(d => "- " + d.summary)) + (diffs.Count > 20 ? $"\n... and {diffs.Count - 20} more" : "");
        if (!EditorUtility.DisplayDialog("Apply Default Rule Updates", $"{diffs.Count} definition(s) differ from the default relationship rules:\n\n{list}\n\nOverwrite only these rule fields with the defaults? Other values are kept.", "Apply", "Cancel"))
            return;
        foreach (var d in diffs)
        {
            Undo.RecordObject(d.asset, "Apply default room rules");
            d.asset.CopyRulesFrom(d.spec);
            EditorUtility.SetDirty(d.asset);
            Debug.Log($"Room definition '{d.asset.id}': rules updated ({d.summary}).");
        }
        AssetDatabase.SaveAssets();
    }

    internal static List<(RoomDefinition asset, RoomSpec spec, string summary)> RuleDifferences()
    {
        var defaults = DefaultRoomCatalogue.Create().ToDictionary(x => x.id);
        var list = new List<(RoomDefinition, RoomSpec, string)>();
        foreach (var a in AllDefinitions())
        {
            if (string.IsNullOrEmpty(a.id) || !defaults.TryGetValue(a.id, out var d)) continue;
            var have = a.ToSpec();
            var fields = new List<string>();
            bool Same(RoomCategory[] x, RoomCategory[] y) => x.OrderBy(c => c).SequenceEqual(y.OrderBy(c => c));
            if (!Same(have.preferredNeighbours, d.preferredNeighbours)) fields.Add("preferred neighbours");
            if (!Same(have.forbiddenNeighbours, d.forbiddenNeighbours)) fields.Add("forbidden neighbours");
            if (!Same(have.closeTo, d.closeTo) || have.closeToDistance != d.closeToDistance || have.closeToStrict != d.closeToStrict) fields.Add("close-to rule");
            if (!Same(have.onlyNeighbours, d.onlyNeighbours)) fields.Add("only neighbours");
            if (!Same(have.requiredNeighbours, d.requiredNeighbours)) fields.Add("required neighbours");
            if (have.placement != d.placement) fields.Add("placement preference");
            if (have.requiresVerticalConnection != d.requiresVerticalConnection) fields.Add("vertical connection");
            if (fields.Count > 0) list.Add((a, d, $"{a.id}: {string.Join(", ", fields)}"));
        }
        return list;
    }

    internal static int CreateOrRepair(out List<string> notes)
    {
        notes = new List<string>();
        int changes = 0;
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "Procedural");
        if (!AssetDatabase.IsValidFolder(RoomsFolder)) AssetDatabase.CreateFolder(Folder, "Rooms");

        var existing = AllDefinitions();
        foreach (var dup in existing.Where(d => !string.IsNullOrEmpty(d.id)).GroupBy(d => d.id).Where(g => g.Count() > 1))
            notes.Add($"Room definitions: the id '{dup.Key}' is used by {dup.Count()} assets ({string.Join(", ", dup.Select(AssetDatabase.GetAssetPath))}); fix it by hand.");
        foreach (var d in existing.Where(d => string.IsNullOrEmpty(d.id)))
            notes.Add($"Room definition '{AssetDatabase.GetAssetPath(d)}' has no id; give it one (or delete it).");

        var byId = new Dictionary<string, RoomDefinition>();
        foreach (var d in existing) if (!string.IsNullOrEmpty(d.id) && !byId.ContainsKey(d.id)) byId[d.id] = d;
        foreach (var spec in DefaultRoomCatalogue.Create())
        {
            if (byId.ContainsKey(spec.id)) continue;
            var asset = ScriptableObject.CreateInstance<RoomDefinition>();
            asset.CopyFrom(spec);
            asset.name = spec.id;
            string path = AssetDatabase.GenerateUniqueAssetPath($"{RoomsFolder}/{spec.id}.asset");
            AssetDatabase.CreateAsset(asset, path);
            byId[spec.id] = asset;
            changes++;
            notes.Add($"Room definition created: {path}");
        }

        var profile = AssetDatabase.LoadAssetAtPath<ShipGenerationProfile>(ProfilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<ShipGenerationProfile>();
            profile.rooms = byId.Values.OrderBy(d => d.id, System.StringComparer.Ordinal).ToList();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            changes++;
            notes.Add($"Generation profile created: {ProfilePath} ({profile.rooms.Count} definitions)");
        }
        else
        {
            int removed = profile.rooms.RemoveAll(d => d == null);
            var missing = DefaultRoomCatalogue.Create().Select(s => byId[s.id]).Where(d => !profile.rooms.Contains(d)).ToList();
            profile.rooms.AddRange(missing);
            if (removed + missing.Count > 0)
            {
                EditorUtility.SetDirty(profile);
                changes++;
                notes.Add($"Generation profile repaired: {missing.Count} definition(s) added, {removed} empty slot(s) removed.");
            }
        }
        if (changes > 0) AssetDatabase.SaveAssets();
        return changes;
    }

    internal static List<RoomDefinition> AllDefinitions() =>
        AssetDatabase.FindAssets("t:RoomDefinition").Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<RoomDefinition>)
            .Where(d => d != null).OrderBy(d => AssetDatabase.GetAssetPath(d), System.StringComparer.Ordinal).ToList();
}
