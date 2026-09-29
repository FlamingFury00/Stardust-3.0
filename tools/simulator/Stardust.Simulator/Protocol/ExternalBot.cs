using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RLBot.Flat;
using Stardust.Simulator.Match;
using Tomlyn;
using Tomlyn.Model;

namespace Stardust.Simulator.Protocol;

/// <summary>How to start a bot process: program, arguments and working directory.</summary>
public sealed record BotBuild(string Label, string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, string Source)
{
    public ProcessStartInfo CreateStartInfo()
    {
        var start = new ProcessStartInfo(Program)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = WorkingDirectory,
        };
        if (OperatingSystem.IsWindows() && Arguments.Count == 4 && Arguments[2] == "/c" &&
            Path.GetFileName(Program).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            // cmd parses shell quotes, not the C runtime backslash escaping used by ArgumentList.
            // /s removes this outer pair while preserving a quoted executable or argument inside.
            start.Arguments = "/d /s /c \"" + Arguments[3] + "\"";
        }
        else
            foreach (string argument in Arguments) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// Accepts a .NET build directory (containing Bot.dll), a path to an entry assembly, or an RLBot v5
    /// bot config (<c>*.toml</c>), started with the host platform's command from its root directory.
    /// </summary>
    public static BotBuild FromPath(string label, string path)
    {
        string full = Path.GetFullPath(path);
        if (full.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            return FromConfig(label, full);
        if (Directory.Exists(full))
            full = Path.Combine(full, "Bot.dll");
        if (!File.Exists(full))
            throw new FileNotFoundException($"Bot entry assembly not found: {full}");
        string directory = Path.GetDirectoryName(full)!;
        return new BotBuild(label, "dotnet", [full], directory, Path.GetFileName(directory));
    }

    /// <summary>
    /// Reads the platform-specific command. On Unix, a Windows-only command retains the existing
    /// path translation fallback; Windows commands and executable extensions are preserved.
    /// </summary>
    public static BotBuild FromConfig(string label, string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Bot config not found: {configPath}");
        Dictionary<string, string> settings = ReadTomlSection(configPath, "settings");
        bool windows = OperatingSystem.IsWindows();
        string command = (windows ? settings.GetValueOrDefault("run_command") :
            settings.GetValueOrDefault("run_command_linux") ?? settings.GetValueOrDefault("run_command"))
            ?? throw new InvalidDataException($"{configPath} has no run_command.");
        if (!windows && !settings.ContainsKey("run_command_linux"))
            command = command.Replace('\\', '/').Replace("/Scripts/", "/bin/").Replace(".exe", "");
        string root = settings.GetValueOrDefault("root_dir") ?? "";
        string directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, root));
        return windows
            ? new BotBuild(label, Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                ["/d", "/s", "/c", command], directory, Path.GetFileName(configPath))
            : new BotBuild(label, "/bin/sh", ["-c", command], directory, Path.GetFileName(configPath));
    }

    /// <summary>Parse TOML literal/escaped strings with the same parser used by RLBot.</summary>
    private static Dictionary<string, string> ReadTomlSection(string path, string section)
    {
        var values = new Dictionary<string, string>();
        TomlTable model = Toml.ToModel(File.ReadAllText(path));
        if (model.TryGetValue(section, out object? table) && table is TomlTable settings)
            foreach (var (key, value) in settings)
                if (value is string text) values[key] = text;
        return values;
    }
}

/// <summary>A bot process speaking the RLBot v5 socket protocol to this simulator.</summary>
public sealed class ExternalBotAgent : IAgent
{
    private readonly Process process;
    private readonly RLBotConnection connection;
    private readonly StreamWriter? log;
    private readonly ProcessOutputDrain? output;
    private ControllerStateT last = new();
    private uint? lastSentFrame;
    private float lastSentTime;

    public ExternalBotAgent(BotBuild build, Process process, RLBotConnection connection, StreamWriter? log,
        ProcessOutputDrain? output = null)
    {
        Build = build;
        this.process = process;
        this.connection = connection;
        this.log = log;
        this.output = output;
    }

    public BotBuild Build { get; }
    public string Description => $"{Build.Label} ({Build.Source})";
    public int TimeoutMilliseconds { get; set; } = 20000;

    public void Send(Participant self, byte[] framedPrediction, byte[] framedPacket, GamePacketT packet,
        BallPredictionT prediction, IReadOnlyList<byte[]> framedComms)
    {
        foreach (byte[] comm in framedComms)
            connection.WriteFramed(comm);
        if (connection.WantsBallPredictions)
            connection.WriteFramed(framedPrediction);
        connection.WriteFramed(framedPacket);
        connection.Flush();
        lastSentFrame = packet.MatchInfo.FrameNum;
        lastSentTime = packet.MatchInfo.SecondsElapsed;
    }

    public ControllerStateT Receive(Participant self, List<MatchCommT> outgoingComms)
    {
        while (true)
        {
            InterfacePacketT message;
            try
            {
                message = connection.Read(TimeoutMilliseconds);
            }
            catch (Exception e) when (e is IOException or SocketException or EndOfStreamException)
            {
                throw new BotCrashedException($"{Context(self)} stopped responding: {e.Message}", e);
            }

            switch (message.Message.Type)
            {
                case InterfaceMessage.PlayerInput:
                    PlayerInputT input = message.Message.AsPlayerInput();
                    if (input.PlayerIndex != self.Index)
                        throw new BotCrashedException($"{Context(self)} returned controls for seat {input.PlayerIndex}.");
                    last = input.ControllerState ?? new ControllerStateT();
                    return last;
                case InterfaceMessage.MatchComm:
                    if (connection.WantsComms)
                        outgoingComms.Add(message.Message.AsMatchComm());
                    break;
                case InterfaceMessage.DisconnectSignal:
                    throw new BotCrashedException($"{Context(self)} disconnected.");
            }
        }
    }

