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

    /// <summary>What a flick adds to the carried ball's velocity (uu/s): along the aim, and upward.</summary>
    public readonly struct FlickImpulse
    {
        public readonly float Push, Lift;
        public FlickImpulse(float push, float lift) { Push = push; Lift = lift; }
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
        /// <summary>
        /// The weakest and strongest impulse the flick gave the ball in the mechanics lab (10th and
        /// 90th percentile over carries at 900-1700 uu/s, ball placed on the spot): how hard the
        /// ball leaves depends on which part of the car meets it. A shot must work at both ends.
        /// </summary>
        public readonly FlickImpulse Weakest, Strongest;
        /// <summary>Exit heading offset from the car (deg).</summary>
        public readonly float Heading;
        /// <summary>How far off the centre line (uu) the ball may sit when the jump starts.</summary>
        public readonly float Tolerance;

        public FlickRecipe(float spot, float tolerance, float hold, float wait, float tiltPitch, float tiltYaw, float tiltRoll,
            float dodgePitch, float dodgeYaw, FlickImpulse weakest, FlickImpulse strongest, float heading)
        {
            Spot = spot; Tolerance = tolerance; Hold = hold; Wait = wait;
            TiltPitch = tiltPitch; TiltYaw = tiltYaw; TiltRoll = tiltRoll;
            DodgePitch = dodgePitch; DodgeYaw = dodgeYaw; Weakest = weakest; Strongest = strongest; Heading = heading;
        }

        public static FlickRecipe For(FlickKind kind) => kind switch
        {
            // Nose down and rolling through the jump, then a slightly diagonal front flip. Robust to a
            // ball 6 uu off its spot: the ball leaves 20° up, 550-830 uu/s faster than the carry and
            // lifted 640-930 uu/s.
            FlickKind.Power => new FlickRecipe(50f, 8f, 0.17f, 1f / 120f, -0.82f, -0.93f, -0.5f, -1f, -0.28f,
                new FlickImpulse(550f, 640f), new FlickImpulse(830f, 930f), -1.1f),
            // Nose down, yawing and rolling, then a diagonal front flip from a centred ball: the ball
            // keeps the carry's speed and leaves lifted 530-870 uu/s, so 20-31° up whatever the pace.
            // (Back-flip lobs reach 35-48° but need the ball within 3 uu of the centre line, which
            // a carry does not hold reliably.)
            _ => new FlickRecipe(10f, 8f, 0.2f, 1f / 120f, -1f, 1f, 1f, -1f, -0.41f,
                new FlickImpulse(5f, 530f), new FlickImpulse(270f, 870f), 0.9f),
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
        public static float TurnLimit = 0.7f, CentreLimit = 1.0f, UrgentCentreLimit = 0.1f;
        private readonly float turnLimit, centreLimit;
        private bool threatened;
        private readonly HoodCarry carry;
        private readonly FlickRecipe recipe;
        private readonly float started = Game.Time;
        private Vec3 aim;
        private Vec3? worldTarget;
        private float centring = float.NaN, jumped = float.NaN, released = float.NaN, dodged = float.NaN;

        public FlickKind Kind { get; }
        /// <summary>Whether the ball sat on the recipe's spot, at rest on the roof, when the jump started.</summary>
        public bool PlacedAtJump { get; private set; }
        /// <summary>The ball's offset from the recipe's spot, in the car's frame, when the jump started.</summary>
        public Vec3 OffsetAtJump { get; private set; }
        /// <summary>Whether the jump has started: from here the flick runs open loop.</summary>
        public bool HasJumped => float.IsFinite(jumped);
        public bool Finished { get; private set; }
        public bool Interruptible => !float.IsFinite(jumped);
        public float ClaimTime => Game.Time + 0.3f;

        /// <param name="urgent">Under a challenge: no turn onto the aim, only a brief centring on the spot.</param>
        public Flick(FlickKind kind, Vec3 aim, HoodCarry carry = null, bool urgent = false)
            : this(kind, aim, carry, urgent, null) { }

        /// <param name="worldTarget">An unpressured finish re-aims at this point while carrying forward during setup.</param>
        public Flick(FlickKind kind, Vec3 aim, HoodCarry carry, bool urgent, Vec3? worldTarget)
        {
            Kind = kind;
            recipe = FlickRecipe.For(kind);
            this.aim = aim.Flatten().Normalize();
            this.worldTarget = worldTarget;
            threatened = urgent;
            this.carry = carry ?? new HoodCarry();
            turnLimit = urgent ? 0f : TurnLimit;
            centreLimit = urgent ? UrgentCentreLimit : CentreLimit;
        }

        /// <summary>Retargets a flick that has not jumped yet.</summary>
        public void Aim(Vec3 direction)
        {
            if (!float.IsFinite(jumped))
            {
                worldTarget = null;
                aim = direction.Flatten().Normalize();
            }
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
            if (worldTarget is Vec3 targetPoint && ControlMath.Finite(targetPoint))
                aim = ControlMath.FlatUnit(targetPoint - Ball.Location, aim);
            Vec3 local = car.Local(Ball.Location - car.Location);
            bool onRoof = car.IsGrounded && local.z > GroundCatch.RoofRest(car) - 25f &&
                MathF.Abs(local.x) < 110f && MathF.Abs(local.y) < 80f;
            if (!onRoof)
            {
                Finished = true;
                return;
            }
            // Free setup is not a commitment to ignore a defender who arrives later. Preserve
            // the short contested sequence once any opponent is within its execution window.
            threatened |= PossessionControl.MostImminent(Ball.MainBall, aim, bot.LivingOpponents).Contact < 0.6f;
            // The recipe's exit heading is a few degrees off the car's: aim the car to cancel it.
            Vec3 target = GroundModel.Rotate(aim, -recipe.Heading * MathF.PI / 180f);
            float headingError = MathF.Abs(GroundModel.SignedAngle(car.Forward.Flatten(), target));
            // First turn the carry onto the aim (the ball rides off-centre to steer it), then centre
            // the ball at the recipe's spot: the recipes were found with the ball on the centre line.
            if (headingError > TurnedTolerance && now - started < (threatened ? 0f : turnLimit))
            {
                centring = float.NaN;
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
                if (placed || now - centring > (threatened ? UrgentCentreLimit : centreLimit))
                {
                    PlacedAtJump = placed;
                    OffsetAtJump = new Vec3(local.x - recipe.Spot, local.y, 0f);
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

    /// <summary>
    /// Takes a hood carry into the air: the ball is moved forward on the roof, a short jump pops it
    /// off the nose, and the car pitches up and boosts to fly under it, then hands over to
    /// <see cref="AerialCarry"/>. This is how pros start an air dribble from the ground; without it
    /// an aerial carry only ever begins by accident.
    /// </summary>
    public sealed class AirDribbleSetup : IPossessionAction
    {
        /// <summary>
        /// Ball spot on the roof at takeoff, jump hold (the full 0.2 s: shorter pops leave the car
        /// under a ball it cannot follow), nose-up stick during the hold, the nose pitch (rad above the
        /// horizon) flown under boost, when the carry takes over, the alignment needed to boost, and
        /// the longest wait for the ball to reach its spot. Tuned in the mechanics lab (hood-to-air:
        /// 60/60 into the air, median 1.8 s carried).
        /// </summary>
        private static float Spot = 30f, Hold = 0.2f, PopPitch = 1f, FlyPitch = 0.75f, Handover = 0.3f,
            BoostAlignment = 0.5f, PlaceTime = 0.3f;
        private readonly HoodCarry carry;
        private readonly float started = Game.Time;
        private Vec3 lane;
        private float jumped = float.NaN;
        private readonly BoostGate boost = new();

        public bool Finished { get; private set; }
        public bool Interruptible => !float.IsFinite(jumped);
        public float ClaimTime => Game.Time + 0.4f;

        public AirDribbleSetup(Vec3 lane, HoodCarry carry = null)
        {
            this.lane = lane.Flatten().Normalize();
            this.carry = carry ?? new HoodCarry();
        }

        /// <summary>A settled carry at a pace the pop can follow, with boost for the flight and room ahead.</summary>
        public static bool CanStart(Car car, Ball ball) =>
            PossessionControl.HasControlledPossession(car, ball) && car.Boost >= 45f &&
            car.Velocity.Dot(car.Forward) is > 600f and < 1500f;

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            float now = Game.Time;
            if (!float.IsFinite(jumped))
            {
                Vec3 local = car.Local(Ball.Location - car.Location);
                if (!car.IsGrounded || local.z < GroundCatch.RoofRest(car) - 25f || MathF.Abs(local.y) > 80f)
                {
                    Finished = true;
                    return;
                }
                carry.SteerToward(car, Ball.MainBall, lane);
                carry.Spot = new Vec3(Spot, carry.Spot.y, 0);
                bool placed = MathF.Abs(local.x - Spot) < 10f && MathF.Abs(local.y) < 15f;
                if (placed || now - started > PlaceTime)
                {
                    jumped = now;
                    bot.Controller.Jump = true;
                    bot.Controller.Pitch = PopPitch;
                    bot.Controller.Throttle = 1f;
                    return;
                }
                bot.Controller = carry.Step(car, Ball.MainBall, bot.DeltaTime, allowBoost: true);
                return;
            }

            float elapsed = now - jumped;
            if (elapsed >= Handover)
            {
                bot.Action = new AerialCarry();
                bot.Action.Run(bot);
                return;
            }
            bot.Controller.Throttle = 1f;
            bot.Controller.Jump = elapsed < Hold;
            if (elapsed < Hold)
            {
                bot.Controller.Pitch = PopPitch;
                return;
            }
            // Pitch the nose up along the lane and boost under the ball.
            Vec3 nose = (lane * MathF.Cos(FlyPitch) + Vec3.Up * MathF.Sin(FlyPitch)).Normalize();
            RedUtils.Physics.AirInput air = RedUtils.Physics.AirControl.Orient(car.Forward, car.Right, car.Up,
                new Vec3(car.AngularVelocity.Dot(car.Forward), car.AngularVelocity.Dot(car.Right), car.AngularVelocity.Dot(car.Up)),
                nose, Vec3.Up);
            bot.Controller.Pitch = air.Pitch;
            bot.Controller.Yaw = air.Yaw;
            bot.Controller.Roll = air.Roll;
            bot.Controller.Boost = car.Forward.Dot(nose) > BoostAlignment && car.Boost > 0f;
        }
    }
}
