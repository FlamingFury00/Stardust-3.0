using System.Diagnostics;
using RLBot.Flat;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Protocol;

namespace Stardust.Simulator.Match;

public sealed class MatchOptions
{
    public int Seed { get; set; } = 1;
    public float MatchSeconds { get; set; } = 300f;
    public float CountdownSeconds { get; set; } = 1.0f;
    public float GoalPauseSeconds { get; set; } = 0.5f;
    public float KickoffAutoActiveSeconds { get; set; } = 2.0f;
    public float OvertimeCapSeconds { get; set; } = 180f;
    /// <summary>
    /// Seeded offset (uu) applied to kickoff spawns. Deterministic bots otherwise replay the same
    /// game from each of the five spawns until the first goal, so series need a little variety.
    /// </summary>
    public float SpawnJitter { get; set; } = 10f;
    public string? LogDirectory { get; set; }
    public ReplayRecorder? Replay { get; set; }
}

/// <summary>Accumulated wall-clock cost of each tick stage (Stopwatch ticks).</summary>
public sealed class TickTiming
{
    public long Build, Send, Wait, Physics, Ticks;

    public override string ToString()
    {
        double Ms(long t) => Ticks > 0 ? t * 1000.0 / Stopwatch.Frequency / Ticks : 0;
        return FormattableString.Invariant(
            $"per tick: build {Ms(Build):F3} ms, send {Ms(Send):F3} ms, bots {Ms(Wait):F3} ms, physics {Ms(Physics):F3} ms");
    }
}

/// <summary>One seat at the table: a team, a display name, and either a bot build or a scripted agent.</summary>
public sealed record Seat(int Team, string Name, BotBuild? Build = null, IAgent? Agent = null);

/// <summary>
/// Lockstep RocketSim match driven through the real RLBot v5 protocol. Each tick the simulator
/// sends the ball prediction and game packet, waits for every agent's controls, then advances
/// physics by exactly one 120 Hz tick.
/// </summary>
public sealed class MatchSession : IDisposable
{
    private const float ShotHorizon = 3.0f;
    private const float TouchDebounceSeconds = 0.25f;
    private readonly SimArena arena = new();
    private readonly List<Participant> participants = new();
    private readonly RsbBallState[] prediction = new RsbBallState[PacketBuilder.PredictionTicks];
    private readonly List<byte[]>[] pendingComms;
    private readonly StatsTracker stats;
    private readonly MatchOptions options;
    private RsbCarState[] cars = Array.Empty<RsbCarState>();
    private RsbBallState ball;
    private uint frame;
    private int predictionCount;
    private int lastToucher = -1;
    private int previousToucher = -1;
    private double previousTouchTime = double.NegativeInfinity;
    private double lastTouchTime = double.NegativeInfinity;
    private int threatenedGoal = -1;     // team whose goal the untouched prediction currently enters
    private bool touchedThisTick;
    private readonly bool needsPredictionObjects;

    public MatchSession(IReadOnlyList<Seat> seats, MatchOptions options)
    {
        this.options = options;
        var external = new List<BotLauncher.Request>();
        var seatAgents = new IAgent?[seats.Count];

        for (int i = 0; i < seats.Count; i++)
        {
            Seat seat = seats[i];
            if (seat.Build != null)
                external.Add(new BotLauncher.Request(seat.Build, i, seat.Team, seat.Name, 1000 + i));
            else
                seatAgents[i] = seat.Agent ?? new IdleAgent();
        }

        for (int i = 0; i < seats.Count; i++)
        {
            uint id = arena.AddCar(seats[i].Team);
            var hitbox = arena.Hitbox(id);
            participants.Add(new Participant
            {
                Index = i, Team = seats[i].Team, Name = seats[i].Name, PlayerId = 1000 + i,
                Agent = seatAgents[i] ?? new IdleAgent(), CarId = id,
                HitboxSize = hitbox.Size, HitboxOffset = hitbox.Offset,
            });
        }

        FieldInfo = PacketBuilder.FieldInfo(arena.GetPads());
        MatchConfig = PacketBuilder.MatchConfiguration(participants);
        if (external.Count > 0)
        {
            var launched = BotLauncher.Launch(external, MatchConfig, FieldInfo, options.LogDirectory);
            for (int k = 0; k < external.Count; k++)
            {
                Participant old = participants[external[k].Index];
                participants[external[k].Index] = new Participant
                {
                    Index = old.Index, Team = old.Team, Name = old.Name, PlayerId = old.PlayerId,
                    Agent = launched[k], CarId = old.CarId, HitboxSize = old.HitboxSize, HitboxOffset = old.HitboxOffset,
                };
            }
        }

        pendingComms = participants.Select(_ => new List<byte[]>()).ToArray();
        needsPredictionObjects = participants.Any(p => p.Agent is ScriptedAgent);
        stats = new StatsTracker(participants);
        Snapshot();
    }

