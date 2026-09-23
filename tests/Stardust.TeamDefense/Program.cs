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

Test("log-v14: airborne pressure overrides stale loose-ball ground ETA", () =>
{
    var frame = new TacticalFrame
    {
        MyEta = 2.1661f,
        OpponentEta = 2.4827f,
        PressureTime = 0.1372f,
        TeamRank = 0,
        TeamCount = 1,
        LastBack = true,
        HasCover = false
    };
    var car = new Car
    {
        Index = 0,
        Team = 0,
        Location = new Vec3(-625.9f, -5081.5f, 17f),
        Velocity = new Vec3(-500f, -300f, 0f),
        Orientation = new Mat3x3(new Vec3(0f, -2.4f, 0f)),
        IsGrounded = true,
        Boost = 0f
    };
    Vec3 ball = new(-1804f, -3952f, 97f);
    Vec3 goal = new(0f, -5120f, 0f);

    float contactEta = Defense.OpponentContactEta(frame);
    Check(MathF.Abs(contactEta - 0.1372f) < 0.001f,
        $"stale ground ETA still won over imminent contact: {contactEta:F4}");
    Check(Defense.EffectiveFreeTime(frame) < -1.9f,
        $"effective race still looked winnable: {Defense.EffectiveFreeTime(frame):F3}");
    Check(!Defense.CanChallenge(frame, car, ball, goal),
        "imminent airborne opponent touch still opened a fresh challenge");
    Check(!Defense.CanContinueChallenge(frame, car, ball, goal),
        "challenge hysteresis survived an imminent opponent touch by over two seconds");
});

Test("log-v14: ordinary near-tie pressure keeps committed challenge hysteresis", () =>
{
    var frame = new TacticalFrame
    {
        MyEta = 0.95f,
        OpponentEta = 0.70f,
        PressureTime = 0.20f,
        TeamRank = 0,
        TeamCount = 1,
        LastBack = true,
        HasCover = false
    };
    var car = new Car
    {
        Index = 0,
        Team = 0,
        Location = new Vec3(0f, -3000f, 17f),
        Velocity = Vec3.Zero,
        Orientation = new Mat3x3(new Vec3(0f, -MathF.PI / 2f, 0f)),
        IsGrounded = true,
        Boost = 30f
    };
    Vec3 ball = new(0f, -2200f, 100f);
    Vec3 goal = new(0f, -5120f, 0f);

    Check(MathF.Abs(Defense.OpponentContactEta(frame) - 0.70f) < 0.001f,
        "normal pressure incorrectly replaced a coherent race ETA");
    Check(Defense.CanContinueChallenge(frame, car, ball, goal),
        "contact-clock fusion made normal committed defense passive");
});

Test("log-v14: contact ETA fusion is continuous through the disagreement band", () =>
{
    var frame = new TacticalFrame
    {
        MyEta = 1.0f,
        OpponentEta = 1.20f,
        PressureTime = 0.64f
    };

    float justInside = Defense.OpponentContactEta(frame);
    frame.PressureTime = 0.63f;
    float slightlyEarlier = Defense.OpponentContactEta(frame);
    Check(justInside <= 1.20f && justInside >= 0.64f,
        $"blended contact ETA left its source interval: {justInside:F4}");
    Check(slightlyEarlier <= justInside,
        $"earlier pressure made fused opponent contact later: {justInside:F4} -> {slightlyEarlier:F4}");
    Check(justInside - slightlyEarlier < 0.10f,
        $"contact ETA fusion still contains a hard tactical jump: {justInside:F4} -> {slightlyEarlier:F4}");
});

Test("log-v14: car behind own goal line exits before loose-ball commitment", () =>
{
    var frame = new TacticalFrame
    {
        MyEta = 2.9576f,
        OpponentEta = 2.6410f,
        TeamRank = 0,
        TeamCount = 1,
        LastBack = true,
        HasCover = false
    };
    var car = new Car
    {
        Index = 0,
        Team = 0,
        Location = new Vec3(-514.6f, -5345.9f, 17f),
        Velocity = new Vec3(69.8f, -807.5f, 13.5f),
        Orientation = new Mat3x3(new Vec3(0f, -1.4224f, 0f)),
        IsGrounded = true,
        Boost = 0f
    };
    Vec3 ball = new(791.7f, -4834.8f, 173.1f);
    Vec3 goal = new(0f, -5120f, 0f);

    Check(Defense.NeedsGoalExit(car, goal),
        "observed deep-net state was not recognized as requiring an exit");
    Check(!Defense.CanChallenge(frame, car, ball, goal),
        "deep-net car was still allowed to start a loose-ball challenge");
    Check(!Defense.CanContinueChallenge(frame, car, ball, goal),
        "deep-net challenge hysteresis was still retained");

    Vec3 exit = Defense.GoalExitTarget(car, goal);
    Check(exit.y > goal.y && exit.y < -4500f,
        $"exit target did not cross the goal line into the field: {exit}");
    Check(MathF.Abs(exit.x) < Goal.Width * 0.5f,
        $"exit target risked a goal post: {exit}");
});

