using System.Reflection;
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

void WithPads(List<Boost> pads, Action<Car> check)
{
    var property = typeof(Field).GetProperty(nameof(Field.Boosts))!;
    var original = Field.Boosts;
    try
    {
        property.SetValue(null, pads);
        check(new Car { Location = new Vec3(0, 0, 17), IsGrounded = true, Boost = 10,
            Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)) });
    }
    finally { property.SetValue(null, original); }
}
Boost Pad(int index, bool large = true) => new(index, new BoostPadT
{
    Location = new Vector3T { X = 0, Y = 500 + index * 100, Z = 0 }, IsFullBoost = large
});
void NoPad(GetBoost action)
{
    Check(action.Finished && action.Interruptible && action.ChosenBoost == null && action.DriveAction == null,
        "missing pad left a live or uninterruptible driving action");
    Check(action.BoostIndex == -1 && float.IsPositiveInfinity(action.Eta), "missing pad retained a usable index or ETA");
    action.Run(null!); // Cancellation must happen before touching a bot/controller.
}

Test("boost action: empty field and invalid explicit indices finish without driving", () =>
{
    WithPads(new List<Boost>(), car =>
    {
        NoPad(new GetBoost(car));
        NoPad(new GetBoost(car, Array.Empty<Boost>(), false));
    });
    WithPads(new List<Boost> { Pad(0) }, car =>
    {
        NoPad(new GetBoost(car, -2, false));
        NoPad(new GetBoost(car, 1));
    });
});

Test("boost action: unavailable candidates never fall through to an unrelated pad zero", () =>
{
    var unavailable = Pad(1);
    unavailable.Update(new BoostPadStateT { IsActive = false, Timer = 0 });
    WithPads(new List<Boost> { Pad(0, false), unavailable }, car =>
    {
        NoPad(new GetBoost(car));
        NoPad(new GetBoost(car, new[] { unavailable }));
        NoPad(new GetBoost(car, new[] { Pad(1) })); // Copied "active" flag must not override live cooldown.
        NoPad(new GetBoost(car, Array.Empty<Boost>()));
    });
});

Test("boost action: valid active and soon-respawning selections retain their route", () =>
{
    var pad = Pad(0);
    WithPads(new List<Boost> { pad }, car =>
    {
        var active = new GetBoost(car);
        Check(!active.Finished && ReferenceEquals(active.ChosenBoost, pad) && active.DriveAction.Target.FlatDist(pad.Location) < 1,
            "valid automatic refill was cancelled or redirected");
        pad.Update(new BoostPadStateT { IsActive = false, Timer = 9.9f });
        var respawning = new GetBoost(car, new[] { pad });
        Check(!respawning.Finished && ReferenceEquals(respawning.ChosenBoost, pad), "reachable respawn was rejected");
        var copied = new GetBoost(car, new[] { Pad(0) });
        Check(!copied.Finished && ReferenceEquals(copied.ChosenBoost, pad), "matching copied candidate lost its live field pad");
    });
});

Test("boost action: changed field or public index cancels before stale route execution", () =>
{
    WithPads(new List<Boost> { Pad(0), Pad(1) }, car =>
    {
        var reindexed = new GetBoost(car, 0, false);
        reindexed.BoostIndex = 1;
        reindexed.Run(null!);
        NoPad(reindexed);
        var replaced = new GetBoost(car, 0, false);
        Field.Boosts[0] = Pad(0);
        replaced.Run(null!);
        NoPad(replaced);
        var removed = new GetBoost(car, 1, false);
        Field.Boosts.Clear();
        removed.Run(null!);
        NoPad(removed);
    });
});

