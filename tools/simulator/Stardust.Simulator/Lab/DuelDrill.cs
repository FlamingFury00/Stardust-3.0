using RLBot.Flat;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>How the scripted defender meets a dribble.</summary>
public enum DefenderMode
{
    /// <summary>Drives straight at the carrier with boost and jumps into the ball: the moment to flick.</summary>
    Challenge,
    /// <summary>Retreats ahead of the carrier, holding about 900 uu of cushion: flicking gives the ball away.</summary>
    Shadow,
    /// <summary>Chases from behind at speed: keep the ball and accelerate away.</summary>
    Chase,
}

/// <summary>A deterministic defender with the three behaviours a dribbler must read.</summary>
public sealed class DefenderAgent : ScriptedAgent
{
    private float jumpStarted = float.NaN;
    public DefenderMode Mode { get; set; }
    public override string Description => "defender";

    public void Reset() => jumpStarted = float.NaN;

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        PlayerInfoT car = packet.Players[self.Index];
        Vector3T ball = packet.Balls[0].Physics.Location;
        Vector3T ballVelocity = packet.Balls[0].Physics.Velocity;
        float now = packet.MatchInfo.SecondsElapsed;
        var controls = new ControllerStateT();
        if (float.IsFinite(jumpStarted))
        {
            controls.Jump = now - jumpStarted < 0.15f;
            controls.Throttle = 1;
            if (car.AirState == AirState.OnGround && now - jumpStarted > 0.4f) jumpStarted = float.NaN;
            return controls;
        }

        float dx = ball.X - car.Physics.Location.X, dy = ball.Y - car.Physics.Location.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (Mode == DefenderMode.Shadow)
        {
            // Hold a cushion in front of the ball on its line to our goal, facing our goal like a shadow defender.
            float goalSide = self.Team == 0 ? -1 : 1;
            float cushionY = ball.Y + goalSide * 900;
            float error = (cushionY - car.Physics.Location.Y) * goalSide;
            float closing = (ballVelocity.Y - car.Physics.Velocity.Y) * goalSide;
            controls.Steer = Flat.SteerToward(car, ball.X, car.Physics.Location.Y + goalSide * 1500);
            controls.Throttle = Math.Clamp(error / 250f + closing / 400f, -1f, 1f);
            controls.Boost = controls.Throttle > 0.95f && MathF.Abs(controls.Steer) < 0.2f;
            return controls;
        }

        controls.Steer = Flat.SteerToward(car, ball.X, ball.Y);
        controls.Throttle = 1;
        controls.Boost = MathF.Abs(controls.Steer) < 0.3f;
        float reach = Mode == DefenderMode.Challenge ? 350 : 250;
        if (car.AirState == AirState.OnGround && distance < reach && ball.Z < 320)
        {
            jumpStarted = now;
            controls.Jump = true;
        }
        return controls;
    }
}

/// <summary>
/// Pressured dribble, full bot: Stardust carries the ball at about 1000 uu/s from its own half while
/// a defender challenges, shadows, or chases. Judged on whether the play keeps or converts the ball.
/// </summary>
public sealed class DribbleDuelDrill : RoofDrill
{
    private readonly DefenderAgent defender = new();
    private DefenderMode mode;
    private bool flicked;

    public override string Name => "dribble-duel";
    public override string Description => "Carry under pressure: flick a committed challenger, keep the ball against shadows and chasers.";
    public override int SeatCount => 2;
    public override IAgent? ScriptedOpponent(int seat) => defender;

    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Mean("challenge-kept", 0.6), Criterion.Mean("shadow-kept", 0.6), Criterion.Mean("chase-kept", 0.5),
        Criterion.Mean("shadow-flick", 0.3, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        mode = (DefenderMode)r.Next(3);
        var car = V(Uniform(r, -800, 800), -2000, 17.01f);
        float yaw = MathF.PI / 2 + Uniform(r, -0.15f, 0.15f);
        EpisodeSetup carry = CarryStart(car, yaw, Uniform(r, 900, 1100), Uniform(r, 0, 20), Uniform(r, -10, 10),
            Uniform(r, 30, 80), 3.5f);
        CarSetup opponent = mode switch
        {
            DefenderMode.Challenge => new CarSetup(V(car.X + Uniform(r, -300, 300), car.Y + 1800 + Uniform(r, -200, 200), 17.01f),
                -MathF.PI / 2, V(0, -Uniform(r, 0, 800), 0), 100),
            DefenderMode.Shadow => new CarSetup(V(car.X + Uniform(r, -300, 300), car.Y + 900, 17.01f),
                MathF.PI / 2, V(0, Uniform(r, 800, 1100), 0), 100),
            _ => new CarSetup(V(car.X + Uniform(r, -200, 200), car.Y - 700, 17.01f),
                MathF.PI / 2, V(0, 1600, 0), 100),
        };
        return carry with { Cars = new[] { carry.Cars[0], opponent } };
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        defender.Mode = mode;
        defender.Reset();
        flicked = false;
        Subject.Director = null;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        flicked |= Subject.Action is global::Bot.Flick;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        string key = mode.ToString().ToLowerInvariant();
        bool goal = trace.GoalTeam == 0;
        bool ours = trace.TouchLog.Count > 0 && trace.TouchLog[^1].Toucher == 0;
        bool opponentTouched = trace.TouchLog.Any(t => t.Toucher == 1);
        // Kept: scored, or still ours and upfield of where the carry started.
        bool kept = goal || (ours && trace.GoalTeam < 0 && trace.FinalBallPosition.Y > setup.BallPosition.Y + 500);
        trace.Metrics[$"{key}-kept"] = kept ? 1 : 0;
        trace.Metrics[$"{key}-flick"] = flicked ? 1 : 0;
        trace.Metrics[$"{key}-contested"] = opponentTouched ? 1 : 0;
        trace.Metrics["goal"] = goal ? 1 : 0;
        trace.Metrics["conceded"] = trace.GoalTeam == 1 ? 1 : 0;
        return kept;
    }
}
