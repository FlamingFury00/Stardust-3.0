using System;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;

namespace Bot
{
    /// <summary>Where and when a falling ball can be met on the roof.</summary>
    public sealed class CatchPlan
    {
        /// <summary>Time the ball comes down to roof height.</summary>
        public float Time;
        /// <summary>Ball position (flat) and velocity at that moment.</summary>
        public Vec3 Location, Velocity;
        /// <summary>Heading to arrive with: along the ball's horizontal travel, or none for a near-vertical drop.</summary>
        public Vec3 Heading;

        /// <summary>
        /// Heading agreement (cosine) the arrival needs: a car off the ball's line by angle a moves
        /// 2·v·sin(a/2) sideways relative to a ball of horizontal speed v, and the roof absorbs
        /// about 150 uu/s of that.
        /// </summary>
        public float Alignment
        {
            get
            {
                float speed = Velocity.Flatten().Length();
                float angle = speed > 150f ? 2f * MathF.Asin(MathF.Min(1f, 75f / speed)) : MathF.PI;
                return MathF.Cos(MathF.Min(angle, MathF.PI / 3f));
            }
        }
    }

    /// <summary>
    /// Cushion catch: be under a dropping ball at the moment it reaches roof height, moving with it.
    /// The catch commits to one descent and re-reads its time and place from the prediction every
    /// tick. Along the approach a minimum-effort law (the acceleration that meets both the arrival
    /// point and the arrival speed at the arrival time) drives the <see cref="SpeedActuator"/>, and
    /// the navigator steers onto the ball's line of travel. In the last moments the hood carry takes
    /// over, matching the ball's horizontal motion through the roof bounces, and once the ball rests
    /// on the roof the action hands straight over to <see cref="GroundDribble"/>.
    /// </summary>
    public sealed class GroundCatch : IPossessionAction
    {
        /// <summary>Time before contact at which the hood carry takes over the approach.</summary>
        private const float FinalPhase = 0.3f;
        /// <summary>Spare time a catch must leave over the flat-out arrival.</summary>
        private const float Margin = 0.15f;
        /// <summary>How far the committed descent may move in the prediction before the catch is off.</summary>
        private const float Drift = 180f;
        private readonly HoodCarry carry = new();
        private readonly SpeedActuator actuator = new();
        private readonly float started = Game.Time;
        private CatchPlan plan;
        private bool touched;

        public bool Finished { get; private set; }
        public bool Interruptible => true;
        public float ClaimTime => plan?.Time ?? Game.Time;

        public GroundCatch(CatchPlan plan = null) { this.plan = plan; }

        /// <summary>Ball centre height above the car origin when resting on the roof.</summary>
        public static float RoofRest(Car car) => car.HitboxSize.z > 1f
            ? car.HitboxOffset.z + car.HitboxSize.z * 0.5f + Ball.Radius
            : 131.3f;

