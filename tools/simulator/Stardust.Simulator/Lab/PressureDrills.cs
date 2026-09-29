using System.Globalization;
using RLBot.Flat;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Full-policy regression fixtures for the two close-ball abandonments at 16.542 and 17.492 s
/// in the 2026-09-28 pid1928 telemetry. Positions are mirrored, perturbed, and optionally rotated
/// with the teams; no director forces a possession action or supplies a successful decision.
/// </summary>
public sealed class ClosePossessionDrill(int team = 0) : Drill
{
    private readonly ContactClock contacts = new();
    private float followTouch, retreatTime, closeTime, opponentTouch, followDeadline;
    private RsbVec initialBall;
    private int fixture;
    private float AttackSign => team == 0 ? 1f : -1f;

    public override string Name => team == 0 ? "close-possession" : "close-possession-orange";
    public override string Description => "Continue close, off-centre possession through catch/contact transitions without retreating.";
    public override int SeatCount => 4;
    public override int TeamOf(int seat) => seat is 0 or 2 ? team : 1 - team;
    public override IAgent? ScriptedOpponent(int seat) => seat == 1 ? new GroundChaseAgent() : new IdleAgent();
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.85), Criterion.Mean("follow-touch", 0.9),
        Criterion.Mean("conceded", 0, atLeast: false),
        Criterion.Median("progress", 200),
    };

    public override EpisodeSetup Generate(Random r)
    {
        fixture = r.Next(2);
        float mirror = r.Next(2) == 0 ? -1f : 1f;
        float rotation = Uniform(r, -0.06f, 0.06f);
        RsbVec pivot = fixture == 0 ? V(-1325.8f, -4045.9f, 17) : V(-2365.4f, -4255.1f, 17);
        RsbVec translation = V(Uniform(r, -35, 35), Uniform(r, -35, 35), 0);
        RsbVec Vector(RsbVec value)
        {
            float x = value.X * MathF.Cos(rotation) - value.Y * MathF.Sin(rotation);
            float y = value.X * MathF.Sin(rotation) + value.Y * MathF.Cos(rotation);
            return V(x * mirror * AttackSign, y * AttackSign, value.Z);
        }
        RsbVec Point(RsbVec value)
        {
            RsbVec shifted = Vector(value - pivot);
            return shifted + V((pivot.X + translation.X) * mirror * AttackSign,
                (pivot.Y + translation.Y) * AttackSign, pivot.Z);
        }
        CarSetup Transform(RsbVec p, RsbVec v, float yaw, float boost)
        {
            RsbVec facing = Vector(Heading(yaw));
            return new CarSetup(Point(p), MathF.Atan2(facing.Y, facing.X), Vector(v), boost);
        }

        RsbVec ball = fixture == 0 ? V(-1297.4f, -4004.7f, 148.1f) : V(-2306.3f, -4197.1f, 152);
        RsbVec velocity = fixture == 0 ? V(-1100.1f, -387.8f, 184.2f) : V(-925.8f, 277.8f, 55.1f);
        CarSetup subject = fixture == 0
            ? Transform(pivot, V(-1157.2f, -818.2f, 0), MathF.Atan2(-0.6f, -0.8f), 1)
            : Transform(pivot, V(-946.4f, 273.5f, 0), MathF.Atan2(0.3f, -1), 13);
        // The second logged challenger was jumping; this ground-only variant starts it farther
        // away so it remains a repeatable race, rather than manufacturing an airborne collision.
        CarSetup challenger = fixture == 0
            ? Transform(V(-1086.8f, -3327, 17), V(-1536.4f, -79.1f, 0), MathF.Atan2(-79.1f, -1536.4f), 8)
            : Transform(V(-2280, -3680, 17), V(-611.9f, -1100, 0), MathF.Atan2(-1100, -611.9f), 5);
        return new EpisodeSetup(Point(ball), Vector(velocity), new[]
        {
            subject, challenger,
            new CarSetup(V(2500 * mirror * AttackSign, -4800 * AttackSign, 17), AttackSign * MathF.PI / 2, V(0, 0, 0), 30),
            new CarSetup(V(-2500 * mirror * AttackSign, 4000 * AttackSign, 17), -AttackSign * MathF.PI / 2, V(0, 0, 0), 30),
        }, 3.5f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        contacts.Reset(session.Cars[0]);
        followTouch = opponentTouch = float.PositiveInfinity;
        followDeadline = 1.2f;
        retreatTime = closeTime = 0;
        initialBall = setup.BallPosition;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbCarState car = session.Cars[0];
        RsbVec ball = session.Ball.Physics.Position;
        bool touched = contacts.Step(car);
        if (trace.Elapsed < 0.12f && contacts.Contacts > 0)
        {
            // The first logged pose is touching the rear roof edge. Its native collision lofts
            // the ball, which cannot return to a grounded car in the original 1.2 s window.
            // Allow the measured untouched prediction's first descending roof-height crossing
            // plus one planning interval. This is bounded, and still must beat the challenger.
            float roofHeight = 17f + RoofRestHeight(session.Participants[0]) + 8f;
            foreach (RedUtils.BallSlice slice in RedUtils.Ball.Prediction.Slices ?? [])
            {
                float delay = slice.Time - RedUtils.Game.Time;
                if (delay < 0.05f || slice.Velocity.z >= 0 || slice.Location.z > roofHeight)
                    continue;
                followDeadline = MathF.Max(followDeadline, MathF.Min(2f, trace.Elapsed + delay + 0.12f));
                break;
            }
        }
        // Initial physical overlap or a passive impact at spawn is not a successful continuation.
        if (touched && trace.Elapsed >= 0.12f && !float.IsFinite(followTouch))
            followTouch = trace.Elapsed;
        if (session.TouchedThisTick && session.LastToucher is 1 or 3 && !float.IsFinite(opponentTouch))
            opponentTouch = trace.Elapsed;
        if ((ball - car.Physics.Position).Length < 650 &&
            (session.Ball.Physics.Velocity - car.Physics.Velocity).Length < 1100)
            closeTime += SimArena.TickTime;
        RsbVec separation = ball - car.Physics.Position;
        RsbVec relativeVelocity = session.Ball.Physics.Velocity - car.Physics.Velocity;
        if (trace.Elapsed <= 1f && car.Physics.Velocity.Y * AttackSign < -150 &&
            separation.Dot(relativeVelocity) > 150 * MathF.Max(1, separation.Length))
            retreatTime += SimArena.TickTime;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool followed = followTouch <= followDeadline && followTouch < opponentTouch;
        bool conceded = trace.GoalTeam == 1 - team;
        bool scored = trace.GoalTeam == team;
        float progress = (trace.FinalBallPosition.Y - initialBall.Y) * AttackSign;
        trace.Notes.Insert(0, $"logged-fixture-{fixture + 1}; team {team}");
        trace.Notes.Insert(1, string.Create(CultureInfo.InvariantCulture,
            $"follow {followTouch:F3}s deadline {followDeadline:F3}s opponent {opponentTouch:F3}s progress {progress:F0}; " +
            $"contacts {string.Join(",", trace.TouchLog.Select(t => $"{t.Time:F2}:{t.Toucher}"))}"));
        trace.Metrics["follow-touch"] = followed ? 1 : 0;
        // Failed contact times are right-censored at the episode duration, never dropped from p90.
        trace.Metrics["follow-touch-s"] = float.IsFinite(followTouch) ? followTouch : trace.Elapsed;
        trace.Metrics["ground-contact-deadline-s"] = followDeadline;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Metrics["own-goal"] = conceded && trace.TouchLog.LastOrDefault().Toucher == 0 && trace.Touches > 0 ? 1 : 0;
        trace.Metrics["progress"] = progress;
        trace.Metrics["close-control-s"] = closeTime;
        trace.Metrics["early-retreat-s"] = retreatTime;
        trace.Metrics[$"fixture-{fixture + 1}-follow-touch"] = followed ? 1 : 0;
        trace.Metrics[$"fixture-{fixture + 1}-progress"] = progress;
        return !conceded && followed && (scored || progress >= 200);
    }
}

