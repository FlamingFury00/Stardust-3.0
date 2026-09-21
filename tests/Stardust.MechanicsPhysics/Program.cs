using System.Reflection;
using Bot;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;
using BallPrediction = RedUtils.BallPrediction;

int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e}"); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Set(Type type, string property, object? instance, object value) =>
    type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!
        .SetValue(instance, value);

void SetBall(Vec3 location, Vec3 velocity, BallPrediction prediction)
{
    Set(typeof(Ball), nameof(Ball.Location), null, location);
    Set(typeof(Ball), nameof(Ball.Velocity), null, velocity);
    Set(typeof(Ball), nameof(Ball.Prediction), null, prediction);
}

ProbeBot Probe(Car car, int team = 0)
{
    var bot = new ProbeBot();
    Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
    Set(typeof(RLBot.Manager.Bot), "Team", bot, team);
    Set(typeof(Cars), nameof(Cars.AllCars), null, new List<Car> { car });
    return bot;
}

Test("aerial carry: predicted vertical overshoot must not request upward boost", () =>
{
    Set(typeof(Game), nameof(Game.Time), null, 0f);
    var car = new Car
    {
        Location = new Vec3(0, 0, 600),
        Velocity = new Vec3(0, 500, 110),
        Orientation = new Mat3x3(new Vec3(0.4f, MathF.PI / 2, 0)),
        Boost = 50,
        IsGrounded = false
    };
    Vec3 location = new(0, 195, 690), velocity = new(0, 500, 0);
    var prediction = new BallPrediction
    {
        Slices = new[]
        {
            new BallSlice(0, location, velocity),
            new BallSlice(0.12f, location + velocity * 0.12f + Game.Gravity * 0.0072f,
                velocity + Game.Gravity * 0.12f)
        }
    };
    SetBall(location, velocity, prediction);
    var bot = Probe(car);
    new AerialCarry().Run(bot);
    Check(!bot.Controller.Boost,
        "air carry requested boost even though its short-horizon contact point is below the car's ballistic path");
});

Test("possession flight: identical ballistic motion needs no control acceleration", () =>
{
    var car = new Car
    {
        Location = new Vec3(100, -200, 650),
        Velocity = new Vec3(700, 120, 180),
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = false,
        Boost = 50
    };
    const float horizon = 0.18f;
    Vec3 targetPosition = car.PredictLocation(horizon);
    Vec3 targetVelocity = car.PredictVelocity(horizon);
    Vec3 acceleration = PossessionControl.FlightAtHorizon(car, targetPosition, targetVelocity, horizon);
    Check(acceleration.Length() < 1f,
        $"identical ballistic motion requested {acceleration} ({acceleration.Length():F1} uu/s^2) of control");
});

Test("ground dribble: pre-contact pressure triggers the flick window", () =>
{
    Set(typeof(Game), nameof(Game.Time), null, 0f);
    var car = new Car
    {
        Index = 0,
        Team = 0,
        Location = new Vec3(0, 0, 17),
        Velocity = new Vec3(0, 800, 0),
        Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)),
        Boost = 40,
        IsGrounded = true
    };
    Vec3 ballLocation = car.Location + car.Forward * 20 + car.Up * 150;
    SetBall(ballLocation, car.Velocity, new BallPrediction
    {
        Slices = new[] { new BallSlice(0.3f, ballLocation, car.Velocity) }
    });

    var bot = new Stardust("stardust-mechanics-regression");
    Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
    Set(typeof(RLBot.Manager.Bot), "Team", bot, 0);
    Set(typeof(Cars), nameof(Cars.AllCars), null, new List<Car> { car });
    Set(typeof(Stardust), nameof(Stardust.Situation), bot, new TacticalFrame
    {
        MyEta = 0.05f,
        OpponentEta = 2f,
        PressureTime = 0.4f,
        FirstMan = 0,
        TeamRank = 0,
        TeamCount = 1
    });

    var dribble = new GroundDribble();
    bot.Action = dribble;
    dribble.Run(bot);
    Set(typeof(Game), nameof(Game.Time), null, 0.30f);
    dribble.Run(bot);
    Check(bot.Action is ControlledFlick,
        $"imminent pre-contact pressure did not trigger flick; action is {bot.Action?.GetType().Name ?? "null"}");
});

