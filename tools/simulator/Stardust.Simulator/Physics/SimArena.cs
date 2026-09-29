namespace Stardust.Simulator.Physics;

/// <summary>Owning handle for one RocketSim Soccar arena.</summary>
public sealed unsafe class SimArena : IDisposable
{
    public const float TickRate = 120f;
    public const float TickTime = 1f / TickRate;
    public const float BallRadius = 91.25f;

    private IntPtr handle;
    private readonly RsbDemo[] demoBuffer = new RsbDemo[16];

    public SimArena()
    {
        if (RocketSimNative.rsb_init() != 0)
            throw new InvalidOperationException("RocketSim initialization failed.");
        handle = RocketSimNative.rsb_arena_create(TickRate);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("RocketSim arena creation failed.");
        PadCount = RocketSimNative.rsb_pad_count(handle);
    }

    public int PadCount { get; }
    public ulong TickCount => RocketSimNative.rsb_tick_count(Handle);

    private IntPtr Handle => handle != IntPtr.Zero ? handle : throw new ObjectDisposedException(nameof(SimArena));

    public uint AddCar(int team, int preset = 0) => RocketSimNative.rsb_add_car(Handle, team, preset);

    public (RsbVec Size, RsbVec Offset) Hitbox(uint id)
    {
        RocketSimNative.rsb_car_hitbox(Handle, id, out RsbVec size, out RsbVec offset);
        return (size, offset);
    }

    /// <summary>
    /// Resets to a random kickoff. RocketSim shuffles the spawns with a linear congruential engine
    /// seeded directly, and nearby seeds shuffle almost alike, so the consecutive kickoffs of a game
    /// would repeat one spawn several times running; a SplitMix64 finaliser decorrelates them.
    /// </summary>
    public void ResetKickoff(int seed) => RocketSimNative.rsb_reset_kickoff(Handle, Mix(seed));

    private static int Mix(int seed)
    {
        ulong z = (uint)seed + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (int)((z ^ (z >> 31)) & 0x7FFFFFFF);
    }
    public void Step(int ticks = 1) => RocketSimNative.rsb_step(Handle, ticks);

    public RsbBallState Ball
    {
        get
        {
            RocketSimNative.rsb_get_ball(Handle, out RsbBallState state);
            return state;
        }
        set => RocketSimNative.rsb_set_ball(Handle, value);
    }

    public RsbCarState GetCar(uint id)
    {
        if (RocketSimNative.rsb_get_car(Handle, id, out RsbCarState state) != 0)
            throw new ArgumentException($"Unknown car id {id}.");
        return state;
    }

    public void SetCar(uint id, in RsbCarState state)
    {
        if (RocketSimNative.rsb_set_car(Handle, id, state) != 0)
            throw new ArgumentException($"Unknown car id {id}.");
    }

    public void SetControls(uint id, in RsbControls controls) =>
        RocketSimNative.rsb_set_controls(Handle, id, controls);

    public RsbPad[] GetPads()
    {
        var pads = new RsbPad[PadCount];
        fixed (RsbPad* pointer = pads)
            RocketSimNative.rsb_get_pads(Handle, pointer, pads.Length);
        return pads;
    }

    public void SetPad(int index, bool active, float cooldown) =>
        RocketSimNative.rsb_set_pad(Handle, index, active ? 1 : 0, cooldown);

    public (RsbEvents Events, RsbDemo[] Demos) PollEvents()
    {
        RsbEvents events;
        int count;
        fixed (RsbDemo* pointer = demoBuffer)
            RocketSimNative.rsb_poll_events(Handle, out events, pointer, demoBuffer.Length, out count);
        return (events, demoBuffer.AsSpan(0, count).ToArray());
    }

    public bool IsBallScored => RocketSimNative.rsb_is_ball_scored(Handle) != 0;

    /// <summary>Fills <paramref name="output"/> with the ball state after 1..N ticks.</summary>
    public int PredictBall(RsbBallState[] output)
    {
        fixed (RsbBallState* pointer = output)
            return RocketSimNative.rsb_predict_ball(Handle, output.Length, pointer, output.Length);
    }

    public int RolloutBall(in RsbBallState start, RsbBallState[] output)
    {
        fixed (RsbBallState* pointer = output)
            return RocketSimNative.rsb_rollout_ball(Handle, start, output.Length, pointer, output.Length);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero)
            return;
        RocketSimNative.rsb_arena_destroy(handle);
        handle = IntPtr.Zero;
    }
}
