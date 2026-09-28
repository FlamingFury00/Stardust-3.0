using Stardust.Simulator;
using Stardust.Simulator.Match;
using Stardust.Simulator.Protocol;
using RLBot.Flat;

int passed = 0, failed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.Message}"); }
}

string directory = Path.Combine(Path.GetTempPath(), $"stardust-sim-check-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    string config = Path.Combine(directory, "bot.toml");
    Test("launcher parses TOML literal strings and selects the host platform", () =>
    {
        File.WriteAllText(config, """
            [settings]
            root_dir = '.'
            run_command = 'bin\bot.exe --name "a b"' # literal Windows path
            run_command_linux = 'bin/bot --name "a b"'
            """);
        BotBuild bot = BotBuild.FromConfig("test", config);
        string expected = OperatingSystem.IsWindows() ? "bin\\bot.exe --name \"a b\"" : "bin/bot --name \"a b\"";
        Check(bot.Arguments[^1] == expected, $"wrong command: {bot.Arguments[^1]}");
        Check(bot.WorkingDirectory == directory, "wrong root directory");
        Check(OperatingSystem.IsWindows() ? bot.Program.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
            : bot.Program == "/bin/sh", "wrong platform shell");
    });
    Test("tournament fingerprint changes for a changed binary but ignores telemetry", () =>
    {
        BotBuild bot = BotBuild.FromConfig("test", config);
        string binary = Path.Combine(directory, "Bot.dll");
        File.WriteAllText(binary, "baseline");
        string before = Tournament.BuildFingerprint(bot);
        File.WriteAllText(Path.Combine(directory, "telemetry.jsonl"), "{}\n");
        Check(before == Tournament.BuildFingerprint(bot), "telemetry invalidated the build identity");
        File.WriteAllText(binary, "changed!");
        Check(before != Tournament.BuildFingerprint(bot), "same-size binary edit reused old results");
    });
    Test("Windows launcher preserves quoted programs and arguments containing spaces", () =>
    {
        if (!OperatingSystem.IsWindows()) return;
        string script = Path.Combine(directory, "a bot.cmd");
        File.WriteAllText(script, "@echo off\r\necho %~1\r\n");
        File.WriteAllText(config, "[settings]\nrun_command = '\"" + script + "\" \"two words\"'\n");
        using var process = System.Diagnostics.Process.Start(BotBuild.FromConfig("quoted", config).CreateStartInfo())!;
        string output = process.StandardOutput.ReadToEnd();
        Check(process.WaitForExit(5000) && process.ExitCode == 0 && output.Trim() == "two words",
            "quoted Windows command did not execute correctly");
    });
    Test("tournament cache rejects changed settings, incomplete games and failures", () =>
    {
        var key = new Tournament.CacheKey("a", "b", 2, 2, 123, 120);
        var games = new[] {
            new MatchResult { Blue = "a", Orange = "b", Seed = 123, TeamSize = 2 },
            new MatchResult { Blue = "b", Orange = "a", Seed = 123, TeamSize = 2 }
        };
        Check(Tournament.CanReuse(key, key, games), "matching complete results rejected");
        foreach (var other in new[] { key with { A = "new" }, key with { B = "new" }, key with { Seed = 124 },
            key with { TeamSize = 1 }, key with { Seconds = 180 }, key with { Games = 4 }, key with { Replays = true } })
            Check(!Tournament.CanReuse(key, other, games), "stale tournament results accepted");
        Check(!Tournament.CanReuse(key, key, games[..1]), "incomplete results accepted");
        Check(!Tournament.CanReuse(key, key with { Experiment = "different-physics" }, games), "changed physics reused results");
        Check(!Tournament.CanReuse(key, key with { ALabel = "other" }, games), "wrong participant reused results");
        games[1].Error = "bot disconnected";
        Check(!Tournament.CanReuse(key, key, games), "failed game counted as a completed tournament");
    });
    Test("tournament experiment identity includes tuning overrides", () =>
    {
        string? before = Environment.GetEnvironmentVariable("STARDUST_TUNE");
        try
        {
            Environment.SetEnvironmentVariable("STARDUST_TUNE", null);
            string original = Tournament.ExperimentFingerprint();
            Environment.SetEnvironmentVariable("STARDUST_TUNE", "Defense.SoloMargin=0.2");
            Check(original != Tournament.ExperimentFingerprint(), "tuning did not invalidate cached experiment");
        }
        finally { Environment.SetEnvironmentVariable("STARDUST_TUNE", before); }
    });
    Test("in-process bots emit the same match communication without a socket", () =>
    {
        var bot = new Bot.Stardust("sim-check");
        MatchCommT? received = null;
        bot.MatchCommSink = message => received = message;
        bot.SendMatchComm(2, 1, new List<byte> { 1, 2, 3 }, "claim", true);
        Check(received is { Index: 2, Team: 1, TeamOnly: true, Display: "claim" } &&
            received.Content.SequenceEqual(new byte[] { 1, 2, 3 }), "match communication was lost or changed");
    });
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"SIMULATOR CHECKS: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;
