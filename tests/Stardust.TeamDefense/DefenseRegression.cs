using System;
using System.Linq;
using System.Reflection;
using Bot;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;

internal static class DefenseRegression
{
    private static void Check(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private static void Near(float actual, float expected, float tolerance = 0.001f)
    {
        Check(float.IsFinite(actual) && MathF.Abs(actual - expected) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    private static void Set(Type type, string property, object? instance, object value) =>
        type.GetProperty(property,
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance)!
            .SetValue(instance, value);

    private static Car CarAt(float x, float y, int index = 0, int team = 0) => new()
    {
        Index = index,
        Team = (uint)team,
        Location = new Vec3(x, y, 17),
        Velocity = Vec3.Zero,
        Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0)),
        IsGrounded = true,
        Boost = 30,
        LastInput = new ControllerStateT()
    };

    private static Stardust World(Vec3 ball, params Car[] cars)
    {
        Set(typeof(Cars), "AllCars", null!, cars.ToList());
        Set(typeof(Ball), "Location", null!, ball);
        Set(typeof(Ball), "Velocity", null!, Vec3.Zero);
        Set(typeof(Ball), "Prediction", null!,
            new RedUtils.BallPrediction { Slices = Array.Empty<BallSlice>() });

        var bot = new Stardust("defense-regression");
        Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
        Set(typeof(RLBot.Manager.Bot), "Team", bot, (int)cars[0].Team);
        return bot;
    }

    public static void Run(Action<string, Action> test)
    {
        Vec3 blueGoal = new(0, -5120, 0);

        test("defense-v3: solo defender is a moving shadow, not a parked anchor", () =>
        {
            var frame = new TacticalFrame { TeamCount = 1, TeamRank = 0, LastBack = true };
            Check(!Defense.ShouldAnchor(frame), "solo defender was converted into an anchor");

            Vec3 target = Defense.ShadowTarget(Vec3.Zero, blueGoal, DefensiveRole.Shadow);
            Check(target.y < -700 && target.y > -2600,
                $"midfield solo shadow was too passive/aggressive: {target}");
        });

        test("defense-v3: deep-ball targets are always goal-side for every role and team", () =>
        {
            foreach (int side in new[] { -1, 1 })
            {
                Vec3 goal = new(0, side * 5120, 0);
                foreach (DefensiveRole role in Enum.GetValues<DefensiveRole>())
                {
                    foreach (float x in new[] { -3000f, -1200f, 0f, 1200f, 3000f })
                    {
                        foreach (float depth in new[] { 3300f, 4200f, 4700f, 5000f })
                        {
                            Vec3 ball = new(x, side * depth, 100);
                            Vec3 target = Defense.ShadowTarget(ball, goal, role, 0.25f);
                            Check(Defense.IsGoalSide(target, ball, goal),
                                $"ahead of ball: side={side}, role={role}, ball={ball}, target={target}");
                        }
                    }
                }
            }
        });

        test("defense-v3: seeded geometry sweep preserves goal-side invariant and team symmetry", () =>
        {
            var random = new Random(730031);
            for (int i = 0; i < 12000; i++)
            {
                Vec3 ball = new(
                    (float)random.NextDouble() * 7600f - 3800f,
                    (float)random.NextDouble() * 9100f - 5100f,
                    100f);
                DefensiveRole role = (DefensiveRole)(i % 3);
                float pressure = (i & 3) == 0 ? 0.2f : float.PositiveInfinity;

                Vec3 target = Defense.ShadowTarget(ball, blueGoal, role, pressure);
                Vec3 mirrored = Defense.ShadowTarget(
                    new Vec3(-ball.x, -ball.y, ball.z),
                    -blueGoal, role, pressure);

                Check(ControlMath.Finite(target) && target.z == 17f,
                    $"invalid target: {target}");
                Check(Defense.IsGoalSide(target, ball, blueGoal, -0.01f),
                    $"goal-side invariant failed: {ball} -> {target}");
                Near(target.x, -mirrored.x, 0.01f);
                Near(target.y, -mirrored.y, 0.01f);
            }
        });

        test("defense-v3: deep anchor blends toward the far post", () =>
        {
            Vec3 blue = Defense.ShadowTarget(
                new Vec3(2000, -4000, 100), blueGoal, DefensiveRole.Anchor);
            Vec3 orange = Defense.ShadowTarget(
                new Vec3(-2000, 4000, 100), -blueGoal, DefensiveRole.Anchor);
            Check(blue.x < 0 && blue.y < -4000,
                $"blue anchor missed far-post geometry: {blue}");
            Check(orange.x > 0 && orange.y > 4000,
                $"orange anchor missed far-post geometry: {orange}");
        });

        test("defense-v3: recovery waypoint goes behind the ball on the far-post side", () =>
        {
            Vec3 ball = new(1600, -3900, 100);
            Vec3 target = Defense.RecoveryTarget(new Vec3(2200, -2200, 17), ball, blueGoal);
            Check(Defense.IsGoalSide(target, ball, blueGoal, 150f),
                $"recovery target was not behind ball: {target}");
            Check(target.x < 0,
                $"right-side attack did not recover through far/left post: {target}");

            Vec3 mirrored = Defense.RecoveryTarget(
                new Vec3(-2200, 2200, 17), new Vec3(-1600, 3900, 100), -blueGoal);
            Near(target.x, -mirrored.x, 0.01f);
            Near(target.y, -mirrored.y, 0.01f);
        });

        test("defense-v3: prior open-net fixture forces an ahead-of-ball car to recover", () =>
        {
            var bot = World(
                new Vec3(400, -4400, 100),
                CarAt(0, -2500),
                CarAt(400, -4150, 1, 1));

            bot.Run();

            var action = bot.Action as DefensiveDrive ??
                throw new Exception($"wrong action: {bot.Action?.GetType().Name}");
            Check(Defense.IsGoalSide(action.Target, Ball.Location, blueGoal, 100f),
                $"planner retained an upfield defensive target: {action.Target}");
            Check(bot.Decision == "defend / recover behind ball",
                $"unexpected decision: {bot.Decision}");
            Check(action.AllowDodges,
                "long goal-side recovery did not enable fast-travel dodges");
        });

        test("defense-v3: first man cannot attack from ahead of the ball", () =>
        {
            var frame = new TacticalFrame
            {
                MyEta = 0.3f,
                OpponentEta = 2f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            };
            Car car = CarAt(0, -1000);
            Vec3 ball = new(0, -2200, 100);
            Check(!Defense.CanChallenge(frame, car, ball, blueGoal),
                "large ETA lead overrode lost goal-side position");
        });

        test("defense-v3: last man challenges a won race but not a lost race", () =>
        {
            Car car = CarAt(0, -3000);
            Vec3 ball = new(0, -2000, 100);
            var frame = new TacticalFrame
            {
                MyEta = 0.8f,
                OpponentEta = 1.2f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            };
            Check(Defense.CanChallenge(frame, car, ball, blueGoal),
                "clean winning challenge was blocked");

            frame.OpponentEta = 0.72f;
            frame.PressureTime = 0.2f;
            Check(Defense.CanChallenge(frame, car, ball, blueGoal),
                "imminent near-tie was shadowed instead of challenged");

            frame.OpponentEta = 0.45f;
            Check(!Defense.CanChallenge(frame, car, ball, blueGoal),
                "clearly lost last-man race was allowed");
        });

        test("defense-v3: immediate controlled contact is not made artificially passive", () =>
        {
            Car car = CarAt(0, -2250);
            Vec3 ball = new(0, -2000, 100);
            var frame = new TacticalFrame
            {
                MyEta = 0.08f,
                OpponentEta = 0.06f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true,
                PressureTime = 0.05f
            };
            Check(Defense.CanChallenge(frame, car, ball, blueGoal),
                "immediate block/contact was suppressed by the safety margin");
        });

        test("defense-v3: grounded corridor teammate is cover; outgoing or wide teammate is not", () =>
        {
            Vec3 ball = new(0, -2500, 100);
            Car cover = CarAt(0, -4400, 2, 0);
            Check(Defense.CoversGoal(cover, ball, blueGoal), "valid cover rejected");

            cover.Velocity = new Vec3(0, 2300, 0);
            Check(!Defense.CoversGoal(cover, ball, blueGoal),
                "teammate leaving the corridor counted as future cover");

            cover.Velocity = Vec3.Zero;
            cover.Location = new Vec3(3000, -4400, 17);
            Check(!Defense.CoversGoal(cover, ball, blueGoal), "wide teammate counted as cover");
        });

        test("defense-v3: coasting close dribbler still creates pre-contact pressure", () =>
        {
            Car opponent = CarAt(0, -1750, 1, 1);
            opponent.Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0));
            opponent.LastInput = new ControllerStateT();
            Ball ball = new(new Vec3(0, -2000, 100), Vec3.Zero);

            float pressure = Tactics.OpponentPressure(new[] { opponent }, ball, blueGoal);
            Check(float.IsFinite(pressure) && pressure < 0.3f,
                $"controlled dribbler was invisible: {pressure}");
        });

