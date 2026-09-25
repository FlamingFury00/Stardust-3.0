using System;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;

namespace RedUtils
{
    /// <summary>
    /// Executes a <see cref="BlockPlan"/>: races to the point under the ball's path, settles there
    /// early, and jumps (or double jumps) so the car's body meets the ball at its height. The point
    /// follows the live prediction, so a late change in the ball's path is tracked.
    /// </summary>
    public sealed class Block : IAction
    {
        private const float ResolveInterval = 1f / 30f;
        private const float MaxDeviation = 250f;
        private const float SettledRadius = 80f;
        /// <summary>Ball ground speed below which the car faces the ball instead of turning across its path.</summary>
        private const float MinimumPathSpeed = 300f;
        /// <summary>Spare time beyond which the drive through the point paces itself instead of going flat out.</summary>
        private const float PaceSlack = 0.05f;
        /// <summary>How far past contact the arrival estimate looks, so lateness still reads as a finite time.</summary>
        private const float LateHorizon = 0.5f;
        /// <summary>Time a parked car should stand still on the point before its takeoff.</summary>
        private const float ParkSettle = 0.15f;
        /// <summary>Ground speed below which a car on the point counts as parked.</summary>
        private const float ParkedSpeed = 150f;
        /// <summary>How close the car's projected position at contact must be to the point to take off.</summary>
        private const float TakeoffReach = 130f;
        /// <summary>How long past its planned takeoff a car waits to cover the point before it jumps regardless.</summary>
        private const float LateTakeoff = 0.08f;

        /// <summary>Optional per-tick diagnostics sink for mechanics debugging (null in play).</summary>
        public static Action<string> Diagnostics;

        public bool Finished { get; private set; }
        public bool Interruptible => !float.IsFinite(jumpStarted);
        public BlockPlan Plan { get; }
        public string Status { get; private set; } = "moving";
        public Vec3 Point { get; private set; }

        private float nextResolve = float.NegativeInfinity;
        private float lastTouch = float.NaN;
        private float jumpStarted = float.NaN;
        private bool secondJumpSent;
        private bool racing;
        /// <summary>Heading held across the ball's path from takeoff, so a head-on ball cannot flip it mid-flight.</summary>
        private Vec3 flightHeading;

        public Block(BlockPlan plan)
        {
            Plan = plan;
            Point = plan.Point;
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            float now = Game.Time;
            float remaining = Plan.ContactTime - now;
            float touch = Ball.LatestTouch?.Time ?? -1f;
            if (float.IsNaN(lastTouch)) lastTouch = touch;
            if (touch != lastTouch || remaining < -0.3f || (float.IsFinite(jumpStarted) && car.IsGrounded && now - jumpStarted > 0.3f))
            {
                Finish(touch != lastTouch ? "touched" : "done");
                return;
            }

            if (now >= nextResolve && remaining > 0.02f)
            {
                nextResolve = now + ResolveInterval;
                BallSlice live = new BallPath(Ball.Prediction.Slices).SliceAt(Plan.ContactTime);
                if ((live.Location - Plan.Slice.Location).Length() > MaxDeviation)
                {
                    Finish("prediction moved");
                    return;
                }
                Point = BlockPlanner.BlockPoint(live.Location, car.Location, Plan.JumpTime > 0f);
            }

            Diagnostics?.Invoke(FormattableString.Invariant(
                $"block tick t={now:F3} remaining={remaining:F3} status={Status} car={car.Location} v={car.Velocity} point={Point} ball={Ball.Location} jump={Plan.JumpTime:F2}"));
            if (float.IsFinite(jumpStarted))
            {
                Fly(bot, car, now);
                return;
            }
            if (!car.IsGrounded)
            {
                // Land facing the point, ready to drive to it.
                Vec3 toPoint = (Point - car.Location).Flatten();
                AirInput land = AirControl.Orient(car.Forward, car.Right, car.Up, LocalAngular(car),
                    toPoint.Length() > 1f ? toPoint.Normalize() : car.Forward.Flatten().Normalize(), Vec3.Up);
                bot.Controller.Pitch = land.Pitch;
                bot.Controller.Yaw = land.Yaw;
                bot.Controller.Roll = land.Roll;
                bot.Controller.Throttle = 1f;
                return;
            }

            float speed = car.Velocity.Dot(car.Forward);
            if (Plan.JumpTime > 0f && remaining <= Plan.JumpTime + 0.5f / RL.TickRate)
            {
                // Take off when the car, carried on at its speed, covers the point at contact;
                // past the last moment jump regardless, as a late body in the path may still deflect it.
                Vec3 projected = car.Location + car.Velocity.Flatten() * remaining;
                bool covers = (projected - Point).Flatten().Length() < TakeoffReach;
                if (covers || remaining < Plan.JumpTime - LateTakeoff)
                {
                    jumpStarted = now;
                    flightHeading = Broadside(car, Ball.Location, Ball.Velocity);
                    Status = "jump";
                    Fly(bot, car, now);
                    return;
                }
            }

            float distance = (Point - car.Location).Flatten().Length();
            float takeoffIn = remaining - Plan.JumpTime;
            var park = new DriveTarget(Point, Vec3.Zero) { ArrivalSpeed = 0f };
            bool parked = distance <= SettledRadius && MathF.Abs(speed) < ParkedSpeed;
            if (parked || (distance > SettledRadius && ParkEta(car, park, takeoffIn - ParkSettle) + ParkSettle <= takeoffIn))
            {
                // Early enough to stop on the point: settle there and wait for the ball.
                if (distance > SettledRadius)
                    Apply(bot, Navigator.Control(car.Location, car.Forward, speed, car.Boost, car.AngularVelocity.z, park, now));
                else
                    bot.Controller.Throttle = MathF.Abs(speed) < 40f ? 0f : System.Math.Clamp(-speed / 300f, -1f, 1f);
                Status = "set";
                return;
            }

            // Drive through the point. The car cannot speed up once it has jumped, so it is timed
            // as flat out until takeoff and coasting after; while that still arrives early it holds
            // a steady pace of path over time, which crosses the point at contact whenever the
            // jump happens. The steering law's own speed (it slows for turns) caps the pace.
            var through = new DriveTarget(Point, Vec3.Zero);
            DriveCommand steer = Navigator.Control(car.Location, car.Forward, speed, car.Boost, car.AngularVelocity.z, through, now);
            float arrival = BlockPlanner.ThroughTime(Navigator.StartState(car), Point,
                Plan.JumpTime > 0f ? takeoffIn : float.PositiveInfinity, remaining + LateHorizon);
            bot.Controller.Steer = steer.Steer;
            // Hysteresis: a car racing to be on time paces again only with clear time in hand.
            racing = remaining - arrival <= (racing ? 2f * PaceSlack : PaceSlack);
            if (!racing)
            {
                float path = Navigator.EstimatePathLength(car.Location, car.Forward, speed, through);
                DriveCommand pace = Navigator.HoldSpeed(MathF.Min(path / MathF.Max(remaining, 1f / RL.TickRate), RL.CarMaxSpeed),
                    speed, car.Boost);
                bot.Controller.Throttle = MathF.Min(steer.Throttle, pace.Throttle);
                bot.Controller.Boost = steer.Boost && pace.Boost;
                Status = "moving";
            }
            else
            {
                Apply(bot, steer);
                Status = "racing";
            }
        }

