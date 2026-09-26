using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>One tick of aerial control.</summary>
    public struct AerialCommand
    {
        public AirInput Input;
        public bool Boost, Jump;
        public float Throttle;
    }

    /// <summary>Outcome of simulating the aerial guidance to its arrival time.</summary>
    public struct AerialResult
    {
        /// <summary>Distance from the target at the arrival time.</summary>
        public float Miss;
        public float BoostUsed;
        public FlightState Final;
        public bool Reached => Miss <= AerialGuidance.ReachTolerance;
    }

    /// <summary>
    /// Guidance for flying the car origin to a point at a given time. Each tick it predicts where
    /// the car coasts to under gravity (and any remaining takeoff jump forces), turns the nose along
    /// the average acceleration still needed, and boosts while that need is large and the nose is
    /// on it. Near arrival the nose swings to the requested contact direction. A car leaving the
    /// ground performs a fast aerial: jump, full hold, and a neutral-stick double jump.
    ///
    /// Planning simulates exactly this law on <see cref="AerialModel"/>, so a planned aerial is the
    /// aerial the car flies.
    /// </summary>
    public static class AerialGuidance
    {
        /// <summary>Largest arrival miss (uu) that still counts as reaching the target.</summary>
        public const float ReachTolerance = 45f;
        /// <summary>Seconds after takeoff at which the fast aerial's double jump is pressed.</summary>
        public const float SecondJumpAt = JumpModel.MaximumHold + 2f / RL.TickRate;
        /// <summary>Boost only while the nose is within this angle of the needed acceleration.</summary>
        private const float BoostAngle = 0.3f;
        /// <summary>Boost while the needed average acceleration exceeds this share of boost thrust.</summary>
        private const float BoostShare = 0.55f;
        /// <summary>Time before arrival from which the nose turns to the contact direction.</summary>
        private const float NoseTime = 0.25f;

        /// <summary>
        /// Control for a car at <paramref name="s"/> that must be at <paramref name="target"/> in
        /// <paramref name="remaining"/> seconds with its nose along <paramref name="nose"/>.
        /// <paramref name="sinceTakeoff"/> is the time since the takeoff jump, or negative when the
        /// aerial started in the air.
        /// </summary>
        public static AerialCommand Control(in FlightState s, float remaining, Vec3 target, Vec3 nose,
            float sinceTakeoff, bool doubleJump)
        {
            var command = new AerialCommand();
            bool taking = sinceTakeoff >= 0f;
            bool holding = taking && sinceTakeoff < JumpModel.MaximumHold;
            bool second = taking && doubleJump && sinceTakeoff >= SecondJumpAt && sinceTakeoff < SecondJumpAt + 1f / RL.TickRate;
            command.Jump = holding || second;

            Vec3 needed = RequiredAcceleration(s, remaining, target, sinceTakeoff, doubleJump);
            float magnitude = needed.Length();
            Vec3 direction = magnitude > 1f ? needed / magnitude : nose;
            // Close to contact with little correction left, present the nose for the hit.
            bool presenting = remaining < NoseTime && magnitude < BoostShare * RL.BoostAccelAir;
            Vec3 aim = presenting && nose.Length() > 0.5f ? nose : direction;

            command.Input = AirControl.Orient(s.Forward, s.Right, s.Up, s.LocalAngularVelocity, aim, Vec3.Up);
            bool aligned = s.Forward.Dot(direction) > MathF.Cos(BoostAngle);
            command.Boost = aligned && remaining > 0f && magnitude > BoostShare * RL.BoostAccelAir;
            command.Throttle = aligned ? System.Math.Clamp(magnitude / RL.AirThrottleAccel, 0f, 1f) : 0f;
            // A second jump press with the stick deflected would dodge instead.
            if (second) command.Input = default;
            return command;
        }

        /// <summary>
        /// Average acceleration, beyond gravity and the takeoff jump still to come, that brings the
        /// car to <paramref name="target"/> in <paramref name="remaining"/> seconds.
        /// </summary>
        public static Vec3 RequiredAcceleration(in FlightState s, float remaining, Vec3 target, float sinceTakeoff, bool doubleJump)
        {
            float t = MathF.Max(remaining, 1f / RL.TickRate);
            Vec3 coast = s.Position + s.Velocity * t + new Vec3(0f, 0f, 0.5f * RL.Gravity * t * t);
            if (sinceTakeoff >= 0f)
            {
                // Remaining jump hold and the pending double jump, along the car's up axis.
                float hold = MathF.Max(0f, MathF.Min(JumpModel.MaximumHold - MathF.Max(sinceTakeoff, JumpModel.MinimumTime), t));
                coast += s.Up * (RL.JumpHoldAccel * hold * (t - 0.5f * hold));
                float untilSecond = SecondJumpAt - sinceTakeoff;
                if (doubleJump && untilSecond >= 0f && untilSecond < t)
                    coast += s.Up * (RL.JumpImpulse * (t - untilSecond));
            }
            return (target - coast) * (2f / (t * t));
        }

        /// <summary>
        /// Simulates the guidance from <paramref name="start"/> for <paramref name="duration"/>
        /// seconds. A grounded start takes off at once with a fast aerial.
        /// </summary>
        public static AerialResult Simulate(FlightState start, bool grounded, float duration, Vec3 target, Vec3 nose,
            bool doubleJump, float dt = 1f / 60f)
        {
            FlightState s = start;
            float startBoost = s.Boost;
            float sinceTakeoff = grounded ? 0f : -1f;
            bool secondDone = false;
            if (grounded)
                s.Velocity += s.Up * (JumpModel.SpeedAfterMinimum - RL.Gravity * JumpModel.MinimumTime);

            while (s.Time < duration - 1e-4f)
            {
                // Resolve the takeoff at the game's tick rate; free flight is smooth.
                float step = MathF.Min(sinceTakeoff >= 0f && sinceTakeoff < SecondJumpAt + 0.02f ? 1f / RL.TickRate : dt,
                    duration - s.Time);
                AerialCommand c = Control(s, duration - s.Time, target, nose, sinceTakeoff, doubleJump && !secondDone);
                AerialModel.Step(ref s, c.Input, c.Boost, c.Throttle, step);
                if (sinceTakeoff >= 0f)
                {
                    if (sinceTakeoff >= JumpModel.MinimumTime && sinceTakeoff < JumpModel.MaximumHold)
                        s.Velocity += s.Up * (RL.JumpHoldAccel * step);
                    if (doubleJump && !secondDone && sinceTakeoff >= SecondJumpAt)
                    {
                        s.Velocity += s.Up * RL.JumpImpulse;
                        secondDone = true;
                    }
                    sinceTakeoff += step;
                }
            }
            return new AerialResult { Miss = (s.Position - target).Length(), BoostUsed = startBoost - s.Boost, Final = s };
        }
    }
}
