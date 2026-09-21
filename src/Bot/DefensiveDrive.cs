using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Persistent defensive arrival. Ordinary Drive is a pass-through controller and can impose
    /// a 400 uu/s turn floor; merely giving it a low target speed does not make it a parking action.
    /// </summary>
    public sealed class DefensiveDrive : IAction
    {
        public bool Finished => false;
        public bool Interruptible => true;
        public Vec3 Target { get; set; }
        public float CruiseSpeed { get; set; }
        public bool Holding { get; private set; }
        private readonly Drive drive;

        public DefensiveDrive(Car car, Vec3 target, float cruiseSpeed = 1800)
        {
            Target = target;
            CruiseSpeed = cruiseSpeed;
            drive = new Drive(car, target, cruiseSpeed, allowDodges: false, wasteBoost: false)
            { AllowHandbrake = false };
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            if (!ControlMath.Finite(Target) || !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity))
            { bot.Controller = new RLBot.Flat.ControllerStateT(); return; }
            float distance = car.Location.FlatDist(Target);
            bool onFloor = car.IsGrounded && car.Location.z < 250 && car.Up.z > 0.75f;
            if (Holding && distance > 140) Holding = false;
            if (onFloor && distance <= Defense.StopRadius) Holding = true;
            if (Holding && onFloor)
            {
                // Preserve the heading instead of steering a circle around a point we already occupy.
                bot.Throttle(0);
                bot.Controller.Steer = 0;
                bot.Controller.Boost = false;
                bot.Controller.Handbrake = false;
                bot.Controller.Jump = false;
                return;
            }

            float speed = onFloor ? Defense.GuardSpeed(car, Target, CruiseSpeed) :
                (float.IsFinite(CruiseSpeed) ? System.Math.Clamp(CruiseSpeed, 0, Car.MaxSpeed) : 0);
            float forwardSpeed = car.Velocity.Dot(car.Forward);
            float along = (Target - car.Location).Dot(ControlMath.FlatUnit(car.Forward, Vec3.X));
            // Re-evaluate short reverse corrections after a target moves, without direction chatter.
            if (onFloor && MathF.Abs(forwardSpeed) < 650 && distance < 1200 &&
                along < -MathF.Max(100, distance * 0.65f)) drive.Backwards = true;
            else if (onFloor && MathF.Abs(forwardSpeed) < 200 && (along > 150 || distance > 1700))
                drive.Backwards = false;
            drive.Target = Target;
            drive.TargetSpeed = MathF.Max(1, speed); // Keep generic Drive's time arithmetic finite.
            drive.AllowDodges = false;
            drive.AllowHandbrake = false;
            drive.Run(bot);
            if (onFloor && speed < 400) bot.Throttle(speed, drive.Backwards);
            bot.Controller.Handbrake = false;
            bot.Controller.Jump = false;
            if (speed < 1800 || distance < 900) bot.Controller.Boost = false;
        }
    }
}
