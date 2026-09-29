using System;

namespace RedUtils.Physics
{
    /// <summary>Throttle and boost for one tick.</summary>
    public readonly struct LongitudinalInput
    {
        public readonly float Throttle;
        public readonly bool Boost;
        public LongitudinalInput(float throttle, bool boost) { Throttle = throttle; Boost = boost; }
    }

    /// <summary>
    /// Realises a requested forward acceleration on a grounded car. The actuators are partly discrete:
    /// throttle torque is continuous (0 up to the torque curve), but letting go coasts at -525 uu/s²,
    /// any reverse throttle brakes at -3500 uu/s², and boost adds 991.7 uu/s² for at least 0.1 s.
    /// Requests between those levels are dithered tick by tick (error diffusion), so the average
    /// acceleration matches the request instead of snapping to the nearest level. That is what a
    /// hood carry or a catch needs: a small negative throttle is otherwise a full brake.
    /// </summary>
    public sealed class SpeedActuator
    {
        /// <summary>Throttle below this coasts (RocketSim's input deadzone).</summary>
        private const float Deadzone = 0.01f;
        private float owed;          // uu/s of speed change requested but not yet delivered
        private float boostHeld;     // seconds the current boost burst has lasted
        private bool boosting;

        public void Reset()
        {
            owed = 0f;
            boostHeld = 0f;
            boosting = false;
        }

        /// <param name="speed">Signed forward speed.</param>
        /// <param name="acceleration">Requested forward acceleration (uu/s²).</param>
        /// <param name="fuel">Boost left.</param>
        /// <param name="allowBoost">Whether boost may be used to reach the request.</param>
        /// <param name="steer">This tick's steer input, for the speed it costs.</param>
        /// <param name="dt">Tick length.</param>
        public LongitudinalInput Step(float speed, float acceleration, float fuel, bool allowBoost, float steer, float dt)
        {
            if (!float.IsFinite(speed) || !float.IsFinite(acceleration) || dt <= 0f)
            {
                Reset();
                return new LongitudinalInput(0f, false);
            }

            float drag = MathF.Sign(speed) * GroundModel.TurnDragAccel(speed, steer, 1f);
            // Aim at the request plus whatever earlier ticks under- or over-delivered.
            float target = acceleration + owed / dt;
            float engine = RL.ThrottleAccel(speed);
            bool canBoost = allowBoost && fuel > 0f && speed >= -1f && speed < RL.CarMaxSpeed - 5f;

            LongitudinalInput input;
            float delivered;
            if (boosting && boostHeld < RL.BoostMinimumTime && fuel > 0f)
            {
                // A burst shorter than 0.1 s is extended by the game anyway: account for it honestly.
                float throttle = Throttle(target - RL.BoostAccelGround + drag, engine);
                input = new LongitudinalInput(throttle, true);
            }
            else if (canBoost && target > engine + 0.5f * RL.BoostAccelGround)
            {
                float throttle = Throttle(target - RL.BoostAccelGround + drag, engine);
                input = new LongitudinalInput(throttle, true);
            }
            else if (target >= -0.5f * RL.CoastBrakeAccel)
                // Between coasting and full throttle the engine torque is continuous.
                input = new LongitudinalInput(Throttle(target + drag, engine), false);
            else if (target >= -0.5f * (RL.CoastBrakeAccel + RL.BrakeAccel) || speed < 25f)
                input = new LongitudinalInput(0f, false);
            else
                input = new LongitudinalInput(-1f, false);

            delivered = GroundModel.Acceleration(speed, input.Throttle, input.Boost, fuel, steer);
            owed = System.Math.Clamp(owed + (acceleration - delivered) * dt, -RL.BrakeAccel * dt, RL.BrakeAccel * dt);
            boostHeld = input.Boost ? (boosting ? boostHeld + dt : dt) : 0f;
            boosting = input.Boost;
            return input;
        }

        /// <summary>Forward throttle giving the requested engine acceleration; never inside the coasting deadzone.</summary>
        private static float Throttle(float acceleration, float engine) =>
            engine <= 1f ? 1f : System.Math.Clamp(acceleration / engine, Deadzone, 1f);
    }
}
