using RLBot.Flat;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;

namespace Stardust.Simulator.Scenarios;

/// <summary>Initial state for one car in a fixture.</summary>
public sealed record CarSetup(RsbVec Position, float Yaw, RsbVec Velocity, float Boost = 50f,
    float Pitch = 0f, float Roll = 0f, RsbVec? AngularVelocity = null, bool OnGround = true,
    bool HasJumped = false, bool HasDoubleJumped = false, bool HasFlipped = false);

/// <summary>A fully specified fixture instance: ball, cars (seat order), and how long it may run.</summary>
public sealed record EpisodeSetup(RsbVec BallPosition, RsbVec BallVelocity, IReadOnlyList<CarSetup> Cars,
    float TimeLimit, RsbVec? BallAngularVelocity = null, bool Kickoff = false, int KickoffSeed = 0);

/// <summary>What happened during an episode, sampled every tick.</summary>
public sealed class EpisodeTrace
{
    public float Elapsed;
    public int GoalTeam = -1;
    public float GoalTime = float.NaN;
    public float FirstTouchTime = float.NaN;
    public int FirstToucher = -1;
    public RsbVec BallVelocityAfterFirstTouch;
    public RsbVec BallPositionAtFirstTouch;
    public RsbVec FinalBallPosition;
    public int Touches;
    public float LandedTime = float.NaN;
    public float MaxBallZ;
    public float MinGoalDistance = float.MaxValue;
    public bool CarLandedUpright;
    public List<(float Time, int Toucher)> TouchLog { get; } = new();
    public Dictionary<string, double> Metrics { get; } = new();
}

/// <summary>
/// A repeatable mechanics fixture: generates randomized starting states and judges each episode.
/// Seat 0 is always the bot under test; further seats are opponents or teammates.
/// </summary>
public abstract class Scenario
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual int SeatCount => 1;
    public virtual int TeamOf(int seat) => seat == 0 ? 0 : 1;

    /// <summary>Optional in-process opponent for seats &gt; 0; null means the opponent build (if any) or idle.</summary>
    public virtual IAgent? ScriptedOpponent(int seat) => null;

    public abstract EpisodeSetup Generate(Random random);

    /// <summary>Called every tick; return true to stop the episode early.</summary>
    public virtual bool ShouldStop(MatchSession session, EpisodeTrace trace) => trace.GoalTeam >= 0;

    /// <summary>Converts the finished trace into a success flag plus named metrics.</summary>
    public abstract bool Judge(EpisodeSetup setup, EpisodeTrace trace);

    protected static float Uniform(Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);
    protected static RsbVec V(float x, float y, float z) => new(x, y, z);
}
