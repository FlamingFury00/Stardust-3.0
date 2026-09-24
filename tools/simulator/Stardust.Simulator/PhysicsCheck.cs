using System.Globalization;
using RedUtils.Math;
using RedUtils.Physics;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator;

/// <summary>
/// Validates the bot's analytic physics models (src/RedUtils/Physics) against RocketSim ground
/// truth. Every model the bot plans with must pass here before it is trusted in play.
/// </summary>
public static class PhysicsCheck
{
    private static Vec3 ToVec(RsbVec v) => new(v.X, v.Y, v.Z);
    private static RsbVec ToRsb(Vec3 v) => new(v.x, v.y, v.z);

    public static int Run(string which, int trials, int seed)
    {
        bool all = which == "all";
        int failures = 0;
        if (all || which == "hit") failures += Hit(trials, seed);
        if (which == "drive-probe") DriveProbe();
        if (which == "drive-cases") DriveCases();
        if (which == "turn-drag") TurnDragTable();
        if (which == "nav-trace") NavTrace();
        if (which == "jump-probe") JumpProbe();
        if (which == "flight-probe") FlightProbe();
        if (all || which == "drive") failures += DriveOpenLoop(trials, seed);
        if (all || which == "navigate") failures += NavigateClosedLoop(trials, seed);
        if (all || which == "timed") failures += NavigateTimed(trials, seed);
        if (all || which == "jump") failures += JumpCheck(trials, seed);
        if (all || which == "dodge") failures += DodgeCheck(trials, seed);
        if (all || which == "orient") failures += OrientCheck(trials, seed);
        if (all || which == "flight") failures += FlightCheck(trials, seed);
        return failures;
    }

