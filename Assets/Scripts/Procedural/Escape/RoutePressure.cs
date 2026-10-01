using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// How much resistance an escape route offers, from mechanics the game already has: walking, gated doors, fetching the keycard or the fuse,
/// console and hold interactions, loud actions, detours to a control room or to an item, and how many progression steps it takes. Every weight
/// is data, so balance is tuned here, not in the generator.
/// </summary>
[Serializable]
public sealed class PressureSettings
{
    public float perRoom = 0.4f;          // each room on the route from the start
    public float perGate = 1f;            // each gated door on the route
    public float keycardRetrieval = 1.5f; // the route needs the keycard
    public float fuseRetrieval = 1.5f;    // the route needs the fuse
    public float console = 0.5f;          // each console the route requires (fuse socket, generator, door control, launch)
    public float hold = 1f;               // each hold-to-operate interaction (generator, manual override, manual release)
    public float loud = 1.5f;             // each loud action (generator, manual override, loud launch countdown)
    public float detourPerRoom = 0.25f;   // rooms walked off the route to fetch an item or reach a control room
    public float perStage = 1f;           // each step that must happen before the next (item, power, door)
    /// <summary>Every usable pod route must reach this; lower is a trivially direct escape.</summary>
    public float minimum = 7f;

    public PressureSettings Clone() => (PressureSettings)MemberwiseClone();
}

public static class RoutePressure
{
    /// <summary>Scores one escape plan and fills its breakdown and requirements. Pure: same scenario, same score.</summary>
    public static void Assess(ShipGraph g, Scenario sc, EscapePlan plan, PressureSettings ps)
    {
        plan.breakdown.Clear();
        plan.requirements.Clear();
        void Add(string what, float value) { if (value != 0f) plan.breakdown.Add((what, value)); }
        var fromSpawn = g.Distances(sc.spawnNode);

        Add($"travel: {plan.hops} rooms", plan.hops * ps.perRoom);

        bool keycard = false, power = plan.needsPower;
        int stages = 0, consoles = 0, holds = 0, louds = 0;
        float detour = 0f;
        foreach (var (edge, easiest) in plan.gates)
        {
            Add($"gate on {g.edges[edge].id} ({easiest})", ps.perGate);
            stages++;
            switch (easiest)
            {
                case OptionKind.Keycard: keycard = true; plan.requirements.Add($"keycard for {g.edges[edge].id}"); break;
                case OptionKind.Power: power = true; plan.requirements.Add($"power for {g.edges[edge].id}"); break;
                case OptionKind.Override: holds++; louds++; plan.requirements.Add($"manual override (loud hold) at {g.edges[edge].id}"); break;
                case OptionKind.Remote:
                {
                    var gate = sc.GateOn(edge);
                    int control = gate.options.First(o => o.kind == OptionKind.Remote).controlNode;
                    consoles++;
                    detour += Math.Max(0, fromSpawn[control] - 1);
                    plan.requirements.Add($"door control in {g.nodes[control].id} for {g.edges[edge].id}");
                    break;
                }
            }
        }
        if (keycard)
        {
            Add("fetch the keycard", ps.keycardRetrieval);
            stages++;
            var key = sc.items.FirstOrDefault(i => i.kind == ItemKind.Keycard && i.critical);
            if (key != null && key.candidates.Count > 0) detour += (float)key.candidates.Average(c => Math.Max(0, fromSpawn[c.node]));
        }
        if (power)
        {
            Add("fetch the fuse", ps.fuseRetrieval);
            stages += 2; // fuse fitted, then power restored
            consoles += 2;
            holds++;
            louds++; // the generator
            plan.requirements.Add("restore power: fuse to the socket, restart the generator (loud hold)");
            var fuse = sc.items.FirstOrDefault(i => i.kind == ItemKind.Fuse && i.critical);
            if (fuse != null && fuse.candidates.Count > 0) detour += (float)fuse.candidates.Average(c => Math.Max(0, fromSpawn[c.node]));
            var gen = sc.Console(ConsoleRole.Generator);
            if (gen != null) detour += Math.Max(0, fromSpawn[gen.node] - plan.hops);
        }
        consoles++; // the launch console
        if (plan.mode == LaunchMode.ManualQuiet && plan.status == PodStatus.Operational) { holds++; plan.requirements.Add("slow quiet manual release (hold)"); }
        else { louds++; plan.requirements.Add("loud launch countdown"); }

        Add($"{consoles} console interaction(s)", consoles * ps.console);
        Add($"{holds} hold interaction(s)", holds * ps.hold);
        Add($"{louds} loud action(s)", louds * ps.loud);
        Add($"detours: {detour:0.#} rooms", detour * ps.detourPerRoom);
        Add($"{stages} progression step(s)", stages * ps.perStage);
        plan.loudActions = louds;
        plan.stages = stages;
        plan.pressure = plan.breakdown.Sum(b => b.value);
    }
}
