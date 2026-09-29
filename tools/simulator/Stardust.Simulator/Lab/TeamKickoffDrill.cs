using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>Full-policy 2v2 kickoff, including the support car and ten seconds after first contact.</summary>
public sealed class TeamKickoffDrill(int team = 0) : Drill
{
    private static readonly (float X, float Y, float Yaw)[] Spawns =
    {
        (-2048, -2560, MathF.PI / 4), (2048, -2560, 3 * MathF.PI / 4),
        (-256, -3840, MathF.PI / 2), (256, -3840, MathF.PI / 2), (0, -4608, MathF.PI / 2),
    };
    private bool followed;
    private float followAt;
    private int firstSpawn, secondSpawn;
    public override string Name => "team-kickoff" + (team == 0 ? "" : "-orange");
    public override string Description => "Score from the kickoff or convert it into a team follow-up without conceding.";
    public override int SeatCount => 4;
    public override bool RequiresOpponent => true;
    public override int TeamOf(int seat) => seat < 2 ? team : 1 - team;
    public override IAgent? ScriptedOpponent(int seat) => seat == 1 ? new StardustAgent() : null;
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.7), Criterion.Mean("conceded", 0.1, atLeast: false),
        Criterion.Mean("follow-up-touch", 0.7),
    };
    public override EpisodeSetup Generate(Random r)
    {
        firstSpawn = r.Next(Spawns.Length);
        secondSpawn = r.Next(Spawns.Length - 1);
        if (secondSpawn >= firstSpawn) secondSpawn++;
        CarSetup Car(int spawn, int carTeam)
        {
            var (x, y, yaw) = Spawns[spawn];
            float sign = carTeam == 0 ? 1 : -1;
            return new CarSetup(V(x * sign, y * sign, 17), yaw + (carTeam == 0 ? 0 : MathF.PI), V(0, 0, 0), 33.3f);
        }
        return new EpisodeSetup(V(0, 0, SimArena.BallRadius), V(0, 0, 0), new[]
        {
            Car(firstSpawn, team), Car(secondSpawn, team),
            Car(firstSpawn, 1 - team), Car(secondSpawn, 1 - team),
        }, 15f, Kickoff: true);
    }
    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        ((StardustAgent)session.Participants[1].Agent).Attach(session);
        followed = false;
        followAt = float.PositiveInfinity;
    }
    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        // Allow the initial collision to separate before counting a follow-up touch.
        if (!followed && float.IsFinite(trace.FirstTouchTime) && trace.Elapsed > trace.FirstTouchTime + 0.35f &&
            session.TouchedThisTick && session.LastToucher is 0 or 1)
        {
            followed = true;
            followAt = trace.Elapsed - trace.FirstTouchTime;
        }
    }
    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) =>
        base.ShouldStop(session, trace) || (float.IsFinite(trace.FirstTouchTime) && trace.Elapsed >= trace.FirstTouchTime + 10f);
    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool conceded = trace.GoalTeam == 1 - team;
        bool scored = trace.GoalTeam == team;
        trace.Metrics["first-touch"] = trace.FirstToucher is 0 or 1 ? 1 : 0;
        trace.Metrics["follow-up-touch"] = followed ? 1 : 0;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Metrics["scored"] = scored ? 1 : 0;
        if (followed) trace.Metrics["follow-up-s"] = followAt;
        trace.Notes.Insert(0, $"spawns {firstSpawn}/{secondSpawn}");
        return scored || (!conceded && followed);
    }
}
