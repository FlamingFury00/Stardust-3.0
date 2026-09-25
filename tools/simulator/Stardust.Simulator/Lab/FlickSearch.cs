using System.Globalization;
using System.Text;
using Bot;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// An open-loop flick: hold jump, release, keep a tilt input while waiting, then dodge. All inputs
/// in the car frame: tilt is (pitch, yaw, roll) stick input, dodge is the (pitch, yaw) stick at the
/// dodge (pitch -1 is a front flip).
/// </summary>
public readonly record struct FlickProgram(float Hold, float Wait, float TiltPitch, float TiltYaw, float TiltRoll,
    float DodgePitch, float DodgeYaw, bool Boost = false)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"hold {Hold:F3} wait {Wait:F3} tilt ({TiltPitch:0.##},{TiltYaw:0.##},{TiltRoll:0.##}) dodge ({DodgePitch:0.##},{DodgeYaw:0.##}){(Boost ? " boost" : "")}");

    public ControllerStateT Input(int tick)
    {
        int holdTicks = Math.Max(1, (int)MathF.Round(Hold * 120f));
        int dodgeTick = holdTicks + Math.Max(1, (int)MathF.Round(Wait * 120f));
        var c = new ControllerStateT { Throttle = 1, Boost = Boost };
        if (tick < holdTicks)
        {
            c.Jump = true;
            (c.Pitch, c.Yaw, c.Roll) = (TiltPitch, TiltYaw, TiltRoll);
        }
        else if (tick < dodgeTick)
            (c.Pitch, c.Yaw, c.Roll) = (TiltPitch, TiltYaw, TiltRoll);
        else if (tick == dodgeTick)
        {
            c.Jump = true;
            (c.Pitch, c.Yaw, c.Roll) = (DodgePitch, DodgeYaw, 0f);
        }
        return c;
    }
}

/// <summary>What a flick did to the ball.</summary>
public readonly record struct FlickResult(float Speed, float Heading, float Elevation, bool Hit)
{
    public static FlickResult Miss => new(0, float.NaN, float.NaN, false);
}

/// <summary>
/// Searches open-loop flick programs in RocketSim from settled carries (the carry is held by the
/// bot's own <see cref="HoodCarry"/>), and reports exit speed, heading relative to the car and
/// elevation. The shipped flick families come from its tables.
/// </summary>
public static class FlickSearch
{
    public sealed record Start(float Speed, float BallForward, RsbCarState Car, RsbBallState Ball);

