using System;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;

namespace RedUtils
{
    /// <summary>
    /// Executes a planned ground, jump, or double-jump touch. The car drives to the contact's ground
    /// position on the planned heading, timed to arrive exactly at contact; jump strikes take off the
    /// model's rise time before contact, since a jump leaves horizontal motion unchanged. The
    /// contact is re-solved against the live prediction, and the strike stands down as soon as it
    /// can no longer make the touch.
    /// </summary>
    public class DrivenStrike : Shot
    {
        private const float ResolveInterval = 1f / 30f;
        private const float MaxDeviation = 120f;
        /// <summary>How close to the contact line (uu) the car must be to start its run-up.</summary>
        private const float LineUpTolerance = 60f;
        /// <summary>Largest takeoff error (uu) accepted beyond what in-flight boost can correct.</summary>
        private const float TakeoffTolerance = 60f;
        /// <summary>Predicted lateness beyond which the strike stands down.</summary>
        private const float LateTolerance = 0.1f;

        public override bool Finished { get; internal set; }
        public override bool Interruptible { get; internal set; } = true;
        public override BallSlice Slice { get; internal set; }
        public override Vec3 ShotTarget { get; internal set; }
        public override Vec3 TargetLocation { get; internal set; }
        public override Vec3 ShotDirection { get; internal set; }

        /// <summary>Optional per-tick diagnostics sink for mechanics debugging (null in play).</summary>
        public static Action<string> Diagnostics;

        public StrikePlan Plan { get; }
        public StrikeKind Kind => Plan.Kind;
        public string Status { get; private set; } = "approach";

        private readonly Vec3 heading;
        private readonly CarGeometry geometry;
        private readonly float contactHeight;
        private float nextResolve = float.NegativeInfinity;
        private bool runningUp;
        private float lastTouch = float.NaN;
        private float jumpStarted = float.NaN;
        private bool secondJumpSent;

        public DrivenStrike(Car car, StrikePlan plan)
        {
            Plan = plan;
            Slice = plan.Slice;
            ShotTarget = plan.Aim;
            heading = plan.Contact.Heading;
            TargetLocation = plan.Contact.CarPosition;
            contactHeight = plan.Contact.CarPosition.z;
            ShotDirection = new Vec3(plan.Contact.BallVelocity.x, plan.Contact.BallVelocity.y, 0).Normalize();
            geometry = CarGeometry.Of(car);
        }

        public bool Airborne => float.IsFinite(jumpStarted);

        public override bool IsValid(Car car) => Plan != null && Plan.Contact.Valid;

        public override void Run(RUBot bot)
        {
            Car car = bot.Me;
            float now = Game.Time;
            float remaining = Slice.Time - now;
            if (float.IsNaN(lastTouch)) lastTouch = Ball.LatestTouch?.Time ?? -1f;

            float touch = Ball.LatestTouch?.Time ?? -1f;
            if (touch != lastTouch && remaining > 0.05f && !Airborne)
            {
                Finish("touched");
                return;
            }
            if (remaining < -0.15f || (Airborne && car.IsGrounded && now - jumpStarted > 0.25f))
            {
                Finish("done");
                return;
            }

            if (Airborne)
            {
                Fly(bot, car, now);
                return;
            }

            if (now >= nextResolve && remaining > 0.02f)
            {
                nextResolve = now + ResolveInterval;
                var path = new BallPath(Ball.Prediction.Slices);
                BallSlice live = path.SliceAt(Slice.Time);
                if ((live.Location - Slice.Location).Length() > MaxDeviation)
                {
                    Finish("prediction moved");
                    return;
                }
                Slice = live;
                // Solve with the speed the car will have at contact, exactly as planned: jump
                // strikes carry their cruise speed, ground strikes arrive at the planned speed.
                float speed = Plan.ContactSpeed;
                float vertical = VerticalSpeedAtContact();
                ContactSolution contact = Contact.Aim(live.Location, live.Velocity, heading,
                    heading * speed + new Vec3(0, 0, vertical), contactHeight, ShotTarget, geometry);
                if (contact.Valid)
                    TargetLocation = contact.CarPosition;
            }

            if (!car.IsGrounded)
            {
                // Landing before the approach: wheels down, nose along the approach.
                AirInput air = AirControl.Orient(car.Forward, car.Right, car.Up, LocalAngular(car), heading, Vec3.Up);
                bot.Controller.Pitch = air.Pitch;
                bot.Controller.Yaw = air.Yaw;
                bot.Controller.Roll = air.Roll;
                bot.Controller.Throttle = 1f;
                Interruptible = true;
                return;
            }

            float forwardSpeed = car.Velocity.Dot(car.Forward);
            Interruptible = remaining > 0.25f + Plan.JumpTime;
            if (Kind == StrikeKind.Ground)
                DriveThrough(bot, car, now, remaining, forwardSpeed);
            else
                RunUpAndJump(bot, car, now, remaining, forwardSpeed);
        }

