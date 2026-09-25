using System;

namespace RedUtils.Physics
{
    /// <summary>
    /// Rocket League physics constants (uu, seconds, radians) as implemented by RocketSim, which
    /// mirrors the game's Bullet setup. Every model in this namespace is validated against it by
    /// tools/simulator (physics-check).
    /// </summary>
    public static class RL
    {
        public const float TickRate = 120f;
        public const float TickTime = 1f / TickRate;
        public const float Gravity = -650f;

        public const float CarMass = 180f;
        public const float BallMass = 30f;
        public const float BallRadius = 91.25f;
        public const float BallRestZ = 93.15f;
        public const float BallMaxSpeed = 6000f;
        public const float BallMaxAngularSpeed = 6f;
        public const float BallDrag = 0.03f;
        public const float BallRestitution = 0.6f;

        public const float CarMaxSpeed = 2300f;
        public const float CarMaxAngularSpeed = 5.5f;
        public const float SupersonicSpeed = 2200f;

        public const float BoostPerSecond = 100f / 3f;
        public const float BoostMinimumTime = 0.1f;
        public const float BoostAccelGround = 2975f / 3f;
        public const float BoostAccelAir = 3175f / 3f;
        public const float ThrottleAccelMax = 1600f;
        /// <summary>Speed above which throttle alone no longer accelerates.</summary>
        public const float ThrottleMaxSpeed = 1410f;
        public const float AirThrottleAccel = 200f / 3f;
        public const float BrakeAccel = 3500f;
        public const float CoastBrakeAccel = 525f;
        public const float StickyAccel = 325f;

        public const float JumpImpulse = 875f / 3f;
        public const float JumpHoldAccel = 4375f / 3f;
        public const float DoubleJumpMaxDelay = 1.25f;

        public const float DodgeImpulse = 500f;
        public const float DodgeSideMaxScale = 1.9f;
        public const float DodgeBackwardMaxScale = 2.5f;
        public const float DodgeBackwardScaleX = 16f / 15f;
        public const float DodgeDeadzone = 0.5f;

        /// <summary>Air control torque (pitch, yaw, roll) and damping, expressed as angular accelerations.</summary>
        public const float TorqueScale = 2f * MathF.PI / 65536f * 1000f;
        public const float PitchTorque = 130f * TorqueScale;
        public const float YawTorque = 95f * TorqueScale;
        public const float RollTorque = 400f * TorqueScale;
        public const float PitchDamping = 30f * TorqueScale;
        public const float YawDamping = 20f * TorqueScale;
        public const float RollDamping = 50f * TorqueScale;

        public const float BallCarExtraZScale = 0.35f;
        public const float BallCarExtraForwardScale = 0.65f;
        public const float BallCarExtraMaxDelta = 4600f;
        public const float CarBallFriction = 2f;

        /// <summary>
        /// Path curvature (1/radius) at full steer for a given forward speed, measured in game.
        /// </summary>
        public static float MaxCurvature(float speed) => Curve(MathF.Abs(speed),
            stackalloc float[] { 0, 500, 1000, 1500, 1750, 2300 },
            stackalloc float[] { 0.0069f, 0.00398f, 0.00235f, 0.001375f, 0.0011f, 0.00088f });

        /// <summary>Highest forward speed that still allows a turn of the given curvature.</summary>
        public static float SpeedForCurvature(float curvature)
        {
            curvature = System.Math.Clamp(curvature, 0.00088f, 0.0069f);
            return Curve(curvature,
                stackalloc float[] { 0.00088f, 0.0011f, 0.001375f, 0.00235f, 0.00398f, 0.0069f },
                stackalloc float[] { 2300, 1750, 1500, 1000, 500, 0 });
        }

        /// <summary>Full-throttle engine acceleration at a forward speed (the torque factor curve).</summary>
        public static float ThrottleAccel(float speed)
        {
            float s = MathF.Abs(speed);
            if (s >= 1410f) return 0f;
            if (s >= 1400f) return ThrottleAccelMax * 0.1f * (1410f - s) / 10f;
            return ThrottleAccelMax * (1f - 0.9f * s / 1400f);
        }

        /// <summary>Scale of the extra car-ball impulse as a function of relative speed.</summary>
        public static float ExtraImpulseFactor(float relativeSpeed) => Curve(relativeSpeed,
            stackalloc float[] { 0, 500, 2300, 4600 },
            stackalloc float[] { 0.65f, 0.65f, 0.55f, 0.30f });

        /// <summary>Piecewise linear interpolation, clamped at both ends.</summary>
        public static float Curve(float x, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys)
        {
            bool increasing = xs[^1] >= xs[0];
            if (increasing ? x <= xs[0] : x >= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
            {
                if (increasing ? x <= xs[i] : x >= xs[i])
                {
                    float t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * t;
                }
            }
            return ys[^1];
        }
    }
}
