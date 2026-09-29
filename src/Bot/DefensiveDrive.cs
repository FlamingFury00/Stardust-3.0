using System;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;

namespace Bot
{
    /// <summary>
    /// Persistent defensive arrival controller. Unlike generic Drive, this controller can actually
    /// brake below the tight-turn 400 uu/s floor and follow a moving shadow target. Sustained
    /// retreats reorient only when reverse cannot serve the route and the swept path is clear.
    /// </summary>
    public sealed class DefensiveDrive : IAction
    {
        public static bool BoundedTurns = true;
        public bool Finished => false;
        public bool Interruptible => drive.Interruptible;
        public Vec3 Target
        {
            get => target;
            set
            {
                float elapsed = Game.Time - targetUpdatedAt;
                Vec3 measured = elapsed > 0 && elapsed <= 0.25f
                    ? (value - target).Flatten() / elapsed : Vec3.Zero;
                // Measure over planner updates, not 120 Hz action ticks. A role change is a target
                // jump, and a repeated stationary waypoint explicitly stops the velocity estimate.
                targetVelocity = measured.Length() <= 3000f ? measured : Vec3.Zero;
                target = value;
                targetUpdatedAt = Game.Time;
            }
        }
        public float CruiseSpeed { get; set; }
        public float TerminalSpeed { get; set; }
        public bool HoldPosition { get; set; }
        public bool AllowDodges { get; set; }
        public bool Holding { get; private set; }
        public string MobilityAction => drive.Action?.GetType().Name;

        private readonly Drive drive;
        private float sustainedReverse;
        private bool turningOutOfReverse;
        private Vec3 target, targetVelocity;
        private float targetUpdatedAt = float.NaN;

        public DefensiveDrive(Car car, Vec3 target, float cruiseSpeed = 1800f,
            float terminalSpeed = 0f, bool holdPosition = false, bool allowDodges = false)
        {
            Target = target;
            CruiseSpeed = cruiseSpeed;
            TerminalSpeed = terminalSpeed;
            HoldPosition = holdPosition;
            AllowDodges = allowDodges;
            drive = new Drive(car, target, MathF.Max(1f, cruiseSpeed), allowDodges, wasteBoost: false)
            {
                AllowHandbrake = false,
                DodgeMinSpeed = allowDodges ? 650f : 850f
            };
        }

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            if (!ControlMath.Finite(Target) || !ControlMath.Finite(car.Location) ||
                !ControlMath.Finite(car.Velocity))
            {
                bot.Controller = new RLBot.Flat.ControllerStateT();
                return;
            }

            float distance = car.Location.FlatDist(Target);
            bool onFloor = car.IsGrounded && car.Location.z < 250f && car.Up.z > 0.72f;
            if (HoldPosition)
            {
                sustainedReverse = 0f;
                turningOutOfReverse = false;
            }

            if (!HoldPosition)
                Holding = false;
            else
            {
                if (Holding && distance > 145f)
                    Holding = false;
                if (!Holding && onFloor && distance <= Defense.ArrivalRadius &&
                    car.Velocity.FlatLen() < 260f)
                    Holding = true;
            }

            if (Holding && onFloor)
            {
                // Throttle-to-zero invokes the normal brake controller rather than allowing a
                // perpetual low-speed orbit around a point that is already covered.
                bot.Throttle(0f);
                bot.Controller.Steer = 0f;
                bot.Controller.Boost = false;
                bot.Controller.Handbrake = false;
                bot.Controller.Jump = false;
                return;
            }

            float speed = onFloor
                ? Defense.DriveSpeed(car, Target, CruiseSpeed, TerminalSpeed)
                : (float.IsFinite(CruiseSpeed) ? System.Math.Clamp(CruiseSpeed, 0f, Car.MaxSpeed) : 0f);

            Vec3 flatForward = ControlMath.FlatUnit(car.Forward, Vec3.X);
            float forwardSpeed = car.Velocity.Dot(car.Forward);
            float along = (Target - car.Location).Dot(flatForward);
            if (Game.Time - targetUpdatedAt > 0.25f || Game.Time < targetUpdatedAt)
                targetVelocity = Vec3.Zero;
            Vec3 retreatRoute = ControlMath.FlatUnit(Target - car.Location, -flatForward);

