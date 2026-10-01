using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>The room templates available to placement, by room definition. Built from the blueprints (tests) or from the prefab library (editor).</summary>
public sealed class TemplateLibrary
{
    readonly Dictionary<string, List<RoomTemplate>> byDefinition = new();

    public IEnumerable<RoomTemplate> All => byDefinition.Keys.OrderBy(k => k, StringComparer.Ordinal).SelectMany(k => byDefinition[k]);

    public void Add(RoomTemplate t)
    {
        if (!byDefinition.TryGetValue(t.definitionId, out var list)) byDefinition[t.definitionId] = list = new List<RoomTemplate>();
        list.Add(t);
        list.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
    }

    /// <summary>The variants for a definition, sorted by id (an empty list when there are none).</summary>
    public IReadOnlyList<RoomTemplate> For(string definitionId) =>
        byDefinition.TryGetValue(definitionId, out var l) ? l : (IReadOnlyList<RoomTemplate>)Array.Empty<RoomTemplate>();

    public static TemplateLibrary FromBlueprints()
    {
        var lib = new TemplateLibrary();
        foreach (var bp in GreyboxBlueprints.Create()) lib.Add(bp.ToTemplate());
        return lib;
    }

    /// <summary>
    /// Problems that would make placement fail or a room unusable: a definition with no variant, a variant with too few sockets for the most
    /// connections its definition allows (in some variant), a malformed template, a duplicate variant id, missing anchors or bounds.
    /// </summary>
    public List<string> Problems(IEnumerable<RoomSpec> catalogue, int maxDegree = 4)
    {
        var list = new List<string>();
        foreach (var dup in All.GroupBy(t => t.id).Where(g => g.Count() > 1)) list.Add($"variant id '{dup.Key}' is used by {dup.Count()} prefabs");
        foreach (var spec in catalogue.OrderBy(s => s.id, StringComparer.Ordinal))
        {
            var variants = For(spec.id);
            if (variants.Count == 0) { list.Add($"no prefab variant for room definition '{spec.id}'"); continue; }
            int need = Math.Min(spec.maxConnections, maxDegree);
            if (variants.Max(v => v.sockets.Count) < need) list.Add($"'{spec.id}': no variant has {need} sockets (its definition allows {spec.maxConnections} connections)");
            foreach (var v in variants)
            {
                list.AddRange(v.Problems());
                list.AddRange(AnchorRules.Missing(spec, v));
            }
        }
        foreach (var t in All)
            if (!catalogue.Any(s => s.id == t.definitionId)) list.Add($"variant '{t.id}' belongs to unknown definition '{t.definitionId}'");
        return list;
    }
}
