using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public sealed class AerialCarry : IPossessionAction
    {
        private readonly BoostGate boost = new();
        private readonly float started = Game.Time;
        public bool Finished { get; private set; }
        public bool Interruptible => true;
        public float ClaimTime => Game.Time + 0.3f;
        public static bool CanStart(Car car, Ball ball, float opponentEta)
        {
            if (car == null || ball == null || car.IsGrounded || car.Location.z <= 180f ||
                ball.location.z <= 300f || car.Boost <= 8f || opponentEta <= 0.18f)
                return false;

            Vec3 delta = ball.location - car.Location;
            float distance = delta.Length();
            if (delta.z <= 20f || delta.z >= 440f || distance >= 690f)
                return false;

            Vec3 relativeVelocity = car.Velocity - ball.velocity;
            float relativeSpeed = relativeVelocity.Length();
            if (relativeSpeed >= 1100f)
                return false;

            // A close, already-controlled air dribble may tolerate a small temporary separation.
            // A distant ball that is rapidly moving away is not a carry start; boosting after it
            // just burns the recovery budget.
            float closing = relativeVelocity.Dot(ControlMath.Unit(delta, Vec3.Up));
            float allowedSeparation = distance < 220f ? -420f : -220f;
            return closing >= allowedSeparation;
        }
        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            Vec3 delta = Ball.Location - car.Location;
            float distance = delta.Length();
            float separationClosing = (car.Velocity - Ball.Velocity)
                .Dot(ControlMath.Unit(delta, Vec3.Up));
            if (car.IsGrounded || distance > 900f || Ball.Location.z < 180f ||
                Game.Time - started > 4f ||
                (car.Boost <= 0f && distance > 220f) ||
                (distance > 360f && separationClosing < -360f))
            {
                Finished = true;
                return;
            }
            if (bot is Stardust stardust && stardust.Options.FlipResets &&
                stardust.Situation.OpponentEta > 1.2f && FlipReset.CanStart(car, Ball.MainBall, bot.Jump))
            { bot.Action = new FlipReset(bot.Jump); return; }
            const float horizon = 0.12f;
            Ball prediction = Ball.Prediction.TrySample(Game.Time + horizon, out Ball sample) ? sample : Ball.MainBall.Predict(horizon);
            Vec3 lane = PossessionControl.AttackingLane(
                car, prediction, bot.LivingOpponents,
                bot.TheirGoal.Location, bot.OurGoal.Location);
            float pressure = bot is Stardust stardustPressure
                ? MathF.Min(stardustPressure.Situation.OpponentEta, stardustPressure.Situation.PressureTime)
                : float.PositiveInfinity;
            bool challenged = float.IsFinite(pressure) && pressure < 0.65f;
            float forwardPush = challenged ? 190f : 90f;
            float lift = pressure < 0.40f ? 35f : 80f;
            Vec3 contactNormal = ControlMath.Unit(
                lane * (challenged ? 0.62f : 0.48f) + Vec3.Up * 0.88f, Vec3.Up);
            Vec3 target = prediction.location - contactNormal * (Ball.Radius + 40);
            Vec3 targetVelocity = prediction.velocity + lane * forwardPush + Vec3.Up * lift;
            Vec3 acceleration = PossessionControl.FlightAtHorizon(car, target, targetVelocity, horizon);
            Vec3 nose = ControlMath.Unit(acceleration, car.Forward);
            ControlMath.Aim(car, bot.Controller, nose, Vec3.Up);
            float closing = (car.Velocity - Ball.Velocity).Dot(ControlMath.Unit(delta, Vec3.Up));
            bool gentle = delta.Length() < 185 && closing > 160;
            bot.Controller.Boost = boost.Step(Game.Time, acceleration.Dot(car.Forward), car.Forward.Dot(nose), car.Boost, gentle);
            bot.Controller.Throttle = gentle ? 0 : 1;
            bot.Controller.Jump = false;
        }
    }

    /// <summary>Experimental, evidence-gated acquisition. Enable with STARDUST_FLIP_RESETS=1.</summary>
    public sealed class FlipReset : IPossessionAction
    {
        private readonly ResetEvidence evidence = new();
        private readonly BoostGate boost = new();
        private readonly float started = Game.Time;
        private float confirmedAt = float.NaN, firedAt = float.NaN;
        public bool Finished { get; private set; }
        public bool Interruptible => !float.IsFinite(firedAt);
        public float ClaimTime => Game.Time + 0.25f;
        /// <summary>Whether the regained flip has been confirmed from the packet flags.</summary>
        public bool Confirmed => float.IsFinite(confirmedAt);
        public FlipReset(JumpState initialState)
        {
            // Capture spent state at selection, even if contact happens before the next control tick.
            evidence.Observe(initialState, false, false, 0, Game.Time);
        }
        public static bool CanStart(Car car, Ball ball, JumpState jump)
        {
            Vec3 delta = ball.location - car.Location;
            return !car.IsGrounded && car.Location.z > 350 && ball.location.z > 550 && car.Boost > 20 &&
                (jump.DoubleJumped || jump.Dodged) && delta.z > 60 && delta.z < 280 && delta.Length() < 360 &&
                (car.Velocity - ball.velocity).Length() < 650;
        }
        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            Vec3 delta = Ball.Location - car.Location;
            if (car.Location.z < 180 || delta.Length() > 650 || Game.Time - started > 2)
            { Finished = true; return; }
            Vec3 towardBall = ControlMath.Unit(delta, Vec3.Up);
            Vec3 lane = ControlMath.FlatUnit(bot.TheirGoal.Location - Ball.Location, car.Forward);
            bool wheelsAligned = (-car.Up).Dot(towardBall) > 0.85f;
            bool confirmed = evidence.Observe(bot.Jump, bot.OwnTouchThisTick, wheelsAligned, car.Location.z, Game.Time);
            bot.Controller.Jump = false;
            bot.Controller.Boost = false;
            if (confirmed)
            {
                if (!float.IsFinite(confirmedAt)) confirmedAt = Game.Time;
                ControlMath.Aim(car, bot.Controller, towardBall, Vec3.Up);
                if (!float.IsFinite(firedAt) && Game.Time - confirmedAt > 0.08f && bot.Jump.CanDodge &&
                    delta.Length() < 230 && car.Forward.Dot(towardBall) > 0.8f && car.AngularVelocity.Length() < 2.5f)
                    firedAt = Game.Time;
                if (float.IsFinite(firedAt))
                {
                    bool pulse = Game.Time - firedAt < 0.05f;
                    bot.Controller.Jump = pulse;
                    if (pulse)
                    {
                        Vec3 local = ControlMath.FlatUnit(car.Local(towardBall), new Vec3(1, 0, 0));
                        bot.Controller.Pitch = -local.x;
                        bot.Controller.Yaw = local.y;
                        bot.Controller.Roll = 0;
                    }
                    Finished = Game.Time - firedAt > 0.8f;
                }
                else if (Game.Time - confirmedAt > 0.55f) Finished = true;
                return;
            }
            if (Game.Time - started > 1.35f) { Finished = true; return; }
            const float horizon = 0.08f;
            Ball prediction = Ball.MainBall.Predict(horizon);
            Vec3 target = prediction.location - Vec3.Up * (Ball.Radius + 18) - lane * 20;
            Vec3 acceleration = PossessionControl.FlightAtHorizon(car, target, prediction.velocity, horizon);
            if (delta.Length() > 220)
            {
                Vec3 nose = ControlMath.Unit(acceleration, car.Forward);
                ControlMath.Aim(car, bot.Controller, nose, -Vec3.Up);
                bot.Controller.Boost = boost.Step(Game.Time, acceleration.Dot(car.Forward), car.Forward.Dot(nose), car.Boost, false);
            }
            else
            {
                // Coast into wheel contact rather than boosting the ball away.
                ControlMath.Aim(car, bot.Controller, lane, -Vec3.Up);
            }
        }
    }

    public sealed class Recover : IAction
    {
        private readonly float started = Game.Time;
        public bool Finished { get; private set; }
        public bool Interruptible => true;
        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            if (car.IsGrounded || Game.Time - started > 1.5f) { Finished = true; return; }
            float time = car.PredictLandingTime();
            time = float.IsFinite(time) ? System.Math.Clamp(time, 0, 2) : 0.3f;
            Vec3 normal = Field.NearestSurface(car.PredictLocation(time)).Normal;
            Vec3 tangent = car.Velocity - normal * car.Velocity.Dot(normal);
            Vec3 fallback = bot.TheirGoal.Location - car.Location;
            fallback -= normal * fallback.Dot(normal);
            ControlMath.Aim(car, bot.Controller, ControlMath.Unit(tangent, ControlMath.Unit(fallback, car.Forward)), normal);
            bot.Controller.Throttle = 1;
            bot.Controller.Boost = false;
            bot.Controller.Jump = false;
            bot.Controller.Handbrake = time < 0.1f && tangent.Length() > 800;
        }
    }
}
