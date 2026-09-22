using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;
using RLBot.Flat;

namespace Bot
{
    /// <summary>
    /// Deterministic possession geometry shared by the supervisor and mechanical controllers.
    /// "Possession" is intentionally distinct from winning a race to a loose ball.
    /// </summary>
    public static class PossessionControl
    {
        /// <summary>
        /// Continuous estimate of how securely the ball is balanced over the car. A nearby opponent
        /// must not erase this state: pressure should normally trigger an outplay, not a retreat.
        /// </summary>
        public static float RoofControlQuality(Car car, Ball ball)
        {
            if (car == null || ball == null || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(car.Velocity) || !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ball.velocity))
                return 0f;

            Vec3 local = car.Local(ball.location - car.Location);
            if (local.z < 85f || local.z > 245f)
                return 0f;

            Vec3 relativeVelocity = car.Local(ball.velocity - car.Velocity);
            float planar = MathF.Sqrt(
                (local.x / 175f) * (local.x / 175f) +
                (local.y / 125f) * (local.y / 125f));

            float centered = 1f - System.Math.Clamp(planar, 0f, 1f);
            float height = 1f - System.Math.Clamp(MathF.Abs(local.z - 155f) / 90f, 0f, 1f);
            float matched = 1f - System.Math.Clamp(relativeVelocity.Length() / 900f, 0f, 1f);
            return System.Math.Clamp(centered * 0.50f + height * 0.20f + matched * 0.30f, 0f, 1f);
        }

        public static bool HasControlledPossession(Car car, Ball ball) =>
            RoofControlQuality(car, ball) >= 0.48f;

        public static bool HasAirControl(Car car, Ball ball)
        {
            if (car == null || ball == null || car.IsGrounded ||
                !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity) ||
                !ControlMath.Finite(ball.location) || !ControlMath.Finite(ball.velocity))
                return false;

