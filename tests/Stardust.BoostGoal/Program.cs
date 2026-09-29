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

Car GroundCar(Vec3 location, float boost, float yaw = -MathF.PI / 2, Vec3? velocity = null, int index = 0) => new()
{
    Index = index,
    Location = location,
    Velocity = velocity ?? new Vec3(0, -800, 0),
    Orientation = new Mat3x3(new Vec3(0, yaw, 0)),
    IsGrounded = true,
    Boost = boost
};
Boost PadAt(int index, float x, float y, bool large) => new(index, new BoostPadT
{
    Location = new Vector3T { X = x, Y = y, Z = 70 }, IsFullBoost = large
});
Func<Car, Vec3, float> Straight(float speed = 1400f) => (car, point) => car.Location.FlatDist(point) / speed;

Test("boost economy: a pad is worth most to an empty tank and nothing to a full one", () =>
{
    float emptyBig = BoostEconomy.Worth(0, true), emptySmall = BoostEconomy.Worth(0, false);
    Check(emptyBig > 3 * emptySmall && emptyBig > 1.0f && emptyBig < 2.0f,
        $"empty-tank values {emptyBig:F2}/{emptySmall:F2} s are outside the measured 1-2 s per full tank");
    float previous = float.PositiveInfinity;
    for (float boost = 0; boost <= 100; boost += 10)
    {
        float worth = BoostEconomy.Worth(boost, true);
        Check(worth <= previous, $"worth rose with a fuller tank at {boost}");
        previous = worth;
    }
    Check(BoostEconomy.Worth(100, true) == 0 && BoostEconomy.Worth(100, false) == 0, "a full tank still values a pad");
    Check(BoostEconomy.Worth(94, false) < BoostEconomy.Worth(60, false),
        "a pad that only tops up 6 units was valued like a full 12");
});

Test("boost economy: slack vanishes when the opponent is on the ball and grows with time", () =>
{
    var frame = new TacticalFrame { OpponentEta = 0.05f, TeamCount = 1 };
    Check(BoostEconomy.Slack(frame, 0f, 0f, false) == 0f, "slack survived an opponent already on the ball");
    frame.OpponentEta = 3f;
    float midfield = BoostEconomy.Slack(frame, 0.5f, 0f, false);
    Check(midfield > 1.5f && midfield <= BoostEconomy.MaxSlack, $"open-ball slack {midfield:F2} s is implausible");
    float deep = BoostEconomy.Slack(frame, 0.5f, 4500f, false);
    Check(deep < midfield - 0.4f, $"a ball deep in our half left {deep:F2} s against {midfield:F2} s at midfield");
    Check(BoostEconomy.Slack(frame, 0.5f, 0f, true) < midfield, "a car out of position kept its full slack");
    Check(BoostEconomy.Slack(frame, 0.5f, 0f, false, pressureTime: 0.6f) < 0.1f,
        "an opponent about to touch did not cap the slack");
    frame.TeamCount = 2;
    frame.TeamRank = 1;
    frame.HasCover = false;
    float uncovered = BoostEconomy.Slack(frame, 0.5f, 0f, false);
    frame.HasCover = true;
    float covered = BoostEconomy.Slack(frame, 0.5f, 0f, false);
    Check(uncovered > midfield && covered > uncovered, "support slack did not follow the first man's contest");
    Check(BoostEconomy.Slack(frame, float.NaN, 0f, false) == 0f, "a nonfinite route time produced slack");
});

Test("boost economy: on-route pads are taken, off-route ones only when the slack pays for them", () =>
{
    Car car = GroundCar(new Vec3(0, -1000, 17), 20);
    Vec3 ball = new(0, 1500, 100), destination = new(0, 1500, 17);
    Boost onRoute = PadAt(0, 40, 0, false), offRoute = PadAt(1, 2400, -1000, false);
    var opponents = Array.Empty<Car>();
    Boost? pick = BoostEconomy.Choose(car, new[] { onRoute }, ball, destination, 0, 0.5f, opponents, Straight());
    Check(ReferenceEquals(pick, onRoute), "a pad on the route was not taken");
    Check(BoostEconomy.Choose(car, new[] { offRoute }, ball, destination, 0, 0.5f, opponents, Straight()) == null,
        "a 2400 uu sidetrack for 12 boost fit half a second of slack");
    Check(BoostEconomy.Choose(car, new[] { onRoute }, ball, destination, 0, 0f, opponents, Straight()) == null,
        "no slack still allowed a detour");
    Check(BoostEconomy.Choose(car, new[] { onRoute }, ball, destination, 0, 2f, opponents, (_, _) => float.NaN) == null,
        "nonfinite travel time must be rejected");
    car.Boost = 99;
    Check(BoostEconomy.Choose(car, new[] { onRoute }, ball, destination, 0, 2f, opponents, Straight()) == null,
        "a full tank drove to a pad");
});

