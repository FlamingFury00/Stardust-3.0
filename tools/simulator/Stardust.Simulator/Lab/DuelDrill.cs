using System.Globalization;
using RLBot.Flat;
using Stardust.Simulator.Match;
using Stardust.Simulator.Physics;
using Stardust.Simulator.Scenarios;

namespace Stardust.Simulator.Lab;

/// <summary>How the scripted defender meets a dribble.</summary>
public enum DefenderMode
{
    /// <summary>Drives straight at the carrier with boost and jumps into the ball: the moment to flick.</summary>
    Challenge,
    /// <summary>Retreats ahead of the carrier, holding about 900 uu of cushion: flicking gives the ball away.</summary>
    Shadow,
    /// <summary>Chases from behind at speed: keep the ball and accelerate away.</summary>
    Chase,
}

/// <summary>A deterministic defender with the three behaviours a dribbler must read.</summary>
public sealed class DefenderAgent : ScriptedAgent
{
    private float jumpStarted = float.NaN;
    public DefenderMode Mode { get; set; }
    public override string Description => "defender";

    public void Reset() => jumpStarted = float.NaN;

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        PlayerInfoT car = packet.Players[self.Index];
        Vector3T ball = packet.Balls[0].Physics.Location;
        Vector3T ballVelocity = packet.Balls[0].Physics.Velocity;
        float now = packet.MatchInfo.SecondsElapsed;
        var controls = new ControllerStateT();
        if (float.IsFinite(jumpStarted))
        {
            controls.Jump = now - jumpStarted < 0.15f;
            controls.Throttle = 1;
            if (car.AirState == AirState.OnGround && now - jumpStarted > 0.4f) jumpStarted = float.NaN;
            return controls;
        }

        float dx = ball.X - car.Physics.Location.X, dy = ball.Y - car.Physics.Location.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (Mode == DefenderMode.Shadow)
        {
            // Hold a cushion in front of the ball on its line to our goal, facing our goal like a shadow defender.
            float goalSide = self.Team == 0 ? -1 : 1;
            float cushionY = ball.Y + goalSide * 900;
            float error = (cushionY - car.Physics.Location.Y) * goalSide;
            float closing = (ballVelocity.Y - car.Physics.Velocity.Y) * goalSide;
            controls.Steer = Flat.SteerToward(car, ball.X, car.Physics.Location.Y + goalSide * 1500);
            controls.Throttle = Math.Clamp(error / 250f + closing / 400f, -1f, 1f);
            controls.Boost = controls.Throttle > 0.95f && MathF.Abs(controls.Steer) < 0.2f;
            return controls;
        }

        controls.Steer = Flat.SteerToward(car, ball.X, ball.Y);
        controls.Throttle = 1;
        controls.Boost = MathF.Abs(controls.Steer) < 0.3f;
        float reach = Mode == DefenderMode.Challenge ? 350 : 250;
        if (car.AirState == AirState.OnGround && distance < reach && ball.Z < 320)
        {
            jumpStarted = now;
            controls.Jump = true;
        }
        return controls;
    }
}

/// <summary>
/// Pressured dribble, full bot: Stardust carries the ball at about 1000 uu/s from its own half while
/// a defender challenges, shadows, or chases. Judged on whether the play keeps or converts the ball.
/// </summary>
public sealed class DribbleDuelDrill : RoofDrill
{
    private readonly DefenderAgent defender = new();
    private DefenderMode mode;
    private bool flicked;
    private int predictedGoal = -1;
    private float endGap, opponentGap, ballPastDefender;

