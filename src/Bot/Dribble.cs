using System;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;
using RLBot.Flat;

namespace Bot
{
    /// <summary>
    /// Keeps a ball balanced on the roof. The ball's velocity is the slow state of a carry: the roof
    /// can only nudge it through friction, so the car's job is to stay underneath. Position and
    /// velocity errors of the ball relative to its target spot on the roof become the car's
    /// forward and sideways accelerations (a PD law on the relative motion). The forward part is
    /// realised by <see cref="SpeedActuator"/>, the sideways part by steering, since a car at speed
    /// v turning at curvature k accelerates sideways at k·v².
    ///
    /// The target spot sits just ahead of the car's origin. Every car-ball contact adds an impulse
    /// pointing from the origin to the ball (RocketSim's extra hit impulse), so a ball far forward
    /// is pushed forward on every touch and runs away. Moving the spot sideways, the same impulse
    /// steers the ball, which is how the carry turns onto its lane.
    /// </summary>
    public sealed class HoodCarry
    {
        /// <summary>Forward offset of the ball over the origin at which the carry holds speed.</summary>
        public const float HoldSpot = 4f;
        private const float PositionGain = 50f, VelocityGain = 17f;
        private const float Lookahead = 1f / 60f;
        private readonly SpeedActuator actuator = new();

        /// <summary>Target spot on the roof, car frame (x forward, y right), relative to the origin.</summary>
        public Vec3 Spot = new(HoldSpot, 0, 0);

        public void Reset() => actuator.Reset();

        /// <summary>
        /// Sideways spot offset per radian of lane error, and its limit. Tuned in the mechanics lab
        /// (carry drill; sweep with --set HoodCarry.LaneGain=...).
        /// </summary>
        private static float LaneGain = 120f, LaneLimit = 40f;

        /// <summary>
        /// Places the spot so the carry turns onto the lane: the ball rides on the side it should
        /// turn toward, where each contact nudges it that way.
        /// </summary>
        public void SteerToward(Car car, Ball ball, Vec3 lane)
        {
            Vec3 heading = ball.velocity.Flatten().Length() > 150f ? ball.velocity.Flatten() : car.Forward.Flatten();
            float error = GroundModel.SignedAngle(heading, lane.Flatten());
            Spot = new Vec3(HoldSpot, System.Math.Clamp(LaneGain * error, -LaneLimit, LaneLimit), 0);
        }

        public ControllerStateT Step(Car car, Ball ball, float dt, bool allowBoost)
        {
            var controls = new ControllerStateT();
            Vec3 relative = ball.location - car.Location;
            Vec3 relativeVelocity = ball.velocity - car.Velocity;
            Vec3 predicted = car.Local(relative + relativeVelocity * Lookahead);
            Vec3 localVelocity = car.Local(relativeVelocity);
            float errorX = predicted.x - Spot.x, errorY = predicted.y - Spot.y;

            float speed = car.Velocity.Dot(car.Forward);
            float lateral = PositionGain * errorY + VelocityGain * localVelocity.y;
            controls.Steer = Steer(speed, lateral);

            float forward = PositionGain * errorX + VelocityGain * localVelocity.x;
            LongitudinalInput input = actuator.Step(speed, forward, car.Boost, allowBoost, controls.Steer, dt);
            controls.Throttle = input.Throttle;
            controls.Boost = input.Boost;
            return controls;
        }

        /// <summary>Steer that yields the requested sideways acceleration (toward the car's right).</summary>
        private static float Steer(float speed, float lateral)
        {
            float v = MathF.Abs(speed);
            // A crawling car cannot move sideways: steering only swings the roof out from under
            // the ball. Fade the correction in as the car gets rolling.
            float rolling = System.Math.Clamp((v - 60f) / 240f, 0f, 1f);
            float authority = GroundModel.Curvature(v) * MathF.Max(v * v, 300f * 300f);
            return ControlRuntime.Axis((speed >= 0f ? 1f : -1f) * rolling * lateral / authority);
        }
    }
}
