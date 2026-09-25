using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>Where and how a grounded car should arrive.</summary>
    public struct DriveTarget
    {
        /// <summary>Destination of the car origin (z ignored).</summary>
        public Vec3 Point;
        /// <summary>Required heading at arrival (flat unit), or zero for any heading.</summary>
        public Vec3 Direction;
        /// <summary>Absolute arrival time, or NaN to arrive as early as possible.</summary>
        public float ArrivalTime;
        /// <summary>Whether boost may be spent.</summary>
        public bool AllowBoost;
        /// <summary>Largest lead-in distance used to align with <see cref="Direction"/>.</summary>
        public float MaxLead;
        /// <summary>Speed to arrive with (positioning), or NaN to arrive as fast as possible.</summary>
        public float ArrivalSpeed;

        public DriveTarget(Vec3 point, Vec3 direction, float arrivalTime = float.NaN, bool allowBoost = true, float maxLead = 1300f)
        {
            ArrivalSpeed = float.NaN;
            Point = new Vec3(point.x, point.y, 0);
            Direction = new Vec3(direction.x, direction.y, 0);
            float length = Direction.Length();
            Direction = length > 0.1f ? Direction / length : Vec3.Zero;
            ArrivalTime = arrivalTime;
            AllowBoost = allowBoost;
            MaxLead = maxLead;
        }

        public bool HasDirection => Direction.Length() > 0.5f;
    }

    public struct DriveCommand
    {
        public float Throttle, Steer;
        public bool Boost, Handbrake;
    }

    /// <summary>Outcome of simulating the navigation policy toward a target.</summary>
    public struct RolloutResult
    {
        public bool Arrived;
        public float Time;              // seconds from rollout start
        public float ArrivalSpeed;
        public float HeadingError;      // radians at arrival
        public float BoostUsed;
        public float ClosestBallApproach; // smallest car-ball distance before arrival (NaN without a ball path)
        public GroundState Final;
    }

    /// <summary>
    /// Ground navigation policy shared by execution and planning. Planning simulates this exact
    /// policy on <see cref="GroundModel"/>, so the times the strategy reasons about are the times
    /// the controller actually achieves.
    /// </summary>
    public static class Navigator
    {
        public const float ArrivalRadius = 55f;
        /// <summary>Early-arrival margin tolerated before a timed approach starts shedding speed.</summary>
        public const float TimingSlack = 0.07f;
        /// <summary>
        /// Largest angle to the steering point at which an early car sheds speed: slowing in a hard
        /// turn leaves it slow and mid-turn, where arrival times are least predictable.
        /// </summary>
        public const float SheddingAngle = 0.5f;
        /// <summary>Speed floor while turning hard (turn radius about 200 uu).</summary>
        public const float MinimumTurnSpeed = 450f;
        /// <summary>Heading agreement (cosine) a directed arrival needs to count as arrived.</summary>
        public const float ArrivalAlignment = 0.95f;
        /// <summary>
        /// Looser agreement used to judge whether an action under way can still arrive: execution
        /// follows a slightly different path than the plan's flat-out rollout, and a few degrees
        /// of heading at contact only shade the touch.
        /// </summary>
        public const float ExecutionAlignment = 0.85f;
        /// <summary>Step multiple used by rollouts while cruising straight far from the target.</summary>
        public const int CruiseStepFactor = 3;
        /// <summary>Speed band (uu/s) around a held speed inside which the throttle only trickles.</summary>
        public const float HoldBand = 15f;
        /// <summary>Throttle that roughly holds speed below 1410 uu/s (it only needs to beat zero to avoid the coasting brake).</summary>
        public const float HoldThrottle = 0.02f;
        /// <summary>Deceleration planned for when shedding speed before a positioning target.</summary>
        public const float PlannedBraking = 2000f;

        /// <summary>
        /// Shortest tangent-arc-line path onto the arrival line: straight to a turning circle, around
        /// it, then straight into the destination along the required heading.
        /// </summary>
        public readonly struct ApproachPath
        {
            public readonly Vec3 Tangent, Entry, Centre;
            public readonly float Radius, TangentLength, ArcLength, FinalLength;
            public readonly int Turn; // +1 counter-clockwise (steer right), -1 clockwise
            public readonly bool Valid;

            public ApproachPath(Vec3 tangent, Vec3 entry, Vec3 centre, float radius, float tangentLength,
                float arcLength, float finalLength, int turn)
            {
                Tangent = tangent; Entry = entry; Centre = centre; Radius = radius;
                TangentLength = tangentLength; ArcLength = arcLength; FinalLength = finalLength; Turn = turn;
                Valid = true;
            }

            public float Length => TangentLength + ArcLength + FinalLength;

            /// <summary>
            /// Point at arc length <paramref name="s"/> along the path from the car. The final line runs
            /// on past the destination, so a lookahead near the end stays on the arrival line.
            /// </summary>
            public Vec3 PointAt(Vec3 start, Vec3 destination, float s)
            {
                if (s <= TangentLength)
                    return TangentLength > 1e-3f ? start + (Tangent - start) * (s / TangentLength) : Tangent;
                s -= TangentLength;
                if (s <= ArcLength && Radius > 1f)
                {
                    float angle = Turn * s / Radius;
                    Vec3 radial = Tangent - Centre;
                    return Centre + GroundModel.Rotate(radial, angle);
                }
                s -= ArcLength;
                Vec3 finalDirection = FinalLength > 1e-3f ? (destination - Entry) / FinalLength : Vec3.Zero;
                return Entry + finalDirection * s;
            }
        }

        /// <summary>Best circle-tangent approach for turn radius <paramref name="radius"/>, or invalid.</summary>
        public static ApproachPath PlanApproach(Vec3 position, Vec3 forward, in DriveTarget target, float radius, float straight)
        {
            Vec3 u = target.Direction;
            Vec3 destination = target.Point;
            Vec3 entry = destination - u * straight;
            Vec3 left = new(-u.y, u.x, 0);
            ApproachPath best = default;
            float bestCost = float.PositiveInfinity;

            for (int t = 0; t < 2; t++)
            {
                int turn = t == 0 ? 1 : -1;
                // A counter-clockwise arc that ends heading along u has its centre on u's left.
                Vec3 centre = entry + left * (turn * radius);
                Vec3 offset = new(position.x - centre.x, position.y - centre.y, 0);
                float d = offset.Length();
                if (d <= radius * 1.001f)
                    continue;
                // Tangent points lie at +-acos(r/d) from the car's direction from the centre.
                float cos = radius / d, sin = MathF.Sqrt(MathF.Max(0f, 1f - cos * cos));
                Vec3 w = offset / d;
                // Of the two tangent points, keep the one where the car's travel matches the arc's sense.
                Vec3 tangent = default;
                bool found = false;
                for (int k = 0; k < 2; k++)
                {
                    float sign = k == 0 ? 1f : -1f;
                    Vec3 radial = new(w.x * cos - w.y * sign * sin, w.x * sign * sin + w.y * cos, 0);
                    Vec3 candidate = centre + radial * radius;
                    Vec3 travel = new(-radial.y * turn, radial.x * turn, 0);
                    Vec3 approach = candidate - new Vec3(position.x, position.y, 0);
                    if (approach.Dot(travel) > 0f)
                    {
                        tangent = candidate;
                        found = true;
                        break;
                    }
                }
                if (!found)
                    continue;

                float tangentLength = (tangent - new Vec3(position.x, position.y, 0)).Length();
                Vec3 fromCentre = tangent - centre, toEntry = entry - centre;
                float sweep = GroundModel.SignedAngle(fromCentre, toEntry) * turn;
                if (sweep < 0f) sweep += 2f * MathF.PI;
                float arc = sweep * radius;
                // The car must first swing its nose onto the tangent line.
                Vec3 lineDirection = tangentLength > 1f ? (tangent - new Vec3(position.x, position.y, 0)) / tangentLength : forward;
                float initialTurn = MathF.Abs(GroundModel.SignedAngle(forward, lineDirection));
                float cost = tangentLength + arc + straight + initialTurn * radius;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = new ApproachPath(tangent, entry, centre, radius, tangentLength, arc, straight, turn);
                }
            }
            return best;
        }

        /// <summary>Length of the straight run-in onto a directed destination <paramref name="distance"/> away.</summary>
        public static float RunIn(in DriveTarget target, float distance) =>
            MathF.Min(target.MaxLead, MathF.Max(120f, distance * 0.25f));

        /// <summary>
        /// Point the car steers at: the destination, or a pure-pursuit point on the approach path.
        /// A directed approach steers along its arrival line right through the destination (the line
        /// extended past it), so the aim never collapses onto the point in the final metres: a car
        /// a few degrees off the line converges smoothly instead of braking for an impossible turn.
        /// </summary>
        public static Vec3 SteeringPoint(Vec3 position, Vec3 forward, float speed, in DriveTarget target)
        {
            Vec3 toTarget = new(target.Point.x - position.x, target.Point.y - position.y, 0);
            float distance = toTarget.Length();
            if (!target.HasDirection)
                return target.Point;
            float lookahead = System.Math.Clamp(MathF.Abs(speed) * 0.22f, 160f, 480f);
            if (distance < 60f)
                return target.Point + target.Direction * lookahead;

            float radius = GroundModel.TurnRadius(System.Math.Clamp(MathF.Abs(speed), 700f, RL.CarMaxSpeed));
            float straight = RunIn(target, distance);
            ApproachPath path = PlanApproach(position, forward, target, radius, straight);
            if (!path.Valid)
            {
                path = PlanApproach(position, forward, target, radius, 0f);
                if (!path.Valid)
                    return target.Point - target.Direction * MathF.Min(target.MaxLead, distance * 0.5f);
            }
            return path.PointAt(new Vec3(position.x, position.y, 0), target.Point, lookahead);
        }

        /// <summary>Control for a grounded car at (position, forward, speed).</summary>
        public static DriveCommand Control(Vec3 position, Vec3 forward, float speed, float boost, float yawRate,
            in DriveTarget target, float now, float remainingPath = float.NaN)
        {
            forward = new Vec3(forward.x, forward.y, 0).Normalize();
            Vec3 aim = SteeringPoint(position, forward, speed, target);
            Vec3 toAim = new(aim.x - position.x, aim.y - position.y, 0);
            float aimDistance = MathF.Max(toAim.Length(), 1f);
            float angle = GroundModel.SignedAngle(forward, toAim);

            var command = new DriveCommand();
            bool reverse = speed < -100f;
            command.Steer = System.Math.Clamp(3.2f * angle - 0.07f * yawRate, -1f, 1f);
            if (reverse) command.Steer = -command.Steer;

            // Tightest curvature needed to reach the aim point from here (circle tangent to heading).
            // A car needs speed to turn at all, so never slow below a brisk turning speed for it.
            float required = 2f * MathF.Sin(MathF.Abs(angle)) / aimDistance;
            float turnSpeed = required > 1e-5f ? GroundModel.SpeedForCurvature(required * 1.05f) : RL.CarMaxSpeed;
            if (MathF.Abs(angle) > 1.6f) turnSpeed = MathF.Min(turnSpeed, 900f);
            turnSpeed = MathF.Max(turnSpeed, MinimumTurnSpeed);

            float desired = RL.CarMaxSpeed;
            if (float.IsFinite(target.ArrivalTime))
            {
                // Arrive on time and as fast as possible: simulate the flat-out policy from here and
                // shed speed only while that would still arrive early.
                float remaining = target.ArrivalTime - now;
                var asap = target;
                asap.ArrivalTime = float.NaN;
                var state = new GroundState(position, forward, speed, target.AllowBoost ? boost : 0f, 0f, yawRate);
                RolloutResult flatOut = Rollout(state, asap, remaining + 1.0f, dt: 1f / 30f);
                float slack = remaining - flatOut.Time;
                if (slack > TimingSlack && MathF.Abs(angle) < SheddingAngle)
                    desired = MathF.Max(0f, speed - (slack > 0.35f ? 600f : 150f));
            }
            if (float.IsFinite(target.ArrivalSpeed))
            {
                float path = float.IsFinite(remainingPath) ? remainingPath : EstimatePathLength(position, forward, speed, target);
                float room = MathF.Max(0f, path - ArrivalRadius);
                desired = MathF.Min(desired, MathF.Sqrt(target.ArrivalSpeed * target.ArrivalSpeed + 2f * PlannedBraking * room));
            }
            desired = MathF.Min(desired, turnSpeed);

            float forwardSpeed = speed;
            float error = desired - forwardSpeed;
            if (error > 0f)
            {
                command.Throttle = 1f;
                bool aligned = MathF.Abs(angle) < 0.3f;
                command.Boost = target.AllowBoost && aligned && boost > 0f && forwardSpeed < RL.CarMaxSpeed - 10f &&
                    (error > 250f || desired > 1410f && error > 40f);
            }
            else if (error > -120f)
            {
                command.Throttle = System.Math.Clamp(error / 120f + 0.35f, 0f, 1f) * (forwardSpeed < 1410f ? 1f : 0f);
            }
            else
            {
                command.Throttle = error < -400f ? -1f : 0f;
            }
            return command;
        }

        /// <summary>
        /// Throttle and boost that track <paramref name="desired"/> forward speed tightly: full
        /// throttle (and boost when well short) below it, a trickle of throttle to hold it without
        /// the coasting brake, and coasting or braking above it.
        /// </summary>
        public static DriveCommand HoldSpeed(float desired, float speed, float boost)
        {
            var command = new DriveCommand();
            float error = desired - speed;
            if (error > HoldBand)
            {
                command.Throttle = 1f;
                command.Boost = boost > 0f && (error > 120f || (desired > RL.ThrottleMaxSpeed && error > HoldBand));
            }
            else if (error > -HoldBand)
                command.Throttle = speed < RL.ThrottleMaxSpeed ? HoldThrottle : 0f;
            else
                command.Throttle = error < -250f ? -1f : 0f;
            return command;
        }

        /// <summary>
        /// Speed to hold now so that full acceleration from here covers <paramref name="distance"/> in
        /// exactly <paramref name="time"/>. Holding back early and accelerating late means the car
        /// arrives on time and as fast as possible, which is what a strike wants.
        /// </summary>
        public static float LatestDepartureSpeed(float distance, float time, float boost)
        {
            if (distance <= 0f) return 0f;
            if (DrivePhysics.TravelTime(distance, RL.CarMaxSpeed, boost) >= time) return RL.CarMaxSpeed;
            if (DrivePhysics.TravelTime(distance, 0f, boost) <= time) return MathF.Min(distance / time, 400f);
            float lo = 0f, hi = RL.CarMaxSpeed;
            for (int i = 0; i < 14; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (DrivePhysics.TravelTime(distance, mid, boost) > time) lo = mid; else hi = mid;
            }
            return hi;
        }

        /// <summary>Straight distance plus the heading change the car must still make, as arc length.</summary>
        public static float EstimatePathLength(Vec3 position, Vec3 forward, float speed, in DriveTarget target)
        {
            Vec3 to = new(target.Point.x - position.x, target.Point.y - position.y, 0);
            float distance = to.Length();
            float radius = GroundModel.TurnRadius(System.Math.Clamp(MathF.Abs(speed), 700f, RL.CarMaxSpeed));
            if (target.HasDirection && distance >= 60f)
            {
                ApproachPath path = PlanApproach(position, forward, target, radius, RunIn(target, distance));
                if (path.Valid)
                    return path.Length;
            }
            float angle = MathF.Abs(GroundModel.SignedAngle(forward, to));
            float extra = angle > 0.2f ? radius * (angle - MathF.Sin(angle)) : 0f;
            return distance + extra;
        }

        /// <summary>
        /// Simulates the ASAP policy until the car reaches the target (within the arrival radius, and
        /// aligned when a direction is required) or the time budget runs out.
        /// </summary>
        public static RolloutResult Rollout(GroundState start, in DriveTarget target, float maxTime,
            BallPath ballPath = null, float now = 0f, float dt = 1f / 60f, float alignment = ArrivalAlignment)
        {
            var result = new RolloutResult { ClosestBallApproach = float.NaN };
            GroundState s = start;
            float startBoost = s.Boost;
            float previousDistance = float.PositiveInfinity;
            var asap = target;
            asap.ArrivalTime = float.NaN;
            float remainder = 0f;
            for (int step = 0; s.Time <= maxTime && step < 2000; step++)
            {
                Vec3 to = new(target.Point.x - s.Position.x, target.Point.y - s.Position.y, 0);
                float distance = to.Length();
                bool aligned = !target.HasDirection || s.Forward.Dot(target.Direction) > alignment;
                bool passing = distance < ArrivalRadius || (distance < 160f && distance > previousDistance && to.Dot(s.Forward) < 0f);
                if (passing)
                {
                    result.Arrived = aligned && distance < 120f;
                    // Report when the car reaches the point itself, not the edge of the radius.
                    remainder = ArrivalRemainder(to, s.Forward, s.Speed);
                    break;
                }
                previousDistance = distance;

                if (ballPath != null)
                {
                    Vec3 ball = ballPath.PositionAt(now + s.Time);
                    float gap = new Vec3(ball.x - s.Position.x, ball.y - s.Position.y, 0).Length();
                    if (ball.z < 250f && (float.IsNaN(result.ClosestBallApproach) || gap < result.ClosestBallApproach))
                        result.ClosestBallApproach = gap;
                }

                DriveCommand c = Control(s.Position, s.Forward, s.Speed, s.Boost, s.YawRate, asap, s.Time);
                // Straight driving far from the target is smooth: integrate it in longer steps.
                bool cruising = distance > 700f && MathF.Abs(c.Steer) < 0.03f && MathF.Abs(s.YawRate) < 0.05f && c.Throttle > 0.99f;
                GroundModel.Step(ref s, c.Throttle, c.Steer, c.Boost, cruising ? CruiseStepFactor * dt : dt);
            }

            result.Time = s.Time + remainder;
            result.ArrivalSpeed = s.Speed;
            result.HeadingError = target.HasDirection
                ? MathF.Abs(GroundModel.SignedAngle(s.Forward, target.Direction)) : 0f;
            result.BoostUsed = startBoost - s.Boost;
            result.Final = s;
            return result;
        }

        /// <summary>Time still needed to reach a point <paramref name="to"/> away once inside the arrival radius.</summary>
        public static float ArrivalRemainder(Vec3 to, Vec3 forward, float speed)
        {
            float along = to.x * forward.x + to.y * forward.y;
            return along > 0f && speed > 100f ? along / speed : 0f;
        }

        /// <summary>
        /// Ground rollout start state for a car that may be airborne or on a wall. Time is the delay
        /// (seconds from now) before the car can drive.
        /// </summary>
        public static GroundState StartState(Car car)
        {
            Vec3 position = car.Location;
            Vec3 velocity = car.Velocity;
            float landing = 0f;
            if (!car.IsGrounded)
            {
                landing = car.PredictLandingTime();
                if (!float.IsFinite(landing) || landing < 0f) landing = 0f;
                landing = MathF.Min(landing, 2f);
                position = car.PredictLocation(landing);
                velocity = car.PredictVelocity(landing);
            }
            else if (car.Up.z < 0.7f)
            {
                // Wall driving: unroll the wall onto the floor so distances stay comparable.
                Vec3 inward = new Vec3(-car.Up.x, -car.Up.y, 0);
                float inwardLength = inward.Length();
                if (inwardLength > 0.1f)
                    position = new Vec3(position.x, position.y, 0) - inward / inwardLength * position.z;
                velocity = new Vec3(velocity.x, velocity.y, 0) + (inwardLength > 0.1f ? -inward / inwardLength * MathF.Max(0f, -velocity.z) : Vec3.Zero);
            }

            Vec3 flat = new(velocity.x, velocity.y, 0);
            Vec3 heading = car.IsGrounded && car.Up.z >= 0.7f ? car.Forward : (flat.Length() > 300f ? flat : car.Forward);
            float speed = car.IsGrounded && car.Up.z >= 0.7f ? car.Velocity.Dot(car.Forward) : flat.Length();
            float boost = car.Boost;
            float yawRate = car.IsGrounded && car.Up.z >= 0.7f ? car.AngularVelocity.z : 0f;
            return new GroundState(position, heading, speed, boost, landing, yawRate);
        }
    }
}
