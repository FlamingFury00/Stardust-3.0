using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RLBot.Flat;
using Stardust.Simulator.Match;

namespace Stardust.Simulator.Protocol;

/// <summary>Where a bot build lives and how to start it.</summary>
public sealed record BotBuild(string Label, string EntryAssembly)
{
    /// <summary>Accepts a build directory (containing Bot.dll) or a path to the entry assembly.</summary>
    public static BotBuild FromPath(string label, string path)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
            full = Path.Combine(full, "Bot.dll");
        if (!File.Exists(full))
            throw new FileNotFoundException($"Bot entry assembly not found: {full}");
        return new BotBuild(label, full);
    }
}

/// <summary>A bot process speaking the RLBot v5 socket protocol to this simulator.</summary>
public sealed class ExternalBotAgent : IAgent
{
    private readonly Process process;
    private readonly RLBotConnection connection;
    private readonly StreamWriter? log;
    private ControllerStateT last = new();

    public ExternalBotAgent(BotBuild build, Process process, RLBotConnection connection, StreamWriter? log)
    {
        Build = build;
        this.process = process;
        this.connection = connection;
        this.log = log;
    }

    public BotBuild Build { get; }
    public string Description => $"{Build.Label} ({Path.GetFileName(Path.GetDirectoryName(Build.EntryAssembly))})";
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
                throw new BotCrashedException($"{Description} stopped responding: {e.Message}", e);
            }

            switch (message.Message.Type)
            {
                case InterfaceMessage.PlayerInput:
                    last = message.Message.AsPlayerInput().ControllerState ?? new ControllerStateT();
                    return last;
                case InterfaceMessage.MatchComm:
                    if (connection.WantsComms)
                        outgoingComms.Add(message.Message.AsMatchComm());
                    break;
                case InterfaceMessage.DisconnectSignal:
                    throw new BotCrashedException($"{Description} disconnected.");
            }
        }
    }

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
        connection.Dispose();
        process.Dispose();
        log?.Dispose();
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
        var processes = new Dictionary<string, (Request Request, Process Process, StreamWriter? Log)>();

        try
        {
            foreach (Request request in requests)
            {
                string agentId = $"stardust-sim/{port}/{request.Index}";
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(request.Build.EntryAssembly)!,
                };
                start.ArgumentList.Add(request.Build.EntryAssembly);
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

                var process = new Process { StartInfo = start, EnableRaisingEvents = true };
                StreamWriter? sink = log;
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (process) sink?.WriteLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (process) sink?.WriteLine("ERR " + e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                processes[agentId] = (request, process, log);
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
                    new ExternalBotAgent(request.Build, owner.Process, connection, owner.Log);
                connected++;
            }

            return agents.Select(a => a!).ToList();
        }
        catch
        {
            foreach (var entry in processes.Values)
            {
                try { entry.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                entry.Log?.Dispose();
            }
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }
}
