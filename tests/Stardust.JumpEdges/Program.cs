using System.Reflection;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;
using RLBot.Flat;

Cars.Initialize(new GamePacketT { Players = new() });

int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void SetGameTime(float time) =>
    typeof(Game).GetProperty(nameof(Game.Time), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
        .SetValue(null, time);
void Set(Type type, string property, object instance, object value) =>
    type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!
        .SetValue(instance, value);

ProbeBot MakeBot()
{
    var bot = new ProbeBot();
    Set(typeof(RLBot.Manager.Bot), "Index", bot, 0);
    Set(typeof(RLBot.Manager.Bot), "Team", bot, 0);
    Cars.AllCars.Clear();
    Cars.AllCars.Add(new Car
    {
        Location = new Vec3(0, 0, 17),
        Velocity = new Vec3(1000, 0, 0),
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = true,
        Boost = 50
    });
    return bot;
}

float DodgeSecondPress(float dt)
{
    SetGameTime(0);
    var bot = MakeBot();
    var action = new Dodge(Vec3.X, 0.1f);
    bool sawInitialJump = false, sawRelease = false;
    for (int i = 0; i < 40; i++)
    {
        SetGameTime(i * dt);
        bot.Controller = new ControllerStateT();
        action.Run(bot);
        bool directional = MathF.Abs(bot.Controller.Pitch) + MathF.Abs(bot.Controller.Yaw) > 0.2f;
        if (bot.Controller.Jump && !directional) sawInitialJump = true;
        if (sawInitialJump && !bot.Controller.Jump) sawRelease = true;
        if (bot.Controller.Jump && directional)
        {
            Check(sawRelease, "dodge second press occurred without an observed release output");
            return i * dt;
        }
        if (i == 0) Cars.AllCars[0].IsGrounded = false;
    }
    return float.PositiveInfinity;
}

float HalfFlipSecondPress(float dt)
{
    SetGameTime(0);
    var bot = MakeBot();
    var action = new HalfFlip();
    bool sawInitialJump = false, sawRelease = false;
    for (int i = 0; i < 40; i++)
    {
        SetGameTime(i * dt);
        bot.Controller = new ControllerStateT();
        action.Run(bot);
        bool directional = MathF.Abs(bot.Controller.Pitch) > 0.5f;
        if (bot.Controller.Jump && !directional) sawInitialJump = true;
        if (sawInitialJump && !bot.Controller.Jump) sawRelease = true;
        if (bot.Controller.Jump && directional)
        {
            Check(sawRelease, "half-flip second press occurred without an observed release output");
            return i * dt;
        }
        if (i == 0) Cars.AllCars[0].IsGrounded = false;
    }
    return float.PositiveInfinity;
}

Test("dodge: one release edge is sufficient at every supported cadence", () =>
{
    foreach (float dt in new[] { 1f / 120, 1f / 60, 1f / 30, 1f / 15 })
    {
        float press = DodgeSecondPress(dt);
        float max = 0.1f + 2.1f * dt;
        Check(float.IsFinite(press) && press <= max,
            $"second press at {1 / dt:F0} Hz was {press:F4}s; expected <= {max:F4}s");
    }
});

Test("half-flip: one release edge is sufficient at every supported cadence", () =>
{
    foreach (float dt in new[] { 1f / 120, 1f / 60, 1f / 30, 1f / 15 })
    {
        float press = HalfFlipSecondPress(dt);
        float max = 0.1f + 2.1f * dt;
        Check(float.IsFinite(press) && press <= max,
            $"second press at {1 / dt:F0} Hz was {press:F4}s; expected <= {max:F4}s");
    }
});

Test("dodge: airborne invocation preserves immediate flip availability", () =>
{
    SetGameTime(0);
    var bot = MakeBot();
    Cars.AllCars[0].IsGrounded = false;
    var action = new Dodge(Vec3.X, 0.1f);
    bot.Controller = new ControllerStateT();
    action.Run(bot);
    Check(bot.Controller.Jump && MathF.Abs(bot.Controller.Pitch) + MathF.Abs(bot.Controller.Yaw) > 0.2f,
        "airborne dodge inserted an unnecessary release delay");
});

Test("dodge: mirrored car headings produce mirrored controls and the intended impulse", () =>
{
    foreach (float speed in new[] { -900f, 0f, 1000f, 2000f })
    foreach (float angle in new[] { 0.25f, 0.7854f, 1.9f, 2.6f })
    {
        var outputs = new List<ControllerStateT>();
        foreach (int mirror in new[] { -1, 1 })
        {
            SetGameTime(0);
            var bot = MakeBot();
            Car car = Cars.AllCars[0];
            car.IsGrounded = false;
            car.Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2 + mirror * angle, 0));
            car.Velocity = car.Forward * speed;
            var action = new Dodge(Vec3.Y);
            action.Run(bot);
            var controls = bot.Controller;
            Vec3 impulse = DodgeModel.Impulse(car.Forward, controls.Pitch, controls.Yaw, speed);
            Check(impulse.Normalize().Dot(Vec3.Y) > 0.999f,
                $"dodge impulse missed direction at speed {speed}, heading {angle * mirror}: {impulse}");
            outputs.Add(controls);
        }
        Check(MathF.Abs(outputs[0].Pitch - outputs[1].Pitch) < 0.0001f &&
            MathF.Abs(outputs[0].Yaw + outputs[1].Yaw) < 0.0001f,
            $"mirrored directions gave unequal controls at speed {speed}, angle {angle}");
    }
});

Console.WriteLine($"JUMP EDGE RESULT: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

sealed class ProbeBot : RUBot
{
    public ProbeBot() : base("stardust-jump-edge-regression") { }
    public override void Run() { }
}
