using System;
using System.Collections.Generic;
using RedUtils.Math;
using RedUtils.Physics;

namespace RedUtils.Planning
{
    public enum StrikeKind
    {
        Ground,
        Jump,
        DoubleJump,
        Aerial,
    }

    /// <summary>A fully specified touch: which ball state, where the car will be, and what should happen.</summary>
    public sealed class StrikePlan
    {
        public StrikeKind Kind;
        public BallSlice Slice;
        public ContactSolution Contact;
        public Vec3 Aim;
        /// <summary>Absolute game time of contact.</summary>
        public float ContactTime;
        /// <summary>Absolute time the car could arrive driving flat out (≤ ContactTime when feasible).</summary>
        public float EarliestArrival;
        public float ContactSpeed;
        public GoalCrossing Crossing;
        public float Quality;
        public float Score;
        /// <summary>Estimated chance a shot scores (0 for clears).</summary>
        public float ScoreChance;
        public int GoalSide;
        public bool Clear;
        /// <summary>Seconds between takeoff and contact for jump strikes (0 on the ground).</summary>
        public float JumpTime;
        /// <summary>Jump strikes: length of the straight run-up along the contact line.</summary>
        public float RunUp;
        /// <summary>Jump strikes: constant speed along the run-up, carried through the flight to contact.</summary>
        public float LineSpeed;

        /// <summary>Jump strikes: where the run-up starts, on the contact line behind the contact.</summary>
        public Vec3 LineUpPoint => Contact.CarPosition.Flatten() - Contact.Heading * RunUp;

        public float Slack => ContactTime - EarliestArrival;
        public override string ToString() => FormattableString.Invariant(
            $"{Kind}(t={ContactTime:F2} slack={Slack:F2} q={Quality:F2} p={ScoreChance:F2} line={LineSpeed:F0} score={Score:F2} ball={Slice.Location} aim={Aim} v_out={Contact.BallVelocity} aimErr={Contact.AimError:F2} onTarget={Crossing.OnTarget} margin={Crossing.FrameMargin:F0} lateral={Contact.Lateral:F0} heading={Contact.Heading})");
    }

    /// <summary>What the planner should aim for.</summary>
    public sealed class StrikeGoal
    {
        /// <summary>+1 to shoot at the orange goal (y > 0), -1 for the blue goal.</summary>
        public int AttackSide;
        /// <summary>When true the touch is a clearance away from <see cref="DefendSide"/> rather than a shot.</summary>
        public bool Clear;
        public int DefendSide;
        /// <summary>Opponent cars used to judge how contested a shot is.</summary>
        public IReadOnlyList<Car> Opponents = Array.Empty<Car>();

        public static StrikeGoal Shoot(int team, IReadOnlyList<Car> opponents) =>
            new() { AttackSide = team == 0 ? 1 : -1, DefendSide = team == 0 ? -1 : 1, Opponents = opponents };

        public static StrikeGoal ClearFrom(int team, IReadOnlyList<Car> opponents) =>
            new() { AttackSide = team == 0 ? 1 : -1, DefendSide = team == 0 ? -1 : 1, Clear = true, Opponents = opponents };
    }

    /// <summary>
    /// Searches the ball prediction for the best reachable touch. A cheap reachability bound finds
    /// the first candidate slice; each candidate is then solved exactly (contact geometry, hit
    /// model, flight to goal) and its timing is verified with the same navigation rollout the
    /// executing controller uses.
    /// </summary>
    public static class StrikePlanner
    {
        public const float GroundMaxBallHeight = 150f;
        /// <summary>Standard deviation of a shot's lateral error at the goal: base plus per unit of travel.</summary>
        public const float ShotSpreadBase = 40f;
        public const float ShotSpreadPerUnit = 0.06f;
        /// <summary>Outgoing ball speed assumed when leading the heading against the ball's motion.</summary>
        public const float TypicalShotSpeed = 2000f;
        /// <summary>Run-up speeds tried for jump strikes, fastest first (a brisk run-up hits harder).</summary>
        private static readonly float[] LineSpeeds = { 1400f, 800f };
        private static readonly float[] UnboostedLineSpeeds = { 1150f, 700f };
        /// <summary>Boost needed to plan the fast run-up (catching up and in-flight correction).</summary>
        public const float RunUpBoostReserve = 20f;
        /// <summary>Time on the run-up before takeoff, for the speed to settle.</summary>
        public const float LineSettleTime = 0.35f;
        /// <summary>Share of the estimated approach length used as a lower bound on the driven path.</summary>
        public const float PathBoundShare = 0.9f;
        public const float SearchStep = 1f / 20f;
        /// <summary>Slice spacing once a feasible touch exists and the search only looks for a better one.</summary>
        public const float RefineStep = 1f / 10f;
        /// <summary>How far past a slice's deadline a rollout runs to measure its lateness.</summary>
        public const float LatenessProbe = 0.8f;
        /// <summary>Share of a measured lateness skipped before the same approach is tried again.</summary>
        public const float SkipShare = 0.7f;
        private const int HeadingSlots = 5;
        private const int MaxContactHeights = 2;
        private const int SkipTableSize = 4 * MaxContactHeights * HeadingSlots;

