using System;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;

namespace Bot
{
    /// <summary>Flick families, by how the ball leaves: flat and hard, or lofted.</summary>
    public enum FlickKind
    {
        /// <summary>Hard and low (about 20° up): a shot, or over a challenger from 600 uu out.</summary>
        Power,
        /// <summary>Softer and higher (about 26° up): a lob over a challenger close in.</summary>
        Lob,
    }

    /// <summary>
    /// One flick recipe: where the ball must sit on the roof, then the open-loop inputs. Found by
    /// the simulator's flick search (RocketSim, carries at 1000 and 1400 uu/s): the exit heading
    /// stays within a few degrees of the car's heading, so the car is aimed before the jump.
    /// </summary>
    public readonly struct FlickRecipe
    {
        /// <summary>Ball position on the roof, forward of the car origin, when the jump starts.</summary>
        public readonly float Spot;
        /// <summary>Jump hold, then the pause between release and dodge (seconds).</summary>
        public readonly float Hold, Wait;
        /// <summary>Stick held from the jump to the dodge: pitch, yaw, roll.</summary>
        public readonly float TiltPitch, TiltYaw, TiltRoll;
        /// <summary>Dodge stick: pitch -1 is a front flip, +1 a back flip.</summary>
        public readonly float DodgePitch, DodgeYaw;
        /// <summary>Measured exit speed above the carry speed, and heading offset from the car (deg).</summary>
        public readonly float Gain, Heading;
        /// <summary>How far off the centre line (uu) the ball may sit when the jump starts.</summary>
        public readonly float Tolerance;

        public FlickRecipe(float spot, float tolerance, float hold, float wait, float tiltPitch, float tiltYaw, float tiltRoll,
            float dodgePitch, float dodgeYaw, float gain, float heading)
        {
            Spot = spot; Tolerance = tolerance; Hold = hold; Wait = wait;
            TiltPitch = tiltPitch; TiltYaw = tiltYaw; TiltRoll = tiltRoll;
            DodgePitch = dodgePitch; DodgeYaw = dodgeYaw; Gain = gain; Heading = heading;
        }

        public static FlickRecipe For(FlickKind kind) => kind switch
        {
            // Nose down and rolling through the jump, then a slightly diagonal front flip. Robust to a
            // ball 6 uu off its spot: +1040 uu/s median (p10 +1000) from 950-1500 uu/s carries, 20° up.
            FlickKind.Power => new FlickRecipe(50f, 8f, 0.17f, 1f / 120f, -0.82f, -0.93f, -0.5f, -1f, -0.28f, 1040f, -1.1f),
            // Nose down, yawing and rolling, then a diagonal front flip from a centred ball: +509 uu/s
            // median, 26° up. (Back-flip lobs reach 35-48° but need the ball within 3 uu of the centre
            // line, which a carry does not hold reliably.)
            _ => new FlickRecipe(10f, 8f, 0.2f, 1f / 120f, -1f, 1f, 1f, -1f, -0.41f, 509f, 0.9f),
        };
    }

    /// <summary>
    /// A flick from a hood carry. First the carry moves the ball to the recipe's spot on the roof
    /// and points the car along the aim (a few tenths of a second); then the recipe's jump, tilt
    /// and dodge run open loop, with no flip cancel afterwards.
    /// </summary>
    public sealed class Flick : IPossessionAction
    {
        /// <summary>Heading error (rad) at which the carry counts as turned onto the aim.</summary>
        private const float TurnedTolerance = 0.05f;
        /// <summary>Longest turn onto the aim, then longest centring, before the flick goes anyway.</summary>
        private const float TurnLimit = 0.7f, CentreLimit = 0.35f, UrgentCentreLimit = 0.1f;
        private readonly float turnLimit, centreLimit;
        private readonly HoodCarry carry;
        private readonly FlickRecipe recipe;
        private readonly float started = Game.Time;
        private Vec3 aim;
        private float centring = float.NaN, jumped = float.NaN, released = float.NaN, dodged = float.NaN;

        public FlickKind Kind { get; }
        public bool Finished { get; private set; }
        public bool Interruptible => !float.IsFinite(jumped);
        public float ClaimTime => Game.Time + 0.3f;

