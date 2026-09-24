using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;

namespace Stardust.Simulator.Scenarios;

/// <summary>Kickoff from one of the five Soccar spawns against the opponent build.</summary>
public sealed class KickoffScenario(int spawn) : Scenario
{
    private static readonly (float X, float Y, float Yaw)[] Spawns =
    {
        (-2048, -2560, MathF.PI / 4), (2048, -2560, 3 * MathF.PI / 4),
        (-256, -3840, MathF.PI / 2), (256, -3840, MathF.PI / 2), (0, -4608, MathF.PI / 2),
    };

    public override string Name => $"kickoff-{new[] { "diag-left", "diag-right", "offc-left", "offc-right", "back" }[spawn]}";
    public override string Description => "Speed to the ball and possession after a standard 1v1 kickoff.";
    public override int SeatCount => 2;

    public override EpisodeSetup Generate(Random random)
    {
        var (x, y, yaw) = Spawns[spawn];
        return new EpisodeSetup(V(0, 0, 93.15f), V(0, 0, 0),
            new[]
            {
                new CarSetup(V(x, y, 17), yaw, V(0, 0, 0), 33.3f),
                new CarSetup(V(-x, -y, 17), yaw + MathF.PI, V(0, 0, 0), 33.3f),
            }, 6f, Kickoff: true);
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        trace.GoalTeam >= 0 || (!float.IsNaN(trace.FirstTouchTime) && trace.Elapsed - trace.FirstTouchTime > 3f);

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["first-touch-s"] = trace.FirstToucher is 0 or -2 ? trace.FirstTouchTime : double.NaN;
        trace.Metrics["opponent-first"] = trace.FirstToucher == 1 ? 1 : 0;
        trace.Metrics["simultaneous"] = trace.FirstToucher == -2 ? 1 : 0;
        trace.Metrics["ball-y-3s"] = trace.GoalTeam < 0 ? trace.FinalBallPosition.Y : double.NaN;
        trace.Metrics["conceded"] = trace.GoalTeam == 1 ? 1 : 0;
        // Won: scored, or the ball sits in the opponent half three seconds after contact (blue attacks +y).
        return trace.GoalTeam == 0 || (trace.GoalTeam < 0 && trace.FinalBallPosition.Y > 0);
    }
}

/// <summary>Unopposed finishing from varied ball states in the attacking half.</summary>
public sealed class OpenNetScenario : Scenario
{
    public override string Name => "open-net";
    public override string Description => "Score into an empty net from rolling/bouncing balls in the attacking half.";

    public override EpisodeSetup Generate(Random r)
    {
        float bx = Uniform(r, -3000, 3000), by = Uniform(r, -500, 3800);
        bool air = r.NextDouble() < 0.4;
        var ball = V(bx, by, air ? Uniform(r, 150, 700) : 93.15f);
        var ballVel = V(Uniform(r, -600, 600), Uniform(r, -800, 600), air ? Uniform(r, -300, 500) : 0);
        float angle = Uniform(r, 0, 2 * MathF.PI);
        float distance = Uniform(r, 1200, 3000);
        var car = V(Math.Clamp(bx + MathF.Cos(angle) * distance, -3800, 3800),
            Math.Clamp(by - MathF.Abs(MathF.Sin(angle)) * distance - 500, -4800, 4500), 17);
        float yaw = Uniform(r, -MathF.PI, MathF.PI);
        float speed = Uniform(r, 0, 1400);
        return new EpisodeSetup(ball, ballVel,
            new[] { new CarSetup(car, yaw, V(MathF.Cos(yaw) * speed, MathF.Sin(yaw) * speed, 0), Uniform(r, 20, 100)) }, 8f);
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["goal-s"] = trace.GoalTeam == 0 ? trace.GoalTime : double.NaN;
        trace.Metrics["touches"] = trace.Touches;
        return trace.GoalTeam == 0;
    }
}

/// <summary>Finishing against a disciplined scripted goalkeeper.</summary>
public sealed class KeeperScenario : Scenario
{
    public override string Name => "vs-keeper";
    public override string Description => "Score against a scripted keeper that holds the line and jumps at shots.";
    public override int SeatCount => 2;
    public override IAgent? ScriptedOpponent(int seat) => new GoalieAgent();

