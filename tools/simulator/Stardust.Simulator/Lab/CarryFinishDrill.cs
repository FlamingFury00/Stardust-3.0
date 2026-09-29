using Bot;
using Stardust.Simulator.Match;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// GroundDribble's autonomous carry-to-flick transition, isolated from the supervisor's separate
/// strike selector. No action is injected after the initial carry: preparation and launch use
/// the production mechanic's pressure/shot decision, unlike the directed standalone flick drill.
/// </summary>
public sealed class CarryFinishDrill : RoofDrill
{
    private bool selectedFlick;
    private float jumpedAt;
    public override string Name => "carry-finish";
    public override string Description => "Convert an unopposed roof carry into an accurately aimed finish.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.85), Criterion.Mean("flick-selected", 0.9),
        Criterion.Quantile("goal-s", 0.9, 3.5, atLeast: false),
        Criterion.Mean("own-goal", 0, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        var position = V(Uniform(r, -1200, 1200), Uniform(r, 2300, 3150), 17.01f);
        float heading = MathF.Atan2(5120 - position.Y, -position.X) + Uniform(r, -0.45f, 0.45f);
        return CarryStart(position, heading, Uniform(r, 900, 1450), Uniform(r, 0, 25),
            Uniform(r, -10, 10), Uniform(r, 30, 80), 4f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        selectedFlick = false;
        jumpedAt = float.PositiveInfinity;
        bool installed = false;
        Subject.Director = bot =>
        {
            if (installed) return;
            bot.Action = new GroundDribble();
            installed = true;
        };
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        selectedFlick |= Subject.Action is Flick;
        if (!float.IsFinite(jumpedAt) && session.Participants[0].LastInput.Jump)
            jumpedAt = trace.Elapsed;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool scored = trace.GoalTeam == 0;
        trace.Metrics["flick-selected"] = selectedFlick ? 1 : 0;
        trace.Metrics["goal"] = scored ? 1 : 0;
        trace.Metrics["own-goal"] = trace.GoalTeam == 1 ? 1 : 0;
        trace.Metrics["first-jump-s"] = float.IsFinite(jumpedAt) ? jumpedAt : trace.Elapsed;
        trace.Metrics["goal-s"] = scored ? trace.GoalTime : setup.TimeLimit;
        return scored;
    }
}
