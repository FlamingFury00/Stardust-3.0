using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Close-range emergency block/clear used when a goal-bound ball is already within contact
    /// distance. Its job is deliberately narrow: touch the ball away from our own goal instead of
    /// abandoning a reachable ball to drive toward a distant goal-line waypoint.
    /// </summary>
    public sealed class EmergencyClear : IAction
    {
        public bool Finished { get; private set; }
        public bool Interruptible =>
            !committed || (float.IsFinite(committedAt) && Game.Time - committedAt > 0.62f);
        public Vec3 Target { get; private set; }
        public Vec3 ClearDirection { get; private set; }
        public bool Committed => committed;

        private readonly Drive drive;
        private readonly JumpSequence jumps = new(0.11f);
        private readonly float started = Game.Time;
        private bool committed;
        private float committedAt = float.NaN;

        public EmergencyClear(Car car, Vec3 ownGoal, Vec3 attackGoal)
        {
            Vec3 fieldward = new(0f, ownGoal.y < 0f ? 1f : -1f, 0f);
            ClearDirection = ControlMath.FlatUnit(
                attackGoal - Ball.Location, fieldward);
            if (!Defense.ClearDirectionIsSafe(ClearDirection, ownGoal, 0.20f))
                ClearDirection = fieldward;

            Target = Field.LimitToNearestSurface(
                Ball.Location - ClearDirection * 145f);
            drive = new Drive(
                car, Target, Car.MaxSpeed,
                allowDodges: false, wasteBoost: true)
            {
                AllowHandbrake = false
            };
        }

        public static bool CanStart(
            Car car, Ball ball, Vec3 ownGoal, float threatTime)
        {
            if (car == null || ball == null || car.IsDemolished ||
                !car.IsGrounded || !float.IsFinite(threatTime) ||
                threatTime < 0f || threatTime > 4.50f ||
                !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(ball.location) ||
                !ControlMath.Finite(ownGoal))
                return false;

            float distance = car.Location.Dist(ball.location);
            if (distance > 540f || ball.location.z > 340f)
                return false;

            // A slight upfield offset is acceptable at point-blank range, but not a full wrong-side
            // chase. The directional dodge below always points away from our own goal.
            return Defense.IsDepthGoalSide(
                car.Location, ball.location, ownGoal, -190f);
        }

        public void Run(RUBot bot)
        {
            if (bot == null || bot.Me == null || bot.Me.IsDemolished)
            {
                Finished = true;
                return;
            }

            if (bot.OwnTouchThisTick)
            {
                Finished = true;
                return;
            }

            float elapsed = Game.Time - started;
            if (!float.IsFinite(elapsed) || elapsed > 0.95f)
            {
                Finished = true;
                return;
            }

            Car car = bot.Me;
            Ball ball = Ball.MainBall;
            float distance = car.Location.Dist(ball.location);

            Vec3 fieldward = new(
                0f, bot.OurGoal.Location.y < 0f ? 1f : -1f, 0f);
            Vec3 direct = ControlMath.FlatUnit(
                bot.TheirGoal.Location - ball.location, fieldward);
            ClearDirection = Defense.ClearDirectionIsSafe(
                    direct, bot.OurGoal.Location, 0.20f)
                ? direct
                : fieldward;

            float horizon = System.Math.Clamp(
                distance / MathF.Max(
                    1200f, car.Velocity.Length() + ball.velocity.Length()) * 0.55f,
                0.04f, 0.18f);
            Ball predicted = Ball.Prediction.TrySample(
                    Game.Time + horizon, out Ball sample)
                ? sample
                : ball.Predict(horizon);

            Vec3 contact = predicted.location - ClearDirection * 145f;
            Target = Field.LimitToNearestSurface(
                new Vec3(contact.x, contact.y, 17f));

            if (!committed)
            {
                drive.Target = Target;
                drive.TargetSpeed = Car.MaxSpeed;
                drive.AllowDodges = false;
                drive.AllowHandbrake = false;
                drive.WasteBoost = true;
                drive.Run(bot);

                // Do not let the "low ball" path become a moving target the car chases until
                // it has already passed. Point-blank low threats also need a committed dodge.
                bool raised = ball.location.z > 125f;
                float threat = bot is Stardust stardust
                    ? stardust.EmergencyThreatTime
                    : float.PositiveInfinity;
                bool hardUrgency = float.IsFinite(threat) && threat < 0.58f;
                bool imminent = distance < 390f ||
                    (distance < 500f && elapsed > 0.08f && (raised || hardUrgency));
                if (imminent)
                {
                    committed = true;
                    committedAt = Game.Time;
                }
                return;
            }

            ControlMath.Aim(
                car, bot.Controller, ClearDirection, Vec3.Up);
            JumpCommand command = jumps.Step(
                Game.Time, bot.Jump.CanDodge);

            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = false;
            bot.Controller.Handbrake = false;
            bot.Controller.Jump = command.Jump;

            if (command.Dodge)
            {
                Vec3 local = ControlMath.FlatUnit(
                    car.Local(ClearDirection), new Vec3(1f, 0f, 0f));
                bot.Controller.Pitch = -local.x;
                bot.Controller.Yaw = local.y;
                bot.Controller.Roll = 0f;
            }

            if (Game.Time - committedAt > 0.72f ||
                (distance > 650f && Game.Time - committedAt > 0.25f))
                Finished = true;
        }
    }
}
