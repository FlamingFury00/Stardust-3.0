using System;
using RedUtils.Math;
using RedUtils.Physics;

namespace RedUtils.Planning
{
    /// <summary>A planned block: be under the ball's path at a ground point and meet it at its height.</summary>
    public sealed class BlockPlan
    {
        public BallSlice Slice;
        /// <summary>Ground point the car occupies at contact, beside the ball's ground track.</summary>
        public Vec3 Point;
        /// <summary>Seconds from takeoff to contact; 0 when the ball is low enough to block on the wheels.</summary>
        public float JumpTime;
        public bool DoubleJump;
        /// <summary>Absolute time the car can reach <see cref="Point"/> driving flat out.</summary>
        public float EarliestArrival;
        /// <summary>False for a best-effort block that the car is predicted to reach a little late.</summary>
        public bool Feasible = true;

        public float ContactTime => Slice.Time;
        public float Slack => ContactTime - EarliestArrival;

        public override string ToString() => FormattableString.Invariant(
            $"Block(t={ContactTime:F2} slack={Slack:F2} ball={Slice.Location} jump={JumpTime:F2} double={DoubleJump} feasible={Feasible})");
    }

    /// <summary>
    /// Plans blocks on a ball heading for our goal. Unlike an aimed strike a block needs no
    /// particular heading: the car only has to put its body in the ball's path, early, at the
    /// right height — the reliable last line of a save.
    /// </summary>
    public static class BlockPlanner
    {
        public const float Step = 1f / 30f;
        /// <summary>Highest ball a double jump can block with the roof (the car origin reaches ~490).</summary>
        public const float MaxBallHeight = 600f;
        /// <summary>Ball heights up to which a car on its wheels blocks with its body.</summary>
        public const float GroundBlockHeight = 170f;
        /// <summary>Car origin below the ball centre at contact: the roof (32 uu above the origin) meets the ball with a margin.</summary>
        private const float ContactDrop = 110f;
        /// <summary>
        /// A car blocking on its wheels stands this far from the ball's ground track, on its own
        /// side: its body still covers the ball (half-width 42 plus the ball's 91 radius) and it
        /// has less to travel.
        /// </summary>
        public const float BlockOffset = 80f;
        /// <summary>
        /// A jump block is flown open loop for up to a second, so the car centres on the track
        /// and keeps the whole of its body's reach either side for the error of the flight.
        /// </summary>
        public const float JumpBlockOffset = 0f;

        /// <summary>Ground point for a block of a ball at <paramref name="ball"/> by a car coming from <paramref name="from"/>.</summary>
        public static Vec3 BlockPoint(Vec3 ball, Vec3 from, bool jumping)
        {
            Vec3 toCar = (from - ball).Flatten();
            float distance = toCar.Length();
            float offset = jumping ? JumpBlockOffset : BlockOffset;
            return ball.Flatten() + (distance > 1f ? toCar / distance * MathF.Min(offset, distance) : Vec3.Zero);
        }
        /// <summary>Deepest ball position (|y|) at which a block still keeps the ball out.</summary>
        public const float GoalLineLimit = 5180f;
        /// <summary>Largest predicted lateness for which a best-effort block is still attempted.</summary>
        private const float MaxShortfall = 0.35f;
        /// <summary>A jump block takes off at least this long before the drive alone would reach the point.</summary>
        private const float MinimumRunIn = 0.05f;
        /// <summary>Time the car should be settled at the point before it has to jump.</summary>
        private const float SettleTime = 0.05f;

