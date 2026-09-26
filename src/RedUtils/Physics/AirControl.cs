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
        /// <summary>Sine of the angle between nose and up hint below which the car's own roll is kept.</summary>
        private const float RollFreeCone = 0.35f;

        /// <summary>Shortest rotation (rotation vector) from the car's attitude to (forward, up), in car-local axes.</summary>
        public static Vec3 RotationError(Vec3 carForward, Vec3 carRight, Vec3 carUp, Vec3 forward, Vec3 up)
        {
            Vec3 f = Unit(forward, carForward);
            Vec3 upHint = Unit(up, Vec3.Up);
            // The roof goes toward the hint square to the nose. As the nose nears the hint that
            // direction becomes undefined, so the roof the car would have after the shortest swing
            // of its nose onto the target takes over (no roll is forced), blended in continuously
            // from RollFreeCone. A swung roof facing away from the hint (a car going over the top)
            // is kept whole inside the cone: adding the two would cancel them to nothing.
            Vec3 hinted = upHint - f * f.Dot(upHint);
            float defined = hinted.Length();
            if (defined < RollFreeCone)
            {
                Vec3 swung = SwungUp(carForward, carUp, f);
                upHint = hinted.Dot(swung) < 0f ? swung : hinted + swung * (RollFreeCone - defined);
            }
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

        /// <summary>The car's roof after the shortest rotation taking its nose onto <paramref name="nose"/>.</summary>
        private static Vec3 SwungUp(Vec3 carForward, Vec3 carUp, Vec3 nose)
        {
            Vec3 axis = carForward.Cross(nose);
            float sin = axis.Length(), cos = carForward.Dot(nose);
            // Nose already on target, or reversed: a half pitch turns the roof over.
            if (sin < 1e-4f) return cos > 0f ? carUp : -carUp;
            axis /= sin;
            // Rodrigues' rotation of the roof about the swing axis.
            return carUp * cos + axis.Cross(carUp) * sin + axis * (axis.Dot(carUp) * (1f - cos));
        }

        private static Vec3 Unit(Vec3 v, Vec3 fallback)
        {
            float length = v.Length();
            return float.IsFinite(length) && length > 1e-4f ? v / length : fallback;
        }
    }
}