Test("log-v14: urgent sub-threshold goal-line save keeps travel speed", () =>
{
    const float distance = 960f;
    const float timeRemaining = 0.895f;

    float required = GoalLineSave.RequiredTravelSpeed(distance, timeRemaining);
    Check(required > 1100f,
        $"observed save geometry understated required travel speed: {required:F0}");
    Check(GoalLineSave.NeedsFastTravel(distance, timeRemaining),
        "960 uu / 0.895 s save still fell into parking mode");
    Check(!GoalLineSave.NeedsFastTravel(distance, 2.2f),
        "early 960 uu positioning incorrectly stayed in emergency travel mode");
    Check(GoalLineSave.NeedsFastTravel(600f, 0.08f),
        "last-100-ms save incorrectly fell back into parking mode");
});

Test("log-v14: emergency planning deadline stops at imminent opponent touch", () =>
{
    var frame = new TacticalFrame
    {
        MyEta = 1.0581f,
        OpponentEta = 1.6913f,
        PressureTime = 0.44f,
        TeamRank = 0,
        TeamCount = 1,
        LastBack = true
    };

    float deadline = Defense.DefensiveDeadline(1.80f, frame);
    Check(deadline >= 0.40f && deadline <= 0.44f,
        $"1.8 s untouched threat ignored 0.44 s opponent contact: {deadline:F3}");

    frame.PressureTime = float.PositiveInfinity;
    deadline = Defense.DefensiveDeadline(1.20f, frame);
    Check(deadline > 1.15f && deadline < 1.20f,
        $"clean untouched threat lost its own crossing deadline: {deadline:F3}");
});

Test("log-v16: 400 uu save with 0.33 s remaining stays in travel mode", () =>
{
    float required = GoalLineSave.RequiredTravelSpeed(400f, 0.33f);
    Check(required > 1400f,
        $"fixture no longer requires urgent travel: {required:F0}");
    Check(GoalLineSave.NeedsFastTravel(400f, 0.33f),
        "short-range goal-line save still entered parking mode before arrival");
    Check(!GoalLineSave.NeedsFastTravel(100f, 0.33f),
        "true arrival-radius save unnecessarily stayed in travel mode");
});

Test("log-v16: lateral alignment cannot trigger a jump far from the goal plane", () =>
{
    var far = new Car
    {
        Location = new Vec3(-1000f, -2200f, 17f),
        IsGrounded = true
    };
    Vec3 guard = new(-537f, -5065f, 17f);

    Check(!GoalLineSave.IsJumpPositioned(far, guard),
        "car nearly 3k uu upfield was considered jump-positioned");

    far.Location = new Vec3(-950f, -4850f, 17f);
    Check(GoalLineSave.IsJumpPositioned(far, guard),
        "real goal-plane coverage was rejected");
});

Test("possession-v16: safe dribble acquisition outranks a routine intercept", () =>
{
    var frame = new TacticalFrame
    {
        MyEta = 0.45f,
        OpponentEta = 1.10f,
        TeamRank = 0,
        TeamCount = 1,
        LastBack = true
    };

    Check(PossessionControl.PreferGroundControl(
            frame, canDribble: true, underPressure: false,
            forceFinishOpportunity: false),
        "clean possession window still preferred a generic hit");
    Check(!PossessionControl.PreferGroundControl(
            frame, canDribble: true, underPressure: true,
            forceFinishOpportunity: false),
        "contested loose control incorrectly ignored pressure");
    Check(!PossessionControl.PreferGroundControl(
            frame, canDribble: true, underPressure: false,
            forceFinishOpportunity: true),
        "forced opponent-box finish was suppressed by dribble setup");
});

