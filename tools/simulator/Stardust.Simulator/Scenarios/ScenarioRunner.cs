using System.Globalization;
using System.Text;
using RLBot.Flat;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Protocol;

namespace Stardust.Simulator.Scenarios;

public sealed class ScenarioOutcome
{
    public required string Scenario { get; init; }
    public required string Bot { get; init; }
    public int Episodes { get; set; }
    public int Successes { get; set; }
    public Dictionary<string, List<double>> Metrics { get; } = new();
    public List<string> Failures { get; } = new();
    public double Rate => Episodes > 0 ? (double)Successes / Episodes : 0;
}

/// <summary>Runs randomized fixtures against one bot build, reusing a single session per scenario.</summary>
public static class ScenarioRunner
{
    public static ScenarioOutcome Run(Scenario scenario, BotBuild bot, BotBuild? opponent, int episodes, int seed,
        string? logDirectory, ReplayRecorder? replay = null)
    {
        using var session = new MatchSession(Seats(scenario, new Seat(scenario.TeamOf(0), bot.Label, bot), opponent),
            new MatchOptions { Seed = seed, LogDirectory = logDirectory, Replay = replay });
        return Run(scenario, session, bot.Label, episodes, seed, logDirectory);
    }

    /// <summary>Seat 0 is the bot under test; further seats get the scenario's scripted agent, the opponent build, or idle.</summary>
    public static List<Seat> Seats(Scenario scenario, Seat underTest, BotBuild? opponent)
    {
        var seats = new List<Seat> { underTest };
        for (int seat = 1; seat < scenario.SeatCount; seat++)
        {
            int team = scenario.TeamOf(seat);
            if (scenario.ScriptedOpponent(seat) is IAgent scripted)
                seats.Add(new Seat(team, scripted.Description + seat, Agent: scripted));
            else if (opponent != null)
                seats.Add(new Seat(team, opponent.Label + seat, opponent));
            else
                seats.Add(new Seat(team, "idle" + seat, Agent: new IdleAgent()));
        }
        return seats;
    }

