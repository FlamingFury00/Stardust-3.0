using Bot;
using RedUtils;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>Contact bookkeeping for one car: ticks since it last touched the ball.</summary>
internal sealed class ContactClock
{
    private ulong lastHit = ulong.MaxValue;
    public int SinceContact { get; private set; } = int.MaxValue;
    public int Contacts { get; private set; }

    public void Reset(in RsbCarState car)
    {
        lastHit = car.LastHitTick;
        SinceContact = int.MaxValue;
        Contacts = 0;
    }

    /// <summary>Returns true on ticks where the car touched the ball.</summary>
    public bool Step(in RsbCarState car)
    {
        bool contact = car.LastHitTick != ulong.MaxValue && car.LastHitTick != lastHit;
        lastHit = car.LastHitTick;
        if (contact)
        {
            Contacts++;
            SinceContact = 0;
        }
        else if (SinceContact != int.MaxValue)
            SinceContact++;
        return contact;
    }
}

/// <summary>Shared setup for drills that start with the ball balanced on a moving car's roof.</summary>
public abstract class RoofDrill : Drill
{
    /// <summary>Ball centre height above the Octane's origin when resting on its roof (offset 20.755 + half height 19.33 + radius).</summary>
    protected const float OctaneRoofRest = 131.34f;

    protected static EpisodeSetup CarryStart(RsbVec car, float yaw, float speed, float ballForward, float ballRight,
        float boost, float timeLimit)
    {
        RsbVec forward = Heading(yaw), right = new(-MathF.Sin(yaw), MathF.Cos(yaw), 0);
        RsbVec velocity = forward * speed;
        RsbVec ball = car + forward * ballForward + right * ballRight + new RsbVec(0, 0, OctaneRoofRest + 0.5f);
        return new EpisodeSetup(ball, velocity, new[] { new CarSetup(car, yaw, velocity, boost) }, timeLimit);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Participant p = session.Participants[0];
        if (MathF.Abs(RoofRestHeight(p) - OctaneRoofRest) > 0.5f)
            throw new InvalidOperationException($"Roof drills assume the Octane hitbox; got rest height {RoofRestHeight(p):F2}.");
    }
}

/// <summary>
/// Hood carry: the ball starts balanced on the roof at matched speed (0–1500 uu/s) with the car up to
/// 46° off the attacking lane. Pros hold such a carry indefinitely while turning onto the lane.
/// </summary>
public sealed class CarryDrill : RoofDrill
{
    private const float HoldTarget = 4f;
    private float lostAt, boostAtStart, fuelLeft;
    private RsbVec endBall, endVelocity;

    public override string Name => "carry";
    public override string Description => "Keep a balanced ball on the roof for 4 s while turning onto the attacking lane.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.9), Criterion.Median("hold-s", HoldTarget), Criterion.Quantile("lane-error-deg", 0.9, 5, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        var car = V(Uniform(r, -2500, 2500), Uniform(r, -4200, -1800), 17.01f);
        float yaw = MathF.Atan2(5120 - car.Y, -car.X) + Uniform(r, -0.8f, 0.8f);
        return CarryStart(car, yaw, Uniform(r, 0, 1500), Uniform(r, -5, 35), Uniform(r, -20, 20),
            Uniform(r, 20, 100), HoldTarget + 0.05f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        lostAt = float.NaN;
        boostAtStart = setup.Cars[0].Boost;
        bool installed = false;
        Subject.Director = bot =>
        {
            if (installed) return;
            bot.Action = new GroundDribble();
            installed = true;
        };
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        if (float.IsNaN(lostAt) && !OnRoof(session, 0))
            lostAt = trace.Elapsed;
        endBall = session.Ball.Physics.Position;
        endVelocity = session.Ball.Physics.Velocity;
        fuelLeft = session.Cars[0].Boost;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => !float.IsNaN(lostAt) || trace.GoalTeam >= 0;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        float hold = float.IsNaN(lostAt) ? trace.Elapsed : lostAt;
        trace.Metrics["hold-s"] = MathF.Min(hold, HoldTarget);
        if (setup.Cars[0].Velocity.Length >= 300) trace.Metrics["rolling-hold"] = hold >= HoldTarget ? 1 : 0;
        trace.Metrics["boost-used"] = boostAtStart - fuelLeft;
        // Heading of the carried ball against the line from the ball to the opponent goal's centre.
        if (hold >= HoldTarget && endVelocity.FlatLength > 200)
            trace.Metrics["lane-error-deg"] = FlatAngle(endVelocity, V(0, 5120, 0) - endBall);
        trace.Metrics["end-speed"] = endVelocity.FlatLength;
        return hold >= HoldTarget;
    }
}

/// <summary>
/// Flick from a settled carry toward the goal: exit speed, direction and elevation of the ball after
/// the car's last contact. A pro power flick leaves at 2500+ uu/s within a few degrees of its aim.
/// </summary>
public sealed class FlickDrill : RoofDrill
{
    private const float Settle = 0.5f;
    private readonly ContactClock contacts = new();
    private bool flicked, unsettled;
    private float flickAt, carSpeedAtFlick;
    private RsbVec lane, exitVelocity, ballAtExit;
    private bool haveExit;