    public override EpisodeSetup Generate(Random r)
    {
        float bx = Uniform(r, -2500, 2500), by = Uniform(r, 0, 3000);
        var ball = V(bx, by, 93.15f);
        var ballVel = V(Uniform(r, -500, 500), Uniform(r, -300, 700), Uniform(r, 0, 400));
        var car = V(Math.Clamp(bx + Uniform(r, -1500, 1500), -3800, 3800), by - Uniform(r, 1200, 2500), 17);
        float yaw = MathF.PI / 2 + Uniform(r, -1.2f, 1.2f);
        float speed = Uniform(r, 300, 1500);
        return new EpisodeSetup(ball, ballVel, new[]
        {
            new CarSetup(car, yaw, V(MathF.Cos(yaw) * speed, MathF.Sin(yaw) * speed, 0), Uniform(r, 30, 100)),
            new CarSetup(V(0, 5000, 17), 0, V(0, 0, 0), 100),
        }, 8f);
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["goal-s"] = trace.GoalTeam == 0 ? trace.GoalTime : double.NaN;
        return trace.GoalTeam == 0;
    }
}

/// <summary>High balls: reach them in the air and redirect them toward the attacking goal.</summary>
public sealed class AerialScenario : Scenario
{
    public override string Name => "aerial";
    public override string Description => "Meet a lofted ball in the air (z > 500) and send it goalward.";

    public override EpisodeSetup Generate(Random r)
    {
        float bx = Uniform(r, -2500, 2500), by = Uniform(r, -1500, 2500);
        float peak = Uniform(r, 700, 1700);
        float vz = MathF.Sqrt(2 * 650 * (peak - 100));
        var ball = V(bx, by, 100);
        var ballVel = V(Uniform(r, -400, 400), Uniform(r, -400, 400), vz);
        float angle = Uniform(r, -MathF.PI, 0);
        float distance = Uniform(r, 1400, 3200);
        var car = V(Math.Clamp(bx + MathF.Cos(angle) * distance, -3800, 3800),
            Math.Clamp(by + MathF.Sin(angle) * distance, -4900, 4900), 17);
        float yaw = MathF.Atan2(by - car.Y, bx - car.X) + Uniform(r, -0.6f, 0.6f);
        float speed = Uniform(r, 500, 1600);
        return new EpisodeSetup(ball, ballVel,
            new[] { new CarSetup(car, yaw, V(MathF.Cos(yaw) * speed, MathF.Sin(yaw) * speed, 0), Uniform(r, 60, 100)) }, 5f);
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        trace.GoalTeam >= 0 || (!float.IsNaN(trace.FirstTouchTime) && trace.Elapsed - trace.FirstTouchTime > 0.3f);

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool aerialTouch = !float.IsNaN(trace.FirstTouchTime) && trace.BallPositionAtFirstTouch.Z > 500;
        RsbVec v = trace.BallVelocityAfterFirstTouch;
        bool goalward = aerialTouch && v.Y > 300;
        trace.Metrics["touch-s"] = aerialTouch ? trace.FirstTouchTime : double.NaN;
        trace.Metrics["touch-z"] = aerialTouch ? trace.BallPositionAtFirstTouch.Z : double.NaN;
        trace.Metrics["aerial-touch"] = aerialTouch ? 1 : 0;
        return goalward;
    }
}

/// <summary>Shots at our goal from varied angles, heights, and speeds with varied defender starts.</summary>
public sealed class SaveScenario : Scenario
{
    public override string Name => "save";
    public override string Description => "Prevent shots on goal; the defender starts in net, at a post, or rotating back.";

    public override EpisodeSetup Generate(Random r)
    {
        var origin = V(Uniform(r, -3000, 3000), Uniform(r, -3200, 0), Uniform(r, 93, 500));
        var aim = V(Uniform(r, -780, 780), -5200, Uniform(r, 100, 560));
        float speed = Uniform(r, 1500, 3200);
        RsbVec direction = (aim - origin).Normalized();
        // Solve a simple ballistic arc so the ball arrives near the aimed height.
        float flat = (aim - origin).FlatLength;
        float time = flat / (speed * MathF.Sqrt(direction.X * direction.X + direction.Y * direction.Y) + 1e-3f);
        var velocity = direction * speed;
        velocity.Z = (aim.Z - origin.Z + 0.5f * 650 * time * time) / MathF.Max(time, 0.2f);

        double roll = r.NextDouble();
        RsbVec car;
        float yaw;
        if (roll < 0.35)
        {
            car = V(Uniform(r, -600, 600), -5050, 17);
            yaw = MathF.PI / 2 + Uniform(r, -0.3f, 0.3f);
        }
        else if (roll < 0.6)
        {
            car = V(MathF.Sign(Uniform(r, -1, 1)) * Uniform(r, 700, 1300), -4700, 17);
            yaw = MathF.Atan2(origin.Y - car.Y, origin.X - car.X);
        }
        else
        {
            car = V(Uniform(r, -2500, 2500), Uniform(r, -3500, -2000), 17);
            yaw = -MathF.PI / 2 + Uniform(r, -0.8f, 0.8f);
        }
        float carSpeed = roll < 0.6 ? 0 : Uniform(r, 800, 1800);
        return new EpisodeSetup(origin, velocity,
            new[] { new CarSetup(car, yaw, V(MathF.Cos(yaw) * carSpeed, MathF.Sin(yaw) * carSpeed, 0), Uniform(r, 20, 100)) }, 5f);
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        trace.GoalTeam >= 0 || session.Ball.Physics.Position.Y > 0;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["touched"] = float.IsNaN(trace.FirstTouchTime) ? 0 : 1;
        return trace.GoalTeam != 1;
    }
}