    public IReadOnlyList<Participant> Participants => participants;
    public SimArena Arena => arena;
    public FieldInfoT FieldInfo { get; }
    public MatchConfigurationT MatchConfig { get; }
    public float Time { get; private set; } = 10f;
    public float TimeRemaining { get; private set; }
    public bool Overtime { get; private set; }
    public int[] Score { get; } = new int[2];
    public RsbCarState[] Cars => cars;
    public RsbBallState Ball => ball;
    public int LastToucher => lastToucher;
    public bool TouchedThisTick => touchedThisTick;
    /// <summary>True when more than one car touched the ball on the latest tick (e.g. a mirrored kickoff).</summary>
    public bool SimultaneousTouch { get; private set; }
    public bool StatsEnabled { get; set; } = true;
    public TickTiming Timing { get; } = new();

    /// <summary>Advances the clock without physics so bots observe a discontinuity and reset.</summary>
    public void BreakClock(float seconds = 1f)
    {
        Time += seconds;
        foreach (Participant p in participants)
            p.LastInput = new ControllerStateT();
    }

    private void JitterSpawns(int seed, float jitter)
    {
        if (jitter <= 0f) return;
        var random = new Random(seed);
        foreach (Participant p in participants)
        {
            RsbCarState car = arena.GetCar(p.CarId);
            car.Physics.Position = new RsbVec(car.Physics.Position.X + (float)(random.NextDouble() * 2 - 1) * jitter,
                car.Physics.Position.Y + (float)(random.NextDouble() * 2 - 1) * jitter, car.Physics.Position.Z);
            arena.SetCar(p.CarId, car);
        }
    }

    public void Snapshot()
    {
        if (cars.Length != participants.Count)
            cars = new RsbCarState[participants.Count];
        for (int i = 0; i < participants.Count; i++)
            cars[i] = arena.GetCar(participants[i].CarId);
        ball = arena.Ball;
    }

    public void ResetTouchState()
    {
        lastToucher = previousToucher = -1;
        lastTouchTime = previousTouchTime = double.NegativeInfinity;
        threatenedGoal = -1;
        foreach (Participant p in participants)
            p.LastSeenHitTick = arena.GetCar(p.CarId).LastHitTick;
    }

    /// <summary>Runs one lockstep tick in the given phase. Physics advances only while play is live.</summary>
    public void Tick(MatchPhase phase)
    {
        bool live = phase is MatchPhase.Kickoff or MatchPhase.Active;
        long started = Stopwatch.GetTimestamp();
        Snapshot();
        predictionCount = arena.PredictBall(prediction);

        GamePacketT packet = BuildPacket(phase);
        byte[] framedPrediction = PacketBuilder.FramePrediction(prediction, predictionCount, Time);
        BallPredictionT? ballPrediction = needsPredictionObjects
            ? PacketBuilder.Prediction(prediction, predictionCount, Time) : null;
        byte[] framedPacket = RLBotConnection.Frame(CoreMessageUnion.FromGamePacket(packet));
        long built = Stopwatch.GetTimestamp();
        Timing.Build += built - started;

        for (int i = 0; i < participants.Count; i++)
        {
            participants[i].Agent.Send(participants[i], framedPrediction, framedPacket, packet, ballPrediction!, pendingComms[i]);
            pendingComms[i].Clear();
        }

        long sent = Stopwatch.GetTimestamp();
        Timing.Send += sent - built;
        var outgoing = new List<MatchCommT>();
        for (int i = 0; i < participants.Count; i++)
        {
            outgoing.Clear();
            ControllerStateT controls = participants[i].Agent.Receive(participants[i], outgoing);
            participants[i].LastInput = controls;
            foreach (MatchCommT comm in outgoing)
                Relay(i, comm);
            if (live)
                arena.SetControls(participants[i].CarId, ToNative(controls));
        }

        long received = Stopwatch.GetTimestamp();
        Timing.Wait += received - sent;
        Timing.Ticks++;
        options.Replay?.Capture(this, phase);
        frame++;
        if (!live)
        {
            Time += SimArena.TickTime;
            return;
        }

        int threatBefore = PredictedGoalTeam();
        arena.Step(1);
        Time += SimArena.TickTime;
        Snapshot();
        Timing.Physics += Stopwatch.GetTimestamp() - received;
        DetectTouches(threatBefore);
        foreach (Participant p in participants)
        {
            ref readonly RsbCarState car = ref cars[p.Index];
            if (car.HasDoubleJumped != 0 && !p.PreviousDoubleJumped) p.DoubleJumpTicks = 13;
            else if (p.DoubleJumpTicks > 0) p.DoubleJumpTicks--;
            p.PreviousDoubleJumped = car.HasDoubleJumped != 0;
        }
        if (StatsEnabled)
            stats.Sample(cars, ball, SimArena.TickTime);
    }

