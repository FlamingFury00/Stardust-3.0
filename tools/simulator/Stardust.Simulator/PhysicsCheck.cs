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
        if (all || which == "arena") failures += ArenaCheck(trials, seed);
        if (all || which == "hit") failures += Hit(trials, seed);
        if (which == "drive-probe") DriveProbe();
        if (which == "drive-cases") DriveCases();
        if (which == "turn-drag") TurnDragTable();
        if (which == "nav-trace") NavTrace();
        if (which == "brake-probe") BrakeProbe();
        if (which == "rollout-bench") RolloutBenchmark(trials, seed);
        if (which == "kickoff-probe") KickoffProbe();
        if (which == "jump-probe") JumpProbe();
        if (which == "flight-probe") FlightProbe();
        if (which == "flip-probe") FlipProbe();
        if (which == "dodge-probe") DodgeProbe();
        if (all || which == "drive") failures += DriveOpenLoop(trials, seed);
        if (all || which == "navigate") failures += NavigateClosedLoop(trials, seed);
        if (all || which == "timed") failures += NavigateTimed(trials, seed);
        if (all || which == "jump") failures += JumpCheck(trials, seed);
        if (all || which == "dodge") failures += DodgeCheck(trials, seed);
        if (all || which == "flip") failures += FlipCheck(trials, seed);
        if (all || which == "orient") failures += OrientCheck(trials, seed);
        if (all || which == "flight") failures += FlightCheck(trials, seed);
        if (all || which == "aerial") failures += AerialCheck(trials, seed);
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
    /// <summary>Optional per-trial CSV rows (STARDUST_CHECK_DUMP=path) for offline error analysis.</summary>
    private static readonly StreamWriter? Dump = Environment.GetEnvironmentVariable("STARDUST_CHECK_DUMP") is { Length: > 0 } path
        ? new StreamWriter(path) { AutoFlush = true } : null;

    private static int NavigateClosedLoop(int trials, int seed)
    {
        var random = new Random(seed + 1);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var timeErrors = new List<double>();
        var relErrors = new List<double>();
        var signed = new List<double>();
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
                    elapsed += Navigator.ArrivalRemainder(to, fwd.Flatten().Normalize(), c.Physics.Velocity.Dot(c.Physics.Forward));
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
                Dump?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{start.Speed:F0},{GroundModel.SignedAngle(start.Forward, (goal - start.Position).Flatten()):F3},{(goal - start.Position).Flatten().Length():F0},{(withDirection ? GroundModel.SignedAngle((goal - start.Position).Flatten(), direction) : float.NaN):F3},{start.Boost:F0},{prediction.Time:F3},{elapsed:F3}"));
                both++;
                timeErrors.Add(Math.Abs(prediction.Time - elapsed));
                signed.Add(elapsed - prediction.Time);
                relErrors.Add(Math.Abs(prediction.Time - elapsed) / Math.Max(elapsed, 0.3));
            }
        }

        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"navigate: arrived {arrived}/{trials}; aligned {aligned}/{directional} directional arrivals; " +
            $"ETA error p50 {Percentile(timeErrors, 0.5):F3} s p90 {Percentile(timeErrors, 0.9):F3} s, bias {(signed.Count > 0 ? signed.Average() : 0):+0.000;-0.000} s (relative p50 {Percentile(relErrors, 0.5):P1}, p90 {Percentile(relErrors, 0.9):P1}, n={both})"));
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
    /// controller must arrive on time (not early) with the requested heading. A second, tight
    /// regime gives strikes' slack (under 0.12 s), where arriving late means missing the touch.
    /// </summary>
    private static int NavigateTimed(int trials, int seed)
    {
        var loose = TimedRegime(trials, seed, 0.1f, 1.5f, "timed");
        var tight = TimedRegime(trials, seed + 1, 0f, 0.12f, "timed-tight");
        bool ok = loose.MedianError < 0.08 && loose.ArrivedShare > 0.9;
        Console.WriteLine(ok ? "timed: PASS" : "timed: FAIL");
        return ok ? 0 : 1;
    }

    private static (double MedianError, double ArrivedShare) TimedRegime(int trials, int seed, float minSlack, float maxSlack, string name)
    {
        var random = new Random(seed + 2);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var timing = new List<double>();
        int early = 0, late = 0;
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
            float arrival = asap.Time + R(minSlack, maxSlack);
            var target = new DriveTarget(goal, direction, arrival);

            float elapsed = 0, previous = float.PositiveInfinity;
            for (int tick = 0; tick < 960; tick++)
            {
                var c = arena.GetCar(car);
                Vec3 pos = ToVec(c.Physics.Position), fwd = ToVec(c.Physics.Forward);
                Vec3 to = (target.Point - pos).Flatten();
                float distance = to.Length();
                if (distance < Navigator.ArrivalRadius || (distance < 160 && distance > previous && to.Dot(fwd) < 0))
                {
                    arrived++;
                    elapsed += Navigator.ArrivalRemainder(to, fwd.Flatten().Normalize(), c.Physics.Velocity.Dot(c.Physics.Forward));
                    timing.Add(elapsed - arrival);
                    if (elapsed < arrival - 0.1f) early++;
                    if (elapsed > arrival + 0.1f) late++;
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
            $"{name}: arrived {arrived}/{feasible}; aligned {aligned}; timing error |p50| {Percentile(absolute, 0.5):F3} s |p90| {Percentile(absolute, 0.9):F3} s; " +
            $"bias {(timing.Count > 0 ? timing.Average() : 0):+0.000;-0.000} s; early by >0.1 s: {early}; late by >0.1 s: {late}"));
        return (Percentile(absolute, 0.5), feasible > 0 ? (double)arrived / feasible : 0);
    }

    /// <summary>
    /// The procedural arena's floor is flat wherever the real one is: an idle car placed on the
    /// floor away from the wall ramps stays at rest height without touching anything. The goal
    /// mouths are sampled densely, since the back-wall ramp must end at the posts.
    /// </summary>
    private static int ArenaCheck(int trials, int seed)
    {
        var random = new Random(seed + 11);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 0, 1500), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        const float CarReach = 90f;
        int bad = 0, samples = 0;
        float worstLift = 0f;
        string worst = "";
        for (int trial = 0; trial < trials; trial++)
        {
            bool mouth = trial % 2 == 0;
            float side = random.NextDouble() < 0.5 ? -1f : 1f;
            // The mouth runs from the posts' inner faces up to the goal line; elsewhere the floor
            // is flat until the side ramps (256 uu) and back-wall ramps (160 uu) begin.
            float x = mouth ? Uniform(random, -892.755f + CarReach, 892.755f - CarReach) : Uniform(random, -4096f + 256f + CarReach, 4096f - 256f - CarReach);
            float y = mouth ? side * Uniform(random, 4700f, 5120f - CarReach) : Uniform(random, -5120f + 160f + CarReach, 5120f - 160f - CarReach);
            if (!mouth && MathF.Abs(x) + MathF.Abs(y) > 8064f - 700f) continue;
            float yaw = Uniform(random, -MathF.PI, MathF.PI);
            RsbCarState state = arena.GetCar(car);
            state.Physics = ScenarioRunner.Orientation(0f, yaw, 0f);
            state.Physics.Position = new RsbVec(x, y, 17f);
            state.IsOnGround = 1;
            state.HasJumped = state.HasDoubleJumped = state.HasFlipped = 0;
            arena.SetCar(car, state);
            float lift = 0f;
            bool touched = false;
            for (int tick = 0; tick < 60; tick++)
            {
                arena.SetControls(car, new RsbControls());
                arena.Step();
                RsbCarState now = arena.GetCar(car);
                lift = MathF.Max(lift, MathF.Abs(now.Physics.Position.Z - 17f));
                touched |= now.HasWorldContact != 0;
            }
            samples++;
            if (lift > 1.5f || touched)
            {
                bad++;
                if (lift > worstLift)
                {
                    worstLift = lift;
                    worst = string.Create(CultureInfo.InvariantCulture, $" worst at ({x:F0}, {y:F0}) lift {lift:F1} uu");
                }
            }
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"arena: {bad}/{samples} floor placements disturbed{worst}"));
        bool ok = bad == 0;
        Console.WriteLine(ok ? "arena: PASS" : "arena: FAIL");
        return ok ? 0 : 1;
    }

    private static float Uniform(Random random, float low, float high) => low + (float)random.NextDouble() * (high - low);

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
    /// Planned flip touches: a car running at a random speed toward a ball at a random height
    /// (resting, or at the top of a bounce) takes off FlipModel.TimeToHeight before contact, releases
    /// jump on the contact clock and dodges FlipModel.Lead before contact, exactly as DrivenStrike
    /// does. The touch must happen on time and the ball must leave as Contact.Aim predicts.
    /// </summary>
    private static int FlipCheck(int trials, int seed)
    {
        var random = new Random(seed + 11);
        float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        var geometry = new RedUtils.Planning.CarGeometry(CarPose.OctaneHitbox, CarPose.OctaneOffset);
        const int RunIn = 30;
        const float Tick = 1f / 120f;
        var direction = new List<double>();
        var speedError = new List<double>();
        var gaps = new List<double>();
        int planned = 0, touched = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            float speed = R(800f, 1900f);
            // Rolling balls rest at 93; bouncing ones are met at the top of the bounce.
            float ballZ = random.NextDouble() < 0.35 ? 93f : R(125f, 210f);
            Vec3 heading = Vec3.Y;
            float jumpTime = FlipModel.TimeToHeight(ballZ - 25f);
            if (!float.IsFinite(jumpTime)) continue;
            int flight = (int)MathF.Round(jumpTime / Tick);
            var (height, vertical) = FlipModel.AtContact(flight * Tick);
            float contactSpeed = FlipModel.SpeedAfter(speed, vertical);
            var ball = new Vec3(R(-200f, 200f), 0f, ballZ);
            Vec3 aim = ball + GroundModel.Rotate(heading, R(-0.4f, 0.4f)) * 3000f;
            var contact = RedUtils.Planning.Contact.Aim(ball, Vec3.Zero, heading, heading * contactSpeed + new Vec3(0f, 0f, vertical),
                height, aim, geometry, FlipModel.Pitch(FlipModel.Lead), RL.CarMaxAngularSpeed);
            if (!contact.Valid) continue;
            planned++;

            // Back the car off along the heading: the flight, then the run-in on the ground.
            float airborne = speed * (flight * Tick - FlipModel.Lead) + contactSpeed * FlipModel.Lead;
            Vec3 start = contact.CarPosition.Flatten() - heading * (airborne + speed * RunIn * Tick);
            var state = GroundCar(arena, car, speed);
            state.Physics.Position = new RsbVec(start.x, start.y, 17.01f);
            arena.SetCar(car, state);
            int contactTick = RunIn + flight;
            arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(3000, 3000, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
            ulong hitBefore = arena.GetCar(car).LastHitTick;

            bool jumpHeld = false, dodged = false;
            bool hit = false;
            for (int tick = 0; tick < contactTick + 30; tick++)
            {
                // The ball appears 0.3 s before contact and peaks (or rests) at the contact point.
                int appear = contactTick - 36;
                if (tick == System.Math.Max(0, appear))
                {
                    float rise = (contactTick - System.Math.Max(0, appear)) * Tick;
                    bool bounce = ballZ > 93f;
                    float z0 = bounce ? ballZ + 0.5f * RL.Gravity * rise * rise : 93f;
                    float vz = bounce ? -RL.Gravity * rise : 0f;
                    arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(ball.x, ball.y, z0), Velocity = new RsbVec(0, 0, vz), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
                }
                var c = arena.GetCar(car);
                var controls = new RsbControls { Throttle = speed < 1410f ? Navigator.HoldThrottle : 1f };
                if (tick >= RunIn && !dodged)
                {
                    float elapsed = (tick - RunIn) * Tick, remaining = (contactTick - tick) * Tick;
                    bool hold = elapsed < JumpModel.MinimumTime - 0.5f * Tick ||
                        (elapsed < JumpModel.MaximumHold - 0.5f * Tick && remaining > FlipModel.Lead + 1.5f * Tick);
                    if (!hold && !jumpHeld && remaining <= FlipModel.Lead + 0.5f * Tick && elapsed >= FlipModel.EarliestDodge - 0.5f * Tick)
                    {
                        (float pitch, float yaw) = DodgeModel.InputToward(ToVec(c.Physics.Forward), heading, c.Physics.Velocity.Dot(c.Physics.Forward));
                        controls = new RsbControls { Jump = 1, Pitch = pitch, Yaw = yaw };
                        dodged = true;
                    }
                    else
                        controls = new RsbControls { Jump = hold ? 1 : 0 };
                    jumpHeld = controls.Jump == 1;
                }
                else if (dodged)
                    controls = new RsbControls();
                arena.SetControls(car, controls);
                arena.Step();
                if (tick + 1 == contactTick)
                {
                    // RocketSim registers a hit a couple of steps after first contact, so timing is
                    // judged geometrically: the car's box should just reach the ball on the planned tick.
                    Vec3 b = ToVec(arena.Ball.Physics.Position);
                    gaps.Add((Pose(arena.GetCar(car)).ClosestPoint(b) - b).Length() - RL.BallRadius);
                }
                if (arena.GetCar(car).LastHitTick != hitBefore)
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) continue;
            touched++;
            // Let the contact finish, then undo the gravity accumulated since.
            for (int after = 0; after < 3; after++) { arena.SetControls(car, new RsbControls()); arena.Step(); }
            Vec3 actual = ToVec(arena.Ball.Physics.Velocity) - new Vec3(0f, 0f, RL.Gravity * 3 * Tick);
            Vec3 predicted = contact.BallVelocity;
            direction.Add(Math.Abs(GroundModel.SignedAngle(predicted.Flatten(), actual.Flatten())) * 180 / Math.PI);
            speedError.Add((actual.Length() - predicted.Length()) / MathF.Max(predicted.Length(), 1f));
        }
        var inv = CultureInfo.InvariantCulture;
        var absoluteSpeed = speedError.Select(Math.Abs).ToList();
        Console.WriteLine(string.Create(inv,
            $"flip: touched {touched}/{planned}; box-to-ball gap on the planned contact tick p50 {Percentile(gaps, 0.5):F1} uu p90 {Percentile(gaps, 0.9):F1}; " +
            $"flat direction error p50 {Percentile(direction, 0.5):F1} deg p90 {Percentile(direction, 0.9):F1}; speed error p50 {Percentile(absoluteSpeed, 0.5):P1} p90 {Percentile(absoluteSpeed, 0.9):P1}, bias {(speedError.Count > 0 ? speedError.Average() : 0):P1}"));
        bool ok = planned > 0 && touched >= 0.9 * planned && Percentile(direction, 0.5) < 4 && Percentile(direction, 0.9) < 12 &&
            Percentile(absoluteSpeed, 0.5) < 0.08 && Percentile(gaps.Select(Math.Abs), 0.9) < 25;
        Console.WriteLine(ok ? "flip: PASS" : "flip: FAIL");
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

    /// <summary>
    /// Flip shots: a car driving straight at a ball (resting, or at the top of a bounce) either
    /// drives through it, jumps into it, or jumps and dodges forward into it. Every jump tick,
    /// hold and dodge delay is tried; the best finishes show how much a dodge adds and when.
    /// </summary>
    private static void FlipProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        const float approach = 0.9f;
        foreach (float height in new[] { 93f, 130f, 170f, 210f, 250f })
        foreach (float speed in new[] { 900f, 1300f, 1700f, 2100f })
        {
            float ballY = -3000f + speed * approach + 150f;
            // Returns (contact tick, ball velocity after the touch) for one finish, or tick -1 on a miss.
            (int Tick, Vec3 Velocity) Shot(int jumpTick, int holdTicks, int dodgeTick)
            {
                GroundCar(arena, car, speed);
                arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(3000, 0, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
                ulong hitBefore = arena.GetCar(car).LastHitTick;
                int total = (int)(approach * 120) + 60;
                for (int tick = 0; tick < total; tick++)
                {
                    // The ball appears 0.3 s before the planned touch and peaks at the touch.
                    int appear = (int)((approach - 0.3f) * 120);
                    if (tick == appear)
                    {
                        float rise = 0.3f;
                        float z0 = height > 93f ? MathF.Max(93f, height + 0.5f * RL.Gravity * rise * rise) : 93f;
                        float vz = height > 93f ? -RL.Gravity * rise : 0f;
                        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, ballY, z0), Velocity = new RsbVec(0, 0, vz), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
                    }
                    var controls = new RsbControls { Throttle = speed < 1410f ? 0.02f : 1f };
                    if (jumpTick >= 0 && tick >= jumpTick && tick < jumpTick + holdTicks) controls.Jump = 1;
                    if (dodgeTick >= 0 && tick == dodgeTick) { controls.Jump = 1; controls.Pitch = -1f; }
                    arena.SetControls(car, controls);
                    arena.Step();
                    if (arena.GetCar(car).LastHitTick != hitBefore)
                    {
                        for (int after = 0; after < 3; after++) { arena.SetControls(car, new RsbControls()); arena.Step(); }
                        return (tick, ToVec(arena.Ball.Physics.Velocity));
                    }
                }
                return (-1, Vec3.Zero);
            }

            var drive = Shot(-1, 0, -1);
            (int Tick, Vec3 Velocity, int Jump, int Hold, int Dodge) bestJump = (-1, Vec3.Zero, 0, 0, 0), bestFlip = bestJump, shortFlip = bestJump;
            int planned = (int)(approach * 120);
            for (int jump = planned - 70; jump <= planned + 6; jump += 1)
            foreach (int hold in new[] { 3, 6, 9, 12, 16, 20, 24 })
            {
                var plain = Shot(jump, hold, -1);
                if (plain.Tick >= 0 && plain.Velocity.y > bestJump.Velocity.y) bestJump = (plain.Tick, plain.Velocity, jump, hold, -1);
                for (int delay = hold + 1; delay <= 40; delay += 2)
                {
                    var flip = Shot(jump, hold, jump + delay);
                    if (flip.Tick >= 0 && flip.Tick >= jump + delay && flip.Velocity.y > bestFlip.Velocity.y)
                        bestFlip = (flip.Tick, flip.Velocity, jump, hold, jump + delay);
                    // The canonical finish: dodge 4 to 8 ticks before the touch.
                    if (flip.Tick >= 0 && flip.Tick - (jump + delay) is >= 4 and <= 8 && flip.Velocity.y > shortFlip.Velocity.y)
                        shortFlip = (flip.Tick, flip.Velocity, jump, hold, jump + delay);
                }
            }
            string Describe(Vec3 v) => string.Create(inv, $"fwd {v.y,5:F0} up {v.z,5:F0} speed {v.Length(),5:F0}");
            Console.WriteLine(string.Create(inv,
                $"ball z {height,3:F0} car {speed,4:F0} | drive {(drive.Tick >= 0 ? Describe(drive.Velocity) : "miss",-32)} | " +
                $"jump {Describe(bestJump.Velocity)} lead {(bestJump.Tick - bestJump.Jump) / 120f:F3} hold {bestJump.Hold,2} | " +
                $"flip {Describe(bestFlip.Velocity)} jump lead {(bestFlip.Tick - bestFlip.Jump) / 120f:F3} hold {bestFlip.Hold,2} dodge lead {(bestFlip.Tick - bestFlip.Dodge) / 120f:F3} | " +
                $"short {Describe(shortFlip.Velocity)} jump lead {(shortFlip.Tick - shortFlip.Jump) / 120f:F3} hold {shortFlip.Hold,2} dodge lead {(shortFlip.Tick - shortFlip.Dodge) / 120f:F3}"));
        }
    }

    /// <summary>Forward dodge after a jump: pitch, pitch rate, height and speeds tick by tick.</summary>
    private static void DodgeProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(3000, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        foreach (float speed in new[] { 1000f, 2000f })
        foreach ((int hold, int dodgeAt) in new[] { (1, 3), (6, 8), (12, 14), (24, 26), (24, 40) })
        {
            GroundCar(arena, car, speed);
            var row = new List<string>();
            for (int tick = 1; tick <= dodgeAt + 40; tick++)
            {
                var controls = new RsbControls { Throttle = 1f, Jump = tick <= hold ? 1 : 0 };
                if (tick == dodgeAt) { controls.Jump = 1; controls.Pitch = -1f; }
                arena.SetControls(car, controls);
                arena.Step();
                var c = arena.GetCar(car);
                int since = tick - dodgeAt;
                if (since >= 0 && since % 3 == 0 && since <= 36)
                {
                    float pitch = MathF.Asin(Math.Clamp(c.Physics.Forward.Z, -1f, 1f));
                    float pitchRate = c.Physics.AngularVelocity.Dot(c.Physics.Right);
                    row.Add(string.Create(inv, $"{since}:{pitch:F2}/{pitchRate:F1} z{c.Physics.Position.Z:F0} vz{c.Physics.Velocity.Z:F0} vy{c.Physics.Velocity.Y:F0}"));
                }
            }
            Console.WriteLine(string.Create(inv, $"v{speed:F0} hold {hold,2} dodge@{dodgeAt,2} | {string.Join(" ", row)}"));
        }
    }

    /// <summary>Prints the model rollout of a navigation case for policy debugging.</summary>
    /// <summary>
    /// Side-by-side closed-loop trace of the navigation policy in RocketSim and in the model from
    /// the same start (NAV_CASE=x,y,fx,fy,speed,tx,ty,dx,dy,boost[,arrival]), for diagnosing ETA errors.
    /// </summary>
    private static void NavTrace()
    {
        var inv = CultureInfo.InvariantCulture;
        string[] parts = (Environment.GetEnvironmentVariable("NAV_CASE") ?? "0,-3000,0,1,1800,1500,0,-1,0,100").Split(',');
        float[] v = parts.Select(p => float.Parse(p, inv)).ToArray();
        var forward = new Vec3(v[2], v[3], 0).Normalize();
        var target = new DriveTarget(new Vec3(v[5], v[6], 0), new Vec3(v[7], v[8], 0), v.Length > 10 ? v[10] : float.NaN);

        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var c0 = GroundCar(arena, car, v[4], MathF.Atan2(forward.y, forward.x));
        c0.Physics.Position = new RsbVec(v[0], v[1], 17.01f);
        c0.Boost = v[9];
        arena.SetCar(car, c0);
        var s = new GroundState(new Vec3(v[0], v[1], 0), forward, v[4], v[9]);

        for (int tick = 0; tick < 720; tick++)
        {
            var c = arena.GetCar(car);
            Vec3 pos = ToVec(c.Physics.Position), fwd = ToVec(c.Physics.Forward);
            float speed = c.Physics.Velocity.Dot(c.Physics.Forward);
            DriveCommand real = Navigator.Control(pos, fwd, speed, c.Boost, c.Physics.AngularVelocity.Z, target, tick / 120f);
            DriveCommand model = Navigator.Control(s.Position, s.Forward, s.Speed, s.Boost, s.YawRate, target, s.Time);
            if (tick % 12 == 0)
                Console.WriteLine(string.Create(inv,
                    $"t {tick / 120f:F2} | sim pos ({pos.x:F0},{pos.y:F0}) v {speed:F0} yaw {MathF.Atan2(fwd.y, fwd.x):F2} w {c.Physics.AngularVelocity.Z:F2} thr {real.Throttle:F2} st {real.Steer:F2} b {(real.Boost ? 1 : 0)} " +
                    $"| model pos ({s.Position.x:F0},{s.Position.y:F0}) v {s.Speed:F0} yaw {MathF.Atan2(s.Forward.y, s.Forward.x):F2} w {s.YawRate:F2} thr {model.Throttle:F2} st {model.Steer:F2} b {(model.Boost ? 1 : 0)}"));
            arena.SetControls(car, new RsbControls { Throttle = real.Throttle, Steer = real.Steer, Boost = real.Boost ? 1 : 0 });
            arena.Step();
            GroundModel.Step(ref s, model.Throttle, model.Steer, model.Boost, 1f / 120f);
            if ((target.Point - pos).Flatten().Length() < Navigator.ArrivalRadius && (target.Point - s.Position).Flatten().Length() < Navigator.ArrivalRadius)
                break;
        }
    }

    /// <summary>
    /// Braking and coasting while steering: longitudinal deceleration and yaw rate (relative to
    /// full-grip curvature times speed) over 0.1 s from straight driving at each speed.
    /// </summary>
    private static void BrakeProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        foreach (float throttle in new[] { -1f, 0f, 1f })
        foreach (float steer in new[] { 0f, 0.5f, 1f })
        {
            Console.WriteLine($"throttle {throttle}, steer {steer}: speed -> decel (uu/s^2) over 0.1 s, yaw rate at 0.1/0.2 s as a fraction of curvature*speed");
            foreach (float v0 in new[] { 300f, 600f, 900f, 1200f, 1500f, 1800f, 2100f, 2300f })
            {
                GroundCar(arena, car, v0, 0f);
                for (int tick = 0; tick < 6; tick++) { arena.SetControls(car, new RsbControls { Throttle = 0.02f }); arena.Step(); }
                var a = arena.GetCar(car);
                float va = a.Physics.Velocity.Dot(a.Physics.Forward);
                for (int tick = 0; tick < 12; tick++) { arena.SetControls(car, new RsbControls { Throttle = throttle, Steer = steer }); arena.Step(); }
                var b = arena.GetCar(car);
                float vb = b.Physics.Velocity.Dot(b.Physics.Forward);
                float w1 = b.Physics.AngularVelocity.Z;
                for (int tick = 0; tick < 12; tick++) { arena.SetControls(car, new RsbControls { Throttle = throttle, Steer = steer }); arena.Step(); }
                var d = arena.GetCar(car);
                float vd = d.Physics.Velocity.Dot(d.Physics.Forward);
                float w2 = d.Physics.AngularVelocity.Z;
                float full1 = steer > 0 ? steer * GroundModel.Curvature(vb) * vb : float.NaN;
                float full2 = steer > 0 ? steer * GroundModel.Curvature(vd) * vd : float.NaN;
                float lateral = Math.Abs(b.Physics.Velocity.Dot(b.Physics.Right));
                Console.WriteLine(string.Create(inv, $"  {va,6:F0}: decel {(va - vb) / 0.1f,6:F0} then {(vb - vd) / 0.1f,6:F0}  yaw {w1 / full1:F2} {w2 / full2:F2}  slip {lateral:F0}"));
            }
        }
    }

    /// <summary>
    /// Fast aerials from the ground: the aerial guidance flown closed-loop in RocketSim toward
    /// targets the model says it reaches, measuring the miss at the planned arrival time.
    /// </summary>
    private static readonly float AerialMargin = float.Parse(Environment.GetEnvironmentVariable("STARDUST_AERIAL_MARGIN") ?? "0.15", CultureInfo.InvariantCulture);

    private static int AerialCheck(int trials, int seed)
    {
        var random = new Random(seed + 10);
        float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
        using var arena = new SimArena();
        uint car = arena.AddCar(0);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 4500, 93), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        var misses = new List<double>();
        int planned = 0;
        bool verbose = Environment.GetEnvironmentVariable("STARDUST_CHECK_VERBOSE") == "1";
        for (int trial = 0; trial < trials; trial++)
        {
            float yaw = R(-3.1f, 3.1f);
            var s = GroundCar(arena, car, R(0, 1500), yaw);
            s.Physics.Position = new RsbVec(R(-2000, 2000), R(-3000, 3000), 17.01f);
            s.Boost = 100;
            arena.SetCar(car, s);
            // Let the suspension settle before taking off.
            for (int i = 0; i < 12; i++) { arena.SetControls(car, new RsbControls { Throttle = 0.02f }); arena.Step(); }
            s = arena.GetCar(car);
            Vec3 start = ToVec(s.Physics.Position), forward = ToVec(s.Physics.Forward);
            float bearing = yaw + R(-1.0f, 1.0f);
            Vec3 target = start + new Vec3(MathF.Cos(bearing), MathF.Sin(bearing), 0) * R(600, 2400) + new Vec3(0, 0, R(350, 1500));
            bool doubleJump = trial % 2 == 0;
            Vec3 nose = (target - start).Normalize();
            var state = new FlightState(start, ToVec(s.Physics.Velocity), ToVec(s.Physics.AngularVelocity), forward,
                ToVec(s.Physics.Right), ToVec(s.Physics.Up), s.Boost);

            float duration = float.NaN;
            for (float t = 0.6f; t <= 3.0f; t += 0.05f)
            {
                if (AerialGuidance.Simulate(state, true, t, target, nose, doubleJump).Reached) { duration = t; break; }
            }
            if (float.IsNaN(duration)) continue;
            planned++;
            duration += AerialMargin;
            AerialResult model = AerialGuidance.Simulate(state, true, duration, target, nose, doubleJump);

            float since = 0f;
            bool secondDone = false;
            int ticks = (int)MathF.Round(duration * 120f);
            for (int tick = 0; tick < ticks; tick++)
            {
                var c = arena.GetCar(car);
                var live = new FlightState(ToVec(c.Physics.Position), ToVec(c.Physics.Velocity), ToVec(c.Physics.AngularVelocity),
                    ToVec(c.Physics.Forward), ToVec(c.Physics.Right), ToVec(c.Physics.Up), c.Boost);
                AerialCommand command = AerialGuidance.Control(live, duration - tick / 120f, target, nose, since, doubleJump && !secondDone);
                if (doubleJump && command.Jump && since >= AerialGuidance.SecondJumpAt) secondDone = true;
                arena.SetControls(car, new RsbControls
                {
                    Pitch = command.Input.Pitch, Yaw = command.Input.Yaw, Roll = command.Input.Roll,
                    Boost = command.Boost ? 1 : 0, Jump = command.Jump ? 1 : 0, Throttle = command.Throttle,
                });
                arena.Step();
                since += 1f / 120f;
            }
            float miss = (ToVec(arena.GetCar(car).Physics.Position) - target).Length();
            misses.Add(miss);
            if (verbose && miss > 100)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  miss {miss:F0}: target {target - start} t={duration:F2} double={doubleJump} model miss {model.Miss:F0} sim end {ToVec(arena.GetCar(car).Physics.Position) - start} model end {model.Final.Position - start}"));
        }
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(inv,
            $"aerial: {misses.Count}/{planned} flown; miss p50 {Percentile(misses, 0.5):F0} uu p90 {Percentile(misses, 0.9):F0} uu; within 60 uu {misses.Count(m => m < 60) * 100.0 / Math.Max(1, misses.Count):F0} %"));
        bool ok = misses.Count > 0 && Percentile(misses, 0.5) < 40 && Percentile(misses, 0.9) < 100;
        Console.WriteLine(ok ? "aerial: PASS" : "aerial: FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Kickoff arrival times from each spawn: boosting straight at the ball with a closing front
    /// flip, versus a speed flip first. Reports when the car first touches the ball and at what
    /// speed, to tune the kickoff (KICKOFF_DODGE=distance at which to dodge into the ball).
    /// </summary>
    private static void KickoffProbe()
    {
        var inv = CultureInfo.InvariantCulture;
        float dodgeAt = float.Parse(Environment.GetEnvironmentVariable("KICKOFF_DODGE") ?? "650", inv);
        var spawns = new (string Name, float X, float Y, float Yaw)[]
        {
            ("diagonal", -2048, -2560, MathF.PI / 4), ("off-centre", -256, -3840, MathF.PI / 2), ("back", 0, -4608, MathF.PI / 2),
        };
        foreach (var spawn in spawns)
        foreach (string style in new[] { "straight", "speedflip" })
        {
            using var arena = new SimArena();
            uint car = arena.AddCar(0);
            arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = new RsbVec(0, 0, 93.15f), Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
            var s0 = GroundCar(arena, car, 0f, spawn.Yaw);
            s0.Physics.Position = new RsbVec(spawn.X, spawn.Y, 17.01f);
            s0.Boost = 33.3f;
            arena.SetCar(car, s0);
            var timeline = new RedUtils.SpeedFlipTimeline();
            float dodgeStarted = float.NaN, flipStarted = float.NaN;
            float touch = float.NaN, touchSpeed = 0f;
            for (int tick = 0; tick < 480; tick++)
            {
                float t = tick / 120f;
                var c = arena.GetCar(car);
                Vec3 pos = ToVec(c.Physics.Position), fwd = ToVec(c.Physics.Forward), vel = ToVec(c.Physics.Velocity);
                var ball = arena.Ball;
                if ((ToVec(ball.Physics.Velocity)).Length() > 50f) { touch = t; touchSpeed = vel.Length(); break; }
                Vec3 to = (new Vec3(0, 0, 0) - pos).Flatten();
                float angle = GroundModel.SignedAngle(fwd.Flatten().Normalize(), to);
                var controls = new RsbControls { Throttle = 1, Boost = 1, Steer = Math.Clamp(3f * angle, -1, 1) };
                bool grounded = c.IsOnGround != 0;
                if (style == "speedflip" && t >= 0.12f && float.IsNaN(dodgeStarted))
                {
                    if (float.IsNaN(flipStarted)) flipStarted = t;
                    RedUtils.SpeedFlipFrame frame = timeline.Step(t, spawn.X < 0 ? 1 : -1);
                    if (!frame.Finished)
                    {
                        controls.Jump = frame.Jump ? 1 : 0;
                        controls.Pitch = frame.Pitch; controls.Yaw = frame.Yaw; controls.Roll = frame.Roll;
                        controls.Handbrake = frame.Handbrake ? 1 : 0;
                        controls.Steer = 0;
                    }
                    else flipStarted = float.PositiveInfinity;
                }
                if (float.IsNaN(dodgeStarted) && grounded && to.Length() < dodgeAt && (style == "straight" || float.IsInfinity(flipStarted)))
                    dodgeStarted = t;
                if (!float.IsNaN(dodgeStarted))
                {
                    float d = t - dodgeStarted;
                    controls.Jump = d < 0.05f || (d > 0.1f && d < 0.15f) ? 1 : 0;
                    controls.Pitch = d > 0.1f ? -1 : 0;
                }
                arena.SetControls(car, controls);
                arena.Step();
            }
            Console.WriteLine(string.Create(inv, $"{spawn.Name,-11} {style,-9}: touch at {touch:F3} s, car speed {touchSpeed:F0}"));
        }
    }

    /// <summary>Throughput of the navigation rollout that planning is built on.</summary>
    private static void RolloutBenchmark(int trials, int seed)
    {
        var random = new Random(seed + 9);
        float R(float a, float b) => a + (float)random.NextDouble() * (b - a);
        var cases = new List<(GroundState Start, DriveTarget Target)>();
        for (int i = 0; i < trials; i++)
        {
            var start = new GroundState(new Vec3(R(-2500, 2500), R(-3500, 3500), 0), GroundModel.Rotate(Vec3.X, R(-3.1f, 3.1f)), R(0, 2000), R(0, 100));
            var direction = i % 2 == 0 ? GroundModel.Rotate(Vec3.X, R(-3.1f, 3.1f)) : Vec3.Zero;
            cases.Add((start, new DriveTarget(new Vec3(R(-3000, 3000), R(-4000, 4000), 0), direction)));
        }
        foreach (var c in cases.Take(50)) Navigator.Rollout(c.Start, c.Target, 6f);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double simulated = 0;
        foreach (var c in cases) simulated += Navigator.Rollout(c.Start, c.Target, 6f).Time;
        double seconds = watch.Elapsed.TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rollout: {seconds * 1e6 / trials:F0} us per rollout, {seconds * 1e9 / (simulated * 60):F0} ns per 1/60 s step ({simulated / trials:F2} s simulated on average)"));

        // Step-size sensitivity: arrival times at 1/30 s and 1/120 s against the 1/60 s planning step.
        foreach (float dt in new[] { 1f / 30f, 1f / 120f })
        {
            var differences = new List<double>();
            foreach (var c in cases)
            {
                RolloutResult reference = Navigator.Rollout(c.Start, c.Target, 6f);
                RolloutResult other = Navigator.Rollout(c.Start, c.Target, 6f, dt: dt);
                if (reference.Arrived && other.Arrived) differences.Add(other.Time - reference.Time);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"rollout dt 1/{1f / dt:F0} vs 1/60: arrival difference p10 {Percentile(differences, 0.1):+0.000;-0.000} p50 {Percentile(differences, 0.5):+0.000;-0.000} p90 {Percentile(differences, 0.9):+0.000;-0.000} mean {differences.Average():+0.000;-0.000} s (n={differences.Count})"));
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
