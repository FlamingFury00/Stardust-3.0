using Bot;
using RedUtils.Math;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>Native recovery check for a sideways landing/slide near the goal line.</summary>
public sealed class GoalwardSlipDrill(int team = 0) : Drill
{
    private float maximumDepth, arrival, earlyBoost;
    private Vec3 target;
    public override string Name => "goalward-slip" + (team == 0 ? "" : "-orange");
    public override string Description => "Recover grip and reach a lateral defensive waypoint without accelerating a slide into the net.";
    public override int TeamOf(int seat) => team;
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.9), Criterion.Quantile("maximum-depth", 0.95, 5180, atLeast: false),
        Criterion.Mean("early-boost-s", 0, atLeast: false),
    };
    public override EpisodeSetup Generate(Random r)
    {
        float sign = team == 0 ? 1 : -1, mirror = r.Next(2) == 0 ? 1 : -1;
        float x = Uniform(r, -200, 200), y = Uniform(r, -4760, -4650);
        target = new Vec3((x + 1500) * mirror * sign, -4750 * sign, 17);
        float yaw = MathF.Atan2(0, mirror * sign);
        return new EpisodeSetup(V(0, 0, 93), V(0, 0, 0), new[]
        {
            new CarSetup(V(x * mirror * sign, y * sign, 17), yaw,
                V(Uniform(r, 30, 120) * mirror * sign, -Uniform(r, 900, 1450) * sign, 0), 45)
        }, 3.5f);
    }
    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        maximumDepth = earlyBoost = 0; arrival = float.PositiveInfinity;
        DefensiveDrive? drive = null;
        Subject.Director = bot =>
        {
            drive ??= new DefensiveDrive(bot.Me, target, 2000, 0, holdPosition: true);
            bot.Action = drive;
        };
    }
    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        var car = session.Cars[0];
        maximumDepth = MathF.Max(maximumDepth, MathF.Abs(car.Physics.Position.Y));
        if (trace.Elapsed < 0.2f && session.Participants[0].LastInput.Boost) earlyBoost += SimArena.TickTime;
        if (car.Physics.Position.Distance(V(target.x, target.y, 17)) < 160 && !float.IsFinite(arrival)) arrival = trace.Elapsed;
    }
    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["maximum-depth"] = maximumDepth;
        trace.Metrics["early-boost-s"] = earlyBoost;
        trace.Metrics["arrival-s"] = float.IsFinite(arrival) ? arrival : trace.Elapsed;
        return maximumDepth < 5180 && earlyBoost == 0 && arrival <= 3.5f;
    }
}