    /// <summary>
    /// Searches each ball placement ("spot", forward of the car origin). Programs are scored on
    /// their worst gain over jittered starts: carry speed 950–1500 uu/s and the ball up to 6 uu off
    /// the spot either way, since a carry never places the ball exactly.
    /// </summary>
    public static int Run(int top, string output, IReadOnlyList<float> spots)
    {
        using var arena = new SimArena();
        uint id = arena.AddCar(0);
        var report = new StringBuilder();
        foreach (float forward in spots)
        {
            var starts = new List<Start>();
            foreach (float speed in new[] { 950f, 1250f, 1500f })
            foreach (float dx in new[] { -6f, 0f, 6f })
            foreach (float dy in new[] { -6f, 0f, 6f })
                starts.Add(Settle(arena, id, speed, forward + dx, dy));
            var programs = new List<FlickProgram>();
            float[] holds = { 0.12f, 0.2f };
            float[] waits = { 1 / 120f, 3 / 120f, 0.06f, 0.12f };
            float[] tilts = { -1f, 0f, 1f };
            foreach (bool boost in new[] { false })
            foreach (float hold in holds)
            foreach (float wait in waits)
            foreach (float pitch in tilts)
            foreach (float yaw in tilts)
            foreach (float roll in tilts)
            for (int d = 0; d < 16; d++)
            {
                (float dp, float dy) = Stick(d * MathF.PI / 8f);
                programs.Add(new FlickProgram(hold, wait, pitch, yaw, roll, dp, dy, boost));
            }
            List<Scored> coarse = Evaluate(programs, starts);
            Console.WriteLine($"ball {forward} uu forward: {coarse.Count}/{programs.Count} programs hit from both speeds");

            var refined = new List<Scored>();
            foreach (Func<Scored, bool> family in new Func<Scored, bool>[] { s => s.Elevation < 20, s => s.Elevation is >= 20 and < 35, s => s.Elevation >= 35 })
            {
                foreach (Scored seed in coarse.Where(family).OrderByDescending(s => s.Score).Take(6))
                {
                    Scored best = seed;
                    var random = new Random(programs.IndexOf(seed.Program));
                    for (int round = 0; round < 6; round++)
                    {
                        var candidates = Enumerable.Range(0, 24).Select(_ => Perturb(best.Program, random)).ToList();
                        Scored challenger = Evaluate(candidates, starts).Where(family).DefaultIfEmpty(best).MaxBy(s => s.Score)!;
                        if (challenger.Score > best.Score) best = challenger;
                    }
                    refined.Add(best);
                }
            }
            Table(report, $"Ball {forward}±6 uu ahead of the origin, ±6 uu sideways; carries at 950–1500 uu/s", refined, top);
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "flicks.md"), report.ToString());
        Console.WriteLine(report);
        return 0;
    }

    /// <summary>A program's results over all starts. Score: the worst speed gain, less a penalty for inconsistent heading.</summary>
    public sealed record Scored(FlickProgram Program, FlickResult[] Results, Start[] Starts)
    {
        public float MinGain => Results.Zip(Starts, (r, s) => r.Speed - s.Speed).Min();
        public float MedianGain => (float)Statistics.Quantile(Results.Zip(Starts, (r, s) => (double)(r.Speed - s.Speed)).ToList(), 0.5);
        public float Spread => Results.Max(r => r.Heading) - Results.Min(r => r.Heading);
        public float Heading => (float)Statistics.Quantile(Results.Select(r => (double)r.Heading).ToList(), 0.5);
        public float Elevation => (float)Statistics.Quantile(Results.Select(r => (double)r.Elevation).ToList(), 0.5);
        public float LowGain => (float)Statistics.Quantile(Results.Zip(Starts, (r, s) => (double)(r.Speed - s.Speed)).ToList(), 0.1);
        public float Score => LowGain - 15f * Spread;
    }

    private static List<Scored> Evaluate(IReadOnlyList<FlickProgram> programs, List<Start> starts)
    {
        var scored = new Scored?[programs.Count];
        Parallel.For(0, programs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            () => { var a = new SimArena(); return (Arena: a, Id: a.AddCar(0)); },
            (i, _, local) =>
            {
                FlickResult[] results = starts.Select(s => Execute(local.Arena, local.Id, s, programs[i])).ToArray();
                scored[i] = results.All(r => r.Hit) ? new Scored(programs[i], results, starts.ToArray()) : null;
                return local;
            },
            local => local.Arena.Dispose());
        return scored.Where(s => s != null).Select(s => s!).ToList();
    }

    private static FlickProgram Perturb(FlickProgram p, Random r)
    {
        float Jitter(float value, float step, float low, float high) =>
            Math.Clamp(value + (float)(r.NextDouble() * 2 - 1) * step, low, high);
        (float dp, float dy) = Stick(MathF.Atan2(p.DodgeYaw, -p.DodgePitch) + (float)(r.NextDouble() * 2 - 1) * 0.25f);
        return new FlickProgram(Jitter(p.Hold, 0.03f, 1 / 120f, 0.2f), Jitter(p.Wait, 0.04f, 1 / 120f, 1.0f),
            Jitter(p.TiltPitch, 0.4f, -1, 1), Jitter(p.TiltYaw, 0.4f, -1, 1), Jitter(p.TiltRoll, 0.4f, -1, 1), dp, dy, p.Boost);
    }

    /// <summary>Full-deflection stick (pitch, yaw) for a dodge direction angle; 0 is a front flip.</summary>
    private static (float Pitch, float Yaw) Stick(float angle)
    {
        float dp = -MathF.Cos(angle), dy = MathF.Sin(angle);
        float scale = 1f / MathF.Max(MathF.Abs(dp), MathF.Abs(dy));
        return (MathF.Round(dp * scale, 3), MathF.Round(dy * scale, 3));
    }

    private static void Table(StringBuilder report, string title, IEnumerable<Scored> rows, int top)
    {
        var inv = CultureInfo.InvariantCulture;
        report.AppendLine($"### {title}").AppendLine();
        report.AppendLine("| Program | p10 gain | median gain | min gain | heading | spread | elevation |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (Scored s in rows.OrderByDescending(s => s.Score).Take(top))
            report.AppendLine(string.Create(inv,
                $"| {s.Program} | {s.LowGain:F0} | {s.MedianGain:F0} | {s.MinGain:F0} | {s.Heading:F1} | {s.Spread:F1} | {s.Elevation:F1} |"));
        report.AppendLine();
    }

    /// <summary>Balances the ball on the roof at the given speed for 0.6 s with the bot's carry controller.</summary>
    public static Start Settle(SimArena arena, uint id, float speed, float ballForward, float ballRight = 0f)
    {
        RsbCarState car = arena.GetCar(id);
        car.Physics = ScenarioRunner.Orientation(0, MathF.PI / 2, 0);
        car.Physics.Position = new RsbVec(0, -3000, 17.01f);
        car.Physics.Velocity = car.Physics.Forward * speed;
        car.Physics.AngularVelocity = new RsbVec(0, 0, 0);
        car.IsOnGround = 1;
        car.HasJumped = car.HasDoubleJumped = car.HasFlipped = car.IsJumping = car.IsFlipping = 0;
        car.Boost = 100;
        arena.SetCar(id, car);
        car = arena.GetCar(id);
        arena.Ball = new RsbBallState
        {
            Physics = new RsbPhysics
            {
                Position = car.Physics.Position + car.Physics.Forward * ballForward + car.Physics.Right * ballRight + new RsbVec(0, 0, 132f),
                Velocity = car.Physics.Velocity,
                Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1),
            },
        };
        var carry = new HoodCarry { Spot = new Vec3(ballForward, ballRight, 0) };
        for (int tick = 0; tick < 72; tick++)
        {
            car = arena.GetCar(id);
            ControllerStateT c = carry.Step(ToCar(car), ToBall(arena.Ball), 1f / 120f, allowBoost: true);
            arena.SetControls(id, Native(c));
            arena.Step();
        }
        return new Start(speed, ballForward, arena.GetCar(id), arena.Ball);
    }

    public static FlickResult Execute(SimArena arena, uint id, Start start, FlickProgram program)
    {
        arena.SetCar(id, start.Car);
        arena.Ball = start.Ball;
        RsbVec heading = start.Car.Physics.Forward;
        ulong lastHit = arena.GetCar(id).LastHitTick;
        int dodgeTick = Math.Max(1, (int)MathF.Round(program.Hold * 120f)) + Math.Max(1, (int)MathF.Round(program.Wait * 120f));
        int since = int.MaxValue;
        bool hitAfterDodge = false;
        RsbVec exit = default;
        bool haveExit = false;
        for (int tick = 0; tick < 150; tick++)
        {
            arena.SetControls(id, Native(program.Input(tick)));
            arena.Step();
            RsbCarState car = arena.GetCar(id);
            bool contact = car.LastHitTick != lastHit;
            lastHit = car.LastHitTick;
            if (contact)
            {
                since = 0;
                if (tick >= dodgeTick) hitAfterDodge = true;
            }
            else if (since != int.MaxValue && ++since == 2)
            {
                exit = arena.Ball.Physics.Velocity;
                haveExit = true;
            }
            if (haveExit && since > 30) break;
        }
        if (!haveExit || !hitAfterDodge) return FlickResult.Miss;
        float relative = MathF.Atan2(heading.X * exit.Y - heading.Y * exit.X, heading.X * exit.X + heading.Y * exit.Y);
        return new FlickResult(exit.Length, relative * 180f / MathF.PI,
            MathF.Atan2(exit.Z, exit.FlatLength) * 180f / MathF.PI, true);
    }

    public static Car ToCar(in RsbCarState s)
    {
        var car = new Car
        {
            Location = V(s.Physics.Position), Velocity = V(s.Physics.Velocity), AngularVelocity = V(s.Physics.AngularVelocity),
            Orientation = new Mat3x3(V(s.Physics.Forward), V(s.Physics.Right), V(s.Physics.Up)),
            IsGrounded = s.IsOnGround != 0, Boost = s.Boost,
        };
        car.LocalAngularVelocity = car.Local(car.AngularVelocity);
        return car;
    }

    public static Ball ToBall(in RsbBallState b) => new(V(b.Physics.Position), V(b.Physics.Velocity), V(b.Physics.AngularVelocity));

    public static RsbControls Native(ControllerStateT c) => new()
    {
        Throttle = c.Throttle, Steer = c.Steer, Pitch = c.Pitch, Yaw = c.Yaw, Roll = c.Roll,
        Jump = c.Jump ? 1 : 0, Boost = c.Boost ? 1 : 0, Handbrake = c.Handbrake ? 1 : 0,
    };

    private static Vec3 V(RsbVec v) => new(v.X, v.Y, v.Z);
}