        /// <summary>
        /// Earliest descent to roof height, within 2.5 s, that the car can drive under in time: the
        /// full-throttle travel time along the approach (with the turn onto the ball's line) must
        /// fit with a margin.
        /// </summary>
        public static CatchPlan FindCatch(Car car, Vec3 lane)
        {
            if (car == null || !car.IsGrounded || car.Up.z < 0.9f || Ball.Prediction.Slices == null)
                return null;
            float contact = car.Location.z + RoofRest(car);
            BallSlice previous = null;
            foreach (BallSlice slice in Ball.Prediction.Slices)
            {
                if (slice == null || slice.Time <= Game.Time) continue;
                if (slice.Time - Game.Time > 2.5f) break;
                if (previous != null && previous.Location.z > contact && slice.Location.z <= contact && slice.Velocity.z < -50f)
                {
                    CatchPlan plan = Plan(previous, slice, contact, lane);
                    if (Reachable(car, plan)) return plan;
                }
                previous = slice;
            }
            return null;
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            if (!car.IsGrounded || Game.Time - started > 3.2f)
            {
                Finished = true;
                return;
            }
            Vec3 lane = ControlMath.FlatUnit(bot.TheirGoal.Location - Ball.Location, car.Forward);
            float contact = car.Location.z + RoofRest(car);

            // Once the ball has come down onto the roof, keep it there and settle it into a carry.
            Vec3 local = car.Local(Ball.Location - car.Location);
            touched |= bot.OwnTouchThisTick && local.z > RoofRest(car) - 40f;
            if (touched)
            {
                if (local.z < RoofRest(car) - 40f || MathF.Abs(local.x) > 130f || MathF.Abs(local.y) > 110f)
                {
                    Finished = true;
                    return;
                }
                bool resting = MathF.Abs(Ball.Velocity.z - car.Velocity.z) < 120f && local.z < RoofRest(car) + 25f;
                if (resting)
                {
                    bot.Action = new GroundDribble(carry);
                    bot.Action.Run(bot);
                    return;
                }
                carry.SteerToward(car, Ball.MainBall, lane);
                bot.Controller = carry.Step(car, Ball.MainBall, bot.DeltaTime, allowBoost: true);
                return;
            }

            CatchPlan next = Refresh(contact, lane);
            if (next == null || (plan != null && next.Location.Dist(plan.Location) > Drift))
            {
                Finished = true;
                return;
            }
            plan = next;
            float remaining = plan.Time - Game.Time;
            if (remaining < FinalPhase)
            {
                carry.SteerToward(car, Ball.MainBall, lane);
                bot.Controller = carry.Step(car, Ball.MainBall, bot.DeltaTime, allowBoost: true);
                return;
            }

            // The navigator's timed, directed arrival: flat out while late, shedding speed while
            // early, braking down to the ball's speed along its line of travel.
            DriveTarget target = Target(plan);
            DriveCommand command = Navigator.Control(car.Location, car.Forward, car.Velocity.Dot(car.Forward), car.Boost,
                car.AngularVelocity.z, target, Game.Time);
            bot.Controller.Throttle = command.Throttle;
            bot.Controller.Steer = command.Steer;
            bot.Controller.Boost = command.Boost;
            bot.Controller.Handbrake = command.Handbrake;
        }

        /// <summary>The committed descent, re-read from the current prediction: the crossing nearest its time.</summary>
        private CatchPlan Refresh(float contact, Vec3 lane)
        {
            if (Ball.Prediction.Slices == null) return null;
            CatchPlan nearest = null;
            BallSlice previous = null;
            foreach (BallSlice slice in Ball.Prediction.Slices)
            {
                if (slice == null || slice.Time <= Game.Time) continue;
                if (plan != null && slice.Time > plan.Time + 0.3f) break;
                if (previous != null && previous.Location.z > contact && slice.Location.z <= contact && slice.Velocity.z < -50f)
                {
                    CatchPlan crossing = Plan(previous, slice, contact, lane);
                    if (plan == null) return crossing;
                    if (nearest == null || MathF.Abs(crossing.Time - plan.Time) < MathF.Abs(nearest.Time - plan.Time))
                        nearest = crossing;
                }
                previous = slice;
            }
            return nearest;
        }

        private static CatchPlan Plan(BallSlice above, BallSlice below, float contact, Vec3 lane)
        {
            float t = (above.Location.z - contact) / MathF.Max(above.Location.z - below.Location.z, 1e-3f);
            Vec3 location = above.Location + (below.Location - above.Location) * t;
            Vec3 velocity = above.Velocity + (below.Velocity - above.Velocity) * t;
            Vec3 flat = velocity.Flatten();
            return new CatchPlan
            {
                Time = above.Time + (below.Time - above.Time) * t,
                Location = location.Flatten(),
                Velocity = velocity,
                Heading = flat.Length() > 250f ? flat.Normalize() : Vec3.Zero,
            };
        }

        /// <summary>Arrive with the hold spot under the ball, heading along its travel at its speed.</summary>
        private static DriveTarget Target(CatchPlan plan) =>
            new(plan.Location - plan.Heading * HoodCarry.HoldSpot, plan.Heading, plan.Time, allowBoost: true, maxLead: 900f)
            {
                ArrivalSpeed = plan.Velocity.Flatten().Length(),
            };

        /// <summary>Whether the navigator's flat-out rollout reaches the arrival line with time to spare.</summary>
        private static bool Reachable(Car car, CatchPlan plan)
        {
            float time = plan.Time - Game.Time;
            DriveTarget asap = Target(plan);
            asap.ArrivalTime = float.NaN;
            asap.ArrivalSpeed = float.NaN;
            RolloutResult rollout = Navigator.Rollout(Navigator.StartState(car), asap, time, dt: 1f / 30f,
                alignment: plan.Alignment);
            return rollout.Arrived && rollout.Time < time - Margin;
        }
    }
}
