using System.Reflection;
using Bot;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;

internal static class ReverseRegression
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Set(Type type, string name, object? instance, object value) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!
            .SetValue(instance, value);

    public static void Run(Action<string, Action> test)
    {
        foreach (int team in new[] { 0, 1 })
        {
            int side = team == 0 ? 1 : -1;
            Stardust World(Vec3 carPosition, Vec3 ballPosition, float reverseSpeed)
            {
                var car = new Car { Index = 0, Team = (uint)team, Location = carPosition,
                    Velocity = new Vec3(0, -side * reverseSpeed, 0),
                    Orientation = new Mat3x3(new Vec3(0, side * MathF.PI / 2, 0)),
                    IsGrounded = true, Boost = 41, LastInput = new ControllerStateT() };
                Set(typeof(Cars), "AllCars", null, new List<Car> { car });
                Set(typeof(Ball), "Location", null, ballPosition);
                Set(typeof(Ball), "Velocity", null, Vec3.Zero);
                Set(typeof(Ball), "Prediction", null, new RedUtils.BallPrediction { Slices = Array.Empty<BallSlice>() });
                var bot = new Stardust("reverse-regression");
                Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
                Set(typeof(RLBot.Manager.Bot), "Team", bot, team);
                Set(typeof(RUBot), "DeltaTime", bot, 1f / 60f);
                return bot;
            }
            void Step(Stardust bot, DefensiveDrive drive, int ticks, float targetSpeed = 0)
            {
                for (int tick = 0; tick < ticks; tick++)
                {
                    Set(typeof(Game), "Time", null, 500f + tick / 60f);
                    if (targetSpeed > 0) drive.Target += new Vec3(0, -side * targetSpeed / 60f, 0);
                    bot.Controller = new ControllerStateT();
                    drive.Run(bot);
                }
            }
            test($"reverse: team {team} sustained clear retreat starts a safe half-flip", () =>
            {
                var bot = World(new Vec3(500, side * 1200, 17), new Vec3(900, side * 3000, 100), 1200);
                var drive = new DefensiveDrive(bot.Me, bot.Me.Location + new Vec3(0, -side * 900, 0), 2250, 1350);
                Step(bot, drive, 19, 1900);
                Check(drive.MobilityAction == nameof(HalfFlip) && !drive.Interruptible,
                    "long moving reverse did not become a committed safe reorientation");
            });
            test($"reverse: team {team} nearby ball forces braking without a flip", () =>
            {
                var bot = World(new Vec3(0, -side * 3500, 17), new Vec3(0, -side * 4050, 93), 1200);
                var drive = new DefensiveDrive(bot.Me, new Vec3(0, -side * 4900, 17), 2250, 1350);
                Step(bot, drive, 2);
                Check(drive.MobilityAction == null && !bot.Controller.Jump && bot.Controller.Throttle > 0,
                    "near-ball reverse exit flipped or kept accelerating backwards toward our goal");
            });
            test($"reverse: team {team} short net correction stays grounded", () =>
            {
                var bot = World(new Vec3(0, -side * 4700, 17), new Vec3(1200, 0, 93), 150);
                var drive = new DefensiveDrive(bot.Me, new Vec3(0, -side * 4930, 17), 1700, 0, true);
                Step(bot, drive, 35);
                Check(drive.MobilityAction == null && !bot.Controller.Jump,
                    "short net correction started an aerial reorientation");
            });
            test($"reverse: team {team} fast incoming ball vetoes the half-flip", () =>
            {
                var bot = World(new Vec3(0, side * 1200, 17), new Vec3(0, side * 2800, 93), 1200);
                Set(typeof(Ball), "Velocity", null, new Vec3(0, -side * 3000, 0));
                var drive = new DefensiveDrive(bot.Me, bot.Me.Location + new Vec3(0, -side * 900, 0), 2250, 1350);
                Step(bot, drive, 19, 1900);
                Check(drive.MobilityAction == null && !bot.Controller.Jump && bot.Controller.Throttle > 0,
                    "static clearance allowed a flip through a ball entering the flight corridor");
            });
            test($"reverse: team {team} crossing ball vetoes the dodge-accelerated corridor", () =>
            {
                var bot = World(new Vec3(0, side * 1200, 17), new Vec3(side * 2300, -side * 1000, 93), 1200);
                Set(typeof(Ball), "Velocity", null, new Vec3(-side * 1916, 0, 0));
                var drive = new DefensiveDrive(bot.Me, bot.Me.Location + new Vec3(0, -side * 900, 0), 2250, 1350);
                Step(bot, drive, 19, 1900);
                Check(drive.MobilityAction == null && !bot.Controller.Jump,
                    "coasting-only clearance missed the crossing ball on the accelerated flip path");
            });
            test($"reverse: team {team} wide back-wall approach brakes instead of half-flipping", () =>
            {
                var bot = World(new Vec3(side * 2000, -side * 3200, 17), new Vec3(side * 2000, side * 1000, 93), 1200);
                var drive = new DefensiveDrive(bot.Me, bot.Me.Location + new Vec3(0, -side * 900, 0), 2250, 1350);
                Step(bot, drive, 19, 1900);
                Check(drive.MobilityAction == null && !bot.Controller.Jump && bot.Controller.Throttle > 0,
                    "the reverse exit committed through the back wall outside the goal mouth");
            });
            test($"reverse: team {team} sustainable shadow keeps facing the nearby attacker", () =>
            {
                var bot = World(new Vec3(0, side * 1000, 17), new Vec3(0, side * 1600, 93), 1200);
                var drive = new DefensiveDrive(bot.Me, bot.Me.Location + new Vec3(0, -side * 900, 0), 2250, 1350);
                Step(bot, drive, 19, 1200);
                Check(drive.MobilityAction == null && !bot.Controller.Jump && bot.Controller.Throttle < 0,
                    "a sustainable goal-facing shadow was needlessly reversed or braked");
            });
            test($"reverse: team {team} fast crossing between coarse samples vetoes reorientation", () =>
            {
                var bot = World(new Vec3(0, side * 1200, 17), new Vec3(-side * 1800, side * 500, 93), 2200);
                Set(typeof(Ball), "Velocity", null, new Vec3(side * 6000, 0, 0));
                var drive = new DefensiveDrive(bot.Me, bot.Me.Location + new Vec3(0, -side * 2200, 0), 2250, 1350);
                Step(bot, drive, 19);
                Check(drive.MobilityAction == null && !bot.Controller.Jump,
                    "the flight-clearance sampler skipped a fast ball crossing between samples");
            });
        }
    }
}
