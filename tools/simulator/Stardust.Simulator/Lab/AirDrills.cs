using Bot;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Air dribble: the car is already flying with the ball resting on its nose (as right after a
/// hood pop). Measures how long the carry keeps the ball within reach while both are airborne,
/// the progress toward the opponent goal, and boost spent. No opponent.
/// </summary>
public sealed class AirDribbleDrill : Drill
{
    private readonly ContactClock contacts = new();
    private float lostAt, fuelStart, fuelEnd, startY;
    private RsbVec endBall;

    public override string Name => "air-dribble";
    public override string Description => "Keep a nose-carried ball within reach in the air and carry it toward goal.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Median("carry-s", 2.0), Criterion.Median("progress-per-s", 600),
    };

    public override EpisodeSetup Generate(Random r)
    {
        float pitch = Uniform(r, 0.7f, 1.0f);
        float yaw = MathF.PI / 2 + Uniform(r, -0.3f, 0.3f);
        var car = V(Uniform(r, -1500, 1500), Uniform(r, -2500, -500), Uniform(r, 350, 600));
        var velocity = V(MathF.Cos(yaw) * Uniform(r, 600, 900), MathF.Sin(yaw) * Uniform(r, 600, 900), Uniform(r, 200, 400));
        RsbVec forward = new(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        RsbVec ball = car + forward * 172f + V(0, 0, 10);
        RsbVec ballVelocity = velocity + V(0, Uniform(r, 0, 60), Uniform(r, 30, 90));
        return new EpisodeSetup(ball, ballVelocity, new[]
        {
            new CarSetup(car, yaw, velocity, 100, Pitch: pitch, OnGround: false, HasJumped: true, HasDoubleJumped: true,
                AirTimeSinceJump: 0.6f),
        }, 4f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        lostAt = float.NaN;
        fuelStart = setup.Cars[0].Boost;
        startY = setup.BallPosition.Y;
        bool installed = false;
        Subject.Director = bot =>
        {
            if (installed) return;
            bot.Action = new AerialCarry();
            installed = true;
        };
        contacts.Reset(session.Cars[0]);
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbCarState car = session.Cars[0];
        contacts.Step(car);
        RsbVec ball = session.Ball.Physics.Position;
        bool airborne = car.IsOnGround == 0 && ball.Z > 200;
        if (float.IsNaN(lostAt) && (!airborne || ball.Distance(car.Physics.Position) > 260))
            lostAt = trace.Elapsed;
        endBall = ball;
        fuelEnd = car.Boost;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => !float.IsNaN(lostAt) || trace.GoalTeam >= 0;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        float carry = float.IsNaN(lostAt) ? trace.Elapsed : lostAt;
        trace.Metrics["carry-s"] = carry;
        trace.Metrics["touches-per-s"] = contacts.Contacts / MathF.Max(carry, 0.1f);
        trace.Metrics["progress-per-s"] = (endBall.Y - startY) / MathF.Max(carry, 0.1f);
        trace.Metrics["boost-per-s"] = (fuelStart - fuelEnd) / MathF.Max(carry, 0.1f);
        trace.Metrics["goal"] = trace.GoalTeam == 0 ? 1 : 0;
        return carry >= 2f || trace.GoalTeam == 0;
    }
}

/// <summary>
/// Flip reset from below a high ball, flip already spent (after a double jump, or a single jump
/// whose flip timed out). Ground truth comes from RocketSim: the wheels settle on the ball (the car
/// reads as on the ground high in the air), then the car is airborne again with its jump restored.
/// Also measured: whether the bot confirms the reset from its packet flags, chassis touches that
/// knock the ball before the reset, and whether the flip is then used on the ball.
/// </summary>
public sealed class FlipResetDrill : Drill
{
    private bool singleJump, onBall, acquired, confirmed, dodged, used;
    private int chassisTouches;
    private float acquiredAt, dodgedAt;
    private readonly ContactClock contacts = new();

    public override string Name => "flip-reset";
    public override string Description => "Get the wheels onto a high ball to regain the flip, confirm it, and use it on the ball.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Mean("acquired", 0.6), Criterion.Mean("confirmed-when-acquired", 0.9), Criterion.Mean("used-when-acquired", 0.6),
    };

    public override EpisodeSetup Generate(Random r)
    {
        singleJump = r.NextDouble() < 0.5;
        float J(float s) => Uniform(r, -s, s);
        var car = V(J(40), -180 + J(40), 930 + J(40));
        var velocity = V(J(80), 380 + J(80), 330 + J(80));
        var ball = V(J(40), J(40), 1150 + J(40));
        var ballVelocity = V(J(80), 350 + J(80), 250 + J(80));
        return new EpisodeSetup(ball, ballVelocity, new[]
        {
            new CarSetup(car, MathF.PI / 2, velocity, 60, Pitch: 0.61f, OnGround: false, HasJumped: true,
                HasDoubleJumped: !singleJump, AirTimeSinceJump: 1.5f),
        }, 2.5f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        onBall = acquired = confirmed = dodged = used = false;
        chassisTouches = 0;
        acquiredAt = dodgedAt = float.NaN;
        contacts.Reset(session.Cars[0]);
        bool installed = false;
        Subject.Director = bot =>
        {
            if (!installed)
            {
                bot.Action = new FlipReset(bot.Jump);
                installed = true;
            }
            confirmed |= bot.Action is FlipReset reset && reset.Confirmed;
        };
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbCarState car = session.Cars[0];
        bool contact = contacts.Step(car);
        float gap = car.Physics.Position.Distance(session.Ball.Physics.Position);
        if (!acquired && contact) chassisTouches++;
        // Wheels resting on the ball: the game counts the car as grounded high above the floor.
        if (car.IsOnGround != 0 && car.Physics.Position.Z > 250 && gap < 280) onBall = true;
        if (onBall && !acquired && car.IsOnGround == 0 && car.HasJumped == 0 && car.HasDoubleJumped == 0 && car.HasFlipped == 0)
        {
            acquired = true;
            acquiredAt = trace.Elapsed;
        }
        if (acquired && !dodged && car.HasFlipped != 0)
        {
            dodged = true;
            dodgedAt = trace.Elapsed;
        }
        if (dodged && contact && trace.Elapsed - dodgedAt < 0.35f) used = true;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        trace.GoalTeam >= 0 || (used && trace.Elapsed - dodgedAt > 0.3f) || session.Cars[0].Physics.Position.Z < 60;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Notes.Insert(0, singleJump ? "single-jump" : "double-jump");
        trace.Metrics["acquired"] = acquired ? 1 : 0;
        trace.Metrics[singleJump ? "acquired-single" : "acquired-double"] = acquired ? 1 : 0;
        trace.Metrics["confirmed-when-acquired"] = acquired ? (confirmed ? 1 : 0) : double.NaN;
        trace.Metrics["used-when-acquired"] = acquired ? (used ? 1 : 0) : double.NaN;
        trace.Metrics["chassis-touches-before"] = chassisTouches;
        trace.Metrics["reset-s"] = acquiredAt;
        return acquired && used;
    }
}