    private static double Percentile(IEnumerable<double> values, double q)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(x => x).ToList();
        return sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, q * sorted.Count)];
    }

    /// <summary>Random piecewise-constant inputs applied to RocketSim and to GroundModel.</summary>
    private static int DriveOpenLoop(int trials, int seed)
    {
        var random = new Random(seed);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var err1 = new List<double>();
        var err2 = new List<double>();
        var speedErr = new List<double>();
        var headingErr = new List<double>();

        for (int trial = 0; trial < trials; trial++)
        {
            float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
            float yaw = R(-MathF.PI, MathF.PI);
            float v0 = R(0, 2300);
            var s = GroundCar(arena, car, v0, yaw);
            s.Physics.Position = new RsbVec(R(-400, 400), R(-400, 400), 17.01f);
            s.Boost = R(0, 100);
            arena.SetCar(car, s);
            s = arena.GetCar(car);
            var model = new GroundState(ToVec(s.Physics.Position), ToVec(s.Physics.Forward), s.Physics.Velocity.Dot(s.Physics.Forward), s.Boost, 0, s.Physics.AngularVelocity.Z);

            var controls = new RsbControls();
            for (int tick = 0; tick < 240; tick++)
            {
                // The navigation policy drives forwards: throttle, coast, or brake only while moving
                // forwards quickly. Reversing manoeuvres are out of this model's scope.
                if (tick % 30 == 0)
                {
                    double pick = random.NextDouble();
                    controls = new RsbControls
                    {
                        Throttle = pick < 0.7 ? 1 : pick < 0.88 ? 0 : -1,
                        Steer = random.NextDouble() < 0.4 ? 0 : R(-1, 1),
                        Boost = random.NextDouble() < 0.4 ? 1 : 0,
                    };
                }
                if (controls.Throttle < 0 && model.Speed < 400)
                    controls.Throttle = 0;
                arena.SetControls(car, controls);
                arena.Step();
                GroundModel.Step(ref model, controls.Throttle, controls.Steer, controls.Boost != 0, SimArena.TickTime);
                if (tick == 119 || tick == 239)
                {
                    var actual = arena.GetCar(car);
                    double error = (ToVec(actual.Physics.Position) - model.Position).Flatten().Length();
                    (tick == 119 ? err1 : err2).Add(error);
                    if (tick == 239)
                    {
                        speedErr.Add(Math.Abs(actual.Physics.Velocity.Dot(actual.Physics.Forward) - model.Speed));
                        headingErr.Add(Math.Abs(GroundModel.SignedAngle(ToVec(actual.Physics.Forward).Flatten().Normalize(), model.Forward)) * 180 / Math.PI);
                    }
                }
            }
        }

        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"drive: position error after 1 s p50 {Percentile(err1, 0.5):F0} uu p90 {Percentile(err1, 0.9):F0}; after 2 s p50 {Percentile(err2, 0.5):F0} p90 {Percentile(err2, 0.9):F0}; " +
            $"speed error p50 {Percentile(speedErr, 0.5):F0} uu/s; heading error p50 {Percentile(headingErr, 0.5):F1} deg"));
        bool ok = Percentile(err1, 0.5) < 40 && Percentile(err2, 0.5) < 120;
        Console.WriteLine(ok ? "drive: PASS" : "drive: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The navigator drives the RocketSim car; its model rollout must predict the real arrival time.
    /// Half of the targets require a specific arrival heading.
    /// </summary>
    private static int NavigateClosedLoop(int trials, int seed)
    {
        var random = new Random(seed + 1);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var timeErrors = new List<double>();
        var relErrors = new List<double>();
        int arrived = 0, predictedArrivals = 0, aligned = 0, directional = 0, both = 0;

        for (int trial = 0; trial < trials; trial++)
        {
            float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
            var s = GroundCar(arena, car, R(0, 2000), R(-MathF.PI, MathF.PI));
            s.Physics.Position = new RsbVec(R(-2500, 2500), R(-3500, 3500), 17.01f);
            s.Boost = R(0, 100);
            arena.SetCar(car, s);
            s = arena.GetCar(car);
            var goal = new Vec3(R(-3000, 3000), R(-4000, 4000), 0);
            bool withDirection = trial % 2 == 0;
            Vec3 direction = withDirection ? GroundModel.Rotate(Vec3.X, R(-MathF.PI, MathF.PI)) : Vec3.Zero;
            var target = new DriveTarget(goal, direction);

            var start = new GroundState(ToVec(s.Physics.Position), ToVec(s.Physics.Forward), s.Physics.Velocity.Dot(s.Physics.Forward), s.Boost, 0, s.Physics.AngularVelocity.Z);
            RolloutResult prediction = Navigator.Rollout(start, target, 8f);
            if (prediction.Arrived) predictedArrivals++;

            float elapsed = 0;
            bool done = false;
            float headingError = float.NaN;
            float previous = float.PositiveInfinity;
            for (int tick = 0; tick < 960 && !done; tick++)
            {
                var c = arena.GetCar(car);
                Vec3 pos = ToVec(c.Physics.Position), fwd = ToVec(c.Physics.Forward);
                Vec3 to = (target.Point - pos).Flatten();
                float distance = to.Length();
                if (distance < Navigator.ArrivalRadius || (distance < 160 && distance > previous && to.Dot(fwd) < 0))
                {
                    done = true;
                    headingError = withDirection ? MathF.Abs(GroundModel.SignedAngle(fwd.Flatten().Normalize(), direction)) : 0;
                    break;
                }
                previous = distance;
                float speed = c.Physics.Velocity.Dot(c.Physics.Forward);
                DriveCommand command = Navigator.Control(pos, fwd, speed, c.Boost, c.Physics.AngularVelocity.Z, target, elapsed);
                arena.SetControls(car, new RsbControls { Throttle = command.Throttle, Steer = command.Steer, Boost = command.Boost ? 1 : 0, Handbrake = command.Handbrake ? 1 : 0 });
                arena.Step();
                elapsed += SimArena.TickTime;
            }
            if (!done)
            {
                if (Environment.GetEnvironmentVariable("STARDUST_CHECK_VERBOSE") == "1")
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  no arrival: start {start.Position} fwd {start.Forward} v {start.Speed:F0} -> target {goal} dir {direction}; predicted {prediction.Time:F2}s arrived={prediction.Arrived}; final {ToVec(arena.GetCar(car).Physics.Position)}"));
                continue;
            }
            if (Environment.GetEnvironmentVariable("STARDUST_CHECK_VERBOSE") == "1" && Math.Abs(prediction.Time - elapsed) > 0.4)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  eta miss: start {start.Position} fwd {start.Forward} v {start.Speed:F0} -> target {goal} dir {direction}; predicted {prediction.Time:F2}s actual {elapsed:F2}s"));
            arrived++;
            if (withDirection)
            {
                directional++;
                if (headingError < 0.35f) aligned++;
            }
            if (prediction.Time <= 8f)
            {
                both++;
                timeErrors.Add(Math.Abs(prediction.Time - elapsed));
                relErrors.Add(Math.Abs(prediction.Time - elapsed) / Math.Max(elapsed, 0.3));
            }
        }

        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"navigate: arrived {arrived}/{trials}; aligned {aligned}/{directional} directional arrivals; " +
            $"ETA error p50 {Percentile(timeErrors, 0.5):F3} s p90 {Percentile(timeErrors, 0.9):F3} s (relative p50 {Percentile(relErrors, 0.5):P1}, p90 {Percentile(relErrors, 0.9):P1}, n={both})"));
        bool ok = Percentile(timeErrors, 0.5) < 0.08 && arrived > trials * 0.9;
        Console.WriteLine(ok ? "navigate: PASS" : "navigate: FAIL");
        return ok ? 0 : 1;
    }

    private static RsbCarState GroundCar(SimArena arena, uint car, float speed, float yaw = MathF.PI / 2)
    {
        var s = arena.GetCar(car);
        s.Physics = ScenarioRunner.Orientation(0, yaw, 0);
        s.Physics.Position = new RsbVec(0, -3000, 17.01f);
        s.Physics.Velocity = s.Physics.Forward * speed;
        s.Physics.AngularVelocity = new RsbVec(0, 0, 0);
        s.IsOnGround = 1;
        s.HasJumped = 0;
        s.HasFlipped = 0;
        s.HasDoubleJumped = 0;
        s.Boost = 100;
        arena.SetCar(car, s);
        return arena.GetCar(car);
    }

    /// <summary>
    /// Timed arrival: the target time is the model's earliest arrival plus a random slack. The
    /// controller must arrive on time (not early) with the requested heading.
    /// </summary>
    private static int NavigateTimed(int trials, int seed)
    {
        var random = new Random(seed + 2);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var timing = new List<double>();
        var early = 0;
        int arrived = 0, feasible = 0, aligned = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
            var s = GroundCar(arena, car, R(0, 1800), R(-MathF.PI, MathF.PI));
            s.Physics.Position = new RsbVec(R(-2500, 2500), R(-3500, 3500), 17.01f);
            s.Boost = R(10, 100);
            arena.SetCar(car, s);
            s = arena.GetCar(car);
            var goal = new Vec3(R(-3000, 3000), R(-4000, 4000), 0);
            Vec3 direction = GroundModel.Rotate(Vec3.X, R(-MathF.PI, MathF.PI));
            var start = new GroundState(ToVec(s.Physics.Position), ToVec(s.Physics.Forward), s.Physics.Velocity.Dot(s.Physics.Forward), s.Boost, 0, s.Physics.AngularVelocity.Z);
            RolloutResult asap = Navigator.Rollout(start, new DriveTarget(goal, direction), 6f);
            if (!asap.Arrived) continue;
            feasible++;
            float arrival = asap.Time + R(0.1f, 1.5f);
            var target = new DriveTarget(goal, direction, arrival);

            float elapsed = 0, previous = float.PositiveInfinity;
            bool done = false;
            for (int tick = 0; tick < 960 && !done; tick++)
            {
                var c = arena.GetCar(car);
                Vec3 pos = ToVec(c.Physics.Position), fwd = ToVec(c.Physics.Forward);
                Vec3 to = (target.Point - pos).Flatten();
                float distance = to.Length();
                if (distance < Navigator.ArrivalRadius || (distance < 160 && distance > previous && to.Dot(fwd) < 0))
                {
                    done = true;
                    arrived++;
                    timing.Add(elapsed - arrival);
                    if (elapsed < arrival - 0.1f) early++;
                    if (MathF.Abs(GroundModel.SignedAngle(fwd.Flatten().Normalize(), direction)) < 0.35f) aligned++;
                    break;
                }
                previous = distance;
                DriveCommand command = Navigator.Control(pos, fwd, c.Physics.Velocity.Dot(c.Physics.Forward), c.Boost,
                    c.Physics.AngularVelocity.Z, target, elapsed);
                arena.SetControls(car, new RsbControls { Throttle = command.Throttle, Steer = command.Steer, Boost = command.Boost ? 1 : 0 });
                arena.Step();
                elapsed += SimArena.TickTime;
            }
        }
        var inv = CultureInfo.InvariantCulture;
        var absolute = timing.Select(Math.Abs).ToList();
        Console.WriteLine(string.Create(inv,
            $"timed: arrived {arrived}/{feasible}; aligned {aligned}; timing error |p50| {Percentile(absolute, 0.5):F3} s |p90| {Percentile(absolute, 0.9):F3} s; " +
            $"bias {(timing.Count > 0 ? timing.Average() : 0):+0.000;-0.000} s; early by >0.1 s: {early}"));
        bool ok = Percentile(absolute, 0.5) < 0.08 && arrived > feasible * 0.9;
        Console.WriteLine(ok ? "timed: PASS" : "timed: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Random hold times and double-jump timings: JumpModel against RocketSim heights.</summary>
    private static int JumpCheck(int trials, int seed)
    {
        var random = new Random(seed + 3);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var errors = new List<double>();
        for (int trial = 0; trial < trials; trial++)
        {
            int hold = random.Next(3, 25);
            bool doubleJump = random.NextDouble() < 0.4;
            int second = doubleJump ? 26 : -1;
            if (doubleJump) hold = 24;
            GroundCar(arena, car, (float)random.NextDouble() * 1500);
            for (int t = 0; t < 30; t++) { arena.SetControls(car, new RsbControls { Throttle = 0.01f }); arena.Step(); }
            for (int tick = 1; tick <= 110; tick++)
            {
                arena.SetControls(car, new RsbControls { Jump = tick <= hold || tick == second ? 1 : 0, Throttle = 0.01f });
                arena.Step();
                float t = tick / 120f;
                float model = doubleJump ? JumpModel.Double(t, 26 / 120f - 0.5f / 120f).Height : JumpModel.Single(t, hold / 120f).Height;
                if (tick % 10 == 0) errors.Add(Math.Abs(arena.GetCar(car).Physics.Position.Z - model));
            }
        }
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv, $"jump: height error p50 {Percentile(errors, 0.5):F1} uu p90 {Percentile(errors, 0.9):F1} uu max {errors.Max():F1}"));
        bool ok = Percentile(errors, 0.9) < 8;
        Console.WriteLine(ok ? "jump: PASS" : "jump: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Dodges with random stick inputs and speeds: impulse direction and size against RocketSim.</summary>
    private static int DodgeCheck(int trials, int seed)
    {
        var random = new Random(seed + 4);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var angle = new List<double>();
        var magnitude = new List<double>();
        var steering = new List<double>();
        for (int trial = 0; trial < trials; trial++)
        {
            float speed = (float)random.NextDouble() * 2300 * (random.NextDouble() < 0.15 ? -0.4f : 1f);
            GroundCar(arena, car, speed, (float)(random.NextDouble() * 6.28));
            for (int t = 0; t < 10; t++) { arena.SetControls(car, new RsbControls { Throttle = 0.01f }); arena.Step(); }
            // Jump, release, then dodge; compare the horizontal velocity jump on the dodge tick.
            for (int tick = 0; tick < 6; tick++) { arena.SetControls(car, new RsbControls { Jump = tick < 4 ? 1 : 0 }); arena.Step(); }
            var before = arena.GetCar(car);
            float pitch = (float)(random.NextDouble() * 2 - 1), yaw = (float)(random.NextDouble() * 2 - 1);
            if (random.NextDouble() < 0.3) { var aim = GroundModel.Rotate(Vec3.X, (float)(random.NextDouble() * 6.28)); (pitch, yaw) = DodgeModel.InputToward(ToVec(before.Physics.Forward), aim, before.Physics.Velocity.Dot(before.Physics.Forward)); }
            arena.SetControls(car, new RsbControls { Jump = 1, Pitch = pitch, Yaw = yaw });
            arena.Step();
            var after = arena.GetCar(car);
            Vec3 actual = (ToVec(after.Physics.Velocity) - ToVec(before.Physics.Velocity)).Flatten();
            Vec3 model = DodgeModel.Impulse(ToVec(before.Physics.Forward), pitch, yaw, before.Physics.Velocity.Dot(before.Physics.Forward));
            if (model.Length() < 1) continue;
            angle.Add(Math.Acos(Math.Clamp(actual.Normalize().Dot(model.Normalize()), -1, 1)) * 180 / Math.PI);
            magnitude.Add(Math.Abs(actual.Length() - model.Length()) / model.Length());
        }
        // Steering check: InputToward must produce impulses along the requested direction.
        for (int trial = 0; trial < 200; trial++)
        {
            Vec3 forward = GroundModel.Rotate(Vec3.X, (float)(random.NextDouble() * 6.28));
            Vec3 want = GroundModel.Rotate(Vec3.X, (float)(random.NextDouble() * 6.28));
            float speed = (float)random.NextDouble() * 2300;
            var (p, y) = DodgeModel.InputToward(forward, want, speed);
            Vec3 got = DodgeModel.Impulse(forward, p, y, speed);
            steering.Add(Math.Acos(Math.Clamp(got.Normalize().Dot(want), -1, 1)) * 180 / Math.PI);
        }
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"dodge: direction error p50 {Percentile(angle, 0.5):F2} deg p90 {Percentile(angle, 0.9):F2}; magnitude error p50 {Percentile(magnitude, 0.5):P1} p90 {Percentile(magnitude, 0.9):P1}; InputToward error p90 {Percentile(steering, 0.9):F2} deg"));
        // Components under 0.1 are zeroed by the game, so a few directions are unreachable by ~2 degrees.
        bool ok = Percentile(angle, 0.9) < 3 && Percentile(magnitude, 0.9) < 0.06 && Percentile(steering, 0.9) < 3;
        Console.WriteLine(ok ? "dodge: PASS" : "dodge: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Air reorientation: random attitude and spin to a random target attitude. Reports the time to
    /// settle within 0.1 rad (and 0.5 rad/s) for AirControl.Orient and for the legacy PD law.
    /// </summary>
    private static int OrientCheck(int trials, int seed)
    {
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var inv = CultureInfo.InvariantCulture;
        var results = new Dictionary<string, List<double>>();
        foreach (string controller in new[] { "orient", "legacy-pd" })
        {
            var random = new Random(seed + 5);
            var times = new List<double>();
            for (int trial = 0; trial < trials; trial++)
            {
                float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
                var s = arena.GetCar(car);
                s.Physics = ScenarioRunner.Orientation(R(-1.5f, 1.5f), R(-3.14f, 3.14f), R(-3.14f, 3.14f));
                // High, rising start so the car stays airborne; no dodge (a dodge locks pitch for ~0.95 s).
                s.Physics.Position = new RsbVec(0, 0, 1400);
                s.Physics.Velocity = new RsbVec(0, 0, 650);
                s.Physics.AngularVelocity = new RsbVec(R(-3, 3), R(-3, 3), R(-3, 3));
                s.IsOnGround = 0; s.HasJumped = 1; s.HasFlipped = 0; s.HasDoubleJumped = 1;
                arena.SetCar(car, s);
                RsbPhysics target = ScenarioRunner.Orientation(R(-1.5f, 1.5f), R(-3.14f, 3.14f), R(-3.14f, 3.14f));
                Vec3 tf = ToVec(target.Forward), tu = ToVec(target.Up);
                float settled = float.NaN;
                for (int tick = 0; tick < 240; tick++)
                {
                    var c = arena.GetCar(car);
                    Vec3 f = ToVec(c.Physics.Forward), r = ToVec(c.Physics.Right), u = ToVec(c.Physics.Up), w = ToVec(c.Physics.AngularVelocity);
                    Vec3 local = new(w.Dot(f), w.Dot(r), w.Dot(u));
                    Vec3 error = AirControl.RotationError(f, r, u, tf, tu);
                    if (error.Length() < 0.1f && local.Length() < 0.5f) { if (float.IsNaN(settled)) settled = tick / 120f; }
                    else settled = float.NaN;
                    var controls = new RsbControls();
                    if (controller == "orient")
                    {
                        AirInput a = AirControl.Orient(f, r, u, local, tf, tu);
                        controls.Pitch = a.Pitch; controls.Yaw = a.Yaw; controls.Roll = a.Roll;
                    }
                    else
                    {
                        controls.Roll = Math.Clamp(-(8 * error.x - 2.4f * local.x) / 5, -1, 1);
                        controls.Pitch = Math.Clamp(-(7 * error.y - 2.5f * local.y) / 4, -1, 1);
                        controls.Yaw = Math.Clamp((7 * error.z - 2.5f * local.z) / 4, -1, 1);
                    }
                    arena.SetControls(car, controls);
                    arena.Step();
                }
                times.Add(float.IsNaN(settled) ? 2.0 : settled);
            }
            results[controller] = times;
            Console.WriteLine(string.Create(inv,
                $"orient [{controller}]: settle time p50 {Percentile(times, 0.5):F3} s p90 {Percentile(times, 0.9):F3} s; unsettled after 2 s: {times.Count(t => t >= 2.0)}/{trials}"));
        }
        bool ok = Percentile(results["orient"], 0.5) <= Percentile(results["legacy-pd"], 0.5) && results["orient"].Count(t => t >= 2.0) <= trials / 50;
        Console.WriteLine(ok ? "orient: PASS" : "orient: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>Random air inputs (boost, throttle, pitch/yaw/roll): AerialModel against RocketSim over 1 s.</summary>
    private static int FlightCheck(int trials, int seed)
    {
        var random = new Random(seed + 6);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var position = new List<double>();
        var attitude = new List<double>();
        var boostError = new List<double>();
        for (int trial = 0; trial < trials; trial++)
        {
            float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
            var s = arena.GetCar(car);
            s.Physics = ScenarioRunner.Orientation(R(-1.2f, 1.2f), R(-3.14f, 3.14f), R(-3.14f, 3.14f));
            s.Physics.Position = new RsbVec(R(-1000, 1000), R(-1000, 1000), 1200);
            s.Physics.Velocity = new RsbVec(R(-800, 800), R(-800, 800), R(0, 700));
            s.Physics.AngularVelocity = new RsbVec(R(-3, 3), R(-3, 3), R(-3, 3));
            s.IsOnGround = 0; s.HasJumped = 1; s.HasFlipped = 0; s.HasDoubleJumped = 1;
            s.Boost = R(10, 100);
            arena.SetCar(car, s);
            s = arena.GetCar(car);
            var model = new FlightState(ToVec(s.Physics.Position), ToVec(s.Physics.Velocity), ToVec(s.Physics.AngularVelocity),
                ToVec(s.Physics.Forward), ToVec(s.Physics.Right), ToVec(s.Physics.Up), s.Boost);
            var controls = new RsbControls();
            for (int tick = 0; tick < 120; tick++)
            {
                if (tick % 20 == 0)
                    controls = new RsbControls
                    {
                        Pitch = R(-1, 1), Yaw = R(-1, 1), Roll = R(-1, 1), Throttle = R(-1, 1),
                        Boost = random.NextDouble() < 0.5 ? 1 : 0,
                    };
                arena.SetControls(car, controls);
                arena.Step();
                AerialModel.Step(ref model, new AirInput { Pitch = controls.Pitch, Yaw = controls.Yaw, Roll = controls.Roll },
                    controls.Boost != 0, controls.Throttle, SimArena.TickTime);
            }
            var a = arena.GetCar(car);
            position.Add((ToVec(a.Physics.Position) - model.Position).Length());
            attitude.Add(Math.Acos(Math.Clamp(ToVec(a.Physics.Forward).Dot(model.Forward), -1, 1)) * 180 / Math.PI);
            boostError.Add(Math.Abs(a.Boost - model.Boost));
        }
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"flight: position error after 1 s p50 {Percentile(position, 0.5):F1} uu p90 {Percentile(position, 0.9):F1}; forward-axis error p50 {Percentile(attitude, 0.5):F2} deg p90 {Percentile(attitude, 0.9):F2}; boost error p90 {Percentile(boostError, 0.9):F2}"));
        bool ok = Percentile(position, 0.9) < 25 && Percentile(attitude, 0.9) < 5;
        Console.WriteLine(ok ? "flight: PASS" : "flight: FAIL");
        return ok ? 0 : 1;
    }

    private static void FlightProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var cases = new (string Name, RsbControls C)[]
        {
            ("ballistic", new RsbControls()), ("boost", new RsbControls { Boost = 1 }), ("throttle", new RsbControls { Throttle = 1 }),
            ("pitch", new RsbControls { Pitch = 1 }), ("roll+boost", new RsbControls { Roll = 1, Boost = 1 }),
        };
        foreach (var (name, c) in cases)
        {
            var s = arena.GetCar(car);
            s.Physics = ScenarioRunner.Orientation(0.3f, 0.5f, 0.2f);
            s.Physics.Position = new RsbVec(0, 0, 1200);
            s.Physics.Velocity = new RsbVec(300, 200, 400);
            s.Physics.AngularVelocity = new RsbVec(0, 0, 0);
            s.IsOnGround = 0; s.HasJumped = 1; s.HasFlipped = 0; s.HasDoubleJumped = 1; s.Boost = 100;
            arena.SetCar(car, s);
            s = arena.GetCar(car);
            var model = new FlightState(ToVec(s.Physics.Position), ToVec(s.Physics.Velocity), ToVec(s.Physics.AngularVelocity),
                ToVec(s.Physics.Forward), ToVec(s.Physics.Right), ToVec(s.Physics.Up), s.Boost);
            var row = new List<string>();
            for (int tick = 1; tick <= 120; tick++)
            {
                arena.SetControls(car, c);
                arena.Step();
                AerialModel.Step(ref model, new AirInput { Pitch = c.Pitch, Yaw = c.Yaw, Roll = c.Roll }, c.Boost != 0, c.Throttle, SimArena.TickTime);
                if (tick is 1 or 2 or 12 or 60 or 120)
                {
                    var a = arena.GetCar(car);
                    row.Add(string.Create(inv, $"{tick}: dpos {(ToVec(a.Physics.Position) - model.Position).Length():F1} dvel {(ToVec(a.Physics.Velocity) - model.Velocity).Length():F1} boost {a.Boost:F1}/{model.Boost:F1}"));
                }
            }
            Console.WriteLine($"{name,-11} {string.Join(" | ", row)}");
        }
    }

    /// <summary>Vertical trajectories for jump hold times and double-jump timings.</summary>
    private static void JumpProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        foreach (int holdTicks in new[] { 1, 3, 6, 12, 18, 24, 30 })
        foreach (int secondAt in new[] { -1, holdTicks + 2 })
        {
            if (secondAt > 0 && holdTicks < 24) continue;
            GroundCar(arena, car, 0f);
            // Let the suspension settle.
            for (int t = 0; t < 30; t++) { arena.SetControls(car, new RsbControls()); arena.Step(); }
            var row = new List<string>();
            float peak = 0, peakT = 0;
            for (int tick = 1; tick <= 150; tick++)
            {
                bool jump = tick <= holdTicks || tick == secondAt;
                arena.SetControls(car, new RsbControls { Jump = jump ? 1 : 0 });
                arena.Step();
                var c = arena.GetCar(car);
                if (c.Physics.Position.Z > peak) { peak = c.Physics.Position.Z; peakT = tick / 120f; }
                if (tick is 1 or 2 or 3 or 6 or 12 or 24 or 36 or 48 or 60 or 90 or 120)
                    row.Add(string.Create(inv, $"{tick}:{c.Physics.Position.Z:F1}/{c.Physics.Velocity.Z:F0}"));
            }
            Console.WriteLine(string.Create(inv, $"hold {holdTicks,2}{(secondAt > 0 ? $" +2nd@{secondAt}" : "")}: peak {peak:F1} at {peakT:F3}s | {string.Join(" ", row)}"));
        }
    }

    /// <summary>Prints the model rollout of a navigation case for policy debugging.</summary>
    private static void NavTrace()
    {
        var inv = CultureInfo.InvariantCulture;
        string[] parts = (Environment.GetEnvironmentVariable("NAV_CASE") ?? "-800,-2853,1,0.09,452,-2198,-1158,0.99,0.11").Split(',');
        float[] v = parts.Select(p => float.Parse(p, inv)).ToArray();
        var start = new GroundState(new Vec3(v[0], v[1], 0), new Vec3(v[2], v[3], 0), v[4], 50);
        var target = new DriveTarget(new Vec3(v[5], v[6], 0), new Vec3(v[7], v[8], 0));
        GroundState s = start;
        for (int step = 0; step < 480; step++)
        {
            DriveCommand c = Navigator.Control(s.Position, s.Forward, s.Speed, s.Boost, s.YawRate, target, s.Time);
            if (step % 15 == 0)
            {
                Vec3 aim = Navigator.SteeringPoint(s.Position, s.Forward, s.Speed, target);
                Console.WriteLine(string.Create(inv,
                    $"t {s.Time:F2} pos ({s.Position.x:F0},{s.Position.y:F0}) fwd ({s.Forward.x:F2},{s.Forward.y:F2}) v {s.Speed:F0} aim ({aim.x:F0},{aim.y:F0}) steer {c.Steer:F2} thr {c.Throttle:F2} boost {c.Boost} dist {(target.Point - s.Position).Flatten().Length():F0}"));
            }
            GroundModel.Step(ref s, c.Throttle, c.Steer, c.Boost, 1f / 60f);
        }
    }

    /// <summary>Steady-state longitudinal drag while holding a steer value, for throttle and coast.</summary>
    private static void TurnDragTable()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        foreach (float steer in new[] { 1f, 0.5f })
        foreach (float throttle in new[] { 1f, 0f })
        {
            Console.WriteLine($"steer {steer}, throttle {throttle}: speed -> drag (uu/s^2), drag / lateral accel");
            foreach (float v0 in new[] { 200f, 400f, 600f, 800f, 1000f, 1200f, 1400f, 1600f, 1800f, 2000f, 2300f })
            {
                // Settle into the turn at constant speed with gentle throttle, then measure free response.
                GroundCar(arena, car, v0, 0f);
                for (int tick = 0; tick < 48; tick++)
                {
                    var c0 = arena.GetCar(car);
                    float hold = Math.Clamp((v0 - c0.Physics.Velocity.FlatLength) / 30f, -1f, 1f);
                    arena.SetControls(car, new RsbControls { Throttle = MathF.Abs(hold) < 0.02f ? 0.02f : hold, Steer = steer, Boost = v0 > 1410 && c0.Physics.Velocity.FlatLength < v0 - 5 ? 1 : 0 });
                    arena.Step();
                }
                var a = arena.GetCar(car);
                float va = a.Physics.Velocity.Dot(a.Physics.Forward);
                for (int tick = 0; tick < 12; tick++) { arena.SetControls(car, new RsbControls { Throttle = throttle, Steer = steer }); arena.Step(); }
                var b = arena.GetCar(car);
                float vb = b.Physics.Velocity.Dot(b.Physics.Forward);
                float mean = 0.5f * (va + vb);
                float dvdt = (vb - va) / 0.1f;
                float engine = throttle > 0 ? RL.ThrottleAccel(mean) : -RL.CoastBrakeAccel;
                float drag = engine - dvdt;
                float lateral = steer * GroundModel.Curvature(mean) * mean * mean;
                Console.WriteLine(string.Create(inv, $"  {mean,6:F0}: drag {drag,6:F0}  ratio {drag / lateral:F3}"));
            }
        }
    }

    /// <summary>Fixed input sequences comparing RocketSim and GroundModel over two seconds.</summary>
    private static void DriveCases()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var cases = new (string Name, float V0, RsbControls Controls)[]
        {
            ("throttle", 0, new RsbControls { Throttle = 1 }), ("throttle", 1200, new RsbControls { Throttle = 1 }),
            ("boost", 0, new RsbControls { Throttle = 1, Boost = 1 }), ("boost", 1500, new RsbControls { Throttle = 1, Boost = 1 }),
            ("coast", 1500, new RsbControls()), ("brake", 1500, new RsbControls { Throttle = -1 }),
            ("half-steer", 1000, new RsbControls { Throttle = 1, Steer = 0.5f }), ("half-steer", 1800, new RsbControls { Throttle = 1, Steer = 0.5f }),
            ("full-steer", 800, new RsbControls { Throttle = 1, Steer = 1 }), ("full-steer+boost", 1500, new RsbControls { Throttle = 1, Steer = 1, Boost = 1 }),
            ("coast-steer", 1500, new RsbControls { Steer = 1 }), ("brake-steer", 1500, new RsbControls { Throttle = -1, Steer = 1 }),
            ("partial-throttle", 1200, new RsbControls { Throttle = 0.4f }),
        };
        foreach (var (name, v0, controls) in cases)
        {
            var s = GroundCar(arena, car, v0, 0.3f);
            var model = new GroundState(ToVec(s.Physics.Position), ToVec(s.Physics.Forward), v0, 100, 0, 0);
            var row = new List<string>();
            for (int tick = 1; tick <= 240; tick++)
            {
                arena.SetControls(car, controls);
                arena.Step();
                GroundModel.Step(ref model, controls.Throttle, controls.Steer, controls.Boost != 0, SimArena.TickTime);
                if (tick % 60 == 0)
                {
                    var a = arena.GetCar(car);
                    float err = (ToVec(a.Physics.Position) - model.Position).Flatten().Length();
                    row.Add(string.Create(inv, $"{tick / 120f:F1}s: v {a.Physics.Velocity.Dot(a.Physics.Forward):F0}/{model.Speed:F0} pos err {err:F0}"));
                }
            }
            Console.WriteLine($"{name,-18} v0 {v0,5}: {string.Join(" | ", row)}");
        }
    }

    /// <summary>Prints measured ground dynamics tables used to fit the drive model.</summary>
    private static void DriveProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4000, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };

        Console.WriteLine("Full-lock steady turn: speed -> curvature (measured omega/v) with throttle holding speed");
        foreach (float v0 in new[] { 20f, 100f, 250f, 500f, 750f, 1000f, 1250f, 1500f, 1750f, 2000f, 2150f, 2300f })
        {
            GroundCar(arena, car, v0);
            float curvSum = 0; int n = 0; float speedEnd = 0;
            for (int tick = 0; tick < 90; tick++)
            {
                var s = arena.GetCar(car);
                float speed = s.Physics.Velocity.FlatLength;
                float throttle = Math.Clamp((v0 - speed) / 30f, -1, 1);
                if (MathF.Abs(throttle) < 0.02f) throttle = 0.02f;
                arena.SetControls(car, new RsbControls { Throttle = throttle, Steer = 1, Boost = v0 > 1410 && speed < v0 - 5 ? 1 : 0 });
                arena.Step();
                if (tick >= 30)
                {
                    var after = arena.GetCar(car);
                    float sp = after.Physics.Velocity.FlatLength;
                    curvSum += MathF.Abs(after.Physics.AngularVelocity.Z) / MathF.Max(sp, 1);
                    n++;
                    speedEnd = sp;
                }
            }
            Console.WriteLine(string.Create(inv, $"  v0 {v0,6:F0}: speed {speedEnd,6:F0} curvature {curvSum / n:F6} model {RL.MaxCurvature(speedEnd):F6}"));
        }

        Console.WriteLine("Full throttle + full steer from speed (no boost): speed after 0.25/0.5/1.0 s");
        foreach (float v0 in new[] { 0f, 500f, 1000f, 1400f, 1800f, 2300f })
        {
            GroundCar(arena, car, v0);
            var marks = new List<string>();
            for (int tick = 1; tick <= 120; tick++)
            {
                arena.SetControls(car, new RsbControls { Throttle = 1, Steer = 1 });
                arena.Step();
                if (tick == 30 || tick == 60 || tick == 120)
                    marks.Add(arena.GetCar(car).Physics.Velocity.FlatLength.ToString("F0", inv));
            }
            Console.WriteLine($"  v0 {v0,5}: {string.Join(" / ", marks)}");
        }

        Console.WriteLine("Steer step 0 -> 1 (throttle holds speed): yaw rate / steady-state at 1..12 ticks");
        foreach (float v0 in new[] { 500f, 1000f, 1500f, 2000f })
        {
            GroundCar(arena, car, v0);
            var marks = new List<string>();
            float steady = GroundModel.Curvature(v0) * v0;
            for (int tick = 1; tick <= 24; tick++)
            {
                var c = arena.GetCar(car);
                float throttle = Math.Clamp((v0 - c.Physics.Velocity.FlatLength) / 30f, 0.02f, 1f);
                arena.SetControls(car, new RsbControls { Throttle = throttle, Steer = 1 });
                arena.Step();
                if (tick is 1 or 2 or 3 or 4 or 6 or 8 or 12 or 16 or 24)
                    marks.Add((arena.GetCar(car).Physics.AngularVelocity.Z / steady).ToString("F2", inv));
            }
            Console.WriteLine($"  v0 {v0,5}: {string.Join(" ", marks)}");
        }
        Console.WriteLine("Steer release 1 -> 0 after steady turn: yaw rate fraction at 1,2,3,4,6,8,12,16,24 ticks");
        foreach (float v0 in new[] { 500f, 1000f, 1500f, 2000f })
        {
            GroundCar(arena, car, v0);
            for (int tick = 0; tick < 60; tick++)
            {
                var c = arena.GetCar(car);
                arena.SetControls(car, new RsbControls { Throttle = Math.Clamp((v0 - c.Physics.Velocity.FlatLength) / 30f, 0.02f, 1f), Steer = 1 });
                arena.Step();
            }
            float initial = arena.GetCar(car).Physics.AngularVelocity.Z;
            var marks = new List<string>();
            for (int tick = 1; tick <= 24; tick++)
            {
                var c = arena.GetCar(car);
                arena.SetControls(car, new RsbControls { Throttle = Math.Clamp((v0 - c.Physics.Velocity.FlatLength) / 30f, 0.02f, 1f), Steer = 0 });
                arena.Step();
                if (tick is 1 or 2 or 3 or 4 or 6 or 8 or 12 or 16 or 24)
                    marks.Add((arena.GetCar(car).Physics.AngularVelocity.Z / initial).ToString("F2", inv));
            }
            Console.WriteLine($"  v0 {v0,5}: {string.Join(" ", marks)}");
        }

        Console.WriteLine("Straight line: throttle / boost / coast / brake speed after 0.5 s");
        foreach (float v0 in new[] { 0f, 700f, 1400f, 2000f })
        {
            var row = new List<string>();
            foreach (var (label, controls) in new[]
            {
                ("throttle", new RsbControls { Throttle = 1 }), ("boost", new RsbControls { Throttle = 1, Boost = 1 }),
                ("coast", new RsbControls()), ("brake", new RsbControls { Throttle = -1 }),
            })
            {
                GroundCar(arena, car, v0);
                for (int tick = 0; tick < 60; tick++) { arena.SetControls(car, controls); arena.Step(); }
                var s = arena.GetCar(car);
                row.Add($"{label} {s.Physics.Velocity.Dot(s.Physics.Forward):F0}");
            }
            Console.WriteLine($"  v0 {v0,5}: {string.Join(", ", row)}");
        }

        Console.WriteLine("Powerslide turn (throttle 1, steer 1, handbrake): heading change after 0.5 s, speed");
        foreach (float v0 in new[] { 500f, 1000f, 1500f, 2000f })
        {
            GroundCar(arena, car, v0);
            float yaw0 = MathF.Atan2(arena.GetCar(car).Physics.Forward.Y, arena.GetCar(car).Physics.Forward.X);
            for (int tick = 0; tick < 60; tick++) { arena.SetControls(car, new RsbControls { Throttle = 1, Steer = 1, Handbrake = 1 }); arena.Step(); }
            var s = arena.GetCar(car);
            float yaw1 = MathF.Atan2(s.Physics.Forward.Y, s.Physics.Forward.X);
            float dyaw = MathF.Abs(MathF.IEEERemainder(yaw1 - yaw0, 2 * MathF.PI));
            float velYaw = MathF.Atan2(s.Physics.Velocity.Y, s.Physics.Velocity.X);
            Console.WriteLine(string.Create(inv,
                $"  v0 {v0,5}: heading {dyaw * 180 / MathF.PI:F0} deg, velocity heading {MathF.Abs(MathF.IEEERemainder(velYaw - yaw0, 2 * MathF.PI)) * 180 / MathF.PI:F0} deg, speed {s.Physics.Velocity.FlatLength:F0}"));
        }
    }

    private static CarPose Pose(in RsbCarState c) => new(ToVec(c.Physics.Position), ToVec(c.Physics.Forward),
        ToVec(c.Physics.Right), ToVec(c.Physics.Up), ToVec(c.Physics.Velocity), ToVec(c.Physics.AngularVelocity),
        CarPose.OctaneHitbox, CarPose.OctaneOffset);

    /// <summary>Car-ball impacts in the air (no ground/wall interference), random geometry and speeds.</summary>
    private static int Hit(int trials, int seed)
    {
        var random = new Random(seed);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        var angleErrors = new List<double>();
        var speedErrors = new List<double>();
        var naiveAngleErrors = new List<double>();
        int measured = 0, skipped = 0;

        for (int trial = 0; trial < trials; trial++)
        {
            float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
            var ballPos = new RsbVec(R(-1500, 1500), R(-1500, 1500), R(600, 1300));
            var ballVel = new RsbVec(R(-800, 800), R(-800, 800), R(-300, 300));
            float yaw = R(-MathF.PI, MathF.PI), pitch = R(-0.6f, 0.6f), roll = R(-0.6f, 0.6f);
            RsbPhysics orientation = ScenarioRunner.Orientation(pitch, yaw, roll);
            float approachSpeed = R(400, 2300);
            // Aim the car's nose region at a point near the ball so impacts cover the front, corners and roof.
            var aimOffset = new RsbVec(R(-70, 70), R(-70, 70), R(-70, 70));
            RsbVec direction = (ballPos + aimOffset - (ballPos - orientation.Forward * 400)).Normalized();
            var start = ballPos - orientation.Forward * 330 + aimOffset * 0.5f;

            var state = arena.GetCar(car);
            state.Physics = orientation;
            state.Physics.Position = start;
            state.Physics.Velocity = orientation.Forward * approachSpeed + ballVel;
            state.Physics.AngularVelocity = new RsbVec(R(-2, 2), R(-2, 2), R(-2, 2));
            state.IsOnGround = 0;
            state.HasJumped = 1;
            state.HasFlipped = 1;
            state.Boost = 0;
            arena.SetCar(car, state);
            arena.Ball = new RsbBallState { Physics = new RsbPhysics
            {
                Position = ballPos, Velocity = ballVel, Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0),
                Up = new RsbVec(0, 0, 1), AngularVelocity = new RsbVec(R(-3, 3), R(-3, 3), R(-3, 3)),
            } };
            arena.SetControls(car, new RsbControls());
            ulong hitBefore = arena.GetCar(car).LastHitTick;

            RsbCarState preCar = arena.GetCar(car);
            RsbBallState preBall = arena.Ball;
            bool touched = false;
            for (int tick = 0; tick < 60; tick++)
            {
                preCar = arena.GetCar(car);
                preBall = arena.Ball;
                arena.Step();
                if (arena.GetCar(car).LastHitTick != hitBefore) { touched = true; break; }
            }
            if (!touched) { skipped++; continue; }

            // Let the contact finish, then undo gravity/drag accumulated since the impact tick.
            int after = 0;
            ulong lastHit = arena.GetCar(car).LastHitTick;
            while (after < 12)
            {
                arena.Step();
                after++;
                ulong hit = arena.GetCar(car).LastHitTick;
                if (hit == lastHit && after >= 2) break;
                lastHit = hit;
            }
            RsbBallState post = arena.Ball;
            float elapsed = after * SimArena.TickTime;
            Vec3 actual = ToVec(post.Physics.Velocity) - new Vec3(0, 0, RL.Gravity * elapsed);
            actual *= 1f / MathF.Pow(1f - RL.BallDrag, elapsed);

            // Advance both bodies ballistically to first contact inside the impact tick.
            CarPose pose = Pose(preCar);
            Vec3 ball = ToVec(preBall.Physics.Position);
            Vec3 ballV = ToVec(preBall.Physics.Velocity);
            float lo = 0, hi = SimArena.TickTime;
            for (int i = 0; i < 20; i++)
            {
                float mid = 0.5f * (lo + hi);
                CarPose moved = Advance(pose, mid);
                Vec3 b = ball + ballV * mid;
                if ((moved.ClosestPoint(b) - b).Length() > RL.BallRadius) lo = mid; else hi = mid;
            }
            CarPose atContact = Advance(pose, hi);
            Vec3 ballAtContact = ball + ballV * hi;
            HitModel.Result predicted = HitModel.Collide(atContact, ballAtContact, ballV, ToVec(preBall.Physics.AngularVelocity), 12f);
            if (!predicted.Contact) { skipped++; continue; }

            measured++;
            angleErrors.Add(AngleDegrees(predicted.Velocity - ballV, actual - ballV));
            speedErrors.Add((predicted.Velocity.Length() - actual.Length()) / MathF.Max(actual.Length(), 1f));
            Vec3 naive = ballV + (atContact.Velocity - ballV).Dot((ballAtContact - atContact.Position).Normalize()) *
                (ballAtContact - atContact.Position).Normalize() * 1.5f;
            naiveAngleErrors.Add(AngleDegrees(naive - ballV, actual - ballV));
        }

        var inv = CultureInfo.InvariantCulture;
        double Pct(List<double> v, double q) => v.Count == 0 ? double.NaN : v.OrderBy(x => x).ElementAt((int)Math.Min(v.Count - 1, q * v.Count));
        Console.WriteLine(string.Create(inv,
            $"hit: {measured} impacts ({skipped} skipped). Δv direction error p50 {Pct(angleErrors, 0.5):F1}°, p90 {Pct(angleErrors, 0.9):F1}° " +
            $"(naive centre-line p50 {Pct(naiveAngleErrors, 0.5):F1}°); speed error p50 {Pct(speedErrors.Select(Math.Abs).ToList(), 0.5):P1}, " +
            $"p90 {Pct(speedErrors.Select(Math.Abs).ToList(), 0.9):P1}, bias {(speedErrors.Count > 0 ? speedErrors.Average() : 0):P1}"));
        bool ok = Pct(angleErrors, 0.5) < 8 && Pct(speedErrors.Select(Math.Abs).ToList(), 0.5) < 0.1;
        Console.WriteLine(ok ? "hit: PASS" : "hit: FAIL");
        return ok ? 0 : 1;
    }

    private static CarPose Advance(CarPose pose, float dt) => new(pose.Position + pose.Velocity * dt + new Vec3(0, 0, 0.5f * RL.Gravity * dt * dt),
        pose.Forward, pose.Right, pose.Up, pose.Velocity + new Vec3(0, 0, RL.Gravity * dt), pose.AngularVelocity,
        pose.HitboxSize, pose.HitboxOffset);

    private static double AngleDegrees(Vec3 a, Vec3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-3f || lb < 1e-3f) return 0;
        return Math.Acos(Math.Clamp(a.Dot(b) / (la * lb), -1f, 1f)) * 180 / Math.PI;
    }
}
