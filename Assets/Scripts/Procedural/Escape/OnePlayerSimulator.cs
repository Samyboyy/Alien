using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A literal one-player simulation of a scenario, used to prove what the solver claims. The player holds one item at a time, walks between
/// rooms through doors that are open, picks items up and puts them down, opens doors by the options they offer, fits the fuse, restarts the
/// generator and launches a pod. A breadth-first search over (room, held item, open doors, flags, item locations) looks for any way to escape.
/// Call it with <see cref="Scenario.ForRound"/> to check one concrete round (each item in its one chosen place).
/// </summary>
public static class OnePlayerSimulator
{
    public sealed class Outcome
    {
        public bool escaped;
        public int steps, statesVisited;
        public bool searchLimitHit;
        public List<string> plan = new();
    }

    struct State : IEquatable<State>
    {
        public int room, held, flags;
        public ulong doors;
        public string items; // location per critical/optional item, one char each: room index in base 36, '^' held, 'x' used up
        public bool Equals(State o) => room == o.room && held == o.held && flags == o.flags && doors == o.doors && items == o.items;
        public override bool Equals(object o) => o is State s && Equals(s);
        public override int GetHashCode() => HashCode.Combine(room, held, flags, doors, items);
    }

    const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    public static Outcome CanEscape(ShipGraph g, Scenario sc, int maxStates = 400000, bool wantPlan = false)
    {
        var o = new Outcome();
        var items = sc.items.Where(i => i.kind != ItemKind.None && i.candidates.Count > 0).ToList();
        var gates = sc.gates;
        var start = new State { room = sc.spawnNode, held = -1, flags = 0, doors = 0, items = new string(items.Select(i => Digits[i.candidates[0].node]).ToArray()) };
        var seen = new Dictionary<State, (State prev, string action)> { [start] = (default, null) };
        var queue = new Queue<State>();
        queue.Enqueue(start);
        const int Fuse = 1, Power = 2;
        int sockRoom = sc.Console(ConsoleRole.FuseSocket)?.node ?? -1, genRoom = sc.Console(ConsoleRole.Generator)?.node ?? -1;

        void Push(State from, State to, string action)
        {
            if (seen.ContainsKey(to)) return;
            seen[to] = (from, action);
            queue.Enqueue(to);
        }

        while (queue.Count > 0)
        {
            if (seen.Count > maxStates) { o.searchLimitHit = true; break; }
            var s = queue.Dequeue();
            o.steps++;

            // Launch a pod.
            foreach (var pod in sc.pods)
            {
                if (!pod.Usable || pod.node != s.room) continue;
                if (pod.NeedsPower && (s.flags & Power) == 0) continue;
                o.escaped = true;
                o.statesVisited = seen.Count;
                if (wantPlan) o.plan = Trace(seen, s, g, "launch pod in " + g.nodes[pod.node].id);
                return o;
            }

            // Walk through an open (or ungated) connection.
            for (int e = 0; e < g.EdgeCount; e++)
            {
                var edge = g.edges[e];
                if (edge.a != s.room && edge.b != s.room) continue;
                int gi = gates.FindIndex(x => x.edge == e);
                if (gi >= 0 && (s.doors & (1UL << gi)) == 0) continue;
                var n = s; n.room = edge.Other(s.room);
                Push(s, n, "walk to " + g.nodes[n.room].id);
            }

            // Operate a door from either side of it, or from its control room.
            for (int gi = 0; gi < gates.Count; gi++)
            {
                if ((s.doors & (1UL << gi)) != 0) continue;
                var gate = gates[gi];
                var edge = g.edges[gate.edge];
                bool atDoor = s.room == edge.a || s.room == edge.b;
                foreach (var opt in gate.options)
                {
                    bool ok = opt.kind switch
                    {
                        OptionKind.Override => atDoor,
                        OptionKind.Keycard => atDoor && s.held >= 0 && items[s.held].kind == ItemKind.Keycard,
                        OptionKind.Power => atDoor && (s.flags & Power) != 0,
                        _ => s.room == opt.controlNode,
                    };
                    if (!ok) continue;
                    var n = s; n.doors |= 1UL << gi;
                    Push(s, n, $"open the door on {g.edges[gate.edge].id} ({opt.kind})");
                    break;
                }
            }

            // Pick up and put down.
            if (s.held >= 0)
            {
                var n = s; n.held = -1;
                n.items = Set(s.items, s.held, Digits[s.room]);
                Push(s, n, "put down " + items[s.held].id);
            }
            else
                for (int i = 0; i < items.Count; i++)
                    if (s.items[i] == Digits[s.room] && !items[i].optional)
                    {
                        var n = s; n.held = i;
                        n.items = Set(s.items, i, '^');
                        Push(s, n, "pick up " + items[i].id);
                    }

            // Fuse and generator.
            if (s.room == sockRoom && s.held >= 0 && items[s.held].kind == ItemKind.Fuse && (s.flags & Fuse) == 0)
            {
                var n = s; n.flags |= Fuse; n.held = -1;
                n.items = Set(s.items, s.held, 'x');
                Push(s, n, "fit the fuse");
            }
            if (s.room == genRoom && (s.flags & Fuse) != 0 && (s.flags & Power) == 0)
            {
                var n = s; n.flags |= Power;
                Push(s, n, "restart the generator");
            }
        }
        o.statesVisited = seen.Count;
        return o;
    }

    static string Set(string s, int i, char c) { var a = s.ToCharArray(); a[i] = c; return new string(a); }

    static List<string> Trace(Dictionary<State, (State prev, string action)> seen, State at, ShipGraph g, string last)
    {
        var list = new List<string> { last };
        while (seen.TryGetValue(at, out var p) && p.action != null) { list.Add(p.action); at = p.prev; }
        list.Reverse();
        return list;
    }
}