Test("possession flight: invalid state and horizon cannot produce thrust", () =>
{
    var car = new Car { Location = new Vec3(0, 0, 650), IsGrounded = false };
    foreach (float horizon in new[] { 0f, -0.1f, 1.01f, float.NaN, float.PositiveInfinity })
        Check(PossessionControl.FlightAtHorizon(car, car.Location, Vec3.Zero, horizon).Length() == 0,
            $"invalid horizon produced thrust: {horizon}");
    Check(PossessionControl.FlightAtHorizon(car, new Vec3(float.NaN, 0, 0), Vec3.Zero, 0.1f).Length() == 0,
        "NaN target produced thrust");
    Check(PossessionControl.FlightAtHorizon(null!, car.Location, Vec3.Zero, 0.1f).Length() == 0,
        "missing car produced thrust");
});

Test("possession flight: predicted overshoot and relative speed are corrected with the right sign", () =>
{
    var car = new Car { Location = new Vec3(0, 0, 700), Velocity = new Vec3(400, 0, 200), IsGrounded = false };
    const float horizon = 0.12f;
    Vec3 p = car.PredictLocation(horizon), v = car.PredictVelocity(horizon);
    Vec3 below = PossessionControl.FlightAtHorizon(car, p - Vec3.Up * 50, v - Vec3.Up * 100, horizon);
    Vec3 above = PossessionControl.FlightAtHorizon(car, p + Vec3.Up * 50, v + Vec3.Up * 100, horizon);
    Check(below.z < -500 && above.z > 500 && (below + above).Length() < 0.1f, "relative correction is biased by gravity");
});

Test("possession flight: 1000 common position and velocity shifts preserve relative feedback", () =>
{
    var random = new Random(650120);
    for (int i = 0; i < 1000; i++)
    {
        float h = 0.05f + (float)random.NextDouble() * 0.15f;
        var car = new Car { Location = new Vec3(100, -200, 650), Velocity = new Vec3(700, 120, 180), IsGrounded = false };
        Vec3 p = car.PredictLocation(h) + new Vec3(30, -20, 40), v = car.PredictVelocity(h) + new Vec3(40, 20, -30);
        Vec3 expected = PossessionControl.FlightAtHorizon(car, p, v, h);
        Vec3 translation = new((float)random.NextDouble() * 1000, (float)random.NextDouble() * 1000, 300);
        Vec3 velocity = new(300, -100, (float)random.NextDouble() * 100);
        car.Location += translation; car.Velocity += velocity;
        Vec3 actual = PossessionControl.FlightAtHorizon(car, p + translation + velocity * h, v + velocity, h);
        Check((actual - expected).Length() < 0.01f, $"common motion changed feedback: {actual} vs {expected}");
    }
});

Test("ground dribble: a stable uncontested carry does not flick merely because time elapsed", () =>
{
    Set(typeof(Game), nameof(Game.Time), null, 0f);
    var car = new Car { Index = 0, Team = 0, Location = new Vec3(0, 0, 17),
        Velocity = new Vec3(0, 800, 0), Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)),
        Boost = 40, IsGrounded = true };
    Vec3 ball = car.Location + car.Forward * 20 + car.Up * 150;
    SetBall(ball, car.Velocity, new BallPrediction { Slices = Array.Empty<BallSlice>() });
    var bot = new Stardust("stardust-mechanics-regression");
    Set(typeof(RLBot.Manager.Bot), "Index", bot, 0); Set(typeof(RLBot.Manager.Bot), "Team", bot, 0);
    Set(typeof(Cars), nameof(Cars.AllCars), null, new List<Car> { car });
    Set(typeof(Stardust), nameof(Stardust.Situation), bot, new TacticalFrame { MyEta = 0.05f, OpponentEta = 2 });
    var action = new GroundDribble(); bot.Action = action; action.Run(bot);
    Set(typeof(Game), nameof(Game.Time), null, 0.3f); action.Run(bot);
    Check(ReferenceEquals(bot.Action, action) && !action.Finished, "uncontested possession was discarded");
});

Console.WriteLine($"MECHANICS PHYSICS RESULT: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class ProbeBot : RUBot
{
    public ProbeBot() : base("stardust-mechanics-regression") { }
    public override void Run() { }
}