        /// <summary>
        /// Earliest block on <paramref name="path"/> before <paramref name="deadline"/> (seconds
        /// from <paramref name="now"/>). When none is reachable in time, the block the car misses
        /// by the least (a near miss can still deflect the ball); null when the ball is out of reach.
        /// </summary>
        public static BlockPlan Plan(Car car, BallPath path, float now, float deadline, Action<string> log = null)
        {
            if (car == null || path == null || path.Count == 0) return null;
            GroundState start = Navigator.StartState(car);
            float next = now + 0.1f;
            BlockPlan nearest = null;
            float nearestShortfall = MaxShortfall;
            for (int i = 0; i < path.Count; i++)
            {
                BallSlice slice = path[i];
                if (slice.Time < next) continue;
                float t = slice.Time - now;
                if (t > deadline) break;
                next = slice.Time + Step;
                Vec3 ball = slice.Location;
                if (ball.z > MaxBallHeight) continue;
                // The block must happen in front of the line: past it the ball is already in.
                if (MathF.Abs(ball.y) > GoalLineLimit) break;

                // Meet the ball with the roof: the faster of a single and a double jump.
                float jumpTime = 0f;
                bool doubleJump = false;
                if (ball.z > GroundBlockHeight)
                {
                    float height = ball.z - ContactDrop;
                    float single = JumpModel.TimeToHeight(height, false);
                    float twice = JumpModel.TimeToHeight(height, true);
                    doubleJump = !float.IsFinite(single) || (float.IsFinite(twice) && twice < single);
                    jumpTime = doubleJump ? twice : single;
                    if (!float.IsFinite(jumpTime)) continue;
                }
                // A jumping car keeps its ground speed in the air, so it only has to be heading
                // through the point at contact: the drive and the flight share the time.
                float available = t - SettleTime - (jumpTime > 0f ? MinimumRunIn : 0f);
                if (available <= start.Time) continue;

                Vec3 point = BlockPoint(ball, start.Position, jumpTime > 0f);
                float distance = (point - start.Position).Flatten().Length();
                float bound = start.Time + DrivePhysics.TravelTime(MathF.Max(0f, distance - Navigator.ArrivalRadius),
                    MathF.Max(0f, start.Speed), start.Boost);
                if (bound > available + nearestShortfall) continue;
                float through = ThroughTime(start, point, jumpTime > 0f ? t - jumpTime : float.PositiveInfinity,
                    available + nearestShortfall);
                log?.Invoke(FormattableString.Invariant(
                    $"  t={t:F2} ball={ball} jump={jumpTime:F2} available={available:F2} through={through:F2}"));
                if (!float.IsFinite(through)) continue;
                var plan = new BlockPlan
                {
                    Slice = slice, Point = point, JumpTime = jumpTime, DoubleJump = doubleJump,
                    EarliestArrival = now + through,
                };
                if (through <= available) return plan;
                plan.Feasible = false;
                nearest = plan;
                nearestShortfall = through - available;
            }
            return nearest;
        }

        /// <summary>Largest miss (uu) by which a car coasting after takeoff still counts as passing through the point.</summary>
        public const float CoastReach = 100f;
        /// <summary>Slowest takeoff speed that is counted on to carry the car on to the point.</summary>
        private const float MinimumCoastSpeed = 100f;

        /// <summary>
        /// Seconds from now at which the car origin passes <paramref name="point"/>, driving flat out
        /// until <paramref name="takeoff"/> (seconds from now) and then coasting in a straight line
        /// at its takeoff velocity: a car in the air cannot speed up or turn. Infinity when it does
        /// not get there by <paramref name="maxTime"/> or its coast misses the point.
        /// </summary>
        public static float ThroughTime(GroundState start, Vec3 point, float takeoff, float maxTime)
        {
            var drive = new DriveTarget(point, Vec3.Zero);
            RolloutResult rollout = Navigator.Rollout(start, drive, MathF.Min(takeoff, maxTime));
            if (rollout.Arrived) return rollout.Time;
            if (takeoff >= maxTime) return float.PositiveInfinity;
            GroundState s = rollout.Final;
            Vec3 heading = s.Forward.Flatten().Normalize();
            Vec3 to = (point - s.Position).Flatten();
            float along = to.Dot(heading);
            if (along <= 0f || s.Speed < MinimumCoastSpeed || (to - heading * along).Length() > CoastReach)
                return float.PositiveInfinity;
            float time = s.Time + along / s.Speed;
            return time <= maxTime ? time : float.PositiveInfinity;
        }
    }
}
