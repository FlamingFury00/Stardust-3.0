using System;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;

namespace RedUtils
{
    /// <summary>
    /// Executes a planned ground, jump, flip, or double-jump touch. The car drives to the contact's
    /// ground position on the planned heading, timed to arrive exactly at contact; jump strikes take
    /// off the model's rise time before contact, since a jump leaves horizontal motion unchanged,
    /// and flips dodge forward into the ball a few ticks before it. The contact is re-solved against
    /// the live prediction, and the strike stands down as soon as it can no longer make the touch
    /// or the ball has been touched.
    /// </summary>
    public class DrivenStrike : Shot, IStrike
    {
        private const float ResolveInterval = 1f / 30f;
        private const float MaxDeviation = 120f;
        /// <summary>How close to the contact line (uu) the car must be to start its run-up.</summary>
        private const float LineUpTolerance = 60f;
        private const float HalfTick = 0.5f / RL.TickRate;
        /// <summary>Slowest run-up, as a share of the planned line speed, an early car may join the line at.</summary>
        private const float EarlyRunUpShare = 0.75f;
        /// <summary>Lateral offset per unit of distance still to run that the run-up steering converges.</summary>
        private const float LineConvergence = 0.12f;
        /// <summary>Boost above which a run-up may run faster than the throttle alone allows.</summary>
        private const float RunUpBoostFloor = 10f;
        /// <summary>Largest takeoff error (uu) accepted beyond what in-flight boost can correct.</summary>
        private const float TakeoffTolerance = 60f;
        /// <summary>Predicted lateness beyond which the strike stands down.</summary>
        private const float LateTolerance = 0.1f;
        /// <summary>Lateness past the planned contact after which a missed touch is abandoned.</summary>
        private const float MissTolerance = 0.1f;

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
        /// <summary>Jump output of the previous tick: a dodge needs jump released for a tick first.</summary>
        private bool jumpHeld;
        private bool dodged;

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
            float touch = Ball.LatestTouch?.Time ?? -1f;
            if (float.IsNaN(lastTouch)) lastTouch = touch;

