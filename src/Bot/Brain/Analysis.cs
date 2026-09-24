using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;

namespace Bot.Brain
{
    /// <summary>Earliest moment one car can plausibly touch the ball.</summary>
    public readonly struct Intercept
    {
        public readonly int Index;
        public readonly float Time;      // absolute game time, +inf when unreachable within the horizon
        public readonly Vec3 Ball;       // ball position at that moment
        public readonly bool Aerial;

        public Intercept(int index, float time, Vec3 ball, bool aerial)
        {
            Index = index;
            Time = time;
            Ball = ball;
            Aerial = aerial;
        }

        public bool Reachable => float.IsFinite(Time);
    }

    /// <summary>
    /// Shared view of the play for one decision tick: the ball path, every car's earliest touch
    /// (computed identically for all cars with the navigation rollout), and goal threats.
    /// </summary>
    public sealed class Analysis
    {
        public const float Horizon = 5f;
        private const float Step = 1f / 20f;

        public float Now { get; private set; }
        public BallPath Path { get; private set; }
        public int Team { get; private set; }
        public int Side { get; private set; }            // -1 blue (defends -y), +1 orange
        public Car Me { get; private set; }
        public List<Car> Teammates { get; } = new();
        public List<Car> Opponents { get; } = new();
        public Dictionary<int, Intercept> Intercepts { get; } = new();

        public Intercept Mine { get; private set; }
        public Intercept FirstOpponent { get; private set; }
        public Intercept FirstTeammate { get; private set; }

        /// <summary>Time until the untouched ball enters our goal, +inf if it does not within the horizon.</summary>
        public float ThreatTime { get; private set; } = float.PositiveInfinity;
        public Vec3 ThreatPoint { get; private set; }
        /// <summary>Time until the untouched ball enters their goal.</summary>
        public float ChanceTime { get; private set; } = float.PositiveInfinity;

        /// <summary>Opponent's earliest touch minus ours: positive when we get there first.</summary>
        public float Advantage => (FirstOpponent.Reachable ? FirstOpponent.Time : Now + Horizon + 1f) -
            (Mine.Reachable ? Mine.Time : Now + Horizon + 1f);

        public Vec3 OwnGoal => new(0, Side * 5120f, 0);
        public Vec3 TheirGoal => new(0, -Side * 5120f, 0);

        public static Analysis Build(RUBot bot)
        {
            var a = new Analysis
            {
                Now = Game.Time,
                Path = new BallPath(Ball.Prediction.Slices),
                Team = bot.Team,
                Side = Field.Side(bot.Team),
                Me = bot.Me,
            };
            foreach (Car car in Cars.AllLivingCars)
            {
                if (car.Index == bot.Index) continue;
                (car.Team == bot.Team ? a.Teammates : a.Opponents).Add(car);
            }

            a.Mine = a.Earliest(bot.Me);
            a.Intercepts[bot.Index] = a.Mine;
            a.FirstOpponent = new Intercept(-1, float.PositiveInfinity, Ball.Location, false);
            a.FirstTeammate = new Intercept(-1, float.PositiveInfinity, Ball.Location, false);
            foreach (Car car in a.Opponents)
            {
                Intercept i = a.Earliest(car);
                a.Intercepts[car.Index] = i;
                if (i.Time < a.FirstOpponent.Time) a.FirstOpponent = i;
            }
            foreach (Car car in a.Teammates)
            {
                Intercept i = a.Earliest(car);
                a.Intercepts[car.Index] = i;
                if (i.Time < a.FirstTeammate.Time) a.FirstTeammate = i;
            }
            a.FindGoalCrossings();
            return a;
        }

        /// <summary>
        /// First ball slice the car can reach: ground/jump height balls by navigation rollout to a
        /// point just short of the ball, higher balls by a boost-limited aerial reach estimate.
        /// </summary>
        public Intercept Earliest(Car car)
        {
            if (car == null || car.IsDemolished || Path.Count == 0)
                return new Intercept(car?.Index ?? -1, float.PositiveInfinity, Ball.Location, false);

            GroundState start = Navigator.StartState(car);
            float next = Now + 0.03f;
            for (int i = 0; i < Path.Count; i++)
            {
                BallSlice slice = Path[i];
                if (slice.Time < next) continue;
                float t = slice.Time - Now;
                if (t > Horizon) break;
                next = slice.Time + Step;
                Vec3 ball = slice.Location;

                if (ball.z < 330f)
                {
                    Vec3 flat = (ball - start.Position).Flatten();
                    float reach = MathF.Max(0f, flat.Length() - 150f);
                    float bound = start.Time + DrivePhysics.TravelTime(reach, MathF.Max(0f, start.Speed), start.Boost);
                    if (bound > t) continue;
                    Vec3 point = flat.Length() > 150f ? ball.Flatten() - flat.Normalize() * 140f : start.Position;
                    // Jumps take off while still driving, so they only need time on the wheels.
                    float jump = ball.z > 150f ? JumpModel.TimeToHeight(MathF.Min(ball.z - 45f, 230f), false) : 0f;
                    if (!float.IsFinite(jump)) jump = 0.6f;
                    if (t - start.Time < jump) continue;
                    RolloutResult r = Navigator.Rollout(start, new DriveTarget(point, Vec3.Zero), t + 0.05f, dt: 1f / 30f);
                    if (r.Time <= t)
                        return new Intercept(car.Index, slice.Time, ball, false);
                }
                else if (car.Boost > 12f && AerialReach(car, ball, t))
                {
                    return new Intercept(car.Index, slice.Time, ball, true);
                }
            }
            return new Intercept(car.Index, float.PositiveInfinity, Ball.Location, false);
        }

        /// <summary>Boost-limited reach test: constant thrust after a nose-up turn, from ground or air.</summary>
        private static bool AerialReach(Car car, Vec3 target, float t)
        {
            float turn = car.IsGrounded ? 0.35f : 0.25f;
            if (t <= turn + 0.15f) return false;
            Vec3 velocity = car.Velocity + (car.IsGrounded ? car.Up * (RL.JumpImpulse + 150f) : Vec3.Zero);
            Vec3 drift = car.Location + velocity * t + new Vec3(0, 0, 0.5f * RL.Gravity * t * t);
            Vec3 delta = target - drift;
            float burn = t - turn;
            float required = 2f * delta.Length() / (burn * burn + 2f * burn * turn);
            float fuelTime = car.Boost / RL.BoostPerSecond;
            return required < 0.9f * RL.BoostAccelAir && required * burn / RL.BoostAccelAir <= fuelTime + 0.1f;
        }

        private void FindGoalCrossings()
        {
            for (int i = 0; i < Path.Count; i++)
            {
                BallSlice slice = Path[i];
                float t = slice.Time - Now;
                if (t < 0f) continue;
                if (t > Horizon) break;
                if (MathF.Abs(slice.Location.y) > RL.GoalScoreY)
                {
                    bool ours = MathF.Sign(slice.Location.y) == Side;
                    if (ours && !float.IsFinite(ThreatTime))
                    {
                        ThreatTime = t;
                        ThreatPoint = slice.Location;
                    }
                    else if (!ours && !float.IsFinite(ChanceTime))
                        ChanceTime = t;
                    break;
                }
            }
        }

        /// <summary>Signed distance of a point from the ball toward our goal (positive = goal-side).</summary>
        public float GoalSideOf(Vec3 point, Vec3 ball)
        {
            Vec3 toGoal = (OwnGoal - ball).Flatten();
            float length = toGoal.Length();
            return length < 1f ? 0f : (point - ball).Flatten().Dot(toGoal / length);
        }
    }
}