Test("possession-v16: post-touch aerial separation can be recaptured with boost and time", () =>
{
    var car = new Car
    {
        Location = new Vec3(3400f, 4560f, 270f),
        Velocity = new Vec3(150f, -150f, 0f),
        Orientation = new Mat3x3(new Vec3(0f, 0f, 0f)),
        IsGrounded = false,
        Boost = 100f
    };
    var ball = new Ball(
        new Vec3(3100f, 4560f, 410f),
        new Vec3(-350f, 380f, -160f));
    var frame = new TacticalFrame
    {
        MyEta = 2.17f,
        OpponentEta = 2.17f,
        TeamRank = 0,
        TeamCount = 1,
        LastBack = true
    };
    Vec3 goal = new(0f, -5120f, 0f);

    Check(PossessionControl.CanAcquireAir(frame, car, ball, goal),
        "recoverable opponent-half aerial state still fell straight to Recover");
    Check(AerialCarry.CanStart(car, ball, frame.OpponentEta),
        "healthy-boost separating aerial carry was still rejected");
});

Test("possession-v16: airborne jump-shot setup can hand off before its dodge", () =>
{
    var car = new Car
    {
        Location = new Vec3(0f, 0f, 190f),
        Velocity = new Vec3(450f, 0f, 350f),
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = false,
        Boost = 80f
    };
    var ball = new Ball(
        new Vec3(170f, 0f, 500f),
        new Vec3(300f, 0f, 120f));

    Check(PossessionControl.HasAirControl(car, ball),
        "fixture stopped reproducing established air control");
    Check(PossessionControl.CanHandoffShotToAirCarry(car, ball, 1.6f),
        "air-controlled JumpShot state could not hand off to AerialCarry");

    car.IsGrounded = true;
    Check(!PossessionControl.CanHandoffShotToAirCarry(car, ball, 1.6f),
        "grounded car incorrectly qualified for an airborne handoff");
});

Test("log-v17: staging keeps the full goal-threat horizon after an early pressure clock", () =>
{
    float horizon = Defense.ThreatStagingHorizon(2.48f, 0.54f);
    Check(horizon > 2.45f && horizon < 2.50f,
        $"2.48 s goal threat was incorrectly truncated to next-touch timing: {horizon:F3}");
});

Test("log-v17: owned goal-line lane stays sticky while the ball is not point-blank", () =>
{
    var car = new Car
    {
        Location = new Vec3(512f, -5079f, 17f),
        IsGrounded = true
    };
    Vec3 crossing = new(492f, -5120f, 191f);

    Check(GoalLineSave.ShouldHoldLine(
            car, crossing, 188.135f, new Vec3(0f, -5120f, 0f), 185.658f),
        "car already owning the predicted crossing was allowed to abandon the line");

    crossing.x = -500f;
    Check(!GoalLineSave.ShouldHoldLine(
            car, crossing, 188.135f, new Vec3(0f, -5120f, 0f), 185.658f),
        "large crossing relocation incorrectly kept a stale goal-line save");
});

Test("log-v17: losing-race shadow retreats instead of compressing toward the attacker", () =>
{
    Vec3 ball = new(-3378f, -1144f, 162f);
    Vec3 goal = new(0f, -5120f, 0f);

    Vec3 neutral = Defense.ShadowTarget(
        ball, goal, DefensiveRole.Shadow, 0.56f, 0.20f);
    Vec3 losing = Defense.ShadowTarget(
        ball, goal, DefensiveRole.Shadow, 0.56f, -0.45f);

    Check(Defense.OwnDepth(losing, goal) >
          Defense.OwnDepth(neutral, goal) + 140f,
        $"lost race still compressed upfield: neutral={neutral}, losing={losing}");
});

Test("possession-v17: low airborne setup is eligible for a controlled carry", () =>
{
    var car = new Car
    {
        Location = new Vec3(0f, 0f, 106f),
        Velocity = new Vec3(500f, 0f, 180f),
        Orientation = new Mat3x3(Vec3.Zero),
        IsGrounded = false,
        Boost = 12f
    };
    var ball = new Ball(
        new Vec3(150f, 0f, 388f),
        new Vec3(700f, 0f, 180f));

    Check(PossessionControl.HasAirControl(car, ball),
        "fixture stopped reproducing low established air control");
    Check(AerialCarry.CanStart(car, ball, 2.9f),
        "low post-jump air-dribble setup was still rejected by cold-start gates");

    car.Boost = 6f;
    Check(PossessionControl.CanHandoffShotToAirCarry(car, ball, 1.2f),
        "established air control with a small boost reserve could not hand off from JumpShot");
});

DefenseRegression.Run(Test);

Console.WriteLine($"TEAM DEFENSE RESULT: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;
