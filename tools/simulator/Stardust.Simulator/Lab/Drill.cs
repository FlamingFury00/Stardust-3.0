using System.Globalization;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>A pass condition on a drill's outcome, e.g. "median hold-s ≥ 4".</summary>
public sealed record Criterion(string Label, Func<ScenarioOutcome, double> Measure, double Threshold, bool AtLeast = true)
{
    public static Criterion Rate(double threshold) =>
        new("success rate", o => o.Rate, threshold);

    public static Criterion Median(string metric, double threshold, bool atLeast = true) =>
        Quantile(metric, 0.5, threshold, atLeast);

    public static Criterion Quantile(string metric, double q, double threshold, bool atLeast = true) =>
        new(q == 0.5 ? $"median {metric}" : string.Create(CultureInfo.InvariantCulture, $"p{q * 100:F0} {metric}"),
            o => o.Metrics.TryGetValue(metric, out var values) ? Statistics.Quantile(values, q) : double.NaN,
            threshold, atLeast);

    public static Criterion Mean(string metric, double threshold, bool atLeast = true) =>
        new($"mean {metric}", o => o.Metrics.TryGetValue(metric, out var values) && values.Count > 0 ? values.Average() : double.NaN,
            threshold, atLeast);

    public bool Passes(double value) => double.IsFinite(value) && (AtLeast ? value >= Threshold : value <= Threshold);
}

/// <summary>
/// A mechanics drill: a fixture played by the in-process bot, usually with a director that forces the
/// mechanic under test, judged against the pass criteria a pro-level execution should meet.
/// </summary>
public abstract class Drill : Scenario
{
    /// <summary>The in-process bot under test; set by the lab before the first episode.</summary>
    public StardustAgent Agent { get; set; } = null!;

    public abstract IReadOnlyList<Criterion> Criteria { get; }

    protected LabBot Subject => Agent.Bot;

    /// <summary>Episode whose ticks are printed (car speed, ball on the car frame, controls, action), or -1.</summary>
    public int TraceEpisode { get; set; } = -1;
    private int episode = -1;

    public sealed override void Begin(MatchSession session, EpisodeSetup setup)
    {
        episode++;
        Start(session, setup);
    }

    public sealed override void Observe(MatchSession session, EpisodeTrace trace)
    {
        if (episode == TraceEpisode)
        {
            RsbCarState car = session.Cars[0];
            RsbVec local = Local(car.Physics, session.Ball.Physics.Position);
            RsbVec relative = session.Ball.Physics.Velocity - car.Physics.Velocity;
            var c = session.Participants[0].LastInput;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"t={trace.Elapsed:F3} speed={car.Physics.Velocity.Dot(car.Physics.Forward),6:F0} ground={car.IsOnGround} " +
                $"ball=({local.X,5:F0},{local.Y,5:F0},{local.Z,5:F0}) rel=({relative.Dot(car.Physics.Forward),5:F0},{relative.Dot(car.Physics.Right),5:F0},{relative.Dot(car.Physics.Up),5:F0}) " +
                $"thr={c.Throttle,5:F2} steer={c.Steer,5:F2} boost={(c.Boost ? 1 : 0)} jump={(c.Jump ? 1 : 0)} p/y/r=({c.Pitch:F1},{c.Yaw:F1},{c.Roll:F1}) " +
                $"fuel={car.Boost:F0} action={Subject.Action?.GetType().Name ?? "-"}"));
        }
        Measure(session, trace);
    }

    /// <summary>Per-episode setup: reset measurements and set the director.</summary>
    protected virtual void Start(MatchSession session, EpisodeSetup setup) { }

    /// <summary>Per-tick measurement.</summary>
    protected virtual void Measure(MatchSession session, EpisodeTrace trace) { }

    /// <summary>Car-frame coordinates of a world point: x forward, y right, z up.</summary>
    protected static RsbVec Local(in RsbPhysics car, RsbVec world)
    {
        RsbVec d = world - car.Position;
        return new RsbVec(d.Dot(car.Forward), d.Dot(car.Right), d.Dot(car.Up));
    }

    /// <summary>Whether the ball rests on the car's roof: above it, within the hitbox footprint.</summary>
    protected static bool OnRoof(MatchSession session, int seat)
    {
        Participant p = session.Participants[seat];
        RsbCarState car = session.Cars[seat];
        RsbVec local = Local(car.Physics, session.Ball.Physics.Position);
        return local.Z > 110f && MathF.Abs(local.X - p.HitboxOffset.X) < 110f && MathF.Abs(local.Y) < 90f;
    }

    /// <summary>Ball centre height, relative to the car origin, at which it rests on the roof.</summary>
    protected static float RoofRestHeight(Participant p) => p.HitboxOffset.Z + p.HitboxSize.Z / 2f + SimArena.BallRadius;

    /// <summary>Unit heading vector for a yaw angle.</summary>
    protected static RsbVec Heading(float yaw) => new(MathF.Cos(yaw), MathF.Sin(yaw), 0);

    protected static float Degrees(float radians) => radians * 180f / MathF.PI;

    /// <summary>Unsigned angle between the flat projections of two vectors, in degrees.</summary>
    protected static float FlatAngle(RsbVec a, RsbVec b)
    {
        float dot = a.X * b.X + a.Y * b.Y, cross = a.X * b.Y - a.Y * b.X;
        return MathF.Abs(Degrees(MathF.Atan2(cross, dot)));
    }
}

public static class Statistics
{
    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        double position = q * (sorted.Count - 1);
        int low = (int)Math.Floor(position), high = (int)Math.Ceiling(position);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }
}