/// <summary>Random airborne tumbles: land on the wheels quickly and keep speed.</summary>
public sealed class RecoveryScenario : Scenario
{
    public override string Name => "recovery";
    public override string Description => "Land upright from random airborne orientations and spins.";

    public override EpisodeSetup Generate(Random r)
    {
        var car = V(Uniform(r, -3000, 3000), Uniform(r, -3500, 3500), Uniform(r, 300, 1100));
        var velocity = V(Uniform(r, -1200, 1200), Uniform(r, -1200, 1200), Uniform(r, -300, 600));
        var spin = V(Uniform(r, -4, 4), Uniform(r, -4, 4), Uniform(r, -4, 4));
        return new EpisodeSetup(V(0, 0, 93.15f), V(0, 0, 0), new[]
        {
            new CarSetup(car, Uniform(r, -MathF.PI, MathF.PI), velocity, Uniform(r, 0, 100),
                Pitch: Uniform(r, -1.5f, 1.5f), Roll: Uniform(r, -MathF.PI, MathF.PI), AngularVelocity: spin,
                OnGround: false, HasJumped: true, HasFlipped: true),
        }, 3.5f);
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => !float.IsNaN(trace.LandedTime);

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["land-s"] = trace.LandedTime;
        return !float.IsNaN(trace.LandedTime) && trace.CarLandedUpright;
    }
}

/// <summary>Bouncing balls in the attacking half: touches that need a jump or double jump.</summary>
public sealed class JumpTouchScenario : Scenario
{
    public override string Name => "jump-touch";
    public override string Description => "Meet a bouncing ball (peaks 260-750) and send it toward the opponent goal.";

    public override EpisodeSetup Generate(Random r)
    {
        float bx = Uniform(r, -2000, 2000), by = Uniform(r, 0, 3000);
        float peak = Uniform(r, 260, 750);
        var ball = V(bx, by, 93.15f);
        var ballVel = V(Uniform(r, -150, 150), Uniform(r, -150, 150), MathF.Sqrt(2 * 650 * (peak - 93.15f)));
        float angle = Uniform(r, -MathF.PI * 0.85f, -MathF.PI * 0.15f);
        float distance = Uniform(r, 1500, 2800);
        var car = V(Math.Clamp(bx + MathF.Cos(angle) * distance, -3800, 3800),
            Math.Clamp(by + MathF.Sin(angle) * distance, -4800, 4800), 17);
        float yaw = MathF.Atan2(by - car.Y, bx - car.X) + Uniform(r, -0.5f, 0.5f);
        float speed = Uniform(r, 0, 1200);
        return new EpisodeSetup(ball, ballVel,
            new[] { new CarSetup(car, yaw, V(MathF.Cos(yaw) * speed, MathF.Sin(yaw) * speed, 0), Uniform(r, 30, 100)) }, 4f);
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        trace.GoalTeam >= 0 || (!float.IsNaN(trace.FirstTouchTime) && trace.Elapsed - trace.FirstTouchTime > 0.3f);

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool touched = !float.IsNaN(trace.FirstTouchTime);
        trace.Metrics["touch-s"] = touched ? trace.FirstTouchTime : double.NaN;
        trace.Metrics["touch-z"] = touched ? trace.BallPositionAtFirstTouch.Z : double.NaN;
        trace.Metrics["air-touch"] = touched && trace.AirborneAtFirstTouch ? 1 : 0;
        return touched && trace.BallVelocityAfterFirstTouch.Y > 300;
    }
}

public static class Suites
{
    public static IReadOnlyList<Scenario> All() => new Scenario[]
    {
        new KickoffScenario(0), new KickoffScenario(1), new KickoffScenario(2), new KickoffScenario(3),
        new KickoffScenario(4), new OpenNetScenario(), new KeeperScenario(), new AerialScenario(),
        new SaveScenario(), new RecoveryScenario(), new JumpTouchScenario(),
    };

    public static IReadOnlyList<Scenario> Select(string names) =>
        names == "all" ? All() : All().Where(s => names.Split(',').Any(n => s.Name.StartsWith(n))).ToList();
}
