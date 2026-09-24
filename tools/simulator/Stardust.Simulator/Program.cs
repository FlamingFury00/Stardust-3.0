using System.Globalization;
using Stardust.Simulator;
using Stardust.Simulator.Protocol;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var arguments = CommandLine.Parse(args);
string command = arguments.Command;

switch (command)
{
    case "match":
    {
        var options = new SeriesOptions
        {
            A = BotBuild.FromPath(arguments.Get("a-label", "candidate"), arguments.Require("a")),
            B = BotBuild.FromPath(arguments.Get("b-label", "baseline"), arguments.Require("b")),
            TeamSize = arguments.GetInt("size", 1),
            Games = arguments.GetInt("games", 10),
            Seed = arguments.GetInt("seed", 1),
            Parallel = arguments.GetInt("parallel", Math.Max(1, Environment.ProcessorCount / 2)),
            MatchSeconds = arguments.GetFloat("seconds", 300f),
            OutputDirectory = arguments.Get("out", "sim-results"),
            RecordReplays = arguments.Has("replays"),
        };
        var results = Series.Run(options);
        string report = Series.Report(options, results);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "report.md"), report);
        Console.WriteLine();
        Console.WriteLine(report);
        return results.All(r => r.Error == null) ? 0 : 2;
    }
    default:
        Console.WriteLine("""
            Stardust match simulator (RocketSim + RLBot v5 protocol)

              match --a <bot build dir> --b <bot build dir> [--a-label candidate] [--b-label baseline]
                    [--size 1|2|3] [--games 10] [--seed 1] [--parallel N] [--seconds 300]
                    [--out sim-results] [--replays]
            """);
        return command == "help" ? 0 : 1;
}

internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> values = new();
    public string Command { get; private init; } = "help";

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine { Command = args.Length > 0 ? args[0] : "help" };
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            string key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
            result.values[key] = value;
        }
        return result;
    }

    public bool Has(string key) => values.ContainsKey(key);
    public string Get(string key, string fallback) => values.TryGetValue(key, out var v) && v != null ? v : fallback;
    public string Require(string key) => values.TryGetValue(key, out var v) && v != null
        ? v : throw new ArgumentException($"--{key} is required.");
    public int GetInt(string key, int fallback) => values.TryGetValue(key, out var v) && v != null
        ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public float GetFloat(string key, float fallback) => values.TryGetValue(key, out var v) && v != null
        ? float.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
