using Bot;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;

int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Near(float actual, float expected, float tolerance)
{
    Check(float.IsFinite(actual) && MathF.Abs(actual - expected) <= tolerance,
        $"expected {expected}, got {actual}");
}
Test("drive: ground boost alignment uses yaw rather than pitch", () =>
{
    Near(Drive.GroundHeadingError(0, MathF.PI / 2), MathF.PI / 2, 0.0001f);
    Near(Drive.GroundHeadingError(0.4f, 0.05f), 0.05f, 0.0001f);
});

Test("speed flip: planning speed converts boost fuel to time and stays physical", () =>
{
    float noBoost = Drive.SpeedFlipPlanningSpeed(900f, 0f);
    float thirtyBoost = Drive.SpeedFlipPlanningSpeed(900f, 30f);
    Check(noBoost >= 1399f && noBoost <= 1410f,
        $"unexpected no-boost planning speed {noBoost:F1}");
    Check(thirtyBoost > noBoost && thirtyBoost < 1900f,
        $"30 boost produced implausible planning speed {thirtyBoost:F1}");
    Check(Drive.SpeedFlipPlanningSpeed(2200f, 100f) <= Car.MaxSpeed,
        "speedflip planning exceeded max car speed");
});

Test("drive: close straight path stays finite and approximately straight", () =>
{
    var car = new Car
    {
        Location = new Vec3(0, 0, 17),
        Velocity = Vec3.Zero,
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = true,
        Boost = 0
    };
    float distance = Drive.GetDistance(car, new Vec3(100, 0, 17), false);
    Check(float.IsFinite(distance), $"straight distance was {distance}");
    Check(MathF.Abs(distance - 100) < 5, $"100 uu straight path estimated as {distance:F3} uu");
});

Test("eta: stationary car must be slower than a car already at throttle cap", () =>
{
    var stationary = new Car
    {
        Location = new Vec3(0, 0, 17),
        Velocity = Vec3.Zero,
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = true,
        Boost = 0
    };
    var fast = new Car(stationary) { Velocity = new Vec3(1400, 0, 0) };
    Vec3 target = new(3000, 0, 17);
    float fromRest = Drive.GetEta(stationary, target, false, false);
    float atSpeed = Drive.GetEta(fast, target, false, false);
    Check(float.IsFinite(fromRest) && float.IsFinite(atSpeed), $"non-finite ETA: rest={fromRest}, fast={atSpeed}");
    Check(fromRest > atSpeed + 0.35f, $"rest ETA {fromRest:F3}s should materially exceed 1400-speed ETA {atSpeed:F3}s");
});

Test("eta: short no-boost launch cannot assume instant 1400 speed", () =>
{
    var car = new Car
    {
        Location = new Vec3(0, 0, 17),
        Velocity = Vec3.Zero,
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = true,
        Boost = 0
    };
    float eta = Drive.GetEta(car, new Vec3(1000, 0, 17), false, false);
    Check(eta > 0.9f, $"1000 uu from rest reported implausible ETA {eta:F3}s");
});

Test("landing: parallel motion near a wall keeps a finite ground landing time", () =>
{
    var car = new Car
    {
        Location = new Vec3(Field.Width / 2 - 46, 0, 500),
        Velocity = new Vec3(0, 900, 0),
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = false
    };
    float time = car.PredictLandingTime();
    Check(float.IsFinite(time) && time > 0 && time < 2, $"parallel-wall landing time was {time}");
});

Test("speed flip: dropped frame cannot skip release or dodge", () =>
{
    var timeline = new SpeedFlipTimeline();
    SpeedFlipFrame first = timeline.Step(0, 1);
    SpeedFlipFrame late = timeline.Step(0.16f, 1);
    SpeedFlipFrame dodge = timeline.Step(0.32f, 1);
    Check(first.Jump && !first.Dodge, "first jump was not held");
    Check(!late.Jump && !late.Dodge, "late packet must become the observed release frame");
    Check(dodge.Jump && dodge.Dodge && dodge.Pitch < -0.9f, "dodge did not fire after the observed release");
});
Test("speed flip: duplicate release timestamp cannot manufacture a rising edge", () =>
{
    var timeline = new SpeedFlipTimeline();
    timeline.Step(0, -1);
    SpeedFlipFrame release = timeline.Step(0.11f, -1);
    SpeedFlipFrame duplicate = timeline.Step(0.11f, -1);
    SpeedFlipFrame dodge = timeline.Step(0.12f, -1);
    Check(!release.Jump && !duplicate.Jump, "release was not preserved across duplicate time");
    Check(dodge.Dodge && dodge.Jump && dodge.Roll < 0, "left-side dodge did not fire on the next advancing frame");
});
Test("speed flip: 120/60/30/15 Hz timelines all release before dodging and finish", () =>
{
    foreach (float dt in new[] { 1f / 120, 1f / 60, 1f / 30, 1f / 15 })
    {
        var timeline = new SpeedFlipTimeline();
        bool sawFirstJump = false, sawRelease = false, sawDodge = false, finished = false;
        for (float now = 0; now < 1.5f; now += dt)
        {
            SpeedFlipFrame frame = timeline.Step(now, 1);
            if (frame.Jump && !frame.Dodge && !sawRelease) sawFirstJump = true;
            if (sawFirstJump && !frame.Jump && !sawDodge) sawRelease = true;
            if (frame.Dodge)
            {
                Check(sawRelease, $"dodge preceded release at dt={dt}");
                sawDodge = true;
            }
            if (frame.Finished) { finished = true; break; }
        }
        Check(sawFirstJump && sawRelease && sawDodge && finished,
            $"incomplete speed flip at dt={dt}: jump={sawFirstJump}, release={sawRelease}, dodge={sawDodge}, finished={finished}");
    }
});

