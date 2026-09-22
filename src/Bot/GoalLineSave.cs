using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Final-line save controller. It preserves precise goal-mouth driving, but unlike a pure
    /// DefensiveDrive it can leave the ground when the predicted goal crossing is elevated.
    /// </summary>
    public sealed class GoalLineSave : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible =>
            !jumping || (float.IsFinite(jumpStarted) && Game.Time - jumpStarted > 0.68f);

        public Vec3 Crossing { get; set; }
        public float CrossingTime { get; set; }
        public Vec3 GuardTarget { get; private set; }
        public bool Jumping => jumping;
        public bool UsesDoubleJump => doubleJump;
        public bool FastTravel { get; private set; }
        public bool AirborneFlight { get; private set; }

        private readonly DefensiveDrive drive;
        private readonly JumpSequence jumps = new(0.16f);
        private readonly BoostGate airBoost = new();
        private bool jumping;
        private bool doubleJump;
        private float jumpStarted = float.NaN;

        public GoalLineSave(Car car, Vec3 crossing, float crossingTime)
        {
            Crossing = crossing;
            CrossingTime = crossingTime;
            GuardTarget = crossing;
            drive = new DefensiveDrive(
                car, crossing, Car.MaxSpeed, 0f,
                holdPosition: false, allowDodges: false,
                allowBoost: true);
        }

        /// <summary>
        /// Required flat speed to cover the remaining goal-mouth displacement with a small contact
        /// reserve. This turns save urgency into a time-to-crossing quantity instead of a fixed
        /// distance threshold.
        /// </summary>
        public static float RequiredTravelSpeed(float distance, float timeRemaining)
        {
            if (!float.IsFinite(distance) || !float.IsFinite(timeRemaining) ||
                distance <= 0f)
                return 0f;

            float usable = MathF.Max(0.05f, timeRemaining - 0.08f);
            return System.Math.Clamp(distance / usable, 0f, Car.MaxSpeed);
        }

        public static bool NeedsFastTravel(float distance, float timeRemaining)
        {
            if (!float.IsFinite(distance) || !float.IsFinite(timeRemaining) ||
                distance <= 420f || timeRemaining <= 0f)
                return false;

            // Never fall back into parking mode merely because the crossing is extremely close.
            // At that point the required speed saturates, but continuing maximum useful lateral
            // travel is still strictly better than braking short of an uncovered part of the mouth.
            float required = RequiredTravelSpeed(distance, timeRemaining);
            return distance > 1050f || required > 700f;
        }

        public void Run(RUBot bot)
        {
            if (bot == null || !ControlMath.Finite(Crossing) ||
                !float.IsFinite(CrossingTime))
            {
                Finished = true;
                return;
            }

            Car car = bot.Me;
            float timeRemaining = CrossingTime - Game.Time;
            if (!float.IsFinite(timeRemaining) || timeRemaining < -0.18f)
            {
                Finished = true;
                return;
            }

            GuardTarget = Defense.EmergencyTarget(Crossing, bot.OurGoal.Location);
            float guardDistance = car.Location.FlatDist(GuardTarget);

            // If an emergency begins while already airborne, do not wait passively for a landing.
            // Fly toward the predicted crossing itself; this is especially important for saves where
            // the car is still carrying useful lateral velocity after a failed/contested aerial.
            if (!car.IsGrounded && !jumping)
            {
                AirborneFlight = true;
                FastTravel = false;
                float horizon = System.Math.Clamp(timeRemaining, 0.10f, 1.20f);
                Vec3 acceleration = PossessionControl.FlightAtHorizon(
                    car, Crossing, Vec3.Zero, horizon);
                Vec3 nose = ControlMath.Unit(acceleration, car.Forward);
                ControlMath.Aim(car, bot.Controller, nose, Vec3.Up);
                bot.Controller.Throttle = 1f;
                bot.Controller.Handbrake = false;
                bot.Controller.Jump = false;
                bot.Controller.Boost = airBoost.Step(
                    Game.Time,
                    acceleration.Dot(car.Forward),
                    car.Forward.Dot(nose),
                    car.Boost,
                    false);
                return;
            }

            if (!jumping)
            {
                AirborneFlight = false;
                float requiredSpeed = RequiredTravelSpeed(guardDistance, timeRemaining);
                bool fastTravel = NeedsFastTravel(guardDistance, timeRemaining);
                FastTravel = fastTravel;
                drive.Target = GuardTarget;
                drive.CruiseSpeed = Car.MaxSpeed;
                drive.TerminalSpeed = fastTravel
                    ? System.Math.Clamp(requiredSpeed * 0.72f, 650f, 1350f)
                    : 0f;
                drive.HoldPosition = !fastTravel;
                drive.AllowBoost = fastTravel;
                drive.AllowDodges = fastTravel && timeRemaining > 0.95f &&
                    Defense.CanFastRecover(
                        car, Ball.Location, GuardTarget, bot.OurGoal.Location);
                drive.Run(bot);

                // A low crossing is best covered by staying on the wheels. For an elevated crossing,
                // start the jump only once lateral positioning is close enough and the vertical
                // flight time matches the remaining shot time.
                if (Crossing.z <= 155f)
                    return;

                float blockHeight = System.Math.Clamp(Crossing.z - 120f, 35f, 430f);
                doubleJump = blockHeight > 235f;
                float jumpTime = Utils.TimeToJump(Vec3.Up, blockHeight, doubleJump);
                if (!float.IsFinite(jumpTime) || jumpTime <= 0f)
                    jumpTime = doubleJump ? 0.55f : 0.32f;

                float lateralError = MathF.Abs(car.Location.x - GuardTarget.x);
                bool positioned = lateralError <= 650f ||
                    car.Location.FlatDist(GuardTarget) <= 760f;

                if (positioned && timeRemaining <= jumpTime + 0.12f)
                {
                    jumping = true;
                    jumpStarted = Game.Time;
                }
                else
                    return;
            }

            Vec3 towardCrossing = ControlMath.Unit(
                Crossing - car.Location, car.Forward);
            ControlMath.Aim(car, bot.Controller, towardCrossing, Vec3.Up);
            JumpCommand command = jumps.Step(
                Game.Time, doubleJump && !car.HasDoubleJumped);

            bot.Controller.Jump = command.Jump;
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = false;
            bot.Controller.Handbrake = false;

            // The second press should be a neutral double jump. Directional input here would turn
            // a vertical block into an accidental dodge across the mouth.
            if (command.Dodge)
            {
                bot.Controller.Pitch = 0f;
                bot.Controller.Yaw = 0f;
                bot.Controller.Roll = 0f;
            }

            if (float.IsFinite(jumpStarted) && Game.Time - jumpStarted > 0.30f &&
                car.IsGrounded)
                Finished = true;
        }
    }
}
