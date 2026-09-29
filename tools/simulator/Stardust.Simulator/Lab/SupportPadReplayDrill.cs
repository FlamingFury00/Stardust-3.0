using Bot;
using RedUtils;
using RedUtils.Math;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Full-policy 2v2 from candidate-vs-Nexto game-000 at 42.502s. Original seat order is preserved:
/// car 1 is the empty support car; changing its index would change deterministic race tie-breaks.
/// Positions are mirrored/jittered. Unrecorded angular velocities are zero, pads start available.
/// </summary>
public sealed class SupportPadReplayDrill(int team = 0) : Drill
{
    private Vec3 padPosition;
    private int padIndex;
    private float picked, previousBoost;
    private StardustAgent support = null!;
    public override string Name => "support-pad-replay" + (team == 0 ? "" : "-orange");
    public override string Description => "Preserve pressure and goal coverage while the empty second man collects a near-route pad.";
    public override int SeatCount => 4;
    public override bool RequiresOpponent => true;
    public override int TeamOf(int seat) => seat < 2 ? team : 1 - team;
    public override IAgent? ScriptedOpponent(int seat) => seat == 1 ? new StardustAgent() : null;
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.8), Criterion.Mean("support-pickup", 0.8),
        Criterion.Mean("conceded", 0.1, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        float sign = team == 0 ? 1 : -1, mirror = r.Next(2) == 0 ? 1 : -1;
        float dx = Uniform(r, -15, 15), dy = Uniform(r, -15, 15);
        RsbVec Point(RsbVec p) => V((p.X + dx) * mirror * sign, (p.Y + dy) * sign, p.Z);
        RsbVec Vector(RsbVec v) => V(v.X * mirror * sign, v.Y * sign, v.Z);
        CarSetup Car(RsbVec p, RsbVec v, float yaw, float boost)
        {
            RsbVec forward = Vector(Heading(yaw + Uniform(r, -0.01f, 0.01f)));
            return new CarSetup(Point(p), MathF.Atan2(forward.Y, forward.X), Vector(v), boost, Pitch: -0.01f);
        }
        padPosition = new Vec3(2048 * mirror * sign, -1036 * sign, 17);
        return new EpisodeSetup(Point(V(2876, 141, 655)), Vector(V(-84, -630, 365)), new[]
        {
            Car(V(2129, 336, 17), V(187, -252, 0), -0.93230f, 2),
            Car(V(2008, -476, 17), V(-664, -1200, 0), -2.07246f, 0),
            Car(V(3480, 12, 17), V(-193, -702, 0), -1.93144f, 100),
            Car(V(3347, 142, 17), V(899, -611, 0), -0.52721f, 71),
        }, 5f);
    }
    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        support = (StardustAgent)session.Participants[1].Agent;
        support.Attach(session);
        padIndex = -1; picked = float.PositiveInfinity; previousBoost = 0;
    }
    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        if (padIndex < 0) padIndex = Field.Boosts.MinBy(pad => pad.Location.FlatDist(padPosition))!.Index;
        var car = session.Cars[1];
        if (car.Boost > previousBoost + 6f &&
            car.Physics.Position.Distance(V(padPosition.x, padPosition.y, 17)) < 220f && !float.IsFinite(picked))
            picked = trace.Elapsed;
        previousBoost = car.Boost;
    }
    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool conceded = trace.GoalTeam == 1 - team;
        trace.Metrics["support-pickup"] = float.IsFinite(picked) ? 1 : 0;
        trace.Metrics["support-pickup-s"] = float.IsFinite(picked) ? picked : trace.Elapsed;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        return float.IsFinite(picked) && !conceded;
    }
}