        test("defense-v3: retreating distant opponent does not fabricate pressure", () =>
        {
            Car opponent = CarAt(0, -1000, 1, 1);
            opponent.Location = new Vec3(0, -1000, 17);
            opponent.Velocity = new Vec3(0, 900, 0);
            opponent.Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0));
            opponent.LastInput = new ControllerStateT { Throttle = 1 };
            float pressure = Tactics.OpponentPressure(
                new[] { opponent },
                new Ball(new Vec3(0, -2000, 100), Vec3.Zero),
                blueGoal);
            Check(float.IsPositiveInfinity(pressure),
                $"retreating opponent created false pressure: {pressure}");
        });

        test("defense-v3: reference ball only leads movement toward own goal", () =>
        {
            Vec3 ball = new(200, -2000, 100);
            var incoming = new RedUtils.BallPrediction
            {
                Slices = new[]
                {
                    new BallSlice(10f, ball, new Vec3(0, -1000, 0)),
                    new BallSlice(10.4f, new Vec3(200, -2400, 100), new Vec3(0, -1000, 0))
                }
            };
            Near(Defense.ReferenceBall(incoming, ball, blueGoal, 10f).y, -2200f, 0.01f);

            incoming.Slices[1] = new BallSlice(
                10.4f, new Vec3(200, -1600, 100), new Vec3(0, 1000, 0));
            Near(Defense.ReferenceBall(incoming, ball, blueGoal, 10f).y, -2000f, 0.01f);
        });

        test("defense-v3: anchor can stop while moving shadow retains terminal mobility", () =>
        {
            Car car = CarAt(0, -4500);
            Near(Defense.DriveSpeed(car, car.Location, 1800f, 0f), 0f);
            Near(Defense.DriveSpeed(car, car.Location, 1800f, 600f), 600f);

            float far = Defense.DriveSpeed(car, new Vec3(0, -5000, 17), 1800f, 0f);
            car.Velocity = new Vec3(0, -1900, 0);
            float fastClosing = Defense.DriveSpeed(car, new Vec3(0, -5000, 17), 1800f, 0f);
            Check(fastClosing < far,
                $"reaction distance did not reduce approach speed: {fastClosing} >= {far}");
        });

        test("defense-v3: shadow terminal speed tracks goalward ball motion within bounds", () =>
        {
            float stationary = Defense.ShadowTerminalSpeed(
                new Ball(new Vec3(0, -2000, 100), Vec3.Zero), blueGoal, false);
            float incoming = Defense.ShadowTerminalSpeed(
                new Ball(new Vec3(0, -2000, 100), new Vec3(0, -1400, 0)), blueGoal, true);
            Check(stationary >= 450f && stationary <= 600f,
                $"stationary terminal speed out of range: {stationary}");
            Check(incoming > stationary && incoming <= 1350f,
                $"incoming terminal speed did not scale: {incoming}");
        });

        test("defense-v3: uncovered defender cannot leave own-half shape for boost", () =>
        {
            Car support = CarAt(0, -3000, 2, 0);
            var frame = new TacticalFrame
            {
                TeamCount = 2,
                TeamRank = 1,
                OpponentEta = 4f,
                HasCover = false
            };
            Check(!Defense.CanRefill(frame, support, new Vec3(0, -1500, 100),
                    blueGoal, false),
                "own-half defense allowed an uncovered boost diversion");

            frame.HasCover = true;
            Check(Defense.CanRefill(frame, support, new Vec3(0, -1500, 100),
                    blueGoal, false),
                "covered support could not take a safe refill");
            Check(!Defense.CanRefill(frame, support, new Vec3(0, -1500, 100),
                    blueGoal, true),
                "pressure did not cancel refill");
        });

        test("defense-v3: goal crossing is interpolated in time and lateral position", () =>
        {
            var slices = new[]
            {
                new BallSlice(10f, new Vec3(0, -5000, 100), Vec3.Zero),
                new BallSlice(10.2f, new Vec3(2000, -5500, 100), Vec3.Zero)
            };
            float threat = Defense.GoalThreat(slices, blueGoal, 10f, 2.5f, out Vec3 crossing);
            Near(threat, 0.048f, 0.00001f);
            Near(crossing.x, 480f, 0.01f);
            Near(crossing.y, -5120f, 0.01f);
        });

        test("defense-v3: outside-mouth crossing is not inferred from later in-mouth sample", () =>
        {
            var slices = new[]
            {
                new BallSlice(10f, new Vec3(2200, -5000, 100), Vec3.Zero),
                new BallSlice(10.2f, new Vec3(0, -5500, 100), Vec3.Zero)
            };
            Check(float.IsPositiveInfinity(
                    Defense.GoalThreat(slices, blueGoal, 10f, 2.5f, out _)),
                "wide goal-plane crossing became a false goal threat");
        });

        test("defense-v3: shadow target changes continuously around former hard thresholds", () =>
        {
            foreach (DefensiveRole role in Enum.GetValues<DefensiveRole>())
            {
                foreach (float depth in new[] { 1200f, 2999f, 3000f, 3001f, 4200f })
                {
                    Vec3 a = Defense.ShadowTarget(
                        new Vec3(249.99f, -depth, 100), blueGoal, role, 0.5f);
                    Vec3 b = Defense.ShadowTarget(
                        new Vec3(250.01f, -depth - 0.01f, 100), blueGoal, role, 0.5f);
                    Check(a.FlatDist(b) < 5f,
                        $"target discontinuity at {role}/{depth}: {a} -> {b}");
                }
            }
        });

        test("possession-v4: controlled roof ball survives a slightly lost race", () =>
        {
            Car car = CarAt(0, -1200);
            car.Velocity = new Vec3(0, -650, 0);
            Ball ball = new(new Vec3(0, -1200, 170), car.Velocity);
            var frame = new TacticalFrame
            {
                MyEta = 0.25f,
                OpponentEta = 0.12f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true,
                PressureTime = 0.18f
            };

            Check(PossessionControl.HasControlledPossession(car, ball),
                "centered roof control was not recognized");
            Check(PossessionControl.ShouldRetainPossession(frame, car, ball, blueGoal),
                "pressure incorrectly revoked existing possession");
            Check(GroundDribble.CanStart(car, ball, frame.FreeTime),
                "ground carry refused an already-controlled ball");
        });

        test("possession-v4: blocker creates a bounded cut instead of straight-line dribbling", () =>
        {
            Car car = CarAt(0, 0);
            Ball ball = new(new Vec3(0, 0, 170), Vec3.Zero);
            Car blocker = CarAt(320, 900, 1, 1);

            Vec3 lane = PossessionControl.AttackingLane(
                car, ball, new[] { blocker }, new Vec3(0, 5120, 0));

            Check(lane.y > 0.75f, $"evasion stopped attacking progress: {lane}");
            Check(lane.x < -0.05f, $"lane did not cut away from right-side blocker: {lane}");
        });

        test("possession-v4: pressured roof possession is selected instead of shadow", () =>
        {
            Car car = CarAt(0, -1200);
            car.Velocity = new Vec3(0, -500, 0);
            Car opponent = CarAt(0, -850, 1, 1);
            opponent.Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0));

            var bot = World(new Vec3(0, -1200, 170), car, opponent);
            Set(typeof(Ball), "Velocity", null!, car.Velocity);
            bot.Run();

            Check(bot.Action is GroundDribble,
                $"controlled ball was abandoned for {bot.Action?.GetType().Name}");
            Check(bot.Decision.Contains("ground carry", StringComparison.Ordinal),
                $"unexpected possession decision: {bot.Decision}");
        });

        test("attack-v4: pressure extends commitment beyond opponent loose-ball ETA", () =>
        {
            var frame = new TacticalFrame
            {
                MyEta = 0.48f,
                OpponentEta = 0.60f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true,
                PressureTime = 0.25f
            };

            float normal = Defense.AttackDeadline(frame);
            float possession = Defense.AttackDeadline(frame, controlledPossession: true);
            Check(normal > frame.OpponentEta,
                $"pressure still shortened the attack horizon: {normal}");
            Check(possession >= normal + 0.25f,
                $"controlled possession did not receive continuation time: {possession}");
        });

        test("attack-v4: fallback challenge approaches from behind the ball", () =>
        {
            Car car = CarAt(0, -3000);
            Ball ball = new(new Vec3(0, -2200, 100), new Vec3(0, -250, 0));
            var prediction = new RedUtils.BallPrediction { Slices = Array.Empty<BallSlice>() };

            Vec3 target = Tactics.PressureChallengeTarget(
                car, prediction, ball, new Vec3(0, 5120, 0), 10f, 0.5f);

            Check(ControlMath.Finite(target), "pressure challenge target was not finite");
            Check(target.y < ball.location.y,
                $"challenge approached from the wrong side of the ball: {target}");
        });

        test("log-v5: challenge continuation survives ETA jitter but not a clearly lost race", () =>
        {
            Car car = CarAt(0, -3000);
            Vec3 ball = new(0, -2200, 100);
            var frame = new TacticalFrame
            {
                MyEta = 0.95f,
                OpponentEta = 0.70f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true,
                PressureTime = 0.20f
            };

            Check(Defense.CanContinueChallenge(frame, car, ball, blueGoal),
                "small ETA reversal cancelled an already-committed challenge");

            frame.OpponentEta = 0.40f;
            Check(!Defense.CanContinueChallenge(frame, car, ball, blueGoal),
                "clearly lost race was preserved by hysteresis");
        });

        test("log-v5: reachable emergency block is preferred from goal-side geometry", () =>
        {
            Car car = CarAt(0, -4550);
            car.Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0));
            car.Velocity = new Vec3(0, 900, 0);
            var prediction = new RedUtils.BallPrediction
            {
                Slices = new[]
                {
                    new BallSlice(10.90f, new Vec3(0, -4100, 100), new Vec3(0, -1500, 0)),
                    new BallSlice(11.20f, new Vec3(0, -4550, 100), new Vec3(0, -1500, 0))
                }
            };

            Check(Defense.TryDefensiveIntercept(
                    car, prediction, blueGoal, 10f, 1.15f, out Vec3 block),
                "reachable low-ball emergency intercept was missed");
            Check(block.y < -3950f && block.y > -4650f,
                $"unexpected emergency block point: {block}");

            car.Location = new Vec3(0, -3500, 17);
            Check(!Defense.TryDefensiveIntercept(
                    car, prediction, blueGoal, 10f, 1.15f, out _),
                "wrong-side car was allowed to attack an emergency ball");
        });

        test("log-v5: solo shadow may take a safe refill but never in the deep box", () =>
        {
            Car car = CarAt(0, -2400);
            var frame = new TacticalFrame
            {
                MyEta = 0.8f,
                OpponentEta = 2.0f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            };

            Check(Defense.CanRefill(frame, car, new Vec3(0, 300, 100), blueGoal, false),
                "1v1 rank-zero rule still disabled all intentional boost economy");
            Check(!Defense.CanRefill(frame, car, new Vec3(0, -3300, 100), blueGoal, false),
                "deep own-box ball allowed a solo refill");
            Check(!Defense.CanRefill(frame, car, new Vec3(0, 300, 100), blueGoal, true),
                "opponent pressure did not cancel solo refill");
        });

        test("log-v5: deep corner possession exits inward and suppresses goal-line flick", () =>
        {
            Car car = CarAt(1950, -4900);
            car.Velocity = new Vec3(-450, 850, 0);
            Ball ball = new(new Vec3(1950, -4900, 170), car.Velocity);

            Vec3 lane = PossessionControl.AttackingLane(
                car, ball, Array.Empty<Car>(), new Vec3(0, 5120, 0), blueGoal);

            Check(lane.y > 0.55f,
                $"deep defensive possession did not escape upfield: {lane}");
            Check(lane.x < -0.10f,
                $"deep right-corner possession did not cut inward: {lane}");
            Check(!PossessionControl.ShouldFlick(
                    car, ball, lane, 0.20f, 300f, blueGoal),
                "goal-line possession was allowed to flick across the box");
        });

        test("log-v6: long defensive recovery selects speedflip while precision guard stays grounded", () =>
        {
            Car car = CarAt(0, 0);
            car.Velocity = new Vec3(0, -900, 0);
            var bot = World(new Vec3(0, 1200, 100), car);
            Set(typeof(RUBot), nameof(RUBot.DeltaTime), bot, 0.10f);

            var recovery = new DefensiveDrive(
                car, new Vec3(0, -3500, 17), 2200f, 500f,
                holdPosition: false, allowDodges: true);
            for (int i = 0; i < 3; i++)
            {
                Set(typeof(Game), nameof(Game.Time), null!, 20f + i * 0.10f);
                bot.Controller = new ControllerStateT();
                recovery.Run(bot);
            }

            Check(recovery.MobilityAction == "SpeedFlip",
                $"long aligned recovery selected {recovery.MobilityAction ?? "no mobility action"}");

            var precise = new DefensiveDrive(
                car, new Vec3(0, -3500, 17), 2200f, 500f,
                holdPosition: false, allowDodges: false);
            for (int i = 0; i < 3; i++)
            {
                Set(typeof(Game), nameof(Game.Time), null!, 21f + i * 0.10f);
                bot.Controller = new ControllerStateT();
                precise.Run(bot);
            }

            Check(precise.MobilityAction == null,
                $"precision defense unexpectedly selected {precise.MobilityAction}");
            Check(!bot.Controller.Jump,
                "precision defense leaked a jump input");
        });

        test("telemetry: file JSONL is rate-limited and includes actual control/target", () =>
        {
            string? oldTelemetry = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY");
            string? oldHz = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY_HZ");
            string? oldFile = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY_FILE");
            string path = Path.Combine(Path.GetTempPath(),
                $"stardust-telemetry-regression-{Guid.NewGuid():N}.jsonl");

            try
            {
                Environment.SetEnvironmentVariable("STARDUST_TELEMETRY", "1");
                Environment.SetEnvironmentVariable("STARDUST_TELEMETRY_HZ", "10");
                Environment.SetEnvironmentVariable("STARDUST_TELEMETRY_FILE", path);
                Set(typeof(Game), nameof(Game.Time), null, 10f);

                var car = CarAt(0, -3000);
                var bot = World(new Vec3(0, -2000, 100), car);
                bot.Action = new DefensiveDrive(
                    car, new Vec3(0, -4100, 17), 2200f, 500f,
                    holdPosition: false, allowDodges: true);
                bot.Controller.Throttle = 0.75f;
                bot.Controller.Steer = -0.25f;

                MethodInfo hook = typeof(Stardust).GetMethod(
                    "OnOutputReady", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("post-output telemetry hook not found");

                hook.Invoke(bot, null);
                hook.Invoke(bot, null); // same timestamp: must be suppressed
                Set(typeof(Game), nameof(Game.Time), null, 10.11f);
                hook.Invoke(bot, null);

                string[] lines = File.ReadAllLines(path);
                Check(lines.Length == 2, $"10 Hz telemetry wrote {lines.Length} lines for 0.11 s");

                using var first = System.Text.Json.JsonDocument.Parse(lines[0]);
                var root = first.RootElement;
                Check(root.GetProperty("schema").GetInt32() == 4, "telemetry schema missing");
                Check(root.TryGetProperty("build", out _), "telemetry build fingerprint missing");
                Check(root.GetProperty("controller").GetProperty("throttle").GetSingle() == 0.75f,
                    "telemetry did not capture actual sanitized controller output");
                Check(root.GetProperty("target").GetProperty("p")[1].GetSingle() == -4100f,
                    "telemetry did not capture defensive action target");
                Check(root.GetProperty("possession").TryGetProperty("controlled", out _),
                    "telemetry omitted possession state");
                Check(root.GetProperty("car_state").TryGetProperty("forward", out _),
                    "telemetry omitted car orientation");
                Check(root.GetProperty("tactics").TryGetProperty("raw_can_challenge", out _),
                    "telemetry omitted raw challenge state");
                Check(root.GetProperty("tactics").TryGetProperty("can_challenge", out _),
                    "telemetry omitted committed challenge state");
                Check(root.GetProperty("action_detail").GetProperty("allow_dodges").GetBoolean(),
                    "telemetry omitted defensive fast-travel state");
                Check(root.GetProperty("action_detail").TryGetProperty("mobility_action", out _),
                    "telemetry omitted defensive mobility subaction");
            }
            finally
            {
                Environment.SetEnvironmentVariable("STARDUST_TELEMETRY", oldTelemetry);
                Environment.SetEnvironmentVariable("STARDUST_TELEMETRY_HZ", oldHz);
                Environment.SetEnvironmentVariable("STARDUST_TELEMETRY_FILE", oldFile);
            }
        });
    }
}
