using Stardust.Simulator;
using Stardust.Simulator.Match;
using Stardust.Simulator.Protocol;
using Stardust.Simulator.Lab;
using Stardust.Simulator.Scenarios;
using RLBot.Flat;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

if (args is ["--crash-output"])
{
    for (int line = 0; line < 1000; line++) Console.WriteLine($"startup line {line}");
    return 7;
}
if (args is ["--duplex-output"])
{
    string payload = new('x', 2048);
    for (int line = 0; line < 256; line++)
    {
        Console.Out.WriteLine($"out {line} {payload}");
        Console.Error.WriteLine($"err {line} {payload}");
    }
    return 7;
}
if (args is ["--pipe-holder"])
{
    Thread.Sleep(20000);
    return 0;
}
if (args is ["--orphan-wrapper", var marker])
{
    var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--pipe-holder");
    using var child = System.Diagnostics.Process.Start(start)!;
    File.WriteAllText(marker, child.Id.ToString());
    return 7;
}

int passed = 0, failed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.Message}"); }
}

Test("kickoff outcome accepts a direct goal without requiring a later touch", () =>
{
    foreach (int team in new[] { 0, 1 })
    {
        var drill = new TeamKickoffDrill(team);
        var setup = drill.Generate(new Random(3));
        Check(drill.Judge(setup, new EpisodeTrace { GoalTeam = team }),
            "a scored kickoff was labelled a failed follow-up");
        Check(!drill.Judge(setup, new EpisodeTrace { GoalTeam = 1 - team }),
            "a conceded kickoff was labelled successful");
    }
});

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

    Test("real manager handles fragmented lockstep frames and interleaved claims", () => ManagerTransportChecks.Run(false));
    Test("real manager releases controls on a failed frame and processes the next frame", () => ManagerTransportChecks.Run(true));
    Test("connection cleanup tolerates queued output after its shared wire closes", () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            peer.Connect((IPEndPoint)listener.LocalEndpoint);
            var connection = new RLBotConnection(listener.AcceptTcpClient());
            connection.Queue(CoreMessageUnion.FromPingRequest(new PingRequestT { Cookie = 42 }));
            connection.Dispose();
            connection.Dispose();
        }
        finally { listener.Stop(); }
    });

    Test("timeout reports identify the seat and last delivered frame", () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            peer.Connect((IPEndPoint)listener.LocalEndpoint);
            using var connection = new RLBotConnection(listener.AcceptTcpClient());
            using var process = new System.Diagnostics.Process();
            var agent = new ExternalBotAgent(new BotBuild("fixture", "unused", [], directory, "test"), process, connection, null)
                { TimeoutMilliseconds = 100 };
            var self = new Participant { Index = 3, Team = 1, Name = "orange-2", PlayerId = 1003, Agent = agent };
            byte[] bytes = RLBotConnection.Frame(CoreMessageUnion.FromPingRequest(new PingRequestT { Cookie = 42 }));
            agent.Send(self, bytes, bytes, new GamePacketT
            {
                MatchInfo = new MatchInfoT { FrameNum = 654, SecondsElapsed = 42.625f }
            }, new BallPredictionT(), []);
            try { agent.Receive(self, []); throw new Exception("silent peer did not time out"); }
            catch (BotCrashedException error)
            {
                Check(error.Message.Contains("seat 3 (orange-2)") && error.Message.Contains("frame 654 at 42.625s"),
                    "timeout lost the seat/frame context");
            }
        }
        finally { listener.Stop(); }
    });

    foreach (int split in new[] { 1, 2, 7 })
    Test($"socket framing resumes after a partial frame at byte {split}", () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            using TcpClient server = listener.AcceptTcpClient();
            NetworkStream stream = client.GetStream();
            Type type = typeof(RLBot.Manager.Bot).Assembly.GetType("RLBot.Util.SpecStreamReader")!;
            object reader = Activator.CreateInstance(type, stream)!;
            MethodInfo read = type.GetMethod("ReadOne")!;
            byte[] frame = RLBotConnection.Frame(CoreMessageUnion.FromPingRequest(new PingRequestT { Cookie = 123456789 }));
            client.Client.Blocking = false;
            server.GetStream().Write(frame, 0, split);
            Check(client.Client.Poll(1_000_000, SelectMode.SelectRead), "fragment did not arrive");
            try { read.Invoke(reader, null); throw new Exception("incomplete packet was accepted"); }
            catch (TargetInvocationException e) when (e.InnerException is IOException { InnerException: SocketException socket } &&
                socket.SocketErrorCode == SocketError.WouldBlock) { }
            server.GetStream().Write(frame, split, frame.Length - split);
            Check(client.Client.Poll(1_000_000, SelectMode.SelectRead), "remaining fragment did not arrive");
            CorePacket packet = (CorePacket)read.Invoke(reader, null)!;
            Check(packet.UnPack().Message.AsPingRequest().Cookie == 123456789, "partial read desynchronized the stream");
            server.GetStream().Write(frame);
            Check(client.Client.Poll(1_000_000, SelectMode.SelectRead), "next packet did not arrive");
            CorePacket next = (CorePacket)read.Invoke(reader, null)!;
            Check(next.UnPack().Message.AsPingRequest().Cookie == 123456789, "completed frame did not reset the reader");
        }
        finally { listener.Stop(); }
    });

    Test("interface draining preserves blocking writes and detects a disconnected peer", () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var api = new global::Interface("fragmented-interface-check");
            const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
            using var client = (TcpClient)typeof(global::Interface).GetField("_client", hidden)!.GetValue(api)!;
            client.Connect((IPEndPoint)listener.LocalEndpoint);
            using TcpClient server = listener.AcceptTcpClient();
            Type type = typeof(global::Interface).Assembly.GetType("RLBot.Util.SpecStreamReader")!;
            typeof(global::Interface).GetField("_socketSpecReader", hidden)!
                .SetValue(api, Activator.CreateInstance(type, client.GetStream()));
            typeof(global::Interface).GetProperty("IsConnected")!.SetValue(api, true);
            Check(api.HandleIncomingMessages(false) == global::Interface.MsgHandlingResult.NoIncomingMsgs,
                "empty nonblocking read did not yield");
            Check(client.Client.Blocking, "draining messages left controller writes nonblocking");
            server.Client.Shutdown(SocketShutdown.Both);
            server.Close();
            Check(client.Client.Poll(1_000_000, SelectMode.SelectRead), "peer closure was not visible");
            Check(api.HandleIncomingMessages(false) == global::Interface.MsgHandlingResult.Terminated,
                "closed peer was treated as a temporary empty queue");
        }
        finally { listener.Stop(); }
    });
    Test("blocking match workers leave thread-pool callbacks serviceable", () =>
    {
        ThreadPool.GetMinThreads(out int minimum, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximum, out int maximumIo);
        try
        {
            Check(ThreadPool.SetMinThreads(1, minimumIo) && ThreadPool.SetMaxThreads(1, maximumIo),
                "could not constrain the test pool");
            MatchWorkers.Run(4, 2, _ =>
            {
                // Process stdout/stderr drains resume on the pool while a match blocks for input.
                Task callback = Task.Run(() => { });
                Check(callback.Wait(2000), "blocked matches starved an asynchronous transport/log callback");
            });
        }
        finally
        {
            ThreadPool.SetMaxThreads(maximum, maximumIo);
            ThreadPool.SetMinThreads(minimum, minimumIo);
        }
    });
    Test("failed startup drains output before closing the log", () =>
    {
        var build = new BotBuild("crash", "dotnet", [Assembly.GetExecutingAssembly().Location, "--crash-output"], directory, "fixture");
        try
        {
            BotLauncher.Launch([new BotLauncher.Request(build, 0, 0, "crash", 1000)],
                new MatchConfigurationT(), new FieldInfoT(), directory, timeoutSeconds: 10);
            throw new Exception("crashed bot was accepted");
        }
        catch (BotCrashedException) { }
        string log = Path.Combine(directory, "bot-0-crash.log");
        Check(File.ReadAllLines(log).Length == 1000, "startup output was lost before cleanup completed");
        using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    });
    Test("stdout and stderr drain concurrently with a constrained thread pool", () =>
    {
        ThreadPool.GetMinThreads(out int minimum, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximum, out int maximumIo);
        try
        {
            Check(ThreadPool.SetMinThreads(1, minimumIo) && ThreadPool.SetMaxThreads(1, maximumIo),
                "could not constrain the test pool");
            var build = new BotBuild("duplex", "dotnet", [Assembly.GetExecutingAssembly().Location, "--duplex-output"], directory, "fixture");
            try
            {
                BotLauncher.Launch([new BotLauncher.Request(build, 0, 0, "duplex", 1000)],
                    new MatchConfigurationT(), new FieldInfoT(), directory, timeoutSeconds: 3);
                throw new Exception("crashed bot was accepted");
            }
            catch (BotCrashedException error)
            {
                Check(error.Message.Contains("code 7"), $"output blocked process completion: {error.Message}");
            }
            Check(File.ReadAllLines(Path.Combine(directory, "bot-0-duplex.log")).Length == 512,
                "both pipes were not completely drained");
        }
        finally
        {
            ThreadPool.SetMaxThreads(maximum, maximumIo);
            ThreadPool.SetMinThreads(minimum, minimumIo);
        }
    });
    Test("a failed log sink cannot block the bot process", () =>
    {
        var build = new BotBuild("log-failure", "dotnet", [Assembly.GetExecutingAssembly().Location, "--duplex-output"], directory, "fixture");
        using var process = new System.Diagnostics.Process { StartInfo = build.CreateStartInfo() };
        using var sink = new FailedLogWriter();
        process.Start();
        var output = new ProcessOutputDrain(process, sink);
        output.Start();
        try
        {
            Check(process.WaitForExit(3000), "failed sink stopped pipe drainage");
            output.Complete();
            Check(process.ExitCode == 7 && output.LogError is IOException, "log fault or child result was lost");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            output.Complete();
        }
    });
    Test("an exited wrapper with inherited pipe handles has bounded output cleanup", () =>
    {
        string marker = Path.Combine(directory, "pipe-holder.pid");
        var build = new BotBuild("wrapper", "dotnet", [Assembly.GetExecutingAssembly().Location, "--orphan-wrapper", marker], directory, "fixture");
        using var process = new System.Diagnostics.Process { StartInfo = build.CreateStartInfo() };
        using var sink = new StringWriter();
        process.Start();
        var output = new ProcessOutputDrain(process, sink);
        output.Start();
        try
        {
            Check(process.WaitForExit(5000) && process.ExitCode == 7, "wrapper did not exit");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Check(!output.Complete(100), "fixture did not preserve inherited pipe handles");
            Check(elapsed.ElapsedMilliseconds < 2000, "output cleanup waited for the surviving child");
        }
        finally
        {
            // This PID is written by the fixture we launched; do not target unrelated processes.
            if (File.Exists(marker))
            {
                using var child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            output.Complete();
        }
    });
    Test("an executable that cannot start still releases its log", () =>
    {
        var build = new BotBuild("missing", Path.Combine(directory, "not-an-executable"), [], directory, "fixture");
        try
        {
            BotLauncher.Launch([new BotLauncher.Request(build, 0, 0, "missing", 1000)],
                new MatchConfigurationT(), new FieldInfoT(), directory, timeoutSeconds: 10);
            throw new Exception("missing executable was accepted");
        }
        catch (System.ComponentModel.Win32Exception) { }
        using var exclusive = new FileStream(Path.Combine(directory, "bot-0-missing.log"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    });
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"SIMULATOR CHECKS: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class FailedLogWriter : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void WriteLine(string? value) => throw new IOException("Deliberate log sink failure.");
}
