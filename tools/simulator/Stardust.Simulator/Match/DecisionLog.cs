using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Stardust.Simulator.Match;

/// <summary>
/// Reads the decision transitions a bot prints with <c>STARDUST_TRACE=1</c>
/// ("stardust t=12.345 car=0 decision=defend / block rank=...") from its seat log, and attributes
/// a series' goals to the decision the conceding car was running just before each one.
/// </summary>
public static partial class DecisionLog
{
    /// <summary>How long before a goal the conceding car's decision is read.</summary>
    public const float Lead = 0.5f;

    [GeneratedRegex(@"^stardust t=(?<t>[0-9.]+) car=\d+ decision=(?<d>.+?) rank=")]
    private static partial Regex Transition();

    public static List<(float Time, string Decision)> Read(string path)
    {
        var transitions = new List<(float, string)>();
        if (!File.Exists(path)) return transitions;
        foreach (string line in File.ReadLines(path))
        {
            System.Text.RegularExpressions.Match m = Transition().Match(line);
            if (m.Success && float.TryParse(m.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float t))
                transitions.Add((t, m.Groups["d"].Value.Trim()));
        }
        return transitions;
    }

    /// <summary>The decision in force at <paramref name="time"/>, or null before the first one.</summary>
    public static string? At(List<(float Time, string Decision)> transitions, float time)
    {
        string? current = null;
        foreach (var (t, decision) in transitions)
        {
            if (t > time) break;
            current = decision;
        }
        return current;
    }

    /// <summary>
    /// Report section: for each build, the goals it conceded by the decision it was running
    /// <see cref="Lead"/> seconds before (own goals marked), and its time share per decision.
    /// Empty when no seat log holds traced decisions.
    /// </summary>
    public static string Section(string outputDirectory, IReadOnlyList<MatchResult> results, string a, string b)
    {
        var conceded = new Dictionary<string, Dictionary<string, (int Goals, int Own)>> { [a] = new(), [b] = new() };
        var share = new Dictionary<string, Dictionary<string, double>> { [a] = new(), [b] = new() };
        bool any = false;
        for (int game = 0; game < results.Count; game++)
        {
            MatchResult r = results[game];
            if (r.Error != null) continue;
            string directory = Path.Combine(outputDirectory, $"game-{game:D3}");
            if (!Directory.Exists(directory)) continue;
            var logs = new Dictionary<int, List<List<(float, string)>>>();
            foreach (string path in Directory.GetFiles(directory, "bot-*.log"))
            {
                // bot-{seat}-{label}-{n}.log: seats are numbered blue first.
                string[] parts = Path.GetFileNameWithoutExtension(path).Split('-');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int seat)) continue;
                int team = seat < r.TeamSize ? 0 : 1;
                var transitions = Read(path);
                if (transitions.Count == 0) continue;
                any = true;
                if (!logs.TryGetValue(team, out var list)) logs[team] = list = new();
                list.Add(transitions);
                string label = team == 0 ? r.Blue : r.Orange;
                if (!share.TryGetValue(label, out var shares)) continue;
                for (int i = 0; i < transitions.Count; i++)
                {
                    float end = i + 1 < transitions.Count ? transitions[i + 1].Item1 : transitions[i].Item1;
                    shares[transitions[i].Item2] = shares.GetValueOrDefault(transitions[i].Item2) + Math.Max(0, end - transitions[i].Item1);
                }
            }
            foreach (GoalEvent goal in r.Goals)
            {
                int losing = 1 - goal.Team;
                string label = losing == 0 ? r.Blue : r.Orange;
                if (!conceded.TryGetValue(label, out var byDecision) || !logs.TryGetValue(losing, out var seats)) continue;
                foreach (var transitions in seats)
                {
                    string decision = At(transitions, (float)goal.Time - Lead) ?? "(none)";
                    var (goals, own) = byDecision.GetValueOrDefault(decision);
                    byDecision[decision] = (goals + 1, own + (goal.OwnGoal ? 1 : 0));
                }
            }
        }
        if (!any) return "";

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine(string.Create(inv, $"Goals conceded by the decision running {Lead:F1} s before (own goals in brackets):"));
        sb.AppendLine();
        var decisions = conceded[a].Keys.Union(conceded[b].Keys)
            .OrderByDescending(d => conceded[a].GetValueOrDefault(d).Goals + conceded[b].GetValueOrDefault(d).Goals).ToList();
        sb.AppendLine($"| Decision | {a} conceded | {b} conceded | {a} time % | {b} time % |");
        sb.AppendLine("|---|---|---|---|---|");
        double totalA = Math.Max(1e-9, share[a].Values.Sum()), totalB = Math.Max(1e-9, share[b].Values.Sum());
        string Cell((int Goals, int Own) c) => c.Own > 0 ? $"{c.Goals} ({c.Own})" : c.Goals.ToString(inv);
        foreach (string d in decisions)
            sb.AppendLine(string.Create(inv,
                $"| {d} | {Cell(conceded[a].GetValueOrDefault(d))} | {Cell(conceded[b].GetValueOrDefault(d))} | " +
                $"{100 * share[a].GetValueOrDefault(d) / totalA:F1} | {100 * share[b].GetValueOrDefault(d) / totalB:F1} |"));
        return sb.ToString();
    }
}