Car Runner(float speed, float boost, Vec3? at = null) => new()
{
    Index = 0, Team = 0, IsGrounded = true, Boost = boost,
    Location = at ?? new Vec3(0, -3000, 17),
    Velocity = new Vec3(0, speed, 0),
    Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)),
};
Car Runaway(Vec3 at, Vec3 velocity) => new()
{
    Index = 1, Team = 1, IsGrounded = true, Location = at, Velocity = velocity,
    Orientation = new Mat3x3(new Vec3(0, MathF.Atan2(velocity.y, velocity.x), 0)),
};

Test("demolition: advance follows the throttle curve and boost, and stops at the top speed", () =>
{
    var (distance, speed) = Demolition.Advance(1400f, 100f, 0.9f);
    Check(speed > 2250f && speed <= Car.MaxSpeed, $"boosted run reached {speed:F0} uu/s");
    Check(distance > 1500f && distance < 1900f, $"boosted run covered {distance:F0} uu");
    var (dry, dryspeed) = Demolition.Advance(1000f, 0f, 1.0f);
    Check(dryspeed < 1410f && dry < distance, $"a dry tank reached {dryspeed:F0} uu/s");
    var (none, unchanged) = Demolition.Advance(1800f, 50f, 0f);
    Check(none == 0f && unchanged == 1800f, "a zero-length advance moved the car");
});

Test("demolition: a run is planned only where supersonic contact is reachable", () =>
{
    Car car = Runner(1400f, 60f);
    Car ahead = Runaway(new Vec3(0, -1200, 17), new Vec3(0, 500, 0));
    Demolition.Run? run = Demolition.Plan(car, ahead);
    Check(run.HasValue && run.Value.Speed >= Demolition.ContactSpeed && run.Value.Time <= Demolition.MaxTime,
        "no run to an opponent 1800 uu ahead of a fast, boosted car");
    Check(Demolition.Plan(Runner(1400f, 5f), ahead) == null, "a car with 5 boost planned a run it cannot finish");
    Check(Demolition.Plan(Runner(0f, 60f, new Vec3(0, -600, 17)), Runaway(new Vec3(0, 400, 17), Vec3.Zero)) == null,
        "a standing start planned a demolition 1000 uu away");
    Car airborne = Runaway(new Vec3(0, -1200, 400), new Vec3(0, 500, 0));
    Check(Demolition.Plan(car, airborne) == null, "a flying opponent was chased with the bumper");
    Car teammate = Runaway(new Vec3(0, -1200, 17), new Vec3(0, 500, 0));
    teammate.Team = 0;
    Check(Demolition.Plan(car, teammate) == null, "a teammate was chosen as a target");
    Car behind = Runner(1400f, 60f);
    behind.Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0));
    Check(Demolition.Plan(behind, ahead) == null, "a car facing away from the opponent planned a run");
    Car wall = Runaway(new Vec3(3900, 0, 17), new Vec3(1200, 0, 0));
    Check(Demolition.Plan(car, wall) == null, "the meeting point was inside the wall");
});

Test("demolition: the opponent on the ball outranks one far from it, and a quick run outranks a slow one", () =>
{
    Vec3 ball = new(0, 500, 93), goal = new(0, -5120, 0);
    Car near = Runaway(new Vec3(100, 600, 17), new Vec3(0, -600, 0));
    Car far = Runaway(new Vec3(3000, 3000, 17), new Vec3(0, 200, 0));
    var quick = new Demolition.Run(Vec3.Zero, 0.6f, 2300f, 0f);
    var slow = new Demolition.Run(Vec3.Zero, 1.2f, 2300f, 0f);
    Check(Demolition.Value(near, ball, goal, quick) > Demolition.Value(far, ball, goal, quick),
        "an opponent far from the ball was worth as much as the carrier");
    Check(Demolition.Value(near, ball, goal, quick) > Demolition.Value(near, ball, goal, slow),
        "a slower run was worth more");
    var carrier = Runaway(new Vec3(0, 700, 17), new Vec3(0, -1500, 0));
    Check(Demolition.Value(carrier, ball, goal, quick) > Demolition.Value(near, ball, goal, quick),
        "a carrier heading for our goal was not worth more");
});

Console.WriteLine($"MOVEMENT RESULT: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class ProbeBot : RUBot
{
    public ProbeBot() : base("stardust-movement-regression") { }
    public override void Run() { }
}
