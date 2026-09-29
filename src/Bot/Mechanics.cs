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
    /// <summary>
    /// Flip reset: put the wheels on a high ball to get the flip back, then use it on the ball.
    ///
    /// In Rocket League (and RocketSim) the wheels are suspension rays that also land on the ball:
    /// three touching wheels count as grounded, which clears the used jump and flip. The ball must sit
    /// within about 30° of the car's underside and near the wheel centre; the wheels leave no ball
    /// touch and do not push the ball. So the approach first closes in nose-first under boost, then,
    /// in the last half second, presents the underside to the ball and drifts in at a gentle closing
    /// speed. The reset is confirmed from the packet flags alone: airborne with jump, double jump and
    /// dodge all cleared after they were spent. Then the car lets the ball move ahead, flies in behind
    /// it relative to the target, and dodges into it: the regained flip becomes a shot.
    /// </summary>
    public sealed class FlipReset : IPossessionAction
    {
        /// <summary>Car origin to ball centre when the wheels rest on the ball (RocketSim: 100-103 uu).</summary>
        private const float WheelContact = 102f;
        /// <summary>
        /// Time to contact from which the underside faces the ball, and the closing speed along the
        /// line to the ball to arrive with. Tuned in the mechanics lab (flip-reset drill).
        /// </summary>
        private static float PresentTime = 0.45f, ClosingSpeed = 220f;
        /// <summary>Distance behind the ball, along the aim, from which the reset flip is fired.</summary>
        private const float ShotStandoff = 190f;
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
            // Record the spent flip at selection, even if the wheels land on the next tick.
            evidence.Observe(initialState, 0f);
        }

        /// <summary>
        /// Airborne with no flip left (used, or a single jump's flip timed out), boost to fly with,
        /// and a high ball just above and near enough to reach before gravity separates them.
        /// </summary>
        public static bool CanStart(Car car, Ball ball, JumpState jump)
        {
            Vec3 delta = ball.location - car.Location;
            return !car.IsGrounded && !jump.Grounded && !jump.CanDodge && car.Location.z > 350f &&
                ball.location.z > 550f && car.Boost > 20f && delta.z > 60f && delta.z < 400f &&
                delta.Length() < 500f && (car.Velocity - ball.velocity).Length() < 700f;
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            Vec3 delta = Ball.Location - car.Location;
            float distance = delta.Length();
            // Grounded high up means the wheels are on the ball: that is contact, not a landing.
            bool landed = car.IsGrounded && car.Location.z < 250f;
            if (landed || distance > 900f || Game.Time - started > 3f)
            {
                Finished = true;
                return;
            }
            if (!Confirmed && evidence.Observe(bot.Jump, car.Location.z))
                confirmedAt = Game.Time;
            bot.Controller.Jump = false;
            bot.Controller.Throttle = 0f;

            Vec3 aim = ControlMath.FlatUnit(bot.TheirGoal.Location - Ball.Location, car.Forward);
            if (Confirmed)
            {
                Shoot(bot, car, delta, distance, aim);
                return;
            }
            if (Game.Time - started > 1.6f)
            {
                Finished = true;
                return;
            }
            Approach(bot, car, delta, distance, aim);
        }

        private void Approach(RUBot bot, Car car, Vec3 delta, float distance, Vec3 aim)
        {
            Vec3 toBall = delta / MathF.Max(distance, 1f);
            float closing = (car.Velocity - Ball.Velocity).Dot(toBall);
            float contact = MathF.Max(0f, distance - WheelContact) / MathF.Max(closing, 60f);
            if (contact > PresentTime)
            {
                // Close in nose first: be one wheel-contact short of the ball, closing gently.
                float horizon = System.Math.Clamp(contact, 0.1f, 0.5f);
                Ball ahead = Ball.Prediction.TrySample(Game.Time + horizon, out Ball sample) ? sample : Ball.MainBall.Predict(horizon);
                Vec3 target = ahead.location - toBall * WheelContact;
                Vec3 acceleration = PossessionControl.FlightAtHorizon(car, target, ahead.velocity + toBall * ClosingSpeed, horizon);
                Vec3 nose = ControlMath.Unit(acceleration, car.Forward);
                Orient(bot, car, nose, -toBall);
                bot.Controller.Boost = boost.Step(Game.Time, acceleration.Dot(car.Forward), car.Forward.Dot(nose), car.Boost, false);
                return;
            }
            // Present the underside: roof away from the ball, nose along the aim across the line to it.
            Vec3 across = aim - toBall * aim.Dot(toBall);
            Vec3 nose2 = ControlMath.Unit(across, ControlMath.Unit(Vec3.Up - toBall * toBall.z, car.Forward));
            Orient(bot, car, nose2, -toBall);
            bot.Controller.Boost = false;
        }

        private void Shoot(RUBot bot, Car car, Vec3 delta, float distance, Vec3 aim)
        {
            if (float.IsFinite(firedAt))
            {
                // A short press dodges; after it, hands off so nothing cancels the flip.
                bot.Controller.Jump = Game.Time - firedAt < 0.05f;
                Finished = Game.Time - firedAt > 0.6f;
                return;
            }
            Vec3 toBall = delta / MathF.Max(distance, 1f);
            // Dodge once the nose is on the ball, close, and the ball would leave toward the aim.
            if (bot.Jump.CanDodge && distance < ShotStandoff + 60f && car.Forward.Dot(toBall) > 0.85f &&
                toBall.Flatten().Normalize().Dot(aim) > 0.6f)
            {
                firedAt = Game.Time;
                Vec3 local = ControlMath.FlatUnit(car.Local(toBall), new Vec3(1, 0, 0));
                bot.Controller.Jump = true;
                bot.Controller.Pitch = -local.x;
                bot.Controller.Yaw = local.y;
                bot.Controller.Roll = 0f;
                return;
            }
            // Fly to the spot behind the ball along the aim, nose on the ball.
            const float horizon = 0.2f;
            Ball ahead = Ball.Prediction.TrySample(Game.Time + horizon, out Ball sample) ? sample : Ball.MainBall.Predict(horizon);
            Vec3 target = ahead.location - (aim * 0.8f + Vec3.Up * 0.2f).Normalize() * ShotStandoff;
            Vec3 acceleration = PossessionControl.FlightAtHorizon(car, target, ahead.velocity, horizon);
            bool facing = car.Forward.Dot(toBall) > 0.6f;
            Vec3 nose = distance < 350f && facing ? toBall : ControlMath.Unit(acceleration, car.Forward);
            Orient(bot, car, nose, Vec3.Up);
            bot.Controller.Boost = boost.Step(Game.Time, acceleration.Dot(car.Forward), car.Forward.Dot(nose), car.Boost, false);
            if (Game.Time - confirmedAt > 1.5f) Finished = true;
        }

        private static void Orient(RUBot bot, Car car, Vec3 forward, Vec3 up)
        {
            RedUtils.Physics.AirInput air = RedUtils.Physics.AirControl.Orient(car.Forward, car.Right, car.Up,
                new Vec3(car.AngularVelocity.Dot(car.Forward), car.AngularVelocity.Dot(car.Right), car.AngularVelocity.Dot(car.Up)),
                forward, up);
            bot.Controller.Pitch = air.Pitch;
            bot.Controller.Yaw = air.Yaw;
            bot.Controller.Roll = air.Roll;
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
