using System.Text.Json.Serialization;
using Stardust.Simulator.Physics;

namespace Stardust.Simulator.Match;

/// <summary>
/// Per-player match statistics using the same categories as ballchasing.com replay analysis, so a
/// simulated series can be compared against published professional averages.
/// </summary>
public sealed class PlayerStats
{
    public const float SupersonicSpeed = 2200f;
    public const float BoostSpeed = 1410f;

    public double Seconds { get; set; }
    public double Distance { get; set; }
    public double SpeedIntegral { get; set; }
    public double SlowSeconds { get; set; }
    public double BoostSpeedSeconds { get; set; }
    public double SupersonicSeconds { get; set; }
    public double GroundSeconds { get; set; }
    public double WallSeconds { get; set; }
    public double LowAirSeconds { get; set; }
    public double HighAirSeconds { get; set; }
    public double BoostCollected { get; set; }
    public double BoostUsed { get; set; }
    public double BoostIntegral { get; set; }
    public double ZeroBoostSeconds { get; set; }
    public double FullBoostSeconds { get; set; }
    public int BigPads { get; set; }
    public int SmallPads { get; set; }
    public double StolenBoost { get; set; }
    public double OverfillBoost { get; set; }
    public double DefensiveThirdSeconds { get; set; }
    public double NeutralThirdSeconds { get; set; }
    public double OffensiveThirdSeconds { get; set; }
    public double BehindBallSeconds { get; set; }
    public double LastBackSeconds { get; set; }
    public double ClosestToBallSeconds { get; set; }
    public double DemoedSeconds { get; set; }
    public int Touches { get; set; }
    public int AerialTouches { get; set; }
    public int Goals { get; set; }
    public int OwnGoals { get; set; }
    public int Shots { get; set; }
    public int Saves { get; set; }
    public int Assists { get; set; }
    public int DemosInflicted { get; set; }
    public int DemosTaken { get; set; }
    public int KickoffsTaken { get; set; }
    public int KickoffFirstTouches { get; set; }
    public double KickoffTimeToBall { get; set; }

    [JsonIgnore] public double Minutes => Seconds / 60.0;
    public double AverageSpeed => Seconds > 0 ? SpeedIntegral / Seconds : 0;
    public double BoostPerMinute => Minutes > 0 ? BoostUsed / Minutes : 0;
    public double AverageBoost => Seconds > 0 ? BoostIntegral / Seconds : 0;
    public double Percent(double seconds) => Seconds > 0 ? 100.0 * seconds / Seconds : 0;

    public void Add(PlayerStats other)
    {
        foreach (var property in typeof(PlayerStats).GetProperties())
        {
            if (!property.CanWrite) continue;
            if (property.PropertyType == typeof(double))
                property.SetValue(this, (double)property.GetValue(this)! + (double)property.GetValue(other)!);
            else if (property.PropertyType == typeof(int))
                property.SetValue(this, (int)property.GetValue(this)! + (int)property.GetValue(other)!);
        }
    }
}

public sealed record GoalEvent(double Time, int Team, int? ScorerIndex, bool OwnGoal, double BallSpeed,
    int? AssistIndex, bool Overtime);

public sealed record KickoffEvent(int Number, int? FirstTouchTeam, double TimeToFirstTouch,
    double BallYAfter3s, int? Advantage, bool GoalWithin10s, int? GoalTeam);

public sealed class MatchResult
{
    public required string Blue { get; init; }
    public required string Orange { get; init; }
    public required int Seed { get; init; }
    public required int TeamSize { get; init; }
    public int BlueScore { get; set; }
    public int OrangeScore { get; set; }
    public bool Overtime { get; set; }
    public bool Draw { get; set; }
    public double GameSeconds { get; set; }
    public double WallSeconds { get; set; }
    public string? Error { get; set; }
    public string? Timing { get; set; }
    public List<GoalEvent> Goals { get; } = new();
    public List<KickoffEvent> Kickoffs { get; } = new();
    public List<PlayerSummary> Players { get; } = new();
    public int Winner => BlueScore > OrangeScore ? 0 : OrangeScore > BlueScore ? 1 : -1;
}

public sealed record PlayerSummary(int Index, int Team, string Name, string Build, PlayerStats Stats);

