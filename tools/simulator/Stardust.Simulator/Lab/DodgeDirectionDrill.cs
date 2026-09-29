using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>Measure the real Dodge action's first impulse in paired mirrored airborne states.</summary>
public sealed class DodgeDirectionDrill : Drill
{
    private int episode;
    private float speed, angle;
    private RsbVec expected, pairedVelocity;
    private float velocityError, mirrorError;
    public override string Name => "dodge-direction";
    public override string Description => "Apply the requested world-space dodge impulse with symmetric pitch/yaw compensation.";
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Rate(0.99), Criterion.Quantile("velocity-error", 0.99, 3, atLeast: false),
        Criterion.Quantile("mirror-error", 0.99, 3, atLeast: false),
    };
    public override EpisodeSetup Generate(Random r)
    {
        if (episode % 2 == 0)
        {
            speed = new[] { -900f, 0f, 1000f, 2000f }[r.Next(4)];
            angle = Uniform(r, 0.22f, 2.9f);
        }
        float yaw = MathF.PI / 2 + (episode % 2 == 0 ? -angle : angle);
        var forward = Heading(yaw);
        var f = new Vec3(forward.X, forward.Y, 0);
        var input = DodgeModel.InputToward(f, Vec3.Y, speed);
        Vec3 impulse = DodgeModel.Impulse(f, input.Pitch, input.Yaw, speed);
        Vec3 velocity = (f * speed + impulse).Cap(0, Car.MaxSpeed);
        expected = V(velocity.x, velocity.y, 0);
        return new EpisodeSetup(V(3500, 3500, 93), V(0, 0, 0), new[]
        {
            new CarSetup(V(0, 0, 500), yaw, forward * speed, 0,
                OnGround: false, HasJumped: true)
        }, SimArena.TickTime);
    }
    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        velocityError = mirrorError = float.PositiveInfinity;
        Subject.Director = bot => bot.Action = new Dodge(Vec3.Y);
    }
    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        RsbVec velocity = session.Cars[0].Physics.Velocity;
        velocity = V(velocity.X, velocity.Y, 0);
        velocityError = velocity.Distance(expected);
        if (episode % 2 == 0)
        {
            pairedVelocity = velocity;
            mirrorError = 0;
        }
        else
            mirrorError = velocity.Distance(V(-pairedVelocity.X, pairedVelocity.Y, 0));
    }
    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Metrics["velocity-error"] = velocityError;
        if (episode % 2 == 1) trace.Metrics["mirror-error"] = mirrorError;
        episode++;
        return velocityError <= 3 && mirrorError <= 3;
    }
}
