using System;
using RedUtils.Math;
using RedUtils.Physics;

namespace RedUtils
{
    /// <summary>
    /// Continuous movement toward a <see cref="DriveTarget"/> that the decision layer may retarget
    /// every tick. Ground driving uses the shared navigation policy; around it this action adds the
    /// car-control skills a player uses between touches: powersliding through sharp turns,
    /// backpedalling to a spot just behind, and landing on the wheels when airborne.
    /// </summary>
    public sealed class Travel : IAction
    {
        /// <summary>Powerslide when the target is this far off the nose at speed.</summary>
        private const float SlideAngle = 1.25f;
        private const float SlideReleaseAngle = 0.3f;
        private const float SlideMinimumSpeed = 900f;
        private const float ParkRadius = 90f;

        public DriveTarget Target;
        public bool Finished => false;
        public bool Interruptible => true;
        public string Mode { get; private set; } = "drive";

        private bool sliding;

        public Travel(DriveTarget target) => Target = target;

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            if (!car.IsGrounded)
            {
                Land(bot, car);
                return;
            }
            if (car.Up.z < 0.7f)
            {
                LeaveWall(bot, car);
                return;
            }

            Vec3 forward = car.Forward.Flatten().Normalize();
            Vec3 to = (Target.Point - car.Location).Flatten();
            float distance = to.Length();
            float angle = GroundModel.SignedAngle(forward, to);
            float speed = car.Velocity.Dot(car.Forward);
            bool positioning = float.IsFinite(Target.ArrivalSpeed);

            if (positioning && distance < ParkRadius)
            {
                Park(bot, car, speed);
                return;
            }

            // A short hop to a spot behind, when the car should keep facing the way it faces now
            // (e.g. retreating while watching the ball): reverse instead of turning around.
            bool keepsHeading = !Target.HasDirection || forward.Dot(Target.Direction) > 0.3f;
            if (MathF.Abs(angle) > 2.3f && distance < 900f && keepsHeading && speed < 400f)
            {
                Backpedal(bot, car, to, distance, speed);
                return;
            }

            DriveCommand command = Navigator.Control(car.Location, car.Forward, speed, car.Boost,
                car.AngularVelocity.z, Target, Game.Time);
            bot.Controller.Throttle = command.Throttle;
            bot.Controller.Steer = command.Steer;
            bot.Controller.Boost = command.Boost;

            // Powerslide to swing the nose around when the required turn is far tighter than the
            // car's turning circle at this speed.
            float absAngle = MathF.Abs(angle);
            if (!sliding && absAngle > SlideAngle && speed > SlideMinimumSpeed && distance > 500f)
                sliding = true;
            else if (sliding && (absAngle < SlideReleaseAngle || speed < 300f))
                sliding = false;
            if (sliding)
            {
                bot.Controller.Handbrake = true;
                bot.Controller.Steer = MathF.Sign(angle);
                bot.Controller.Throttle = 1f;
                bot.Controller.Boost = false;
                Mode = "powerslide";
            }
            else
            {
                bot.Controller.Handbrake = false;
                Mode = "drive";
            }
        }

        private void Park(RUBot bot, Car car, float speed)
        {
            Mode = "park";
            sliding = false;
            bot.Controller.Boost = false;
            bot.Controller.Handbrake = false;
            bot.Controller.Throttle = System.Math.Clamp(-speed / 300f, -1f, 1f);
            if (MathF.Abs(speed) < 40f) bot.Controller.Throttle = 0f;
            bot.Controller.Steer = 0f;
        }

        private void Backpedal(RUBot bot, Car car, Vec3 to, float distance, float speed)
        {
            Mode = "reverse";
            sliding = false;
            // Steer as if the rear were the front: angle measured from the backward axis.
            Vec3 backward = -car.Forward.Flatten().Normalize();
            float angle = GroundModel.SignedAngle(backward, to);
            bot.Controller.Steer = System.Math.Clamp(-3f * angle, -1f, 1f);
            float desired = float.IsFinite(Target.ArrivalSpeed)
                ? MathF.Sqrt(Target.ArrivalSpeed * Target.ArrivalSpeed + 2f * Navigator.PlannedBraking * MathF.Max(0f, distance - ParkRadius))
                : RL.CarMaxSpeed;
            desired = MathF.Min(desired, 1400f);
            float error = desired - (-speed);
            bot.Controller.Throttle = error > 0f ? -1f : error < -200f ? 1f : 0f;
            bot.Controller.Boost = false;
            bot.Controller.Handbrake = false;
        }

        /// <summary>Orient to land on the wheels, nose along the landing velocity (or toward the target).</summary>
        private void Land(RUBot bot, Car car)
        {
            Mode = "air";
            sliding = false;
            float time = car.PredictLandingTime();
            time = float.IsFinite(time) ? System.Math.Clamp(time, 0f, 2f) : 0.3f;
            Vec3 normal = Field.NearestSurface(car.PredictLocation(time)).Normal;
            Vec3 velocity = car.PredictVelocity(time);
            Vec3 tangent = velocity - normal * velocity.Dot(normal);
            Vec3 toTarget = Target.Point - car.Location;
            toTarget -= normal * toTarget.Dot(normal);
            Vec3 nose = tangent.Length() > 500f ? tangent : toTarget.Length() > 1f ? toTarget : car.Forward;
            Vec3 local = new(car.AngularVelocity.Dot(car.Forward), car.AngularVelocity.Dot(car.Right), car.AngularVelocity.Dot(car.Up));
            AirInput air = AirControl.Orient(car.Forward, car.Right, car.Up, local, nose.Normalize(), normal);
            bot.Controller.Pitch = air.Pitch;
            bot.Controller.Yaw = air.Yaw;
            bot.Controller.Roll = air.Roll;
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = false;
            bot.Controller.Jump = false;
            // Landing with a powerslide keeps the speed carried into the ground.
            bot.Controller.Handbrake = time < 0.1f && tangent.Length() > 800f;
        }

        /// <summary>Drive down off a wall toward the target.</summary>
        private void LeaveWall(RUBot bot, Car car)
        {
            Mode = "wall";
            sliding = false;
            Vec3 point = new(Target.Point.x, Target.Point.y, 0f);
            Vec3 local = car.Local(point - car.Location);
            float angle = MathF.Atan2(local.y, local.x);
            bot.Controller.Steer = System.Math.Clamp(3f * angle, -1f, 1f);
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = MathF.Abs(angle) < 0.3f && car.Velocity.Length() < 1900f && car.Boost > 30f;
            bot.Controller.Handbrake = false;
        }
    }
}