        /// <summary>Time for a stopping approach to the point, or infinity when it would not settle within <paramref name="within"/>.</summary>
        private static float ParkEta(Car car, in DriveTarget park, float within)
        {
            if (within <= 0f) return float.PositiveInfinity;
            RolloutResult rollout = Navigator.Rollout(Navigator.StartState(car), park, within);
            return rollout.Arrived && rollout.Time <= within ? rollout.Time : float.PositiveInfinity;
        }

        private static void Apply(RUBot bot, DriveCommand command)
        {
            bot.Controller.Throttle = command.Throttle;
            bot.Controller.Steer = command.Steer;
            bot.Controller.Boost = command.Boost;
        }

        private void Fly(RUBot bot, Car car, float now)
        {
            Vec3 face = flightHeading;
            float elapsed = now - jumpStarted;
            bool hold = elapsed < JumpModel.MaximumHold;
            bool second = Plan.DoubleJump && !hold && !secondJumpSent && elapsed >= JumpModel.MaximumHold + 1.5f / RL.TickRate;
            bot.Controller.Jump = hold || second;
            if (second) secondJumpSent = true;
            AirInput air = AirControl.Orient(car.Forward, car.Right, car.Up, LocalAngular(car),
                face.Length() > 1f ? face.Normalize() : car.Forward, Vec3.Up);
            // Neutral sticks on the second press, or it would dodge.
            bot.Controller.Pitch = second ? 0f : air.Pitch;
            bot.Controller.Yaw = second ? 0f : air.Yaw;
            bot.Controller.Roll = second ? 0f : air.Roll;
            bot.Controller.Throttle = 0f;
            bot.Controller.Boost = false;
        }

        private void Finish(string reason)
        {
            Status = reason;
            Finished = true;
        }

        /// <summary>
        /// Heading that turns the car's length across the ball's path, the widest body it can put
        /// in the way (118 uu instead of 84 nose-on), whichever way across is nearer its heading.
        /// A slow ball has no path to cross, so the car faces it.
        /// </summary>
        private static Vec3 Broadside(Car car, Vec3 ball, Vec3 ballVelocity)
        {
            Vec3 path = ballVelocity.Flatten();
            if (path.Length() < MinimumPathSpeed) return (ball - car.Location).Flatten();
            Vec3 across = new Vec3(-path.y, path.x, 0f).Normalize();
            return across.Dot(car.Forward) >= 0f ? across : -across;
        }

        private static Vec3 LocalAngular(Car car) =>
            new(car.AngularVelocity.Dot(car.Forward), car.AngularVelocity.Dot(car.Right), car.AngularVelocity.Dot(car.Up));
    }
}
