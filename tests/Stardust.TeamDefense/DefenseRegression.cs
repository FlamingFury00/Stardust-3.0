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

        test("log-v13: own-half diagonal geometry cannot fake goal-side", () =>
        {
            Vec3 car = new(-239.9f, -608.4f, 17f);
            Vec3 ball = new(1963.9f, -1685.9f, 91.5f);

            Check(Defense.IsGoalSide(car, ball, blueGoal),
                "fixture stopped reproducing the diagonal false positive");
            Check(!Defense.IsTacticallyGoalSide(car, ball, blueGoal, 10f),
                "car 1077 uu upfield of an own-half ball was still treated as goal-side");
        });

        test("log-v13: imminent opponent touch is the loose-ball attack deadline", () =>
        {
            var frame = new TacticalFrame
            {
                MyEta = 0.90f,
                OpponentEta = 0.90f,
                PressureTime = 0.10f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            };

            float loose = Defense.AttackDeadline(frame, controlledPossession: false);
            Check(loose >= 0.10f && loose <= 0.30f,
                $"0.10 s pressure still allowed a late loose-ball shot: {loose}");

            float controlled = Defense.AttackDeadline(frame, controlledPossession: true);
            Check(controlled > 1.0f,
                $"controlled possession lost its continuation window: {controlled}");
        });

        test("log-v13: solo refill is blocked while the opponent owns the race", () =>
        {
            Car car = CarAt(-2609.84f, 1082f);
            Vec3 ball = new(-2485.82f, 2493.79f, 237.89f);
            var frame = new TacticalFrame
            {
                MyEta = 2.9583f,
                OpponentEta = 1.5333f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            };

            Check(!Defense.CanRefill(frame, car, ball, blueGoal, pressure: false),
                "lost 1v1 race still detoured to a pad instead of staying in the play");
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

        test("attack-v4: imminent pressure constrains loose-ball shot but not possession", () =>
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
            Check(normal < frame.OpponentEta,
                $"imminent touch still allowed the old long loose-ball horizon: {normal}");
            Check(possession > frame.OpponentEta,
                $"controlled possession lost its continuation time: {possession}");
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

        test("log-v7: close wrong-side recovery routes around the ball and forbids a recovery flip", () =>
        {
            Car car = CarAt(-679f, -2778f);
            car.Velocity = new Vec3(457f, -902f, 0f);
            Vec3 ball = new(-510f, -2963f, 154f);

            Vec3 target = Defense.RecoveryTarget(car.Location, ball, blueGoal);

            Check(target.x < ball.x - 250f,
                $"close recovery still crossed through the ball: {target}");
            Check(Defense.IsGoalSide(target, ball, blueGoal, 250f),
                $"bypass waypoint was not goal-side: {target}");
            Check(!Defense.CanFastRecover(car, ball, target, blueGoal),
                "near-ball wrong-side recovery was allowed to dodge");

            car.Location = new Vec3(2500f, 1500f, 17f);
            Vec3 safeBall = new(-1800f, -500f, 100f);
            Vec3 safeTarget = new(2200f, -2600f, 17f);
            Check(Defense.CanFastRecover(car, safeBall, safeTarget, blueGoal),
                "clear long recovery corridor was denied fast travel");
        });

        test("log-v7: elevated goal-line crossing triggers a jump while a low crossing stays grounded", () =>
        {
            Car car = CarAt(0f, -5060f);
            car.Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0));
            var bot = World(new Vec3(0f, -4900f, 400f), car);
            Set(typeof(RUBot), nameof(RUBot.DeltaTime), bot, 1f / 120f);
            Set(typeof(Game), nameof(Game.Time), null!, 10f);

            var high = new GoalLineSave(
                car, new Vec3(0f, -5120f, 442f), 10.50f);
            bot.Controller = new ControllerStateT();
            high.Run(bot);
            Check(high.Jumping && bot.Controller.Jump,
                "442 uu crossing did not start a goal-line jump");

            var low = new GoalLineSave(
                car, new Vec3(0f, -5120f, 130f), 10.50f);
            bot.Controller = new ControllerStateT();
            low.Run(bot);
            Check(!low.Jumping && !bot.Controller.Jump,
                "low goal-line crossing unnecessarily left the ground");
        });

        test("log-v7: imminent opponent-half finish outranks possession", () =>
        {
            Car car = CarAt(1880f, 1870f);
            Car defender = CarAt(800f, 4250f, 1, 1);
            var bot = World(new Vec3(1900f, 2070f, 150f), car, defender);
            Set(typeof(Game), nameof(Game.Time), null!, 10f);

            var slice = new BallSlice(
                10.40f, new Vec3(1900f, 2070f, 150f), Vec3.Zero);
            var shot = new GroundShot(car, slice, new Vec3(0f, 5120f, 120f));
            var frame = new TacticalFrame
            {
                TeamRank = 0,
                TeamCount = 1,
                MyEta = 0.25f,
                OpponentEta = 2.0f
            };

            Check(Tactics.PreferImmediateShot(bot, shot, frame),
                "0.40 s direct finish was subordinated to possession");
        });

        test("log-v7: slow blocked setup does not force a speculative boom", () =>
        {
            Car car = CarAt(0f, 500f);
            Car defender = CarAt(0f, 2500f, 1, 1);
            var bot = World(new Vec3(0f, 1000f, 150f), car, defender);
            Set(typeof(Game), nameof(Game.Time), null!, 20f);

            var slice = new BallSlice(
                21.20f, new Vec3(0f, 1000f, 150f), Vec3.Zero);
            var shot = new GroundShot(car, slice, new Vec3(0f, 5120f, 120f));
            var frame = new TacticalFrame
            {
                TeamRank = 0,
                TeamCount = 1,
                MyEta = 0.8f,
                OpponentEta = 2.2f
            };

            Check(!Tactics.PreferImmediateShot(bot, shot, frame),
                "blocked 1.2 s setup was incorrectly forced into a shot");
        });

        test("log-v9: lateral offset cannot fake emergency goal-side depth", () =>
        {
            Car car = CarAt(-894.8f, -3559.7f);
            Vec3 ball = new(1406.8f, -3886.4f, 110f);

            Check(Defense.GoalSideProgress(car.Location, ball, blueGoal) > 0f,
                "fixture no longer reproduces the diagonal false-positive");
            Check(Defense.GoalDepthProgress(car.Location, ball, blueGoal) < -300f,
                "strict depth model did not recognize the car as upfield");
            Check(!Defense.IsDepthGoalSide(car.Location, ball, blueGoal, -100f),
                "upfield lateral car was allowed to take an emergency clear");
        });

        test("log-v9: close inbound shot commits an away-from-goal emergency touch", () =>
        {
            Car car = CarAt(-755.6f, -4577.8f);
            car.Velocity = new Vec3(-83.5f, 303.7f, 0.3f);
            car.Boost = 12f;
            var bot = World(new Vec3(-768.3f, -4467.3f, 212.8f), car);
            Set(typeof(Ball), nameof(Ball.Velocity), null!,
                new Vec3(1493.8f, -711.3f, -277.8f));
            Set(typeof(Ball), nameof(Ball.Prediction), null!,
                new RedUtils.BallPrediction
                {
                    Slices = new[]
                    {
                        new BallSlice(108.525f,
                            new Vec3(-668.4f, -4515f, 192.8f),
                            new Vec3(1490f, -710f, -320f))
                    }
                });
            Set(typeof(Game), nameof(Game.Time), null!, 108.458f);
            Set(typeof(RUBot), nameof(RUBot.DeltaTime), bot, 1f / 120f);

            Check(EmergencyClear.CanStart(
                    car, Ball.MainBall, blueGoal, 1.15f),
                "225 uu / 1.15 s emergency still did not authorize a direct touch");

            var clear = new EmergencyClear(
                car, blueGoal, new Vec3(0f, 5120f, 0f));
            bot.Controller = new ControllerStateT();
            clear.Run(bot);
            Check(clear.Committed,
                "raised point-blank threat did not commit the emergency jump");

            Set(typeof(Game), nameof(Game.Time), null!, 108.466f);
            bot.Controller = new ControllerStateT();
            clear.Run(bot);

            Check(bot.Controller.Jump,
                "emergency clear committed but did not leave the ground");
            Check(Defense.ClearDirectionIsSafe(
                    clear.ClearDirection, blueGoal, 0.20f),
                $"emergency touch direction aimed back at own goal: {clear.ClearDirection}");
        });

        test("log-v9: raw emergency interceptor refuses an own-goal chase", () =>
        {
            Car car = CarAt(-894.8f, -3559.7f);
            car.Velocity = new Vec3(1200f, -1500f, 0f);
            var prediction = new RedUtils.BallPrediction
            {
                Slices = new[]
                {
                    new BallSlice(190.90f,
                        new Vec3(823.5f, -4836f, 104f),
                        new Vec3(-715f, -1208f, 0f))
                }
            };

            Check(!Defense.TryDefensiveIntercept(
                    car, prediction, blueGoal, 190.158f, 0.98f, out _),
                "raw Drive fallback still chased a future slice toward our own goal");
        });

        test("scenario-v11: defensive-third goal-side uses true field depth", () =>
        {
            Vec3 car = new(747.65f, -3389.5f, 17f);
            Vec3 ball = new(3488.76f, -4057.08f, 1209f);

            Check(Defense.IsGoalSide(car, ball, blueGoal),
                "fixture no longer reproduces diagonal goal-side false positive");
            Check(!Defense.IsTacticallyGoalSide(car, ball, blueGoal),
                "deep defense still trusted diagonal progress over field depth");
        });

        test("log-v13: near-vertical own-box JumpShot is not an attack", () =>
        {
            Car car = CarAt(731.79f, -4832.41f);
            car.Velocity = new Vec3(841.33f, -673.97f, 0.27f);
            var bot = World(new Vec3(1003.67f, -4722.9f, 102.18f), car);
            Set(typeof(Game), nameof(Game.Time), null!, 87.3917f);

            var slice = new BallSlice(
                87.7164f,
                new Vec3(1002.70f, -5022.04f, 99.97f),
                new Vec3(-3.04f, -924f, 70f));
            var shot = new JumpShot(
                car, slice, new Vec3(730.742f, 5212f, 220f));

            Check(Tactics.FlatShotAuthority(shot) < 0.52f,
                $"fixture stopped reproducing the steep shot: {Tactics.FlatShotAuthority(shot)}");
            Check(!Tactics.AttackContactIsSane(bot, slice, shot),
                "steep defensive-box pop was still accepted as an offensive commitment");
        });

        test("possession-v16: interruptible routine shot yields to controlled ground carry", () =>
        {
            Car car = CarAt(0f, 0f);
            car.Velocity = new Vec3(0f, 600f, 0f);
            car.Orientation = new Mat3x3(
                new Vec3(0f, MathF.PI / 2f, 0f));

            Vec3 ballLocation = car.Location + car.Forward * 20f + car.Up * 153f;
            Vec3 ballVelocity = car.Velocity;
            var bot = World(ballLocation, car);
            Set(typeof(Ball), nameof(Ball.Velocity), null!, ballVelocity);
            Set(typeof(Game), nameof(Game.Time), null!, 80f);

            var slice = new BallSlice(
                80.45f, ballLocation + ballVelocity * 0.45f, ballVelocity);
            Set(typeof(Ball), nameof(Ball.Prediction), null!,
                new RedUtils.BallPrediction
                {
                    Slices = new[]
                    {
                        new BallSlice(80f, ballLocation, ballVelocity),
                        slice
                    }
                });

            var routine = new GroundShot(
                car, slice, new Vec3(0f, 5212f, 240f));
            bot.Action = routine;

            Check(PossessionControl.HasControlledPossession(
                    car, Ball.MainBall),
                "fixture stopped reproducing controlled ground possession");
            Check(routine.Interruptible,
                "fixture routine shot unexpectedly became committed");

            bot.Run();

            Check(bot.Action is GroundDribble,
                $"interruptible routine shot still monopolized possession state: {bot.Action?.GetType().Name}");
            Check(bot.Decision.Contains("ground carry", StringComparison.Ordinal),
                $"ground possession takeover was not exposed: {bot.Decision}");
        });

        test("possession-v16: controlled ground ball reclaims routine shot under pressure", () =>
        {
            Car car = CarAt(0f, 0f);
            car.Velocity = new Vec3(0f, 600f, 0f);
            car.Orientation = new Mat3x3(
                new Vec3(0f, MathF.PI / 2f, 0f));

            Vec3 ballLocation = car.Location + car.Forward * 20f + car.Up * 153f;
            Vec3 ballVelocity = car.Velocity;

            Car opponent = CarAt(0f, 260f, 1, 1);
            opponent.Orientation = new Mat3x3(
                new Vec3(0f, -MathF.PI / 2f, 0f));
            opponent.Velocity = Vec3.Zero;
            opponent.LastInput = new ControllerStateT();

            var bot = World(ballLocation, car, opponent);
            Set(typeof(Ball), nameof(Ball.Velocity), null!, ballVelocity);
            Set(typeof(Game), nameof(Game.Time), null!, 82f);

            var slice = new BallSlice(
                82.40f, ballLocation + ballVelocity * 0.40f, ballVelocity);
            Set(typeof(Ball), nameof(Ball.Prediction), null!,
                new RedUtils.BallPrediction
                {
                    Slices = new[]
                    {
                        new BallSlice(82f, ballLocation, ballVelocity),
                        slice
                    }
                });

            bot.Action = new GroundShot(
                car, slice, new Vec3(0f, 5212f, 240f));

            float pressure = Tactics.OpponentPressure(
                new[] { opponent }, Ball.MainBall, blueGoal);
            Check(float.IsFinite(pressure),
                "fixture failed to create close opponent pressure");
            Check(PossessionControl.HasControlledPossession(
                    car, Ball.MainBall),
                "fixture lost roof control");

            bot.Run();

            Check(bot.Action is GroundDribble,
                $"pressure still locked controlled possession inside a routine shot: {bot.Action?.GetType().Name}");
        });

        test("possession-v16: supervisor interrupts pre-dodge JumpShot for air carry", () =>
        {
            Car car = CarAt(0f, 1500f);
            car.Location = new Vec3(0f, 1500f, 190f);
            car.Velocity = new Vec3(450f, 0f, 350f);
            car.Orientation = new Mat3x3(Vec3.Zero);
            car.IsGrounded = false;
            car.Boost = 80f;

            Vec3 ballLocation = new(170f, 1500f, 500f);
            Vec3 ballVelocity = new(300f, 0f, 120f);
            var bot = World(ballLocation, car);
            Set(typeof(Ball), nameof(Ball.Velocity), null!, ballVelocity);
            Set(typeof(Game), nameof(Game.Time), null!, 70f);
            Set(typeof(Stardust), nameof(Stardust.Situation), bot, new TacticalFrame
            {
                MyEta = 0.6f,
                OpponentEta = 1.6f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            });

            var slice = new BallSlice(
                70.45f, ballLocation + ballVelocity * 0.45f, ballVelocity);
            var shot = new JumpShot(car, slice, new Vec3(0f, 5212f, 260f));
            typeof(JumpShot).GetProperty(nameof(JumpShot.Interruptible))!
                .SetValue(shot, false);
            bot.Action = shot;

            Check(PossessionControl.CanHandoffShotToAirCarry(
                    car, Ball.MainBall, 1.6f),
                "fixture stopped reproducing air-carry handoff geometry");

            bot.Run();

            Check(bot.Action is AerialCarry,
                $"non-interruptible JumpShot still blocked carry handoff: {bot.Action?.GetType().Name}");
            Check(bot.Decision.Contains("aerial carry handoff", StringComparison.Ordinal),
                $"handoff did not expose its decision: {bot.Decision}");
        });

        test("possession-v16: broad immediate shot does not break midfield control", () =>
        {
            Car car = CarAt(0f, 700f);
            car.Velocity = new Vec3(0f, 700f, 0f);
            var bot = World(new Vec3(0f, 1200f, 100f), car);
            Set(typeof(Game), nameof(Game.Time), null!, 60f);

            var routineSlice = new BallSlice(
                60.50f, new Vec3(0f, 1200f, 100f), new Vec3(0f, 300f, 0f));
            var routine = new GroundShot(
                car, routineSlice, new Vec3(0f, 5212f, 240f));
            var frame = new TacticalFrame
            {
                MyEta = 0.35f,
                OpponentEta = 1.20f,
                TeamRank = 0,
                TeamCount = 1,
                LastBack = true
            };

            Check(Tactics.PreferImmediateShot(bot, routine, frame),
                "fixture stopped reproducing the broad immediate-shot preference");
            Check(!Tactics.PreferPossessionFinish(bot, routine, frame),
                "routine offensive-half hit still qualified to break possession");

            var closeSlice = new BallSlice(
                60.50f, new Vec3(0f, 4250f, 100f), new Vec3(0f, 300f, 0f));
            var close = new GroundShot(
                car, closeSlice, new Vec3(0f, 5212f, 240f));
            Check(Tactics.PreferPossessionFinish(bot, close, frame),
                "point-blank scoring contact failed to outrank possession");
        });

        test("scenario-v11: pressured goal-mouth possession forces a clear", () =>
        {
            Car car = CarAt(1389.2f, -4957.55f);
            Ball ball = new(
                new Vec3(1344.31f, -4862.73f, 170.88f),
                new Vec3(-639.54f, 53.34f, -143.78f));
            var frame = new TacticalFrame
            {
                TeamRank = 0,
                TeamCount = 1,
                MyEta = 1.0581f,
                OpponentEta = 2.0078f,
                PressureTime = 1.1163f,
                LastBack = true
            };

            Check(Defense.ShouldForceBoxClear(frame, car, ball, blueGoal),
                "goal-line pocket still preferred pressured dribble possession");
        });

        test("log-v9: pressured own-box boom outranks soft possession", () =>
        {
            Car car = CarAt(-715.6f, -4720.2f);
            car.Velocity = new Vec3(-100f, -400f, 0f);
            var bot = World(new Vec3(-1149.2f, -4892.6f, 286.2f), car);
            Set(typeof(Game), nameof(Game.Time), null!, 106.975f);

            var slice = new BallSlice(
                107.258f,
                new Vec3(-818.7f, -4791.1f, 125.7f),
                new Vec3(1170f, 360f, -500f));
            var shot = new GroundShot(
                car, slice, new Vec3(-730.8f, 5212f, 125.7f));
            var frame = new TacticalFrame
            {
                TeamRank = 0,
                TeamCount = 1,
                MyEta = 0.20f,
                OpponentEta = 1.1f,
                PressureTime = 0.9f,
                LastBack = true
            };

            Check(Tactics.PreferDefensiveClear(bot, shot, frame),
                "0.28 s own-box clear was subordinated to catch/dribble possession");
        });

        test("log-v9: marginal own-box dribble is released under closing pressure", () =>
        {
            Car car = CarAt(-796f, -4799f);
            car.Velocity = new Vec3(0f, -300f, 0f);
            Ball ball = new(
                new Vec3(-850f, -4801f, 145f),
                new Vec3(900f, 250f, 0f));
            var frame = new TacticalFrame
            {
                TeamRank = 0,
                TeamCount = 1,
                MyEta = 0.25f,
                OpponentEta = 0.9f,
                PressureTime = 0.6f,
                LastBack = true
            };

            Check(!PossessionControl.HasControlledPossession(car, ball),
                "fixture accidentally became strong roof possession");
            Check(!PossessionControl.ShouldRetainPossession(
                    frame, car, ball, blueGoal),
                "marginal deep-box possession remained sticky under pressure");
        });

        test("scenario-v10: close counter-threat starts emergency clear before hard horizon", () =>
        {
            Car car = CarAt(-254f, -815f);
            Ball ball = new(
                new Vec3(-396f, -775f, 93f),
                new Vec3(-53f, -1542f, 10f));

            Check(EmergencyClear.CanStart(
                    car, ball, blueGoal, 4.466f),
                "179 uu goal-bound counter threat waited for the 2.5 s emergency horizon");
        });

        test("scenario-v11: low point-blank emergency stays grounded for lateral steering", () =>
        {
            Car car = CarAt(140.5f, -3242.7f);
            car.Velocity = new Vec3(-6.4f, 280.4f, 0f);
            var bot = World(
                new Vec3(-64.8f, -2846.7f, 113.5f), car);
            Set(typeof(Ball), nameof(Ball.Velocity), null!,
                new Vec3(400.3f, -2271.4f, 6.2f));
            Set(typeof(Game), nameof(Game.Time), null!, 20f);
            Set(typeof(RUBot), nameof(RUBot.DeltaTime), bot, 1f / 120f);

            var clear = new EmergencyClear(
                car, blueGoal, new Vec3(0f, 5120f, 0f));
            bot.Controller = new ControllerStateT();
            clear.Run(bot);

            Check(clear.GroundBlock,
                "z≈113 emergency did not select grounded block mode");
            Check(!clear.Committed && !bot.Controller.Jump,
                "low emergency unnecessarily jumped and surrendered steering");
        });

        test("log-v16: high emergency uses vertical second jump before any contact dodge", () =>
        {
            Car car = CarAt(-887.94f, -3395.55f);
            car.Location = new Vec3(-887.94f, -3395.55f, 24.52f);
            car.Velocity = new Vec3(-817.08f, 1284.35f, 303.84f);
            car.Orientation = new Mat3x3(
                new Vec3(-0.0099f, 2.1544f, -0.0002f));
            car.IsGrounded = false;

            var bot = World(
                new Vec3(-884.31f, -3234.36f, 323.16f), car);
            Set(typeof(Ball), nameof(Ball.Velocity), null!,
                new Vec3(176.64f, -1035.76f, -633.91f));
            Set(typeof(Game), nameof(Game.Time), null!, 35f);
            Set(typeof(RUBot), nameof(RUBot.DeltaTime), bot, 1f / 120f);

            var clear = new EmergencyClear(
                car, blueGoal, new Vec3(0f, 5120f, 0f));
            bot.Controller = new ControllerStateT();
            clear.Run(bot);

            Check(clear.Committed,
                "high close emergency did not commit immediately");
            Check(clear.NeutralSecondJump,
                "car far below high emergency did not reserve a neutral vertical second jump");
            Check(!clear.DirectionalDodgeAllowed,
                "car ~250 uu below contact armed a fieldward directional dodge");
        });

        test("scenario-v11: raised point-blank emergency jumps on the first action tick", () =>
        {
            Car car = CarAt(1638.9f, -4293.6f);
            car.Velocity = new Vec3(21f, 153.5f, 0f);
            var bot = World(
                new Vec3(1761.5f, -3951.9f, 161.8f), car);
            Set(typeof(Ball), nameof(Ball.Velocity), null!,
                new Vec3(-1448f, -832.5f, 282.2f));
            Set(typeof(Game), nameof(Game.Time), null!, 30f);
            Set(typeof(RUBot), nameof(RUBot.DeltaTime), bot, 1f / 120f);

            var clear = new EmergencyClear(
                car, blueGoal, new Vec3(0f, 5120f, 0f));
            bot.Controller = new ControllerStateT();
            clear.Run(bot);

            Check(clear.Committed && bot.Controller.Jump,
                "raised close emergency spent an extra planning frame before jumping");
            Check(!clear.GroundBlock,
                "raised emergency was incorrectly treated as a ground block");
            Check(!clear.DirectionalDodgeAllowed,
                "mid-height emergency armed an immediate directional dodge before reaching ball height");
        });

        test("scenario-v10: counter threat stages behind a future ball instead of shallow parking", () =>
        {
            Car car = CarAt(0f, -1500f);
            car.Orientation = new Mat3x3(new Vec3(0f, -MathF.PI / 2f, 0f));
            car.Velocity = new Vec3(0f, -1200f, 0f);
            car.Boost = 30f;
            var prediction = new RedUtils.BallPrediction
            {
                Slices = new[]
                {
                    new BallSlice(30.80f,
                        new Vec3(0f, -2200f, 93f),
                        new Vec3(0f, -1500f, 0f)),
                    new BallSlice(31.25f,
                        new Vec3(0f, -2875f, 93f),
                        new Vec3(0f, -1500f, 0f))
                }
            };

            Check(Defense.TryThreatStagingTarget(
                    car, prediction, blueGoal, new Vec3(0f, 5120f, 0f),
                    30f, 1.30f, out Vec3 stage, out float contact),
                "reachable future threat staging point was not found");
            Check(stage.y < -2200f,
                $"staging point was not behind the future ball: {stage}");
            Check(contact <= 1.30f,
                $"staging contact exceeded deadline: {contact}");
        });

        test("scenario-v10: distant goal-line save travels fast and airborne save stays active", () =>
        {
            Set(typeof(Game), nameof(Game.Time), null!, 40f);

            Car ground = CarAt(450f, -1773f);
            ground.Velocity = new Vec3(-128f, 274f, 0f);
            ground.Boost = 23f;
            var groundBot = World(
                new Vec3(-525f, -3300f, 93f), ground);
            var travel = new GoalLineSave(
                ground, new Vec3(-647f, -5120f, 93f), 42.46f);
            groundBot.Controller = new ControllerStateT();
            travel.Run(groundBot);
            Check(travel.FastTravel,
                "3k+ uu goal-line route still used parking mode");

            Car air = CarAt(-2211f, -4587f);
            air.Location = new Vec3(-2211f, -4587f, 113f);
            air.IsGrounded = false;
            air.Velocity = new Vec3(-1400f, -12f, 395f);
            air.Boost = 20f;
            var airBot = World(
                new Vec3(-2646f, -4599f, 464f), air);
            var aerial = new GoalLineSave(
                air, new Vec3(-5f, -5120f, 206f), 41.93f);
            airBot.Controller = new ControllerStateT();
            aerial.Run(airBot);
            Check(aerial.AirborneFlight,
                "airborne emergency save still waited passively for landing");
            Check(MathF.Abs(airBot.Controller.Pitch) +
                  MathF.Abs(airBot.Controller.Yaw) +
                  MathF.Abs(airBot.Controller.Roll) > 0.01f,
                "airborne emergency save produced no attitude command");
        });

        test("scenario-v10: near-tie at opponent goal still searches for a finish", () =>
        {
            Car car = CarAt(-1365f, 4865f);
            Ball ball = new(
                new Vec3(-1529f, 5029f, 306f),
                new Vec3(-318f, 0f, -242f));
            var frame = new TacticalFrame
            {
                TeamRank = 0,
                TeamCount = 1,
                MyEta = 0.5832f,
                OpponentEta = 0.5832f,
                LastBack = true
            };

            Check(Tactics.CanSearchAttack(
                    frame, car, ball, new Vec3(0f, 5120f, 0f),
                    canChallenge: false, controlledPossession: false),
                "opponent-box tie still suppressed shot search and forced catch");
            Check(!Tactics.CanSearchAttack(
                    frame, car, new Ball(new Vec3(0f, 0f, 100f), Vec3.Zero),
                    new Vec3(0f, 5120f, 0f),
                    canChallenge: false, controlledPossession: false),
                "near-tie shot-search exception leaked into midfield");
        });

        test("debug-v2: packaged bot enables debugger without shell flag propagation", () =>
        {
            string? oldAgent = Environment.GetEnvironmentVariable("RLBOT_AGENT_ID");
            string? oldDebug = Environment.GetEnvironmentVariable("STARDUST_DEBUG");
            string? oldOpen = Environment.GetEnvironmentVariable("STARDUST_DEBUG_OPEN");
            string? oldScenarios = Environment.GetEnvironmentVariable("STARDUST_SCENARIOS");
            string? oldDir = Environment.GetEnvironmentVariable("STARDUST_SCENARIO_DIR");
            string directory = Path.Combine(
                Path.GetTempPath(), $"stardust-debug-default-{Guid.NewGuid():N}");

            try
            {
                Environment.SetEnvironmentVariable("RLBOT_AGENT_ID", "debug-default-regression");
                Environment.SetEnvironmentVariable("STARDUST_DEBUG", null);
                Environment.SetEnvironmentVariable("STARDUST_DEBUG_OPEN", "0");
                Environment.SetEnvironmentVariable("STARDUST_SCENARIOS", null);
                Environment.SetEnvironmentVariable("STARDUST_SCENARIO_DIR", directory);

                var bot = new Stardust();
                System.Reflection.FieldInfo field = typeof(Stardust).GetField(
                    "debugDashboard", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("debug dashboard field missing");
                var dashboard = field.GetValue(bot) as StardustDebugDashboard;

                Check(dashboard?.Enabled == true,
                    "normal packaged bot still depended on STARDUST_DEBUG=1");
            }
            finally
            {
                Environment.SetEnvironmentVariable("RLBOT_AGENT_ID", oldAgent);
                Environment.SetEnvironmentVariable("STARDUST_DEBUG", oldDebug);
                Environment.SetEnvironmentVariable("STARDUST_DEBUG_OPEN", oldOpen);
                Environment.SetEnvironmentVariable("STARDUST_SCENARIOS", oldScenarios);
                Environment.SetEnvironmentVariable("STARDUST_SCENARIO_DIR", oldDir);
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        });

        test("debug-v1: live snapshot exposes objective, target, checks, and world state", () =>
        {
            Car car = CarAt(0, -2500);
            var bot = World(new Vec3(300, -1900, 100), car);
            bot.Action = new DefensiveDrive(
                car, new Vec3(0, -4100, 17), 2200f, 500f,
                holdPosition: false, allowDodges: true);
            Set(typeof(Stardust), nameof(Stardust.Decision), bot,
                "defend / recover behind ball");

            DebugLiveSnapshot live = StardustDebugSnapshot.CaptureLive(bot);

            Check(live.Objective == "Restore goal-side position",
                $"unexpected debugger objective: {live.Objective}");
            Check(live.Target != null && live.Target.P[1] == -4100f,
                "debugger omitted action target");
            Check(live.Checks != null && live.Cars?.Count == 1,
                "debugger omitted tactical checks or world cars");
            Check(live.Explanation.Contains("goal-side", StringComparison.Ordinal),
                "debugger omitted decision explanation");
        });

        test("debug-v1: manual and conceded captures write replayable scenario files", () =>
        {
            string directory = Path.Combine(
                Path.GetTempPath(), $"stardust-scenario-regression-{Guid.NewGuid():N}");
            try
            {
                Car car = CarAt(0, -2500);
                var bot = World(new Vec3(0, -1800, 100), car);
                var recorder = new StardustScenarioRecorder(
                    enabled: true, hz: 20f, preSeconds: 8f,
                    requestedDirectory: directory);

                Set(typeof(Game), nameof(Game.Scores), null!, new uint[] { 0, 0 });
                Set(typeof(Game), nameof(Game.Time), null!, 10f);
                recorder.OnPacket(bot);

                recorder.RequestManualSave();
                Set(typeof(Game), nameof(Game.Time), null!, 10.05f);
                recorder.OnPacket(bot);

                string[] manual = Directory.GetFiles(directory, "manual-*.json");
                Check(manual.Length == 1, "manual scenario capture was not written");
                using (var doc = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(manual[0])))
                {
                    Check(doc.RootElement.GetProperty("schema").GetInt32() == 1,
                        "scenario schema missing");
                    Check(doc.RootElement.GetProperty("frames").GetArrayLength() >= 1,
                        "scenario timeline empty");
                    Check(doc.RootElement.GetProperty("replay_frame_index").GetInt32() >= 0,
                        "scenario replay frame missing");
                }

                Set(typeof(Game), nameof(Game.Scores), null!, new uint[] { 0, 1 });
                Set(typeof(Game), nameof(Game.Time), null!, 10.10f);
                recorder.OnPacket(bot);

                string[] conceded = Directory.GetFiles(directory, "conceded-*.json");
                Check(conceded.Length == 1,
                    "opponent score increment did not auto-save a conceded scenario");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
                Set(typeof(Game), nameof(Game.Scores), null!, new uint[] { 0, 0 });
            }
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
                Check(root.GetProperty("schema").GetInt32() == 7, "telemetry schema missing");
                Check(root.TryGetProperty("build", out _), "telemetry build fingerprint missing");
                Check(root.GetProperty("controller").GetProperty("throttle").GetSingle() == 0.75f,
                    "telemetry did not capture actual sanitized controller output");
                Check(root.GetProperty("target").GetProperty("p")[1].GetSingle() == -4100f,
                    "telemetry did not capture defensive action target");
                var possession = root.GetProperty("possession");
                Check(possession.TryGetProperty("controlled", out _),
                    "telemetry omitted possession state");
                Check(possession.TryGetProperty("ground_dribble_ready", out _) &&
                      possession.TryGetProperty("air_carry_ready", out _) &&
                      possession.TryGetProperty("shot_to_air_handoff", out _),
                    "telemetry omitted possession acquisition/handoff diagnostics");
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
