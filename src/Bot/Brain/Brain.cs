using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;
using RedUtils.Physics;
using RedUtils.Planning;

namespace Bot.Brain
{
    public enum Role
    {
        /// <summary>First man: plays the ball.</summary>
        Attacker,
        /// <summary>Second man: follows the play upfield, ready for the next touch.</summary>
        Support,
        /// <summary>Last man back: covers the net.</summary>
        Anchor,
    }

    /// <summary>
    /// Decision layer. Every decision tick it builds a shared <see cref="Analysis"/> (who reaches
    /// the ball first, whether the ball is going in), assigns team roles from it, and chooses
    /// between physically planned touches and positioning:
    /// <list type="bullet">
    /// <item>the untouched ball is going into our net: clear it, block it, or save on the line;</item>
    /// <item>we are first to the ball: take the best planned shot, or a safe touch;</item>
    /// <item>a 50/50 from the goal side: challenge;</item>
    /// <item>the opponent is first: shadow between the ball and our net, collecting boost when safe.</item>
    /// </list>
    /// Committed strikes run to completion unless the play changes under them.
    /// </summary>
    public sealed class Brain
    {
        private const float ThinkInterval = 0.1f;
        /// <summary>Earliest-touch lead that makes the ball ours rather than contested.</summary>
        private const float ClearLead = 0.3f;
        /// <summary>Earliest-touch deficit still worth challenging from the goal side.</summary>
        private const float ChallengeDeficit = 0.2f;
        /// <summary>Scoring chance that makes a touch a shot rather than a set-up touch.</summary>
        private const float ShotChance = 0.35f;

        public Analysis Analysis { get; private set; }
        public Role Role { get; private set; } = Role.Attacker;
        public string Decision { get; private set; } = "startup";
        public StrikePlan LastPlan { get; private set; }

        private float nextThink = float.NegativeInfinity;

        public void Reset()
        {
            nextThink = float.NegativeInfinity;
            Analysis = null;
            LastPlan = null;
            Role = Role.Attacker;
        }

        /// <summary>Updates <see cref="Stardust.Action"/> for this tick.</summary>
        public void Think(Stardust bot)
        {
            IAction current = bot.Action;
            if (current != null && !current.Interruptible) return;
            float now = Game.Time;
            if (current != null && now < nextThink) return;
            nextThink = now + ThinkInterval;

            Analysis a = Analysis = Analysis.Build(bot);
            Role = AssignRole(bot, a);
            if (Continue(bot, a)) return;

            switch (Role)
            {
                case Role.Attacker:
                    Attack(bot, a);
                    break;
                case Role.Support:
                    Support(bot, a);
                    break;
                default:
                    Anchor(bot, a);
                    break;
            }
        }

        // ------------------------------------------------------------------ roles

        /// <summary>
        /// The attacker is the car with the earliest touch, penalised for approaching from the
        /// wrong side of the ball; of the others the one nearest our net anchors. Every teammate
        /// runs the same computation on the same state, so the team agrees without talking;
        /// shot claims break the remaining ties.
        /// </summary>
        private static Role AssignRole(Stardust bot, Analysis a)
        {
            if (a.Teammates.Count == 0) return Role.Attacker;

            float Cost(Car car)
            {
                Intercept i = a.Intercepts[car.Index];
                if (!i.Reachable) return 99f;
                float cost = i.Time - a.Now;
                if (a.GoalSideOf(car.Location, i.Ball) < -200f) cost += 0.6f;
                return cost;
            }

            float mine = Cost(a.Me) - (bot.Action is Shot ? 0.25f : 0f);
            bool claimedByMate = a.Mine.Reachable && bot.Claimed(a.Mine.Time);
            bool first = !claimedByMate;
            foreach (Car mate in a.Teammates)
            {
                float cost = Cost(mate);
                if (cost < mine - 0.02f || (MathF.Abs(cost - mine) <= 0.02f && mate.Index < a.Me.Index))
                    first = false;
            }
            if (first) return Role.Attacker;

            // Of the cars not attacking, the one closest to our net stays back.
            float myDepth = a.Me.Location.Dist(a.OwnGoal);
            int closer = 0;
            int others = 0;
            foreach (Car mate in a.Teammates)
            {
                others++;
                if (mate.Location.Dist(a.OwnGoal) < myDepth) closer++;
            }
            // With one teammate the non-attacker is the last man back; with two, the deeper one is.
            if (others == 1) return Role.Anchor;
            return closer == 0 ? Role.Anchor : Role.Support;
        }