    public override string Name => "dribble-duel";
    public override string Description => "Carry under pressure: flick a committed challenger, keep the ball against shadows and chasers.";
    public override int SeatCount => 2;
    public override IAgent? ScriptedOpponent(int seat) => defender;

    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Mean("challenge-good", 0.6), Criterion.Mean("shadow-good", 0.7), Criterion.Mean("chase-good", 0.6),
        Criterion.Mean("shadow-flick", 0.3, atLeast: false), Criterion.Mean("lost", 0.25, atLeast: false),
    };

    public override EpisodeSetup Generate(Random r)
    {
        mode = (DefenderMode)r.Next(3);
        var car = V(Uniform(r, -800, 800), -2000, 17.01f);
        float yaw = MathF.PI / 2 + Uniform(r, -0.15f, 0.15f);
        EpisodeSetup carry = CarryStart(car, yaw, Uniform(r, 900, 1100), Uniform(r, 0, 20), Uniform(r, -10, 10),
            Uniform(r, 30, 80), 4f);
        CarSetup opponent = mode switch
        {
            DefenderMode.Challenge => new CarSetup(V(car.X + Uniform(r, -300, 300), car.Y + 1800 + Uniform(r, -200, 200), 17.01f),
                -MathF.PI / 2, V(0, -Uniform(r, 0, 800), 0), 100),
            DefenderMode.Shadow => new CarSetup(V(car.X + Uniform(r, -300, 300), car.Y + 900, 17.01f),
                MathF.PI / 2, V(0, Uniform(r, 800, 1100), 0), 100),
            _ => new CarSetup(V(car.X + Uniform(r, -200, 200), car.Y - 700, 17.01f),
                MathF.PI / 2, V(0, 1600, 0), 100),
        };
        return carry with { Cars = new[] { carry.Cars[0], opponent } };
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        base.Start(session, setup);
        defender.Mode = mode;
        defender.Reset();
        flicked = false;
        predictedGoal = -1;
        Subject.Director = null;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        flicked |= Subject.Action is global::Bot.Flick;
        predictedGoal = session.PredictedGoalTeam();
        RsbVec ball = session.Ball.Physics.Position;
        endGap = ball.Distance(session.Cars[0].Physics.Position);
        opponentGap = ball.Distance(session.Cars[1].Physics.Position);
        ballPastDefender = ball.Y - session.Cars[1].Physics.Position.Y;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        string key = mode.ToString().ToLowerInvariant();
        trace.Notes.Insert(0, key);
        bool goal = trace.GoalTeam == 0;
        bool ours = trace.TouchLog.Count > 0 && trace.TouchLog[^1].Toucher == 0;
        // Good: scored, a shot heading into the goal, or the ball still ours and close to our car.
        bool shot = trace.GoalTeam < 0 && predictedGoal == 1;
        bool possession = trace.GoalTeam < 0 && ours && endGap < 500;
        // Beaten: the ball got past the defender (toward its goal) off our touch, and we are nearer to it.
        bool beaten = trace.GoalTeam < 0 && ours && ballPastDefender > 200 && endGap < opponentGap;
        bool good = goal || shot || possession || beaten;
        bool lost = !good && (trace.GoalTeam == 1 || !ours);
        trace.Metrics[$"{key}-good"] = good ? 1 : 0;
        trace.Metrics[$"{key}-flick"] = flicked ? 1 : 0;
        trace.Metrics["goal"] = goal ? 1 : 0;
        trace.Metrics["shot"] = shot ? 1 : 0;
        trace.Metrics["beaten"] = beaten ? 1 : 0;
        trace.Metrics["lost"] = lost ? 1 : 0;
        trace.Metrics["conceded"] = trace.GoalTeam == 1 ? 1 : 0;
        return good;
    }
}

/// <summary>
/// Plays full kickoff-to-minute games with the complete bot against an opponent build (or idle)
/// and records the share of time each action runs, and the share of each decision. It shows how
/// much a mechanic matters in real play, not whether it works.
/// </summary>
public sealed class ActionProfileDrill : Drill
{
    private readonly Dictionary<string, float> actionTime = new(), decisionTime = new();
    private float total, zeroBoost, boostSum, collected, lastBoost = float.NaN, speedSum;

    public override string Name => "profile";
    public override string Description => "Time share of each action and decision in one-minute games from kickoff.";
    public override int SeatCount => 2;
    public override IReadOnlyList<Criterion> Criteria => Array.Empty<Criterion>();

    public override EpisodeSetup Generate(Random r)
    {
        float Jitter() => Uniform(r, -10, 10);
        return new EpisodeSetup(V(0, 0, 93.15f), V(0, 0, 0), new[]
        {
            new CarSetup(V(-2048 + Jitter(), -2560 + Jitter(), 17), MathF.PI / 4, V(0, 0, 0), 33.3f),
            new CarSetup(V(2048 + Jitter(), 2560 + Jitter(), 17), MathF.PI / 4 + MathF.PI, V(0, 0, 0), 33.3f),
        }, 60f, Kickoff: true);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup) => Subject.Director = null;

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        string action = Subject.Action?.GetType().Name ?? "none";
        string decision = Subject.Decision ?? "none";
        actionTime[action] = actionTime.GetValueOrDefault(action) + SimArena.TickTime;
        decisionTime[decision] = decisionTime.GetValueOrDefault(decision) + SimArena.TickTime;
        total += SimArena.TickTime;
        RsbCarState car = session.Cars[0];
        if (car.Boost < 0.5f) zeroBoost += SimArena.TickTime;
        boostSum += car.Boost * SimArena.TickTime;
        speedSum += car.Physics.Velocity.Length * SimArena.TickTime;
        if (float.IsFinite(lastBoost) && car.Boost > lastBoost + 0.5f) collected += car.Boost - lastBoost;
        lastBoost = car.Boost;
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => false;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        // Cumulative shares so far: the last episode's metrics are the totals over all games.
        foreach (var (key, time) in actionTime) trace.Metrics["action:" + key] = time / total;
        foreach (var (key, time) in decisionTime.OrderByDescending(d => d.Value).Take(14))
            trace.Metrics["decision:" + key] = time / total;
        trace.Metrics["goals-for"] = trace.GoalTeam == 0 ? 1 : 0;
        trace.Metrics["zero-boost-share"] = zeroBoost / total;
        trace.Metrics["mean-boost"] = boostSum / total;
        trace.Metrics["mean-speed"] = speedSum / total;
        trace.Metrics["boost-collected-per-min"] = collected / total * 60f;
        lastBoost = float.NaN;
        return true;
    }
}