        /// <summary>Ground strike: drive through the contact on time, arriving as fast as possible.</summary>
        private void DriveThrough(RUBot bot, Car car, float now, float remaining, float forwardSpeed)
        {
            var target = new DriveTarget(TargetLocation, heading, Slice.Time, true);
            DriveCommand command = Navigator.Control(car.Location, car.Forward, forwardSpeed, car.Boost,
                car.AngularVelocity.z, target, now);
            Apply(bot, command);
            Diagnostics?.Invoke(FormattableString.Invariant(
                $"strike tick t={now:F3} {Kind} remaining={remaining:F3} speed={forwardSpeed:F0} thr={command.Throttle:F2} st={command.Steer:F2} boost={command.Boost}"));

            if (remaining > 0.25f && now >= nextResolve - ResolveInterval * 0.5f)
            {
                // Give up when even flat-out driving can no longer make the touch, using the same
                // rollout the planner accepted the strike with. Model noise is a few hundredths of
                // a second, so only a clear shortfall ends the strike.
                var asap = target;
                asap.ArrivalTime = float.NaN;
                RolloutResult check = Navigator.Rollout(Navigator.StartState(car), asap, remaining + 0.3f);
                if (!check.Arrived || check.Time > remaining + LateTolerance)
                    Finish(FormattableString.Invariant($"late eta={check.Time:F2} remaining={remaining:F2}"));
            }
        }

        /// <summary>
        /// Jump strike: a timed drive onto the contact line at the line-up point, then a run-up
        /// along the line at the constant speed that reaches the contact exactly on time, so the
        /// car is one flight's distance short of it when it takes off.
        /// </summary>
        private void RunUpAndJump(RUBot bot, Car car, float now, float remaining, float forwardSpeed)
        {
            Vec3 right = new(-heading.y, heading.x, 0f);
            Vec3 toContact = (TargetLocation - car.Location).Flatten();
            float along = toContact.Dot(heading);
            float across = toContact.Dot(right);
            float alignment = car.Forward.Flatten().Normalize().Dot(heading);
            if (!runningUp && along <= Plan.RunUp + LineUpTolerance && MathF.Abs(across) < LineUpTolerance && alignment > 0.97f)
                runningUp = true;

            if (!runningUp)
            {
                Vec3 lineUp = TargetLocation.Flatten() - heading * Plan.RunUp;
                float lineUpTime = Slice.Time - Plan.RunUp / Plan.LineSpeed;
                var target = new DriveTarget(lineUp, heading, lineUpTime, true);
                DriveCommand command = Navigator.Control(car.Location, car.Forward, forwardSpeed, car.Boost,
                    car.AngularVelocity.z, target, now);
                Apply(bot, command);
                Diagnostics?.Invoke(FormattableString.Invariant(
                    $"strike tick t={now:F3} {Kind} line-up remaining={remaining:F3} along={along:F0} across={across:F0} speed={forwardSpeed:F0} align={alignment:F3}"));
                if (now >= nextResolve - ResolveInterval * 0.5f)
                {
                    // The run-up can absorb a late line-up by running faster, up to top speed.
                    var asap = target;
                    asap.ArrivalTime = float.NaN;
                    RolloutResult check = Navigator.Rollout(Navigator.StartState(car), asap, remaining);
                    float left = remaining - check.Time;
                    if (!check.Arrived || left < Plan.JumpTime + 0.1f || Plan.RunUp / left > RL.CarMaxSpeed)
                        Finish(FormattableString.Invariant($"late eta={check.Time:F2} remaining={remaining:F2}"));
                }
                return;
            }

            // Run-up: steer along the contact line, hold the speed that arrives exactly on time.
            var line = new DriveTarget(TargetLocation, heading, float.NaN, true);
            DriveCommand steer = Navigator.Control(car.Location, car.Forward, forwardSpeed, car.Boost,
                car.AngularVelocity.z, line, now);
            float desired = along / MathF.Max(remaining, 1f / RL.TickRate);
            DriveCommand speed = Navigator.HoldSpeed(desired, forwardSpeed, car.Boost);
            bot.Controller.Steer = steer.Steer;
            bot.Controller.Throttle = speed.Throttle;
            bot.Controller.Boost = speed.Boost && MathF.Abs(steer.Steer) < 0.3f;
            bot.Controller.Handbrake = false;
            Diagnostics?.Invoke(FormattableString.Invariant(
                $"strike tick t={now:F3} {Kind} run-up remaining={remaining:F3} along={along:F0} across={across:F0} speed={forwardSpeed:F0} desired={desired:F0} align={alignment:F3}"));

            if (remaining <= Plan.JumpTime + 0.5f / RL.TickRate)
            {
                // Take off when the flight will carry the car onto the contact: it keeps its
                // ground speed in the air, and boosting in flight can make up a shortfall.
                Vec3 landing = car.Location + car.Velocity.Flatten() * remaining;
                Vec3 miss = (landing - TargetLocation).Flatten();
                float missAlong = miss.Dot(heading);
                float missAcross = miss.Dot(right);
                float reach = AirBoostReach(car, remaining);
                if (alignment > 0.97f && MathF.Abs(missAcross) < TakeoffTolerance && missAlong < TakeoffTolerance &&
                    missAlong > -(reach + TakeoffTolerance))
                {
                    jumpStarted = now;
                    Status = "jump";
                    Fly(bot, car, now);
                    return;
                }
                if (remaining < Plan.JumpTime - 0.06f)
                    Finish(FormattableString.Invariant($"missed takeoff along={missAlong:F0} across={missAcross:F0} reach={reach:F0} speed={forwardSpeed:F0} line={Plan.LineSpeed:F0} align={alignment:F3}"));
            }
        }

