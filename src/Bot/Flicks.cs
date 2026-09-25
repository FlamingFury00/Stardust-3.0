using System;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;

namespace Bot
{
    /// <summary>Flick families, by how the ball leaves: flat and hard, or lofted.</summary>
    public enum FlickKind
    {
        /// <summary>Hard and low (about 21° up): a shot or a pass past a challenger.</summary>
        Power,
        /// <summary>Lofted (about 35° up): over a grounded challenger.</summary>
        Lob,
        /// <summary>Steep (about 48° up, musty-style back flip): over a challenger who has jumped.</summary>
        High,
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

        public FlickRecipe(float spot, float hold, float wait, float tiltPitch, float tiltYaw, float tiltRoll,
            float dodgePitch, float dodgeYaw, float gain, float heading)
        {
            Spot = spot; Hold = hold; Wait = wait;
            TiltPitch = tiltPitch; TiltYaw = tiltYaw; TiltRoll = tiltRoll;
            DodgePitch = dodgePitch; DodgeYaw = dodgeYaw; Gain = gain; Heading = heading;
        }

        public static FlickRecipe For(FlickKind kind) => kind switch
        {
            // Nose down through the jump, then a slightly diagonal front flip: 2240/2560 uu/s from 1000/1400.
            FlickKind.Power => new FlickRecipe(50f, 0.2f, 1f / 120f, -1f, 0f, 0f, -1f, 0.41f, 1160f, -1.0f),
            // Nose up and yawing, then a diagonal back flip: 2270/2655 uu/s, 35° up.
            FlickKind.Lob => new FlickRecipe(30f, 0.2f, 0.014f, 0.71f, 1f, 0.26f, 1f, 0.67f, 1255f, -3.4f),
            // Nose up with roll, then a shallow diagonal back flip: 2090/2520 uu/s, 48° up.
            _ => new FlickRecipe(10f, 0.182f, 1f / 120f, 1f, 0.1f, 0.67f, 1f, 0.31f, 1090f, -1.1f),
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
        private const float TurnLimit = 0.7f, CentreLimit = 0.35f;
        private readonly HoodCarry carry;
        private readonly FlickRecipe recipe;
        private readonly float started = Game.Time;
        private Vec3 aim;
        private float centring = float.NaN, jumped = float.NaN, released = float.NaN, dodged = float.NaN;

        public FlickKind Kind { get; }
        public bool Finished { get; private set; }
        public bool Interruptible => !float.IsFinite(jumped);
        public float ClaimTime => Game.Time + 0.3f;

        public Flick(FlickKind kind, Vec3 aim, HoodCarry carry = null)
        {
            Kind = kind;
            recipe = FlickRecipe.For(kind);
            this.aim = aim.Flatten().Normalize();
            this.carry = carry ?? new HoodCarry();
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
            if (headingError > TurnedTolerance && now - started < TurnLimit)
            {
                carry.SteerToward(car, Ball.MainBall, target);
                carry.Spot = new Vec3(recipe.Spot, carry.Spot.y, 0);
            }
            else
            {
                if (!float.IsFinite(centring)) centring = now;
                carry.Spot = new Vec3(recipe.Spot, 0, 0);
                Vec3 relative = car.Local(Ball.Velocity - car.Velocity);
                bool placed = MathF.Abs(local.x - recipe.Spot) < 8f && MathF.Abs(local.y) < 8f &&
                    relative.Flatten().Length() < 90f && MathF.Abs(relative.z) < 150f;
                if (placed || now - centring > CentreLimit)
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
