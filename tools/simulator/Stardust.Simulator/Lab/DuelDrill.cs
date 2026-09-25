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
    private int predictedGoal = -1;
    private float endGap, opponentGap, ballPastDefender;

    public override string Name => "dribble-duel";
    public override string Description => "Carry under pressure: flick a committed challenger, keep the ball against shadows and chasers.";
    public override int SeatCount => 2;
    public override IAgent? ScriptedOpponent(int seat) => defender;

    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Mean("challenge-good", 0.6), Criterion.Mean("shadow-good", 0.7), Criterion.Mean("chase-good", 0.6),
        Criterion.Mean("shadow-flick", 0.3, atLeast: false), Criterion.Mean("lost", 0.25, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        mode = (DefenderMode)r.Next(3);
        var car = V(Uniform(r, -800, 800), -2000, 17.01f);
        float yaw = MathF.PI / 2 + Uniform(r, -0.15f, 0.15f);
        EpisodeSetup carry = CarryStart(car, yaw, Uniform(r, 900, 1100), Uniform(r, 0, 20), Uniform(r, -10, 10),
            Uniform(r, 30, 80), 4f);
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
        predictedGoal = -1;
        Subject.Director = null;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        flicked |= Subject.Action is global::Bot.Flick;
        predictedGoal = session.PredictedGoalTeam();
        RsbVec ball = session.Ball.Physics.Position;
        endGap = ball.Distance(session.Cars[0].Physics.Position);
        opponentGap = ball.Distance(session.Cars[1].Physics.Position);
        ballPastDefender = ball.Y - session.Cars[1].Physics.Position.Y;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        string key = mode.ToString().ToLowerInvariant();
        trace.Notes.Insert(0, key);
        bool goal = trace.GoalTeam == 0;
        bool ours = trace.TouchLog.Count > 0 && trace.TouchLog[^1].Toucher == 0;
        // Good: scored, a shot heading into the goal, or the ball still ours and close to our car.
        bool shot = trace.GoalTeam < 0 && predictedGoal == 1;
        bool possession = trace.GoalTeam < 0 && ours && endGap < 500;
        // Beaten: the ball got past the defender (toward its goal) off our touch, and we are nearer to it.
        bool beaten = trace.GoalTeam < 0 && ours && ballPastDefender > 200 && endGap < opponentGap;
        bool good = goal || shot || possession || beaten;
        bool lost = !good && (trace.GoalTeam == 1 || !ours);
        trace.Metrics[$"{key}-good"] = good ? 1 : 0;
        trace.Metrics[$"{key}-flick"] = flicked ? 1 : 0;
        trace.Metrics["goal"] = goal ? 1 : 0;
        trace.Metrics["shot"] = shot ? 1 : 0;
        trace.Metrics["beaten"] = beaten ? 1 : 0;
        trace.Metrics["lost"] = lost ? 1 : 0;
        trace.Metrics["conceded"] = trace.GoalTeam == 1 ? 1 : 0;
        return good;
    }
}

/// <summary>
/// Plays full kickoff-to-minute games with the complete bot against an opponent build (or idle)
/// and records the share of time each action runs, and the share of each decision. It shows how
/// much a mechanic matters in real play, not whether it works.
/// </summary>
public sealed class ActionProfileDrill : Drill
{
    private readonly Dictionary<string, float> actionTime = new(), decisionTime = new();
    private float total;

    public override string Name => "profile";
    public override string Description => "Time share of each action and decision in one-minute games from kickoff.";
    public override int SeatCount => 2;
    public override IReadOnlyList<Criterion> Criteria => Array.Empty<Criterion>();

    public override EpisodeSetup Generate(Random r)
    {
        float Jitter() => Uniform(r, -10, 10);
        return new EpisodeSetup(V(0, 0, 93.15f), V(0, 0, 0), new[]
        {
            new CarSetup(V(-2048 + Jitter(), -2560 + Jitter(), 17), MathF.PI / 4, V(0, 0, 0), 33.3f),
            new CarSetup(V(2048 + Jitter(), 2560 + Jitter(), 17), MathF.PI / 4 + MathF.PI, V(0, 0, 0), 33.3f),
        }, 60f, Kickoff: true);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup) => Subject.Director = null;

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        string action = Subject.Action?.GetType().Name ?? "none";
        string decision = Subject.Decision ?? "none";
        actionTime[action] = actionTime.GetValueOrDefault(action) + SimArena.TickTime;
        decisionTime[decision] = decisionTime.GetValueOrDefault(decision) + SimArena.TickTime;
        total += SimArena.TickTime;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => false;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        // Cumulative shares so far: the last episode's metrics are the totals over all games.
        foreach (var (key, time) in actionTime) trace.Metrics["action:" + key] = time / total;
        foreach (var (key, time) in decisionTime.OrderByDescending(d => d.Value).Take(14))
            trace.Metrics["decision:" + key] = time / total;
        trace.Metrics["goals-for"] = trace.GoalTeam == 0 ? 1 : 0;
        return true;
    }
}
