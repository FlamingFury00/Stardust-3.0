using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Close-range emergency block/clear used when a goal-bound ball is already within contact
    /// distance. Its job is deliberately narrow: touch the ball away from our own goal instead of
    /// abandoning a reachable ball to drive toward a distant goal-line waypoint.
    /// </summary>
    public sealed class EmergencyClear : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible =>
            !committed || (float.IsFinite(committedAt) && Game.Time - committedAt > 0.62f);
        public Vec3 Target { get; private set; }
        public Vec3 ClearDirection { get; private set; }
        public bool Committed => committed;
        public bool GroundBlock { get; private set; }
        public bool DirectionalDodgeAllowed { get; private set; }
        public bool NeutralSecondJump { get; private set; }

        private readonly Drive drive;
        private readonly Vec3 ownGoal;
        // A close scoring ball can traverse 250+ uu during a conventional 0.12 s jump hold.
        // Use the shortest legal hold so the release/dodge edge is available near first contact.
        private readonly JumpSequence jumps = new(0.025f);
        private readonly float started = Game.Time;
        private bool committed;
        private float committedAt = float.NaN;

        public EmergencyClear(Car car, Vec3 ownGoal, Vec3 attackGoal)
        {
            this.ownGoal = ownGoal;
            Vec3 fieldward = new(0f, ownGoal.y < 0f ? 1f : -1f, 0f);
            ClearDirection = ControlMath.FlatUnit(
                attackGoal - Ball.Location, fieldward);
            if (!Defense.ClearDirectionIsSafe(ClearDirection, ownGoal, 0.20f))
                ClearDirection = fieldward;

            Target = Field.LimitToNearestSurface(
                SafeContactTarget(Ball.Location, ClearDirection, ownGoal, 145f));
            drive = new Drive(
                car, Target, Car.MaxSpeed,
                allowDodges: false, wasteBoost: true)
            {
                AllowHandbrake = false
            };
        }

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
            if (distance > 540f || ball.location.z > 340f)
                return false;

            // A slight upfield offset is acceptable at point-blank range, but not a full wrong-side
            // chase. The directional dodge below always points away from our own goal.
            return Defense.IsDepthGoalSide(
                car.Location, ball.location, ownGoal, -190f);
        }

        /// <summary>
        /// Hysteresis envelope for an already-selected emergency contact. Starting a clear remains
        /// strict, but once a save attempt is in motion it may continue through a wider distance and
        /// airborne envelope so a remote, impossible goal-line fallback cannot steal the action.
        /// </summary>
        public static bool CanContinue(
            Car car, Ball ball, Vec3 ownGoal, float threatTime)
        {
            if (car == null || ball == null || car.IsDemolished ||
                !float.IsFinite(threatTime) || threatTime < 0f || threatTime > 1.60f ||
                !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ownGoal))
                return false;

            if (car.Location.Dist(ball.location) > 860f || ball.location.z > 430f)
                return false;

            return Defense.IsDepthGoalSide(
                car.Location, ball.location, ownGoal, -360f);
        }

        /// <summary>
        /// Pre-contact car targets must remain field-side of the own goal plane. When the normal
        /// behind-ball shooting offset would fall inside the net, collapse that offset onto a safe
        /// block plane instead of asking the car to chase the ball through its own goal.
        /// </summary>
        public static Vec3 SafeContactTarget(
            Vec3 predictedBall, Vec3 clearDirection, Vec3 ownGoal, float offset)
        {
            if (!ControlMath.Finite(predictedBall) ||
                !ControlMath.Finite(clearDirection) ||
                !ControlMath.Finite(ownGoal))
                return predictedBall;

            Vec3 target = predictedBall -
                ControlMath.FlatUnit(clearDirection,
                    new Vec3(0f, ownGoal.y < 0f ? 1f : -1f, 0f)) *
                MathF.Max(0f, offset);

            float side = ownGoal.y < 0f ? -1f : 1f;
            float safeDepth = MathF.Max(0f, MathF.Abs(ownGoal.y) - 90f);
            if (target.y * side > safeDepth)
                target.y = side * safeDepth;

            return new Vec3(target.x, target.y, 17f);
        }

        public void Run(RUBot bot)
        {
            if (bot == null || bot.Me == null || bot.Me.IsDemolished)
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
            if (!float.IsFinite(elapsed) || elapsed > 0.95f)
            {
                Finished = true;
                return;
            }

            Car car = bot.Me;
            Ball ball = Ball.MainBall;
            float distance = car.Location.Dist(ball.location);

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
                0.04f, 0.18f);
            Ball predicted = Ball.Prediction.TrySample(
                    Game.Time + horizon, out Ball sample)
                ? sample
                : ball.Predict(horizon);

            Target = Field.LimitToNearestSurface(
                SafeContactTarget(
                    predicted.location, ClearDirection,
                    bot.OurGoal.Location, 145f));

            if (!committed)
            {
                // A low emergency block is not a shooting setup. Fresh telemetry had the car only
                // ~171 uu from a goal-bound ball, yet the 145 uu "behind ball" clear target sat
                // another ~200 uu deeper in the net, so the controller circled behind the play
                // instead of making the available save. Drive essentially through the ball for low
                // blocks; clear direction matters only after contact has been secured.
                bool raised = predicted.location.z > 155f;
                GroundBlock = !raised;
                if (!raised)
                {
                    Target = Field.LimitToNearestSurface(
                        SafeContactTarget(
                            predicted.location, ClearDirection,
                            bot.OurGoal.Location, 30f));
                }

                drive.Target = Target;
                drive.TargetSpeed = Car.MaxSpeed;
                drive.AllowDodges = false;
                drive.AllowHandbrake = false;
                drive.WasteBoost = true;
                drive.Run(bot);

                // Low shots should be blocked on the wheels. The previous implementation jumped
                // at z≈100–130, removing lateral steering exactly as the ball crossed the car.
                // Raised contacts still need an immediate jump/dodge, but start it on this same
                // controller tick rather than spending another planning frame in drive mode.
                bool imminent = distance < 470f ||
                    (distance < 530f && elapsed > 0.05f);

                if (!raised || !imminent)
                    return;

                committed = true;
                committedAt = Game.Time;
            }

            GroundBlock = false;

            Vec3 towardBall = ControlMath.Unit(
                predicted.location - car.Location, car.Forward);
            Vec3 contactAim = ControlMath.Unit(
                towardBall * 0.84f + ClearDirection * 0.16f, towardBall);
            ControlMath.Aim(
                car, bot.Controller, contactAim, Vec3.Up);

            // Contact comes before power. For mid-height balls, one steerable jump is enough.
            // For genuinely high balls, the second press is often required for vertical reach,
            // but it must remain a neutral double-jump while the car is still far below the ball.
            // The 2-1 -> 2-2 trace showed the old fieldward dodge firing ~250 uu below contact,
            // instantly adding huge +Y speed and removing the car from the goal-mouth play.
            float verticalGap = predicted.location.z - car.Location.z;
            bool highContact = predicted.location.z > 235f;
            bool contactReadyForDodge =
                highContact &&
                MathF.Abs(verticalGap) <= 155f &&
                distance <= 235f &&
                car.Forward.Dot(towardBall) > 0.30f;

            DirectionalDodgeAllowed = contactReadyForDodge;
            NeutralSecondJump = highContact && !contactReadyForDodge;
            JumpCommand command = jumps.Step(
                Game.Time, highContact && bot.Jump.CanDodge);

            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = false;
            bot.Controller.Handbrake = false;
            bot.Controller.Jump = command.Jump;

            if (command.Dodge)
            {
                if (DirectionalDodgeAllowed)
                {
                    Vec3 local = ControlMath.FlatUnit(
                        car.Local(contactAim), new Vec3(1f, 0f, 0f));
                    bot.Controller.Pitch = -local.x;
                    bot.Controller.Yaw = local.y;
                    bot.Controller.Roll = 0f;
                }
                else
                {
                    // Neutral second jump: maximize vertical coverage without throwing away
                    // the lateral/goal-side line that made the save reachable.
                    bot.Controller.Pitch = 0f;
                    bot.Controller.Yaw = 0f;
                    bot.Controller.Roll = 0f;
                }
            }

            if (Game.Time - committedAt > 0.82f ||
                (distance > 900f && Game.Time - committedAt > 0.32f))
                Finished = true;
        }
    }
}
