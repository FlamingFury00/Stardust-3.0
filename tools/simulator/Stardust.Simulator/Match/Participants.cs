using RLBot.Flat;
using Stardust.Simulator.Physics;

namespace Stardust.Simulator.Match;

/// <summary>A car in the arena and the agent that drives it.</summary>
public sealed class Participant
{
    public required int Index { get; init; }
    public required int Team { get; init; }
    public required string Name { get; init; }
    public required int PlayerId { get; init; }
    public required IAgent Agent { get; init; }
    public uint CarId { get; set; }
    public RsbVec HitboxSize { get; set; }
    public RsbVec HitboxOffset { get; set; }
    public ControllerStateT LastInput { get; set; } = new();
    public PlayerStats Stats { get; } = new();

    // Packet bookkeeping mirrored from the real game.
    public TouchT? LatestTouch { get; set; }
    public ulong LastSeenHitTick { get; set; } = ulong.MaxValue;
    public int DoubleJumpTicks { get; set; }
    public float LastCountedTouch { get; set; } = float.NegativeInfinity;
    public bool PreviousDoubleJumped { get; set; }
    public uint Goals, OwnGoals, Assists, Saves, Shots, Demolitions, Score;
}

/// <summary>Anything that turns a game packet into controls: an external RLBot process or an in-process script.</summary>
public interface IAgent : IDisposable
{
    string Description { get; }

    /// <summary>Delivers the tick's state. External agents begin computing concurrently.</summary>
    void Send(Participant self, byte[] framedPrediction, byte[] framedPacket, GamePacketT packet,
        BallPredictionT prediction, IReadOnlyList<byte[]> framedComms);

    /// <summary>Returns the controls for the tick delivered by the last <see cref="Send"/>.</summary>
    ControllerStateT Receive(Participant self, List<MatchCommT> outgoingComms);
}

/// <summary>Deterministic in-process opponent used by fixtures that need a simple, repeatable adversary.</summary>
public abstract class ScriptedAgent : IAgent
{
    private GamePacketT? packet;
    private BallPredictionT? prediction;

    public abstract string Description { get; }

    public void Send(Participant self, byte[] framedPrediction, byte[] framedPacket, GamePacketT packet,
        BallPredictionT prediction, IReadOnlyList<byte[]> framedComms)
    {
        this.packet = packet;
        this.prediction = prediction;
    }

    public ControllerStateT Receive(Participant self, List<MatchCommT> outgoingComms) =>
        packet == null ? new ControllerStateT() : Act(self, packet, prediction!);

    protected abstract ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction);

    public virtual void Dispose() { }
}

/// <summary>Sits still. Used for unopposed mechanics fixtures.</summary>
public sealed class IdleAgent : ScriptedAgent
{
    public override string Description => "idle";
    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction) => new();
}
