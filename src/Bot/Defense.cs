using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public enum DefensiveRole
    {
        Shadow,
        Support,
        Anchor
    }

    /// <summary>
    /// Goal-relative defensive geometry and commitment gates.
    /// Race ownership and actual goal coverage are intentionally separate concepts.
    /// </summary>
    public static class Defense
    {
        public const float ReactionTime = 0.12f;
        public const float ArrivalRadius = 70f;
        public const float ShallowNetDepth = 60f;

        private static float Side(Vec3 goal) => goal.y < 0 ? -1 : 1;

        private static float SmoothStep(float value)
        {
            value = System.Math.Clamp(value, 0f, 1f);
            return value * value * (3f - 2f * value);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>
        /// Signed planar distance from the ball in the ball-to-own-goal direction.
        /// Positive values are goal-side of the ball; negative values are upfield/ahead of it.
        /// </summary>
        public static float GoalSideProgress(Vec3 point, Vec3 ball, Vec3 goal)
        {
            if (!ControlMath.Finite(point) || !ControlMath.Finite(ball) || !ControlMath.Finite(goal))
                return float.NegativeInfinity;

            Vec3 direction = ControlMath.FlatUnit(goal - ball, new Vec3(0, Side(goal), 0));
            return (point - ball).Flatten().Dot(direction);
        }

        public static bool IsGoalSide(Vec3 point, Vec3 ball, Vec3 goal, float buffer = 0) =>
            GoalSideProgress(point, ball, goal) >= buffer;

        /// <summary>
        /// A teammate only counts as usable cover if it remains in the ball-to-mouth shooting corridor
        /// after a short momentum projection. Merely being deeper on the field is not enough.
        /// </summary>
        public static bool CoversGoal(Car car, Vec3 ball, Vec3 goal)
        {
            if (car == null || car.IsDemolished || !car.IsGrounded || car.Location.z > 300 ||
                !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity) ||
                !IsGoalSide(car.Location, ball, goal))
                return false;

            Vec3 goalward = ControlMath.FlatUnit(goal - ball, new Vec3(0, Side(goal), 0));
            float outboundSpeed = -car.Velocity.Dot(goalward);
            if (outboundSpeed > 900f)
                return false;

            Vec3 future = car.Location + car.Velocity * 0.22f;
            if (!IsGoalSide(future, ball, goal))
                return false;

            float side = Side(goal);
            float span = (goal.y - ball.y) * side;
            if (span < 120)
                return false;

            bool InCorridor(Vec3 point)
            {
                float fraction = (point.y - ball.y) * side / span;
                if (fraction < 0.04f || fraction > 1.08f)
                    return false;

                float center = ball.x + (goal.x - ball.x) * fraction;
                float halfWidth = Goal.Width * 0.5f * MathF.Min(fraction, 1f) + 230f;
                return MathF.Abs(point.x - center) <= halfWidth;
            }

            return InCorridor(car.Location) && InCorridor(future);
        }

        /// <summary>
        /// Solo defense deliberately remains a dynamic shadow role. Anchoring is a team coverage role,
        /// not a synonym for being the only defender.
        /// </summary>
        public static bool ShouldAnchor(TacticalFrame frame)
        {
            if (frame == null || frame.TeamCount <= 1)
                return false;
            if (frame.TeamCount == 2)
                return frame.TeamRank > 0;
            if (frame.TeamRank >= 2)
                return true;
            return frame.TeamRank > 0 && (frame.LastBack || !frame.HasCover);
        }

        /// <summary>
        /// Use a short future ball sample only when the untouched prediction is moving toward our goal.
        /// This compensates planning latency without letting a retreating ball drag the defense upfield.
        /// </summary>
        public static Vec3 ReferenceBall(BallPrediction prediction, Vec3 ball, Vec3 goal, float now)
        {
            if (!ControlMath.Finite(ball) || !ControlMath.Finite(goal))
                return ball;

            if (prediction.TrySample(now + 0.20f, out Ball sample) &&
                sample != null && ControlMath.Finite(sample.location) &&
                GoalSideProgress(sample.location, ball, goal) > 20f)
                return sample.location;

            return ball;
        }

        /// <summary>
        /// Continuous ball-to-goal defensive positioning. Every role is constrained to remain goal-side
        /// of the reference ball. The solo shadow compresses under imminent contact instead of parking.
        /// </summary>
        public static Vec3 ShadowTarget(Vec3 ball, Vec3 goal, DefensiveRole role,
            float pressureTime = float.PositiveInfinity)
        {
            if (!ControlMath.Finite(goal))
                goal = new Vec3(0, -5120, 0);

            float side = Side(goal);
            float goalDepth = MathF.Abs(goal.y);
            if (!ControlMath.Finite(ball))
                return new Vec3(goal.x, side * (goalDepth - 260), 17);

            Vec3 flatBall = new Vec3(ball.x, ball.y, 17);
            Vec3 flatGoal = new Vec3(goal.x, goal.y, 17);
            Vec3 goalward = ControlMath.FlatUnit(flatGoal - flatBall, new Vec3(0, side, 0));
            float goalDistance = flatBall.FlatDist(flatGoal);

            // danger approaches one as the ball enters the defensive third.
            float danger = 1f - SmoothStep((goalDistance - 650f) / 3500f);
            float urgency = float.IsFinite(pressureTime)
                ? 1f - SmoothStep((pressureTime - 0.08f) / 1.15f)
                : 0f;

            float desiredGap;
            float minimumProgress;
            float lateralBias;
            switch (role)
            {
                case DefensiveRole.Anchor:
                    desiredGap = Lerp(1950f, 1125f, danger);
                    minimumProgress = 720f;
                    lateralBias = 520f;
                    break;
                case DefensiveRole.Support:
                    desiredGap = Lerp(1050f, 760f, danger);
                    minimumProgress = 500f;
                    lateralBias = 650f;
                    break;
                default:
                    // A solo/first defender preserves reaction space, then closes it as contact becomes imminent.
                    desiredGap = Lerp(1500f, 1125f, danger) - 360f * urgency;
                    desiredGap = System.Math.Clamp(desiredGap, 820f, 1550f);
                    minimumProgress = 600f - 120f * urgency;
                    lateralBias = 260f;
                    break;
            }

            // Stay outside the goal line whenever there is room. When the ball is already extremely
            // deep, allow only a shallow net position so the invariant can still be respected.
            float lineClearance = role == DefensiveRole.Anchor ? 105f : 220f;
            float maximumUsefulGap = goalDistance > lineClearance + 230f
                ? goalDistance - lineClearance
                : goalDistance + ShallowNetDepth;
            float gap = MathF.Min(desiredGap, MathF.Max(220f, maximumUsefulGap));

            Vec3 target = flatBall + goalward * gap;
            float lateral = MathF.Tanh((flatBall.x - flatGoal.x) / 900f);
            target.x -= lateral * lateralBias * (1f - 0.40f * danger);

            // Deep anchors prefer the far-post half of the mouth. Blend continuously so crossing
            // midfield or a side threshold cannot teleport the target.
            if (role == DefensiveRole.Anchor)
            {
                float ballDepth = flatBall.y * side;
                float nearGoal = SmoothStep((ballDepth - (goalDepth - 2400f)) / 1800f);
                float sign = MathF.Abs(flatBall.x - flatGoal.x) < 60f ? 0f : MathF.Sign(flatBall.x - flatGoal.x);
                float farPostX = flatGoal.x - sign * (Goal.Width * 0.5f - 220f);
                target.x = Lerp(target.x, farPostX, nearGoal);
            }

            float signedTargetDepth = target.y * side;
            float funnel = SmoothStep((signedTargetDepth - (goalDepth - 1200f)) / 950f);
            float safeHalfWidth = MathF.Max(0f, Goal.Width * 0.5f - 175f);
            float halfWidth = Lerp(3200f, safeHalfWidth, funnel);
            target.x = System.Math.Clamp(target.x, goal.x - halfWidth, goal.x + halfWidth);
            target.y = side * System.Math.Clamp(target.y * side, -goalDepth + 180f, goalDepth + ShallowNetDepth);

            // Lateral lane shaping must never pull the target back ahead of the ball.
            float availableProgress = GoalSideProgress(target, flatBall, flatGoal);
            float requiredProgress = MathF.Min(minimumProgress, MathF.Max(150f, goalDistance + ShallowNetDepth));
            if (availableProgress < requiredProgress)
                target += goalward * (requiredProgress - availableProgress);

            // Re-apply mouth bounds after the progress correction.
            target.y = side * System.Math.Clamp(target.y * side, -goalDepth + 180f, goalDepth + ShallowNetDepth);
            if (target.y * side > goalDepth - 1200f)
                target.x = System.Math.Clamp(target.x, goal.x - safeHalfWidth, goal.x + safeHalfWidth);

            return new Vec3(target.x, target.y, 17);
        }

        /// <summary>
        /// When a car is ahead of the ball, recover through the far-post side rather than circling
        /// in front of the attacker. The returned waypoint is itself goal-side of the ball.
        /// </summary>
        public static Vec3 RecoveryTarget(Vec3 car, Vec3 ball, Vec3 goal)
        {
            if (!ControlMath.Finite(goal))
                goal = new Vec3(0, -5120, 0);
            if (!ControlMath.Finite(ball))
                return new Vec3(goal.x, goal.y - Side(goal) * 260f, 17);

            float side = Side(goal);
            float goalDepth = MathF.Abs(goal.y);
            Vec3 flatBall = new Vec3(ball.x, ball.y, 17);
            Vec3 flatGoal = new Vec3(goal.x, goal.y, 17);
            Vec3 goalward = ControlMath.FlatUnit(flatGoal - flatBall, new Vec3(0, side, 0));
            float goalDistance = flatBall.FlatDist(flatGoal);
            float safePost = Goal.Width * 0.5f - 215f;

            float ballSign = MathF.Abs(flatBall.x - goal.x) > 70f
                ? MathF.Sign(flatBall.x - goal.x)
                : (ControlMath.Finite(car) && MathF.Abs(car.x - goal.x) > 70f ? MathF.Sign(car.x - goal.x) : 1f);
            float farPostX = goal.x - ballSign * safePost;

            float desiredProgress = System.Math.Clamp(goalDistance * 0.72f, 850f, 2200f);
            if (goalDistance < 1100f)
                desiredProgress = MathF.Max(280f, goalDistance - 170f);
            if (goalDistance < 420f)
                desiredProgress = goalDistance + 35f;

            Vec3 target = flatBall + goalward * desiredProgress;
            float farPostBlend = SmoothStep((flatBall.y * side - 1000f) / 2600f);
            target.x = Lerp(target.x, farPostX, 0.45f + 0.40f * farPostBlend);

            float progress = GoalSideProgress(target, flatBall, flatGoal);
            float required = MathF.Min(650f, MathF.Max(160f, goalDistance + ShallowNetDepth));
            if (progress < required)
                target += goalward * (required - progress);

            float signedDepth = target.y * side;
            if (signedDepth > goalDepth - 1150f)
                target.x = System.Math.Clamp(target.x, goal.x - safePost, goal.x + safePost);
            target.y = side * System.Math.Clamp(target.y * side, -goalDepth + 180f, goalDepth + ShallowNetDepth);
            return new Vec3(target.x, target.y, 17);
        }

        /// <summary>
        /// Last-man challenges require both goal-side geometry and a race margin. Immediate controlled
        /// contact remains legal, preventing the safety gate from becoming passive goal-line defense.
        /// </summary>
        public static bool CanChallenge(TacticalFrame frame, Car car, Vec3 ball, Vec3 goal)
        {
            if (frame == null || car == null || car.IsDemolished || frame.TeamRank != 0 ||
                !IsGoalSide(car.Location, ball, goal, 30f) ||
                !float.IsFinite(frame.MyEta) || !float.IsFinite(frame.OpponentEta))
                return false;

            bool immediate = car.Location.Dist(ball) < 275f && frame.MyEta <= 0.22f;
            if (immediate)
                return true;

            float requiredMargin;
            if (frame.HasCover)
                requiredMargin = -0.05f;
            else if (frame.UnderPressure)
                requiredMargin = 0.07f;
            else
                requiredMargin = 0.16f;

            if (frame.LastBack && !frame.HasCover)
                requiredMargin += 0.04f;

            return frame.FreeTime >= requiredMargin;
        }

        public static float AttackDeadline(TacticalFrame frame)
        {
            if (frame == null || !float.IsFinite(frame.OpponentEta))
                return 0f;
            float reserve = frame.HasCover || frame.MyEta <= 0.20f ? -0.05f : 0.16f;
            return System.Math.Clamp(frame.OpponentEta - reserve, 0f, 3f);
        }

        public static bool CanRefill(TacticalFrame frame, Car car, Vec3 ball, Vec3 goal, bool pressure)
        {
            if (frame == null || car == null || pressure || frame.TeamRank == 0 ||
                !IsGoalSide(car.Location, ball, goal, 20f))
                return false;

            if (frame.HasCover)
                return true;

            // Without explicit cover, only a non-first-man may refill when the ball is clearly
            // out of our half and the opponent contact window is long.
            float side = Side(goal);
            bool ballSafelyUpfield = ball.y * side < -900f;
            return ballSafelyUpfield && frame.OpponentEta > 2.5f;
        }

        /// <summary>
        /// Target speed for a moving shadow. Match a useful fraction of the ball's goalward speed,
        /// while retaining enough speed to adjust laterally under pressure.
        /// </summary>
        public static float ShadowTerminalSpeed(Ball ball, Vec3 goal, bool pressure)
        {
            if (ball == null || !ControlMath.Finite(ball.location) || !ControlMath.Finite(ball.velocity) ||
                !ControlMath.Finite(goal))
                return pressure ? 700f : 500f;

            Vec3 axis = ControlMath.FlatUnit(goal - ball.location, new Vec3(0, Side(goal), 0));
            float goalwardSpeed = MathF.Max(0f, ball.velocity.Dot(axis));
            return System.Math.Clamp(goalwardSpeed * 0.80f + (pressure ? 180f : 0f), 450f, 1350f);
        }

        /// <summary>
        /// Braking envelope with a configurable terminal speed. Existing closing speed consumes
        /// reaction distance, so the controller does not repeatedly fly through a moving shadow point.
        /// </summary>
        public static float DriveSpeed(Car car, Vec3 target, float cruiseSpeed, float terminalSpeed)
        {
            if (car == null || !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity) ||
                !ControlMath.Finite(target) || !float.IsFinite(cruiseSpeed) || !float.IsFinite(terminalSpeed))
                return 0f;

            float cruise = System.Math.Clamp(cruiseSpeed, 0f, Car.MaxSpeed);
            float terminal = System.Math.Clamp(terminalSpeed, 0f, cruise);
            float distance = car.Location.FlatDist(target);
            Vec3 direction = ControlMath.FlatUnit(target - car.Location, car.Forward);
            float closing = MathF.Max(0f, car.Velocity.Dot(direction));
            float room = MathF.Max(0f, distance - ArrivalRadius - closing * ReactionTime);
            float braking = MathF.Sqrt(terminal * terminal + 2f * Car.BrakeAccel * 0.86f * room);
            float approach = terminal + MathF.Max(0f, distance - ArrivalRadius) * 2.7f;
            return MathF.Min(cruise, MathF.Min(braking, approach));
        }

        /// <summary>
        /// Interpolate inbound goal-plane crossings rather than trusting the first sample beyond the line.
        /// </summary>
        public static float GoalThreat(BallSlice[] slices, Vec3 goal, float now, float horizon, out Vec3 crossing)
        {
            crossing = goal;
            if (slices == null || !ControlMath.Finite(goal) || !float.IsFinite(now) ||
                !float.IsFinite(horizon) || horizon < 0)
                return float.PositiveInfinity;

            float side = Side(goal);
            float depth = MathF.Abs(goal.y);
            BallSlice previous = null;

            bool InMouth(Vec3 point) =>
                MathF.Abs(point.x - goal.x) < Goal.Width * 0.5f + Ball.Radius &&
                point.z >= -Ball.Radius && point.z < Goal.Height + Ball.Radius;

            foreach (BallSlice slice in slices)
            {
                if (slice == null || !float.IsFinite(slice.Time) || !ControlMath.Finite(slice.Location))
                {
                    previous = null;
                    continue;
                }

                if (previous != null && slice.Time <= previous.Time)
                {
                    previous = null;
                    continue;
                }

                float after = slice.Location.y * side;
                if (previous != null)
                {
                    float before = previous.Location.y * side;
                    if (before < depth && after >= depth && after > before)
                    {
                        float fraction = (depth - before) / (after - before);
                        float time = previous.Time + (slice.Time - previous.Time) * fraction;
                        Vec3 point = previous.Location + (slice.Location - previous.Location) * fraction;
                        if (time >= now && time <= now + horizon && InMouth(point))
                        {
                            crossing = point;
                            return time - now;
                        }
                    }
                }
                else if (slice.Time >= now && slice.Time <= now + horizon && after >= depth && InMouth(slice.Location))
                {
                    crossing = slice.Location;
                    return slice.Time - now;
                }

                if (slice.Time > now + horizon)
                    break;
                previous = slice;
            }

            return float.PositiveInfinity;
        }

        public static Vec3 EmergencyTarget(Vec3 crossing, Vec3 goal)
        {
            float half = MathF.Max(0f, Goal.Width * 0.5f - 160f);
            return new Vec3(System.Math.Clamp(crossing.x, goal.x - half, goal.x + half),
                goal.y - Side(goal) * 55f, 17);
        }
    }
}
