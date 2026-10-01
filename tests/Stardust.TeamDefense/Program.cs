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

Test("roles: first-man comparator is a total order for all observers", () =>
{
    float[] eta = { 1.12f, 1.06f, 1.00f };
    int Winner(int observer)
    {
        int winner = observer;
        for (int i = 0; i < eta.Length; i++)
            if (i != observer && Tactics.WinsTie(eta[i], i, eta[winner], winner))
                winner = i;
        return winner;
    }
    int a = Winner(0), b = Winner(1), c = Winner(2);
    Check(a == b && b == c, $"same snapshot elected different first men: {a}, {b}, {c}");
});

Test("roles: three cars always split into one first man, one anchor and one support", () =>
{
    // Depths and ETAs chosen so that the deepest car is not the closest to the ball, and so that
    // two cars share an ETA bucket: every observer must still elect the same three jobs.
    (float Eta, float Depth)[][] snapshots =
    {
        new[] { (0.9f, 1200f), (1.4f, 3800f), (1.6f, 2200f) },
        new[] { (1.0f, 4200f), (1.02f, 1500f), (2.5f, 1490f) },
        new[] { (0.5f, 2000f), (0.5f, 2000f), (0.5f, 2000f) },
        new[] { (2.0f, -1500f), (0.4f, 900f), (1.1f, 3000f) },
    };
    foreach (var snapshot in snapshots)
    {
        int first = 0, anchor = 0, support = 0;
        for (int me = 0; me < 3; me++)
        {
            var mates = new List<Tactics.TeamMember>();
            for (int other = 0; other < 3; other++)
                if (other != me)
                    mates.Add(new Tactics.TeamMember(other, snapshot[other].Eta, snapshot[other].Depth, false));
            TacticalFrame frame = Tactics.Rank(me, snapshot[me].Eta, snapshot[me].Depth, mates);
            if (frame.TeamRank == 0) first++;
            else if (Defense.ShouldAnchor(frame)) anchor++;
            else support++;
        }
        Check(first == 1 && anchor == 1 && support == 1,
            $"roles split {first} first / {anchor} anchor / {support} support");
    }
});

Test("roles: the anchor is the deepest car that is not the first man", () =>
{
    // Car 0 is the deepest but the closest to the ball, so it is the first man; car 1 is the deeper
    // of the other two and anchors, car 2 supports.
    (float Eta, float Depth)[] cars = { (0.6f, 4000f), (1.0f, 3000f), (1.5f, 1800f) };
    TacticalFrame Frame(int me) => Tactics.Rank(me, cars[me].Eta, cars[me].Depth,
        Enumerable.Range(0, 3).Where(i => i != me)
            .Select(i => new Tactics.TeamMember(i, cars[i].Eta, cars[i].Depth, false)).ToList());
    Check(Frame(0).TeamRank == 0 && !Defense.ShouldAnchor(Frame(0)), "the closest car did not take the ball");
    Check(Defense.ShouldAnchor(Frame(1)), "the deeper car of the other two did not anchor");
    Check(!Defense.ShouldAnchor(Frame(2)) && Frame(2).TeamRank > 0, "the shallower car of the other two anchored");

    var pair = new List<Tactics.TeamMember> { new(0, 0.6f, 500f, false) };
    Check(Defense.ShouldAnchor(Tactics.Rank(1, 1.4f, 3000f, pair)), "the second car of a pair did not anchor");
    Check(!Defense.ShouldAnchor(Tactics.Rank(0, 0.6f, 500f, new List<Tactics.TeamMember> { new(1, 1.4f, 3000f, false) })),
        "the first man of a pair anchored");
});

Test("roles: support and anchor cannot collapse onto the same lane", () =>
{
    Vec3 ball = new(0, 0, 100), goal = new(0, -5120, 0);
    Vec3 support = Tactics.ShadowTarget(ball, goal, false);
    Vec3 anchor = Tactics.ShadowTarget(ball, goal, true);
    float separation = support.FlatDist(anchor);
    Check(separation >= 900, $"support/anchor separation is only {separation:F1} uu");
});

Test("opponent model: car adapter exposes RLBot v5 last input", () =>
{
    System.Reflection.FieldInfo? field = typeof(Car).GetField("LastInput", BindingFlags.Public | BindingFlags.Instance);
    Check(field != null && field.FieldType == typeof(ControllerStateT),
        "Car discards PlayerInfo.last_input, so opponent intent cannot be modeled");
});

Test("opponent model: attacking input creates pressure before a ball-only goal threat", () =>
{
    var opponent = new Car
    {
        Index = 3,
        Team = 1,
        Location = new Vec3(0, 950, 17),
        Velocity = new Vec3(0, -900, 0),
        Orientation = new Mat3x3(new Vec3(0, -MathF.PI / 2, 0)),
        IsGrounded = true,
        Boost = 40,
        LastInput = new ControllerStateT { Throttle = 1, Boost = true }
    };
    var ball = new Ball(new Vec3(0, 0, 100), Vec3.Zero);
    float pressure = Tactics.OpponentPressure(new[] { opponent }, ball, new Vec3(0, -5120, 0));
    Check(float.IsFinite(pressure) && pressure < 1.35f, $"attacking contact intent was not detected: {pressure}");
    Check(float.IsPositiveInfinity(Tactics.GoalThreat(new[] { new BallSlice(Game.Time + 1, ball.location, ball.velocity) },
        new Vec3(0, -5120, 0), Game.Time)), "fixture accidentally already contains a goal-bound ball path");
});

Test("opponent model: player facing and driving away does not create pressure", () =>
{
    var opponent = new Car
    {
        Index = 3,
        Team = 1,
        Location = new Vec3(0, 950, 17),
        Velocity = new Vec3(0, 800, 0),
        Orientation = new Mat3x3(new Vec3(0, MathF.PI / 2, 0)),
        IsGrounded = true,
        Boost = 40,
        LastInput = new ControllerStateT { Throttle = 1 }
    };
    float pressure = Tactics.OpponentPressure(new[] { opponent },
        new Ball(new Vec3(0, 0, 100), Vec3.Zero), new Vec3(0, -5120, 0));
    Check(float.IsPositiveInfinity(pressure), $"retreating opponent created false pressure: {pressure}");
});

DefenseRegression.Run(Test);
PressureRegression.Run(Test);
ReverseRegression.Run(Test);

Console.WriteLine($"TEAM DEFENSE RESULT: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;
