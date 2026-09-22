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
        public string MobilityAction => drive.Action?.GetType().Name;

        private readonly Drive drive;

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

            // Short reverse correction is preferable to a 180-degree loop in front of net.
            if (onFloor && MathF.Abs(forwardSpeed) < 700f && distance < 1150f &&
                along < -MathF.Max(110f, distance * 0.62f))
                drive.Backwards = true;
            else if (onFloor && MathF.Abs(forwardSpeed) < 220f && (along > 170f || distance > 1650f))
                drive.Backwards = false;

            bool fastTravel = AllowDodges && !HoldPosition && onFloor &&
                distance > 1350f && !drive.Backwards && CruiseSpeed >= 2050f;

            drive.Target = Target;
            drive.TargetSpeed = MathF.Max(1f, speed);
            drive.AllowDodges = fastTravel;
            drive.DodgeMinSpeed = fastTravel ? 650f : 850f;
            drive.AllowHandbrake = false;
            drive.WasteBoost = AllowBoost;
            drive.Run(bot);

            bool mobilityCommitted = drive.Action != null && !drive.Action.Finished;

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
