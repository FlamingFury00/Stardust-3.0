using System.Reflection;
using Bot;
using RedUtils;
using RedUtils.Math;

internal static class PressureRegression
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Set(Type type, string name, object? instance, object value) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance)!.SetValue(instance, value);

    public static void Run(Action<string, Action> test)
    {
        test("possession: both logged cushion transitions keep the ball on both teams", () =>
        {
            foreach (int team in new[] { 0, 1 })
            foreach (int fixture in new[] { 0, 1 })
            {
                float sign = team == 0 ? 1 : -1;
                Vec3 Mirror(Vec3 p) => new(p.x * sign, p.y * sign, p.z);
                Vec3 facing = fixture == 0 ? new(-0.8f, -0.6f, 0) : new(-1, 0.3f, 0);
                var car = new Car
                {
                    Index = 0, Team = (uint)team, IsGrounded = true, Boost = 13,
                    Location = Mirror(fixture == 0 ? new(-1325.8f, -4045.9f, 17) : new(-2365.4f, -4255.1f, 17)),
                    Velocity = Mirror(fixture == 0 ? new(-1157.2f, -818.2f, -88) : new(-946.4f, 273.5f, 2.9f)),
                    Orientation = new Mat3x3(new Vec3(0, MathF.Atan2(facing.y * sign, facing.x * sign), 0)),
                };
                Vec3 ball = Mirror(fixture == 0 ? new(-1297.4f, -4004.7f, 148.1f) : new(-2306.3f, -4197.1f, 152));
                Vec3 velocity = Mirror(fixture == 0 ? new(-1100.1f, -387.8f, 184.2f) : new(-925.8f, 277.8f, 55.1f));
                Set(typeof(Cars), "AllCars", null, new List<Car> { car });
                Set(typeof(Ball), "Location", null, ball);
                Set(typeof(Ball), "Velocity", null, velocity);
                Set(typeof(Ball), "Prediction", null, new BallPrediction { Slices = Array.Empty<BallSlice>() });
                var bot = new Stardust("pressure-regression");
                Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
                Set(typeof(RLBot.Manager.Bot), "Team", bot, team);
                bot.Run();
                Check(bot.Action is GroundDribble, $"team {team} fixture {fixture}: {bot.Decision}");

                var frame = bot.Situation;
                var close = Ball.MainBall;
                frame.TeamRank = 1;
                Check(!PossessionControl.CanKeepGroundControl(frame, car, close, bot.OurGoal.Location),
                    "second man stole the first man's unsettled ball");
                frame.TeamRank = 0;
                close.velocity += new Vec3(1600, 0, 0);
                Check(!PossessionControl.CanKeepGroundControl(frame, car, close, bot.OurGoal.Location),
                    "passing ball was mistaken for controlled possession");
            }
        });

        test("pressure: carrier challenge is distinct from a lost loose-ball race", () =>
        {
            foreach (int team in new[] { 0, 1 })
            {
                float sign = team == 0 ? 1 : -1;
                Vec3 goal = new(0, -5120 * sign, 0);
                var ball = new Ball(new Vec3(0, 1000 * sign, 100), new Vec3(0, -500 * sign, 0));
                var us = new Car { IsGrounded = true, Boost = 60, Location = new Vec3(0, 0, 17),
                    Velocity = new Vec3(0, 500 * sign, 0),
                    Orientation = new Mat3x3(new Vec3(0, sign * MathF.PI / 2, 0)) };
                var opponent = new Car { IsGrounded = true, Location = new Vec3(0, 1170 * sign, 17),
                    Velocity = ball.velocity,
                    Orientation = new Mat3x3(new Vec3(0, -sign * MathF.PI / 2, 0)) };
                var frame = new TacticalFrame { TeamCount = 2, TeamRank = 0, HasCover = true,
                    MyEta = 1.2f, OpponentEta = 0.05f, PressureTime = 0.08f };
                Check(!Defense.CanChallenge(frame, us, ball.location, goal), "fixture is not a lost race");
                Check(Defense.TryChallengeCarrier(frame, us, ball, new[] { opponent }, goal, out Vec3 contact),
                    "covered first man left the carrier uncontested");
                Check(Defense.IsGoalSide(us.Location, contact, goal, 50), "challenge ran behind the carrier");
                frame.HasCover = false;
                Check(!Defense.TryChallengeCarrier(frame, us, ball, new[] { opponent }, goal, out _),
                    "uncovered defender took a long speculative lunge");
                frame.HasCover = true;
                frame.TeamRank = 1;
                Check(!Defense.TryChallengeCarrier(frame, us, ball, new[] { opponent }, goal, out _),
                    "second man double committed");
                frame.TeamRank = 0;
                opponent.Location.y += 900 * sign;
                Check(!Defense.TryChallengeCarrier(frame, us, ball, new[] { opponent }, goal, out _),
                    "loose ball bypassed race safety");
                opponent.Location.y -= 900 * sign;
                ball.location.z = 210;
                ball.velocity.z = 200;
                Check(!Defense.TryChallengeCarrier(frame, us, ball, new[] { opponent }, goal, out _),
                    "ground challenge drove under an elevated rising ball");
                ball.location.z = 100;
                ball.velocity.z = 600;
                Check(!Defense.TryChallengeCarrier(frame, us, ball, new[] { opponent }, goal, out _),
                    "ground challenge chased a flick");
            }
        });
    }
}
