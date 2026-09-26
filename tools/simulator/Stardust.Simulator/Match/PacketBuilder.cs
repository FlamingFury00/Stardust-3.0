using RLBot.Flat;
using Stardust.Simulator.Physics;

namespace Stardust.Simulator.Match;

/// <summary>Converts simulator state into the RLBot v5 flatbuffer objects a real server would send.</summary>
public static class PacketBuilder
{
    public const float Gravity = -650f;
    public const int PredictionTicks = 720; // six seconds at 120 Hz, matching RLBot v5
    private const float DoubleJumpMaxDelay = 1.25f;

    public static Vector3T V(RsbVec v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    /// <summary>Unreal rotator (pitch, yaw, roll) whose matrix has the given forward/right/up columns.</summary>
    public static RotatorT Rotator(in RsbPhysics p)
    {
        float pitch = MathF.Asin(Math.Clamp(p.Forward.Z, -1f, 1f));
        float yaw = MathF.Atan2(p.Forward.Y, p.Forward.X);
        float roll = MathF.Atan2(-p.Right.Z, p.Up.Z);
        return new RotatorT { Pitch = pitch, Yaw = yaw, Roll = roll };
    }

    public static PhysicsT Physics(in RsbPhysics p) => new()
    {
        Location = V(p.Position),
        Rotation = Rotator(p),
        Velocity = V(p.Velocity),
        AngularVelocity = V(p.AngularVelocity),
    };

    public static AirState AirStateOf(in RsbCarState car, int doubleJumpTicks)
    {
        if (car.IsOnGround != 0) return AirState.OnGround;
        if (car.IsJumping != 0) return AirState.Jumping;
        if (car.IsFlipping != 0) return AirState.Dodging;
        if (doubleJumpTicks > 0) return AirState.DoubleJumping;
        return AirState.InAir;
    }

    public static float DodgeTimeout(in RsbCarState car)
    {
        if (car.IsOnGround != 0 || car.HasJumped == 0 || car.HasDoubleJumped != 0 || car.HasFlipped != 0)
            return -1f;
        float remaining = DoubleJumpMaxDelay - car.AirTimeSinceJump;
        return remaining > 0 ? remaining : -1f;
    }

    public static PlayerInfoT Player(Participant p, in RsbCarState car)
    {
        return new PlayerInfoT
        {
            Physics = Physics(car.Physics),
            ScoreInfo = new ScoreInfoT
            {
                Score = p.Score, Goals = p.Goals, OwnGoals = p.OwnGoals, Assists = p.Assists,
                Saves = p.Saves, Shots = p.Shots, Demolitions = p.Demolitions,
            },
            Hitbox = new BoxShapeT { Length = p.HitboxSize.X, Width = p.HitboxSize.Y, Height = p.HitboxSize.Z },
            HitboxOffset = V(p.HitboxOffset),
            LatestTouch = p.LatestTouch,
            AirState = AirStateOf(car, p.DoubleJumpTicks),
            DodgeTimeout = DodgeTimeout(car),
            DemolishedTimeout = car.IsDemoed != 0 ? car.DemoRespawnTimer : -1f,
            IsSupersonic = car.IsSupersonic != 0,
            IsBot = true,
            Name = p.Name,
            Team = (uint)p.Team,
            Boost = car.Boost,
            PlayerId = p.PlayerId,
            Accolades = new List<string>(),
            LastInput = p.LastInput,
            HasJumped = car.HasJumped != 0,
            HasDoubleJumped = car.HasDoubleJumped != 0,
            HasDodged = car.HasFlipped != 0,
            DodgeElapsed = car.IsFlipping != 0 ? car.FlipTime : 0f,
            DodgeDir = new Vector2T(),
        };
    }

    public static BallInfoT Ball(in RsbBallState ball) => new()
    {
        Physics = Physics(ball.Physics),
        Shape = CollisionShapeUnion.FromSphereShape(new SphereShapeT { Diameter = SimArena.BallRadius * 2 }),
        ChargeLevel = -1, // not a dropshot ball
    };

    public static BallPredictionT Prediction(RsbBallState[] states, int count, float now)
    {
        var slices = new List<PredictionSliceT>(count);
        for (int i = 0; i < count; i++)
            slices.Add(new PredictionSliceT
            {
                GameSeconds = now + (i + 1) * SimArena.TickTime,
                Physics = Physics(states[i].Physics),
            });
        return new BallPredictionT { Slices = slices };
    }

    [ThreadStatic] private static Google.FlatBuffers.FlatBufferBuilder? predictionBuilder;

    /// <summary>Serializes the prediction straight into a framed CorePacket without object allocation.</summary>
    public static byte[] FramePrediction(RsbBallState[] states, int count, float now)
    {
        var builder = predictionBuilder ??= new Google.FlatBuffers.FlatBufferBuilder(64 * 1024);
        builder.Clear();
        BallPrediction.StartSlicesVector(builder, count);
        for (int i = count - 1; i >= 0; i--)
        {
            RsbPhysics p = states[i].Physics;
            RotatorT r = Rotator(p);
            PredictionSlice.CreatePredictionSlice(builder, now + (i + 1) * SimArena.TickTime,
                p.Position.X, p.Position.Y, p.Position.Z, r.Pitch, r.Yaw, r.Roll,
                p.Velocity.X, p.Velocity.Y, p.Velocity.Z,
                p.AngularVelocity.X, p.AngularVelocity.Y, p.AngularVelocity.Z);
        }
        var slices = builder.EndVector();
        var prediction = BallPrediction.CreateBallPrediction(builder, slices);
        var core = CorePacket.CreateCorePacket(builder, CoreMessage.BallPrediction, prediction.Value);
        builder.Finish(core.Value);
        ArraySegment<byte> bytes = builder.DataBuffer.ToArraySegment(
            builder.DataBuffer.Position, builder.DataBuffer.Length - builder.DataBuffer.Position);
        var framed = new byte[bytes.Count + 2];
        framed[0] = (byte)(bytes.Count >> 8);
        framed[1] = (byte)(bytes.Count & 0xFF);
        Array.Copy(bytes.Array!, bytes.Offset, framed, 2, bytes.Count);
        return framed;
    }

    public static float PadRespawn(bool big) => big ? 10f : 4f;

    public static FieldInfoT FieldInfo(RsbPad[] pads)
    {
        return new FieldInfoT
        {
            BoostPads = pads.Select(p => new BoostPadT { Location = V(p.Position), IsFullBoost = p.IsBig != 0 }).ToList(),
            Goals =
            [
                new GoalInfoT { TeamNum = 0, Location = new Vector3T { X = 0, Y = -5120, Z = 321.3875f },
                    Direction = new Vector3T { X = 0, Y = 1, Z = 0 }, Width = 1785.55f, Height = 642.775f },
                new GoalInfoT { TeamNum = 1, Location = new Vector3T { X = 0, Y = 5120, Z = 321.3875f },
                    Direction = new Vector3T { X = 0, Y = -1, Z = 0 }, Width = 1785.55f, Height = 642.775f },
            ],
            Tiles = [], // Soccar has no dropshot tiles
        };
    }

    public static MatchConfigurationT MatchConfiguration(IEnumerable<Participant> participants)
    {
        return new MatchConfigurationT
        {
            Launcher = Launcher.Custom,
            LauncherArg = "stardust-simulator",
            AutoStartAgents = false,
            WaitForAgents = true,
            GameMapUpk = "Stadium_P",
            PlayerConfigurations = participants.Select(p => new PlayerConfigurationT
            {
                Variety = PlayerClassUnion.FromCustomBot(new CustomBotT
                {
                    Name = p.Name, RootDir = "", RunCommand = "", AgentId = $"sim/{p.Index}", Hivemind = false,
                }),
                Team = (uint)p.Team,
                PlayerId = p.PlayerId,
            }).ToList(),
            ScriptConfigurations = new List<ScriptConfigurationT>(),
            GameMode = GameMode.Soccar,
            SkipReplays = true,
            InstantStart = true,
            Mutators = new MutatorSettingsT(),
            EnableRendering = DebugRendering.AlwaysOff,
            EnableStateSetting = false,
            AutoSaveReplay = false,
            Freeplay = false,
        };
    }
}
