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
        /// How securely the ball is balanced on the roof, 0 to 1. A ball off the roof footprint, well
        /// above or below its resting height, or moving fast relative to the car scores zero;
        /// otherwise centring, height and matched motion multiply, so no one of them can make up for
        /// a ball that is not on the car. A nearby opponent does not change this: pressure calls for
        /// an outplay, not a retreat.
        /// </summary>
        public static float RoofControlQuality(Car car, Ball ball)
        {
            if (car == null || ball == null || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(car.Velocity) || !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ball.velocity))
                return 0f;

            Vec3 local = car.Local(ball.location - car.Location);
            Vec3 relative = car.Local(ball.velocity - car.Velocity);
            float rest = GroundCatch.RoofRest(car);
            float planar = relative.Flatten().Length();
            if (MathF.Abs(local.x - RoofCentre) > 75f || MathF.Abs(local.y) > 55f ||
                local.z < rest - 15f || local.z > rest + 70f || MathF.Abs(relative.z) > 300f || planar > 400f)
                return 0f;

            float offCentre = MathF.Sqrt(MathF.Pow((local.x - RoofCentre) / 75f, 2) + MathF.Pow(local.y / 55f, 2));
            float centred = 1f - System.Math.Clamp(offCentre, 0f, 1f);
            float height = 1f - System.Math.Clamp(MathF.Abs(local.z - rest - 5f) / 60f, 0f, 1f);
            float matched = 1f - System.Math.Clamp(planar / 400f, 0f, 1f);
            return System.Math.Clamp(centred * (0.55f + 0.25f * height + 0.2f * matched), 0f, 1f);
        }

        /// <summary>Forward position (car frame) of the middle of the carry's working area on the roof.</summary>
        private const float RoofCentre = 10f;

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

            if (HasControlledPossession(car, ball))
                return true;
            if (HasAirControl(car, ball) && (car.Boost > 0f || car.Location.Dist(ball.location) < 230f))
                return true;

            if (frame.TeamRank != 0 || !Defense.IsGoalSide(car.Location, ball.location, ownGoal, -80f))
                return false;

            Vec3 local = car.Local(ball.location - car.Location);
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            bool closeControl = ball.location.z < 360f && local.x > -140f && local.x < 560f &&
                MathF.Abs(local.y) < 240f && local.Length() < 620f && relativeSpeed < 1050f;

            return closeControl && frame.FreeTime >= -0.16f;
        }

        /// <summary>Ball depth (uu into our half) beyond which no new ground catch or carry is started.</summary>
        public static float OwnHalfCatchDepth = float.PositiveInfinity;

        public static bool CanAcquireGround(TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal)
        {
            if (frame == null || car == null || ball == null || frame.TeamRank != 0 ||
                car.IsDemolished || !car.IsGrounded)
                return false;

            if (HasControlledPossession(car, ball))
                return true;

            float side = ownGoal.y < 0 ? -1f : 1f;
            float ownDepth = ball.location.y * side;
            // Deep in our own half a catch keeps the ball where a lost touch is a chance on our goal.
            if (ownDepth > OwnHalfCatchDepth)
                return false;
            float requiredProgress = ownDepth > 3800f ? 35f : -40f;
            bool safeGeometry = Defense.IsGoalSide(
                car.Location, ball.location, ownGoal, requiredProgress);
            return safeGeometry && frame.FreeTime >= -0.10f;
        }

        public static bool CanAcquireAir(TacticalFrame frame, Car car, Ball ball, Vec3 ownGoal)
        {
            if (frame == null || car == null || ball == null || frame.TeamRank != 0 || car.IsDemolished)
                return false;

            if (HasAirControl(car, ball))
                return true;

            return Defense.IsGoalSide(car.Location, ball.location, ownGoal, -100f) &&
                frame.FreeTime >= -0.08f;
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

        /// <summary>How an opponent is coming at the ball.</summary>
        public readonly struct Challenge
        {
            public readonly Car Opponent;
            /// <summary>Seconds until the opponent reaches the ball at its current closing speed (infinite when not closing).</summary>
            public readonly float Contact;
            /// <summary>Opponent position relative to the ball along the lane (positive: in front) and across it.</summary>
            public readonly float Ahead, Across;
            public readonly bool Airborne;

            public Challenge(Car opponent, float contact, float ahead, float across, bool airborne)
            {
                Opponent = opponent; Contact = contact; Ahead = ahead; Across = across; Airborne = airborne;
            }

            /// <summary>Coming from in front and fast: committed to the ball, not shadowing it.</summary>
            public bool Committed => Opponent != null && Ahead > 0f && Contact < 1.2f;
        }

        /// <summary>The opponent that will reach the ball first at the speed it is closing.</summary>
        public static Challenge MostImminent(Ball ball, Vec3 lane, IEnumerable<Car> opponents)
        {
            Challenge best = new(null, float.PositiveInfinity, 0f, 0f, false);
            if (opponents == null) return best;
            Vec3 flatLane = ControlMath.FlatUnit(lane, Vec3.X);
            Vec3 right = new(-flatLane.y, flatLane.x, 0f);
            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished || !ControlMath.Finite(opponent.Location)) continue;
                Vec3 offset = (opponent.Location - ball.location).Flatten();
                float distance = offset.Length();
                if (distance < 1f) continue;
                Vec3 toBall = offset / -distance;
                float closing = (opponent.Velocity - ball.velocity).Flatten().Dot(toBall);
                // Within reach of a car's front and roof counts as contact already.
                float contact = closing > 150f ? MathF.Max(0f, distance - 180f) / closing : float.PositiveInfinity;
                if (contact < best.Contact)
                    best = new Challenge(opponent, contact, offset.Dot(flatLane), offset.Dot(right), !opponent.IsGrounded);
            }
            return best;
        }

        /// <summary>
        /// Which flick to make from a controlled carry, or null to keep carrying. Pros flick when a
        /// challenger commits (a lob over it in the last half second) or when a hard flick is a shot
        /// (close enough with the lane to goal open). Against a defender who hangs back, or a chaser
        /// from behind, the carry goes on: a flick only hands the ball over. Never from the pocket in
        /// front of the own goal.
        /// </summary>
        public static FlickKind? PlanFlick(Car car, Ball ball, Vec3 lane, IEnumerable<Car> opponents,
            Vec3 ownGoal, Vec3 theirGoal, out Vec3 aim)
        {
            aim = ControlMath.FlatUnit(lane, car.Forward);
            if (!HasControlledPossession(car, ball) || car.Velocity.Dot(car.Forward) < 250f || !Safe(car, ball, aim, ownGoal))
                return null;

            Challenge challenge = MostImminent(ball, aim, opponents);
            if (challenge.Committed && challenge.Contact < ChallengeWindow)
            {
                // From 500-700 uu the power flick's 20° climb clears the challenger's roof; closer in, lob.
                return challenge.Contact > 0.25f ? FlickKind.Power : FlickKind.Lob;
            }

            Vec3 toGoal = (theirGoal - ball.location).Flatten();
            if (toGoal.Length() < ShotRange)
            {
                Vec3 shot = toGoal.Normalize();
                if (LaneOpen(ball.location, theirGoal, opponents, challenge))
                {
                    aim = shot;
                    return FlickKind.Power;
                }
            }
            return null;
        }

        /// <summary>
        /// Whether to take a carry into the air: settled, with boost, nobody within 1.5 s of the ball,
        /// and 2500-6000 uu from goal: far enough to have room to fly, near enough that the air dribble
        /// ends in a shot.
        /// </summary>
        public static bool ShouldAirDribble(Car car, Ball ball, Vec3 lane, IEnumerable<Car> opponents, Vec3 theirGoal)
        {
            if (!AirDribbleSetup.CanStart(car, ball)) return false;
            float goalDistance = ball.location.FlatDist(theirGoal);
            return goalDistance is > AirDribbleMinRange and < AirDribbleMaxRange &&
                MostImminent(ball, lane, opponents).Contact > AirDribbleClearance;
        }

        public const float AirDribbleMinRange = 2500f, AirDribbleMaxRange = 6000f, AirDribbleClearance = 1.5f;

        /// <summary>Time to a committed challenger's contact inside which the flick goes (lob apex clears a car at 600 uu).</summary>
        public const float ChallengeWindow = 0.45f;
        /// <summary>Distance to the opponent goal inside which a power flick is a shot.</summary>
        public const float ShotRange = 3000f;

        /// <summary>No defender near the ball's line to goal who could get there before a 2200 uu/s flick.</summary>
        private static bool LaneOpen(Vec3 from, Vec3 goal, IEnumerable<Car> opponents, Challenge imminent)
        {
            if (opponents == null) return true;
            Vec3 line = (goal - from).Flatten();
            float length = line.Length();
            Vec3 direction = line / MathF.Max(length, 1f);
            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished) continue;
                Vec3 offset = (opponent.Location - from).Flatten();
                float along = System.Math.Clamp(offset.Dot(direction), 0f, length);
                float miss = (offset - direction * along).Length();
                float flight = along / 2200f;
                // A defender reaching the line within the ball's flight time (at ~1400 uu/s plus a car's reach) blocks it.
                if (miss < 180f + 1400f * flight) return false;
            }
            return true;
        }

        /// <summary>Never flick from the back-wall pocket, nor across our own box.</summary>
        private static bool Safe(Car car, Ball ball, Vec3 lane, Vec3 ownGoal)
        {
            if (!ControlMath.Finite(ownGoal) || MathF.Abs(ownGoal.y) < 1000f) return true;
            float side = ownGoal.y < 0 ? -1f : 1f;
            float ownDepth = ball.location.y * side;
            Vec3 fieldward = new(0f, -side, 0f);
            if (ownDepth > 4200f) return false;
            return ownDepth <= 3400f || (lane.Dot(fieldward) >= 0.70f &&
                car.Forward.FlatNorm().Dot(fieldward) >= 0.72f && car.Velocity.Dot(fieldward) >= 450f);
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
