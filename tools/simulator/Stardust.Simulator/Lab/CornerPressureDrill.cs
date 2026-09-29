using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Full 2v2 policy from the cycle-2 Necto 56.605s and Nexto 164.749s corner approaches.
/// Replays round positions and omit angular velocity; resets explicitly use zero angular velocity.
/// Both defenders execute Stardust, and two external opponents continue from the recorded state.
/// </summary>
public sealed class CornerPressureDrill(int team = 0, bool backwall = false) : Drill
{
    private float closeAt, teamCloseAt, touchAt, initialGap, gapAtOne;
    public override string Name => (backwall ? "backwall-pressure" : "corner-pressure") + (team == 0 ? "" : "-orange");
    public override string Description => "Close the first-man corner lane while a second defender covers the goal.";
    public override int SeatCount => 4;
    public override bool RequiresOpponent => true;
    public override int TeamOf(int seat) => seat < 2 ? team : 1 - team;
    public override IAgent? ScriptedOpponent(int seat) => seat == 1 ? new StardustAgent() : null;
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.8), Criterion.Mean("conceded", 0.1, atLeast: false),
        Criterion.Mean("team-touch", 0.8), Criterion.Median("team-close-s", 1.5, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        float sign = team == 0 ? 1 : -1;
        float mirror = r.Next(2) == 0 ? 1 : -1;
        float dx = Uniform(r, -20, 20), dy = Uniform(r, -20, 20);
        RsbVec Point(RsbVec p) => V((p.X + dx) * mirror * sign, (p.Y + dy) * sign, p.Z);
        RsbVec Vector(RsbVec v) => V(v.X * mirror * sign, v.Y * sign, v.Z);
        CarSetup Car(RsbVec p, RsbVec v, float yaw, float boost)
        {
            RsbVec forward = Vector(Heading(yaw + Uniform(r, -0.015f, 0.015f)));
            return new CarSetup(Point(p), MathF.Atan2(forward.Y, forward.X), Vector(v), boost, Pitch: -0.01f);
        }
        return backwall
            ? new EpisodeSetup(Point(V(-2507, -4836, 235)), Vector(V(71, -761, -419)), new[]
            {
                Car(V(-764, -4881, 17), V(194, -409, 0), -1.05748f, 44),
                Car(V(644, -4864, 17), V(85, -324, 0), 1.75897f, 12),
                Car(V(-2663, -4812, 17), V(515, -749, 0), -0.96810f, 58),
                Car(V(-2795, -3254, 17), V(416, -1210, 0), -1.26075f, 96),
            }, 5f)
            : new EpisodeSetup(Point(V(2877, -4192, 104)), Vector(V(-789, -582, 89)), new[]
            {
                Car(V(801, -4338, 17), V(-441, -1026, 0), -1.91470f, 40),
                Car(V(-294, -4597, 17), V(-722, -345, 0), 0.48466f, 30),
                Car(V(3242, -3525, 17), V(-477, -878, 0), -2.09801f, 11),
                Car(V(3157, -4238, 17), V(-573, -1038, 1), -2.13522f, 95),
            }, 5f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        ((StardustAgent)session.Participants[1].Agent).Attach(session);
        closeAt = teamCloseAt = touchAt = float.PositiveInfinity;
        initialGap = setup.Cars[0].Position.Distance(setup.BallPosition);
        gapAtOne = initialGap;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        float gap = session.Cars[0].Physics.Position.Distance(session.Ball.Physics.Position);
        if (trace.Elapsed <= 1f) gapAtOne = gap;
        if (gap <= 900 && !float.IsFinite(closeAt)) closeAt = trace.Elapsed;
        float teammateGap = session.Cars[1].Physics.Position.Distance(session.Ball.Physics.Position);
        if (MathF.Min(gap, teammateGap) <= 900 && !float.IsFinite(teamCloseAt)) teamCloseAt = trace.Elapsed;
        if (session.TouchedThisTick && session.LastToucher is 0 or 1 && !float.IsFinite(touchAt)) touchAt = trace.Elapsed;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool conceded = trace.GoalTeam == 1 - team;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Metrics["team-touch"] = float.IsFinite(touchAt) ? 1 : 0;
        trace.Metrics["touch-s"] = float.IsFinite(touchAt) ? touchAt : trace.Elapsed;
        trace.Metrics["initial-first-man-close-s"] = float.IsFinite(closeAt) ? closeAt : trace.Elapsed;
        trace.Metrics["team-close-s"] = float.IsFinite(teamCloseAt) ? teamCloseAt : trace.Elapsed;
        trace.Metrics["initial-gap"] = initialGap;
        trace.Metrics["initial-first-man-gap-at-1s"] = gapAtOne;
        return !conceded && float.IsFinite(touchAt);
    }
}
