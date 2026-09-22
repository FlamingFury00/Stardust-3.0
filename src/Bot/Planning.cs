using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public sealed class TacticalFrame
    {
        public float MyEta, OpponentEta = 6f, TeammateEta = 6f;
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
            long bucket = float.IsFinite(eta)
                ? (long)MathF.Floor(MathF.Max(0f, eta) / bucketSize)
                : long.MaxValue;
            long otherBucket = float.IsFinite(otherEta)
                ? (long)MathF.Floor(MathF.Max(0f, otherEta) / bucketSize)
                : long.MaxValue;
            return bucket != otherBucket ? bucket < otherBucket : index < otherIndex;
        }

        public static bool KickoffBefore(Car a, Car b, Vec3 ball, int team)
        {
            float da = a.Location.Dist(ball), db = b.Location.Dist(ball);
            if (MathF.Abs(da - db) > 20f)
                return da < db;

            float leftA = -Field.Side(team) * a.Location.x;
            float leftB = -Field.Side(team) * b.Location.x;
            if (MathF.Abs(leftA - leftB) > 1f)
                return leftA > leftB;
            return a.Index < b.Index;
        }

        /// <summary>
        /// Earliest plausible low-ball ground intercept. This is a race estimate, not a proof that a
        /// touch is strategically safe; the latter is handled separately by Defense.CanChallenge.
        /// </summary>
        public static float GroundEta(Car car)
        {
            if (car == null || car.IsDemolished || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(car.Velocity))
                return 6f;

            if (car.Location.Dist(Ball.Location) < 180f &&
                (car.Velocity - Ball.Velocity).Length() < 600f)
                return 0.05f;

            float next = Game.Time + 0.1f;
            BallSlice[] slices = Ball.Prediction.Slices;
            if (slices != null)
            {
                foreach (BallSlice slice in slices)
                {
                    if (slice == null || !float.IsFinite(slice.Time) ||
                        !ControlMath.Finite(slice.Location) || slice.Time < next)
                        continue;

                    float t = slice.Time - Game.Time;
                    if (t > 3.5f)
                        break;

                    next = slice.Time + 0.15f;
                    if (slice.Location.z > 300f)
                        continue;

                    float eta = Drive.GetEta(car, slice.Location);
                    if (float.IsFinite(eta) && eta <= t)
                        return t;
                }
            }

            float fallback = Drive.GetEta(car, Ball.Location);
            return float.IsFinite(fallback)
                ? System.Math.Clamp(fallback, 0.05f, 6f)
                : 6f;
        }

        public static TacticalFrame Evaluate(RUBot bot)
        {
            var result = new TacticalFrame
            {
                MyEta = GroundEta(bot.Me),
                FirstMan = bot.Index,
                LastBack = true
            };

            float best = result.MyEta;
            int side = Field.Side(bot.Team);
            int myDepth = (int)MathF.Floor(bot.Me.Location.y * side / 100f);

            foreach (Car car in Cars.AllLivingCars)
            {
                if (car == null || car.Index == bot.Index)
                    continue;

                float eta = GroundEta(car);
                if (car.Team != bot.Team)
                {
                    result.OpponentEta = MathF.Min(result.OpponentEta, eta);
                    continue;
                }

                result.TeamCount++;
                result.TeammateEta = MathF.Min(result.TeammateEta, eta);
                result.HasCover |= Defense.CoversGoal(car, Ball.Location, bot.OurGoal.Location);

                // Stable depth buckets prevent cars at effectively identical depth from both deciding
                // that the other car is last back.
                int otherDepth = (int)MathF.Floor(car.Location.y * side / 100f);
                if (otherDepth > myDepth || (otherDepth == myDepth && car.Index < bot.Index))
                    result.LastBack = false;

                if (WinsTie(eta, car.Index, result.MyEta, bot.Index))
                    result.TeamRank++;

                if (WinsTie(eta, car.Index, best, result.FirstMan))
                {
                    best = eta;
                    result.FirstMan = car.Index;
                }
            }

            return result;
        }

        /// <summary>Compatibility overload retained for existing callers and tests.</summary>
        public static Vec3 ShadowTarget(Vec3 ball, Vec3 ownGoal, bool lastBack) =>
            Defense.ShadowTarget(ball, ownGoal,
                lastBack ? DefensiveRole.Anchor : DefensiveRole.Support);

        /// <summary>
        /// Short-horizon pre-contact threat estimate. RLBot's ball prediction cannot predict the
        /// next player touch, so controlled/coasting dribblers and committed approaches are modeled
        /// independently. The resulting time adjusts urgency; it never becomes the position target.
        /// </summary>
        public static float OpponentPressure(IEnumerable<Car> opponents, Ball ball, Vec3 ownGoal,
            float maxContactTime = 1.35f)
        {
            if (opponents == null || ball == null || !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ball.velocity) || !ControlMath.Finite(ownGoal))
                return float.PositiveInfinity;

            float earliest = float.PositiveInfinity;
            Vec3 attackDirection = ControlMath.FlatUnit(ownGoal - ball.location,
                new Vec3(0, ownGoal.y < 0 ? -1 : 1, 0));

            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished || !ControlMath.Finite(opponent.Location) ||
                    !ControlMath.Finite(opponent.Velocity) || !ControlMath.Finite(opponent.Forward))
                    continue;

                Vec3 toBall = ControlMath.FlatUnit(ball.location - opponent.Location, opponent.Forward);
                float distance = opponent.Location.Dist(ball.location);
                float attackAlignment = toBall.Dot(attackDirection);
                float facing = opponent.Forward.FlatNorm().Dot(toBall);
                float closing = (opponent.Velocity - ball.velocity).Dot(toBall);

                // A dribbler can threaten without throttle input. Close physical control and reasonable
                // orientation are enough to force the defender to respect a near-future touch.
                bool closeControl = distance < 360f &&
                    attackAlignment > -0.18f && facing > 0.05f && closing > -320f;
                if (closeControl)
                {
                    earliest = MathF.Min(earliest, 0.08f + MathF.Max(0f, distance - 180f) / 2300f);
                    continue;
                }

                if (attackAlignment < 0.30f)
                    continue;

                var input = opponent.LastInput ?? new RLBot.Flat.ControllerStateT();
                bool forwardIntent = input.Boost || input.Throttle > 0.18f || closing > 420f;
                if (!forwardIntent || (facing < 0.35f && closing < 520f))
                    continue;

                float eta = Drive.GetEta(opponent, ball.location);
                if (!opponent.IsGrounded && closing > 100f)
                    eta = MathF.Min(eta, distance / closing);

                if (float.IsFinite(eta) && eta >= 0f && eta <= maxContactTime)
                    earliest = MathF.Min(earliest, eta);
            }

            return earliest;
        }

        /// <summary>
        /// Short predicted contact waypoint used when a pressured first man has no valid scripted
        /// shot. Approach from the attacking side of the ball rather than dropping back to shadow.
        /// </summary>
        public static Vec3 PressureChallengeTarget(Car car, BallPrediction prediction, Ball ball,
            Vec3 attackGoal, float now, float eta)
        {
            if (car == null || ball == null || !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(attackGoal))
                return ball?.location ?? Vec3.Zero;

            float horizon = float.IsFinite(eta)
                ? System.Math.Clamp(eta * 0.45f, 0.08f, 0.32f)
                : 0.16f;
            Ball contact = prediction.TrySample(now + horizon, out Ball sample)
                ? sample
                : ball.Predict(horizon);

            Vec3 lane = ControlMath.FlatUnit(attackGoal - contact.location, car.Forward);
            Vec3 target = contact.location - lane * 70f;
            return Field.LimitToNearestSurface(target);
        }

        /// <summary>
        /// Geometric open-net check for a direct ball-to-goal lane. It intentionally ignores opponents
        /// behind the ball and expands the blocking corridor toward the goal mouth.
        /// </summary>
        public static bool GoalLaneOpen(IEnumerable<Car> opponents, Vec3 ball, Vec3 goal)
        {
            if (!ControlMath.Finite(ball) || !ControlMath.Finite(goal))
                return false;

            Vec3 axis = ControlMath.FlatUnit(goal - ball, new Vec3(0, goal.y < ball.y ? -1f : 1f, 0));
            float length = ball.FlatDist(goal);
            if (length < 1f)
                return true;

            if (opponents == null)
                return true;

            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished ||
                    !ControlMath.Finite(opponent.Location) || opponent.Location.z > 520f)
                    continue;

                Vec3 rel = (opponent.Location - ball).Flatten();
                float along = rel.Dot(axis);
                if (along < 80f || along > length + 250f)
                    continue;

                float fraction = System.Math.Clamp(along / length, 0f, 1f);
                float halfWidth = 430f + fraction * (Goal.Width * 0.5f - 120f);
                Vec3 lateral = rel - axis * along;
                if (lateral.Length() <= halfWidth)
                    return false;
            }

            return true;
        }

        public static bool CanSearchAttack(
            TacticalFrame frame, Car car, Ball ball, Vec3 attackGoal,
            bool canChallenge, bool controlledPossession)
        {
            if (canChallenge || controlledPossession)
                return true;
            if (CanForceFinishOpportunity(frame, car, ball, attackGoal))
                return true;
            if (frame == null || car == null || ball == null ||
                frame.TeamRank != 0 || !ControlMath.Finite(attackGoal) ||
                !float.IsFinite(frame.FreeTime))
                return false;

            // Slightly negative ETA estimates in the attacking third should not suppress the shot
            // planner entirely. The selected shot still has to pass its own mechanical validity.
            return ball.location.FlatDist(attackGoal) < 3200f &&
                frame.FreeTime >= -0.08f &&
                car.Location.Dist(ball.location) < 1500f;
        }

        /// <summary>
        /// Offensive-box exception to the conservative loose-ball challenge gate. When the ball is
        /// already near the opponent goal and the race is effectively tied, Stardust should still
        /// search for a finish instead of demoting the play into a cushion catch.
        /// </summary>
        public static bool CanForceFinishOpportunity(
            TacticalFrame frame, Car car, Ball ball, Vec3 attackGoal)
        {
            if (frame == null || car == null || ball == null ||
                car.IsDemolished || frame.TeamRank != 0 ||
                !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(attackGoal) ||
                !float.IsFinite(frame.MyEta) ||
                !float.IsFinite(frame.OpponentEta))
                return false;

            float goalDistance = ball.location.FlatDist(attackGoal);
            float carDistance = car.Location.FlatDist(ball.location);
            if (goalDistance > 1900f || carDistance > 1750f)
                return false;

            float raceDeficit = frame.MyEta - frame.OpponentEta;
            if (raceDeficit > 0.20f)
                return false;

            Vec3 toGoal = ControlMath.FlatUnit(
                attackGoal - ball.location, car.Forward);
            float movingGoalward = ball.velocity.Dot(toGoal);
            bool mouthPressure = goalDistance < 1250f;
            bool usableMotion = movingGoalward > -650f;

            return mouthPressure || usableMotion;
        }

        /// <summary>
        /// A high-value direct finish outranks keeping possession. Possession is still preferred for
        /// low-value/slow contacts, but an imminent goal-directed hit in the attacking half should
        /// not be converted into another catch or dribble setup.
        /// </summary>
        public static bool PreferImmediateShot(RUBot bot, Shot shot, TacticalFrame frame)
        {
            if (bot == null || shot == null || shot.Slice == null ||
                !float.IsFinite(shot.Slice.Time) || !ControlMath.Finite(shot.Slice.Location) ||
                !ControlMath.Finite(shot.ShotDirection))
                return false;

            float contactTime = shot.Slice.Time - Game.Time;
            if (!float.IsFinite(contactTime) || contactTime <= 0f || contactTime > 1.35f)
                return false;

            Vec3 attackGoal = bot.TheirGoal.Location;
            Vec3 goalAxis = ControlMath.FlatUnit(
                attackGoal - shot.Slice.Location, bot.Me.Forward);
            Vec3 shotDirection = ControlMath.FlatUnit(shot.ShotDirection, goalAxis);
            float goalward = shotDirection.Dot(goalAxis);
            if (goalward < 0.42f)
                return false;

            float side = Field.Side(bot.Team);
            bool offensiveHalf = shot.Slice.Location.y * side < -250f;
            float goalDistance = shot.Slice.Location.FlatDist(attackGoal);
            bool openLane = GoalLaneOpen(bot.LivingOpponents, shot.Slice.Location, attackGoal);

            bool immediateBoom = offensiveHalf && contactTime <= 0.72f && goalDistance < 5000f;
            bool closeFinish = goalDistance < 3200f && contactTime <= 1.00f;
            bool openNet = openLane && offensiveHalf && goalDistance < 5200f && contactTime <= 1.25f;
            bool pressuredRelease = frame?.UnderPressure == true &&
                offensiveHalf && contactTime <= 0.70f;

            return immediateBoom || closeFinish || openNet || pressuredRelease;
        }

        /// <summary>
        /// In the defensive third, an imminent clean clear outranks a soft catch/dribble when the
        /// opponent is closing. This is the "best defense is attack" gate: convert pressure into
        /// field position instead of preserving fragile possession beside our own net.
        /// </summary>
        public static bool PreferDefensiveClear(RUBot bot, Shot shot, TacticalFrame frame)
        {
            if (bot == null || shot == null || shot.Slice == null || frame == null ||
                !float.IsFinite(shot.Slice.Time) ||
                !ControlMath.Finite(shot.Slice.Location) ||
                !ControlMath.Finite(shot.ShotDirection))
                return false;

            float contactTime = shot.Slice.Time - Game.Time;
            if (!float.IsFinite(contactTime) || contactTime <= 0f || contactTime > 0.90f)
                return false;

            float ownDepth = Defense.OwnDepth(
                shot.Slice.Location, bot.OurGoal.Location);
            float goalDistance = shot.Slice.Location.FlatDist(bot.OurGoal.Location);
            bool deep = ownDepth > 2850f || goalDistance < 2450f;
            if (!deep)
                return false;

            bool pressure = frame.UnderPressure ||
                (float.IsFinite(frame.OpponentEta) && frame.OpponentEta < 1.55f) ||
                goalDistance < 1500f;
            if (!pressure)
                return false;

            return Defense.ClearDirectionIsSafe(
                shot.ShotDirection, bot.OurGoal.Location, 0.18f);
        }

        /// <summary>Compatibility wrapper: stationary defensive parking has zero terminal speed.</summary>
        public static float GuardSpeed(Car car, Vec3 target, float cruiseSpeed) =>
            Defense.DriveSpeed(car, target, cruiseSpeed, 0f);

        /// <summary>
        /// A genuinely deep-net car exits through the central mouth before receiving a field-side
        /// waypoint. A correctly placed shallow guard is not repeatedly ejected from the net.
        /// </summary>
        public static Vec3 GoalReturnTarget(Car car, Vec3 desiredGuard, Vec3 ownGoal)
        {
            if (car == null || !ControlMath.Finite(desiredGuard) || !ControlMath.Finite(ownGoal))
                return desiredGuard;

            float side = ownGoal.y < 0 ? -1 : 1;
            float line = MathF.Abs(ownGoal.y);
            bool behindLine = car.Location.y * side > line + 40f;
            bool insideMouth = MathF.Abs(car.Location.x - ownGoal.x) < Goal.Width * 0.5f + 220f;
            bool shallowGuard = desiredGuard.y * side >= line - 100f &&
                car.Location.y * side <= line + 260f;

            if (!behindLine || !insideMouth || shallowGuard)
                return desiredGuard;

            float safeHalfWidth = Goal.Width * 0.5f - 160f;
            float x = System.Math.Clamp(desiredGuard.x,
                ownGoal.x - safeHalfWidth, ownGoal.x + safeHalfWidth);
            return new Vec3(x, side * (line - 300f), 17);
        }

        /// <summary>
        /// Bias attacking aim away from floor-level "easy" targets. The raw Target.Clamp result is
        /// still retained as a fallback, but a second candidate aims through the middle/lower-middle
        /// of the aperture so a valid power contact does not default to z≈ball-radius every time.
        /// </summary>
        public static Vec3 PowerShotTarget(Target target, BallSlice slice, Vec3 easiest, Goal goal)
        {
            if (target == null || slice == null || goal == null ||
                !ControlMath.Finite(slice.Location) || !ControlMath.Finite(easiest))
                return easiest;

            target.GetCorrectedLimits(slice.Location,
                out _, out _, out Vec3 correctedTop, out Vec3 correctedBottom);

            float low = MathF.Min(correctedTop.z, correctedBottom.z);
            float high = MathF.Max(correctedTop.z, correctedBottom.z);
            if (!float.IsFinite(low) || !float.IsFinite(high) || high - low < 80f)
                return easiest;

            float goalDistance = slice.Location.FlatDist(goal.Location);
            float close = 1f - System.Math.Clamp(goalDistance / 4200f, 0f, 1f);
            float ballHeight = System.Math.Clamp((slice.Location.z - 110f) / 650f, 0f, 1f);

            // Far shots still target above the floor; close/high-ball finishes climb further into
            // the net while preserving substantial crossbar margin.
            float desiredZ = 220f + 95f * close + 85f * ballHeight;
            desiredZ = System.Math.Clamp(desiredZ, low + 28f, high - 28f);

            Vec3 raised = target.TargetSurface.Limit(
                new Vec3(easiest.x, easiest.y, desiredZ));
            return ControlMath.Finite(raised) ? raised : easiest;
        }

        /// <summary>
        /// Lightweight impact-strength estimate used for ranking mechanically valid shots. This is
        /// not a replacement for Rocket League collision physics; it only prevents the planner from
        /// always choosing the earliest weak touch when a slightly later, much harder hit is valid.
        /// </summary>
        public static float EstimateShotSpeed(Car car, BallSlice slice, Shot shot)
        {
            if (car == null || slice == null || shot == null ||
                !float.IsFinite(slice.Time) || !ControlMath.Finite(shot.TargetLocation) ||
                !ControlMath.Finite(shot.ShotDirection))
                return 0f;

            float t = slice.Time - Game.Time;
            if (!float.IsFinite(t) || t <= 0.001f)
                return 0f;

            Vec3 plannedCarVelocity =
                ((shot.TargetLocation - car.Location) / t).Cap(0f, Car.MaxSpeed);

            // A JumpShot finishes with a directional dodge, which contributes a substantial
            // contact-speed impulse that a plain GroundShot never gets. Include a conservative
            // fraction of that impulse so the planner can prefer a real power shot.
            if (shot is JumpShot jump)
            {
                Vec3 dodge = ControlMath.Unit(jump.DodgeDirection, shot.ShotDirection);
                plannedCarVelocity =
                    (plannedCarVelocity + dodge * 420f).Cap(0f, Car.MaxSpeed);
            }

            Vec3 relative = plannedCarVelocity - slice.Velocity;
            float relativeSpeed = relative.Length();
            if (!float.IsFinite(relativeSpeed))
                return 0f;

            Vec3 direction = ControlMath.Unit(shot.ShotDirection, Vec3.X);
            float alignment = relativeSpeed > 1f
                ? System.Math.Clamp(relative.Dot(direction) / relativeSpeed, 0f, 1f)
                : 0f;
            float existing = MathF.Max(0f, slice.Velocity.Dot(direction));
            float impulse = relativeSpeed * Utils.ShotPowerModifier(relativeSpeed) * alignment;

            return System.Math.Clamp(existing + impulse, 0f, Ball.MaxSpeed);
        }

        /// <summary>
        /// Bounded shot search with an optional hard contact deadline. Emergency defense must not
        /// select a nominally valid contact that occurs after the ball has already crossed the line.
        /// </summary>
        public static Shot SelectShot(RUBot bot, bool emergency, float opponentEta,
            Func<float, bool> claimed, float maxContactTime = 3f)
        {
            BallSlice[] slices = Ball.Prediction.Slices;
            if (slices == null || slices.Length == 0 ||
                !float.IsFinite(maxContactTime) || maxContactTime <= 0f)
                return null;

            Target target = new Target(emergency ? bot.OurGoal : bot.TheirGoal, emergency);
            float next = Game.Time + 0.08f;
            float bestScore = float.NegativeInfinity;
            int evaluated = 0;
            Shot best = null;

            foreach (BallSlice slice in slices)
            {
                if (slice == null || !float.IsFinite(slice.Time) ||
                    !ControlMath.Finite(slice.Location) || !ControlMath.Finite(slice.Velocity) ||
                    slice.Time < next)
                    continue;

                float t = slice.Time - Game.Time;
                if (t > MathF.Min(3f, maxContactTime) || evaluated >= 48)
                    break;

                next = slice.Time + 0.06f;
                evaluated++;

                // Emergency clears are allowed to intercept an inbound future slice even when
                // the current car position is not behind that future slice yet. Safety is enforced
                // on the resulting contact direction instead of a static pre-contact position test.
                if ((!emergency && !target.Fits(slice.Location)) ||
                    (!emergency && claimed(slice.Time)))
                    continue;

                Ball after = slice.ToBall();
                Vec3 approach = (slice.Location - bot.Me.Location) / t;
                after.velocity = approach.Cap(0f, Car.MaxSpeed) + slice.Velocity * 0.25f;
                Vec3 easiest = target.Clamp(after);
                if (!ControlMath.Finite(easiest))
                    continue;

                Vec3 powerAim = emergency
                    ? easiest
                    : PowerShotTarget(target, slice, easiest, bot.TheirGoal);
                Vec3[] destinations = easiest.Dist(powerAim) > 12f
                    ? new[] { powerAim, easiest }
                    : new[] { easiest };

                foreach (Vec3 destination in destinations)
                {
                    bool lowMechanicValid = false;

                    void Consider(Shot candidate, float cost)
                    {
                        if (candidate == null || !candidate.IsValid(bot.Me))
                            return;

                        if (emergency && !Defense.ClearDirectionIsSafe(
                                candidate.ShotDirection, bot.OurGoal.Location, 0.08f))
                            return;

                        lowMechanicValid |= candidate is GroundShot || candidate is JumpShot;

                        float score;
                        if (emergency)
                        {
                            score = -t - cost;
                        }
                        else
                        {
                            float predictedSpeed = EstimateShotSpeed(bot.Me, slice, candidate);
                            float powerBonus = System.Math.Clamp(
                                (predictedSpeed - 950f) / 900f, -0.45f, 1.45f);

                            float idealHeight = 300f;
                            float placement = 1f - System.Math.Clamp(
                                MathF.Abs(candidate.ShotTarget.z - idealHeight) / 300f,
                                0f, 1f);

                            // Power and useful net height are first-class objectives. Waiting a few
                            // tenths for a much stronger contact is often superior to the first
                            // barely-valid ground touch.
                            score = -t * 0.72f - cost +
                                powerBonus * 1.15f + placement * 0.22f -
                                MathF.Max(0f, t - opponentEta) * 2f;
                        }

                        if (score > bestScore)
                        {
                            best = candidate;
                            bestScore = score;
                        }
                    }

                    // For low balls, consider both mechanics. The old fallback chain never even
                    // evaluated a power dodge if GroundShot was technically valid.
                    Consider(new GroundShot(bot.Me, slice, destination), 0f);
                    Consider(new JumpShot(bot.Me, slice, destination), emergency ? 0.12f : 0.06f);

                    if (!lowMechanicValid)
                    {
                        Consider(new DoubleJumpShot(bot.Me, slice, destination), 0.34f);
                        Consider(new AerialShot(bot.Me, slice, destination), 0.8f);
                    }
                }

                if (best != null && t > best.Slice.Time - Game.Time + 0.45f)
                    break;
            }

            return best;
        }
    }
}
