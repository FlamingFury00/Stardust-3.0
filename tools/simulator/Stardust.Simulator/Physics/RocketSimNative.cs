using System.Reflection;
using System.Runtime.InteropServices;

namespace Stardust.Simulator.Physics;

[StructLayout(LayoutKind.Sequential)]
public struct RsbVec
{
    public float X, Y, Z;

    public RsbVec(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static RsbVec operator +(RsbVec a, RsbVec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static RsbVec operator -(RsbVec a, RsbVec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static RsbVec operator *(RsbVec a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public float Dot(RsbVec o) => X * o.X + Y * o.Y + Z * o.Z;
    public float Length => MathF.Sqrt(Dot(this));
    public float FlatLength => MathF.Sqrt(X * X + Y * Y);
    public float Distance(RsbVec o) => (this - o).Length;
    public float FlatDistance(RsbVec o) => MathF.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    public RsbVec Normalized() => Length > 1e-6f ? this * (1f / Length) : new RsbVec(0, 0, 0);
    public override string ToString() => FormattableString.Invariant($"({X:F0}, {Y:F0}, {Z:F0})");
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbPhysics
{
    public RsbVec Position;
    public RsbVec Forward, Right, Up;
    public RsbVec Velocity;
    public RsbVec AngularVelocity;
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbControls
{
    public float Throttle, Steer, Pitch, Yaw, Roll;
    public int Jump, Boost, Handbrake;
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbCarState
{
    public ulong LastHitTick;
    public RsbPhysics Physics;
    public RsbControls LastControls;
    public RsbVec LastHitBallPosition;
    public RsbVec LastHitRelativePosition;
    public RsbVec WorldContactNormal;
    public uint Id;
    public int Team;
    public int IsOnGround, HasJumped, HasDoubleJumped, HasFlipped;
    public int IsJumping, IsFlipping, IsSupersonic, IsDemoed, IsBoosting, HasWorldContact;
    public int WheelContactMask;
    public float Boost, JumpTime, FlipTime, AirTime, AirTimeSinceJump, DemoRespawnTimer;
    public float HandbrakeValue, SupersonicTime, TimeSinceBoosted;
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbBallState
{
    public RsbPhysics Physics;
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbPad
{
    public RsbVec Position;
    public int IsBig, IsActive;
    public float Cooldown;
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbEvents
{
    public int GoalTeam;
    public int Demolitions;
    public int Bumps;
}

[StructLayout(LayoutKind.Sequential)]
public struct RsbDemo
{
    public uint Bumper, Victim;
}

/// <summary>P/Invoke surface of tools/simulator/native (RocketSim + procedural Soccar arena).</summary>
internal static unsafe class RocketSimNative
{
    private const string Library = "rsbridge";

    static RocketSimNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(RocketSimNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library)
            return IntPtr.Zero;

        string? explicitPath = Environment.GetEnvironmentVariable("STARDUST_RSBRIDGE");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitPath))
            candidates.Add(explicitPath);
        string fileName = OperatingSystem.IsWindows() ? "rsbridge.dll"
            : OperatingSystem.IsMacOS() ? "librsbridge.dylib" : "librsbridge.so";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "build", fileName));

        foreach (string candidate in candidates)
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                return handle;

        throw new DllNotFoundException(
            $"Could not load the RocketSim bridge ({fileName}). Build it with tools/simulator/build.sh " +
            "or point STARDUST_RSBRIDGE at the compiled library.");
    }

    [DllImport(Library)] public static extern int rsb_init();
    [DllImport(Library)] public static extern int rsb_mesh_stats(out int triangles, out int vertices);
    [DllImport(Library)] public static extern IntPtr rsb_arena_create(float tickRate);
    [DllImport(Library)] public static extern void rsb_arena_destroy(IntPtr arena);
    [DllImport(Library)] public static extern uint rsb_add_car(IntPtr arena, int team, int preset);
    [DllImport(Library)] public static extern void rsb_car_hitbox(IntPtr arena, uint id, out RsbVec size, out RsbVec offset);
    [DllImport(Library)] public static extern void rsb_reset_kickoff(IntPtr arena, int seed);
    [DllImport(Library)] public static extern void rsb_step(IntPtr arena, int ticks);
    [DllImport(Library)] public static extern ulong rsb_tick_count(IntPtr arena);
    [DllImport(Library)] public static extern float rsb_tick_time(IntPtr arena);
    [DllImport(Library)] public static extern void rsb_get_ball(IntPtr arena, out RsbBallState state);
    [DllImport(Library)] public static extern void rsb_set_ball(IntPtr arena, in RsbBallState state);
    [DllImport(Library)] public static extern int rsb_get_car(IntPtr arena, uint id, out RsbCarState state);
    [DllImport(Library)] public static extern int rsb_set_car(IntPtr arena, uint id, in RsbCarState state);
    [DllImport(Library)] public static extern int rsb_set_controls(IntPtr arena, uint id, in RsbControls controls);
    [DllImport(Library)] public static extern int rsb_pad_count(IntPtr arena);
    [DllImport(Library)] public static extern void rsb_get_pads(IntPtr arena, RsbPad* pads, int capacity);
    [DllImport(Library)] public static extern void rsb_set_pad(IntPtr arena, int index, int active, float cooldown);
    [DllImport(Library)] public static extern void rsb_poll_events(IntPtr arena, out RsbEvents events, RsbDemo* demos, int capacity, out int demoCount);
    [DllImport(Library)] public static extern int rsb_is_ball_scored(IntPtr arena);
    [DllImport(Library)] public static extern int rsb_predict_ball(IntPtr arena, int ticks, RsbBallState* output, int capacity);
    [DllImport(Library)] public static extern int rsb_rollout_ball(IntPtr arena, in RsbBallState start, int ticks, RsbBallState* output, int capacity);
}
