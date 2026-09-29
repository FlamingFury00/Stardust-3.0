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
        /// <summary>Boost needed to be worth the run: about two thirds of a second of thrust. <see cref="Plan"/> decides whether it is enough for a given run.</summary>
        public static float MinBoost = 22f;
        /// <summary>Flat distance (uu) beyond which no run is planned.</summary>
        public static float MaxRange = 2600f;
        /// <summary>Longest run (s) planned: an opponent's path is only predictable over about a second.</summary>
        public static float MaxTime = 1.25f;
        /// <summary>Speed (uu/s) at which a car turns supersonic and its bumper demolishes.</summary>
        public const float SupersonicSpeed = 2200f;
        /// <summary>Speed (uu/s) required at contact: supersonic with a margin for the bump itself.</summary>
        public static float ContactSpeed = 2260f;
        /// <summary>Lowest <see cref="Value"/> worth a run: a target far from the ball and not attacking is left alone.</summary>
        public static float MinValue = 0.15f;
        /// <summary>Largest angle (rad) between the nose and the run when it starts.</summary>
        public static float MaxLaunchAngle = 0.4f;
        /// <summary>Centre-to-centre distance (uu) at which two hitboxes meet head on.</summary>
        public const float ContactReach = 118f;
        /// <summary>Tallest opponent (uu) worth chasing: an airborne car cannot be met with the bumper.</summary>
        public const float MaxTargetHeight = 190f;

        /// <summary>A run: the meeting point, the time to it and the speed the car arrives with.</summary>
        public readonly record struct DemoRun(Vec3 Point, float Time, float Speed);

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
        public static DemoRun? Plan(Car car, Car target)
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
                return new DemoRun(meeting, t, speed);
            }

            return null;
        }

        /// <summary>
        /// How much a demolition is worth against <paramref name="target"/>: only opponents on or near
        /// the ball, or carrying it toward our goal, are worth a run, and a quick run beats a slow one.
        /// </summary>
        public static float Value(Car target, Vec3 ball, Vec3 ownGoal, in DemoRun run)
        {
            float toBall = target.Location.FlatDist(ball);
            float value = 0f;
            if (toBall < 900f)
                value += 1.2f;
            else if (toBall < 1800f)
                value += 0.5f;

            Vec3 goalward = ControlMath.FlatUnit(ownGoal - ball, Vec3.Y);
            if (target.Velocity.Flatten().Dot(goalward) > 600f && toBall < 1600f)
                value += 0.6f;
            return value - 0.35f * run.Time;
        }

        /// <summary>
        /// The best target among <paramref name="opponents"/>, with its run, or null. An opponent a
        /// teammate stands clearly nearer to is left to that teammate: two cars on one target leave
        /// the rest of the pitch empty.
        /// </summary>
        public static (Car Target, DemoRun Run)? Choose(Car car, IEnumerable<Car> opponents, Vec3 ball, Vec3 ownGoal,
            IEnumerable<Car> teammates = null)
        {
            (Car, DemoRun)? best = null;
            float bestValue = MinValue;
            foreach (Car opponent in opponents)
            {
                DemoRun? run = Plan(car, opponent);
                if (run == null || TeammateNearer(car, opponent, teammates))
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

        /// <summary>Share of this car's own distance under which a teammate is nearer enough to own the target.</summary>
        private const float TeammateNearShare = 0.85f;

        private static bool TeammateNearer(Car car, Car target, IEnumerable<Car> teammates)
        {
            if (teammates == null)
                return false;
            float mine = car.Location.FlatDist(target.Location);
            foreach (Car mate in teammates)
                if (mate != null && !mate.IsDemolished && mate.Location.FlatDist(target.Location) < mine * TeammateNearShare)
                    return true;
            return false;
        }
    }

    /// <summary>Drives flat out at a moving opponent until it is demolished, escapes, or the run goes stale.</summary>
    public sealed class DemoAttack : IAction
    {
        private readonly int targetIndex;
        private readonly float started = Game.Time;
        private float lostSince = float.NaN;
        private bool imminent;
        public bool Finished { get; private set; }
        /// <summary>A run within a moment of contact is not called off: a bump one tick early demolishes nothing.</summary>
        public bool Interruptible => !imminent;
        public int TargetIndex => targetIndex;
        /// <summary>Time to contact (s) from which the run can no longer be interrupted.</summary>
        public static float CommitTime = 0.5f;
        /// <summary>How long (s) a run without a valid plan is kept before the car gives up.</summary>
        public static float Patience = 0.72f;
        /// <summary>Alignment (cosine), closing speed (uu/s) and distance (uu) that keep a run alive without a plan.</summary>
        private const float HoldAlignment = 0.92f, HoldClosingSpeed = 900f, HoldRange = 2000f;
        /// <summary>Boost is only fed while the nose is within this many radians of the aim.</summary>
        private const float BoostAlignment = 0.32f;
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

            Demolition.DemoRun? run = Demolition.Plan(car, target);
            if (run == null)
            {
                // Once under way the run is judged on the geometry alone: the planner needs time to
                // build speed, which a car already at speed and closing no longer has.
                Vec3 toTarget = target.Location.Flatten() - car.Location.Flatten();
                Vec3 heading = ControlMath.FlatUnit(car.Forward, Vec3.X);
                float alignment = heading.Dot(ControlMath.FlatUnit(toTarget, heading));
                float closing = (car.Velocity - target.Velocity).Flatten().Dot(ControlMath.FlatUnit(toTarget, heading));
                // Only a run that can still be supersonic at contact is worth finishing: a bump below
                // 2200 uu/s costs the car its speed and demolishes nothing.
                bool supersonicAtContact = car.IsSupersonic ||
                    Demolition.Advance(car.Velocity.Dot(heading), car.Boost, 0.2f).Speed >= Demolition.SupersonicSpeed + 5f;
                float distance = toTarget.Length();
                bool closingIn = alignment > HoldAlignment && closing > HoldClosingSpeed && distance < HoldRange &&
                    supersonicAtContact;
                lostSince = float.IsNaN(lostSince) ? Game.Time : lostSince;
                if (!closingIn || Game.Time - lostSince > Patience)
                {
                    Finished = true;
                    return;
                }
                float contact = MathF.Max(0f, distance - Demolition.ContactReach) / closing;
                run = new Demolition.DemoRun(target.Location + target.Velocity * contact, contact, car.Velocity.Length());
            }
            else
                lostSince = float.NaN;

            imminent = run.Value.Time <= CommitTime && car.IsSupersonic;
            Vec3 aim = run.Value.Point;
            float[] angles = bot.AimAt(aim);
            float error = MathF.Abs(angles[1]);
            bot.Controller.Throttle = 1f;
            bot.Controller.Boost = car.Boost > 0f && error < BoostAlignment && car.Velocity.Length() < Car.MaxSpeed - 10f;
            bot.Controller.Handbrake = false;
            bot.Controller.Jump = false;
        }
    }
}
