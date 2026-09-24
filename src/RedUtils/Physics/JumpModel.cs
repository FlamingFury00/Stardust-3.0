using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>
    /// Vertical motion of a jump from flat ground, fitted to RocketSim: the first three ticks (the
    /// minimum jump) lift the car to 24.5 uu at 298 uu/s, holding jump then adds a net 808.3 uu/s^2
    /// until at most 0.2 s, after which only gravity acts. A double jump adds 291.67 uu/s along the
    /// car's up axis. Heights are for the car origin (resting at 17 uu).
    /// </summary>
    public static class JumpModel
    {
        public const float RestHeight = 17f;
        public const float MinimumTime = 0.025f;
        public const float MaximumHold = 0.2f;
        public const float HeightAfterMinimum = 24.5f;
        public const float SpeedAfterMinimum = 298f;
        public const float HoldAcceleration = RL.JumpHoldAccel + RL.Gravity;

        /// <summary>Height and vertical speed after <paramref name="t"/> seconds, holding jump for <paramref name="hold"/>.</summary>
        public static (float Height, float Speed) Single(float t, float hold = MaximumHold)
        {
            hold = System.Math.Clamp(hold, MinimumTime, MaximumHold);
            if (t <= 0f) return (RestHeight, 0f);
            if (t < MinimumTime)
            {
                float f = t / MinimumTime;
                return (RestHeight + (HeightAfterMinimum - RestHeight) * f, SpeedAfterMinimum);
            }
            float held = MathF.Min(t, hold) - MinimumTime;
            float z = HeightAfterMinimum + SpeedAfterMinimum * held + 0.5f * HoldAcceleration * held * held;
            float v = SpeedAfterMinimum + HoldAcceleration * held;
            if (t > hold)
            {
                float free = t - hold;
                z += v * free + 0.5f * RL.Gravity * free * free;
                v += RL.Gravity * free;
            }
            return (z, v);
        }

        /// <summary>Full-hold jump followed by a double jump at <paramref name="secondAt"/> seconds.</summary>
        public static (float Height, float Speed) Double(float t, float secondAt = MaximumHold + 2f / RL.TickRate)
        {
            if (t <= secondAt) return Single(t);
            var (z, v) = Single(secondAt);
            v += RL.JumpImpulse;
            float free = t - secondAt;
            return (z + v * free + 0.5f * RL.Gravity * free * free, v + RL.Gravity * free);
        }

        /// <summary>Highest point of a single jump with the given hold time.</summary>
        public static float PeakSingle(float hold = MaximumHold)
        {
            var (z, v) = Single(hold, hold);
            return z + v * v / (2f * -RL.Gravity);
        }

        public static float PeakDouble()
        {
            var (z, v) = Double(MaximumHold + 2f / RL.TickRate + 1e-4f);
            return z + v * v / (2f * -RL.Gravity);
        }

        /// <summary>
        /// Earliest rising time at which the car origin reaches <paramref name="height"/> with a
        /// full-hold single (or double) jump, or NaN when unreachable.
        /// </summary>
        public static float TimeToHeight(float height, bool doubleJump)
        {
            if (height <= RestHeight) return 0f;
            float peakTime = doubleJump ? 1.25f : 0.87f;
            if (height > (doubleJump ? PeakDouble() : PeakSingle()) - 0.5f) return float.NaN;
            float lo = 0f, hi = peakTime;
            for (int i = 0; i < 24; i++)
            {
                float mid = 0.5f * (lo + hi);
                float z = doubleJump ? Double(mid).Height : Single(mid).Height;
                if (z < height) lo = mid; else hi = mid;
            }
            return hi;
        }

        /// <summary>
        /// Hold duration that makes a single jump pass through <paramref name="height"/> at exactly
        /// <paramref name="time"/> after takeoff while still rising or near the apex, or NaN.
        /// </summary>
        public static float HoldForHeightAtTime(float height, float time)
        {
            if (time < MinimumTime) return float.NaN;
            float lo = MinimumTime, hi = MaximumHold;
            float zLo = Single(time, lo).Height, zHi = Single(time, hi).Height;
            if (height < zLo - 1f || height > zHi + 1f) return float.NaN;
            for (int i = 0; i < 20; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Single(time, mid).Height < height) lo = mid; else hi = mid;
            }
            return hi;
        }
    }

    /// <summary>Velocity change of a dodge, exactly as Rocket League applies it (see RocketSim Car.cpp).</summary>
    public static class DodgeModel
    {
        /// <summary>
        /// Impulse for a dodge with stick input (pitch, yaw) at forward speed <paramref name="forwardSpeed"/>.
        /// Returned in world space, using the car's flattened forward and right axes.
        /// </summary>
        public static Vec3 Impulse(Vec3 carForward, float pitch, float yaw, float forwardSpeed)
        {
            // Below the dodge deadzone the second jump press is a plain double jump.
            if (MathF.Abs(pitch) + MathF.Abs(yaw) < RL.DodgeDeadzone) return Vec3.Zero;
            // Dodge direction in (forward, right): forward is -pitch, right is yaw (+ roll).
            float dx = -pitch, dy = yaw;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (MathF.Abs(dy) < 0.1f && MathF.Abs(dx) < 0.1f) return Vec3.Zero;
            dx /= length;
            dy /= length;
            if (MathF.Abs(dx) < 0.1f) dx = 0f;
            if (MathF.Abs(dy) < 0.1f) dy = 0f;

            float speedRatio = MathF.Abs(forwardSpeed) / RL.CarMaxSpeed;
            bool backwards = MathF.Abs(forwardSpeed) < 100f ? dx < 0f : (dx >= 0f) != (forwardSpeed >= 0f);
            float x = dx * RL.DodgeImpulse, y = dy * RL.DodgeImpulse;
            float maxScaleX = backwards ? RL.DodgeBackwardMaxScale : 1f;
            x *= (maxScaleX - 1f) * speedRatio + 1f;
            y *= (RL.DodgeSideMaxScale - 1f) * speedRatio + 1f;
            if (backwards) x *= RL.DodgeBackwardScaleX;

            Vec3 forward = new Vec3(carForward.x, carForward.y, 0).Normalize();
            Vec3 right = new(-forward.y, forward.x, 0);
            return forward * x + right * y;
        }

        /// <summary>
        /// Stick input (pitch, yaw) whose impulse points along a flat world direction, compensating
        /// for the speed-dependent side and backward impulse scaling.
        /// </summary>
        public static (float Pitch, float Yaw) InputToward(Vec3 carForward, Vec3 direction, float forwardSpeed)
        {
            Vec3 forward = new Vec3(carForward.x, carForward.y, 0).Normalize();
            Vec3 right = new(-forward.y, forward.x, 0);
            Vec3 flat = new Vec3(direction.x, direction.y, 0).Normalize();
            float along = flat.Dot(forward), side = flat.Dot(right);
            float speedRatio = MathF.Abs(forwardSpeed) / RL.CarMaxSpeed;
            bool backwards = MathF.Abs(forwardSpeed) < 100f ? along < 0f : (along >= 0f) != (forwardSpeed >= 0f);
            float scaleX = backwards ? ((RL.DodgeBackwardMaxScale - 1f) * speedRatio + 1f) * RL.DodgeBackwardScaleX : 1f;
            float scaleY = (RL.DodgeSideMaxScale - 1f) * speedRatio + 1f;
            float x = along / scaleX, y = side / scaleY;
            float scale = 1f / MathF.Max(MathF.Max(MathF.Abs(x), MathF.Abs(y)), 1e-4f);
            return (-x * scale, y * scale);
        }
    }
}