            // Our touch is the planned contact. Anyone else's changes the play and ends the approach,
            // but a committed flight carries on: the car cannot change course in the air.
            if (touch != lastTouch)
            {
                bool ours = car.LatestTouch != null && car.LatestTouch.Time == touch;
                if (ours || !Airborne)
                {
                    Finish(ours ? "hit" : "touched");
                    return;
                }
                lastTouch = touch;
            }
            if (remaining < -MissTolerance || (Airborne && car.IsGrounded && now - jumpStarted > 0.25f))
            {
                Finish("missed");
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
                // Solve with the speed and pose the car will have at contact: ground strikes arrive
                // at the planned speed, jump strikes carry their run-up speed (plus a flip's dodge).
                float speed = runningUp ? ContactSpeed(RunUpSpeed(car, remaining)) : Plan.ContactSpeed;
                float vertical = VerticalSpeedAtContact();
                ContactSolution contact = Contact.Aim(live.Location, live.Velocity, heading,
                    heading * speed + new Vec3(0, 0, vertical), contactHeight, ShotTarget, geometry,
                    Plan.Flip ? FlipModel.Pitch(FlipModel.Lead) : 0f, Plan.Flip ? RL.CarMaxAngularSpeed : 0f);
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

            if (car.Up.z < StrikePlanner.FloorUp)
            {
                // Strikes are driven on the floor: a car that ran up a ramp comes back down first,
                // for as long as the touch is still in reach.
                LeaveWall(bot, car);
                Interruptible = true;
                if (now >= nextResolve - ResolveInterval * 0.5f && remaining > 0.25f)
                {
                    RolloutResult check = Navigator.Rollout(Navigator.StartState(car), StrikePlanner.StrikeTarget(Plan),
                        remaining + 0.3f, alignment: Navigator.ExecutionAlignment);
                    if (!check.Arrived || check.Time > remaining - Plan.RunUpTime + LateTolerance)
                        Finish(FormattableString.Invariant($"late off the floor eta={check.Time:F2} remaining={remaining:F2}"));
                }
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
            var target = new DriveTarget(TargetLocation, heading, Slice.Time, Plan.UsesBoost);
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
                RolloutResult check = Navigator.Rollout(Navigator.StartState(car), asap, remaining + 0.3f,
                    alignment: Navigator.ExecutionAlignment);
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
            if (!runningUp && alignment > 0.95f && MathF.Abs(across) < MathF.Max(LineUpTolerance, LineConvergence * along))
            {
                // On the line: run up from here. An early car may join the line further back and run
                // up a little slower rather than brake to wait at the line-up point, as long as the
                // touch keeps close to its planned pace and the run-up can really be driven.
                float needed = RunUpSpeed(car, remaining);
                float fuel = RunUpFuel(car);
                float reachable = fuel > RunUpBoostFloor ? 0.95f * RL.CarMaxSpeed : 0.97f * RL.ThrottleMaxSpeed;
                runningUp = along <= Plan.RunUp + LineUpTolerance ||
                    (needed >= EarlyRunUpShare * Plan.LineSpeed && needed <= reachable && RunUpMakeable(car, remaining, forwardSpeed));
            }

            if (!runningUp)
            {
                Vec3 lineUp = TargetLocation.Flatten() - heading * Plan.RunUp;
                float lineUpTime = Slice.Time - Plan.RunUpTime;
                var target = new DriveTarget(lineUp, heading, lineUpTime, Plan.UsesBoost);
                DriveCommand command = Navigator.Control(car.Location, car.Forward, forwardSpeed, car.Boost,
                    car.AngularVelocity.z, target, now);
                Apply(bot, command);
                Diagnostics?.Invoke(FormattableString.Invariant(
                    $"strike tick t={now:F3} {Kind} line-up remaining={remaining:F3} along={along:F0} across={across:F0} speed={forwardSpeed:F0} align={alignment:F3}"));
                // Near the line-up point an early car only has to settle onto the line.
                if (now >= nextResolve - ResolveInterval * 0.5f && (lineUp - car.Location).Flatten().Length() > 2.5f * LineUpTolerance)
                {
                    // The run-up can absorb a late line-up by running faster, up to top speed.
                    var asap = target;
                    asap.ArrivalTime = float.NaN;
                    RolloutResult check = Navigator.Rollout(Navigator.StartState(car), asap, remaining,
                        alignment: Navigator.ExecutionAlignment);
                    float left = remaining - check.Time;
                    if (!check.Arrived || left < Plan.JumpTime + 0.1f || Plan.RunUp / left > RL.CarMaxSpeed)
                        Finish(FormattableString.Invariant($"late eta={check.Time:F2} remaining={remaining:F2}"));
                }
                return;
            }

            // Run-up: steer along the contact line, hold the speed that arrives exactly on time
            // (a flip's dodge covers the last stretch faster).
            var line = new DriveTarget(TargetLocation, heading, float.NaN, Plan.UsesBoost);
            DriveCommand steer = Navigator.Control(car.Location, car.Forward, forwardSpeed, car.Boost,
                car.AngularVelocity.z, line, now);
            float desired = RunUpSpeed(car, remaining);
            DriveCommand speed = Navigator.HoldSpeed(desired, forwardSpeed, RunUpFuel(car));
            bot.Controller.Steer = steer.Steer;
            bot.Controller.Throttle = speed.Throttle;
            bot.Controller.Boost = speed.Boost && MathF.Abs(steer.Steer) < 0.3f;
            bot.Controller.Handbrake = false;
            Diagnostics?.Invoke(FormattableString.Invariant(
                $"strike tick t={now:F3} {Kind} run-up remaining={remaining:F3} along={along:F0} across={across:F0} speed={forwardSpeed:F0} desired={desired:F0} align={alignment:F3}"));

            if (remaining > Plan.JumpTime + 0.1f && now >= nextResolve - ResolveInterval * 0.5f &&
                !RunUpMakeable(car, remaining + LateTolerance, forwardSpeed))
            {
                Finish(FormattableString.Invariant($"late run-up speed={forwardSpeed:F0} desired={desired:F0} remaining={remaining:F2}"));
                return;
            }

            if (remaining <= Plan.JumpTime + 0.5f / RL.TickRate)
            {
                // Take off when the flight will carry the car onto the contact: it keeps its
                // ground speed in the air (plus a flip's dodge), and boosting in flight can make
                // up a shortfall.
                Vec3 landing = car.Location + car.Velocity.Flatten() * remaining + heading * Plan.DodgeGain;
                Vec3 miss = (landing - TargetLocation).Flatten();
                float missAlong = miss.Dot(heading);
                float missAcross = miss.Dot(right);
                float reach = AirBoostReach(car, remaining);
                // A flip needs its minimum jump, a released tick and the dodge lead before contact.
                bool flipFits = !Plan.Flip || remaining >= FlipModel.MinimumJumpTime - HalfTick;
                if (flipFits && alignment > 0.97f && MathF.Abs(missAcross) < TakeoffTolerance && missAlong < TakeoffTolerance &&
                    missAlong > -(reach + TakeoffTolerance))
                {
                    jumpStarted = now;
                    Status = "jump";
                    Fly(bot, car, now);
                    return;
                }
                if (remaining < Plan.JumpTime - 0.06f || !flipFits)
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

        /// <summary>Boost the run-up may spend: none when the strike was planned without boost.</summary>
        private float RunUpFuel(Car car) => Plan.UsesBoost ? car.Boost : 0f;

        /// <summary>Whether flat-out driving from here still covers the run-up to the contact within <paramref name="time"/>.</summary>
        private bool RunUpMakeable(Car car, float time, float forwardSpeed)
        {
            float along = (TargetLocation - car.Location).Flatten().Dot(heading) - Plan.DodgeGain;
            return DrivePhysics.TravelTime(MathF.Max(0f, along), MathF.Max(0f, forwardSpeed), RunUpFuel(car)) <= time;
        }

        /// <summary>Constant run-up speed that meets the contact exactly on time from where the car is.</summary>
        private float RunUpSpeed(Car car, float remaining) => System.Math.Clamp(
            ((TargetLocation - car.Location).Flatten().Dot(heading) - Plan.DodgeGain) / MathF.Max(remaining, 1f / RL.TickRate),
            0f, RL.CarMaxSpeed);

        /// <summary>Flat speed at contact for a run-up at <paramref name="runUpSpeed"/>.</summary>
        private float ContactSpeed(float runUpSpeed) =>
            Plan.Flip ? FlipModel.SpeedAfter(runUpSpeed, VerticalSpeedAtContact()) : runUpSpeed;

        private float VerticalSpeedAtContact() => Kind switch
        {
            StrikeKind.Jump => JumpModel.Single(Plan.JumpTime, Plan.Hold).Speed,
            StrikeKind.DoubleJump => JumpModel.Double(Plan.JumpTime).Speed,
            _ => 0f,
        };

        private void Fly(RUBot bot, Car car, float now)
        {
            float elapsed = now - jumpStarted;
            float remaining = Slice.Time - now;
            Interruptible = false;
            bot.Controller.Throttle = 0f;
            bot.Controller.Handbrake = false;
            bot.Controller.Boost = false;
            if (dodged)
            {
                // Let the flip rotate freely into the ball: any stick input would cancel it.
                bot.Controller.Jump = false;
                bot.Controller.Pitch = bot.Controller.Yaw = bot.Controller.Roll = 0f;
                return;
            }

            // A flip releases jump on the contact clock, exactly one tick before its dodge.
            bool hold = Plan.Flip
                ? elapsed < JumpModel.MinimumTime - HalfTick ||
                    (elapsed < JumpModel.MaximumHold - HalfTick && remaining > FlipModel.Lead + 1.5f / RL.TickRate)
                : elapsed < Plan.Hold;
            if (Plan.Flip && !hold && !jumpHeld && remaining <= FlipModel.Lead + HalfTick &&
                elapsed >= FlipModel.EarliestDodge - HalfTick)
            {
                // Dodge along the contact heading; the stick direction compensates for the car's yaw.
                (float pitch, float yaw) = DodgeModel.InputToward(car.Forward, heading, car.Velocity.Dot(car.Forward));
                bot.Controller.Jump = true;
                bot.Controller.Pitch = pitch;
                bot.Controller.Yaw = yaw;
                bot.Controller.Roll = 0f;
                dodged = true;
                Status = "flip";
                return;
            }
            bool second = Kind == StrikeKind.DoubleJump && !hold && !secondJumpSent &&
                elapsed >= JumpModel.MaximumHold + 1.5f / RL.TickRate;
            bot.Controller.Jump = hold || second;
            jumpHeld = bot.Controller.Jump;
            if (second) secondJumpSent = true;

            AirInput air = AirControl.Orient(car.Forward, car.Right, car.Up, LocalAngular(car), heading, Vec3.Up);
            // A second press with stick input would dodge instead of double jumping.
            bot.Controller.Pitch = second ? 0f : air.Pitch;
            bot.Controller.Yaw = second ? 0f : air.Yaw;
            bot.Controller.Roll = second ? 0f : air.Roll;

            // Boost while the car would otherwise arrive short: with the nose level the thrust
            // is horizontal, so it closes the gap without changing the jump's height.
            if (remaining > 0.02f && MathF.Abs(car.Forward.z) < 0.25f && car.Forward.Flatten().Normalize().Dot(heading) > 0.95f)
            {
                float shortfall = (TargetLocation - car.Location).Flatten().Dot(heading) - car.Velocity.Flatten().Dot(heading) * remaining - Plan.DodgeGain;
                float required = 2f * shortfall / (remaining * remaining);
                bot.Controller.Boost = required > 0.5f * RL.BoostAccelAir;
            }
        }

        /// <summary>Drive down off a wall toward the contact.</summary>
        private void LeaveWall(RUBot bot, Car car)
        {
            Vec3 local = car.Local(TargetLocation.Flatten() - car.Location);
            float angle = MathF.Atan2(local.y, local.x);
            bot.Controller.Steer = System.Math.Clamp(3f * angle, -1f, 1f);
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = Plan.UsesBoost && MathF.Abs(angle) < 0.3f && car.Velocity.Length() < 1900f;
            bot.Controller.Handbrake = false;
            Status = "leaving wall";
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
