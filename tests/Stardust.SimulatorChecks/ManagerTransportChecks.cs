using System.Net;
using System.Net.Sockets;
using RLBot.Flat;
using Stardust.Simulator.Protocol;

/// <summary>Exercise Bot.Run and the real socket pump, including controller faults and framing.</summary>
internal static class ManagerTransportChecks
{
    private sealed class Probe(bool fail) : RLBot.Manager.Bot("manager-transport-check")
    {
        public int Calls, Claims;
        public override void HandleMatchComm(int index, int team, List<byte> content, string? display, bool teamOnly) => Claims++;
        public override ControllerStateT GetOutput(GamePacketT packet)
        {
            Calls++;
            uint frame = packet.MatchInfo.FrameNum;
            if (BallPrediction.Slices.Count != 1 || BallPrediction.Slices[0].GameSeconds != frame)
                throw new InvalidOperationException("Prediction and game packet were desynchronized.");
            if (fail && frame == 6) throw new InvalidOperationException("Deliberate controller fault.");
            SendMatchComm(0, 0, [(byte)frame], teamOnly: true);
            return new ControllerStateT { Throttle = frame / 100f, Jump = true, Boost = true };
        }
    }

    /// <summary>
    /// Longest wait (ms) for one message from the manager. The check is about order and content, not
    /// latency: a loaded CI runner has taken over 3 s to answer the frame after a logged controller fault.
    /// </summary>
    private const int ReadTimeout = 15000;

    public static void Run(bool fail)
    {
        const int count = 64;
        string? previousPort = Environment.GetEnvironmentVariable("RLBOT_SERVER_PORT");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var bot = new Probe(fail);
        var thread = new Thread(() => bot.Run()) { IsBackground = true };
        TcpClient? peer = null;
        try
        {
            Environment.SetEnvironmentVariable("RLBOT_SERVER_PORT", ((IPEndPoint)listener.LocalEndpoint).Port.ToString());
            thread.Start();
            var accept = listener.AcceptTcpClientAsync();
            if (!accept.Wait(5000)) throw new Exception("manager did not connect");
            peer = accept.Result;
            using var server = new RLBotConnection(peer);
            if (server.Read(ReadTimeout).Message.Type != InterfaceMessage.ConnectionSettings)
                throw new Exception("manager did not send connection settings");
            server.Queue(CoreMessageUnion.FromControllableTeamInfo(new ControllableTeamInfoT
            {
                Team = 0, Controllables = [new ControllableInfoT { Index = 0, Identifier = 123 }]
            }));
            server.Queue(CoreMessageUnion.FromMatchConfiguration(new MatchConfigurationT
            {
                LauncherArg = "", GameMapUpk = "Stadium_P", ScriptConfigurations = [],
                PlayerConfigurations = [new PlayerConfigurationT
                {
                    PlayerId = 123, Team = 0,
                    Variety = PlayerClassUnion.FromCustomBot(new CustomBotT
                    { Name = "manager-probe", RootDir = "", RunCommand = "", AgentId = "manager-transport-check" })
                }]
            }));
            server.Queue(CoreMessageUnion.FromFieldInfo(new FieldInfoT { BoostPads = [], Goals = [], Tiles = [] }));
            server.Flush();
            if (server.Read(ReadTimeout).Message.Type != InterfaceMessage.InitComplete)
                throw new Exception("manager did not initialize");

            NetworkStream wire = peer.GetStream();
            for (uint frame = 1; frame <= count; frame++)
            {
                SendFragmented(CoreMessageUnion.FromBallPrediction(new BallPredictionT
                {
                    Slices = [new PredictionSliceT { GameSeconds = frame, Physics = new PhysicsT() }]
                }), (int)frame);
                server.Queue(CoreMessageUnion.FromGamePacket(new GamePacketT
                {
                    Players = [new PlayerInfoT
                    {
                        PlayerId = 123, Physics = new PhysicsT(), ScoreInfo = new ScoreInfoT(),
                        Hitbox = new BoxShapeT(), HitboxOffset = new Vector3T(), Name = "manager-probe",
                        Accolades = [], LastInput = new ControllerStateT(), DodgeDir = new Vector2T()
                    }],
                    Balls = [], BoostPads = [], Tiles = [], Teams = [],
                    MatchInfo = new MatchInfoT { FrameNum = frame, SecondsElapsed = 10f + frame / 120f }
                }));
                server.Flush();
                // A partial message after the game packet exercises the nonblocking drain path.
                SendFragmented(CoreMessageUnion.FromMatchComm(new MatchCommT
                {
                    Index = 1, Team = 0, Content = [(byte)frame], TeamOnly = true
                }), (int)frame + 1);
                InterfacePacketT output;
                do { output = server.Read(ReadTimeout); }
                while (output.Message.Type == InterfaceMessage.MatchComm);
                if (output.Message.Type != InterfaceMessage.PlayerInput)
                    throw new Exception($"frame {frame} had no controller response");
                PlayerInputT input = output.Message.AsPlayerInput();
                bool neutral = fail && frame == 6;
                if (input.PlayerIndex != 0 || MathF.Abs(input.ControllerState.Throttle - (neutral ? 0f : frame / 100f)) > 1e-6f ||
                    input.ControllerState.Jump == neutral || input.ControllerState.Boost == neutral)
                    throw new Exception($"incorrect or duplicated controller at frame {frame}");
            }
            server.Queue(CoreMessageUnion.FromPingRequest(new PingRequestT { Cookie = 42 }));
            server.Flush();
            if (server.Read(ReadTimeout).Message.Type != InterfaceMessage.PingResponse)
                throw new Exception("extra controller or lost ping after lockstep sequence");
            server.Queue(CoreMessageUnion.FromDisconnectSignal(new DisconnectSignalT()));
            server.Flush();
            if (!thread.Join(3000) || bot.Calls != count || bot.Claims != count)
                throw new Exception($"pump did not finish cleanly: {bot.Calls} outputs, {bot.Claims} claims");

            void SendFragmented(CoreMessageUnion message, int pattern)
            {
                byte[] bytes = RLBotConnection.Frame(message);
                int split = new[] { 1, 2, 7 }[pattern % 3];
                wire.Write(bytes, 0, split);
                Thread.Sleep(1);
                wire.Write(bytes, split, bytes.Length - split);
            }
        }
        finally
        {
            peer?.Dispose();
            listener.Stop();
            thread.Join(3000);
            Environment.SetEnvironmentVariable("RLBOT_SERVER_PORT", previousPort);
        }
    }
}
