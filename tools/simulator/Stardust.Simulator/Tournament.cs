using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
    public bool RecordReplays { get; init; }
}

/// <summary>
/// Round robin between bot builds: every pair plays a paired, side-swapped series. Results are ranked
/// by Bradley–Terry strength fitted to all decided games and reported on the Elo scale, with records,
/// goal difference and a pairwise matrix. Each pair's results are kept on disk, so a tournament can be
/// resumed or extended with new bots without replaying finished pairs.
/// </summary>
public static class Tournament
{
    public sealed record Result(string Report, int FailedGames);

    // Cache identity includes the bot code, models and configuration, not just its roster label.
    public static string BuildFingerprint(BotBuild bot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
        Add(bot.Program);
        foreach (string argument in bot.Arguments) Add(argument);
        Add(bot.WorkingDirectory);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dll", ".exe", ".so", ".dylib", ".py", ".toml", ".json", ".pt", ".onnx", ".bin", ".pkl" };
        foreach (string file in Directory.EnumerateFiles(bot.WorkingDirectory, "*", SearchOption.AllDirectories)
                     .Where(f => extensions.Contains(Path.GetExtension(f)) || Path.GetExtension(f).Length == 0)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(bot.WorkingDirectory, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(p => p is "logs" or "log" or ".git" or "__pycache__")) continue;
            Add(relative);
            using var stream = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string ExperimentFingerprint()
    {
        string simulator = BuildFingerprint(new BotBuild("simulator", "dotnet",
            [typeof(Tournament).Assembly.Location], AppContext.BaseDirectory, "simulator"));
        var settings = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(e => e.Key is string key && key.StartsWith("STARDUST_", StringComparison.Ordinal))
            .OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)
            .Select(e => $"{e.Key}={e.Value}");
        string native = Environment.GetEnvironmentVariable("STARDUST_RSBRIDGE") ?? "";
        string nativeHash = "";
        if (File.Exists(native))
        {
            using var stream = File.OpenRead(native);
            nativeHash = Convert.ToHexString(SHA256.HashData(stream));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            simulator + "\n" + nativeHash + "\n" + string.Join("\n", settings))));
    }

    public sealed record CacheKey(string A, string B, int Games, int TeamSize, int Seed, float Seconds,
        bool Replays = false, string Experiment = "", string ALabel = "a", string BLabel = "b");
    private sealed record CachedSeries(CacheKey Key, List<MatchResult> Results);

    public static bool CanReuse(CacheKey expected, CacheKey? actual, IReadOnlyList<MatchResult> results) =>
        expected == actual && results.Count == expected.Games && results.Select((r, i) =>
            r.Error == null && r.Seed == expected.Seed + i / 2 && r.TeamSize == expected.TeamSize &&
            r.Blue == (i % 2 == 0 ? expected.ALabel : expected.BLabel) &&
            r.Orange == (i % 2 == 0 ? expected.BLabel : expected.ALabel)).All(valid => valid);
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

    public static Result Run(TournamentOptions options)
    {
        if (options.Bots.Count < 2 || options.GamesPerPair < 2 || options.GamesPerPair % 2 != 0 ||
            options.TeamSize is < 1 or > 3 || !float.IsFinite(options.MatchSeconds) || options.MatchSeconds <= 0)
            throw new ArgumentException("Tournament needs at least two bots, a positive even game count, size 1–3 and positive duration.");
        Directory.CreateDirectory(options.OutputDirectory);
        var all = new List<MatchResult>();
        var fingerprints = options.Bots.Select(BuildFingerprint).ToArray();
        string experiment = ExperimentFingerprint();
        int pair = 0;
        for (int i = 0; i < options.Bots.Count; i++)
        for (int j = i + 1; j < options.Bots.Count; j++, pair++)
        {
            BotBuild a = options.Bots[i], b = options.Bots[j];
            string directory = Path.Combine(options.OutputDirectory, $"{a.Label}-vs-{b.Label}");
            string saved = Path.Combine(directory, "results.json");
            string cache = Path.Combine(directory, "cache.json");
            var key = new CacheKey(fingerprints[i], fingerprints[j], options.GamesPerPair,
                options.TeamSize, options.Seed + 1000 * pair, options.MatchSeconds, options.RecordReplays,
                experiment, a.Label, b.Label);
            List<MatchResult>? results = null;
            if (File.Exists(cache))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<CachedSeries>(File.ReadAllText(cache), Series.JsonOptions);
                    if (cached?.Results != null && CanReuse(key, cached.Key, cached.Results)) results = cached.Results;
                }
                catch (JsonException) { } // Interrupted writes are rerun, never counted as results.
            }
            if (results != null)
            {
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
                    RecordReplays = options.RecordReplays,
                };
                results = Series.Run(series);
                File.WriteAllText(Path.Combine(directory, "report.md"), Series.Report(series, results));
                if (results.All(r => r.Error == null))
                {
                    File.WriteAllText(saved, JsonSerializer.Serialize(results, Series.JsonOptions));
                    File.WriteAllText(cache + ".tmp", JsonSerializer.Serialize(new CachedSeries(key, results), Series.JsonOptions));
                    File.Move(cache + ".tmp", cache, overwrite: true);
                }
            }
            all.AddRange(results);
        }

        string report = Report(options, all);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "tournament.md"), report);
        return new Result(report, all.Count(r => r.Error != null));
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
