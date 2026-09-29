using System;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;

namespace RedUtils
{
    /// <summary>
    /// Flies a planned aerial touch with <see cref="AerialGuidance"/>, the law the planner
    /// simulated: a fast aerial takeoff from the ground, then closed-loop thrust toward the point
    /// one nose-length behind the ball at the contact time, re-targeted on the live prediction.
    /// </summary>
    public class AerialStrike : Shot, IStrike
    {
        private const float ResolveInterval = 1f / 30f;
        private const float MaxDeviation = 150f;
        /// <summary>Required acceleration, as a share of boost thrust, beyond which the touch is lost.</summary>
        private const float Unreachable = 1.35f;

        public override bool Finished { get; internal set; }
        public override bool Interruptible { get; internal set; } = true;
        public override BallSlice Slice { get; internal set; }
        public override Vec3 ShotTarget { get; internal set; }
        public override Vec3 TargetLocation { get; internal set; }
        public override Vec3 ShotDirection { get; internal set; }

        public StrikePlan Plan { get; }
        public string Status { get; private set; } = "takeoff";

        /// <summary>Optional per-tick diagnostics sink for mechanics debugging (null in play).</summary>
        public static Action<string> Diagnostics;

        private readonly Vec3 nose;
        private readonly Vec3 offset;
        private float sinceTakeoff = float.NaN;
        private bool secondJumpDone;
        private float nextResolve = float.NegativeInfinity;
        private float lastTouch = float.NaN;
        private float strained;

        public AerialStrike(StrikePlan plan)
        {
            Plan = plan;
            Slice = plan.Slice;
            ShotTarget = plan.Aim;
            nose = plan.Contact.Heading;
            TargetLocation = plan.Contact.CarPosition;
            offset = plan.Contact.CarPosition - plan.Slice.Location;
            ShotDirection = plan.Contact.BallVelocity.Normalize();
        }

        public override bool IsValid(Car car) => Plan != null;

        public override void Run(RUBot bot)
        {
            Car car = bot.Me;
            float now = Game.Time;
            float remaining = Slice.Time - now;
            float touch = Ball.LatestTouch?.Time ?? -1f;
            if (float.IsNaN(lastTouch)) lastTouch = touch;

            if (float.IsNaN(sinceTakeoff))
                sinceTakeoff = car.IsGrounded ? 0f : -1f;

            if (touch != lastTouch)
            {
                Finish(remaining < 0.1f ? "done" : "touched");
                return;
            }
            if (remaining < -0.1f || (sinceTakeoff > 0.4f && car.IsGrounded))
            {
                Finish("done");
                return;
            }

            if (now >= nextResolve && remaining > 0.05f)
            {
                nextResolve = now + ResolveInterval;
                BallSlice live = new BallPath(Ball.Prediction.Slices).SliceAt(Slice.Time);
                if ((live.Location - Slice.Location).Length() > MaxDeviation)
                {
                    Finish("prediction moved");
                    return;
                }
                Slice = live;
                TargetLocation = live.Location + offset;
            }

            FlightState state = FlightState.From(car);
            bool doubleJump = Plan.DoubleJump && !secondJumpDone;
            AerialCommand command = AerialGuidance.Control(state, remaining, TargetLocation, nose, sinceTakeoff, doubleJump);
            if (doubleJump && command.Jump && sinceTakeoff >= AerialGuidance.SecondJumpAt) secondJumpDone = true;

            bot.Controller.Pitch = command.Input.Pitch;
            bot.Controller.Yaw = command.Input.Yaw;
            bot.Controller.Roll = command.Input.Roll;
            bot.Controller.Boost = command.Boost;
            bot.Controller.Jump = command.Jump;
            bot.Controller.Throttle = command.Throttle;
            bot.Controller.Handbrake = false;
            Interruptible = false;
            Status = sinceTakeoff >= 0f && sinceTakeoff < AerialGuidance.SecondJumpAt ? "takeoff" : "flying";

            // Stand down when the touch needs more than boost can give for a sustained moment.
            float needed = AerialGuidance.RequiredAcceleration(state, remaining, TargetLocation, sinceTakeoff, doubleJump).Length();
            strained = needed > Unreachable * RL.BoostAccelAir && remaining > 0.2f ? strained + bot.DeltaTime : 0f;
            if (strained > 0.1f || (car.Boost <= 0f && needed > 0.5f * RL.BoostAccelAir && remaining > 0.3f))
                Finish(FormattableString.Invariant($"unreachable need={needed:F0}"));

            Diagnostics?.Invoke(FormattableString.Invariant(
                $"aerial tick t={now:F3} remaining={remaining:F3} miss={(TargetLocation - car.Location).Length():F0} need={needed:F0} boost={command.Boost} jump={command.Jump} fuel={car.Boost:F0}"));
            if (sinceTakeoff >= 0f) sinceTakeoff += bot.DeltaTime;
        }

        private void Finish(string reason)
        {
            Status = reason;
            Finished = true;
            Interruptible = true;
        }
    }
}
