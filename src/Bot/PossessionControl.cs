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
            float contactWindow = Defense.OpponentContactEta(frame);
            float roofQuality = RoofControlQuality(car, ball);

            // PR3's important invariant was that an opponent merely being "under pressure" did not
            // erase real possession. Later deep-box gates turned a 0.8-1.2 s contest clock into an
            // automatic boom. Keep established control unless the next touch is genuinely immediate.
            if (roofQuality >= 0.48f)
            {
                bool goalMouthPocket = ownDepth > 4550f;
                if (goalMouthPocket && float.IsFinite(contactWindow) &&
                    contactWindow < 0.22f && roofQuality < 0.70f)
                    return false;
                return true;
            }

            if (HasAirControl(car, ball) &&
                (car.Boost > 0f || car.Location.Dist(ball.location) < 230f))
            {
                return !(ownDepth > 4400f &&
                    float.IsFinite(contactWindow) && contactWindow < 0.28f);
            }

            if (frame.TeamRank != 0 || Defense.NeedsGoalExit(car, ownGoal) ||
                !Defense.IsTacticallyGoalSide(car.Location, ball.location, ownGoal, -140f))
                return false;

            Vec3 local = car.Local(ball.location - car.Location);
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            bool closeControl = ball.location.z < 365f &&
                local.x > -165f && local.x < 610f &&
                MathF.Abs(local.y) < 260f && local.Length() < 670f &&
                relativeSpeed < 1200f;

            if (!closeControl)
                return false;

            if (float.IsFinite(contactWindow) && contactWindow < 0.14f)
                return false;

            return Defense.EffectiveFreeTime(frame) >= -0.22f;
        }

        /// <summary>
        /// A catch-to-dribble handoff needs a brief settling window before roof-control quality can
        /// become high. This is not unconditional stickiness: an immediate opponent touch, wrong-side
        /// geometry, or a runaway ball still cancels the setup.
        /// </summary>
        public static bool CanRetainGroundSetup(
            TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal, float setupAge)
        {
            if (frame == null || car == null || ball == null ||
                frame.TeamRank != 0 || car.IsDemolished || !car.IsGrounded ||
                !float.IsFinite(setupAge) || setupAge < 0f || setupAge > 0.48f ||
                Defense.NeedsGoalExit(car, ownGoal))
                return false;

            if (HasControlledPossession(car, ball))
                return true;

            float distance = car.Location.Dist(ball.location);
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            if (distance > 690f || ball.location.z > 365f || relativeSpeed > 1350f)
                return false;

            float ownDepth = Defense.OwnDepth(ball.location, ownGoal);
            float requiredProgress = ownDepth > 4300f ? -70f : -180f;
            if (!Defense.IsTacticallyGoalSide(
                    car.Location, ball.location, ownGoal, requiredProgress))
                return false;

            float contactWindow = Defense.OpponentContactEta(frame);
            if (float.IsFinite(contactWindow) &&
                contactWindow < (ownDepth > 4400f ? 0.28f : 0.14f))
                return false;

            return Defense.EffectiveFreeTime(frame) >= -0.22f;
        }

        /// <summary>
        /// Decide whether a deep defensive state is safe enough to CONTROL rather than automatically
        /// boom. The emergency goal-threat branch still runs before this gate; this only distinguishes
        /// a real possession window from generic "own box = clear" logic.
        /// </summary>
        public static bool CanPreferDefensiveControl(
            TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal,
            bool catchAvailable, bool dribbleReady)
        {
            if (frame == null || car == null || ball == null ||
                frame.TeamRank != 0 || car.IsDemolished || !car.IsGrounded ||
                Defense.NeedsGoalExit(car, ownGoal) ||
                !ControlMath.Finite(ball.location) || !ControlMath.Finite(ball.velocity))
                return false;

            float ownDepth = Defense.OwnDepth(ball.location, ownGoal);
            if (ownDepth < 3300f)
                return catchAvailable || dribbleReady || HasControlledPossession(car, ball);

            if (!Defense.IsTacticallyGoalSide(
                    car.Location, ball.location, ownGoal, -100f))
                return false;

            float side = ownGoal.y < 0f ? -1f : 1f;
            Vec3 fieldward = new(0f, -side, 0f);
            float towardOwnGoal = ball.velocity.Dot(-fieldward);
            if (ownDepth > 4300f && towardOwnGoal > 650f)
                return false;

            float contactWindow = Defense.OpponentContactEta(frame);
            float effectiveFree = Defense.EffectiveFreeTime(frame);
            float roofQuality = RoofControlQuality(car, ball);

            if (roofQuality >= 0.52f)
            {
                if (ownDepth > 4700f && float.IsFinite(contactWindow) &&
                    contactWindow < 0.24f)
                    return false;
                return true;
            }

            if (catchAvailable)
            {
                float required = ownDepth > 4550f ? 0.58f : 0.42f;
                return (!float.IsFinite(contactWindow) || contactWindow >= required) &&
                    effectiveFree >= -0.02f;
            }

            if (dribbleReady)
            {
                float required = ownDepth > 4550f ? 0.42f : 0.30f;
                return (!float.IsFinite(contactWindow) || contactWindow >= required) &&
                    effectiveFree >= -0.08f;
            }

            return false;
        }

        public static bool CanAcquireGround(TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal)
        {
            if (frame == null || car == null || ball == null || frame.TeamRank != 0 ||
                car.IsDemolished || !car.IsGrounded || Defense.NeedsGoalExit(car, ownGoal))
                return false;

            if (HasControlledPossession(car, ball))
                return true;

            float side = ownGoal.y < 0 ? -1f : 1f;
            float ownDepth = ball.location.y * side;
            float requiredProgress = ownDepth > 4200f ? 15f : -90f;
            bool safeGeometry = Defense.IsTacticallyGoalSide(
                car.Location, ball.location, ownGoal, requiredProgress);
            if (!safeGeometry)
                return false;

            float contactWindow = Defense.OpponentContactEta(frame);
            if (ownDepth > 4300f && float.IsFinite(contactWindow) && contactWindow < 0.20f)
                return false;

            float freeFloor = ownDepth > 3900f ? -0.05f : -0.18f;
            return Defense.EffectiveFreeTime(frame) >= freeFloor;
        }

        public static bool PreferGroundControl(
            TacticalFrame frame, bool canDribble, bool underPressure, bool forceFinishOpportunity)
        {
            if (frame == null || !canDribble || forceFinishOpportunity)
                return false;

            float effectiveFree = Defense.EffectiveFreeTime(frame);
            if (!underPressure)
                return effectiveFree >= 0.16f;

            // Pressure is not synonymous with "hit the ball away." If we still own a meaningful
            // contact window, starting control creates the flick/cut threat that PR3 used well.
            float contactWindow = Defense.OpponentContactEta(frame);
            return effectiveFree >= 0.05f &&
                (!float.IsFinite(contactWindow) || contactWindow >= 0.45f);
        }

        public static bool CanHandoffShotToAirCarry(
            Car car, Ball ball, float opponentWindow)
        {
            if (car == null || ball == null || car.IsGrounded ||
                car.Boost <= 4f || !float.IsFinite(opponentWindow) ||
                opponentWindow <= 0.35f)
                return false;

            // Handoff is intentionally wider than a cold AerialCarry start. A JumpShot has already
            // spent the setup cost and may have created genuine air control only 50-150 uu off the
            // floor; requiring the cold-start height/boost gates made every logged handoff remain
            // false even when HasAirControl was true.
            return HasAirControl(car, ball);
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

                    Vec3 predictedOpponent =
                        opponent.Location + opponent.Velocity * 0.18f;
                    if (!ControlMath.Finite(predictedOpponent))
                        predictedOpponent = opponent.Location;

                    Vec3 rel = (predictedOpponent - ball.location).Flatten();
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
            {
                // Close predicted blocks deserve a decisive cut; distant blocks only bend the lane.
                float cut = 0.58f + 0.18f * strongest;
                lane = ControlMath.FlatUnit(
                    direct + right * evadeSign * (cut * strongest), direct);
            }

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