        private static void Apply(RUBot bot, DriveCommand command)
        {
            bot.Controller.Throttle = command.Throttle;
            bot.Controller.Steer = command.Steer;
            bot.Controller.Boost = command.Boost;
            bot.Controller.Handbrake = false;
        }

        /// <summary>Extra distance full boost can add over <paramref name="time"/> of flight (with a safety share).</summary>
        private static float AirBoostReach(Car car, float time)
        {
            float fuelTime = car.Boost / RL.BoostPerSecond;
            float burn = MathF.Max(0f, MathF.Min(time, fuelTime));
            return 0.8f * 0.5f * RL.BoostAccelAir * burn * burn;
        }

        private float VerticalSpeedAtContact() => Kind switch
        {
            StrikeKind.Jump => JumpModel.Single(Plan.JumpTime).Speed,
            StrikeKind.DoubleJump => JumpModel.Double(Plan.JumpTime).Speed,
            _ => 0f,
        };

        private void Fly(RUBot bot, Car car, float now)
        {
            float elapsed = now - jumpStarted;
            float remaining = Slice.Time - now;
            Interruptible = false;
            bool hold = elapsed < JumpModel.MaximumHold;
            bool second = Kind == StrikeKind.DoubleJump && !hold && !secondJumpSent &&
                elapsed >= JumpModel.MaximumHold + 1.5f / RL.TickRate;
            bot.Controller.Jump = hold || second;
            if (second) secondJumpSent = true;

            AirInput air = AirControl.Orient(car.Forward, car.Right, car.Up, LocalAngular(car), heading, Vec3.Up);
            // A second press with stick input would dodge instead of double jumping.
            bot.Controller.Pitch = second ? 0f : air.Pitch;
            bot.Controller.Yaw = second ? 0f : air.Yaw;
            bot.Controller.Roll = second ? 0f : air.Roll;
            bot.Controller.Throttle = 0f;
            bot.Controller.Handbrake = false;

            // Boost while the car would otherwise arrive short: with the nose level the thrust
            // is horizontal, so it closes the gap without changing the jump's height.
            bot.Controller.Boost = false;
            if (remaining > 0.02f && MathF.Abs(car.Forward.z) < 0.25f && car.Forward.Flatten().Normalize().Dot(heading) > 0.95f)
            {
                float shortfall = (TargetLocation - car.Location).Flatten().Dot(heading) - car.Velocity.Flatten().Dot(heading) * remaining;
                float required = 2f * shortfall / (remaining * remaining);
                bot.Controller.Boost = required > 0.5f * RL.BoostAccelAir;
            }
        }

        private void Finish(string reason)
        {
            Status = reason;
            Finished = true;
            Interruptible = true;
        }

        private static Vec3 LocalAngular(Car car) =>
            new(car.AngularVelocity.Dot(car.Forward), car.AngularVelocity.Dot(car.Right), car.AngularVelocity.Dot(car.Up));
    }
}