            Vec3 local = car.Local(ball.location - car.Location);
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            return ball.location.z > 240f && local.z > -80f && local.x > -220f &&
                local.Length() < 720f && relativeSpeed < 1250f;
        }

        /// <summary>
        /// Whether an existing possession mechanic deserves continuity even when a loose-ball race
        /// estimator briefly says the opponent is earlier.
        /// </summary>
        public static bool ShouldRetainPossession(TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal)
        {
            if (frame == null || car == null || ball == null || car.IsDemolished)
                return false;

            float ownDepth = Defense.OwnDepth(ball.location, ownGoal);
            bool deepPressure = ownDepth > 3000f &&
                ((float.IsFinite(frame.PressureTime) && frame.PressureTime < 1.20f) ||
                 (float.IsFinite(frame.OpponentEta) && frame.OpponentEta < 1.30f));

            float roofQuality = RoofControlQuality(car, ball);
            if (roofQuality >= 0.48f)
            {
                // High-quality roof possession can still be carried out of the box, but a marginal
                // own-box dribble under immediate pressure must not be sticky.
                if (deepPressure && roofQuality < 0.72f)
                    return false;
                return true;
            }
            if (HasAirControl(car, ball) && (car.Boost > 0f || car.Location.Dist(ball.location) < 230f))
                return !deepPressure;

            if (deepPressure)
                return false;

            if (frame.TeamRank != 0 ||
                !Defense.IsTacticallyGoalSide(car.Location, ball.location, ownGoal, -80f))
                return false;

            Vec3 local = car.Local(ball.location - car.Location);
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            bool closeControl = ball.location.z < 360f && local.x > -140f && local.x < 560f &&
                MathF.Abs(local.y) < 240f && local.Length() < 620f && relativeSpeed < 1050f;

            return closeControl && Defense.EffectiveFreeTime(frame) >= -0.16f;
        }

        public static bool CanAcquireGround(TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal)
        {
            if (frame == null || car == null || ball == null || frame.TeamRank != 0 ||
                car.IsDemolished || !car.IsGrounded)
                return false;

            if (HasControlledPossession(car, ball))
                return true;

            float side = ownGoal.y < 0 ? -1f : 1f;
            float ownDepth = ball.location.y * side;
            float requiredProgress = ownDepth > 3800f ? 35f : -40f;
            bool safeGeometry = Defense.IsTacticallyGoalSide(
                car.Location, ball.location, ownGoal, requiredProgress);
            return safeGeometry && Defense.EffectiveFreeTime(frame) >= -0.10f;
        }

        public static bool PreferGroundControl(
            TacticalFrame frame, bool canDribble, bool underPressure, bool forceFinishOpportunity)
        {
            if (frame == null || !canDribble || underPressure || forceFinishOpportunity)
                return false;
            return Defense.EffectiveFreeTime(frame) >= 0.22f;
        }

        public static bool CanAcquireAir(TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal)
        {
            if (frame == null || car == null || ball == null || frame.TeamRank != 0 ||
                car.IsDemolished || car.IsGrounded ||
                !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity) ||
                !ControlMath.Finite(ball.location) || !ControlMath.Finite(ball.velocity))
                return false;

            if (HasAirControl(car, ball))
                return true;

            // Acquisition is wider than established control. After a soft aerial touch the ball can
            // be slightly behind the nose while still being recoverable with boost; the old strict
            // HasAirControl envelope forced an immediate Recover and made AerialCarry effectively
            // disappear from real matches.
            Vec3 delta = ball.location - car.Location;
            Vec3 local = car.Local(delta);
            float distance = delta.Length();
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            if (car.Boost <= 8f || ball.location.z <= 240f ||
                distance > 720f || relativeSpeed > 1150f ||
                local.x < -430f || local.z < -180f)
                return false;

            float ownDepth = Defense.OwnDepth(ball.location, ownGoal);
            bool safeGeometry =
                Defense.IsTacticallyGoalSide(car.Location, ball.location, ownGoal, -180f) ||
                ownDepth < 0f; // opponent half: allow a controlled continuation slightly goal-ahead

            return safeGeometry && Defense.EffectiveFreeTime(frame) >= -0.12f;
        }

        /// <summary>
        /// Goal-directed lane with a bounded lateral cut away from the strongest defender blocking
        /// the next ~2200 uu. This creates useful cuts without turning possession into random weaving.
        /// </summary>
        public static Vec3 AttackingLane(Car car, Ball ball, IEnumerable<Car> opponents, Vec3 goal) =>
            AttackingLane(car, ball, opponents, goal, Vec3.Zero);

        public static Vec3 AttackingLane(Car car, Ball ball, IEnumerable<Car> opponents,
            Vec3 goal, Vec3 ownGoal)
        {
            Vec3 fallback = car?.Forward ?? Vec3.X;
            if (ball == null || !ControlMath.Finite(ball.location) || !ControlMath.Finite(goal))
                return ControlMath.FlatUnit(fallback, Vec3.X);

            Vec3 direct = ControlMath.FlatUnit(goal - ball.location, fallback);
            Vec3 right = new Vec3(-direct.y, direct.x, 0);
            float strongest = 0f;
            float evadeSign = 0f;

            if (opponents != null)
            {
                foreach (Car opponent in opponents)
                {
                    if (opponent == null || opponent.IsDemolished ||
                        !ControlMath.Finite(opponent.Location))
                        continue;

                    Vec3 rel = (opponent.Location - ball.location).Flatten();
                    float ahead = rel.Dot(direct);
                    float lateral = rel.Dot(right);
                    if (ahead < 80f || ahead > 2200f || MathF.Abs(lateral) > 1050f)
                        continue;

                    float strength =
                        (1f - System.Math.Clamp((ahead - 80f) / 2120f, 0f, 1f)) *
                        (1f - System.Math.Clamp(MathF.Abs(lateral) / 1050f, 0f, 1f));
                    if (strength <= strongest)
                        continue;

                    strongest = strength;
                    // Cut to the open side of the blocker.
                    evadeSign = lateral >= 0f ? -1f : 1f;
                }
            }

            Vec3 lane = direct;
            if (strongest > 0f)
                lane = ControlMath.FlatUnit(direct + right * evadeSign * (0.58f * strongest), direct);

            // Do not choose a cut that drives the dribble directly into a side wall.
            float projectedX = ball.location.x + lane.x * 1450f;
            float wall = Field.Width * 0.5f - 350f;
            if (MathF.Abs(projectedX) > wall)
            {
                float inward = projectedX > 0f ? -1f : 1f;
                lane = ControlMath.FlatUnit(lane + new Vec3(inward * 0.45f, 0, 0), direct);
            }

            // Deep in our own end, ordinary opponent evasion can accidentally turn into a lateral
            // dribble across the goal face. Bias strongly toward an inward/upfield escape corridor.
            if (ControlMath.Finite(ownGoal) && MathF.Abs(ownGoal.y) > 1000f)
            {
                float side = ownGoal.y < 0 ? -1f : 1f;
                float ownDepth = ball.location.y * side;
                float danger = System.Math.Clamp((ownDepth - 3300f) / 1450f, 0f, 1f);
                if (danger > 0f)
                {
                    Vec3 escape = ControlMath.FlatUnit(
                        new Vec3(-ball.location.x * 0.60f, -side * 1800f, 0f), direct);
                    lane = ControlMath.FlatUnit(lane * (1f - danger) + escape * (1.55f * danger), escape);
                }
            }

            return lane;
        }

        public static bool ShouldFlick(Car car, Ball ball, Vec3 lane,
            float pressureTime, float opponentDistance) =>
            ShouldFlick(car, ball, lane, pressureTime, opponentDistance, Vec3.Zero);

        public static bool ShouldFlick(Car car, Ball ball, Vec3 lane,
            float pressureTime, float opponentDistance, Vec3 ownGoal)
        {
            if (!HasControlledPossession(car, ball))
                return false;

            Vec3 flatLane = ControlMath.FlatUnit(lane, car.Forward);
            float speed = car.Velocity.Dot(car.Forward);
            float alignment = car.Forward.FlatNorm().Dot(flatLane);
            bool imminent = (float.IsFinite(pressureTime) && pressureTime < 0.85f) ||
                (float.IsFinite(opponentDistance) && opponentDistance < 720f);

            if (ControlMath.Finite(ownGoal) && MathF.Abs(ownGoal.y) > 1000f)
            {
                float side = ownGoal.y < 0 ? -1f : 1f;
                float ownDepth = ball.location.y * side;
                Vec3 fieldward = new Vec3(0f, -side, 0f);

                // Never flick from the back-wall/goal-line pocket. Carry the ball into a safe
                // upfield lane first; a lateral flick here can center the ball for the opponent.
                if (ownDepth > 4200f)
                    return false;

                if (ownDepth > 3400f &&
                    (flatLane.Dot(fieldward) < 0.70f ||
                     car.Forward.FlatNorm().Dot(fieldward) < 0.72f ||
                     car.Velocity.Dot(fieldward) < 450f))
                    return false;
            }

            return imminent && speed > 250f && alignment > 0.72f;
        }

        /// <summary>Predict RELATIVE motion: equal world velocity must not create a fictitious hood error.</summary>
        public static ControllerStateT GroundCarry(Car car, Ball ball, Vec3 lane)
        {
            lane = ControlMath.FlatUnit(lane, car.Forward);
            Vec3 relativeVelocity = car.Local(ball.velocity - car.Velocity);
            Vec3 local = car.Local(ball.location - car.Location);
            Vec3 hoodError = car.Local(ball.location - car.Location +
                (ball.velocity - car.Velocity) * 0.10f - lane * 38);
            float lateral = System.Math.Clamp(hoodError.y * 4.5f + relativeVelocity.y * 0.7f, -650, 650);
            Vec3 heading = lane * 350 + car.Right * lateral;
            float speed = System.Math.Clamp(ball.velocity.Dot(car.Forward) + hoodError.x * 3.8f, -350, 1550);
            speed *= 1 - 0.35f * MathF.Min(1, MathF.Abs(local.y) / 160);
            var controls = new ControllerStateT();
            ControlMath.Ground(car, controls, heading, speed);
            return controls;
        }

        /// <summary>
        /// Both objects are advanced to the same look-ahead time before computing flight error.
        /// The car prediction already includes gravity, so this is a residual correction at the
        /// future state and must not add gravity feed-forward a second time.
        /// </summary>
        public static Vec3 FlightAtHorizon(Car car, Vec3 targetPosition, Vec3 targetVelocity, float horizon)
        {
            if (car == null || !float.IsFinite(horizon) || horizon < 0f ||
                !ControlMath.Finite(targetPosition) || !ControlMath.Finite(targetVelocity))
                return Vec3.Zero;

            return ControlMath.FlightAcceleration(
                car.PredictLocation(horizon),
                car.PredictVelocity(horizon),
                targetPosition,
                targetVelocity,
                Vec3.Zero);
        }
    }
}
