using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public sealed class TacticalFrame
    {
        public float MyEta, OpponentEta = 6, TeammateEta = 6;
        public float PressureTime = float.PositiveInfinity;
        public int FirstMan, TeamRank, TeamCount = 1;
        public bool LastBack, HasCover;
        public float FreeTime => OpponentEta - MyEta;
        public bool UnderPressure => float.IsFinite(PressureTime);
    }

    public static class Tactics
    {
        public static float GoalThreat(BallSlice[] slices, Vec3 goal, float now, float horizon = 2.5f) =>
            Defense.GoalThreat(slices, goal, now, horizon, out _);

        /// <summary>Fixed ETA buckets give all observers the same transitive race ownership order.</summary>
        public static bool WinsTie(float eta, int index, float otherEta, int otherIndex, float margin = 0.08f)
        {
            float bucketSize = float.IsFinite(margin) ? MathF.Max(margin, 0.001f) : 0.08f;
            long bucket = float.IsFinite(eta) ? (long)MathF.Floor(MathF.Max(0, eta) / bucketSize) : long.MaxValue;
            long otherBucket = float.IsFinite(otherEta) ? (long)MathF.Floor(MathF.Max(0, otherEta) / bucketSize) : long.MaxValue;
            return bucket != otherBucket ? bucket < otherBucket : index < otherIndex;
        }

        public static bool KickoffBefore(Car a, Car b, Vec3 ball, int team)
        {
            float da = a.Location.Dist(ball), db = b.Location.Dist(ball);
            if (MathF.Abs(da - db) > 20) return da < db;
            float leftA = -Field.Side(team) * a.Location.x, leftB = -Field.Side(team) * b.Location.x;
            if (MathF.Abs(leftA - leftB) > 1) return leftA > leftB;
            return a.Index < b.Index;
        }

        public static float GroundEta(Car car)
        {
            if (car == null || car.IsDemolished || !ControlMath.Finite(car.Location)) return 6;
            if (car.Location.Dist(Ball.Location) < 180 && (car.Velocity - Ball.Velocity).Length() < 600) return 0.05f;
            float next = Game.Time + 0.1f;
            BallSlice[] slices = Ball.Prediction.Slices;
            if (slices != null)
                foreach (BallSlice slice in slices)
                {
                    if (slice == null || !float.IsFinite(slice.Time) || !ControlMath.Finite(slice.Location) || slice.Time < next) continue;
                    float t = slice.Time - Game.Time;
                    if (t > 3.5f) break;
                    next = slice.Time + 0.15f;
                    if (slice.Location.z > 300) continue;
                    float eta = Drive.GetEta(car, slice.Location);
                    if (float.IsFinite(eta) && eta <= t) return t;
                }
            float fallback = Drive.GetEta(car, Ball.Location);
            return float.IsFinite(fallback) ? System.Math.Clamp(fallback, 0.05f, 6) : 6;
        }

        public static TacticalFrame Evaluate(RUBot bot)
        {
            var result = new TacticalFrame { MyEta = GroundEta(bot.Me), FirstMan = bot.Index, LastBack = true };
            float best = result.MyEta;
            int side = Field.Side(bot.Team);
            foreach (Car car in Cars.AllLivingCars)
            {
                if (car.Index == bot.Index) continue;
                float eta = GroundEta(car);
                if (car.Team != bot.Team)
                {
                    result.OpponentEta = MathF.Min(result.OpponentEta, eta);
                    continue;
                }
                result.TeamCount++;
                result.TeammateEta = MathF.Min(result.TeammateEta, eta);
                result.HasCover |= Defense.CoversGoal(car, Ball.Location, bot.OurGoal.Location);
                // Stable depth buckets avoid assigning two last-back cars at nearly identical depths.
                int otherDepth = (int)MathF.Floor(car.Location.y * side / 100);
                int myDepth = (int)MathF.Floor(bot.Me.Location.y * side / 100);
                if (otherDepth > myDepth || (otherDepth == myDepth && car.Index < bot.Index)) result.LastBack = false;
                if (WinsTie(eta, car.Index, result.MyEta, bot.Index)) result.TeamRank++;
                if (WinsTie(eta, car.Index, best, result.FirstMan))
                {
                    best = eta;
                    result.FirstMan = car.Index;
                }
            }
            return result;
        }

        public static Vec3 ShadowTarget(Vec3 ball, Vec3 ownGoal, bool lastBack) =>
            Defense.ShadowTarget(ball, ownGoal, lastBack);

        /// <summary>
        /// Pre-contact pressure complements ball-only prediction. Close control counts even when
        /// a dribbler coasts or stops; absence of a throttle input is not absence of a threat.
        /// </summary>
        public static float OpponentPressure(IEnumerable<Car> opponents, Ball ball, Vec3 ownGoal,
            float maxContactTime = 1.35f)
        {
            if (opponents == null || ball == null || !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ball.velocity) || !ControlMath.Finite(ownGoal)) return float.PositiveInfinity;
            float earliest = float.PositiveInfinity;
            Vec3 attackDirection = ControlMath.FlatUnit(ownGoal - ball.location,
                new Vec3(0, ownGoal.y < 0 ? -1 : 1, 0));
            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished || !ControlMath.Finite(opponent.Location) ||
                    !ControlMath.Finite(opponent.Velocity)) continue;
                Vec3 toBall = ControlMath.FlatUnit(ball.location - opponent.Location, opponent.Forward);
                float attackAlignment = toBall.Dot(attackDirection);
                float facing = opponent.Forward.FlatNorm().Dot(toBall);
                float closing = (opponent.Velocity - ball.velocity).Dot(toBall);
                bool closeControl = opponent.Location.Dist(ball.location) < 300 &&
                    attackAlignment > -0.1f && facing > 0.1f && closing > -250;
                if (closeControl) { earliest = MathF.Min(earliest, 0.10f); continue; }
                if (attackAlignment < 0.35f) continue;
                var input = opponent.LastInput ?? new RLBot.Flat.ControllerStateT();
                bool forwardIntent = input.Boost || input.Throttle > 0.2f || closing > 450;
                if (!forwardIntent || (facing < 0.4f && closing < 500)) continue;
                float eta = Drive.GetEta(opponent, ball.location);
                if (!opponent.IsGrounded && closing > 100)
                    eta = MathF.Min(eta, opponent.Location.Dist(ball.location) / closing);
                if (float.IsFinite(eta) && eta >= 0 && eta <= maxContactTime) earliest = MathF.Min(earliest, eta);
            }
            return earliest;
        }

        public static float GuardSpeed(Car car, Vec3 target, float cruiseSpeed) =>
            Defense.GuardSpeed(car, target, cruiseSpeed);

        /// <summary>Exit a deep net through the mouth, without ejecting a correctly placed shallow guard.</summary>
        public static Vec3 GoalReturnTarget(Car car, Vec3 desiredGuard, Vec3 ownGoal)
        {
            if (car == null || !ControlMath.Finite(desiredGuard) || !ControlMath.Finite(ownGoal)) return desiredGuard;
            float side = ownGoal.y < 0 ? -1 : 1, line = MathF.Abs(ownGoal.y);
            bool behindLine = car.Location.y * side > line + 40;
            bool insideMouth = MathF.Abs(car.Location.x - ownGoal.x) < Goal.Width / 2 + 220;
            bool shallowGuard = desiredGuard.y * side >= line - 100 && car.Location.y * side <= line + 260;
            if (!behindLine || !insideMouth || shallowGuard) return desiredGuard;
            float safeHalfWidth = Goal.Width / 2 - 160;
            float x = System.Math.Clamp(desiredGuard.x, ownGoal.x - safeHalfWidth, ownGoal.x + safeHalfWidth);
            return new Vec3(x, side * (line - 300), 17);
        }

        /// <summary>Bounded search with a hard contact deadline, including emergency goal crossings.</summary>
        public static Shot SelectShot(RUBot bot, bool emergency, float opponentEta, Func<float, bool> claimed,
            float maxContactTime = 3)
        {
            BallSlice[] slices = Ball.Prediction.Slices;
            if (slices == null || slices.Length == 0 || !float.IsFinite(maxContactTime) || maxContactTime <= 0) return null;
            Target target = new Target(emergency ? bot.OurGoal : bot.TheirGoal, emergency);
            float next = Game.Time + 0.08f, bestScore = float.NegativeInfinity;
            int evaluated = 0;
            Shot best = null;
            foreach (BallSlice slice in slices)
            {
                if (slice == null || !float.IsFinite(slice.Time) || !ControlMath.Finite(slice.Location) ||
                    !ControlMath.Finite(slice.Velocity) || slice.Time < next) continue;
                float t = slice.Time - Game.Time;
                if (t > MathF.Min(3, maxContactTime) || evaluated >= 48) break;
                next = slice.Time + 0.06f;
                evaluated++;
                if (!target.Fits(slice.Location) || (!emergency && claimed(slice.Time))) continue;
                if (!emergency && opponentEta < 1.5f && t > opponentEta + 0.35f) continue;
                Ball after = slice.ToBall();
                Vec3 approach = (slice.Location - bot.Me.Location) / t;
                after.velocity = approach.Cap(0, Car.MaxSpeed) + slice.Velocity * 0.25f;
                Vec3 destination = target.Clamp(after);
                if (!ControlMath.Finite(destination)) continue;
                Shot candidate = new GroundShot(bot.Me, slice, destination);
                float cost = 0;
                if (!candidate.IsValid(bot.Me)) { candidate = new JumpShot(bot.Me, slice, destination); cost = 0.15f; }
                if (!candidate.IsValid(bot.Me)) { candidate = new DoubleJumpShot(bot.Me, slice, destination); cost = 0.4f; }
                if (!candidate.IsValid(bot.Me)) { candidate = new AerialShot(bot.Me, slice, destination); cost = 0.8f; }
                if (!candidate.IsValid(bot.Me)) continue;
                float score = -t - cost - MathF.Max(0, t - opponentEta) * (emergency ? 0 : 2);
                if (score > bestScore) { best = candidate; bestScore = score; }
                if (best != null && t > best.Slice.Time - Game.Time + 0.3f) break;
            }
            return best;
        }
    }
}
