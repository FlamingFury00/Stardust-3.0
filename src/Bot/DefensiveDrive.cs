using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Persistent defensive arrival controller. Unlike generic Drive, this controller can actually
    /// brake below the tight-turn 400 uu/s floor, cannot defensive-flip/powerslide, and can follow
    /// a moving shadow target without treating it as a one-shot waypoint.
    /// </summary>
    public sealed class DefensiveDrive : IAction
    {
        public bool Finished => false;
        public bool Interruptible => drive.Interruptible;
        public Vec3 Target { get; set; }
        public float CruiseSpeed { get; set; }
        public float TerminalSpeed { get; set; }
        public bool HoldPosition { get; set; }
        public bool AllowDodges { get; set; }
        public bool AllowBoost { get; set; }
        public bool Holding { get; private set; }
        public bool Backwards => drive.Backwards;
        public string MobilityAction => drive.Action?.GetType().Name;

        private readonly Drive drive;
        private Vec3 mobilityReferenceTarget;
        private bool mobilityReferenceInitialized;
        private float mobilityStableSince = float.NaN;
        private float mobilityCooldownUntil = float.NegativeInfinity;
        private bool mobilityWasActive;

        public DefensiveDrive(Car car, Vec3 target, float cruiseSpeed = 1800f,
            float terminalSpeed = 0f, bool holdPosition = false,
            bool allowDodges = false, bool allowBoost = false)
        {
            Target = target;
            CruiseSpeed = cruiseSpeed;
            TerminalSpeed = terminalSpeed;
            HoldPosition = holdPosition;
            AllowDodges = allowDodges;
            AllowBoost = allowBoost;
            drive = new Drive(car, target, MathF.Max(1f, cruiseSpeed), allowDodges, wasteBoost: false)
            {
                AllowHandbrake = false,
                DodgeMinSpeed = allowDodges ? 650f : 850f
            };
        }

        public static bool ShouldDriveBackwards(
            bool currentlyBackwards, bool onFloor,
            float forwardSpeed, float distance, float along)
        {
            if (!onFloor || !float.IsFinite(forwardSpeed) ||
                !float.IsFinite(distance) || !float.IsFinite(along))
                return false;

            if (currentlyBackwards)
            {
                // Reverse is a local correction, not a persistent driving mode. Fresh side-defense
                // telemetry showed a short reverse choice surviving after the target moved into a
                // 1.3-3.4k uu lateral recovery, keeping the car backwards through the entire play.
                bool routeExpanded = distance > 1325f;
                bool noLongerMostlyBehind =
                    along > -MathF.Max(90f, distance * 0.28f);
                if (routeExpanded || noLongerMostlyBehind)
                    return false;
                return true;
            }

            return MathF.Abs(forwardSpeed) < 700f &&
                distance < 1150f &&
                along < -MathF.Max(110f, distance * 0.62f);
        }

        public static bool ShouldUseMobility(
            bool allowDodges, bool holdPosition, bool onFloor,
            float distance, bool backwards, float cruiseSpeed,
            float stableFor, float cooldownRemaining)
        {
            if (!allowDodges || holdPosition || !onFloor || backwards ||
                !float.IsFinite(distance) || !float.IsFinite(cruiseSpeed) ||
                !float.IsFinite(stableFor) || float.IsNaN(cooldownRemaining))
                return false;

            // A defensive speed mechanic is only worth the loss of steering if the route is both
            // long and stable. This prevents moving shadow/recovery waypoints from spawning flips
            // that are obsolete before the dodge finishes.
            return distance > 1800f &&
                cruiseSpeed >= 2100f &&
                stableFor >= 0.22f &&
                cooldownRemaining <= 0f;
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            if (!ControlMath.Finite(Target) || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(car.Velocity))
            {
                bot.Controller = new RLBot.Flat.ControllerStateT();
                return;
            }

            float distance = car.Location.FlatDist(Target);
            bool onFloor = car.IsGrounded && car.Location.z < 250f && car.Up.z > 0.72f;

            if (!HoldPosition)
                Holding = false;
            else
            {
                if (Holding && distance > 145f)
                    Holding = false;
                if (!Holding && onFloor && distance <= Defense.ArrivalRadius &&
                    car.Velocity.FlatLen() < 260f)
                    Holding = true;
            }

            if (Holding && onFloor)
            {
                // Throttle-to-zero invokes the normal brake controller rather than allowing a
                // perpetual low-speed orbit around a point that is already covered.
                bot.Throttle(0f);
                bot.Controller.Steer = 0f;
                bot.Controller.Boost = false;
                bot.Controller.Handbrake = false;
                bot.Controller.Jump = false;
                return;
            }

            float speed = onFloor
                ? Defense.DriveSpeed(car, Target, CruiseSpeed, TerminalSpeed)
                : (float.IsFinite(CruiseSpeed) ? System.Math.Clamp(CruiseSpeed, 0f, Car.MaxSpeed) : 0f);

            Vec3 flatForward = ControlMath.FlatUnit(car.Forward, Vec3.X);
            float forwardSpeed = car.Velocity.Dot(car.Forward);
            float along = (Target - car.Location).Dot(flatForward);

            // Short reverse correction is preferable to a 180-degree loop in front of net,
            // but it must be released as soon as the moving defensive target becomes a real route.
            drive.Backwards = ShouldDriveBackwards(
                drive.Backwards, onFloor, forwardSpeed, distance, along);

            if (!mobilityReferenceInitialized ||
                !ControlMath.Finite(mobilityReferenceTarget) ||
                mobilityReferenceTarget.FlatDist(Target) > 180f)
            {
                mobilityReferenceTarget = Target;
                mobilityReferenceInitialized = true;
                mobilityStableSince = Game.Time;
            }
            else if (!float.IsFinite(mobilityStableSince))
                mobilityStableSince = Game.Time;

            float stableFor = MathF.Max(0f, Game.Time - mobilityStableSince);
            float cooldownRemaining = mobilityCooldownUntil - Game.Time;
            bool fastTravel = ShouldUseMobility(
                AllowDodges, HoldPosition, onFloor,
                distance, drive.Backwards, CruiseSpeed,
                stableFor, cooldownRemaining);

            drive.Target = Target;
            drive.TargetSpeed = MathF.Max(1f, speed);
            drive.AllowDodges = fastTravel;
            drive.DodgeMinSpeed = fastTravel ? 650f : 850f;
            drive.AllowHandbrake = false;
            drive.WasteBoost = AllowBoost;
            drive.Run(bot);

            bool mobilityCommitted = drive.Action != null && !drive.Action.Finished;
            if (mobilityCommitted && !mobilityWasActive)
                mobilityCooldownUntil = Game.Time + 1.05f;
            mobilityWasActive = mobilityCommitted;

            // Generic Drive has a 400 uu/s minimum in its tight-turn branch. Override only that
            // terminal regime while preserving its mature route/surface steering everywhere else.
            if (!mobilityCommitted && onFloor && speed < 430f)
                bot.Throttle(speed, drive.Backwards);

            // Normal defensive driving suppresses jump/powerslide so a parking controller cannot
            // accidentally leave the ground. Once Drive has intentionally committed a speedflip,
            // dodge, wavedash, or half-flip, preserve that subaction's exact controller sequence.
            if (!mobilityCommitted)
            {
                bot.Controller.Handbrake = false;
                bot.Controller.Jump = false;
            }

            if (!AllowBoost && !mobilityCommitted &&
                (speed < 1800f || distance < 950f || drive.Backwards))
                bot.Controller.Boost = false;
        }
    }
}
