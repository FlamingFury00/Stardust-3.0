using RLBot.Flat;
using Stardust.Simulator.Match;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>
/// The production Stardust bot class, run in-process on simulator packets. With a director set, the
/// decision layer is bypassed after its tactical assessment and the director chooses the action, so
/// a mechanic can be measured on its own; without one the full bot plays.
/// </summary>
public sealed class LabBot : global::Bot.Stardust
{
    public LabBot() : base("stardust-lab") { }

    /// <summary>Runs every tick in place of the decision layer. Null plays the full bot.</summary>
    public Action<LabBot>? Director { get; set; }

    public override void Run()
    {
        if (Director == null)
        {
            base.Run();
            return;
        }
        Assess(global::Bot.Tactics.OpponentPressure(LivingOpponents, RedUtils.Ball.MainBall, OurGoal.Location));
        Director(this);
    }
}

/// <summary>
/// Seat agent that feeds a <see cref="LabBot"/> the simulator's packet and ball prediction directly,
/// in place of an RLBot connection. RedUtils keeps its world state in statics, so only one
/// in-process bot can run at a time.
/// </summary>
public sealed class StardustAgent : ScriptedAgent
{
    private static readonly Type ManagerBot = typeof(RLBot.Manager.Bot);
    private FieldInfoT? fieldInfo;
    private MatchConfigurationT? matchConfig;
    private bool bound;

    public LabBot Bot { get; } = new();
    public override string Description => "stardust (in-process)";

    /// <summary>Supplies the session's field and match configuration, as the RLBot core would at startup.</summary>
    public void Attach(MatchSession session)
    {
        fieldInfo = session.FieldInfo;
        matchConfig = session.MatchConfig;
    }

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        if (!bound)
        {
            if (fieldInfo == null || matchConfig == null)
                throw new InvalidOperationException("Attach the agent to its session before the first tick.");
            Set("Index", self.Index);
            Set("Team", self.Team);
            Set("PlayerId", self.PlayerId);
            Set("FieldInfo", fieldInfo);
            Set("MatchConfig", matchConfig);
            // No renderer is connected; the bot skips render groups when rendering is off.
            typeof(RLBot.Manager.Renderer).GetProperty(nameof(RLBot.Manager.Renderer.CanRender))!
                .SetValue(((RLBot.Manager.Bot)Bot).Renderer, false);
            bound = true;
        }
        Set("BallPrediction", prediction);
        return Bot.GetOutput(packet);
    }

    private void Set(string property, object value) => ManagerBot.GetProperty(property)!.SetValue(Bot, value);
}
