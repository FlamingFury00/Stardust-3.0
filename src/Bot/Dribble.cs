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

    public sealed class GroundDribble : IPossessionAction
    {
        private readonly float started = Game.Time;
        private readonly HoodCarry carry;
        private float stableSince = float.NaN;
        public bool Finished { get; private set; }
        public bool Interruptible => true;
        public float ClaimTime => Game.Time + 0.25f;

        /// <param name="carry">Carry controller to continue with, e.g. from the catch that settled the ball.</param>
        public GroundDribble(HoodCarry carry = null) { this.carry = carry ?? new HoodCarry(); }

        public static bool CanStart(Car car, Ball ball, float freeTime)
        {
            if (car == null || ball == null || !car.IsGrounded || car.Up.z <= 0.9f)
                return false;

            Vec3 local = car.Local(ball.location - car.Location);
            float relativeSpeed = (ball.velocity - car.Velocity).Length();
            bool alreadyControlled = PossessionControl.HasControlledPossession(car, ball);
            return ball.location.z < 300f &&
                local.x > -105f && local.x < 590f && MathF.Abs(local.y) < 195f &&
                relativeSpeed < 1100f && (alreadyControlled || freeTime > 0.10f);
        }
        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            Vec3 offset = Ball.Location - car.Location;
            if (!car.IsGrounded || offset.Length() > 850f || Game.Time - started > 8f || Ball.Location.z > 365f)
            { Finished = true; return; }

            Vec3 lane = PossessionControl.AttackingLane(
                car, Ball.MainBall, bot.LivingOpponents,
                bot.TheirGoal.Location, bot.OurGoal.Location);
            bool carried = PossessionControl.HasControlledPossession(car, Ball.MainBall);
            stableSince = carried ? (float.IsFinite(stableSince) ? stableSince : Game.Time) : float.NaN;
            if (carried && Game.Time - stableSince >= 0.06f &&
                PossessionControl.PlanFlick(car, Ball.MainBall, lane, bot.LivingOpponents,
                    bot.OurGoal.Location, bot.TheirGoal.Location, out Vec3 aim) is FlickKind kind)
            {
                bot.Action = new Flick(kind, aim, carry, urgent: true);
                bot.Action.Run(bot);
                return;
            }

            // With a challenger on the way, carry the ball where the flick starts, so it can go at once.
            PossessionControl.Challenge challenge = PossessionControl.MostImminent(Ball.MainBall, lane, bot.LivingOpponents);
            carry.SteerToward(car, Ball.MainBall, lane);
            if (challenge.Committed)
                carry.Spot = new Vec3(FlickRecipe.For(FlickKind.Power).Spot, carry.Spot.y, 0);

            // Stay on the ball under pressure. The pressure response is the flick above, not abandoning
            // possession and driving back into a shadow lane.
            bot.Controller = carry.Step(car, Ball.MainBall, bot.DeltaTime, allowBoost: true);
        }
    }
}
