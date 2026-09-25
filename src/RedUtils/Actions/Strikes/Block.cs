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
        /// <summary>Spare time beyond which the car stops on the point instead of running through it.</summary>
        private const float ParkSlack = 0.35f;
        /// <summary>How close the car's projected position at contact must be to the point to take off.</summary>
        private const float TakeoffReach = 130f;

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
                Point = BlockPlanner.BlockPoint(live.Location, car.Location);
            }

            Vec3 face = (Ball.Location - car.Location).Flatten();
            Diagnostics?.Invoke(FormattableString.Invariant(
                $"block tick t={now:F3} remaining={remaining:F3} status={Status} car={car.Location} v={car.Velocity} point={Point} ball={Ball.Location} jump={Plan.JumpTime:F2}"));
            if (float.IsFinite(jumpStarted))
            {
                Fly(bot, car, now, face);
                return;
            }
            if (!car.IsGrounded)
            {
                AirInput land = AirControl.Orient(car.Forward, car.Right, car.Up, LocalAngular(car), face.Normalize(), Vec3.Up);
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
                if (covers || remaining < Plan.JumpTime - 0.08f)
                {
                    jumpStarted = now;
                    Status = "jump";
                    Fly(bot, car, now, face);
                    return;
                }
            }

            float distance = (Point - car.Location).Flatten().Length();
            var drive = new DriveTarget(Point, Vec3.Zero);
            float eta = distance > SettledRadius
                ? Navigator.Rollout(Navigator.StartState(car), drive, remaining + 0.3f).Time
                : 0f;
            if (remaining - eta > ParkSlack)
            {
                // Well early: settle on the point and wait for the ball.
                if (distance > SettledRadius)
                {
                    drive.ArrivalSpeed = 0f;
                    Apply(bot, Navigator.Control(car.Location, car.Forward, speed, car.Boost, car.AngularVelocity.z, drive, now));
                }
                else
                    bot.Controller.Throttle = MathF.Abs(speed) < 40f ? 0f : System.Math.Clamp(-speed / 300f, -1f, 1f);
                Status = "set";
            }
            else
            {
                // Tight: run through the point flat out.
                Apply(bot, Navigator.Control(car.Location, car.Forward, speed, car.Boost, car.AngularVelocity.z, drive, now));
                Status = "moving";
            }
        }

        private static void Apply(RUBot bot, DriveCommand command)
        {
            bot.Controller.Throttle = command.Throttle;
            bot.Controller.Steer = command.Steer;
            bot.Controller.Boost = command.Boost;
        }

        private void Fly(RUBot bot, Car car, float now, Vec3 face)
        {
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

        private static Vec3 LocalAngular(Car car) =>
            new(car.AngularVelocity.Dot(car.Forward), car.AngularVelocity.Dot(car.Right), car.AngularVelocity.Dot(car.Up));
    }
}