    private void Relay(int sender, MatchCommT comm)
    {
        comm.Index = (uint)sender;
        comm.Team = (uint)participants[sender].Team;
        byte[] framed = RLBotConnection.Frame(CoreMessageUnion.FromMatchComm(comm));
        for (int i = 0; i < participants.Count; i++)
        {
            if (i == sender || (comm.TeamOnly && participants[i].Team != participants[sender].Team))
                continue;
            pendingComms[i].Add(framed);
        }
    }

    private static RsbControls ToNative(ControllerStateT c) => new()
    {
        Throttle = c.Throttle, Steer = c.Steer, Pitch = c.Pitch, Yaw = c.Yaw, Roll = c.Roll,
        Jump = c.Jump ? 1 : 0, Boost = c.Boost ? 1 : 0, Handbrake = c.Handbrake ? 1 : 0,
    };

    private GamePacketT BuildPacket(MatchPhase phase)
    {
        RsbPad[] pads = arena.GetPads();
        return new GamePacketT
        {
            Players = participants.Select(p => PacketBuilder.Player(p, cars[p.Index])).ToList(),
            BoostPads = pads.Select(pad => new BoostPadStateT
            {
                IsActive = pad.IsActive != 0,
                Timer = pad.IsActive != 0 ? 0 : MathF.Max(0, PacketBuilder.PadRespawn(pad.IsBig != 0) - pad.Cooldown),
            }).ToList(),
            Balls = new List<BallInfoT> { PacketBuilder.Ball(ball) },
            MatchInfo = new MatchInfoT
            {
                SecondsElapsed = Time,
                GameTimeRemaining = MathF.Max(0, TimeRemaining),
                IsOvertime = Overtime,
                IsUnlimitedTime = false,
                MatchPhase = phase,
                WorldGravityZ = PacketBuilder.Gravity,
                GameSpeed = 1f,
                LastSpectated = 0,
                FrameNum = frame,
            },
            Teams = new List<TeamInfoT>
            {
                new() { TeamIndex = 0, Score = (uint)Score[0] },
                new() { TeamIndex = 1, Score = (uint)Score[1] },
            },
        };
    }

    /// <summary>Team whose goal the current untouched prediction enters within the shot horizon, else -1.</summary>
    public int PredictedGoalTeam()
    {
        int horizon = Math.Min(predictionCount, (int)(ShotHorizon * SimArena.TickRate));
        for (int i = 0; i < horizon; i++)
        {
            float y = prediction[i].Physics.Position.Y;
            if (MathF.Abs(y) > 5124.25f + SimArena.BallRadius)
                return y > 0 ? 1 : 0;
        }
        return -1;
    }