        public sealed class Options
        {
            public float MaxTime = 4f;
            public float ExtraSearch = 0.6f;
            public float TimePenalty = 0.15f;
            public Func<float, bool> Claimed;
            public bool AllowBoost = true;
            /// <summary>Largest accepted angle between the predicted and aimed ball direction.</summary>
            public float AimTolerance = 0.35f;
            /// <summary>Optional diagnostics sink (planner rejections and accepted candidates).</summary>
            public Action<string> Log;
        }

        public static StrikePlan Plan(Car car, BallPath path, float now, StrikeGoal goal, Options options = null)
        {
            options ??= new Options();
            if (path == null || path.Count == 0 || car == null) return null;
            var geometry = CarGeometry.Of(car);
            GroundState start = Navigator.StartState(car);
            float firstFeasible = float.NaN;
            StrikePlan best = null;
            float nextSample = now + 0.05f;
            var skip = new float[SkipTableSize];

            for (int i = 0; i < path.Count; i++)
            {
                BallSlice slice = path[i];
                if (slice.Time < nextSample) continue;
                float t = slice.Time - now;
                if (t > options.MaxTime) break;
                if (float.IsFinite(firstFeasible) && slice.Time > firstFeasible + options.ExtraSearch) break;
                // Past the first feasible touch the search only refines, so it can sample coarser.
                nextSample = slice.Time + (float.IsFinite(firstFeasible) ? RefineStep : SearchStep);
                if (options.Claimed != null && options.Claimed(slice.Time)) continue;
                if (MathF.Abs(slice.Location.y) > 5200f || slice.Location.z > DoubleJumpMaxBallHeight) continue;

                // Lower bound: straight-line flat-out travel from the post-landing state.
                float distance = MathF.Max(0f, (slice.Location - start.Position).Flatten().Length() - geometry.FrontReach - RL.BallRadius);
                float bound = start.Time + DrivePhysics.TravelTime(distance, MathF.Max(0f, start.Speed), options.AllowBoost ? start.Boost : 0f);
                if (bound > t) continue;
                options.Log?.Invoke(FormattableString.Invariant($"slice t={t:F2} ball={slice.Location} v={slice.Velocity}"));

                foreach (StrikeKind kind in DrivenKinds(slice.Location.z))
                {
                    StrikePlan plan = PlanDriven(kind, start, slice, now, goal, geometry, options, skip);
                    if (plan == null) continue;
                    if (!float.IsFinite(firstFeasible)) firstFeasible = slice.Time;
                    if (best == null || plan.Score > best.Score) best = plan;
                }
            }
            return best;
        }

        public const float JumpMinBallHeight = 120f;
        public const float JumpMaxBallHeight = 300f;
        public const float DoubleJumpMinBallHeight = 250f;
        public const float DoubleJumpMaxBallHeight = 560f;

        private static IEnumerable<StrikeKind> DrivenKinds(float ballZ)
        {
            if (ballZ <= GroundMaxBallHeight) yield return StrikeKind.Ground;
            if (ballZ >= JumpMinBallHeight && ballZ <= JumpMaxBallHeight) yield return StrikeKind.Jump;
            if (ballZ >= DoubleJumpMinBallHeight && ballZ <= DoubleJumpMaxBallHeight) yield return StrikeKind.DoubleJump;
        }

        /// <summary>Car-origin heights to meet a ball at <paramref name="ballZ"/>: level with the box, or under its top edge.</summary>
        private static IEnumerable<float> ContactHeights(StrikeKind kind, float ballZ)
        {
            if (kind == StrikeKind.Ground)
            {
                yield return JumpModel.RestHeight;
                yield break;
            }
            yield return ballZ - 25f;
            yield return ballZ - 55f;
        }

