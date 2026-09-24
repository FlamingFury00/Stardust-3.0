using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>Pitch, yaw and roll stick values.</summary>
    public struct AirInput
    {
        public float Pitch, Yaw, Roll;
    }

    /// <summary>
    /// Attitude control from Rocket League's air-control model: full input gives angular
    /// accelerations of 12.46 (pitch), 9.11 (yaw) and 38.35 (roll) rad/s^2 about the car's right, up and
    /// forward axes, pitch and yaw damping act only while their input is released, roll damping
    /// always acts, and angular speed is capped at 5.5 rad/s.
    ///
    /// The controller tracks a braking-curve angular velocity toward the shortest rotation, so it
    /// spins up as hard as possible and stops just in time instead of creeping in on a PD tail.
    /// </summary>
    public static class AirControl
    {
        private const float Margin = 0.9f;

        /// <summary>Shortest rotation (rotation vector) from the car's attitude to (forward, up), in car-local axes.</summary>
        public static Vec3 RotationError(Vec3 carForward, Vec3 carRight, Vec3 carUp, Vec3 forward, Vec3 up)
        {
            Vec3 f = Unit(forward, carForward);
            Vec3 upHint = Unit(up, Vec3.Up);
            if (MathF.Abs(f.Dot(upHint)) > 0.98f)
                upHint = MathF.Abs(f.z) < 0.9f ? Vec3.Up : new Vec3(0, 1, 0);
            Vec3 r = Unit(upHint.Cross(f), carRight);
            Vec3 u = Unit(f.Cross(r), carUp);
            // Rotation matrix of the target expressed in car-local axes (columns = target axes).
            float m00 = carForward.Dot(f), m01 = carForward.Dot(r), m02 = carForward.Dot(u);
            float m10 = carRight.Dot(f), m11 = carRight.Dot(r), m12 = carRight.Dot(u);
            float m20 = carUp.Dot(f), m21 = carUp.Dot(r), m22 = carUp.Dot(u);
            float w, x, y, z, s;
            float trace = m00 + m11 + m22;
            if (trace > 0)
            {
                s = MathF.Sqrt(trace + 1) * 2;
                w = 0.25f * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                s = MathF.Sqrt(MathF.Max(1e-6f, 1 + m00 - m11 - m22)) * 2;
                w = (m21 - m12) / s; x = 0.25f * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                s = MathF.Sqrt(MathF.Max(1e-6f, 1 + m11 - m00 - m22)) * 2;
                w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25f * s; z = (m12 + m21) / s;
            }
            else
            {
                s = MathF.Sqrt(MathF.Max(1e-6f, 1 + m22 - m00 - m11)) * 2;
                w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25f * s;
            }
            Vec3 imaginary = new Vec3(x, y, z) * (w < 0 ? -1 : 1);
            float norm = imaginary.Length();
            return norm < 1e-5f ? imaginary * 2 : imaginary * (2 * MathF.Atan2(norm, MathF.Abs(w)) / norm);
        }

        /// <summary>
        /// Stick input steering the car toward the target attitude. <paramref name="localAngularVelocity"/>
        /// is (forward, right, up) components of the world angular velocity.
        /// </summary>
        public static AirInput Orient(Vec3 carForward, Vec3 carRight, Vec3 carUp, Vec3 localAngularVelocity,
            Vec3 targetForward, Vec3 targetUp, float dt = 1f / 120f)
        {
            Vec3 error = RotationError(carForward, carRight, carUp, targetForward, targetUp);
            float angle = error.Length();
            Vec3 axis = angle > 1e-5f ? error / angle : Vec3.Zero;

            // Axis-specific acceleration limits, blended along the rotation axis.
            float accel = 1f / (MathF.Abs(axis.x) / RL.RollTorque + MathF.Abs(axis.y) / RL.PitchTorque +
                MathF.Abs(axis.z) / RL.YawTorque + 1e-6f);
            float speed = MathF.Min(RL.CarMaxAngularSpeed, MathF.Sqrt(2f * accel * Margin * angle));
            Vec3 desired = axis * speed;

            // Close the velocity gap within about two ticks, compensating the damping that acts
            // whenever an input is released.
            float horizon = MathF.Max(dt, 1f / 60f);
            Vec3 w = localAngularVelocity;
            float rollAccel = (desired.x - w.x) / horizon + RL.RollDamping * w.x;
            float pitchAccel = (desired.y - w.y) / horizon;
            float yawAccel = (desired.z - w.z) / horizon;

            var input = new AirInput
            {
                Roll = System.Math.Clamp(-rollAccel / RL.RollTorque, -1f, 1f),
                Pitch = System.Math.Clamp(-pitchAccel / RL.PitchTorque, -1f, 1f),
                Yaw = System.Math.Clamp(yawAccel / RL.YawTorque, -1f, 1f),
            };
            // Damping on pitch/yaw scales with (1 - |input|); feed it forward where input is partial.
            input.Pitch = System.Math.Clamp(input.Pitch - RL.PitchDamping * w.y * (1f - MathF.Abs(input.Pitch)) / RL.PitchTorque, -1f, 1f);
            input.Yaw = System.Math.Clamp(input.Yaw + RL.YawDamping * w.z * (1f - MathF.Abs(input.Yaw)) / RL.YawTorque, -1f, 1f);
            return input;
        }

        /// <summary>Rough time to rotate through <paramref name="angle"/> radians from rest.</summary>
        public static float TurnTime(float angle, Vec3 localAxis)
        {
            Vec3 axis = localAxis.Length() > 1e-5f ? localAxis.Normalize() : new Vec3(0, 1, 0);
            float accel = 1f / (MathF.Abs(axis.x) / RL.RollTorque + MathF.Abs(axis.y) / RL.PitchTorque +
                MathF.Abs(axis.z) / RL.YawTorque + 1e-6f);
            float spinUp = RL.CarMaxAngularSpeed / accel;
            float triangular = 2f * MathF.Sqrt(angle / accel);
            if (triangular * 0.5f <= spinUp) return triangular;
            float cruise = (angle - accel * spinUp * spinUp) / RL.CarMaxAngularSpeed;
            return 2f * spinUp + cruise;
        }

        private static Vec3 Unit(Vec3 v, Vec3 fallback)
        {
            float length = v.Length();
            return float.IsFinite(length) && length > 1e-4f ? v / length : fallback;
        }
    }
}
