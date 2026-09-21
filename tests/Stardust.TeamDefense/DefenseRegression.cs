using System.Reflection;
using Bot;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;
using BallPrediction = RedUtils.BallPrediction;

internal static class DefenseRegression
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static void Near(float actual, float expected, float tolerance = 0.001f)
    { Check(float.IsFinite(actual) && MathF.Abs(actual - expected) <= tolerance, $"expected {expected}, got {actual}"); }
    private static void Set(Type type, string property, object? instance, object value) =>
        type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!
            .SetValue(instance, value);
    private static Car CarAt(float x, float y, int index = 0, int team = 0) => new()
    {
        Index = index, Team = (uint)team, Location = new Vec3(x, y, 17), Velocity = Vec3.Zero,
        Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)), IsGrounded = true, Boost = 30
    };
    private static Stardust World(Vec3 ball, params Car[] cars)
    {
        Set(typeof(Cars), "AllCars", null, cars.ToList());
        Set(typeof(Ball), "Location", null, ball);
        Set(typeof(Ball), "Velocity", null, Vec3.Zero);
        Set(typeof(Ball), "Prediction", null, new BallPrediction { Slices = Array.Empty<BallSlice>() });
        var bot = new Stardust("defense-regression");
        Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
        Set(typeof(RLBot.Manager.Bot), "Team", bot, (int)cars[0].Team);
        return bot;
    }

    public static void Run(Action<string, Action> test)
    {
        Vec3 goal = new(0, -5120, 0);
        test("defense-v2: solo race owner is still the goal-cover anchor", () =>
            Check(Defense.ShouldAnchor(new TacticalFrame { TeamCount = 1, TeamRank = 0 }), "solo defender was not anchor"));
        test("defense-v2: fastest last-back car keeps coverage responsibility", () =>
            Check(Defense.ShouldAnchor(new TacticalFrame { TeamCount = 3, TeamRank = 0, LastBack = true }), "ETA erased last-back duty"));
        test("defense-v2: covered second man retains a separate support role", () =>
            Check(!Defense.ShouldAnchor(new TacticalFrame { TeamCount = 3, TeamRank = 1, HasCover = true }), "all roles collapsed to anchor"));
        test("defense-v2: target is behind deep balls for both roles and teams", () =>
        {
            foreach (int side in new[] { -1, 1 })
                foreach (bool anchor in new[] { false, true })
                    foreach (float depth in new[] { 3300f, 4200f, 4700f, 5000f, 5100f })
                    {
                        Vec3 ball = new(500, side * depth, 100), g = new(0, side * 5120, 0);
                        Vec3 target = Defense.ShadowTarget(ball, g, anchor);
                        Check(Defense.IsGoalSide(target, ball, g), $"ahead of ball: side={side}, role={anchor}, ball={ball}, target={target}");
                    }
        });
        test("defense-v2: 24000 seeded field states preserve geometry and team symmetry", () =>
        {
            var random = new Random(730031);
            for (int i = 0; i < 24000; i++)
            {
                Vec3 ball = new((float)random.NextDouble() * 7600 - 3800,
                    (float)random.NextDouble() * 9100 - 5100, 100);
                bool anchor = (i & 1) == 0;
                Vec3 target = Defense.ShadowTarget(ball, goal, anchor);
                Vec3 mirrored = Defense.ShadowTarget(new Vec3(-ball.x, -ball.y, ball.z), -goal, anchor);
                Check(ControlMath.Finite(target) && target.z == 17, "invalid floor target");
                Check(Defense.IsGoalSide(target, ball, goal, 0.01f), $"goal-side invariant failed: {ball} -> {target}");
                Near(target.x, -mirrored.x, 0.005f); Near(target.y, -mirrored.y, 0.005f);
                if (target.y < -4920) Check(MathF.Abs(target.x) <= Goal.Width / 2 - 179, "post clearance violated");
            }
        });
        test("defense-v2: old depth and side thresholds no longer jump target", () =>
        {
            foreach (bool anchor in new[] { false, true })
                foreach (float x in new[] { -2000f, -250f, -150f, 0f, 150f, 250f, 2000f })
                    foreach (float depth in new[] { 1200f, 2999f, 3000f, 3001f, 4500f })
                    {
                        Vec3 a = Defense.ShadowTarget(new Vec3(x - 0.01f, -depth, 100), goal, anchor);
                        Vec3 b = Defense.ShadowTarget(new Vec3(x + 0.01f, -depth - 0.01f, 100), goal, anchor);
                        Check(a.FlatDist(b) < 2, $"discontinuous target at {x}, {depth}: {a.FlatDist(b)}");
                    }
        });
        test("defense-v2: post funnel is continuous for deep corner balls", () =>
        {
            Vec3 previous = Defense.ShadowTarget(new Vec3(3500, -4000, 100), goal, false);
            for (float depth = 4000.25f; depth <= 5100; depth += 0.25f)
            {
                Vec3 target = Defense.ShadowTarget(new Vec3(3500, -depth, 100), goal, false);
                Check(target.FlatDist(previous) < 5, $"post funnel jumped at {depth}: {previous} -> {target}");
                previous = target;
            }
        });
        test("defense-v2: malformed geometry fails to a finite guard", () =>
        {
            Check(ControlMath.Finite(Defense.ShadowTarget(new Vec3(float.NaN, 0, 0), goal, true)), "NaN target");
            Check(ControlMath.Finite(Defense.ShadowTarget(Vec3.Zero, new Vec3(0, float.NaN, 0), true)), "NaN goal");
            Check(!Defense.IsGoalSide(new Vec3(float.NaN, 0, 0), Vec3.Zero, goal), "NaN car accepted");
        });
        test("defense-v2: future inbound ball determines reference, not car position", () =>
        {
            Vec3 ball = new(200, -2000, 100);
            var prediction = new BallPrediction { Slices = new[]
            {
                new BallSlice(10, ball, new Vec3(0, -1000, 0)),
                new BallSlice(10.4f, new Vec3(200, -2400, 100), new Vec3(0, -1000, 0))
            }};
            Vec3 reference = Defense.ReferenceBall(prediction, ball, goal, 10);
            Near(reference.y, -2200, 0.01f);
            prediction.Slices[1] = new BallSlice(10.4f, new Vec3(200, -1600, 100), Vec3.Zero);
            Near(Defense.ReferenceBall(prediction, ball, goal, 10).y, -2000);
        });
        test("defense-v2: aerial and demolished teammates do not provide cover", () =>
        {
            Car car = CarAt(0, -4400); Vec3 ball = new(0, -2000, 100);
            Check(Defense.CoversGoal(car, ball, goal), "valid cover rejected");
            car.IsGrounded = false; Check(!Defense.CoversGoal(car, ball, goal), "airborne cover accepted");
            car.IsGrounded = true; car.IsDemolished = true; Check(!Defense.CoversGoal(car, ball, goal), "demolished cover accepted");
        });
        test("defense-v2: wide and outgoing teammates do not provide cover", () =>
        {
            Vec3 ball = new(0, -3500, 100); Car car = CarAt(3000, -4400);
            Check(!Defense.CoversGoal(car, ball, goal), "wide car counted as goal cover");
            car.Location = new Vec3(0, -3800, 17); car.Velocity = new Vec3(0, 2300, 0);
            Check(!Defense.CoversGoal(car, ball, goal), "outgoing momentum counted as reliable cover");
        });
        test("defense-v2: last man may challenge a winning race but not a lost one", () =>
        {
            Car car = CarAt(0, -3000); Vec3 ball = new(0, -2000, 100);
            var frame = new TacticalFrame { MyEta = 0.8f, OpponentEta = 1.2f, TeamCount = 1 };
            Check(Defense.CanChallenge(frame, car, ball, goal), "clean winning challenge blocked");
            frame.OpponentEta = 0.7f;
            Check(!Defense.CanChallenge(frame, car, ball, goal), "lost last-man race allowed");
            car.Location = new Vec3(0, -1000, 17); frame.OpponentEta = 4;
            Check(!Defense.CanChallenge(frame, car, ball, goal), "car ahead of ball allowed to attack");
        });
        test("defense-v2: contact deadline agrees with immediate-contact gate", () =>
        {
            var frame = new TacticalFrame { MyEta = 0.05f, OpponentEta = 0.06f };
            Check(Defense.AttackDeadline(frame) >= 0.08f, "immediate block cannot reach the shot search");
            frame.MyEta = 0.8f; frame.OpponentEta = 1;
            Near(Defense.AttackDeadline(frame), 0.82f);
            frame.HasCover = true; Near(Defense.AttackDeadline(frame), 1.05f);
        });
        test("defense-v2: unpressured last man cannot abandon own-half cover for boost", () =>
        {
            var frame = new TacticalFrame { TeamCount = 1, OpponentEta = 4 };
            Check(!Defense.CanRefill(frame, new Vec3(0, -1000, 100), goal, false), "last man abandoned net without explicit pressure");
            frame.HasCover = true;
            Check(Defense.CanRefill(frame, Vec3.Zero, goal, false), "covered safe refill blocked");
            Check(!Defense.CanRefill(frame, Vec3.Zero, goal, true), "pressure did not cancel refill");
        });
        test("defense-v2: stationary close dribbler creates pressure without input", () =>
        {
            Car car = CarAt(0, 200, 1, 1);
            car.Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0));
            car.LastInput = new ControllerStateT();
            Check(float.IsFinite(Tactics.OpponentPressure(new[] { car }, new Ball(new Vec3(0, 0, 100), Vec3.Zero), goal)),
                "coasting dribbler was invisible to threat model");
        });
        test("defense-v2: braking reaches zero instead of a perpetual speed floor", () =>
        {
            Car car = CarAt(0, -4400);
            Near(Defense.GuardSpeed(car, car.Location, 1800), 0);
            Near(Defense.GuardSpeed(car, new Vec3(0, -4450, 17), 1800), 0);
            float rest = Defense.GuardSpeed(car, new Vec3(0, -4900, 17), 1800);
            car.Velocity = new Vec3(0, -2000, 0);
            Check(Defense.GuardSpeed(car, new Vec3(0, -4900, 17), 1800) < rest, "reaction distance ignored");
            Near(Defense.GuardSpeed(car, car.Location, float.NaN), 0);
        });
        test("defense-v2: shallow guard is not repeatedly ejected from net", () =>
        {
            Car car = CarAt(0, -5200); Vec3 desired = new(100, -5200, 17);
            Near(Tactics.GoalReturnTarget(car, desired, goal).FlatDist(desired), 0);
            car.Location = new Vec3(0, -5550, 17);
            Check(Tactics.GoalReturnTarget(car, desired, goal).y > -5120, "deep-net exit lost");
        });
        test("defense-v2: sparse crossing uses interpolated x, z and time", () =>
        {
            var slices = new[]
            {
                new BallSlice(10, new Vec3(0, -5000, 100), Vec3.Zero),
                new BallSlice(10.2f, new Vec3(2000, -5500, 100), Vec3.Zero)
            };
            float threat = Defense.GoalThreat(slices, goal, 10, 2.5f, out Vec3 crossing);
            Near(threat, 0.048f, 0.00001f); Near(crossing.x, 480, 0.01f); Near(crossing.y, -5120);
        });
        test("defense-v2: outside-mouth crossing is not inferred from a later in-mouth slice", () =>
        {
            var slices = new[]
            {
                new BallSlice(10, new Vec3(2200, -5000, 100), Vec3.Zero),
                new BallSlice(10.2f, new Vec3(0, -5500, 100), Vec3.Zero)
            };
            Check(float.IsPositiveInfinity(Defense.GoalThreat(slices, goal, 10, 2.5f, out _)), "wide crossing became a goal");
        });
        test("defense-v2: crossing after horizon and outgoing ball are excluded", () =>
        {
            var slices = new[]
            {
                new BallSlice(10, new Vec3(0, -5000, 100), Vec3.Zero),
                new BallSlice(12, new Vec3(0, -5200, 100), Vec3.Zero)
            };
            Check(float.IsPositiveInfinity(Defense.GoalThreat(slices, goal, 10, 0.5f, out _)), "late threat included");
            Array.Reverse(slices);
            Check(float.IsPositiveInfinity(Defense.GoalThreat(slices, goal, 13, 2.5f, out _)), "historical outgoing ball included");
        });
        test("defense-v2: solo planner actually returns behind the ball", () =>
        {
            var bot = World(new Vec3(400, -4400, 100), CarAt(0, -2500), CarAt(400, -4150, 1, 1));
            bot.Run();
            var guard = bot.Action as DefensiveDrive ?? throw new Exception($"wrong action: {bot.Action?.GetType().Name}");
            Check(Defense.IsGoalSide(guard.Target, Ball.Location, goal), "planner chose upfield guard");
            Check(bot.Decision == "defend / recover goal-side", bot.Decision);
        });
        test("defense-v2: elected owner cannot preserve an upfield possession controller", () =>
        {
            var bot = World(new Vec3(0, -4400, 150), CarAt(0, -2500), CarAt(0, -4200, 1, 1));
            bot.Action = new GroundDribble(); bot.Run();
            Check(bot.Action is DefensiveDrive, "old carry survived loss of goal-side coverage");
        });
        test("defense-v2: threat during physical commit is handled immediately after release", () =>
        {
            var bot = World(new Vec3(0, -4600, 100), CarAt(0, -4200), CarAt(0, -4000, 1, 1));
            Set(typeof(Ball), "Prediction", null, new BallPrediction { Slices = new[]
                { new BallSlice(Game.Time + 0.3f, new Vec3(0, -5200, 100), Vec3.Zero) } });
            typeof(Stardust).GetField("nextPlan", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bot, Game.Time + 100);
            var committed = new CommitAction(); bot.Action = committed; bot.Run();
            Check(ReferenceEquals(bot.Action, committed), "physically committed action interrupted");
            committed.Interruptible = true; bot.Run();
            Check(bot.Decision == "defend / predicted goal" && !ReferenceEquals(bot.Action, committed), "threat transition was consumed during commit");
        });
        test("defense-v2: parked guard holds, brakes and tolerates target jitter", () =>
        {
            Car car = CarAt(0, -4800); var bot = World(new Vec3(0, -3000, 100), car);
            var action = new DefensiveDrive(car, car.Location);
            action.Run(bot);
            Check(action.Holding && !action.Finished && action.Interruptible, "guard did not persist");
            Near(bot.Controller.Throttle, 0); Near(bot.Controller.Steer, 0);
            car.Velocity = new Vec3(0, 500, 0); action.Target += new Vec3(0, 20, 0);
            action.Run(bot);
            Check(bot.Controller.Throttle < 0 && action.Holding, "jitter caused acceleration instead of braking");
            Check(!bot.Controller.Boost && !bot.Controller.Handbrake && !bot.Controller.Jump, "unsafe parking inputs");
        });
        test("defense-v2: close reverse correction does not turn a circle", () =>
        {
            Car car = CarAt(0, -4800); var bot = World(new Vec3(0, -3000, 100), car);
            var action = new DefensiveDrive(car, new Vec3(0, -5050, 17)); action.Run(bot);
            Check(bot.Controller.Throttle < 0, "behind-target correction did not reverse");
            Check(MathF.Abs(bot.Controller.Steer) < 0.1f && !bot.Controller.Handbrake && !bot.Controller.Boost, "reverse correction became an orbit");
        });
    }

    private sealed class CommitAction : IAction
    {
        public bool Finished => false;
        public bool Interruptible { get; set; }
        public void Run(RUBot bot) { }
    }
}