/// <summary>
/// Defending an attack, full bot, against an opponent build (--opponent; Nexto is the benchmark).
/// The opponent starts with the ball near midfield, driving at our goal; Stardust starts goal-side
/// in net, at the edge of the box, or caught upfield. Six seconds: did the attack score, and did the
/// defence clear the ball into the opponent half, and how much of it was spent on the goal line.
/// </summary>
public sealed class DefenseDrill : Drill
{
    private float lineTime, total;
    private int start;

    public override string Name => "defense";
    public override string Description => "Defend an attack from midfield by the opponent build.";
    public override int SeatCount => 2;
    public override IReadOnlyList<Criterion> Criteria => new[]
    {
        Criterion.Mean("conceded", 0.25, atLeast: false), Criterion.Mean("cleared", 0.5),
    };

    public override EpisodeSetup Generate(Random r)
    {
        // The attacker (orange) carries the ball from around midfield toward the blue goal (-y).
        float x = Uniform(r, -1800, 1800);
        var attacker = V(x, Uniform(r, 300, 1200), 17);
        float attackYaw = -MathF.PI / 2 + Uniform(r, -0.35f, 0.35f);
        float speed = Uniform(r, 600, 1300);
        RsbVec heading = Heading(attackYaw);
        var ball = attacker + heading * 180 + V(0, 0, 76);
        start = r.Next(3);
        CarSetup defender = start switch
        {
            0 => new CarSetup(V(Uniform(r, -300, 300), -5000, 17), MathF.PI / 2, V(0, 0, 0), Uniform(r, 20, 100)),
            1 => new CarSetup(V(Uniform(r, -1200, 1200), -3300, 17), MathF.PI / 2 + Uniform(r, -0.5f, 0.5f), V(0, 0, 0), Uniform(r, 20, 100)),
            _ => new CarSetup(V(x + Uniform(r, -1500, 1500), Uniform(r, -600, 400), 17), -MathF.PI / 2 + Uniform(r, -0.6f, 0.6f),
                Heading(-MathF.PI / 2) * Uniform(r, 500, 1400), Uniform(r, 0, 60)),
        };
        return new EpisodeSetup(ball, heading * speed, new[]
        {
            defender, new CarSetup(attacker, attackYaw, heading * speed, Uniform(r, 30, 100)),
        }, 6f);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        lineTime = total = 0;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        total += SimArena.TickTime;
        if (Subject.Action is global::Bot.GoalLineSave) lineTime += SimArena.TickTime;
    }

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        string key = new[] { "in-net", "box", "upfield" }[start];
        trace.Notes.Insert(0, key);
        bool conceded = trace.GoalTeam == 1;
        bool cleared = trace.GoalTeam < 0 && trace.FinalBallPosition.Y > 0;
        trace.Metrics["conceded"] = conceded ? 1 : 0;
        trace.Metrics[$"{key}-conceded"] = conceded ? 1 : 0;
        trace.Metrics["cleared"] = cleared ? 1 : 0;
        trace.Metrics["scored"] = trace.GoalTeam == 0 ? 1 : 0;
        trace.Metrics["goal-line-share"] = lineTime / MathF.Max(total, 1e-3f);
        return !conceded;
    }
}

/// <summary>
/// Kickoff and the ten seconds after it, full bot, against an opponent build (--opponent). Judged on
/// first touch and on goals either way before the play settles: the minutes after a won kickoff
/// are where a strong opponent turns the ball around.
/// </summary>
public sealed class KickoffFollowDrill : Drill
{
    private static readonly (float X, float Y, float Yaw)[] Spawns =
    {
        (-2048, -2560, MathF.PI / 4), (2048, -2560, 3 * MathF.PI / 4),
        (-256, -3840, MathF.PI / 2), (256, -3840, MathF.PI / 2), (0, -4608, MathF.PI / 2),
    };
    private int spawn;

    public override string Name => "kickoff-follow";
    public override string Description => "Kickoff plus the next ten seconds against the opponent build.";
    public override int SeatCount => 2;
    public override IReadOnlyList<Criterion> Criteria => new[] { Criterion.Mean("conceded", 0.1, atLeast: false) };