Test("boost economy: an empty tank crosses the field for a big pad, a half tank does not", () =>
{
    Car car = GroundCar(new Vec3(2200, -2200, 17), 5);
    Boost big = PadAt(0, 3072, -4096, true);
    Vec3 ball = new(0, -500, 100), destination = new(2600, -4000, 17);
    var opponents = Array.Empty<Car>();
    Check(ReferenceEquals(BoostEconomy.Choose(car, new[] { big }, ball, destination, 0, 2f, opponents, Straight()), big),
        "a nearly empty car ignored a big pad 2 s of slack could pay for");
    car.Boost = 70;
    Check(BoostEconomy.Choose(car, new[] { big }, new Vec3(0, -500, 100), new Vec3(3000, -1500, 17), 0, 0.4f,
        opponents, Straight()) == null, "a car with 70 boost detoured for a big pad in 0.4 s of slack");
});

Test("boost economy: pads upfield of the ball, dark pads and pads the opponent reaches first are refused", () =>
{
    Car car = GroundCar(new Vec3(0, -1000, 17), 5);
    Vec3 ball = new(0, -2000, 100), destination = new(0, -1200, 17);
    Boost ahead = PadAt(0, 0, -3000, true);
    Check(BoostEconomy.Choose(car, new[] { ahead }, ball, destination, 0, 3f, Array.Empty<Car>(), Straight()) == null,
        "a pad upfield of the ball was taken as a refill");

    Boost pad = PadAt(1, 300, -800, true);
    ball = new Vec3(0, 1500, 100);
    destination = new Vec3(0, 1000, 17);
    var dark = PadAt(2, 300, -800, true);
    dark.Update(new BoostPadStateT { IsActive = false, Timer = 1f });
    Check(BoostEconomy.Choose(car, new[] { dark }, ball, destination, 0, 3f, Array.Empty<Car>(), Straight()) == null,
        "a pad dark for another nine seconds was chosen");

    Car rival = GroundCar(new Vec3(330, -800, 17), 50, index: 1);
    rival.Team = 1;
    Check(BoostEconomy.Choose(car, new[] { pad }, ball, destination, 0, 3f, new[] { rival }, Straight()) == null,
        "a pad the opponent reaches first was contested");
    Check(ReferenceEquals(BoostEconomy.Choose(car, new[] { pad }, ball, destination, 0, 3f, Array.Empty<Car>(), Straight()), pad),
        "the same pad without a rival was refused");
});

Test("boost economy: a pad being driven to is kept unless another is clearly better", () =>
{
    Car car = GroundCar(new Vec3(0, -1000, 17), 10);
    Vec3 ball = new(0, 2000, 100), destination = new(0, 1500, 17);
    Boost current = PadAt(0, 120, 0, true), rival = PadAt(1, -110, 0, true);
    var pads = new[] { current, rival };
    Boost? kept = BoostEconomy.Choose(car, pads, ball, destination, 0, 3f, Array.Empty<Car>(), Straight(), current);
    Check(ReferenceEquals(kept, current), "a pad in progress lost to an equivalent one");
    Boost? unbiased = BoostEconomy.Choose(car, pads, ball, destination, 0, 3f, Array.Empty<Car>(), Straight());
    Check(unbiased != null, "the fixture offered no pad at all");
});

Test("kickoff: only the back car of a team takes its own corner's big pad", () =>
{
    var pads = new List<Boost>
    {
        PadAt(0, 3072, -4096, true), PadAt(1, -3072, -4096, true), PadAt(2, 3072, 4096, true),
        PadAt(3, -3072, 4096, true), PadAt(4, 3584, 0, true), PadAt(5, 1788, -2300, false),
    };
    Vec3 spawn = new(2048, -2560, 17);
    Check(BoostEconomy.KickoffPad(spawn, 0, pads, 0, 3, 2800f) == null, "the taker went for a pad");
    Check(BoostEconomy.KickoffPad(spawn, 0, pads, 1, 3, 2800f) == null, "the cheat car of three went for a pad");
    Boost? corner = BoostEconomy.KickoffPad(spawn, 0, pads, 2, 3, 2800f);
    Check(corner != null && corner.Index == 0, "the back car of three did not take its own corner's pad");
    Check(BoostEconomy.KickoffPad(spawn, 0, pads, 1, 2, 2800f)?.Index == 0, "the second car of two did not take a pad");
    Check(BoostEconomy.KickoffPad(spawn, 0, pads, 1, 1, 2800f) == null, "a lone car took a pad");
    Check(BoostEconomy.KickoffPad(spawn, 0, pads, 2, 3, 1500f) == null, "a pad beyond the reach was chosen");
    Vec3 orangeSpawn = new(-2048, 2560, 17);
    Check(BoostEconomy.KickoffPad(orangeSpawn, 1, pads, 2, 3, 2800f)?.Index == 3,
        "orange's back car did not take its own corner");
    pads[0].Update(new BoostPadStateT { IsActive = false, Timer = 0 });
    Check(BoostEconomy.KickoffPad(spawn, 0, pads, 2, 3, 2800f) == null, "a taken pad was chosen");
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
