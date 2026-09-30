using System.Globalization;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// An opponent runs at a loose ball it reaches well before we do. A pro closes the gap at supersonic
/// speed and takes the car out instead of trailing it to the ball. Judged on whether the full bot
/// starts a run when one is possible and whether the runs it starts end in a demolition. The runner
/// is a plain ground chaser, so the fixture measures the run, not the opponent's evasion.
/// </summary>
public sealed class DemolitionDrill : Drill
{
    private bool attempted, demolished;
    private float demolishedAt;

    public override string Name => "demolition";
    public override string Description => "Demolish a chaser that wins the race to a loose ball, from a fast approach with boost.";
    public override int SeatCount => 2;
    public override IAgent? ScriptedOpponent(int seat) => new GroundChaseAgent();

    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Mean("attempted", 0.6), Criterion.Mean("converted", 0.75),
        Criterion.Mean("conceded", 0, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        float x = Uniform(r, -1200, 1200);
        var ball = V(x, Uniform(r, 300, 900), 93.15f);
        // The runner is a second or so from the ball, we are well over a second further away: the race is lost.
        var runner = V(x + Uniform(r, -400, 400), ball.Y + Uniform(r, 700, 1300), 17.01f);
        var us = V(x + Uniform(r, -400, 400), ball.Y - Uniform(r, 2400, 3000), 17.01f);
        float toRunner = MathF.Atan2(runner.Y - us.Y, runner.X - us.X);
        float yaw = toRunner + Uniform(r, -0.12f, 0.12f);
        float speed = Uniform(r, 1000, 1600);
        float runnerYaw = MathF.Atan2(ball.Y - runner.Y, ball.X - runner.X);
        return new EpisodeSetup(ball, V(0, 0, 0), new[]
        {
            new CarSetup(us, yaw, Heading(yaw) * speed, Uniform(r, 50, 100)),
            new CarSetup(runner, runnerYaw, Heading(runnerYaw) * Uniform(r, 500, 1000), 0),
        }, 4f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        attempted = demolished = false;
        demolishedAt = float.NaN;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        attempted |= Subject.Action is global::Bot.DemoAttack;
        if (!demolished && session.Cars[1].IsDemoed != 0)
        {
            demolished = true;
            demolishedAt = trace.Elapsed;
        }
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Notes.Insert(0, attempted ? (demolished ? "demolished" : "missed") : "no run");
        trace.Metrics["attempted"] = attempted ? 1 : 0;
        if (attempted)
            trace.Metrics["converted"] = demolished ? 1 : 0;
        if (demolished)
            trace.Metrics["demolition-s"] = demolishedAt;
        trace.Metrics["conceded"] = trace.GoalTeam == 1 ? 1 : 0;
        return demolished;
    }
}