        /// <param name="urgent">Under a challenge: no turn onto the aim, only a brief centring on the spot.</param>
        public Flick(FlickKind kind, Vec3 aim, HoodCarry carry = null, bool urgent = false)
        {
            Kind = kind;
            recipe = FlickRecipe.For(kind);
            this.aim = aim.Flatten().Normalize();
            this.carry = carry ?? new HoodCarry();
            turnLimit = urgent ? 0f : TurnLimit;
            centreLimit = urgent ? UrgentCentreLimit : CentreLimit;
        }

        /// <summary>Retargets a flick that has not jumped yet.</summary>
        public void Aim(Vec3 direction)
        {
            if (!float.IsFinite(jumped)) aim = direction.Flatten().Normalize();
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            float now = Game.Time;
            if (!float.IsFinite(jumped))
            {
                Setup(bot, car, now);
                return;
            }

            float elapsed = now - jumped;
            if (elapsed > 1.2f || (float.IsFinite(dodged) && car.IsGrounded && now - dodged > 0.2f))
            {
                Finished = true;
                return;
            }
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = false;
            if (!float.IsFinite(released))
            {
                if (elapsed < recipe.Hold)
                {
                    bot.Controller.Jump = true;
                    Tilt(bot);
                    return;
                }
                released = now;
            }
            if (!float.IsFinite(dodged))
            {
                // Dodge once the pause has passed and the jump button has been up for a tick.
                if (now - released >= recipe.Wait && now > released && bot.Jump.CanDodge)
                {
                    dodged = now;
                    bot.Controller.Jump = true;
                    bot.Controller.Pitch = recipe.DodgePitch;
                    bot.Controller.Yaw = recipe.DodgeYaw;
                    bot.Controller.Roll = 0f;
                    return;
                }
                Tilt(bot);
                if (now - released > recipe.Wait + 0.3f) Finished = true;
            }
            // After the dodge: hands off, so nothing cancels the flip.
        }

        private void Setup(RUBot bot, Car car, float now)
        {
            Vec3 local = car.Local(Ball.Location - car.Location);
            bool onRoof = car.IsGrounded && local.z > GroundCatch.RoofRest(car) - 25f &&
                MathF.Abs(local.x) < 110f && MathF.Abs(local.y) < 80f;
            if (!onRoof)
            {
                Finished = true;
                return;
            }
            // The recipe's exit heading is a few degrees off the car's: aim the car to cancel it.
            Vec3 target = GroundModel.Rotate(aim, -recipe.Heading * MathF.PI / 180f);
            float headingError = MathF.Abs(GroundModel.SignedAngle(car.Forward.Flatten(), target));
            // First turn the carry onto the aim (the ball rides off-centre to steer it), then centre
            // the ball at the recipe's spot: the recipes were found with the ball on the centre line.
            if (headingError > TurnedTolerance && now - started < turnLimit)
            {
                carry.SteerToward(car, Ball.MainBall, target);
                carry.Spot = new Vec3(recipe.Spot, carry.Spot.y, 0);
            }
            else
            {
                if (!float.IsFinite(centring)) centring = now;
                carry.Spot = new Vec3(recipe.Spot, 0, 0);
                Vec3 relative = car.Local(Ball.Velocity - car.Velocity);
                bool placed = MathF.Abs(local.x - recipe.Spot) < 8f && MathF.Abs(local.y) < recipe.Tolerance &&
                    relative.Flatten().Length() < 90f && MathF.Abs(relative.z) < 150f;
                if (placed || now - centring > centreLimit)
                {
                    jumped = now;
                    bot.Controller.Jump = true;
                    bot.Controller.Throttle = 1f;
                    Tilt(bot);
                    return;
                }
            }
            bot.Controller = carry.Step(car, Ball.MainBall, bot.DeltaTime, allowBoost: true);
        }

        private void Tilt(RUBot bot)
        {
            bot.Controller.Pitch = recipe.TiltPitch;
            bot.Controller.Yaw = recipe.TiltYaw;
            bot.Controller.Roll = recipe.TiltRoll;
        }
    }
}
