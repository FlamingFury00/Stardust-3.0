using System.Globalization;
using System.Text;
using RLBot.Flat;
using Stardust.Simulator.Physics;

namespace Stardust.Simulator.Match;

/// <summary>
/// Compact trajectory recorder. Each captured frame stores the ball and every car (position,
/// orientation, velocity, boost, contact flags) plus the controls actually applied, which is
/// enough to render a replay and to audit individual decisions.
/// </summary>
public sealed class ReplayRecorder
{
    private readonly StringBuilder frames = new();
    private readonly int stride;
    private int counter;
    private int frameCount;

    public ReplayRecorder(float hz = 30f)
    {
        stride = Math.Max(1, (int)MathF.Round(SimArena.TickRate / hz));
        Hz = SimArena.TickRate / stride;
    }

    public float Hz { get; }

    public void Capture(MatchSession session, MatchPhase phase)
    {
        seen ??= session.Participants;
        if (counter++ % stride != 0)
            return;
        var inv = CultureInfo.InvariantCulture;
        RsbPhysics b = session.Ball.Physics;
        if (frameCount++ > 0)
            frames.Append(",\n");
        frames.Append('[').Append(session.Time.ToString("F3", inv)).Append(',').Append((int)phase).Append(',');
        AppendVec(frames, b.Position);
        frames.Append(',');
        AppendVec(frames, b.Velocity);
        foreach (Participant p in session.Participants)
        {
            RsbCarState c = session.Cars[p.Index];
            ControllerStateT u = p.LastInput;
            frames.Append(",[");
            AppendVec(frames, c.Physics.Position);
            frames.Append(',');
            AppendVec(frames, c.Physics.Forward, "F3");
            frames.Append(',');
            AppendVec(frames, c.Physics.Up, "F3");
            frames.Append(',');
            AppendVec(frames, c.Physics.Velocity);
            frames.Append(',').Append(((int)c.Boost).ToString(inv));
            int flags = (c.IsOnGround != 0 ? 1 : 0) | (c.IsDemoed != 0 ? 2 : 0) | (c.IsSupersonic != 0 ? 4 : 0) |
                (c.HasJumped != 0 ? 8 : 0) | (c.HasDoubleJumped != 0 ? 16 : 0) | (c.HasFlipped != 0 ? 32 : 0) |
                (u.Jump ? 64 : 0) | (u.Boost ? 128 : 0) | (u.Handbrake ? 256 : 0);
            frames.Append(',').Append(flags.ToString(inv));
            frames.Append(',').Append(u.Throttle.ToString("F2", inv)).Append(',').Append(u.Steer.ToString("F2", inv));
            frames.Append(']');
        }
        frames.Append(']');
    }

    private static void AppendVec(StringBuilder sb, RsbVec v, string format = "F0")
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append('[').Append(v.X.ToString(format, inv)).Append(',').Append(v.Y.ToString(format, inv))
            .Append(',').Append(v.Z.ToString(format, inv)).Append(']');
    }

    private IReadOnlyList<Participant>? seen;

    public void Save(string path, IEnumerable<Participant> participants, string title)
    {
        if (!participants.Any() && seen != null) participants = seen;
        var sb = new StringBuilder();
        sb.Append("{\"title\":").Append(System.Text.Json.JsonSerializer.Serialize(title));
        sb.Append(",\"hz\":").Append(Hz.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"players\":[");
        sb.Append(string.Join(",", participants.Select(p =>
            $"{{\"name\":{System.Text.Json.JsonSerializer.Serialize(p.Name)},\"team\":{p.Team}}}")));
        sb.Append("],\"format\":\"[t,phase,ballPos,ballVel,...cars:[pos,forward,up,vel,boost,flags,throttle,steer]]\"");
        sb.Append(",\"frames\":[\n").Append(frames).Append("\n]}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString());
    }
}