    public override string Name => "flick";
    public override string Description => "Flick a settled carry toward the far goal: exit speed, aim error and elevation.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.8), Criterion.Median("exit-speed", 2500), Criterion.Median("aim-error-deg", 6, atLeast: false),
        Criterion.Quantile("aim-error-deg", 0.9, 15, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        var car = V(Uniform(r, -1800, 1800), Uniform(r, -1500, 1200), 17.01f);
        float yaw = MathF.Atan2(5120 - car.Y, -car.X) + Uniform(r, -0.3f, 0.3f);
        return CarryStart(car, yaw, Uniform(r, 600, 1400), Uniform(r, 0, 25), Uniform(r, -15, 15),
            Uniform(r, 30, 100), Settle + 1.6f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        flicked = unsettled = haveExit = false;
        flickAt = float.NaN;
        bool installed = false;
        float started = float.NaN;
        Subject.Director = bot =>
        {
            if (!installed)
            {
                bot.Action = new GroundDribble();
                installed = true;
                started = Game.Time;
                return;
            }
            if (flicked || unsettled || Game.Time - started < Settle) return;
            if (!PossessionControl.HasControlledPossession(bot.Me, RedUtils.Ball.MainBall))
            {
                unsettled = true;
                return;
            }
            RedUtils.Math.Vec3 aim = (bot.TheirGoal.Location - RedUtils.Ball.Location).FlatNorm();
            bot.Action = new ControlledFlick(bot.Me, aim);
            lane = new RsbVec(aim.x, aim.y, 0);
            flicked = true;
        };
        contacts.Reset(session.Cars[0]);
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbCarState car = session.Cars[0];
        bool contact = contacts.Step(car);
        if (!flicked) return;
        if (float.IsNaN(flickAt))
        {
            flickAt = trace.Elapsed;
            carSpeedAtFlick = car.Physics.Velocity.Length;
            contacts.Reset(car);
            return;
        }
        if (!contact && contacts.SinceContact == 2)
        {
            exitVelocity = session.Ball.Physics.Velocity;
            ballAtExit = session.Ball.Physics.Position;
            haveExit = true;
        }
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        unsettled || trace.GoalTeam >= 0 ||
        (!float.IsNaN(flickAt) && trace.Elapsed - flickAt > 1.2f) ||
        (haveExit && contacts.SinceContact > 36);

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["unsettled"] = unsettled ? 1 : 0;
        if (!haveExit) return false;
        float speed = exitVelocity.Length;
        float aimError = FlatAngle(exitVelocity, lane);
        float elevation = Degrees(MathF.Atan2(exitVelocity.Z, exitVelocity.FlatLength));
        trace.Metrics["exit-speed"] = speed;
        trace.Metrics["gain"] = speed - carSpeedAtFlick;
        trace.Metrics["aim-error-deg"] = aimError;
        trace.Metrics["elevation-deg"] = elevation;
        // On target if the flat path crosses the goal line between the posts.
        bool onTarget = exitVelocity.Y > 0 &&
            MathF.Abs(ballAtExit.X + exitVelocity.X / exitVelocity.Y * (5120 - ballAtExit.Y)) < 800;
        trace.Metrics["on-target"] = onTarget ? 1 : 0;
        return speed >= 2000 && aimError <= 10;
    }
}

/// <summary>
/// Cushion catch of a dropping ball, uncontested: the car starts 200–1100 uu from where the ball
/// first comes down to roof height, with time to get there. Success is the ball settled on the roof
/// for a second. Directed like the decision layer when unopposed: catch, then carry.
/// </summary>
public sealed class CatchDrill : RoofDrill
{
    private const float RoofContactZ = 148.3f;
    private readonly ContactClock contacts = new();
    private float roofSince, settledAt, contactSlip;