        // ------------------------------------------------------------------ continuing actions

        /// <summary>Keeps a committed action while the play it was chosen for still holds.</summary>
        private bool Continue(Stardust bot, Analysis a)
        {
            switch (bot.Action)
            {
                case DrivenStrike strike when !strike.Finished:
                {
                    float contact = strike.Plan.ContactTime - a.Now;
                    bool threatFirst = a.ThreatTime < contact - 0.05f;
                    bool beaten = a.FirstOpponent.Reachable && a.FirstOpponent.Time - a.Now < contact - 0.3f &&
                        !strike.Plan.Clear;
                    bool yielded = Role != Role.Attacker && !strike.Plan.Clear;
                    return !threatFirst && !beaten && !yielded;
                }
                case AerialShot aerial when !aerial.Finished:
                    return aerial.IsPredictionValid(80f);
                case GoalLineSave save when !save.Finished:
                    if (!float.IsFinite(a.ThreatTime)) return false;
                    save.Crossing = a.ThreatPoint;
                    save.CrossingTime = a.Now + a.ThreatTime;
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ first man

        private void Attack(Stardust bot, Analysis a)
        {
            float myT = Relative(a.Mine, a);
            float oppT = Relative(a.FirstOpponent, a);
            float threat = a.ThreatTime;

            // The ball is going in unless somebody touches it, and nobody else will first.
            if (float.IsFinite(threat) && threat < oppT + 0.2f && Defend(bot, a, threat))
                return;

            float lead = oppT - myT;
            if (!a.Mine.Reachable)
            {
                Shadow(bot, a);
                return;
            }

            if (lead > ClearLead)
            {
                float window = MathF.Min(4f, oppT - 0.1f);
                StrikePlan shot = Plan(bot, a, StrikeGoal.Shoot(a.Team, a.Opponents), window, claims: true);
                if (shot != null && shot.ScoreChance > ShotChance)
                {
                    Strike(bot, shot, "attack / shot");
                    return;
                }
                if (a.Mine.Aerial && TryAerial(bot, a, window, a.TheirGoal, "attack / aerial"))
                    return;
                if (shot != null && shot.Quality > 0.02f)
                {
                    // No real chance yet: a touch that moves the ball toward their net sets one up.
                    Strike(bot, shot, "attack / advance");
                    return;
                }
                SetUp(bot, a);
                return;
            }

            bool goalSide = a.GoalSideOf(a.Me.Location, a.Mine.Ball) > -100f;
            if (lead > -ChallengeDeficit && goalSide)
            {
                // A 50/50: meet the ball as early as possible with the strongest forward touch.
                var options = new StrikePlanner.Options
                {
                    MaxTime = MathF.Min(4f, myT + 0.5f), TimePenalty = 1.2f, AimTolerance = MathF.PI, Claimed = bot.Claimed,
                };
                StrikePlan challenge = StrikePlanner.Plan(a.Me, a.Path, a.Now, StrikeGoal.Shoot(a.Team, a.Opponents), options);
                if (challenge != null && challenge.Quality > -0.4f)
                {
                    Strike(bot, challenge, "challenge / 50-50");
                    return;
                }
                if (a.Mine.Aerial && TryAerial(bot, a, myT + 0.3f, a.TheirGoal, "challenge / aerial"))
                    return;
                Go(bot, new DriveTarget(a.Mine.Ball, Vec3.Zero), "challenge / meet");
                return;
            }

            Shadow(bot, a);
        }

        /// <summary>Stops a ball that will otherwise go in. Returns false when nothing can reach it.</summary>
        private bool Defend(Stardust bot, Analysis a, float threat)
        {
            float window = MathF.Max(0.1f, threat - 0.05f);
            var clearOptions = new StrikePlanner.Options { MaxTime = window, TimePenalty = 0.6f, AimTolerance = 0.6f };
            StrikePlan clear = StrikePlanner.Plan(a.Me, a.Path, a.Now, StrikeGoal.ClearFrom(a.Team, a.Opponents), clearOptions);
            var shotOptions = new StrikePlanner.Options { MaxTime = window, TimePenalty = 0.6f };
            StrikePlan counter = StrikePlanner.Plan(a.Me, a.Path, a.Now, StrikeGoal.Shoot(a.Team, a.Opponents), shotOptions);

            if (counter != null && counter.ScoreChance > 0.5f &&
                (clear == null || counter.ContactTime <= clear.ContactTime + 0.2f))
            {
                Strike(bot, counter, "defend / counter shot");
                return true;
            }
            if (clear != null && clear.Quality > -0.5f)
            {
                Strike(bot, clear, "defend / clear");
                return true;
            }

            // Any touch that keeps the ball out beats a tidy one that is too late.
            var blockOptions = new StrikePlanner.Options { MaxTime = window, TimePenalty = 1.5f, AimTolerance = MathF.PI };
            StrikePlan block = StrikePlanner.Plan(a.Me, a.Path, a.Now, StrikeGoal.ClearFrom(a.Team, a.Opponents), blockOptions);
            if (block != null && block.Quality > -1.5f)
            {
                Strike(bot, block, "defend / block");
                return true;
            }
            if (TryAerial(bot, a, window, ClearTarget(a), "defend / aerial clear"))
                return true;

            if (!(bot.Action is GoalLineSave))
                bot.Action = new GoalLineSave(a.Me, a.ThreatPoint, a.Now + threat);
            SetDecision(bot, "defend / goal-line save");
            return true;
        }

        /// <summary>
        /// No useful touch yet but the ball is ours: get behind it on the line to their net so the
        /// next plan has a shot.
        /// </summary>
        private void SetUp(Stardust bot, Analysis a)
        {
            Vec3 ball = a.Mine.Ball;
            Vec3 toGoal = (a.TheirGoal - ball).Flatten().Normalize();
            Vec3 spot = Field.LimitToNearestSurface(ball - toGoal * 900f).Flatten();
            spot = ClampToField(spot, 300f);
            Go(bot, new DriveTarget(spot, toGoal, float.NaN, allowBoost: true), "attack / set up");
        }

        // ------------------------------------------------------------------ positioning

        /// <summary>
        /// Stay between the ball and our net, facing the ball, far enough back to react to what the
        /// opponent does with it. Wrong-side cars first recover around the ball to the far post.
        /// </summary>
        private void Shadow(Stardust bot, Analysis a)
        {
            Vec3 reference = a.FirstOpponent.Reachable ? a.FirstOpponent.Ball : Ball.Location;
            if (a.GoalSideOf(a.Me.Location, reference) < -150f)
            {
                Recover(bot, a, reference, "defend / recover");
                return;
            }

            Vec3 spot = ShadowSpot(a, reference, 0.4f, 700f, 2000f);
            if (TryBoost(bot, a, spot, 30f, "defend / shadow via boost")) return;
            Vec3 face = (reference - spot).Flatten();
            Go(bot, Positioning(spot, face), "defend / shadow");
        }

        private void Support(Stardust bot, Analysis a)
        {
            Vec3 ball = Ball.Location;
            if (a.GoalSideOf(a.Me.Location, ball) < -300f)
            {
                Recover(bot, a, ball, "support / rotate back");
                return;
            }
            // Second man: level with the play, a little behind and toward the middle.
            float y = ball.y + a.Side * 2400f;
            Vec3 spot = ClampToField(new Vec3(ball.x * 0.5f, y, 0f), 400f);
            if (MathF.Abs(spot.y) > 4700f) spot = new Vec3(spot.x * 0.5f, a.Side * 4700f, 0f);
            if (TryBoost(bot, a, spot, 60f, "support / boost")) return;
            Go(bot, Positioning(spot, (ball - spot).Flatten()), "support / second man");
        }

        private void Anchor(Stardust bot, Analysis a)
        {
            Vec3 ball = Ball.Location;
            if (a.GoalSideOf(a.Me.Location, ball) < -300f)
            {
                Recover(bot, a, ball, "anchor / rotate back");
                return;
            }
            // Last man: deep, ball-side of centre, far enough out to meet a clearance.
            float depth = MathF.Min(MathF.Abs(ball.y - a.OwnGoal.y) * 0.5f, 3000f);
            Vec3 spot = new(System.Math.Clamp(ball.x * 0.3f, -900f, 900f), a.Side * (5000f - MathF.Max(depth, 600f)), 0f);
            if (TryBoost(bot, a, spot, 45f, "anchor / boost")) return;
            Go(bot, Positioning(spot, (ball - spot).Flatten()), "anchor / cover");
        }

        /// <summary>Goal-side point on the line from <paramref name="reference"/> to our net.</summary>
        private static Vec3 ShadowSpot(Analysis a, Vec3 reference, float share, float min, float max)
        {
            Vec3 goal = new(0f, a.Side * 5000f, 0f);
            Vec3 toGoal = (goal - reference).Flatten();
            float distance = toGoal.Length();
            if (distance < 1300f)
                return new Vec3(System.Math.Clamp(reference.x * 0.35f, -650f, 650f), a.Side * 4950f, 0f);
            float back = System.Math.Clamp(distance * share, min, max);
            return ClampToField(reference.Flatten() + toGoal / distance * MathF.Min(back, distance - 300f), 250f);
        }

        /// <summary>Back to our side of the ball around its far side, toward the far post.</summary>
        private void Recover(Stardust bot, Analysis a, Vec3 ball, string decision)
        {
            float farPost = MathF.Abs(ball.x) > 200f ? -MathF.Sign(ball.x) : (a.Me.Location.x >= 0f ? 1f : -1f);
            Vec3 post = new(farPost * 850f, a.Side * 4700f, 0f);
            // Pass the ball on its far side with a safe gap before heading for the post.
            Vec3 me = a.Me.Location.Flatten();
            Vec3 lateral = new(farPost, 0f, 0f);
            Vec3 waypoint = ball.Flatten() + lateral * 900f;
            bool pastBall = (me.y - ball.y) * a.Side > -200f;
            Vec3 target = pastBall || MathF.Abs(me.x - ball.x) > 700f ? post : ClampToField(waypoint, 300f);
            if (TryBoost(bot, a, target, 25f, decision + " via boost")) return;
            Go(bot, new DriveTarget(target, Vec3.Zero), decision);
        }

        private static DriveTarget Positioning(Vec3 spot, Vec3 face) =>
            new(spot, face, float.NaN, allowBoost: true, maxLead: 600f) { ArrivalSpeed = 0f };

        /// <summary>Detour for a boost pad when low and the detour is cheap for the time available.</summary>
        private bool TryBoost(Stardust bot, Analysis a, Vec3 destination, float wanted, string decision)
        {
            Car me = a.Me;
            if (me.Boost >= wanted || !me.IsGrounded) return false;
            float available = MathF.Min(Relative(a.FirstOpponent, a), a.ThreatTime);
            if (available < 1.2f) return false;

            Boost best = null;
            float bestCost = float.PositiveInfinity;
            Vec3 start = me.Location.Flatten();
            float direct = start.FlatDist(destination);
            foreach (Boost pad in Field.Boosts)
            {
                float toPad = start.FlatDist(pad.Location);
                float eta = toPad / MathF.Max(1200f, me.Velocity.Length());
                if (!pad.IsActive && pad.TimeUntilActive > eta) continue;
                // Pads on the wrong side of the ball pull the car out of position.
                if (a.GoalSideOf(pad.Location, Ball.Location) < -300f) continue;
                float detour = toPad + pad.Location.FlatDist(destination) - direct;
                float limit = pad.IsLarge ? MathF.Min(2200f, 900f * (available - 0.8f)) : 350f;
                if (detour > limit || eta > available - 0.5f) continue;
                float gain = MathF.Min(100f - me.Boost, pad.IsLarge ? 100f : 12f);
                float cost = detour - gain * (pad.IsLarge ? 14f : 20f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = pad;
                }
            }
            if (best == null) return false;
            Go(bot, new DriveTarget(best.Location, Vec3.Zero), decision);
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private StrikePlan Plan(Stardust bot, Analysis a, StrikeGoal goal, float window, bool claims)
        {
            if (window <= 0.05f) return null;
            var options = new StrikePlanner.Options { MaxTime = MathF.Min(4f, window), Claimed = claims ? bot.Claimed : null };
            return StrikePlanner.Plan(a.Me, a.Path, a.Now, goal, options);
        }

        private void Strike(Stardust bot, StrikePlan plan, string decision)
        {
            LastPlan = plan;
            bot.Action = new DrivenStrike(bot.Me, plan);
            SetDecision(bot, decision);
        }

        /// <summary>Aerial touches still use the legacy controller until the planned aerial lands.</summary>
        private bool TryAerial(Stardust bot, Analysis a, float window, Vec3 target, string decision)
        {
            if (a.Me.Boost < 15f) return false;
            float next = a.Now + 0.2f;
            foreach (BallSlice slice in Ball.Prediction.Slices)
            {
                if (slice == null || slice.Time < next) continue;
                float t = slice.Time - a.Now;
                if (t > window) break;
                next = slice.Time + 0.05f;
                if (slice.Location.z < StrikePlanner.JumpMaxBallHeight || MathF.Abs(slice.Location.y) > 5100f) continue;
                var shot = new AerialShot(a.Me, slice, target);
                if (!shot.IsValid(a.Me)) continue;
                bot.Action = shot;
                SetDecision(bot, decision);
                return true;
            }
            return false;
        }

        private void Go(Stardust bot, DriveTarget target, string decision)
        {
            target.Point = ClampToField(target.Point, 120f);
            if (bot.Action is Travel travel)
                travel.Target = target;
            else
                bot.Action = new Travel(target);
            SetDecision(bot, decision);
        }

        private void SetDecision(Stardust bot, string decision)
        {
            Decision = decision;
            bot.SetDecision(decision);
        }

        private static float Relative(in Intercept i, Analysis a) => i.Reachable ? i.Time - a.Now : float.PositiveInfinity;

        private static Vec3 ClearTarget(Analysis a) => new(0f, -a.Side * 2000f, 0f);

        /// <summary>Keeps a ground point inside the field walls, with the goal mouths open.</summary>
        private static Vec3 ClampToField(Vec3 point, float margin)
        {
            float x = System.Math.Clamp(point.x, -4096f + margin, 4096f - margin);
            float yLimit = MathF.Abs(x) < 800f ? 5100f : 5120f - margin;
            float y = System.Math.Clamp(point.y, -yLimit, yLimit);
            // Corners: stay inside the 45 degree corner planes.
            float corner = MathF.Abs(x) + MathF.Abs(y) - (8064f - margin * 1.41f);
            if (corner > 0f)
            {
                x -= MathF.Sign(x) * corner * 0.5f;
                y -= MathF.Sign(y) * corner * 0.5f;
            }
            return new Vec3(x, y, 0f);
        }
    }
}