    public override EpisodeSetup Generate(Random r)
    {
        spawn = r.Next(Spawns.Length);
        var (x, y, yaw) = Spawns[spawn];
        return new EpisodeSetup(V(0, 0, 93.15f), V(0, 0, 0), new[]
        {
            new CarSetup(V(x, y, 17), yaw, V(0, 0, 0), 33.3f),
            new CarSetup(V(-x, -y, 17), yaw + MathF.PI, V(0, 0, 0), 33.3f),
        }, 10f, Kickoff: true);
    }

    protected override void Start(MatchSession session, EpisodeSetup setup) => Subject.Director = null;

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        trace.Notes.Insert(0, $"spawn{spawn}");
        trace.Metrics["first-touch"] = trace.FirstToucher == 0 ? 1 : 0;
        trace.Metrics["conceded"] = trace.GoalTeam == 1 ? 1 : 0;
        trace.Metrics["scored"] = trace.GoalTeam == 0 ? 1 : 0;
        trace.Metrics["goal-s"] = trace.GoalTeam >= 0 ? trace.GoalTime : double.NaN;
        return trace.GoalTeam != 1;
    }
}

/// <summary>
/// Shots at our goal, full bot, no opponent: the scenario suite's save fixture played in-process.
/// Shots of 1500-3200 uu/s from up to 5000 uu out, the defender in net, at a post, or rotating
/// back. Saved means no goal before the ball leaves our half.
/// </summary>
public sealed class SaveDrill : Drill
{
    private readonly SaveScenario shots = new();
    private string start = "";

    public override string Name => "save";
    public override string Description => "Stop shots at our goal from varied angles, heights and speeds.";
    public override IReadOnlyList<Criterion> Criteria => new[] { Criterion.Rate(0.7) };

    public override EpisodeSetup Generate(Random r)
    {
        EpisodeSetup setup = shots.Generate(r);
        RsbVec car = setup.Cars[0].Position;
        start = MathF.Abs(car.Y) > 4900 ? "in-net" : MathF.Abs(car.Y) > 4600 ? "post" : "rotating";
        return setup;
    }

    // Closest pass of the ball by the car's hitbox before the first touch: the gap and where the
    // ball was relative to the car (car frame), so a miss reads as under, over, short or wide.
    private float closestGap;
    private RsbVec closestLocal, hitboxHalf;
    private bool airborneAtClosest;

    protected override void Start(MatchSession session, EpisodeSetup setup)
    {
        Subject.Director = null;
        closestGap = float.PositiveInfinity;
    }

    protected override void Measure(MatchSession session, EpisodeTrace trace)
    {
        if (!float.IsNaN(trace.FirstTouchTime)) return;
        Participant p = session.Participants[0];
        RsbCarState car = session.Cars[0];
        RsbVec local = Local(car.Physics, session.Ball.Physics.Position) - p.HitboxOffset;
        RsbVec half = hitboxHalf = p.HitboxSize * 0.5f;
        RsbVec outside = new(MathF.Max(0f, MathF.Abs(local.X) - half.X), MathF.Max(0f, MathF.Abs(local.Y) - half.Y),
            MathF.Max(0f, MathF.Abs(local.Z) - half.Z));
        float gap = outside.Length - SimArena.BallRadius;
        if (gap < closestGap)
        {
            closestGap = gap;
            closestLocal = local;
            airborneAtClosest = car.IsOnGround == 0;
        }
    }

    public override bool ShouldStop(MatchSession session, EpisodeTrace trace) => shots.ShouldStop(session, trace);

    public override bool Judge(EpisodeSetup setup, EpisodeTrace trace)
    {
        bool saved = trace.GoalTeam != 1;
        trace.Notes.Insert(0, start);
        trace.Metrics[$"{start}-saved"] = saved ? 1 : 0;
        trace.Metrics["touched"] = float.IsNaN(trace.FirstTouchTime) ? 0 : 1;
        if (!saved && float.IsNaN(trace.FirstTouchTime) && float.IsFinite(closestGap))
        {
            // Which way the ball got past: the largest excess over the hitbox half extents.
            RsbVec half = hitboxHalf;
            float over = MathF.Abs(closestLocal.Z) - half.Z, along = MathF.Abs(closestLocal.X) - half.X, wide = MathF.Abs(closestLocal.Y) - half.Y;
            string side = over >= along && over >= wide ? (closestLocal.Z > 0 ? "over" : "under")
                : along >= wide ? (closestLocal.X > 0 ? "ahead" : "behind") : "beside";
            trace.Notes.Insert(1, string.Create(CultureInfo.InvariantCulture,
                $"missed {side} by {closestGap:F0} ({(airborneAtClosest ? "air" : "ground")})"));
            trace.Metrics["miss-gap"] = closestGap;
        }
        return saved;
    }
}
