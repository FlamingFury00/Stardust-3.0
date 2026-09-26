using System.Globalization;
using System.Text;
using System.Text.Json;
using Stardust.Simulator.Match;
using Stardust.Simulator.Protocol;

namespace Stardust.Simulator;

public sealed class TournamentOptions
{
    public required IReadOnlyList<BotBuild> Bots { get; init; }
    public int GamesPerPair { get; init; } = 8;
    public int TeamSize { get; init; } = 1;
    public int Seed { get; init; } = 1;
    public int Parallel { get; init; } = 2;
    public float MatchSeconds { get; init; } = 300f;
    public string OutputDirectory { get; init; } = "sim-tournament";
}

/// <summary>
/// Round robin between bot builds: every pair plays a paired, side-swapped series. Results are ranked
/// by Bradley–Terry strength fitted to all decided games and reported on the Elo scale, with records,
/// goal difference and a pairwise matrix. Each pair's results are kept on disk, so a tournament can be
/// resumed or extended with new bots without replaying finished pairs.
/// </summary>
public static class Tournament
{
    /// <summary>Reads a roster: one <c>label = path</c> per line (build directory, entry assembly or bot config).</summary>
    public static List<BotBuild> ReadRoster(string path)
    {
        var bots = new List<BotBuild>();
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int equals = line.IndexOf('=');
            if (equals < 0) throw new InvalidDataException($"Roster line '{line}' is not 'label = path'.");
            string label = line[..equals].Trim();
            string location = line[(equals + 1)..].Trim();
            bots.Add(BotBuild.FromPath(label, Path.Combine(directory, location)));
        }
        if (bots.Select(b => b.Label).Distinct().Count() != bots.Count)
            throw new InvalidDataException("Roster labels must be unique.");
        return bots;
    }

    public static string Run(TournamentOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var all = new List<MatchResult>();
        int pair = 0;
        for (int i = 0; i < options.Bots.Count; i++)
        for (int j = i + 1; j < options.Bots.Count; j++, pair++)
        {
            BotBuild a = options.Bots[i], b = options.Bots[j];
            string directory = Path.Combine(options.OutputDirectory, $"{a.Label}-vs-{b.Label}");
            string saved = Path.Combine(directory, "results.json");
            List<MatchResult> results;
            if (File.Exists(saved))
            {
                results = JsonSerializer.Deserialize<List<MatchResult>>(File.ReadAllText(saved), Series.JsonOptions)!;
                Console.WriteLine($"{a.Label} vs {b.Label}: reusing {results.Count} finished games");
            }
            else
            {
                Console.WriteLine($"{a.Label} vs {b.Label}: {options.GamesPerPair} games");
                var series = new SeriesOptions
                {
                    A = a, B = b, TeamSize = options.TeamSize, Games = options.GamesPerPair,
                    Seed = options.Seed + 1000 * pair, Parallel = options.Parallel, MatchSeconds = options.MatchSeconds,
                    OutputDirectory = directory,
                };
                results = Series.Run(series);
                File.WriteAllText(Path.Combine(directory, "report.md"), Series.Report(series, results));
                if (results.All(r => r.Error == null))
                    File.WriteAllText(saved, JsonSerializer.Serialize(results, Series.JsonOptions));
            }
            all.AddRange(results);
        }

        string report = Report(options, all);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "tournament.md"), report);
        return report;
    }

    public static string Report(TournamentOptions options, IReadOnlyList<MatchResult> results)
    {
        var inv = CultureInfo.InvariantCulture;
        List<string> labels = options.Bots.Select(b => b.Label).ToList();
        int n = labels.Count;
        var index = labels.Select((l, i) => (l, i)).ToDictionary(x => x.l, x => x.i);
        // score[i, j]: points of i against j (win 1, draw 0.5); games[i, j]: games between them.
        var score = new double[n, n];
        var games = new int[n, n];
        var goalsFor = new int[n, n];
        var wins = new int[n];
        var losses = new int[n];
        var draws = new int[n];
        var played = new int[n];
        var goals = new int[n];
        var conceded = new int[n];
        int errors = 0;
        foreach (MatchResult r in results)
        {
            if (r.Error != null) { errors++; continue; }
            int blue = index[r.Blue], orange = index[r.Orange];
            games[blue, orange]++;
            games[orange, blue]++;
            goalsFor[blue, orange] += r.BlueScore;
            goalsFor[orange, blue] += r.OrangeScore;
            played[blue]++;
            played[orange]++;
            goals[blue] += r.BlueScore; conceded[blue] += r.OrangeScore;
            goals[orange] += r.OrangeScore; conceded[orange] += r.BlueScore;
            if (r.BlueScore == r.OrangeScore)
            {
                score[blue, orange] += 0.5; score[orange, blue] += 0.5;
                draws[blue]++; draws[orange]++;
            }
            else
            {
                (int winner, int loser) = r.BlueScore > r.OrangeScore ? (blue, orange) : (orange, blue);
                score[winner, loser] += 1;
                wins[winner]++; losses[loser]++;
            }
        }

        double[] elo = BradleyTerryElo(score, games);
        var order = Enumerable.Range(0, n).OrderByDescending(i => elo[i]).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(inv,
            $"# Tournament — {options.TeamSize}v{options.TeamSize}, {options.GamesPerPair} games per pair, {options.MatchSeconds:F0} s games"));
        sb.AppendLine();
        sb.AppendLine("| Rank | Bot | Elo | W-L-D | Win % | Goals / game | Conceded / game | Difference / game |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        int rank = 0;
        foreach (int i in order)
        {
            rank++;
            double decided = wins[i] + losses[i];
            double g = Math.Max(1, played[i]);
            sb.AppendLine(string.Create(inv,
                $"| {rank} | {labels[i]} | {elo[i]:F0} | {wins[i]}-{losses[i]}-{draws[i]} | {(decided > 0 ? wins[i] / decided : 0.5):P0} | " +
                $"{goals[i] / g:F2} | {conceded[i] / g:F2} | {(goals[i] - conceded[i]) / g:+0.00;-0.00} |"));
        }
        if (errors > 0) sb.AppendLine().AppendLine($"{errors} games failed and are excluded.");

        sb.AppendLine();
        sb.AppendLine("Goal difference per game of the row bot against the column bot:");
        sb.AppendLine();
        sb.AppendLine("| | " + string.Join(" | ", order.Select(i => labels[i])) + " |");
        sb.AppendLine("|---|" + string.Concat(order.Select(_ => "---|")));
        foreach (int i in order)
        {
            var cells = order.Select(j => i == j || games[i, j] == 0 ? "" :
                ((goalsFor[i, j] - goalsFor[j, i]) / (double)games[i, j]).ToString("+0.00;-0.00", inv));
            sb.AppendLine($"| **{labels[i]}** | " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bradley–Terry strengths fitted by the standard minorise–maximise iteration, with a weak prior
    /// (one drawn game against a unit-strength virtual opponent) so unbeaten or winless bots stay
    /// finite. Reported as Elo: 400·log10(strength), centred on 1500.
    /// </summary>
    public static double[] BradleyTerryElo(double[,] score, int[,] games)
    {
        int n = score.GetLength(0);
        var strength = Enumerable.Repeat(1.0, n).ToArray();
        for (int iteration = 0; iteration < 500; iteration++)
        {
            var next = new double[n];
            for (int i = 0; i < n; i++)
            {
                double won = 0.5, denominator = 1.0 / (strength[i] + 1.0);
                for (int j = 0; j < n; j++)
                {
                    if (i == j || games[i, j] == 0) continue;
                    won += score[i, j];
                    denominator += games[i, j] / (strength[i] + strength[j]);
                }
                next[i] = won / denominator;
            }
            double scale = Math.Exp(next.Select(s => Math.Log(s)).Average());
            for (int i = 0; i < n; i++) next[i] /= scale;
            double change = Enumerable.Range(0, n).Max(i => Math.Abs(Math.Log(next[i] / strength[i])));
            strength = next;
            if (change < 1e-9) break;
        }
        return strength.Select(s => 1500 + 400 * Math.Log10(s)).ToArray();
    }
}