    private void DetectTouches(int threatBefore)
    {
        touchedThisTick = false;
        ulong now = arena.TickCount;
        int toucher = -1;
        int touchersThisTick = 0;
        foreach (Participant p in participants)
        {
            ref readonly RsbCarState car = ref cars[p.Index];
            if (car.LastHitTick == ulong.MaxValue || car.LastHitTick == p.LastSeenHitTick)
                continue;
            touchersThisTick++;
            p.LastSeenHitTick = car.LastHitTick;
            float hitTime = Time - (now - car.LastHitTick) * SimArena.TickTime;
            RsbVec contact = car.LastHitBallPosition + car.LastHitRelativePosition;
            RsbVec normal = (car.LastHitRelativePosition * -1f).Normalized();
            p.LatestTouch = new TouchT
            {
                GameSeconds = hitTime, Location = PacketBuilder.V(contact), Normal = PacketBuilder.V(normal), BallIndex = 0,
            };
            // Continuous contact reports a hit every tick; statistics count a touch only after a
            // short separation, like replay analysis tools do.
            if (StatsEnabled && hitTime - p.LastCountedTouch > TouchDebounceSeconds)
            {
                p.Stats.Touches++;
                if (car.IsOnGround == 0 && ball.Physics.Position.Z > 300) p.Stats.AerialTouches++;
            }
            p.LastCountedTouch = hitTime;
            toucher = p.Index;
        }
        if (toucher < 0)
        {
            threatenedGoal = threatBefore;
            return;
        }

        touchedThisTick = true;
        SimultaneousTouch = touchersThisTick > 1;
        if (toucher != lastToucher)
        {
            previousToucher = lastToucher;
            previousTouchTime = lastTouchTime;
        }
        lastToucher = toucher;
        lastTouchTime = Time;

        // Shot/save attribution from the untouched prediction before and after the contact.
        predictionCount = arena.PredictBall(prediction);
        int threatAfter = PredictedGoalTeam();
        Participant hitter = participants[toucher];
        int opponent = 1 - hitter.Team;
        if (StatsEnabled && threatAfter == opponent && threatenedGoal != opponent)
        {
            hitter.Stats.Shots++;
            hitter.Shots++;
        }
        if (StatsEnabled && threatBefore == hitter.Team && threatAfter != hitter.Team)
        {
            hitter.Stats.Saves++;
            hitter.Saves++;
        }
        threatenedGoal = threatAfter;
    }

    /// <summary>Records a goal for scoring team <paramref name="team"/> and returns the event.</summary>
    public GoalEvent RegisterGoal(int team)
    {
        Score[team]++;
        int? scorer = lastToucher >= 0 ? lastToucher : null;
        bool own = scorer.HasValue && participants[scorer.Value].Team != team;
        int? assist = null;
        if (scorer.HasValue && !own && previousToucher >= 0 && previousToucher != scorer &&
            participants[previousToucher].Team == team && lastTouchTime - previousTouchTime < 5.0)
            assist = previousToucher;

        if (StatsEnabled && scorer.HasValue)
        {
            Participant p = participants[scorer.Value];
            if (own) { p.Stats.OwnGoals++; p.OwnGoals++; }
            else { p.Stats.Goals++; p.Goals++; }
        }
        if (StatsEnabled && assist.HasValue)
        {
            participants[assist.Value].Stats.Assists++;
            participants[assist.Value].Assists++;
        }
        return new GoalEvent(Time, team, scorer, own, ball.Physics.Velocity.Length, assist, Overtime);
    }