            // Short reverse correction is preferable to a 180-degree loop in front of net.
            if (!turningOutOfReverse && onFloor && MathF.Abs(forwardSpeed) < 700f && distance < 1150f &&
                along < -MathF.Max(110f, distance * 0.62f))
                drive.Backwards = true;
            else if (onFloor && MathF.Abs(forwardSpeed) < 220f && (along > 170f || distance > 1650f))
                drive.Backwards = false;

            // A nearby ball in front of the nose is safe to shadow in reverse. Brake only for a
            // ball inside the actual reverse stopping corridor, where continuing could push it home.
            Vec3 toBall = (Ball.Location - car.Location).Flatten();
            float ballAlong = toBall.Dot(retreatRoute);
            float retreatSpeed = MathF.Max(0f, car.Velocity.Dot(retreatRoute));
            float closing = (car.Velocity - Ball.Velocity).Dot(retreatRoute);
            float stoppingRoom = retreatSpeed * retreatSpeed / (2f * Car.BrakeAccel) +
                MathF.Max(0f, closing) * 0.15f + 220f;
            bool reverseCollision = onFloor && drive.Backwards && forwardSpeed < -550f &&
                ballAlong > 0f && ballAlong < stoppingRoom && closing > 150f &&
                (toBall - retreatRoute * ballAlong).Length() < 260f && Ball.Location.z < 250f;
            if (reverseCollision && drive.Action == null)
            {
                turningOutOfReverse = true;
                drive.Backwards = false;
            }

            // Reverse is useful for a short net correction. A moving retreat target can otherwise
            // keep it latched indefinitely at reverse's speed limit, even with a full boost tank.
            bool longReverse = !HoldPosition && onFloor && drive.Backwards && forwardSpeed < -550f &&
                CruiseSpeed >= 1800f && distance > 450f &&
                (distance > 1400f || (TerminalSpeed >= 1000f && targetVelocity.Dot(retreatRoute) > 1550f));
            sustainedReverse = longReverse ? sustainedReverse + MathF.Min(bot.DeltaTime, 0.1f) : 0f;
            if (longReverse && drive.Action == null)
            {
                Vec3 route = retreatRoute;
                Vec3 recovery = car.Location + route * 2200f;
                float flightDistance = MathF.Max(1700f, -forwardSpeed * HalfFlip.Duration + 650f);
                bool clearCorridor = Defense.CanFastRecover(car, Ball.Location, recovery, bot.OurGoal.Location);
                bool canFlip = clearCorridor && forwardSpeed < -750f &&
                    -flatForward.Dot(route) > 0.94f &&
                    (car.Velocity - route * car.Velocity.Dot(route)).FlatLen() < 350f &&
                    ReverseFlightClear(car, route, flightDistance) &&
                    !float.IsFinite(Defense.GoalThreat(Ball.Prediction.Slices,
                        bot.OurGoal.Location, Game.Time, HalfFlip.Duration + 0.25f, out _));
                if (sustainedReverse >= 0.28f)
                {
                    if (canFlip)
                        drive.Action = new HalfFlip();
                    else
                    {
                        // Brake first through normal forward throttle, then steer onto the route.
                        // Keep this choice latched until turned: otherwise the short-reverse branch
                        // immediately selects reverse again while the car is decelerating.
                        turningOutOfReverse = true;
                        drive.Backwards = false;
                    }
                    sustainedReverse = 0f;
                }
            }
            if (turningOutOfReverse && onFloor && forwardSpeed >= 0f &&
                flatForward.Dot(ControlMath.FlatUnit(Target - car.Location, flatForward)) > 0.75f)
                turningOutOfReverse = false;

            bool fastTravel = AllowDodges && !HoldPosition && onFloor &&
                distance > 1350f && !drive.Backwards && CruiseSpeed >= 2050f;

            float turnLimit = !drive.Backwards && onFloor && drive.Action == null && BoundedTurns
                ? TurnSpeedLimit(car, Target, bot.OurGoal.Location, speed) : speed;
            drive.Target = Target;
            drive.TargetSpeed = MathF.Max(1f, turnLimit);
            drive.AllowDodges = fastTravel;
            drive.DodgeMinSpeed = fastTravel ? 650f : 850f;
            drive.AllowHandbrake = false;
            drive.WasteBoost = false;
            drive.Run(bot);

            bool mobilityCommitted = drive.Action != null && !drive.Action.Finished;

            // Generic Drive has a 400 uu/s minimum in its tight-turn branch. Override only that
            // terminal regime while preserving its mature route/surface steering everywhere else.
            if (!mobilityCommitted && onFloor && speed < 430f)
                bot.Throttle(speed, drive.Backwards);
            if (!mobilityCommitted && turnLimit < speed)
            {
                bot.Throttle(turnLimit);
                bot.Controller.Boost = false;
            }