    public override string Name => "catch";
    public override string Description => "Catch a dropping ball on the roof and settle it for 1 s.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.8), Criterion.Median("contact-slip", 150, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        while (true)
        {
            var ball = V(Uniform(r, -2500, 2500), Uniform(r, -3000, 2000), Uniform(r, 350, 1100));
            float horizontal = new[] { 0f, 400f, 800f, 1200f }[r.Next(4)];
            float direction = MathF.PI / 2 + Uniform(r, -MathF.PI / 3, MathF.PI / 3);
            float vz = Uniform(r, -150, 350);
            RsbVec velocity = Heading(direction) * horizontal + V(0, 0, vz);
            // First descent through roof-contact height, ignoring drag.
            float drop = (vz + MathF.Sqrt(vz * vz + 4 * 325 * (ball.Z - RoofContactZ))) / 650f;
            if (drop < 0.7f) continue;
            RsbVec landing = ball + Heading(direction) * (horizontal * drop);
            landing.Z = 17.01f;
            if (MathF.Abs(landing.X) > 3500 || MathF.Abs(landing.Y) > 4500) continue;
            float distance = Uniform(r, 200, MathF.Min(1100, 900 * drop));
            float around = Uniform(r, -MathF.PI, MathF.PI);
            RsbVec car = landing + Heading(around) * distance;
            if (MathF.Abs(car.X) > 3900 || MathF.Abs(car.Y) > 4900) continue;
            float yaw = (horizontal > 0 ? direction : Uniform(r, -MathF.PI, MathF.PI)) + Uniform(r, -MathF.PI / 2, MathF.PI / 2);
            RsbVec carVelocity = Heading(yaw) * Uniform(r, 0, 1000);
            float boost = r.NextDouble() < 0.5 ? 0 : 60;
            return new EpisodeSetup(ball, velocity, new[] { new CarSetup(car, yaw, carVelocity, boost) }, drop + 2.5f);
        }
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        roofSince = settledAt = contactSlip = float.NaN;
        contacts.Reset(session.Cars[0]);
        Subject.Director = bot =>
        {
            if (bot.Action != null) return;
            if (GroundDribble.CanStart(bot.Me, RedUtils.Ball.MainBall, 1))
                bot.Action = new GroundDribble();
            else if (GroundCatch.FindCatch(bot.Me) != null)
                bot.Action = new GroundCatch();
        };
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbCarState car = session.Cars[0];
        bool contact = contacts.Step(car);
        if (contact && float.IsNaN(contactSlip) && Local(car.Physics, session.Ball.Physics.Position).Z > 100)
        {
            RsbVec slip = session.Ball.Physics.Velocity - car.Physics.Velocity;
            contactSlip = slip.FlatLength;
        }
        bool resting = OnRoof(session, 0) && car.IsOnGround != 0;
        if (!resting) roofSince = float.NaN;
        else if (float.IsNaN(roofSince)) roofSince = trace.Elapsed;
        if (float.IsNaN(settledAt) && !float.IsNaN(roofSince) && trace.Elapsed - roofSince >= 1f)
            settledAt = trace.Elapsed;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => !float.IsNaN(settledAt) || trace.GoalTeam >= 0;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["contact-slip"] = contactSlip;
        trace.Metrics["settle-s"] = settledAt;
        trace.Metrics["no-boost"] = setup.Cars[0].Boost == 0 ? 1 : 0;
        return !float.IsNaN(settledAt);
    }
}

/// <summary>
/// Ground pickup: a ball rolling on the floor, either ahead of the car in its direction or toward it.
/// Success is the ball carried on the roof for half a second.
/// </summary>
public sealed class PickupDrill : RoofDrill
{
    private float roofSince, pickedAt;

    public override string Name => "pickup";
    public override string Description => "Lift a rolling ball onto the roof and carry it for 0.5 s.";
    public override IReadOnlyList<Criterion> Criteria => new[] { Criterion.Rate(0.6) };

    public override EpisodeSetup Generate(Random r)
    {
        float yaw = MathF.PI / 2 + Uniform(r, -0.5f, 0.5f);
        var car = V(Uniform(r, -2000, 2000), Uniform(r, -4000, -1500), 17.01f);
        float ballSpeed = Uniform(r, 0, 1000);
        bool toward = r.NextDouble() < 0.5;
        float gap = toward ? Uniform(r, 900, 1600) : Uniform(r, 250, 500);
        RsbVec ball = car + Heading(yaw) * gap;
        ball.Z = 93.15f;
        RsbVec ballVelocity = Heading(yaw) * (toward ? -ballSpeed : ballSpeed);
        float carSpeed = toward ? Uniform(r, 0, 800) : ballSpeed + Uniform(r, 0, 300);
        // Rolling without slipping: spin about the axis perpendicular to travel.
        RsbVec spin = new RsbVec(-ballVelocity.Y, ballVelocity.X, 0) * (1f / SimArena.BallRadius);
        return new EpisodeSetup(ball, ballVelocity,
            new[] { new CarSetup(car, yaw, Heading(yaw) * carSpeed, Uniform(r, 30, 100)) }, 4.5f, spin);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        roofSince = pickedAt = float.NaN;
        Subject.Director = bot =>
        {
            if (bot.Action == null && GroundDribble.CanStart(bot.Me, RedUtils.Ball.MainBall, 1))
                bot.Action = new GroundDribble();
        };
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        bool resting = OnRoof(session, 0) && session.Cars[0].IsOnGround != 0;
        if (!resting) roofSince = float.NaN;
        else if (float.IsNaN(roofSince)) roofSince = trace.Elapsed;
        if (float.IsNaN(pickedAt) && !float.IsNaN(roofSince) && trace.Elapsed - roofSince >= 0.5f)
            pickedAt = trace.Elapsed;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => !float.IsNaN(pickedAt) || trace.GoalTeam >= 0;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["pickup-s"] = pickedAt;
        return !float.IsNaN(pickedAt);
    }
}
