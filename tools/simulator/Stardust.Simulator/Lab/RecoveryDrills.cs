using System.Globalization;
using Bot;
using RedUtils.Math;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Native controller regression for the sustained reverse retreat in candidate-vs-Nexto game 0
/// at 71.61–74.84 s. The director supplies a moving retreat waypoint, while the real action,
/// packet adapter and native car physics execute it. This isolates recovery, not match policy.
/// </summary>
public sealed class ReverseRecoveryDrill(int team = 0, string mode = "retreat") : Drill
{
    private DefensiveDrive? drive;
    private RsbVec origin, route, ballStart, endPosition;
    private float started, reverseTime, facedAt, maximumSpeed, finalError, ballGoalward;
    private bool jumped, boostedForward;
    private float sign => team == 0 ? 1 : -1;

    public override string Name => $"reverse-{mode}" + (team == 0 ? "" : "-orange");
    public override string Description => mode switch
    {
        "net" => "Keep short goal-mouth reverse corrections grounded and precise.",
        "slow" => "Preserve a sustainable goal-facing reverse shadow without an unnecessary reorientation.",
        "near-ball" => "Brake out of a long reverse command without flipping through a nearby own-goal corridor.",
        _ => "Turn a sustained reverse retreat into forward travel with usable boost.",
    };
    public override int TeamOf(int seat) => team;
    public override IReadOnlyList<Criterion> Criteria => mode == "retreat" ? new[]
    {
        Criterion.Rate(0.9), Criterion.Quantile("faced-s", 0.9, 2.0, atLeast: false),
        Criterion.Mean("forward-boost", 0.9), Criterion.Median("peak-forward-speed", 1800),
        Criterion.Mean("conceded", 0, atLeast: false),
    } : new[]
    {
        Criterion.Rate(0.95), Criterion.Mean("jumped", 0, atLeast: false),
        Criterion.Mean("conceded", 0, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        float x = Uniform(r, -1500, 1500), y = Uniform(r, 1050, 1500);
        float yaw = MathF.PI / 2 + Uniform(r, -0.18f, 0.18f) + (team == 1 ? MathF.PI : 0);
        float speed = Uniform(r, 1000, 1320), boost = Uniform(r, 29, 55);
        float limit = 3.5f;
        if (mode == "net")
        {
            x = Uniform(r, -350, 350); y = -4650;
            speed = Uniform(r, 100, 250); yaw = sign * MathF.PI / 2; limit = 1.5f;
        }
        else if (mode == "near-ball")
        {
            x = Uniform(r, -180, 180); y = -3500;
            yaw = sign * MathF.PI / 2; limit = 0.7f;
        }
        origin = V(x * sign, y * sign, 17);
        route = Heading(yaw) * -1;
        RsbVec ball = mode == "near-ball"
            ? origin + route * Uniform(r, 500, 650) + V(0, 0, 76)
            : origin - route * 1900 + V(500 * sign, 0, 76);
        return new EpisodeSetup(ball, mode is "retreat" or "slow" ? route * (mode == "slow" ? 1200 : 1300) : V(0, 0, 0),
            new[] { new CarSetup(origin, yaw, route * speed, boost) }, limit);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        drive = null;
        started = session.Time;
        ballStart = setup.BallPosition;
        reverseTime = maximumSpeed = finalError = ballGoalward = 0;
        facedAt = float.PositiveInfinity;
        jumped = boostedForward = false;
        Subject.Director = bot =>
        {
            float elapsed = RedUtils.Game.Time - started;
            RsbVec target = mode == "net" ? origin + route * 230 :
                origin + route * (mode == "near-ball" ? 1600 : 1000 + (mode == "slow" ? 1200 : 1900) * elapsed);
            target.Y = sign * MathF.Max(target.Y * sign, -4900);
            var destination = new Vec3(target.X, target.Y, 17);
            drive ??= new DefensiveDrive(bot.Me, destination, mode == "net" ? 1700 : 2250,
                mode == "net" ? 0 : 1350, holdPosition: mode == "net");
            drive.Target = destination;
            bot.Action = drive;
        };
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbCarState car = session.Cars[0];
        endPosition = car.Physics.Position;
        var controls = session.Participants[0].LastInput;
        float forward = car.Physics.Velocity.Dot(car.Physics.Forward);
        float aligned = car.Physics.Forward.Dot(route);
        if (forward < -500) reverseTime += SimArena.TickTime;
        if (aligned > 0.8f && forward > 800 && car.IsOnGround != 0 && !float.IsFinite(facedAt))
            facedAt = trace.Elapsed;
        if (aligned > 0.8f && forward > 0)
        {
            maximumSpeed = MathF.Max(maximumSpeed, forward);
            boostedForward |= controls.Boost;
        }
        jumped |= controls.Jump;
        if (drive != null)
            finalError = car.Physics.Position.Distance(V(drive.Target.x, drive.Target.y, 17));
        ballGoalward = MathF.Max(ballGoalward, -(session.Ball.Physics.Position.Y - ballStart.Y) * sign);
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool conceded = trace.GoalTeam == 1 - team;
        float progress = (endPosition - origin).Dot(route);
        trace.Metrics["faced-s"] = float.IsFinite(facedAt) ? facedAt : trace.Elapsed;
        trace.Metrics["reverse-s"] = reverseTime;
        trace.Metrics["forward-boost"] = boostedForward ? 1 : 0;
        trace.Metrics["peak-forward-speed"] = maximumSpeed;
        trace.Metrics["jumped"] = jumped ? 1 : 0;
        trace.Metrics["final-target-error"] = finalError;
        trace.Metrics["ball-goalward"] = ballGoalward;
        trace.Metrics["retreat-progress"] = progress;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Notes.Insert(0, string.Create(CultureInfo.InvariantCulture,
            $"{mode} team {team}, faced {facedAt:F2}, reverse {reverseTime:F2}, peak {maximumSpeed:F0}"));
        return !conceded && (mode == "retreat"
            ? facedAt <= 2 && boostedForward && maximumSpeed >= 1800 && progress >= 3000
            : !jumped && (mode == "net" ? finalError <= 100 : mode == "slow"
                ? reverseTime > trace.Elapsed * 0.9f && finalError < 1150 : ballGoalward < 150));
    }
}
