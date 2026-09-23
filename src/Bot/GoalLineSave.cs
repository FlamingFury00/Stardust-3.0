using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Final-line POSITIONING controller. It never initiates a jump, dodge, or aerial. Elevated
    /// defensive contacts are owned by the normal shot solvers before this fallback is reached.
    /// </summary>
    public sealed class GoalLineSave : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible => true;

        public Vec3 Crossing { get; set; }
        public float CrossingTime { get; set; }
        public Vec3 GuardTarget { get; private set; }

        // Diagnostic compatibility: GoalLineSave is now explicitly non-jumping.
        public bool Jumping => false;
        public bool UsesDoubleJump => false;
        public bool FastTravel { get; private set; }
        public bool AirborneFlight => false;

        private readonly DefensiveDrive drive;

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

        public static float RequiredTravelSpeed(float distance, float timeRemaining)
        {
            if (!float.IsFinite(distance) || !float.IsFinite(timeRemaining) ||
                distance <= 0f)
                return 0f;

            float usable = MathF.Max(0.05f, timeRemaining - 0.08f);
            return System.Math.Clamp(distance / usable, 0f, Car.MaxSpeed);
        }

        public static float OptimisticTravelDistance(
            float flatSpeed, float timeRemaining)
        {
            if (!float.IsFinite(flatSpeed) || !float.IsFinite(timeRemaining) ||
                timeRemaining <= 0f)
                return 0f;

            float speed = System.Math.Clamp(MathF.Abs(flatSpeed), 0f, Car.MaxSpeed);
            const float optimisticAcceleration = 2600f;
            if (speed >= Car.MaxSpeed)
                return Car.MaxSpeed * timeRemaining;

            float toMax = (Car.MaxSpeed - speed) / optimisticAcceleration;
            if (timeRemaining <= toMax)
                return speed * timeRemaining +
                    0.5f * optimisticAcceleration * timeRemaining * timeRemaining;

            float accelerating = speed * toMax +
                0.5f * optimisticAcceleration * toMax * toMax;
            return accelerating + Car.MaxSpeed * (timeRemaining - toMax);
        }

        public static bool CanReachGuard(
            Car car, Vec3 guardTarget, float timeRemaining)
        {
            if (car == null || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(car.Velocity) ||
                !ControlMath.Finite(guardTarget) ||
                !float.IsFinite(timeRemaining))
                return false;

            float distance = car.Location.FlatDist(guardTarget);
            if (distance <= Defense.ArrivalRadius + 70f)
                return true;

            float usable = timeRemaining - 0.04f;
            if (usable <= 0f)
                return false;

            float optimistic = OptimisticTravelDistance(
                car.Velocity.FlatLen(), usable);
            return distance <= optimistic + 145f;
        }

        public static bool NeedsFastTravel(float distance, float timeRemaining)
        {
            if (!float.IsFinite(distance) || !float.IsFinite(timeRemaining) ||
                distance <= Defense.ArrivalRadius + 45f || timeRemaining <= 0f)
                return false;

            float required = RequiredTravelSpeed(distance, timeRemaining);
            return distance > 1050f || required > 700f;
        }

        /// <summary>
        /// Retained for telemetry/schema compatibility. Final-line positioning itself never starts
        /// a jump; a mechanically reachable elevated save must come from SelectShot.
        /// </summary>
        public static bool IsJumpPositioned(Car car, Vec3 guardTarget) => false;

        public static bool ShouldHoldLine(
            Car car, Vec3 crossing, float crossingTime, Vec3 goal, float now)
        {
            if (car == null || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(crossing) || !ControlMath.Finite(goal) ||
                !float.IsFinite(crossingTime) || !float.IsFinite(now))
                return false;

            float remaining = crossingTime - now;
            if (remaining <= 0.12f || remaining > 3.25f)
                return false;

            Vec3 guard = Defense.EmergencyTarget(crossing, goal);
            float distance = car.Location.FlatDist(guard);
            float depthError = MathF.Abs(car.Location.y - guard.y);
            return distance <= 260f && depthError <= 220f;
        }

        public void Run(RUBot bot)
        {
            if (bot == null || bot.Me == null ||
                !ControlMath.Finite(Crossing) ||
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

            // Do not manufacture an aerial from a positioning fallback. If we are airborne and the
            // shot planner found no mechanical save, yield so the supervisor can use Recover.
            if (!car.IsGrounded)
            {
                Finished = true;
                return;
            }

            GuardTarget = Defense.EmergencyTarget(
                Crossing, bot.OurGoal.Location);
            float guardDistance = car.Location.FlatDist(GuardTarget);
            float requiredSpeed = RequiredTravelSpeed(
                guardDistance, timeRemaining);
            bool fastTravel = NeedsFastTravel(
                guardDistance, timeRemaining);

            FastTravel = fastTravel;
            drive.Target = GuardTarget;
            drive.CruiseSpeed = Car.MaxSpeed;
            drive.TerminalSpeed = fastTravel
                ? System.Math.Clamp(requiredSpeed * 0.72f, 650f, 1350f)
                : 0f;
            drive.HoldPosition = !fastTravel;
            drive.AllowBoost = fastTravel;

            // Positioning saves stay on their wheels. If a jump is useful, SelectShot must prove
            // that a JumpShot/DoubleJumpShot/AerialShot can actually reach a ball slice.
            drive.AllowDodges = false;
            drive.Run(bot);
        }
    }
}
