using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Goal-relative defensive geometry and commitment gates. Race ownership is not goal coverage.
    /// Distances are Unreal units and times are seconds. These are bounded heuristics, not a
    /// proof of save reachability: jumps, collisions and opponent touches still require match tests.
    /// </summary>
    public static class Defense
    {
        public const float ReactionTime = 0.12f;
        public const float StopRadius = 75f;
        public const float ShallowNetDepth = 80f;

        private static float Side(Vec3 goal) => goal.y < 0 ? -1 : 1;
        private static float SmoothStep(float x)
        {
            x = System.Math.Clamp(x, 0, 1);
            return x * x * (3 - 2 * x);
        }

        public static bool IsGoalSide(Vec3 car, Vec3 ball, Vec3 goal, float tolerance = 0)
        {
            if (!ControlMath.Finite(car) || !ControlMath.Finite(ball) || !ControlMath.Finite(goal)) return false;
            Vec3 direction = ControlMath.FlatUnit(goal - ball, new Vec3(0, Side(goal), 0));
            return (car.y - ball.y) * Side(goal) >= -tolerance &&
                (car - ball).Dot(direction) >= -tolerance;
        }

        /// <summary>A teammate must cover the shooting corridor now AND after its current momentum.</summary>
        public static bool CoversGoal(Car car, Vec3 ball, Vec3 goal)
        {
            if (car == null || car.IsDemolished || !car.IsGrounded || car.Location.z > 300 ||
                !ControlMath.Finite(car.Velocity) || !IsGoalSide(car.Location, ball, goal)) return false;
            Vec3 future = car.Location + car.Velocity * 0.25f;
            if (!IsGoalSide(future, ball, goal)) return false;
            float span = (goal.y - ball.y) * Side(goal);
            if (span < 100) return false;
            bool InCorridor(Vec3 point)
            {
                float fraction = (point.y - ball.y) * Side(goal) / span;
                if (fraction < 0.05f || fraction > 1.08f) return false;
                float center = ball.x + (goal.x - ball.x) * fraction;
                float halfWidth = Goal.Width * 0.5f * MathF.Min(fraction, 1) + 220;
                return MathF.Abs(point.x - center) <= halfWidth;
            }
            return InCorridor(car.Location) && InCorridor(future);
        }

        public static bool ShouldAnchor(TacticalFrame frame) =>
            frame.TeamCount <= 1 || frame.LastBack || frame.TeamRank >= 2 ||
            (frame.TeamCount == 2 && frame.TeamRank > 0) || (!frame.HasCover && frame.TeamRank > 0);

        /// <summary>Never convert a lost last-man race into an attacking commitment.</summary>
        public static bool CanChallenge(TacticalFrame frame, Car car, Vec3 ball, Vec3 goal)
        {
            if (frame.TeamRank != 0 || car == null || car.IsDemolished ||
                !IsGoalSide(car.Location, ball, goal, 80)) return false;
            float margin = frame.HasCover ? -0.05f : 0.20f;
            // A controlled, immediate contact may be played without waiting for a 200 ms lead.
            if (car.Location.Dist(ball) < 250 && frame.MyEta <= 0.20f) margin = -0.05f;
            return float.IsFinite(frame.MyEta) && float.IsFinite(frame.OpponentEta) && frame.FreeTime >= margin;
        }

        public static float AttackDeadline(TacticalFrame frame) =>
            MathF.Max(0, MathF.Min(3, frame.OpponentEta - (frame.HasCover || frame.MyEta <= 0.20f ? -0.05f : 0.18f)));

        public static bool CanRefill(TacticalFrame frame, Vec3 ball, Vec3 goal, bool pressure) =>
            !pressure && (frame.HasCover ||
                (ball.y * Side(goal) < -1000 && frame.OpponentEta > 2.5f));

        /// <summary>
        /// Lead an incoming ball using the collision-aware prediction, never an opponent's position.
        /// Do not move the defensive reference upfield just because the untouched ball is moving away.
        /// </summary>
        public static Vec3 ReferenceBall(BallPrediction prediction, Vec3 ball, Vec3 goal, float now)
        {
            if (prediction.TrySample(now + 0.20f, out Ball sample) &&
                ControlMath.Finite(sample.location) && sample.location.y * Side(goal) > ball.y * Side(goal))
                return sample.location;
            return ball;
        }

        /// <summary>
        /// Continuous ball-to-mouth geometry. Deep balls move the guard towards the goal line,
        /// not towards a fixed 3250/4400 field coordinate. A shallow, post-safe net position is legal.
        /// </summary>
        public static Vec3 ShadowTarget(Vec3 ball, Vec3 goal, bool anchor)
        {
            if (!ControlMath.Finite(goal)) goal = new Vec3(0, -5120, 0);
            float side = Side(goal), goalDepth = MathF.Abs(goal.y);
            if (!ControlMath.Finite(ball)) return new Vec3(goal.x, side * (goalDepth - 100), 17);
            float safeHalfWidth = MathF.Max(0, Goal.Width / 2 - 180);
            float post = -System.Math.Clamp((ball.x - goal.x) * 0.30f, -600, 600);
            Vec3 mouth = new Vec3(goal.x + post, side * (goalDepth + ShallowNetDepth), 17);
            Vec3 flatBall = new Vec3(ball.x, ball.y, 17);
            float distance = flatBall.FlatDist(mouth);
            float fraction = distance < 1 ? 1 : MathF.Min(1, (anchor ? 1900 : 900) / distance);
            if (anchor) fraction = MathF.Max(fraction, SmoothStep((ball.y * side - 1200) / 1800));
            Vec3 target = flatBall + (mouth - flatBall) * fraction;
            if (!anchor)
                target.x -= MathF.Tanh((ball.x - goal.x) / 700) * 650 * (1 - fraction);
            target.x = System.Math.Clamp(target.x, -3200, 3200);
            // Narrow continuously towards the mouth; a hard near-post clamp creates a lateral
            // target jump when a deep corner ball crosses the clamp threshold.
            float funnel = SmoothStep((target.y * side - (goalDepth - 900)) / 700);
            float halfWidth = 3200 + (safeHalfWidth - 3200) * funnel;
            target.x = System.Math.Clamp(target.x, goal.x - halfWidth, goal.x + halfWidth);
            target.y = side * System.Math.Clamp(target.y * side, -goalDepth + 200, goalDepth + ShallowNetDepth);
            return new Vec3(target.x, target.y, 17);
        }

        /// <summary>Terminal speed is zero; latency and existing closing speed consume stopping room.</summary>
        public static float GuardSpeed(Car car, Vec3 target, float cruiseSpeed)
        {
            if (car == null || !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity) ||
                !ControlMath.Finite(target) || !float.IsFinite(cruiseSpeed)) return 0;
            float distance = car.Location.FlatDist(target);
            Vec3 direction = ControlMath.FlatUnit(target - car.Location, car.Forward);
            float closing = MathF.Max(0, car.Velocity.Dot(direction));
            float room = MathF.Max(0, distance - StopRadius - closing * ReactionTime);
            float braking = MathF.Sqrt(2 * Car.BrakeAccel * 0.85f * room);
            float approach = MathF.Max(0, distance - StopRadius) * 2.5f;
            return MathF.Min(System.Math.Clamp(cruiseSpeed, 0, Car.MaxSpeed), MathF.Min(braking, approach));
        }

        /// <summary>Interpolate inbound goal-plane crossings rather than using a later slice's x/z.</summary>
        public static float GoalThreat(BallSlice[] slices, Vec3 goal, float now, float horizon, out Vec3 crossing)
        {
            crossing = goal;
            if (slices == null || !ControlMath.Finite(goal) || !float.IsFinite(now) ||
                !float.IsFinite(horizon) || horizon < 0) return float.PositiveInfinity;
            float side = Side(goal), depth = MathF.Abs(goal.y);
            BallSlice previous = null;
            bool InMouth(Vec3 p) => MathF.Abs(p.x - goal.x) < Goal.Width / 2 + Ball.Radius &&
                p.z >= -Ball.Radius && p.z < Goal.Height + Ball.Radius;
            foreach (BallSlice slice in slices)
            {
                if (slice == null || !float.IsFinite(slice.Time) || !ControlMath.Finite(slice.Location))
                { previous = null; continue; }
                if (previous != null && slice.Time <= previous.Time) { previous = null; continue; }
                float before = previous == null ? depth : previous.Location.y * side;
                float after = slice.Location.y * side;
                if (previous != null && before < depth && after >= depth && after > before)
                {
                    float fraction = (depth - before) / (after - before);
                    float time = previous.Time + (slice.Time - previous.Time) * fraction;
                    Vec3 point = previous.Location + (slice.Location - previous.Location) * fraction;
                    if (time >= now && time <= now + horizon && InMouth(point))
                    { crossing = point; return time - now; }
                }
                // A prediction can start beyond the plane. It is already an emergency if current/future.
                else if (previous == null && slice.Time >= now && slice.Time <= now + horizon &&
                    after >= depth && InMouth(slice.Location))
                { crossing = slice.Location; return slice.Time - now; }
                if (slice.Time > now + horizon) break;
                previous = slice;
            }
            return float.PositiveInfinity;
        }

        public static Vec3 EmergencyTarget(Vec3 crossing, Vec3 goal)
        {
            float half = MathF.Max(0, Goal.Width / 2 - 160);
            return new Vec3(System.Math.Clamp(crossing.x, goal.x - half, goal.x + half),
                goal.y - Side(goal) * 60, 17);
        }
    }
}
