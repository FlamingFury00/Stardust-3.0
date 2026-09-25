using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Stardust.Simulator.Match;
using Stardust.Simulator.Protocol;

namespace Stardust.Simulator;

public sealed class SeriesOptions
{
    public required BotBuild A { get; init; }
    public required BotBuild B { get; init; }
    public int TeamSize { get; init; } = 1;
    public int Games { get; init; } = 10;
    public int Seed { get; init; } = 1;
    public int Parallel { get; init; } = 2;
    public float MatchSeconds { get; init; } = 300f;
    public string OutputDirectory { get; init; } = "sim-results";
    public bool RecordReplays { get; init; }
}

/// <summary>
/// Paired, side-swapped series: every seed is played twice with the builds exchanging colours,
/// so spawn luck and colour asymmetries cancel out.
/// </summary>
public static class Series
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Results are read back (tournament resume): fill their get-only lists.
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,
    };

    public static List<MatchResult> Run(SeriesOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var results = new ConcurrentBag<(int Game, MatchResult Result)>();
        int completed = 0;
        var inv = CultureInfo.InvariantCulture;

        System.Threading.Tasks.Parallel.For(0, options.Games,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallel) }, game =>
        {
            int seed = options.Seed + game / 2;
            bool aIsBlue = game % 2 == 0;
            BotBuild blue = aIsBlue ? options.A : options.B;
            BotBuild orange = aIsBlue ? options.B : options.A;
            var seats = new List<Seat>();
            for (int i = 0; i < options.TeamSize; i++)
                seats.Add(new Seat(0, $"{blue.Label}-{i + 1}", blue));
            for (int i = 0; i < options.TeamSize; i++)
                seats.Add(new Seat(1, $"{orange.Label}-{i + 1}", orange));

            string gameDirectory = Path.Combine(options.OutputDirectory, $"game-{game:D3}");
            var replay = options.RecordReplays ? new ReplayRecorder() : null;
            var matchOptions = new MatchOptions
            {
                Seed = seed, MatchSeconds = options.MatchSeconds, LogDirectory = gameDirectory, Replay = replay,
            };

            MatchResult result;
            try
            {
                using var session = new MatchSession(seats, matchOptions);
                result = session.PlayMatch(blue.Label, orange.Label, options.TeamSize);
                replay?.Save(Path.Combine(gameDirectory, "replay.json"), session.Participants,
                    $"{blue.Label} vs {orange.Label} seed {seed}");
            }
            catch (Exception e)
            {
                result = new MatchResult { Blue = blue.Label, Orange = orange.Label, Seed = seed, TeamSize = options.TeamSize, Error = e.ToString() };
            }

            File.WriteAllText(Path.Combine(gameDirectory, "result.json"),
                JsonSerializer.Serialize(result, JsonOptions));
            results.Add((game, result));
            int done = Interlocked.Increment(ref completed);
            Console.WriteLine(string.Create(inv,
                $"[{done}/{options.Games}] game {game}: {result.Blue} {result.BlueScore} - {result.OrangeScore} {result.Orange}" +
                $"{(result.Overtime ? " (OT)" : "")}{(result.Error != null ? "  ERROR: " + result.Error.Split('\n')[0] : "")}" +
                $"  [{result.WallSeconds:F0}s wall, {result.GameSeconds / Math.Max(1, result.WallSeconds):F1}x; {result.Timing}]"));
        });

        return results.OrderBy(r => r.Game).Select(r => r.Result).ToList();
    }

    public static string Report(SeriesOptions options, IReadOnlyList<MatchResult> results)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        string a = options.A.Label, b = options.B.Label;
        var valid = results.Where(r => r.Error == null).ToList();
        int wins = 0, losses = 0, draws = 0, goalsFor = 0, goalsAgainst = 0;
        foreach (MatchResult r in valid)
        {
            int aTeam = r.Blue == a ? 0 : 1;
            int aScore = aTeam == 0 ? r.BlueScore : r.OrangeScore;
            int bScore = aTeam == 0 ? r.OrangeScore : r.BlueScore;
            goalsFor += aScore;
            goalsAgainst += bScore;
            if (r.Draw || aScore == bScore) draws++;
            else if (aScore > bScore) wins++;
            else losses++;
        }

        int decided = wins + losses;
        double rate = decided > 0 ? (double)wins / decided : 0.5;
        (double low, double high) = Wilson(wins, decided);
        sb.AppendLine(string.Create(inv, $"# {a} vs {b} — {options.TeamSize}v{options.TeamSize}, {valid.Count} games"));
        sb.AppendLine();
        sb.AppendLine(string.Create(inv, $"- Record for {a}: **{wins}W {losses}L {draws}D**, win rate {rate:P1} (95% CI {low:P0}–{high:P0})"));
        sb.AppendLine(string.Create(inv, $"- Goals: {goalsFor} for, {goalsAgainst} against ({(goalsFor - goalsAgainst) / Math.Max(1.0, valid.Count):+0.00;-0.00} per game)"));
        int errors = results.Count - valid.Count;
        if (errors > 0) sb.AppendLine($"- Errors: {errors} games failed");
        sb.AppendLine();

        sb.AppendLine("| Stat (per player) | " + a + " | " + b + " |");
        sb.AppendLine("|---|---|---|");
        var statsA = Aggregate(valid, a);
        var statsB = Aggregate(valid, b);
        foreach (var (name, fa, fb) in StatRows(statsA, statsB))
            sb.AppendLine($"| {name} | {fa} | {fb} |");

        var kickoffs = valid.SelectMany(r => r.Kickoffs.Select(k => (r, k))).ToList();
        int aFirst = kickoffs.Count(x => x.k.FirstTouchTeam == (x.r.Blue == a ? 0 : 1));
        int aAdvantage = kickoffs.Count(x => x.k.Advantage == (x.r.Blue == a ? 0 : 1));
        int bAdvantage = kickoffs.Count(x => x.k.Advantage == (x.r.Blue == a ? 1 : 0));
        int aKickoffGoals = kickoffs.Count(x => x.k.GoalWithin10s && x.k.GoalTeam == (x.r.Blue == a ? 0 : 1));
        int bKickoffGoals = kickoffs.Count(x => x.k.GoalWithin10s && x.k.GoalTeam == (x.r.Blue == a ? 1 : 0));
        sb.AppendLine();
        sb.AppendLine(string.Create(inv,
            $"Kickoffs: {kickoffs.Count}; {a} first touch {aFirst} ({aFirst / Math.Max(1.0, kickoffs.Count):P0}); " +
            $"ball-side advantage {a} {aAdvantage} / {b} {bAdvantage}; goals within 10 s {a} {aKickoffGoals} / {b} {bKickoffGoals}"));
        return sb.ToString();
    }

    public static PlayerStats Aggregate(IEnumerable<MatchResult> results, string label)
    {
        var total = new PlayerStats();
        foreach (MatchResult r in results)
            foreach (PlayerSummary p in r.Players)
                if ((p.Team == 0 ? r.Blue : r.Orange) == label)
                    total.Add(p.Stats);
        return total;
    }

    public static IEnumerable<(string Name, string A, string B)> StatRows(PlayerStats a, PlayerStats b)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(double v, string f = "F1") => v.ToString(f, inv);
        double PerGame(PlayerStats s, double v) => s.Minutes > 0 ? v / (s.Minutes / 5.0) : 0;
        yield return ("Goals / 5 min", F(PerGame(a, a.Goals), "F2"), F(PerGame(b, b.Goals), "F2"));
        yield return ("Shots / 5 min", F(PerGame(a, a.Shots), "F2"), F(PerGame(b, b.Shots), "F2"));
        yield return ("Saves / 5 min", F(PerGame(a, a.Saves), "F2"), F(PerGame(b, b.Saves), "F2"));
        yield return ("Assists / 5 min", F(PerGame(a, a.Assists), "F2"), F(PerGame(b, b.Assists), "F2"));
        yield return ("Own goals / 5 min", F(PerGame(a, a.OwnGoals), "F2"), F(PerGame(b, b.OwnGoals), "F2"));
        yield return ("Touches / 5 min", F(PerGame(a, a.Touches)), F(PerGame(b, b.Touches)));
        yield return ("Aerial touches / 5 min", F(PerGame(a, a.AerialTouches)), F(PerGame(b, b.AerialTouches)));
        yield return ("Demos / 5 min", F(PerGame(a, a.DemosInflicted), "F2"), F(PerGame(b, b.DemosInflicted), "F2"));
        yield return ("Avg speed (uu/s)", F(a.AverageSpeed, "F0"), F(b.AverageSpeed, "F0"));
        yield return ("Supersonic %", F(a.Percent(a.SupersonicSeconds)), F(b.Percent(b.SupersonicSeconds)));
        yield return ("Boost-speed %", F(a.Percent(a.BoostSpeedSeconds)), F(b.Percent(b.BoostSpeedSeconds)));
        yield return ("Slow %", F(a.Percent(a.SlowSeconds)), F(b.Percent(b.SlowSeconds)));
        yield return ("Ground %", F(a.Percent(a.GroundSeconds)), F(b.Percent(b.GroundSeconds)));
        yield return ("Wall %", F(a.Percent(a.WallSeconds)), F(b.Percent(b.WallSeconds)));
        yield return ("Low air %", F(a.Percent(a.LowAirSeconds)), F(b.Percent(b.LowAirSeconds)));
        yield return ("High air %", F(a.Percent(a.HighAirSeconds)), F(b.Percent(b.HighAirSeconds)));
        yield return ("Boost used / min (BPM)", F(a.BoostPerMinute, "F0"), F(b.BoostPerMinute, "F0"));
        yield return ("Avg boost", F(a.AverageBoost, "F0"), F(b.AverageBoost, "F0"));
        yield return ("Zero boost %", F(a.Percent(a.ZeroBoostSeconds)), F(b.Percent(b.ZeroBoostSeconds)));
        yield return ("Full boost %", F(a.Percent(a.FullBoostSeconds)), F(b.Percent(b.FullBoostSeconds)));
        yield return ("Big pads / 5 min", F(PerGame(a, a.BigPads)), F(PerGame(b, b.BigPads)));
        yield return ("Small pads / 5 min", F(PerGame(a, a.SmallPads)), F(PerGame(b, b.SmallPads)));
        yield return ("Stolen boost / 5 min", F(PerGame(a, a.StolenBoost), "F0"), F(PerGame(b, b.StolenBoost), "F0"));
        yield return ("Defensive third %", F(a.Percent(a.DefensiveThirdSeconds)), F(b.Percent(b.DefensiveThirdSeconds)));
        yield return ("Neutral third %", F(a.Percent(a.NeutralThirdSeconds)), F(b.Percent(b.NeutralThirdSeconds)));
        yield return ("Offensive third %", F(a.Percent(a.OffensiveThirdSeconds)), F(b.Percent(b.OffensiveThirdSeconds)));
        yield return ("Behind ball %", F(a.Percent(a.BehindBallSeconds)), F(b.Percent(b.BehindBallSeconds)));
        yield return ("Last back %", F(a.Percent(a.LastBackSeconds)), F(b.Percent(b.LastBackSeconds)));
        yield return ("Closest to ball %", F(a.Percent(a.ClosestToBallSeconds)), F(b.Percent(b.ClosestToBallSeconds)));
        double KickoffTime(PlayerStats s) => s.KickoffFirstTouches > 0 ? s.KickoffTimeToBall / s.KickoffFirstTouches : double.NaN;
        yield return ("Kickoff time to first touch (s)", F(KickoffTime(a), "F2"), F(KickoffTime(b), "F2"));
    }

    /// <summary>Wilson score interval for a binomial proportion.</summary>
    public static (double Low, double High) Wilson(int successes, int trials, double z = 1.96)
    {
        if (trials == 0) return (0, 1);
        double p = (double)successes / trials;
        double denominator = 1 + z * z / trials;
        double centre = (p + z * z / (2 * trials)) / denominator;
        double margin = z * Math.Sqrt(p * (1 - p) / trials + z * z / (4.0 * trials * trials)) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }
}