        /// <summary>Aim candidates for a touch from <paramref name="ball"/>.</summary>
        public static IEnumerable<Vec3> AimPoints(Vec3 ball, StrikeGoal goal)
        {
            if (!goal.Clear)
            {
                float y = goal.AttackSide * (BallFlight.GoalLine + 200f);
                yield return new Vec3(0f, y, 0f);
                yield return new Vec3(-560f, y, 0f);
                yield return new Vec3(560f, y, 0f);
                yield break;
            }
            // Clears: upfield toward either touchline, away from the middle in front of our net.
            float up = -goal.DefendSide;
            float side = MathF.Abs(ball.x) > 300f ? MathF.Sign(ball.x) : 1f;
            yield return new Vec3(side * 3600f, ball.y + up * 2500f, 0f);
            yield return new Vec3(side * 2000f, ball.y + up * 4000f, 0f);
            yield return new Vec3(-side * 3600f, ball.y + up * 2500f, 0f);
        }

        /// <summary>
        /// A touch made from the ground or from a jump. Driving is identical in every case — the car
        /// arrives at the contact's ground position on time — and jumps take off exactly the model's
        /// rise time before contact, so horizontal motion is unchanged by the jump.
        /// </summary>
        private static StrikePlan PlanDriven(StrikeKind kind, GroundState start, BallSlice slice, float now,
            StrikeGoal goal, CarGeometry geometry, Options options, float[] skip)
        {
            float t = slice.Time - now;
            Vec3 ball = slice.Location;
            Vec3 toBall = (ball - start.Position).Flatten();
            Vec3 natural = toBall.Length() > 1f ? toBall.Normalize() : start.Forward;
            Vec3 centreAim = new(0f, goal.AttackSide * (BallFlight.GoalLine + 200f), 0f);
            Vec3 desired = goal.Clear
                ? (AimPointsFirst(ball, goal) - ball).Flatten().Normalize()
                : (centreAim - ball).Flatten().Normalize();
            // The touch adds velocity roughly along the car's heading, so the heading that sends
            // the ball toward the aim leads the aim against the ball's own motion.
            Vec3 primary = (desired * TypicalShotSpeed - slice.Velocity.Flatten()).Normalize();
            StrikePlan best = null;

            // How fast the contact point can run away from an approach, for skipping ahead.
            float closing = 1f + slice.Velocity.Flatten().Length() / RL.CarMaxSpeed;
            int heightIndex = -1;
            foreach (float height in ContactHeights(kind, ball.z))
            {
                heightIndex++;
                bool doubleJump = kind == StrikeKind.DoubleJump;
                float jumpTime = kind == StrikeKind.Ground ? 0f : JumpModel.TimeToHeight(height, doubleJump);
                if (!float.IsFinite(jumpTime) || t - jumpTime < start.Time + 0.05f) continue;
                float verticalSpeed = kind == StrikeKind.Ground ? 0f
                    : doubleJump ? JumpModel.Double(jumpTime).Speed : JumpModel.Single(jumpTime).Speed;

                foreach ((int slot, Vec3 heading) in Headings(primary, natural))
                {
                    int key = ((int)kind * MaxContactHeights + heightIndex) * HeadingSlots + slot;
                    if (t < skip[key]) continue;
                    if (!Contact.FirstTouch(ball, heading, 0f, height, geometry, out Vec3 nominal))
                        continue;
                    if (kind == StrikeKind.Ground)
                    {
                        var target = new DriveTarget(nominal, heading, float.NaN, options.AllowBoost);
                        if (!Approach(start, target, t, t, key, skip, closing, options, out RolloutResult rollout))
                            continue;
                        float groundSpeed = MathF.Max(300f, rollout.ArrivalSpeed);
                        best = Choose(best, kind, slice, now, goal, geometry, options, heading, height, 0f, groundSpeed,
                            rollout.Time, 0f, 0f, 0f, start, t);
                        continue;
                    }

                    // Jump strikes: reach a line-up point on the contact line early, then run up
                    // along the line at constant speed, taking off the jump time before contact.
                    // Without boost the run-up tops out near 1410 uu/s, so keep headroom to catch up.
                    bool boosted = options.AllowBoost && start.Boost >= RunUpBoostReserve;
                    foreach (float lineSpeed in boosted ? LineSpeeds : UnboostedLineSpeeds)
                    {
                        float runUp = lineSpeed * (jumpTime + LineSettleTime);
                        var target = new DriveTarget(nominal.Flatten() - heading * runUp, heading, float.NaN, options.AllowBoost);
                        float lineUpBy = t - runUp / lineSpeed;
                        if (!Approach(start, target, lineUpBy, t, key, skip, closing, options, out RolloutResult rollout))
                            continue;
                        best = Choose(best, kind, slice, now, goal, geometry, options, heading, height, verticalSpeed, lineSpeed,
                            rollout.Time, jumpTime, runUp, lineSpeed, start, lineUpBy);
                        break;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Whether the approach to <paramref name="target"/> can arrive by <paramref name="deadline"/>
        /// (seconds from now): a cheap path-length bound first, then the navigation rollout. Late
        /// approaches record how late they were so the search skips slices they cannot make either.
        /// </summary>
        private static bool Approach(GroundState start, DriveTarget target, float deadline, float t, int key, float[] skip,
            float closing, Options options, out RolloutResult rollout)
        {
            rollout = default;
            float path = Navigator.EstimatePathLength(start.Position, start.Forward, start.Speed, target);
            float bound = start.Time + DrivePhysics.TravelTime(PathBoundShare * path, MathF.Max(0f, start.Speed),
                options.AllowBoost ? start.Boost : 0f);
            if (bound > deadline)
            {
                skip[key] = MathF.Max(skip[key], t + SkipShare * (bound - deadline) / closing);
                return false;
            }
            // Simulate past the deadline so a late approach reports how late it is.
            rollout = Navigator.Rollout(start, target, deadline + LatenessProbe);
            if (rollout.Arrived && rollout.Time <= deadline) return true;
            float late = rollout.Arrived ? rollout.Time - deadline : LatenessProbe;
            skip[key] = MathF.Max(skip[key], t + SkipShare * late / closing);
            options.Log?.Invoke(FormattableString.Invariant(
                $"  t={t:F2} heading={target.Direction}: late (arrived={rollout.Arrived} eta={rollout.Time:F2} deadline={deadline:F2})"));
            return false;
        }

        /// <summary>
        /// Solves the aimed contacts for one approach, scores them, and verifies the best against
        /// the exact drive the strike will execute (the aimed contact sits off the nominal line).
        /// </summary>
        private static StrikePlan Choose(StrikePlan best, StrikeKind kind, BallSlice slice, float now, StrikeGoal goal,
            CarGeometry geometry, Options options, Vec3 heading, float height, float verticalSpeed, float groundSpeed,
            float arrival, float jumpTime, float runUp, float lineSpeed, GroundState start, float deadline)
        {
            Vec3 ball = slice.Location;
            Vec3 carVelocity = heading * groundSpeed + new Vec3(0f, 0f, verticalSpeed);
            var candidates = new List<StrikePlan>(3);
            foreach (Vec3 aim in AimPoints(ball, goal))
            {
                ContactSolution contact = Contact.Aim(ball, slice.Velocity, heading, carVelocity, height, aim, geometry);
                if (!contact.Valid || MathF.Abs(contact.AimError) > options.AimTolerance) continue;
                StrikePlan plan = Score(kind, slice, contact, aim, now, now + arrival, groundSpeed, goal, options);
                plan.JumpTime = jumpTime;
                plan.RunUp = runUp;
                plan.LineSpeed = lineSpeed;
                candidates.Add(plan);
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (StrikePlan plan in candidates)
            {
                if (best != null && plan.Score <= best.Score) break;
                RolloutResult exact = Navigator.Rollout(start, StrikeTarget(plan), deadline + 0.05f);
                if (!exact.Arrived || exact.Time > deadline)
                {
                    options.Log?.Invoke(FormattableString.Invariant($"  aimed contact late (eta={exact.Time:F2}) {plan}"));
                    continue;
                }
                plan.EarliestArrival = now + exact.Time + (plan.Kind == StrikeKind.Ground ? 0f : plan.RunUp / plan.LineSpeed);
                options.Log?.Invoke("  candidate " + plan);
                return plan;
            }
            return best;
        }

        /// <summary>
        /// The drive a strike executes: to the contact's ground position along its heading, or for
        /// jump strikes to the line-up point where the run-up begins.
        /// </summary>
        public static DriveTarget StrikeTarget(StrikePlan plan, bool allowBoost = true) =>
            new(plan.Kind == StrikeKind.Ground ? plan.Contact.CarPosition : plan.LineUpPoint, plan.Contact.Heading, float.NaN, allowBoost);

        private static Vec3 AimPointsFirst(Vec3 ball, StrikeGoal goal)
        {
            foreach (Vec3 aim in AimPoints(ball, goal)) return aim;
            return ball;
        }

        /// <summary>
        /// Approach headings to try, each in a fixed slot: the aim heading, the car's natural line
        /// to the ball, and a fan between them, so a car on the wrong side of the ball can still
        /// find a redirecting touch instead of only a long loop around it.
        /// </summary>
        private static IEnumerable<(int Slot, Vec3 Heading)> Headings(Vec3 primary, Vec3 natural)
        {
            yield return (0, primary);
            float difference = GroundModel.SignedAngle(primary, natural);
            float magnitude = MathF.Abs(difference);
            if (magnitude < 0.08f) yield break;
            if (magnitude > 0.35f)
                yield return (1, GroundModel.Rotate(primary, MathF.Sign(difference) * 0.35f));
            if (magnitude > 0.9f)
                yield return (2, GroundModel.Rotate(primary, difference * 0.5f));
            if (magnitude > 0.5f)
                yield return (3, GroundModel.Rotate(natural, -MathF.Sign(difference) * 0.3f));
            yield return (4, natural);
        }

        public static StrikePlan Score(StrikeKind kind, BallSlice slice, ContactSolution contact, Vec3 aim, float now,
            float earliest, float contactSpeed, StrikeGoal goal, Options options)
        {
            var plan = new StrikePlan
            {
                Kind = kind, Slice = slice, Contact = contact, Aim = aim, ContactTime = slice.Time,
                EarliestArrival = earliest, ContactSpeed = contactSpeed, GoalSide = goal.AttackSide, Clear = goal.Clear,
            };
            float t = slice.Time - now;
            Vec3 v = contact.BallVelocity;

            if (!goal.Clear)
            {
                plan.Crossing = BallFlight.ToGoal(slice.Location, v, goal.AttackSide);
                // Probability the shot is on target given execution error that grows with the
                // distance the ball travels, times the chance nobody saves it; a miss still has
                // some value when it moves the ball toward their net.
                float onTarget = 0f;
                if (plan.Crossing.Reaches)
                {
                    float travel = (plan.Crossing.Point - slice.Location).Flatten().Length();
                    float spread = ShotSpreadBase + ShotSpreadPerUnit * travel;
                    onTarget = NormalCdf(plan.Crossing.FrameMargin / spread);
                }
                float pace = System.Math.Clamp(v.Length() / 3000f, 0f, 1f);
                float saved = SaveChance(plan.Crossing, goal.Opponents, t);
                float progress = System.Math.Clamp(v.y * goal.AttackSide / 2500f, -1f, 1f);
                plan.ScoreChance = onTarget * (1f - saved);
                plan.Quality = plan.ScoreChance * (0.85f + 0.3f * pace) + (1f - plan.ScoreChance) * 0.25f * progress;
            }
            else
            {
                // A clearance should leave our half quickly and not cross the front of our goal.
                float away = -v.y * goal.DefendSide;
                float pace = System.Math.Clamp(away / 2500f, -1f, 1f);
                GoalCrossing own = BallFlight.ToGoal(slice.Location, v, goal.DefendSide, 3f);
                float danger = own.OnTarget ? 1f : own.Reaches ? 0.5f : 0f;
                float centre = 1f - System.Math.Clamp(MathF.Abs(slice.Location.x + v.x * 0.6f) / 2500f, 0f, 1f);
                plan.Quality = 0.8f * pace + 0.3f * System.Math.Clamp(v.Length() / 3000f, 0f, 1f) - 2f * danger - 0.2f * centre * (pace < 0.3f ? 1f : 0f);
            }

            plan.Score = plan.Quality - options.TimePenalty * t;
            return plan;
        }

        /// <summary>Standard normal cumulative distribution (Abramowitz-Stegun 7.1.26 erf).</summary>
        public static float NormalCdf(float z)
        {
            if (float.IsNegativeInfinity(z)) return 0f;
            if (float.IsPositiveInfinity(z)) return 1f;
            float x = MathF.Abs(z) / MathF.Sqrt(2f);
            float t = 1f / (1f + 0.3275911f * x);
            float erf = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
            return 0.5f * (1f + MathF.Sign(z) * erf);
        }

        /// <summary>
        /// Rough chance a defender reaches the shot before it crosses: compares each opponent's
        /// straight-line travel time to the crossing point with the ball's time to goal.
        /// </summary>
        public static float SaveChance(in GoalCrossing crossing, IReadOnlyList<Car> opponents, float timeToContact)
        {
            float best = 0f;
            if (opponents == null) return 0f;
            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished) continue;
                Vec3 point = crossing.Point;
                float distance = (point - opponent.Location).Flatten().Length();
                float reach = MathF.Max(0f, distance - 250f);
                float speed = MathF.Max(opponent.Velocity.Length(), 600f);
                float eta = DrivePhysics.TravelTime(reach, speed, opponent.Boost) * 0.9f;
                float available = timeToContact + crossing.Time + 0.15f;
                float margin = available - eta;
                float chance = System.Math.Clamp(0.5f + margin * 0.9f, 0f, 1f);
                if (point.z > 300f) chance *= 0.75f;
                best = MathF.Max(best, chance);
            }
            return best;
        }
    }
}
