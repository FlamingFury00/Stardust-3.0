using System.Globalization;
using System.Text;
using Stardust.Simulator.Match;
using Stardust.Simulator.Protocol;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Runs mechanics drills against the in-process Stardust bot and checks each against its pass
/// criteria. Drills run one after another: the bot's world state is process-wide.
/// </summary>
public static class MechanicsLab
{
    public static IReadOnlyList<Drill> All() => new Drill[]
    {
        new CarryDrill(), new FlickDrill(), new CatchDrill(), new PickupDrill(), new DribbleDuelDrill(),
        new AirDribbleDrill(), new FlipResetDrill(), new HoodToAirDrill(), new SaveDrill(),
    };

    /// <summary>Drills run only by name, not as part of "all".</summary>
    public static IReadOnlyList<Drill> Extra() => new Drill[] { new ActionProfileDrill(), new DefenseDrill(), new KickoffFollowDrill() };

    /// <summary>
    /// Overrides static tuning fields of the bot for an experiment, e.g. "HoodCarry.LaneGain=40".
    /// Only for parameter sweeps; shipped values live in the source.
    /// </summary>
    public static void Override(string assignments)
    {
        foreach (string line in RedUtils.Tuning.Apply(assignments, typeof(global::Bot.Stardust).Assembly, typeof(RedUtils.Car).Assembly))
            Console.WriteLine($"override {line}");
    }

    public static int Run(string names, int episodes, int seed, string outputDirectory, int traceEpisode = -1,
        BotBuild? opponent = null)
    {
        List<Drill> drills = names == "all"
            ? All().ToList()
            : All().Concat(Extra()).Where(d => names.Split(',').Contains(d.Name)).ToList();
        if (drills.Count == 0)
        {
            Console.WriteLine($"No drill matches '{names}'. Drills: {string.Join(", ", All().Select(d => d.Name))}.");
            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        var inv = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine("| Drill | Success | Criteria | Metrics |");
        report.AppendLine("|---|---|---|---|");
        int failedCriteria = 0;
        foreach (Drill drill in drills)
        {
            var agent = new StardustAgent();
            drill.Agent = agent;
            drill.TraceEpisode = traceEpisode;
            List<Seat> seats = ScenarioRunner.Seats(drill, new Seat(drill.TeamOf(0), "stardust", Agent: agent), opponent);
            string directory = Path.Combine(outputDirectory, drill.Name);
            using var session = new MatchSession(seats, new MatchOptions { Seed = seed, LogDirectory = directory });
            agent.Attach(session);
            ScenarioOutcome outcome = ScenarioRunner.Run(drill, session, "stardust", episodes, seed, directory);

            var verdicts = drill.Criteria.Select(c =>
            {
                double value = c.Measure(outcome);
                bool pass = c.Passes(value);
                if (!pass) failedCriteria++;
                return string.Create(inv, $"{(pass ? "✅" : "❌")} {c.Label} {value:0.###} ({(c.AtLeast ? "≥" : "≤")} {c.Threshold:0.###})");
            }).ToList();
            string metrics = string.Join(", ", outcome.Metrics.OrderBy(m => m.Key).Select(m =>
                string.Create(inv, $"{m.Key} mean {(m.Value.Count > 0 ? m.Value.Average() : double.NaN):0.###} p10/50/90 {Statistics.Quantile(m.Value, 0.1):0.##}/{Statistics.Quantile(m.Value, 0.5):0.##}/{Statistics.Quantile(m.Value, 0.9):0.##} (n={m.Value.Count})")));
            var (low, high) = Series.Wilson(outcome.Successes, outcome.Episodes);
            report.AppendLine(string.Create(inv,
                $"| {drill.Name} | {outcome.Successes}/{outcome.Episodes} = {outcome.Rate:P0} [{low:P0}–{high:P0}] | {string.Join("<br>", verdicts)} | {metrics} |"));
            if (outcome.Failures.Count > 0)
                File.WriteAllLines(Path.Combine(directory, "failures.txt"), outcome.Failures);
            Console.WriteLine(string.Create(inv, $"{drill.Name}: {outcome.Successes}/{outcome.Episodes}; {string.Join("; ", verdicts)}"));
        }

        File.WriteAllText(Path.Combine(outputDirectory, "mechanics.md"), report.ToString());
        Console.WriteLine();
        Console.WriteLine(report);
        return failedCriteria == 0 ? 0 : 1;
    }
}