/// <summary>Accumulates ballchasing-style statistics from successive simulator snapshots.</summary>
public sealed class StatsTracker
{
    private readonly IReadOnlyList<Participant> players;
    private readonly float[] previousBoost;
    private readonly bool[] previousDemoed;

    public StatsTracker(IReadOnlyList<Participant> players)
    {
        this.players = players;
        previousBoost = new float[players.Count];
        previousDemoed = new bool[players.Count];
        Array.Fill(previousBoost, float.NaN);
    }

    public void ResetBoostBaseline(RsbCarState[] cars)
    {
        for (int i = 0; i < players.Count; i++)
        {
            previousBoost[i] = cars[i].Boost;
            previousDemoed[i] = cars[i].IsDemoed != 0;
        }
    }

    public void Sample(RsbCarState[] cars, in RsbBallState ball, float dt)
    {
        RsbVec ballPos = ball.Physics.Position;
        int[] closestPerTeam = { -1, -1 };
        float[] closestDistance = { float.MaxValue, float.MaxValue };
        int[] lastBackPerTeam = { -1, -1 };
        float[] lastBackDepth = { float.MinValue, float.MinValue };

        for (int i = 0; i < players.Count; i++)
        {
            if (cars[i].IsDemoed != 0) continue;
            int team = players[i].Team;
            float side = team == 0 ? -1 : 1;
            float distance = cars[i].Physics.Position.Distance(ballPos);
            if (distance < closestDistance[team])
            {
                closestDistance[team] = distance;
                closestPerTeam[team] = i;
            }
            float depth = cars[i].Physics.Position.Y * side;
            if (depth > lastBackDepth[team])
            {
                lastBackDepth[team] = depth;
                lastBackPerTeam[team] = i;
            }
        }

        for (int i = 0; i < players.Count; i++)
        {
            PlayerStats s = players[i].Stats;
            ref readonly RsbCarState car = ref cars[i];
            int team = players[i].Team;
            float side = team == 0 ? -1 : 1;
            s.Seconds += dt;

            bool demoed = car.IsDemoed != 0;
            if (demoed)
            {
                s.DemoedSeconds += dt;
                previousDemoed[i] = true;
                previousBoost[i] = car.Boost;
                continue;
            }

            float speed = car.Physics.Velocity.Length;
            s.Distance += speed * dt;
            s.SpeedIntegral += speed * dt;
            if (speed >= PlayerStats.SupersonicSpeed) s.SupersonicSeconds += dt;
            else if (speed >= PlayerStats.BoostSpeed) s.BoostSpeedSeconds += dt;
            else s.SlowSeconds += dt;

            float z = car.Physics.Position.Z;
            if (car.IsOnGround != 0)
            {
                if (z < 60 && car.Physics.Up.Z > 0.9f) s.GroundSeconds += dt;
                else s.WallSeconds += dt;
            }
            else if (z < 642.775f) s.LowAirSeconds += dt;
            else s.HighAirSeconds += dt;

            float boost = car.Boost;
            s.BoostIntegral += boost * dt;
            if (boost < 0.5f) s.ZeroBoostSeconds += dt;
            if (boost > 99.5f) s.FullBoostSeconds += dt;

            float previous = previousBoost[i];
            if (!float.IsNaN(previous) && !previousDemoed[i])
            {
                float delta = boost - previous;
                if (delta < 0) s.BoostUsed += -delta;
                else if (delta > 0.01f)
                {
                    s.BoostCollected += delta;
                    bool big = delta > 12.5f || boost >= 99.9f && previous < 88f;
                    if (big) s.BigPads++; else s.SmallPads++;
                    // Overfill: the part of a pad that the full tank could not absorb.
                    float padValue = big ? 100f : 12f;
                    s.OverfillBoost += MathF.Max(0, padValue - delta);
                    if (car.Physics.Position.Y * side < 0) s.StolenBoost += delta;
                }
            }
            previousBoost[i] = boost;
            previousDemoed[i] = false;

            float depth = car.Physics.Position.Y * side;
            if (depth > 1707f) s.DefensiveThirdSeconds += dt;
            else if (depth < -1707f) s.OffensiveThirdSeconds += dt;
            else s.NeutralThirdSeconds += dt;
            if (depth > ballPos.Y * side) s.BehindBallSeconds += dt;
            if (lastBackPerTeam[team] == i) s.LastBackSeconds += dt;
            if (closestPerTeam[team] == i) s.ClosestToBallSeconds += dt;
        }
    }
}