    private string Context(Participant self) => lastSentFrame is { } frame
        ? FormattableString.Invariant($"{Description}, seat {self.Index} ({self.Name}), frame {frame} at {lastSentTime:F3}s")
        : $"{Description}, seat {self.Index} ({self.Name}), before the first frame";

    public void Dispose()
    {
        try
        {
            connection.Queue(CoreMessageUnion.FromDisconnectSignal(new DisconnectSignalT()));
            connection.Flush();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }

        if (!process.WaitForExit(3000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        process.WaitForExit();
        try { output?.Complete(); }
        finally
        {
            connection.Dispose();
            process.Dispose();
            log?.Dispose();
        }
    }
}

public sealed class BotCrashedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal RLBotServer: launches bot processes, performs the v5 handshake, and hands back connected
/// agents. Each bot receives its own controllable-team info, the match configuration and field info.
/// </summary>
public static class BotLauncher
{
    public sealed record Request(BotBuild Build, int Index, int Team, string Name, int PlayerId);

    public static IReadOnlyList<ExternalBotAgent> Launch(IReadOnlyList<Request> requests,
        MatchConfigurationT matchConfig, FieldInfoT fieldInfo, string? logDirectory, int timeoutSeconds = 60)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var processes = new Dictionary<string, (Request Request, Process Process, StreamWriter? Log, ProcessOutputDrain Output)>();
        var connections = new List<RLBotConnection>();

        try
        {
            foreach (Request request in requests)
            {
                string agentId = $"stardust-sim/{port}/{request.Index}";
                var start = request.Build.CreateStartInfo();
                start.Environment["RLBOT_AGENT_ID"] = agentId;
                start.Environment["RLBOT_SERVER_PORT"] = port.ToString();
                // Production builds log telemetry to files by default; keep simulated series quiet
                // unless the caller explicitly asked for telemetry.
                if (Environment.GetEnvironmentVariable("STARDUST_TELEMETRY") == null)
                    start.Environment["STARDUST_TELEMETRY"] = "0";

                StreamWriter? log = null;
                if (logDirectory != null)
                {
                    Directory.CreateDirectory(logDirectory);
                    log = new StreamWriter(Path.Combine(logDirectory, $"bot-{request.Index}-{request.Name}.log")) { AutoFlush = true };
                }

                var process = new Process { StartInfo = start };
                var output = new ProcessOutputDrain(process, log);
                // Register before Start: an invalid executable must also release its opened log.
                processes[agentId] = (request, process, log, output);
                process.Start();
                output.Start();
            }

            var agents = new ExternalBotAgent?[requests.Count];
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            int connected = 0;
            while (connected < requests.Count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Timed out waiting for bots to connect.");
                foreach (var entry in processes.Values)
                    if (entry.Process.HasExited)
                        throw new BotCrashedException(
                            $"Bot {entry.Request.Name} exited during startup with code {entry.Process.ExitCode}.");
                if (!listener.Pending())
                {
                    Thread.Sleep(5);
                    continue;
                }

                var connection = new RLBotConnection(listener.AcceptTcpClient());
                connections.Add(connection);
                InterfacePacketT hello = connection.Read(timeoutSeconds * 1000);
                if (hello.Message.Type != InterfaceMessage.ConnectionSettings)
                    throw new InvalidDataException($"Expected ConnectionSettings, got {hello.Message.Type}.");
                ConnectionSettingsT settings = hello.Message.AsConnectionSettings();
                if (!processes.TryGetValue(settings.AgentId, out var owner))
                    throw new InvalidDataException($"Unknown agent id {settings.AgentId}.");

                connection.AgentId = settings.AgentId;
                connection.WantsBallPredictions = settings.WantsBallPredictions;
                connection.WantsComms = settings.WantsComms;

                Request request = owner.Request;
                connection.Queue(CoreMessageUnion.FromControllableTeamInfo(new ControllableTeamInfoT
                {
                    Team = (uint)request.Team,
                    Controllables = [new ControllableInfoT { Index = (uint)request.Index, Identifier = request.PlayerId }],
                }));
                connection.Queue(CoreMessageUnion.FromMatchConfiguration(matchConfig));
                connection.Queue(CoreMessageUnion.FromFieldInfo(fieldInfo));
                connection.Flush();

                while (true)
                {
                    InterfacePacketT next = connection.Read(timeoutSeconds * 1000);
                    if (next.Message.Type == InterfaceMessage.InitComplete)
                        break;
                }

                agents[requests.ToList().IndexOf(request)] =
                    new ExternalBotAgent(request.Build, owner.Process, connection, owner.Log, owner.Output);
                connected++;
            }

            return agents.Select(a => a!).ToList();
        }
        catch
        {
            foreach (var entry in processes.Values)
            {
                try
                {
                    if (!entry.Process.HasExited) entry.Process.Kill(entireProcessTree: true);
                    entry.Process.WaitForExit();
                }
                catch (InvalidOperationException) { } // Start failed before a process existed.
                finally
                {
                    try { entry.Output.Complete(); }
                    finally
                    {
                        entry.Log?.Dispose();
                        entry.Process.Dispose();
                    }
                }
            }
            foreach (var connection in connections) connection.Dispose();
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }
}
