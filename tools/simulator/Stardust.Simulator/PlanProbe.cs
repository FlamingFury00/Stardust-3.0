using System.Globalization;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator;

/// <summary>
/// Rebuilds a scenario episode's initial state in-process and runs the strike planner on it with
/// diagnostics, so planner decisions can be inspected without the bot process.
/// </summary>
public static class PlanProbe
{
    public static int Run(string suite, int episode, int seed)
    {
        Scenario scenario = Suites.Select(suite).First();
        var random = new Random(seed);
        EpisodeSetup setup = scenario.Generate(random);
        for (int i = 0; i < episode; i++) setup = scenario.Generate(random);

        using var arena = new SimArena();
        uint id = arena.AddCar(0);
        var session = new { };
        var state = arena.GetCar(id);
        CarSetup c = setup.Cars[0];
        state.Physics = ScenarioRunner.Orientation(c.Pitch, c.Yaw, c.Roll);
        state.Physics.Position = c.Position;
        state.Physics.Velocity = c.Velocity;
        state.IsOnGround = 1;
        state.Boost = c.Boost;
        arena.SetCar(id, state);
        arena.Ball = new RsbBallState { Physics = new RsbPhysics { Position = setup.BallPosition,
            Velocity = setup.BallVelocity.Length > 0.01f ? setup.BallVelocity : new RsbVec(0, 0, -0.01f),
            Forward = new RsbVec(1, 0, 0), Right = new RsbVec(0, 1, 0), Up = new RsbVec(0, 0, 1) } };
        state = arena.GetCar(id);

        var prediction = new RsbBallState[720];
        int count = arena.PredictBall(prediction);
        var slices = new BallSlice[count];
        for (int i = 0; i < count; i++)
        {
            var p = prediction[i].Physics;
            slices[i] = new BallSlice((i + 1) / 120f, new Vec3(p.Position.X, p.Position.Y, p.Position.Z),
                new Vec3(p.Velocity.X, p.Velocity.Y, p.Velocity.Z), new Vec3(p.AngularVelocity.X, p.AngularVelocity.Y, p.AngularVelocity.Z));
        }

        var car = new Car
        {
            Location = new Vec3(state.Physics.Position.X, state.Physics.Position.Y, state.Physics.Position.Z),
            Velocity = new Vec3(state.Physics.Velocity.X, state.Physics.Velocity.Y, state.Physics.Velocity.Z),
            AngularVelocity = new Vec3(state.Physics.AngularVelocity.X, state.Physics.AngularVelocity.Y, state.Physics.AngularVelocity.Z),
            Orientation = new Mat3x3(new Vec3(state.Physics.Forward.X, state.Physics.Forward.Y, state.Physics.Forward.Z),
                new Vec3(state.Physics.Right.X, state.Physics.Right.Y, state.Physics.Right.Z),
                new Vec3(state.Physics.Up.X, state.Physics.Up.Y, state.Physics.Up.Z)),
            IsGrounded = true,
            Boost = state.Boost,
        };
        Console.WriteLine($"episode {episode}: ball {setup.BallPosition} v{setup.BallVelocity}; car {setup.Cars[0].Position} yaw {c.Yaw:F2} v {c.Velocity}");
        bool quiet = Environment.GetEnvironmentVariable("STARDUST_PROBE_QUIET") == "1";
        var options = new StrikePlanner.Options { Log = quiet ? null : Console.WriteLine };
        var path = new BallPath(slices);
        StrikePlan plan = StrikePlanner.Plan(car, path, 0f, StrikeGoal.Shoot(0, Array.Empty<Car>()), options);
        Console.WriteLine(plan == null ? "no plan" : "chosen " + plan);

        // Planner cost without diagnostics.
        options.Log = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int repeats = 20;
        for (int i = 0; i < repeats; i++) StrikePlanner.Plan(car, path, 0f, StrikeGoal.Shoot(0, Array.Empty<Car>()), options);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"planner time {watch.Elapsed.TotalMilliseconds / repeats:F2} ms"));
        return 0;
    }
}