/// <summary>
/// A covered first defender must contest a grounded carrier instead of holding a support gap.
/// A real physics contact and an upfield outcome are required; selecting an aggressive action
/// or surviving a short timeout alone cannot pass.
/// </summary>
public sealed class CarrierPressureDrill(int team = 0, bool fast = false, bool covered = true) : Drill
{
    private readonly ContactClock contacts = new();
    private float contactTime, retreatTime, exitSpeed, emergencyTime, counterTime;
    private RsbVec initialBall;
    private float AttackSign => team == 0 ? 1f : -1f;

    public override string Name => (fast ? $"fast-carrier-{(covered ? "covered" : "uncovered")}" : "carrier-pressure") +
        (team == 0 ? "" : "-orange");
    public override string Description => "Contest a grounded opponent carrier with goal cover, then produce a safe upfield touch.";
    public override int SeatCount => 4;
    public override int TeamOf(int seat) => seat is 0 or 2 ? team : 1 - team;
    public override IAgent? ScriptedOpponent(int seat) => seat == 1 ? new GroundCarrierAgent(fast ? 2200 : 1000) : new IdleAgent();
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.8), Criterion.Mean("contact-in-time", 0.9),
        Criterion.Mean("conceded", 0, atLeast: false),
        Criterion.Quantile("contact-s", 0.9, 1.5, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        float x = Uniform(r, -1000, 1000), y = fast ? Uniform(r, -700, 600) : Uniform(r, 600, 1700);
        float gap = covered ? Uniform(r, 800, 1250) : Uniform(r, 450, 750);
        float speed = fast ? Uniform(r, 1200, 1800) : Uniform(r, 350, 700);
        RsbVec Transform(RsbVec v) => V(v.X * AttackSign, v.Y * AttackSign, v.Z);
        float subjectYaw = MathF.PI / 2 + Uniform(r, -0.12f, 0.12f) + (team == 1 ? MathF.PI : 0);
        float attackerYaw = -MathF.PI / 2 + (team == 1 ? MathF.PI : 0);
        return new EpisodeSetup(Transform(V(x, y, 102)), Transform(V(0, -speed, 0)), new[]
        {
            new CarSetup(Transform(V(x + Uniform(r, -100, 100), y - gap, 17)), subjectYaw,
                Heading(subjectYaw) * Uniform(r, 300, 600), Uniform(r, 35, 80)),
            new CarSetup(Transform(V(x, y + 170, 17)), attackerYaw, Transform(V(0, -speed, 0)), 30),
            new CarSetup(Transform(covered ? V(x * 0.2f, -4400, 17) : V(3500, 2000, 17)), subjectYaw, V(0, 0, 0), 50),
            new CarSetup(Transform(V(3000, 4500, 17)), attackerYaw, V(0, 0, 0), 30),
        }, 4f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        contacts.Reset(session.Cars[0]);
        contactTime = float.PositiveInfinity;
        retreatTime = exitSpeed = emergencyTime = counterTime = 0;
        initialBall = setup.BallPosition;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        if (contacts.Step(session.Cars[0]) && !float.IsFinite(contactTime))
            contactTime = trace.Elapsed;
        if (trace.Elapsed >= contactTime && trace.Elapsed <= contactTime + 0.25f)
            exitSpeed = session.Ball.Physics.Velocity.Y * AttackSign;
        if (float.IsFinite(Subject.EmergencyThreatTime)) emergencyTime += SimArena.TickTime;
        else if (float.IsFinite(Subject.CounterThreatTime)) counterTime += SimArena.TickTime;
        RsbCarState car = session.Cars[0];
        RsbVec separation = session.Ball.Physics.Position - car.Physics.Position;
        RsbVec relativeVelocity = session.Ball.Physics.Velocity - car.Physics.Velocity;
        if (trace.Elapsed <= 1.5f && car.Physics.Velocity.Y * AttackSign < -150 &&
            separation.Dot(relativeVelocity) > 150 * MathF.Max(1, separation.Length))
            retreatTime += SimArena.TickTime;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool onTime = contactTime <= 1.5f;
        bool conceded = trace.GoalTeam == 1 - team;
        float progress = (trace.FinalBallPosition.Y - initialBall.Y) * AttackSign;
        trace.Metrics["contact-in-time"] = onTime ? 1 : 0;
        trace.Metrics["contact-s"] = float.IsFinite(contactTime) ? contactTime : trace.Elapsed;
        trace.Metrics["exit-upfield-speed"] = exitSpeed;
        trace.Metrics["progress"] = progress;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Metrics["own-goal"] = conceded && trace.TouchLog.LastOrDefault().Toucher == 0 && trace.Touches > 0 ? 1 : 0;
        trace.Metrics["early-retreat-s"] = retreatTime;
        trace.Metrics["emergency-s"] = emergencyTime;
        trace.Metrics["counter-threat-s"] = counterTime;
        return !conceded && onTime && (trace.GoalTeam == team || progress >= 200 || exitSpeed >= 500);
    }
}