    /// <summary>Plays the scenario's episodes in an existing session whose seat 0 is the bot under test.</summary>
    public static ScenarioOutcome Run(Scenario scenario, MatchSession session, string label, int episodes, int seed,
        string? logDirectory)
    {
        var outcome = new ScenarioOutcome { Scenario = scenario.Name, Bot = label };
        var random = new Random(seed);
        session.StatsEnabled = false;
        var episodeLog = logDirectory != null ? new List<string>() : null;

        for (int episode = 0; episode < episodes; episode++)
        {
            EpisodeSetup setup = scenario.Generate(random);
            EpisodeTrace trace;
            try
            {
                trace = RunEpisode(session, scenario, setup);
            }
            catch (BotCrashedException e)
            {
                outcome.Failures.Add($"episode {episode}: bot crashed: {e.Message}");
                break;
            }

            bool success = scenario.Judge(setup, trace);
            episodeLog?.Add(string.Create(CultureInfo.InvariantCulture,
                $"episode {episode} start {trace.StartTime:F3} end {trace.StartTime + trace.Elapsed:F3} success {success} " +
                $"ball {setup.BallPosition} v{setup.BallVelocity} car {setup.Cars[0].Position} yaw {setup.Cars[0].Yaw:F2} " +
                $"speed {setup.Cars[0].Velocity.Length:F0} goal {trace.GoalTeam} first-touch {trace.FirstTouchTime:F2}"));
            outcome.Episodes++;
            if (success) outcome.Successes++;
            else if (outcome.Failures.Count < 12)
                outcome.Failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"episode {episode}: ball {setup.BallPosition} v{setup.BallVelocity} car {setup.Cars[0].Position} " +
                    $"yaw {setup.Cars[0].Yaw:F2} -> touch {trace.FirstTouchTime:F2}s goal {trace.GoalTeam}"));
            foreach (var (key, value) in trace.Metrics)
            {
                if (!outcome.Metrics.TryGetValue(key, out var list))
                    outcome.Metrics[key] = list = new List<double>();
                if (double.IsFinite(value)) list.Add(value);
            }
        }
        if (episodeLog != null)
        {
            Directory.CreateDirectory(logDirectory!);
            File.WriteAllLines(Path.Combine(logDirectory!, "episodes.txt"), episodeLog);
        }
        return outcome;
    }

    public static EpisodeTrace RunEpisode(MatchSession session, Scenario scenario, EpisodeSetup setup)
    {
        SimArena arena = session.Arena;
        session.BreakClock(1.0f);
        var trace = new EpisodeTrace { StartTime = session.Time };

        {
            ApplyCars(session, setup, onlyBoost: false);
            arena.Ball = new RsbBallState
            {
                Physics = new RsbPhysics
                {
                    Position = setup.BallPosition,
                    Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1),
                    // A ball with exactly zero velocity sleeps; nudge it so gravity acts like in game.
                    Velocity = setup.BallVelocity.Length > 0.01f || setup.BallPosition.Z <= 94f
                        ? setup.BallVelocity : new RsbVec(0, 0, -0.01f),
                    AngularVelocity = setup.BallAngularVelocity ?? new RsbVec(0, 0, 0),
                },
            };
        }
        for (int pad = 0; pad < arena.PadCount; pad++)
            arena.SetPad(pad, true, 0);
        arena.PollEvents();
        session.ResetTouchState();
        scenario.Begin(session, setup);

        if (setup.Kickoff)
            for (float t = 0; t < 0.5f; t += SimArena.TickTime)
                session.Tick(MatchPhase.Countdown);

        bool kickoffPhase = setup.Kickoff;
        bool wasOnGround = session.Cars[0].IsOnGround != 0;
        while (trace.Elapsed < setup.TimeLimit)
        {
            MatchPhase phase = kickoffPhase ? MatchPhase.Kickoff : MatchPhase.Active;
            session.Tick(phase);
            trace.Elapsed += SimArena.TickTime;
            if (kickoffPhase && (session.TouchedThisTick || trace.Elapsed >= 2f))
                kickoffPhase = false;

            RsbBallState ball = session.Ball;
            trace.MaxBallZ = MathF.Max(trace.MaxBallZ, ball.Physics.Position.Z);
            if (session.TouchedThisTick)
            {
                trace.Touches++;
                trace.TouchLog.Add((trace.Elapsed, session.LastToucher));
                if (float.IsNaN(trace.FirstTouchTime))
                {
                    trace.FirstTouchTime = trace.Elapsed;
                    trace.FirstToucher = session.SimultaneousTouch ? -2 : session.LastToucher;
                    trace.BallPositionAtFirstTouch = ball.Physics.Position;
                    trace.AirborneAtFirstTouch = session.Cars[0].IsOnGround == 0;
                }
            }
            if (!float.IsNaN(trace.FirstTouchTime) && trace.Elapsed - trace.FirstTouchTime < 0.1f)
                trace.BallVelocityAfterFirstTouch = ball.Physics.Velocity;

            RsbCarState me = session.Cars[0];
            bool onGround = me.IsOnGround != 0;
            if (onGround && !wasOnGround && float.IsNaN(trace.LandedTime))
            {
                trace.LandedTime = trace.Elapsed;
                trace.CarLandedUpright = me.Physics.Up.Z > 0.7f || me.Physics.Position.Z > 200;
            }
            wasOnGround = onGround;

            var (events, _) = session.Arena.PollEvents();
            if (events.GoalTeam >= 0 && trace.GoalTeam < 0)
            {
                trace.GoalTeam = events.GoalTeam;
                trace.GoalTime = trace.Elapsed;
            }
            scenario.Observe(session, trace);
            if (scenario.ShouldStop(session, trace))
                break;
        }
        trace.FinalBallPosition = session.Ball.Physics.Position;
        return trace;
    }

    public static void ApplyCars(MatchSession session, EpisodeSetup setup, bool onlyBoost)
    {
        for (int i = 0; i < session.Participants.Count && i < setup.Cars.Count; i++)
        {
            Participant p = session.Participants[i];
            CarSetup c = setup.Cars[i];
            RsbCarState state = session.Arena.GetCar(p.CarId);
            if (!onlyBoost)
            {
                state.Physics = Orientation(c.Pitch, c.Yaw, c.Roll);
                state.Physics.Position = c.Position;
                state.Physics.Velocity = c.Velocity;
                state.Physics.AngularVelocity = c.AngularVelocity ?? new RsbVec(0, 0, 0);
                state.IsOnGround = c.OnGround ? 1 : 0;
                state.HasJumped = c.HasJumped ? 1 : 0;
                state.HasDoubleJumped = c.HasDoubleJumped ? 1 : 0;
                state.HasFlipped = c.HasFlipped ? 1 : 0;
                state.AirTime = c.OnGround ? 0 : 0.5f;
                state.AirTimeSinceJump = c.HasJumped ? 0.3f : 0f;
            }
            state.Boost = c.Boost;
            session.Arena.SetCar(p.CarId, state);
        }
    }

    /// <summary>Rotation columns for an Unreal (pitch, yaw, roll) rotator.</summary>
    public static RsbPhysics Orientation(float pitch, float yaw, float roll)
    {
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cr = MathF.Cos(roll), sr = MathF.Sin(roll);
        return new RsbPhysics
        {
            Forward = new RsbVec(cp * cy, cp * sy, sp),
            Right = new RsbVec(cy * sp * sr - cr * sy, sy * sp * sr + cr * cy, -cp * sr),
            Up = new RsbVec(-cr * cy * sp - sr * sy, -cr * sy * sp + sr * cy, cp * cr),
        };
    }

    public static string Format(IEnumerable<ScenarioOutcome> outcomes)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("| Scenario | Bot | Success | Metrics |");
        sb.AppendLine("|---|---|---|---|");
        foreach (ScenarioOutcome o in outcomes)
        {
            var (low, high) = Series.Wilson(o.Successes, o.Episodes);
            string metrics = string.Join(", ", o.Metrics.OrderBy(m => m.Key).Select(m =>
                string.Create(inv, $"{m.Key} med {Median(m.Value):F2} / mean {(m.Value.Count > 0 ? m.Value.Average() : double.NaN):F2} (n={m.Value.Count})")));
            sb.AppendLine(string.Create(inv,
                $"| {o.Scenario} | {o.Bot} | {o.Successes}/{o.Episodes} = {o.Rate:P0} [{low:P0}–{high:P0}] | {metrics} |"));
        }
        return sb.ToString();
    }

    public static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
    }
}
