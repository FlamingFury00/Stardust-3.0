using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>Planar kinematic car state used by ground rollouts.</summary>
    public struct GroundState
    {
        public Vec3 Position;
        public Vec3 Forward;   // flat unit heading
        public float Speed;    // signed forward speed
        public float YawRate;  // heading rate (rad/s), counter-clockwise positive
        public float Boost;
        public float Time;

        public GroundState(Vec3 position, Vec3 forward, float speed, float boost, float time = 0, float yawRate = 0)
        {
            YawRate = yawRate;
            Position = new Vec3(position.x, position.y, 0);
            Forward = new Vec3(forward.x, forward.y, 0).Normalize();
            if (Forward.Length() < 0.5f) Forward = Vec3.X;
            Speed = speed;
            Boost = boost;
            Time = time;
        }

        /// <summary>The car's right side: +90 degrees counter-clockwise from forward in the left-handed frame.</summary>
        public Vec3 Right => new Vec3(-Forward.y, Forward.x, 0);
        public Vec3 Velocity => Forward * Speed;
    }

    /// <summary>
    /// Longitudinal and turning dynamics of a grounded car, fitted to RocketSim: throttle torque
    /// curve, ground boost, coasting and braking, speed-dependent full-lock curvature, and the
    /// lateral-slip drag that bleeds speed in sustained turns.
    /// </summary>
    public static class GroundModel
    {
        /// <summary>Full-lock slip drag as a fraction of the full-lock lateral acceleration (RocketSim fit).</summary>
        public const float TurnDrag = 0.087f;
        /// <summary>Extra drag while the engine pushes through a turn below the 1410 uu/s throttle limit.</summary>
        public const float ThrottleTurnDrag = 80f;
        /// <summary>First-order time constant of the yaw rate following a steering change.</summary>
        public const float YawResponse = 0.075f;
        /// <summary>
        /// Braking against the direction of travel costs front grip: the car turns at about this
        /// fraction of its free-rolling yaw rate (physics-check brake-probe).
        /// </summary>
        public const float BrakingYawFactor = 0.75f;

        private static readonly float[] CurvatureSpeeds = { 0, 250, 500, 750, 1000, 1250, 1500, 1750, 2000, 2300 };
        private static readonly float[] CurvatureValues = { 0.00690f, 0.00530f, 0.00399f, 0.00318f, 0.00234f, 0.00186f, 0.00136f, 0.00111f, 0.000980f, 0.000856f };

        /// <summary>Full-lock curvature refitted to RocketSim (1/uu).</summary>
        public static float Curvature(float speed) => RL.Curve(MathF.Abs(speed), CurvatureSpeeds, CurvatureValues);

        public static float TurnRadius(float speed) => 1f / Curvature(speed);

        /// <summary>Highest speed whose full-lock turn is at least as tight as the requested curvature.</summary>
        /// <remarks>Curvature falls monotonically with speed, so this is the exact piecewise-linear inverse.</remarks>
        public static float SpeedForCurvature(float curvature) =>
            RL.Curve(MathF.Abs(curvature), CurvatureValues, CurvatureSpeeds);

        /// <summary>
        /// Longitudinal speed lost to tyre slip while steering. The slip part grows with the cube of
        /// the steer input and fades out at crawling speed; engine torque through a turn adds a
        /// roughly constant loss. Fitted to RocketSim steady-state turns (physics-check turn-drag).
        /// </summary>
        public static float TurnDragAccel(float speed, float steer, float throttle)
        {
            float s = MathF.Abs(steer);
            if (s < 1e-3f) return 0f;
            float v = MathF.Abs(speed);
            float lateral = Curvature(v) * v * v;
            float fade = System.Math.Clamp((v - 250f) / 450f, 0f, 1f);
            if (speed * throttle < 0f) lateral *= BrakingYawFactor;
            float slip = TurnDrag * s * s * s * lateral * fade;
            float engine = throttle > 0.01f && v < 1410f ? ThrottleTurnDrag * MathF.Min(1f, 2f * s) : 0f;
            return slip + engine;
        }

        /// <summary>Longitudinal acceleration for the given inputs.</summary>
        public static float Acceleration(float speed, float throttle, bool boost, float fuel, float steer)
        {
            float accel;
            bool boosting = boost && fuel > 0f && speed >= -1f;
            if (MathF.Abs(throttle) < 0.01f && !boosting)
            {
                // Coasting applies a light brake that snaps to a stop near zero.
                accel = -MathF.Sign(speed) * RL.CoastBrakeAccel;
                if (MathF.Abs(speed) < 25f) accel = -speed * RL.TickRate;
            }
            else if (speed * throttle < 0f && MathF.Abs(speed) > 25f)
            {
                accel = -MathF.Sign(speed) * RL.BrakeAccel;
            }
            else
            {
                accel = throttle * RL.ThrottleAccel(speed);
                if (boosting) accel += RL.BoostAccelGround;
            }

            accel -= MathF.Sign(speed) * TurnDragAccel(speed, steer, throttle);
            return accel;
        }

        /// <summary>Advances the state by dt under constant inputs (semi-implicit Euler with exact max-speed clamp).</summary>
        public static void Step(ref GroundState s, float throttle, float steer, bool boost, float dt)
        {
            float accel = Acceleration(s.Speed, throttle, boost, s.Boost, steer);
            float speed = System.Math.Clamp(s.Speed + accel * dt, -RL.CarMaxSpeed, RL.CarMaxSpeed);
            if (s.Speed != 0 && MathF.Sign(speed) != MathF.Sign(s.Speed) && MathF.Abs(throttle) < 0.01f)
                speed = 0f;
            float mean = 0.5f * (s.Speed + speed);
            // Positive steer turns toward the car's right, which is counter-clockwise about +z in
            // Rocket League's left-handed world (heading angle increases). The yaw rate lags the
            // steering command with a first-order response, as measured in RocketSim.
            bool braking = s.Speed * throttle < 0f && MathF.Abs(s.Speed) > 25f;
            float steady = steer * Curvature(mean) * mean * (braking ? BrakingYawFactor : 1f);
            float blend = 1f - MathF.Exp(-dt / YawResponse);
            float yawRate = s.YawRate + (steady - s.YawRate) * blend;
            float heading = 0.5f * (s.YawRate + yawRate) * dt;
            s.YawRate = yawRate;
            float arc = mean * dt;
            if (MathF.Abs(heading) > 1e-6f)
            {
                // Exact displacement along a circular arc with signed radius arc/heading.
                float radius = arc / heading;
                (float sin, float cos) = MathF.SinCos(heading);
                Vec3 f = s.Forward;
                Vec3 normal = new(-f.y, f.x, 0);
                s.Position += f * (radius * sin) + normal * (radius * (1 - cos));
                s.Forward = new Vec3(f.x * cos - f.y * sin, f.x * sin + f.y * cos, 0);
            }
            else
            {
                s.Position += s.Forward * arc;
            }
            if (boost && s.Boost > 0f && s.Speed >= -1f)
                s.Boost = MathF.Max(0f, s.Boost - RL.BoostPerSecond * dt);
            s.Speed = speed;
            s.Time += dt;
        }

        /// <summary>Rotates a flat vector counter-clockwise by angle (radians) about +z.</summary>
        public static Vec3 Rotate(Vec3 v, float angle)
        {
            (float s, float c) = MathF.SinCos(angle);
            return new Vec3(v.x * c - v.y * s, v.x * s + v.y * c, 0);
        }

        /// <summary>Signed flat angle from a to b, positive counter-clockwise about +z.</summary>
        public static float SignedAngle(Vec3 a, Vec3 b)
        {
            float cross = a.x * b.y - a.y * b.x;
            float dot = a.x * b.x + a.y * b.y;
            return MathF.Atan2(cross, dot);
        }
    }
}