/// <summary>A stateless ground challenger; episode resets cannot inherit an old jump timer.</summary>
internal sealed class GroundChaseAgent : ScriptedAgent
{
    public override string Description => "ground chaser";

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        PlayerInfoT car = packet.Players[self.Index];
        var ball = packet.Balls[0].Physics.Location;
        return new ControllerStateT { Throttle = 1, Steer = Flat.SteerToward(car, ball.X, ball.Y) };
    }
}

/// <summary>
/// Deterministic grounded nose carry. Controls are derived only from the ordinary packet; it
/// receives no state-setting assistance after spawn and deliberately has no jump or flick.
/// </summary>
internal sealed class GroundCarrierAgent(float maxSpeed = 1000) : ScriptedAgent
{
    public override string Description => "ground carrier";

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        PlayerInfoT car = packet.Players[self.Index];
        var ball = packet.Balls[0].Physics;
        var (fx, fy, _) = Flat.Forward(car.Physics.Rotation);
        float dx = ball.Location.X - car.Physics.Location.X;
        float dy = ball.Location.Y - car.Physics.Location.Y;
        float forwardDistance = dx * fx + dy * fy;
        float ballSpeed = ball.Velocity.X * fx + ball.Velocity.Y * fy;
        float targetSpeed = Math.Clamp(ballSpeed + (forwardDistance - 155f) * 3f, 250f, maxSpeed);
        return new ControllerStateT
        {
            Steer = Flat.SteerToward(car, ball.Location.X, ball.Location.Y),
            Throttle = Math.Clamp((targetSpeed - Flat.ForwardSpeed(car)) / 250f, -1f, 1f),
        };
    }
}
