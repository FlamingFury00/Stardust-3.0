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
        /// <summary>Whether the drive may spend boost (planned without it when boost is not worth it).</summary>
        public bool UsesBoost = true;
        /// <summary>Aerials: whether the takeoff includes the fast aerial's double jump.</summary>
        public bool DoubleJump;
        /// <summary>Aerials: boost the simulated flight spends.</summary>
        public float BoostUsed;
        /// <summary>Jump strikes: length of the straight run-up along the contact line, flight included.</summary>
        public float RunUp;
        /// <summary>Jump strikes: seconds from the line-up point to contact.</summary>
        public float RunUpTime;
        /// <summary>Jump strikes: constant speed along the run-up, carried through the flight to contact.</summary>
        public float LineSpeed;
        /// <summary>Jump strikes: how long jump is held after takeoff.</summary>
        public float Hold = JumpModel.MaximumHold;
        /// <summary>Jump strikes: dodge forward into the ball <see cref="FlipModel.Lead"/> before contact.</summary>
        public bool Flip;
        /// <summary>Flips: extra distance the dodge's impulse carries the car before contact.</summary>
        public float DodgeGain;

        /// <summary>Jump strikes: where the run-up starts, on the contact line behind the contact.</summary>
        public Vec3 LineUpPoint => Contact.CarPosition.Flatten() - Contact.Heading * RunUp;

        public float Slack => ContactTime - EarliestArrival;
        public override string ToString() => FormattableString.Invariant(
            $"{Kind}{(Flip ? "+flip" : "")}(t={ContactTime:F2} slack={Slack:F2} q={Quality:F2} p={ScoreChance:F2} line={LineSpeed:F0} score={Score:F2} ball={Slice.Location} aim={Aim} v_out={Contact.BallVelocity} aimErr={Contact.AimError:F2} onTarget={Crossing.OnTarget} margin={Crossing.FrameMargin:F0} lateral={Contact.Lateral:F0} heading={Contact.Heading})");
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
        /// <summary>Roof agreement with world up a car needs to start a driven strike: on the floor, not a wall or ramp.</summary>
        public const float FloorUp = 0.9f;
        /// <summary>Standard deviation of a shot's lateral error at the goal: base plus per unit of travel.</summary>
        public const float ShotSpreadBase = 40f;
        public const float ShotSpreadPerUnit = 0.06f;
        /// <summary>Outgoing ball speed assumed when leading the heading against the ball's motion.</summary>
        public const float TypicalShotSpeed = 2000f;
        /// <summary>Run-up speeds tried for jump strikes, fastest first (a brisk run-up hits harder).</summary>
        private static readonly float[] LineSpeeds = { 1400f, 1150f, 800f };
        /// <summary>Without boost the throttle alone must reach the run-up speed, so keep headroom below 1410 uu/s.</summary>
        private static readonly float[] UnboostedLineSpeeds = { 1150f, 700f };
        /// <summary>Boost needed at the line-up point for the fast run-up (catching up and in-flight correction).</summary>
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
        /// <summary>Finishes per kind: none/jump, and the flip.</summary>
        private const int Finishes = 2;
        private const int SkipTableSize = 4 * Finishes * MaxContactHeights * HeadingSlots;

        public sealed class Options
        {
            public float MaxTime = 4f;
            public float ExtraSearch = 0.6f;
            public float TimePenalty = 0.15f;
            public Func<float, bool> Claimed;
            public bool AllowBoost = true;
            public bool AllowAerials = true;
            /// <summary>Largest accepted angle between the predicted and aimed ball direction.</summary>
            public float AimTolerance = 0.35f;
            /// <summary>How much earlier than planned an aerial must be able to reach its contact.</summary>
            public float AerialLead = AerialMargin;
            /// <summary>Optional diagnostics sink (planner rejections and accepted candidates).</summary>
            public Action<string> Log;
        }

        public static StrikePlan Plan(Car car, BallPath path, float now, StrikeGoal goal, Options options = null)
        {
            options ??= new Options();
            if (path == null || path.Count == 0 || car == null) return null;
            var geometry = CarGeometry.Of(car);
            GroundState start = Navigator.StartState(car);
            // Driven strikes are planned from the floor; a car on a wall comes down first.
            bool onWall = car.IsGrounded && car.Up.z < FloorUp;
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
                if (MathF.Abs(slice.Location.y) > 5200f) continue;

                if (options.AllowAerials && slice.Location.z >= AerialMinBallHeight)
                {
                    StrikePlan aerial = PlanAerial(car, slice, now, goal, geometry, options);
                    if (aerial != null)
                    {
                        if (!float.IsFinite(firstFeasible)) firstFeasible = slice.Time;
                        if (best == null || aerial.Score > best.Score) best = aerial;
                    }
                }
                if (slice.Location.z > DoubleJumpMaxBallHeight || onWall) continue;

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

        public const float AerialMinBallHeight = 300f;
        /// <summary>An aerial must be reachable this much earlier than planned; flown with that margin it hits within a few units.</summary>
        public const float AerialMargin = 0.15f;
        /// <summary>Overlap (uu) planned between the nose and the ball so the planned contact really touches.</summary>
        private const float AerialOverlap = 8f;
        /// <summary>Score cost per unit of boost an aerial spends, and for the recovery it leaves.</summary>
        private const float AerialBoostCost = 0.003f;
        private const float AerialRecoveryCost = 0.1f;

        /// <summary>
        /// An aerial touch on <paramref name="slice"/>: for a few nose directions between the aim and
        /// the approach, the car origin sits one nose-length behind the ball. The guidance is
        /// simulated to that point (it must also reach it a margin earlier), and the simulated
        /// arrival pose goes through the hit model.
        /// </summary>
        private static StrikePlan PlanAerial(Car car, BallSlice slice, float now, StrikeGoal goal, CarGeometry geometry, Options options)
        {
            float t = slice.Time - now;
            if (!options.AllowBoost || t < options.AerialLead + 0.35f || car.Boost < 5f) return null;
            bool grounded = car.IsGrounded;
            if (grounded && car.Up.z < 0.9f) return null;
            FlightState flight = FlightState.From(car);
            Vec3 ball = slice.Location;

            // Bound: after gravity and the takeoff jumps, boost must supply the rest on average.
            float jumpSpeed = grounded ? 2f * RL.JumpImpulse + RL.JumpHoldAccel * JumpModel.MaximumHold : 0f;
            Vec3 coast = flight.Position + flight.Velocity * t + new Vec3(0f, 0f, 0.5f * RL.Gravity * t * t + jumpSpeed * t);
            float need = 2f * (ball - coast).Length() / (t * t);
            if (need > RL.BoostAccelAir || need / RL.BoostAccelAir * t * RL.BoostPerSecond > car.Boost + 10f)
            {
                options.Log?.Invoke(FormattableString.Invariant($"aerial t={t:F2} ball={ball}: bound need={need:F0}"));
                return null;
            }

            Vec3 approach = (ball - flight.Position).Normalize();
            StrikePlan best = null;
            foreach (Vec3 aim in AimPoints(ball, goal))
            {
                Vec3 desired = (aim - ball).Normalize();
                foreach (Vec3 nose in new[] { (desired + approach).Normalize(), approach })
                {
                    Vec3 lift = Vec3.Up - nose * nose.Dot(Vec3.Up);
                    lift = lift.Length() > 0.1f ? lift.Normalize() : Vec3.Up;
                    Vec3 target = ball - nose * (RL.BallRadius + geometry.FrontReach - AerialOverlap) - lift * geometry.HitboxOffset.z;
                    if (target.z < 60f) continue;
                    bool doubleJump = grounded && target.z - flight.Position.z > 450f;
                    AerialResult early = AerialGuidance.Simulate(flight, grounded, t - options.AerialLead, target, nose, doubleJump);
                    if (!early.Reached)
                    {
                        options.Log?.Invoke(FormattableString.Invariant($"aerial t={t:F2} ball={ball}: early miss={early.Miss:F0}"));
                        continue;
                    }
                    AerialResult flown = AerialGuidance.Simulate(flight, grounded, t, target, nose, doubleJump);
                    FlightState f = flown.Final;
                    var pose = new CarPose(f.Position, f.Forward, f.Right, f.Up, f.Velocity, f.AngularVelocity,
                        geometry.HitboxSize, geometry.HitboxOffset);
                    HitModel.Result hit = HitModel.Collide(pose, ball, slice.Velocity, slice.AngularVelocity, 12f);
                    if (!hit.Contact)
                    {
                        options.Log?.Invoke(FormattableString.Invariant($"aerial t={t:F2} ball={ball}: no contact miss={flown.Miss:F0}"));
                        continue;
                    }
                    Vec3 flat = hit.Velocity.Flatten();
                    float aimError = flat.Length() > 1f ? GroundModel.SignedAngle(desired.Flatten().Normalize(), flat) : MathF.PI;
                    if (MathF.Abs(aimError) > options.AimTolerance)
                    {
                        options.Log?.Invoke(FormattableString.Invariant($"aerial t={t:F2} ball={ball}: aim error {aimError:F2}"));
                        continue;
                    }
                    var contact = new ContactSolution(target, nose, 0f, hit.Velocity, aimError);
                    StrikePlan plan = Score(StrikeKind.Aerial, slice, contact, aim, now, now + t - options.AerialLead, f.Velocity.Length(), goal, options);
                    plan.Score -= AerialRecoveryCost + AerialBoostCost * flown.BoostUsed;
                    plan.DoubleJump = doubleJump;
                    plan.BoostUsed = flown.BoostUsed;
                    options.Log?.Invoke("  aerial candidate " + plan);
                    if (best == null || plan.Score > best.Score) best = plan;
                }
            }
            return best;
        }

        /// <summary>Lowest ball for a jump without a dodge: below it the car would jump over a rolling ball.</summary>
        public const float JumpMinBallHeight = 120f;
        public const float JumpMaxBallHeight = 300f;
        /// <summary>Score cost of a flip's recovery (the car spins and lands instead of driving on).</summary>
        private const float FlipRecoveryCost = 0.03f;
        public const float DoubleJumpMinBallHeight = 250f;
        public const float DoubleJumpMaxBallHeight = 560f;

        private static IEnumerable<StrikeKind> DrivenKinds(float ballZ)
        {
            if (ballZ <= GroundMaxBallHeight) yield return StrikeKind.Ground;
            // Jump strikes include flips, which meet even a rolling ball.
            if (ballZ <= JumpMaxBallHeight) yield return StrikeKind.Jump;
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
            for (int finish = 0; finish < (kind == StrikeKind.Jump ? Finishes : 1); finish++)
            {
                bool flip = finish == 1;
                // A plain jump would sail over a low ball; a flip dips its nose into it.
                if (kind == StrikeKind.Jump && !flip && ball.z < JumpMinBallHeight) continue;
                float pitch = flip ? FlipModel.Pitch(FlipModel.Lead) : 0f;
                float pitchRate = flip ? RL.CarMaxAngularSpeed : 0f;
                int heightIndex = -1;
                foreach (float height in ContactHeights(kind, ball.z))
                {
                    heightIndex++;
                    bool doubleJump = kind == StrikeKind.DoubleJump;
                    float jumpTime = kind == StrikeKind.Ground ? 0f
                        : flip ? FlipModel.TimeToHeight(height) : JumpModel.TimeToHeight(height, doubleJump);
                    if (!float.IsFinite(jumpTime) || t - jumpTime < start.Time + 0.05f) continue;
                    // The quickest flip already rises past the lowest contact heights.
                    float contactHeight = flip ? FlipModel.AtContact(jumpTime).Height : height;
                    if (flip && heightIndex > 0 && contactHeight > height + 1f) continue;
                    float hold = flip ? FlipModel.Hold(jumpTime) : JumpModel.MaximumHold;
                    float verticalSpeed = kind == StrikeKind.Ground ? 0f
                        : flip ? FlipModel.AtContact(jumpTime).Speed
                        : doubleJump ? JumpModel.Double(jumpTime).Speed : JumpModel.Single(jumpTime).Speed;

                    foreach ((int slot, Vec3 heading) in Headings(primary, natural))
                    {
                        int key = (((int)kind * Finishes + finish) * MaxContactHeights + heightIndex) * HeadingSlots + slot;
                        if (t < skip[key]) continue;
                        if (!Contact.FirstTouch(ball, heading, 0f, contactHeight, geometry, out Vec3 nominal, pitch))
                            continue;
                        var finishing = new Finish(heading, contactHeight, verticalSpeed, jumpTime, hold, flip, pitch, pitchRate);
                        if (kind == StrikeKind.Ground)
                        {
                            var target = new DriveTarget(nominal, heading, float.NaN, options.AllowBoost);
                            if (!Approach(start, target, t, t, key, skip, closing, options, out RolloutResult rollout))
                                continue;
                            float groundSpeed = MathF.Max(300f, rollout.ArrivalSpeed);
                            best = Choose(best, kind, slice, now, goal, geometry, options, finishing, groundSpeed, rollout.Time, start, t);
                            continue;
                        }

                        // Jump strikes: reach a line-up point on the contact line early, then run up
                        // along the line at constant speed, taking off the jump time before contact.
                        // Without boost the run-up tops out near 1410 uu/s, so keep headroom to catch up.
                        bool boosted = options.AllowBoost && start.Boost >= RunUpBoostReserve;
                        foreach (float lineSpeed in boosted ? LineSpeeds : UnboostedLineSpeeds)
                        {
                            float runUpTime = LineSettleTime + jumpTime;
                            float runUp = lineSpeed * runUpTime + finishing.DodgeGain(lineSpeed);
                            var target = new DriveTarget(nominal.Flatten() - heading * runUp, heading, float.NaN, options.AllowBoost);
                            float lineUpBy = t - runUpTime;
                            if (!Approach(start, target, lineUpBy, t, key, skip, closing, options, out RolloutResult rollout))
                                continue;
                            // The drive to the line-up may burn the boost the fast run-up relies on.
                            if (lineSpeed > UnboostedLineSpeeds[0] && rollout.Final.Boost < RunUpBoostReserve)
                                continue;
                            best = Choose(best, kind, slice, now, goal, geometry, options, finishing, lineSpeed, rollout.Time,
                                start, lineUpBy, runUp, runUpTime);
                            break;
                        }
                    }
                }
            }
            return best;
        }

        /// <summary>How a driven strike meets the ball: car height, vertical motion, and the flip's pose.</summary>
        private readonly struct Finish
        {
            public readonly Vec3 Heading;
            public readonly float Height, VerticalSpeed, JumpTime, Hold, Pitch, PitchRate;
            public readonly bool Flip;

            public Finish(Vec3 heading, float height, float verticalSpeed, float jumpTime, float hold, bool flip, float pitch, float pitchRate)
            {
                Heading = heading; Height = height; VerticalSpeed = verticalSpeed; JumpTime = jumpTime; Hold = hold;
                Flip = flip; Pitch = pitch; PitchRate = pitchRate;
            }

            /// <summary>Flat speed at contact for a car arriving (or running up) at <paramref name="groundSpeed"/>.</summary>
            public float ContactSpeed(float groundSpeed) => Flip ? FlipModel.SpeedAfter(groundSpeed, VerticalSpeed) : groundSpeed;

            /// <summary>Extra distance the dodge covers before contact for a run-up at <paramref name="lineSpeed"/>.</summary>
            public float DodgeGain(float lineSpeed) => Flip ? (ContactSpeed(lineSpeed) - lineSpeed) * FlipModel.Lead : 0f;
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
            CarGeometry geometry, Options options, in Finish finish, float groundSpeed, float arrival, GroundState start,
            float deadline, float runUp = 0f, float runUpTime = 0f)
        {
            Vec3 ball = slice.Location;
            float contactSpeed = finish.ContactSpeed(groundSpeed);
            Vec3 carVelocity = finish.Heading * contactSpeed + new Vec3(0f, 0f, finish.VerticalSpeed);
            var candidates = new List<StrikePlan>(3);
            foreach (Vec3 aim in AimPoints(ball, goal))
            {
                ContactSolution contact = Contact.Aim(ball, slice.Velocity, finish.Heading, carVelocity, finish.Height, aim, geometry,
                    finish.Pitch, finish.PitchRate);
                if (!contact.Valid || MathF.Abs(contact.AimError) > options.AimTolerance) continue;
                StrikePlan plan = Score(kind, slice, contact, aim, now, now + arrival, contactSpeed, goal, options);
                plan.UsesBoost = options.AllowBoost;
                plan.JumpTime = finish.JumpTime;
                plan.Hold = finish.Hold;
                plan.Flip = finish.Flip;
                plan.RunUp = runUp;
                plan.RunUpTime = runUpTime;
                plan.LineSpeed = kind == StrikeKind.Ground ? 0f : groundSpeed;
                plan.DodgeGain = finish.DodgeGain(groundSpeed);
                if (finish.Flip) plan.Score -= FlipRecoveryCost;
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
                plan.EarliestArrival = now + exact.Time + plan.RunUpTime;
                options.Log?.Invoke("  candidate " + plan);
                return plan;
            }
            return best;
        }

        /// <summary>
        /// The drive a strike executes: to the contact's ground position along its heading, or for
        /// jump strikes to the line-up point where the run-up begins.
        /// </summary>
        public static DriveTarget StrikeTarget(StrikePlan plan) =>
            new(plan.Kind == StrikeKind.Ground ? plan.Contact.CarPosition : plan.LineUpPoint, plan.Contact.Heading, float.NaN, plan.UsesBoost);

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