Test("boost: critically low support may choose a safe full pad beyond 400 uu detour", () =>
{
    var car = new Car
    {
        Location = new Vec3(2200, -2200, 17),
        Velocity = new Vec3(0, -800, 0),
        Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0)),
        IsGrounded = true,
        Boost = 5
    };
    var full = new Boost(0, new BoostPadT
    {
        Location = new Vector3T { X = 3072, Y = -4096, Z = 73 },
        IsFullBoost = true
    });
    Vec3 ball = new(0, -500, 100);
    Vec3 destination = new(500, -4000, 17);
    float detour = RoutePlanner.Detour(car.Location, full.Location, destination);
    Check(detour > 400, $"fixture detour {detour:F1} did not exceed legacy cap");
    Boost? selected = RoutePlanner.SelectBoost(car, new[] { full }, ball, destination, 0, 4f, (_, _) => 1f);
    Check(ReferenceEquals(selected, full), $"safe full pad was rejected at detour {detour:F1}");
});

Test("boost: pressure still rejects a full-pad excursion", () =>
{
    var car = new Car
    {
        Location = new Vec3(2200, -2200, 17),
        Velocity = new Vec3(0, -800, 0),
        Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0)),
        IsGrounded = true,
        Boost = 5
    };
    var full = new Boost(0, new BoostPadT
    {
        Location = new Vector3T { X = 3072, Y = -4096, Z = 73 },
        IsFullBoost = true
    });
    Boost? selected = RoutePlanner.SelectBoost(car, new[] { full }, new Vec3(0, -500, 100),
        new Vec3(500, -4000, 17), 0, 1.5f, (_, _) => 1f);
    Check(selected == null, "pressure allowed a risky full-pad excursion");
});

Test("goal return: drive exposes a handbrake safety switch", () =>
{
    System.Reflection.FieldInfo? allow = typeof(Drive).GetField("AllowHandbrake", BindingFlags.Public | BindingFlags.Instance);
    Check(allow != null && allow.FieldType == typeof(bool),
        "generic Drive cannot disable powerslide while parking in the goal mouth");
});

Test("goal return: guard speed brakes as distance collapses", () =>
{
    MethodInfo? method = typeof(Tactics).GetMethod("GuardSpeed", BindingFlags.Public | BindingFlags.Static);
    Check(method != null, "no braking-aware defensive guard speed exists");
    var car = new Car
    {
        Location = new Vec3(0, -4200, 17),
        Velocity = new Vec3(0, -1500, 0),
        Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0)),
        IsGrounded = true
    };
    float far = (float)method!.Invoke(null, new object[] { car, new Vec3(0, -4700, 17), 1800f })!;
    float near = (float)method.Invoke(null, new object[] { car, new Vec3(0, -4300, 17), 1800f })!;
    Check(near < far && near <= 900, $"near guard speed {near:F1} did not brake below far speed {far:F1}");
});

Test("goal return: deep-net car is routed out through the mouth before parking", () =>
{
    MethodInfo? method = typeof(Tactics).GetMethod("GoalReturnTarget", BindingFlags.Public | BindingFlags.Static);
    Check(method != null, "no explicit deep-net exit waypoint exists");
    var car = new Car
    {
        Location = new Vec3(500, -5550, 17),
        Velocity = new Vec3(0, 300, 0),
        Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)),
        IsGrounded = true
    };
    Vec3 target = (Vec3)method!.Invoke(null, new object[] { car, new Vec3(-600, -4400, 17), new Vec3(0, -5120, 0) })!;
    Check(target.y > -5120 && target.y < -4500, $"deep-net waypoint did not exit goal: {target}");
    Check(MathF.Abs(target.x) < Goal.Width / 2 - 100, $"exit waypoint is too close to a post: {target}");
});

Test("defensive turn: goalward lateral slip is not treated as a stationary aligned car", () =>
{
    foreach (int side in new[] { -1, 1 })
    {
        var car = new Car { Location = new Vec3(0, side * 4700, 17), Velocity = new Vec3(80, side * 1300, 0),
            Orientation = new Mat3x3(Vec3.Zero), IsGrounded = true, Boost = 45 };
        Vec3 target = new(1500, side * 4750, 17), goal = new(0, side * 5120, 0);
        Check(DefensiveDrive.TurnSpeedLimit(car, target, goal, 2000) <= 200,
            "sideways momentum cleared a full-speed sweep through the goal line");
    }
});

Console.WriteLine($"BOOST/GOAL RESULT: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;