            // Normal defensive driving suppresses jump/powerslide so a parking controller cannot
            // accidentally leave the ground. Once Drive has intentionally committed a speedflip,
            // dodge, wavedash, or half-flip, preserve that subaction's exact controller sequence.
            if (!mobilityCommitted)
            {
                bot.Controller.Handbrake = false;
                bot.Controller.Jump = false;
            }

            if (!mobilityCommitted && (speed < 1800f || distance < 950f || drive.Backwards))
                bot.Controller.Boost = false;
        }

        /// <summary>
        /// A short ground rollout bounds forward turns near our net. Generic Drive can accelerate
        /// through a large turning circle into the goal while trying to reach a field-side point.
        /// Brake enough to keep the swept turn shallow; normal aligned travel keeps its speed.
        /// </summary>
        public static float TurnSpeedLimit(Car car, Vec3 destination, Vec3 goal, float requested)
        {
            float side = goal.y < 0 ? -1f : 1f;
            float depth = car.Location.y * side, line = MathF.Abs(goal.y);
            Vec3 route = ControlMath.FlatUnit(destination - car.Location, car.Forward);
            float heading = car.Forward.FlatNorm().Dot(route);
            Vec3 lateral = car.Velocity.Flatten() - car.Forward.FlatNorm() * car.Velocity.Dot(car.Forward.FlatNorm());
            bool sliding = lateral.Length() > 200f;
            if (requested < 200f || depth < line - 900f || (heading > 0.75f && !sliding) || car.Forward.Dot(car.Velocity) < 0f)
                return requested;

            float maximumDepth = MathF.Max(depth + 20f,
                MathF.Min(line + Defense.ShallowNetDepth, destination.y * side + 160f));
            bool Clear(float targetSpeed)
            {
                var state = new GroundState(car.Location, car.Forward, car.Forward.Dot(car.Velocity),
                    car.Boost, yawRate: car.AngularVelocity.z);
                for (int step = 0; step < 48; step++)
                {
                    float angle = GroundModel.SignedAngle(state.Forward, destination - state.Position);
                    float response = 35f * (angle - state.YawRate * 0.01f);
                    float steer = System.Math.Clamp(response * response * response / 10f, -1f, 1f);
                    float difference = targetSpeed - state.Speed;
                    float throttle = System.Math.Clamp(difference * MathF.Abs(difference) / 1000f, -1f, 1f);
                    GroundModel.Step(ref state, throttle, steer, false, 1f / 60f);
                    // GroundModel has no lateral state. Preserve a coasting slip envelope rather
                    // than treating a sideways landing as stationary; measured grip releases it.
                    Vec3 swept = state.Position + lateral * state.Time;
                    if (swept.y * side > maximumDepth || !Field.InField(swept, 70f))
                        return false;
                }
                return true;
            }
            if (Clear(requested)) return requested;
            for (float candidate = MathF.Min(1100f, requested - 150f); candidate >= 200f; candidate -= 150f)
                if (Clear(candidate)) return candidate;
            return MathF.Min(100f, requested);
        }

        private static bool ReverseFlightClear(Car car, Vec3 route, float flightDistance)
        {
            // A ball currently behind the retreat can still catch the car during the uninterruptible
            // flip. Sweep between coasting and an additional 650 uu of backward-dodge travel, rather
            // than assuming the flip preserves the initial velocity. Include the final landing time.
            // A 6000 uu/s cross-field ball can traverse the old 600 uu clearance between two
            // 0.2 s samples. At 32 Hz its inter-sample travel fits inside the swept-path margin.
            const int samples = 40;
            for (int step = 1; step <= samples; step++)
            {
                float time = HalfFlip.Duration * step / samples;
                if (!Field.InField(car.Location + route * flightDistance * (time / HalfFlip.Duration), 250f))
                    return false;
                Ball future = Ball.Prediction.TrySample(Game.Time + time, out Ball sample)
                    ? sample : Ball.MainBall.Predict(time);
                Vec3 coast = car.Location + car.Velocity * time;
                float extra = 650f * time / HalfFlip.Duration;
                float along = System.Math.Clamp((future.location - coast).Flatten().Dot(route), 0, extra);
                if (future.location.z < 500f && future.location.FlatDist(coast + route * along) < 600f)
                    return false;
            }
            return true;
        }
    }
}
