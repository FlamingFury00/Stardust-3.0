using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// Full-policy 2v2 resets before stale counter-recovery targets in cycle-3 tournament replays.
/// Subject is seat zero. Replay-rounded states omit angular velocity and jump/flip elapsed time;
/// angular velocity and active jump/flip timers reset to zero, active flipping is disabled, and
/// AirTimeSinceJump is 0.3 s. This is a controlled approximation, not replay playback.
/// </summary>
public sealed class CounterReplayDrill(int team = 0, bool necto = false) : Drill
{
    public override string Name => "counter-replay-" + (necto ? "necto" : "nexto") + (team == 0 ? "" : "-orange");
    public override string Description => "Defend a counterattack from a recorded stale-recovery transition.";
    public override int SeatCount => 4;
    public override bool RequiresOpponent => true;
    public override int TeamOf(int seat) => seat < 2 ? team : 1 - team;
    public override IAgent? ScriptedOpponent(int seat) => seat == 1 ? new StardustAgent() : null;
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.8), Criterion.Mean("conceded", 0.1, atLeast: false), Criterion.Mean("team-touch", 0.8),
    };
    public override EpisodeSetup Generate(Random r)
    {
        float sign = team == 0 ? 1 : -1, mirror = r.Next(2) == 0 ? 1 : -1;
        float dx = Uniform(r, -10, 10), dy = Uniform(r, -10, 10);
        RsbVec Point(RsbVec p) => V((p.X + dx) * mirror * sign, (p.Y + dy) * sign, p.Z);
        RsbVec Vector(RsbVec v) => V(v.X * mirror * sign, v.Y * sign, v.Z);
        CarSetup Car(RsbVec p, RsbVec v, float yaw, float boost, float pitch = -0.01f,
            float roll = 0, bool air = false)
        {
            RsbVec forward = Vector(Heading(yaw));
            return new CarSetup(Point(p), MathF.Atan2(forward.Y, forward.X), Vector(v), boost,
                Pitch: pitch, Roll: roll * mirror, OnGround: !air, HasJumped: air, HasFlipped: air);
        }
        return necto
            // candidate-vs-Necto/game-002/replay.json at 72.971 s, subject original seat 0.
            ? new EpisodeSetup(Point(V(1630, -2233, 381)), Vector(V(-318, -994, 179)), new[]
            {
                Car(V(1830, -2220, 17), V(240, 371, 0), 1.027461f, 12),
                Car(V(144, -3926, 17), V(491, -114, 0), -0.274589f, 12),
                Car(V(1779, -2128, 237), V(495, -723, 13), -1.054566f, 44, 0.553890f, -2.614178f, true),
                Car(V(2090, -1115, 17), V(1166, -588, 0), -0.533904f, 20),
            }, 5f)
            // candidate-vs-Nexto/game-000/replay.json at 19.367 s, subject original seat 1.
            : new EpisodeSetup(Point(V(3208, 135, 920)), Vector(V(-1228, -1900, -562)), new[]
            {
                Car(V(2864, 482, 17), V(-484, -1191, 0), 1.053884f, 0),
                Car(V(2587, 4984, 20), V(-500, 129, 102), 2.729584f, 0, 0.086106f, 0.164526f),
                Car(V(3448, 131, 1057), V(-1022, -2037, -15), -1.364951f, 22, -0.068053f, 0.607065f, true),
                Car(V(3315, 635, 17), V(-642, -1952, 0), -1.961893f, 51),
            }, 5f);
    }
    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        ((StardustAgent)session.Participants[1].Agent).Attach(session);
    }
    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool conceded = trace.GoalTeam == 1 - team;
        bool touch = trace.TouchLog.Any(t => t.Toucher is 0 or 1);
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Metrics["team-touch"] = touch ? 1 : 0;
        return !conceded && touch;
    }
}
