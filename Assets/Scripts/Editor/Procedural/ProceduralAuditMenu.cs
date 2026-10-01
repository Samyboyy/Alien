using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Menu: Alien > Procedural Ship > Run Procedural Batch Audit. Generates, places and solves 500 seeds with the current room profile and greybox
/// library (the built-in defaults when they are missing), logs a short summary and writes the full per-seed report to Logs/ProceduralAudit.txt.
/// Changes no asset and no scene.
/// </summary>
public static class ProceduralAuditMenu
{
    internal const string ReportPath = "Logs/ProceduralAudit.txt";

    [MenuItem("Alien/Procedural Ship/Run Procedural Batch Audit")]
    static void Run() => RunAudit(0, 500, true);

    internal static ShipAudit RunAudit(int firstSeed, int count, bool full, int scenarioSeed = 1)
    {
        var profile = AssetDatabase.LoadAssetAtPath<ShipGenerationProfile>(RoomDefinitionSetup.ProfilePath);
        var library = AssetDatabase.LoadAssetAtPath<GreyboxLibrary>(GreyboxLibraryBuilder.LibraryPath);
        var options = new ShipAudit.Options
        {
            firstSeed = firstSeed, count = count, place = full, scenario = full, scenarioSeed = scenarioSeed,
            catalogue = profile != null ? profile.BuildCatalogue() : null,
            graphSettings = profile != null ? profile.settings : null,
            templates = library != null ? library.BuildTemplates() : null,
            progress = (done, total) => !EditorUtility.DisplayCancelableProgressBar("Procedural audit", $"Seed {firstSeed + done} ({done}/{total})", (float)done / total),
        };
        ShipAudit audit;
        try { audit = ShipAudit.Run(options); }
        finally { EditorUtility.ClearProgressBar(); }
        Directory.CreateDirectory("Logs");
        File.WriteAllText(ReportPath, audit.Report());
        Debug.Log(audit.Summary() + $"Full report: {Path.GetFullPath(ReportPath)}");
        return audit;
    }
}
