using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>Airborne rigid-body state for aerial planning.</summary>
    public struct FlightState
    {
        public Vec3 Position, Velocity, AngularVelocity;
        public Vec3 Forward, Right, Up;
        public float Boost;
        public float Time;
        /// <summary>Seconds since the current boost burst started, or negative when not boosting.</summary>
        public float BoostingTime;

        public FlightState(Vec3 position, Vec3 velocity, Vec3 angularVelocity, Vec3 forward, Vec3 right, Vec3 up, float boost)
        {
            Position = position;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
            Forward = forward;
            Right = right;
            Up = up;
            Boost = boost;
            Time = 0f;
            BoostingTime = -1f;
        }

        public static FlightState From(Car car) => new(car.Location, car.Velocity, car.AngularVelocity,
            car.Forward, car.Right, car.Up, car.Boost);

        public Vec3 LocalAngularVelocity => new(AngularVelocity.Dot(Forward), AngularVelocity.Dot(Right), AngularVelocity.Dot(Up));
    }

    /// <summary>
    /// Free-flight dynamics of a car: gravity, air boost (1058.3 uu/s^2 with a 0.1 s minimum burst),
    /// air throttle, and the air-control torque/damping model. Validated against RocketSim.
    /// </summary>
    public static class AerialModel
    {
        public static void Step(ref FlightState s, AirInput input, bool boost, float throttle, float dt)
        {
            // Angular dynamics in car-local axes (forward = roll, right = pitch, up = yaw).
            Vec3 w = s.LocalAngularVelocity;
            float rollAccel = -RL.RollTorque * input.Roll - RL.RollDamping * w.x;
            float pitchAccel = -RL.PitchTorque * input.Pitch - RL.PitchDamping * w.y * (1f - MathF.Abs(input.Pitch));
            float yawAccel = RL.YawTorque * input.Yaw - RL.YawDamping * w.z * (1f - MathF.Abs(input.Yaw));
            Vec3 angular = s.AngularVelocity + (s.Forward * rollAccel + s.Right * pitchAccel + s.Up * yawAccel) * dt;
            float spin = angular.Length();
            if (spin > RL.CarMaxAngularSpeed) angular *= RL.CarMaxAngularSpeed / spin;
            s.AngularVelocity = angular;
            Rotate(ref s, angular * dt);

            // A burst, once started, lasts at least 0.1 s measured from its start (RocketSim _UpdateBoost).
            bool boosting;
            if (s.Boost <= 0f) boosting = false;
            else if (s.BoostingTime >= 0f) boosting = boost || s.BoostingTime < RL.BoostMinimumTime;
            else boosting = boost;
            s.BoostingTime = boosting ? MathF.Max(0f, s.BoostingTime) + dt : -1f;

            Vec3 accel = new(0, 0, RL.Gravity);
            if (boosting)
            {
                accel += s.Forward * RL.BoostAccelAir;
                s.Boost = MathF.Max(0f, s.Boost - RL.BoostPerSecond * dt);
            }
            accel += s.Forward * (RL.AirThrottleAccel * System.Math.Clamp(throttle, -1f, 1f));
            // Semi-implicit Euler, as Bullet integrates.
            Vec3 velocity = s.Velocity + accel * dt;
            float speed = velocity.Length();
            if (speed > RL.CarMaxSpeed) velocity *= RL.CarMaxSpeed / speed;
            s.Position += velocity * dt;
            s.Velocity = velocity;
            s.Time += dt;
        }

        /// <summary>Rotates the basis by the rotation vector (Rodrigues) and re-orthonormalises.</summary>
        public static void Rotate(ref FlightState s, Vec3 rotation)
        {
            float angle = rotation.Length();
            if (angle < 1e-7f) return;
            Vec3 k = rotation / angle;
            float c = MathF.Cos(angle), sn = MathF.Sin(angle);
            Vec3 Apply(Vec3 v) => v * c + k.Cross(v) * sn + k * (k.Dot(v) * (1 - c));
            Vec3 f = Apply(s.Forward).Normalize();
            Vec3 u = Apply(s.Up);
            Vec3 r = u.Cross(f).Normalize();
            u = f.Cross(r).Normalize();
            s.Forward = f;
            s.Right = r;
            s.Up = u;
        }
    }
}
