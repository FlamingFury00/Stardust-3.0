using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Point-blank LOW ground block used only when the normal shot solvers cannot produce a
    /// contact in time. This fallback deliberately never jumps or dodges: elevated defensive
    /// contacts belong to GroundShot/JumpShot/DoubleJumpShot/AerialShot selection.
    /// </summary>
    public sealed class EmergencyClear : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible => true;
        public Vec3 Target { get; private set; }
        public Vec3 ClearDirection { get; private set; }

        // Retained in diagnostics for schema compatibility. EmergencyClear is now ground-only;
        // jump commitment is owned exclusively by the reusable shot/mechanic actions.
        public bool Committed => false;
        public bool GroundBlock { get; private set; } = true;
        public bool DirectionalDodgeAllowed => false;
        public bool NeutralSecondJump => false;

        private readonly Drive drive;
        private readonly float started = Game.Time;

        public EmergencyClear(Car car, Vec3 ownGoal, Vec3 attackGoal)
        {
            Vec3 fieldward = new(0f, ownGoal.y < 0f ? 1f : -1f, 0f);
            ClearDirection = ControlMath.FlatUnit(
                attackGoal - Ball.Location, fieldward);
            if (!Defense.ClearDirectionIsSafe(ClearDirection, ownGoal, 0.20f))
                ClearDirection = fieldward;

            Target = Field.LimitToNearestSurface(
                SafeContactTarget(Ball.Location, ClearDirection, ownGoal, 30f));
            drive = new Drive(
                car, Target, Car.MaxSpeed,
                allowDodges: false, wasteBoost: true)
            {
                AllowHandbrake = false
            };
        }

        /// <summary>
        /// Start only for a genuinely low point-blank ball. If the ball requires leaving the
        /// ground, the supervisor must ask the normal shot planner for a mechanical solution.
        /// </summary>
        public static bool CanStart(
            Car car, Ball ball, Vec3 ownGoal, float threatTime)
        {
            if (car == null || ball == null || car.IsDemolished ||
                !car.IsGrounded || !float.IsFinite(threatTime) ||
                threatTime < 0f || threatTime > 4.50f ||
                !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ownGoal))
                return false;

            float distance = car.Location.Dist(ball.location);
            if (distance > 540f || ball.location.z > 165f)
                return false;

            return Defense.IsDepthGoalSide(
                car.Location, ball.location, ownGoal, -190f);
        }

        /// <summary>
        /// Small continuation envelope for an already-running ground block. This is not a jump
        /// commitment: it remains interruptible and immediately yields when the ball lifts into
        /// shot-mechanic territory.
        /// </summary>
        public static bool CanContinue(
            Car car, Ball ball, Vec3 ownGoal, float threatTime)
        {
            if (car == null || ball == null || car.IsDemolished ||
                !car.IsGrounded ||
                !float.IsFinite(threatTime) || threatTime < 0f || threatTime > 1.60f ||
                !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ownGoal))
                return false;

            if (car.Location.Dist(ball.location) > 700f || ball.location.z > 190f)
                return false;

            return Defense.IsDepthGoalSide(
                car.Location, ball.location, ownGoal, -360f);
        }

        /// <summary>
        /// Pre-contact car targets must remain field-side of the own goal plane. When the normal
        /// behind-ball offset would fall inside the net, collapse it onto a safe block plane.
        /// </summary>
        public static Vec3 SafeContactTarget(
            Vec3 predictedBall, Vec3 clearDirection, Vec3 ownGoal, float offset)
        {
            if (!ControlMath.Finite(predictedBall) ||
                !ControlMath.Finite(clearDirection) ||
                !ControlMath.Finite(ownGoal))
                return predictedBall;

            float safeOffset = float.IsFinite(offset)
                ? MathF.Max(0f, offset)
                : 0f;
            Vec3 target = predictedBall -
                ControlMath.FlatUnit(clearDirection,
                    new Vec3(0f, ownGoal.y < 0f ? 1f : -1f, 0f)) *
                safeOffset;

            float side = ownGoal.y < 0f ? -1f : 1f;
            float safeDepth = MathF.Max(0f, MathF.Abs(ownGoal.y) - 90f);
            if (target.y * side > safeDepth)
                target.y = side * safeDepth;

            return new Vec3(target.x, target.y, 17f);
        }

        public void Run(RUBot bot)
        {
            if (bot == null || bot.Me == null || bot.Me.IsDemolished ||
                !bot.Me.IsGrounded)
            {
                Finished = true;
                return;
            }

            if (bot.OwnTouchThisTick)
            {
                Finished = true;
                return;
            }

            float elapsed = Game.Time - started;
            if (!float.IsFinite(elapsed) || elapsed > 0.72f)
            {
                Finished = true;
                return;
            }

            Car car = bot.Me;
            Ball ball = Ball.MainBall;
            float distance = car.Location.Dist(ball.location);

            // If the ball has lifted enough to require a jump, stop. The next supervisor pass
            // will run the normal shot search instead of inventing a bespoke jump sequence here.
            if (ball.location.z > 190f || distance > 760f)
            {
                Finished = true;
                return;
            }

            Vec3 fieldward = new(
                0f, bot.OurGoal.Location.y < 0f ? 1f : -1f, 0f);
            Vec3 direct = ControlMath.FlatUnit(
                bot.TheirGoal.Location - ball.location, fieldward);
            ClearDirection = Defense.ClearDirectionIsSafe(
                    direct, bot.OurGoal.Location, 0.20f)
                ? direct
                : fieldward;

            float horizon = System.Math.Clamp(
                distance / MathF.Max(
                    1200f, car.Velocity.Length() + ball.velocity.Length()) * 0.55f,
                0.04f, 0.14f);
            Ball predicted = Ball.Prediction.TrySample(
                    Game.Time + horizon, out Ball sample)
                ? sample
                : ball.Predict(horizon);

            if (predicted.location.z > 190f)
            {
                Finished = true;
                return;
            }

            GroundBlock = true;
            Target = Field.LimitToNearestSurface(
                SafeContactTarget(
                    predicted.location, ClearDirection,
                    bot.OurGoal.Location, 30f));

            drive.Target = Target;
            drive.TargetSpeed = Car.MaxSpeed;
            drive.AllowDodges = false;
            drive.AllowHandbrake = false;
            drive.WasteBoost = true;
            drive.Run(bot);
        }
    }
}
