using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Demolitions as a pressure tool. A supersonic car that meets an opponent with its bumper
    /// removes it for three seconds, which breaks a carry, opens a net or wins a 50/50 outright. The
    /// planner looks for a point on the opponent's predicted path that this car can reach at supersonic
    /// speed, and the action drives there flat out.
    /// </summary>
    public static class Demolition
    {
        /// <summary>Boost needed to be worth the run: reaching and holding supersonic burns about a third of a tank.</summary>
        public static float MinBoost = 22f;
        /// <summary>Flat distance (uu) beyond which no run is planned.</summary>
        public static float MaxRange = 2600f;
        /// <summary>Longest run (s) planned: an opponent's path is only predictable over about a second.</summary>
        public static float MaxTime = 1.25f;
        /// <summary>Speed (uu/s) required at contact: supersonic starts at 2200.</summary>
        public static float ContactSpeed = 2215f;
        /// <summary>Largest angle (rad) between the nose and the run when it starts.</summary>
        public static float MaxLaunchAngle = 0.5f;
        /// <summary>Centre-to-centre distance (uu) at which two hitboxes meet head on.</summary>
        public const float ContactReach = 118f;
        /// <summary>Tallest opponent (uu) worth chasing: an airborne car cannot be met with the bumper.</summary>
        public const float MaxTargetHeight = 190f;

        /// <summary>A run: the meeting point, the time to it and the speed the car arrives with.</summary>
        public readonly record struct Run(Vec3 Point, float Time, float Speed, float Angle);

        /// <summary>
        /// Distance covered and speed reached after driving flat out for <paramref name="time"/>
        /// seconds with <paramref name="boost"/> in the tank: throttle acceleration plus boost
        /// while fuel lasts, capped at the top speed.
        /// </summary>
        public static (float Distance, float Speed) Advance(float speed, float boost, float time)
        {
            const float dt = 0.02f;
            float covered = 0f;
            float fuel = System.Math.Clamp(boost, 0f, 100f);
            speed = MathF.Max(0f, speed);
            for (float t = 0f; t < time - 1e-4f; t += dt)
            {
                float step = MathF.Min(dt, time - t);
                bool boosting = fuel > 0f && speed < Car.MaxSpeed;
                float acceleration = DrivePhysics.ThrottleAcceleration(speed) + (boosting ? Car.BoostAccel : 0f);
                float next = MathF.Min(Car.MaxSpeed, speed + acceleration * step);
                covered += (speed + next) * 0.5f * step;
                speed = next;
                if (boosting)
                    fuel = MathF.Max(0f, fuel - Car.BoostConsumption * step);
            }
            return (covered, speed);
        }

        /// <summary>
        /// The earliest point on the target's straight-line path where the car can arrive supersonic,
        /// or null. The target is assumed to keep its velocity; the car drives flat out along a heading
        /// that is at most <see cref="MaxLaunchAngle"/> from its nose.
        /// </summary>
        public static Run? Plan(Car car, Car target)
        {
            if (car == null || target == null || car.IsDemolished || target.IsDemolished ||
                !car.IsGrounded || car.Up.z < 0.85f || car.Boost < MinBoost ||
                !ControlMath.Finite(car.Location) || !ControlMath.Finite(car.Velocity) ||
                !ControlMath.Finite(target.Location) || !ControlMath.Finite(target.Velocity) ||
                target.Location.z > MaxTargetHeight || target.Team == car.Team)
                return null;

            Vec3 origin = car.Location.Flatten();
            if (origin.FlatDist(target.Location) > MaxRange)
                return null;

            Vec3 forward = ControlMath.FlatUnit(car.Forward, Vec3.X);
            float forwardSpeed = car.Velocity.Dot(forward);
            if (forwardSpeed < -150f)
                return null;

            for (float t = 0.2f; t <= MaxTime + 1e-3f; t += 0.05f)
            {
                Vec3 meeting = target.Location.Flatten() + target.Velocity.Flatten() * t;
                if (!Field.InField(meeting, 250f))
                    return null;

                Vec3 route = meeting - origin;
                float distance = route.Length() - ContactReach;
                if (distance <= 0f)
                    continue;

                var (covered, speed) = Advance(forwardSpeed, car.Boost, t);
                if (covered < distance)
                    continue;

                // The first moment the car could be there: it must arrive at speed, aligned.
                if (speed < ContactSpeed)
                    return null;
                float angle = MathF.Acos(System.Math.Clamp(forward.Dot(ControlMath.FlatUnit(route, forward)), -1f, 1f));
                if (angle > MaxLaunchAngle)
                    return null;
                return new Run(meeting, t, speed, angle);
            }

            return null;
        }

        /// <summary>
        /// How much a demolition is worth against <paramref name="target"/>: opponents on the ball, or
        /// carrying it toward our goal, matter most, and a quick run beats a slow one.
        /// </summary>
        public static float Value(Car target, Vec3 ball, Vec3 ownGoal, in Run run)
        {
            float toBall = target.Location.FlatDist(ball);
            float value = 1f;
            if (toBall < 900f)
                value += 1.2f;
            else if (toBall < 1800f)
                value += 0.5f;

            Vec3 goalward = ControlMath.FlatUnit(ownGoal - ball, Vec3.Y);
            if (target.Velocity.Flatten().Dot(goalward) > 600f && toBall < 1600f)
                value += 0.6f;
            return value - 0.35f * run.Time;
        }

        /// <summary>The best target among <paramref name="opponents"/>, with its run, or null.</summary>
        public static (Car Target, Run Run)? Choose(Car car, IEnumerable<Car> opponents, Vec3 ball, Vec3 ownGoal)
        {
            (Car, Run)? best = null;
            float bestValue = float.NegativeInfinity;
            foreach (Car opponent in opponents)
            {
                Run? run = Plan(car, opponent);
                if (run == null)
                    continue;
                float value = Value(opponent, ball, ownGoal, run.Value);
                if (value > bestValue)
                {
                    bestValue = value;
                    best = (opponent, run.Value);
                }
            }
            return best;
        }
    }

    /// <summary>Drives flat out at a moving opponent until it is demolished, escapes, or the run goes stale.</summary>
    public sealed class DemoAttack : IAction
    {
        private readonly int targetIndex;
        private readonly float started = Game.Time;
        private float lostSince = float.NaN;
        public bool Finished { get; private set; }
        public bool Interruptible => true;
        public int TargetIndex => targetIndex;
        /// <summary>How long (s) a run without a valid plan is kept before the car gives up.</summary>
        public static float Patience = 0.18f;
        /// <summary>Longest a single run lasts (s).</summary>
        public static float MaxDuration = 1.9f;

        public DemoAttack(Car target) => targetIndex = target.Index;

        public void Run(RUBot bot)
        {
            Car car = bot.Me;
            Car target = Cars.AllCars.Find(c => c.Index == targetIndex);
            if (target == null || target.IsDemolished || car.IsDemolished || !car.IsGrounded ||
                Game.Time - started > MaxDuration)
            {
                Finished = true;
                return;
            }

            Demolition.Run? run = Demolition.Plan(car, target);
            if (run == null)
            {
                // Once under way the run is judged on the geometry alone: the planner needs time to
                // build speed, which a car already at speed and closing no longer has.
                Vec3 toTarget = target.Location.Flatten() - car.Location.Flatten();
                Vec3 heading = ControlMath.FlatUnit(car.Forward, Vec3.X);
                float alignment = heading.Dot(ControlMath.FlatUnit(toTarget, heading));
                float closing = (car.Velocity - target.Velocity).Flatten().Dot(ControlMath.FlatUnit(toTarget, heading));
                bool closingIn = alignment > 0.92f && closing > 900f && toTarget.Length() < 2000f &&
                    car.Velocity.Length() > 1500f;
                lostSince = float.IsNaN(lostSince) ? Game.Time : lostSince;
                if (!closingIn || Game.Time - lostSince > Patience * 4f)
                {
                    Finished = true;
                    return;
                }
                run = new Demolition.Run(target.Location + target.Velocity * 0.12f, 0.12f, car.Velocity.Length(), 0f);
            }
            else
                lostSince = float.NaN;

            Vec3 aim = run.Value.Point;
            float[] angles = bot.AimAt(aim);
            float error = MathF.Abs(angles[1]);
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = car.Boost > 0f && error < 0.32f && car.Velocity.Length() < Car.MaxSpeed - 10f;
            bot.Controller.Handbrake = false;
            bot.Controller.Jump = false;
        }
    }
}