    /// <summary>Plays a full match with kickoffs, a regulation clock and sudden-death overtime.</summary>
    public MatchResult PlayMatch(string blueLabel, string orangeLabel, int teamSize)
    {
        var result = new MatchResult { Blue = blueLabel, Orange = orangeLabel, Seed = options.Seed, TeamSize = teamSize };
        var watch = Stopwatch.StartNew();
        TimeRemaining = options.MatchSeconds;
        int kickoffNumber = 0;
        float overtimeElapsed = 0;

        try
        {
            while (true)
            {
                kickoffNumber++;
                arena.ResetKickoff(options.Seed * 1009 + kickoffNumber);
                JitterSpawns(options.Seed * 7919 + kickoffNumber, options.SpawnJitter);
                Snapshot();
                ResetTouchState();
                stats.ResetBoostBaseline(cars);
                foreach (Participant p in participants) p.LastInput = new ControllerStateT();

                for (float t = 0; t < options.CountdownSeconds; t += SimArena.TickTime)
                    Tick(MatchPhase.Countdown);

                // Kickoff phase: until the first touch or RLBot's automatic transition to active
                // play after two seconds (bots must not rely on the phase lasting until contact).
                float kickoffElapsed = 0;
                float firstTouch = float.NaN;
                int? firstTeam = null;
                MarkKickoffTakers();
                void NoteFirstTouch()
                {
                    if (!float.IsNaN(firstTouch) || !touchedThisTick) return;
                    firstTouch = kickoffElapsed;
                    firstTeam = participants[lastToucher].Team;
                    Participant p = participants[lastToucher];
                    p.Stats.KickoffFirstTouches++;
                    p.Stats.KickoffTimeToBall += kickoffElapsed;
                }
                while (kickoffElapsed < options.KickoffAutoActiveSeconds)
                {
                    Tick(MatchPhase.Kickoff);
                    kickoffElapsed += SimArena.TickTime;
                    NoteFirstTouch();
                    if (touchedThisTick)
                        break;
                }

                double kickoffStart = Time;
                float ballYAt3 = float.NaN;
                int? goalTeam = null;
                bool regulationOver = false;
                while (true)
                {
                    Tick(MatchPhase.Active);
                    if (float.IsNaN(firstTouch) && kickoffElapsed < 6f)
                    {
                        kickoffElapsed += SimArena.TickTime;
                        NoteFirstTouch();
                    }
                    if (Overtime) overtimeElapsed += SimArena.TickTime;
                    else TimeRemaining -= SimArena.TickTime;
                    if (float.IsNaN(ballYAt3) && Time - kickoffStart >= 3.0) ballYAt3 = ball.Physics.Position.Y;

                    var (events, demos) = arena.PollEvents();
                    foreach (RsbDemo demo in demos) RegisterDemo(demo);
                    if (events.GoalTeam >= 0)
                    {
                        goalTeam = events.GoalTeam;
                        result.Goals.Add(RegisterGoal(events.GoalTeam));
                        break;
                    }

                    if (!Overtime && TimeRemaining <= 0)
                    {
                        // Rocket League ends regulation when the ball next touches the ground.
                        bool ballGrounded = ball.Physics.Position.Z < 100f;
                        if (Score[0] != Score[1] && ballGrounded) { regulationOver = true; break; }
                        if (Score[0] == Score[1] && ballGrounded) { Overtime = true; result.Overtime = true; }
                    }
                    if (Overtime && overtimeElapsed >= options.OvertimeCapSeconds)
                    {
                        result.Draw = true;
                        regulationOver = true;
                        break;
                    }
                }

                double sinceKickoff = Time - kickoffStart;
                int? advantage = float.IsNaN(ballYAt3) ? null : ballYAt3 > 150 ? 0 : ballYAt3 < -150 ? 1 : null;
                result.Kickoffs.Add(new KickoffEvent(kickoffNumber, firstTeam,
                    float.IsNaN(firstTouch) ? double.NaN : firstTouch,
                    float.IsNaN(ballYAt3) ? double.NaN : ballYAt3, advantage,
                    goalTeam.HasValue && sinceKickoff <= 10.0, goalTeam));

                if (regulationOver)
                    break;
                if (goalTeam.HasValue)
                {
                    for (float t = 0; t < options.GoalPauseSeconds; t += SimArena.TickTime)
                        Tick(MatchPhase.GoalScored);
                    if (Overtime)
                        break;
                }
            }
        }
        catch (BotCrashedException e)
        {
            result.Error = e.Message;
        }

        result.BlueScore = Score[0];
        result.OrangeScore = Score[1];
        result.GameSeconds = options.MatchSeconds - TimeRemaining + overtimeElapsed;
        result.WallSeconds = watch.Elapsed.TotalSeconds;
        result.Timing = Timing.ToString();
        foreach (Participant p in participants)
            result.Players.Add(new PlayerSummary(p.Index, p.Team, p.Name, p.Agent.Description, p.Stats));
        return result;
    }

    private void MarkKickoffTakers()
    {
        // The car nearest the ball at the kickoff reset is its team's kickoff taker.
        foreach (int team in new[] { 0, 1 })
        {
            Participant? taker = participants.Where(p => p.Team == team)
                .OrderBy(p => cars[p.Index].Physics.Position.FlatDistance(new RsbVec(0, 0, 0)))
                .FirstOrDefault();
            if (taker != null) taker.Stats.KickoffsTaken++;
        }
    }

    private void RegisterDemo(RsbDemo demo)
    {
        Participant? bumper = participants.FirstOrDefault(p => p.CarId == demo.Bumper);
        Participant? victim = participants.FirstOrDefault(p => p.CarId == demo.Victim);
        if (bumper != null) { bumper.Stats.DemosInflicted++; bumper.Demolitions++; }
        if (victim != null) victim.Stats.DemosTaken++;
    }

    public void Dispose()
    {
        foreach (Participant p in participants)
        {
            try { p.Agent.Dispose(); } catch (Exception) { }
        }
        arena.Dispose();
    }
}
