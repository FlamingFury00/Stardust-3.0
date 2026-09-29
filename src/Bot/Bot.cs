using System;
using RedUtils;
using RedUtils.Physics;
using RedUtils.Planning;
using RedUtils.Math;

namespace Bot
{
    /// <summary>Independent feature switches support in-game ablation against the baseline.</summary>
    public sealed class StardustOptions
    {
        public bool GroundControl { get; init; } = Environment.GetEnvironmentVariable("STARDUST_GROUND_CONTROL") != "0";
        public bool AerialCarry { get; init; } = Environment.GetEnvironmentVariable("STARDUST_AERIAL_CARRY") != "0";
        public bool FlipResets { get; init; } = Environment.GetEnvironmentVariable("STARDUST_FLIP_RESETS") == "1";
        public bool AirDribbles { get; init; } = Environment.GetEnvironmentVariable("STARDUST_AIR_DRIBBLES") == "1";
        public bool Trace { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TRACE") == "1";
        /// <summary>Also log the save options weighed on every planning tick (verbose).</summary>
        public bool TraceSaves { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TRACE_SAVES") == "1";
        public string TelemetrySetting { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY");
        public bool TelemetryConsole { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY_CONSOLE") == "1";
        public string TelemetryFile { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY_FILE");
        public int TelemetryHz { get; init; } = ReadTelemetryHz();

        public bool TelemetryDisabled => TelemetrySetting == "0";
        public bool TelemetryExplicit => TelemetrySetting != null;

        private static int ReadTelemetryHz()
        {
            string raw = Environment.GetEnvironmentVariable("STARDUST_TELEMETRY_HZ");
            return int.TryParse(raw, out int hz) ? System.Math.Clamp(hz, 1, 30) : 10;
        }
    }

    /// <summary>Threat-first planning with explicit goal-side recovery and moving shadow defense.</summary>
    public class Stardust : RUBot
    {
        public StardustOptions Options { get; } = new();
        public TacticalFrame Situation { get; private set; } = new();
        public string Decision { get; private set; } = "startup";
        public bool Shooting { get; set; }

        /// <summary>Independent policy ablations; use STARDUST_TUNE for paired simulator experiments.</summary>
        public static bool CarrierChallenges = true;
        public static bool FirstManShadow = true;
        /// <summary>Seconds between full re-plans while calm, and while an opponent or the ball is about to strike.</summary>
        public static float PlanInterval = 0.12f, UrgentPlanInterval = 0.05f;
        /// <summary>The car furthest back at a team kickoff refills at its own corner's big pad instead of cheating forward.</summary>
        public static bool KickoffPadRun = true;
        /// <summary>Longest flat distance (uu) of that run: from the back-centre spawn to its own corner pad is 3114.</summary>
        public static float KickoffPadReach = 3300f;
        /// <summary>Run supersonic into an opponent worth a demolition. Off until measured in full matches.</summary>
        public static bool DemolitionRuns = false;
        /// <summary>Ball speed (uu/s) above which a pad detour before a touch is not considered.</summary>
        private const float PadBeforeShotBallSpeed = 1800f;
        /// <summary>Time (s) a first man must still have before the ball to start a demolition run instead.</summary>
        private const float DemolitionFreeTime = 0.15f;

        private float nextPlan = float.NegativeInfinity;
        private float challengeCommitUntil = float.NegativeInfinity;
        private bool defending, countering, pressured;
        private Shot defensiveShot;
        private readonly StardustTelemetry telemetry;

        public bool RawCanChallenge { get; private set; }
        public bool ChallengeCommitted { get; private set; }
        public float EmergencyThreatTime { get; private set; } = float.PositiveInfinity;
        public float CounterThreatTime { get; private set; } = float.PositiveInfinity;
        /// <summary>Extra travel (s) the last boost-routing decision could afford; for telemetry.</summary>
        public float BoostSlack { get; private set; }

        public Stardust(string defaultAgentId = null) : base(defaultAgentId)
        {
            // The packaged RLBot process enables file telemetry by default. Test/probe instances pass
            // an explicit agent id and remain quiet unless STARDUST_TELEMETRY was explicitly provided.
            bool productionEntry = defaultAgentId == null;
            bool telemetryEnabled = !Options.TelemetryDisabled &&
                (productionEntry || Options.TelemetryExplicit);
            telemetry = new StardustTelemetry(
                telemetryEnabled, Options.TelemetryHz, Options.TelemetryConsole, Options.TelemetryFile);
        }

        public override void Run()
        {
            if (ClockReset)
            {
                nextPlan = float.NegativeInfinity;
                challengeCommitUntil = float.NegativeInfinity;
                defending = false;
                countering = false;
                pressured = false;
                defensiveShot = null;
                RawCanChallenge = false;
                ChallengeCommitted = false;
                EmergencyThreatTime = float.PositiveInfinity;
                CounterThreatTime = float.PositiveInfinity;
                BoostSlack = 0f;
                telemetry.Reset();
            }

            Shooting = Action is Shot;

            if (IsKickoff)
            {
                if (Action != null)
                    return;

                int rank = 0;
                foreach (Car car in LivingTeammates)
                    if (Tactics.KickoffBefore(car, Me, Ball.Location, Team))
                        rank++;

                if (rank == 0)
                {
                    Action = new Kickoff();
                    SetDecision("kickoff / taker");
                }
                else if (KickoffPadRun && BoostEconomy.KickoffPad(Me.Location, Team, Field.Boosts, rank,
                    LivingTeammates.Count + 1, KickoffPadReach) is Boost corner)
                {
                    Action = new GetBoost(Me, corner.Index, interruptible: true);
                    SetDecision("kickoff / boost run");
                }
                else
                {
                    Vec3 target = rank == 1
                        ? new Vec3(0, Field.Side(Team) * 1200, 17)
                        : new Vec3(-MathF.Sign(Me.Location.x) * 750, Field.Side(Team) * 3500, 17);
                    DriveTo(target, rank == 1 ? 1300f : 1600f, false);
                    SetDecision(rank == 1 ? "kickoff / cheat" : "kickoff / cover");
                }
                return;
            }

            BoostSlack = 0f;
            float threat = Defense.GoalThreat(Ball.Prediction.Slices, OurGoal.Location,
                Game.Time, 2.5f, out Vec3 crossing);
            float counterThreat = threat;
            if (!float.IsFinite(counterThreat))
                counterThreat = Defense.GoalThreat(Ball.Prediction.Slices, OurGoal.Location,
                    Game.Time, 4.5f, out _);

            EmergencyThreatTime = threat;
            CounterThreatTime = counterThreat;

            float pressureTime = Tactics.OpponentPressure(LivingOpponents,
                Ball.MainBall, OurGoal.Location);
            bool emergency = float.IsFinite(threat);
            bool counterDanger = !emergency && float.IsFinite(counterThreat);
            bool underPressure = float.IsFinite(pressureTime);
            bool threatEdge = emergency != defending || counterDanger != countering;
            bool pressureEdge = underPressure != pressured;

            if (threatEdge || pressureEdge)
                nextPlan = float.NegativeInfinity;

            // Do not acknowledge a tactical edge until a physically committed flip/dodge can be
            // interrupted. Otherwise the event is consumed while the old action keeps running.
            if (Action != null && !Action.Interruptible)
                return;

            defending = emergency;
            countering = counterDanger;
            pressured = underPressure;

            if (Action is Shot oldShot && !oldShot.IsPredictionValid())
                Action = null;
            if (threatEdge)
            {
                Action = null;
                defensiveShot = null;
            }
            if (Action == null)
                nextPlan = MathF.Min(nextPlan, Game.Time);
            if (Game.Time < nextPlan)
                return;

            Assess(pressureTime);
            nextPlan = Game.Time + (underPressure || emergency || counterDanger ? UrgentPlanInterval : PlanInterval);

            bool challengeCarrier = Defense.TryChallengeCarrier(Situation, Me, Ball.MainBall,
                LivingOpponents, OurGoal.Location, out Vec3 carrierContact);
            challengeCarrier &= CarrierChallenges;
            RawCanChallenge = challengeCarrier || Defense.CanChallenge(
                Situation, Me, Ball.Location, OurGoal.Location);
            bool challengeSafe = Defense.CanContinueChallenge(
                Situation, Me, Ball.Location, OurGoal.Location);
            if (RawCanChallenge)
                challengeCommitUntil = Game.Time + (underPressure ? 0.36f : 0.27f);
            else if (Game.Time >= challengeCommitUntil || !challengeSafe)
                challengeCommitUntil = float.NegativeInfinity;
            ChallengeCommitted = RawCanChallenge ||
                (Game.Time < challengeCommitUntil && challengeSafe);

            if (emergency && PlannedSave(threat))
                return;

            bool controlledPossession =
                PossessionControl.HasControlledPossession(Me, Ball.MainBall) ||
                PossessionControl.CanKeepGroundControl(Situation, Me, Ball.MainBall, OurGoal.Location) ||
                PossessionControl.HasAirControl(Me, Ball.MainBall);

            // A distant ball-only goal projection ignores the carrier's next touch. Force its
            // decision while covered and in reach instead of retreating until it takes a shot.
            if (!emergency && challengeCarrier && !controlledPossession)
            {
                DriveTo(carrierContact, Car.MaxSpeed, false, urgentBoost: true);
                SetDecision("attack / challenge carrier");
                return;
            }

            if (emergency || counterDanger)
            {
                float dangerTime = emergency ? threat : counterThreat;
                float deadline = MathF.Max(0.05f, dangerTime - 0.025f);
                bool clearSide = Defense.IsGoalSide(
                    Me.Location, Ball.Location, OurGoal.Location, -100f);

                // Before a hard emergency, a clean shot at the opponent net is the strongest clear:
                // it removes the threat and can score. Only do this from safe goal-side ownership.
                if (counterDanger && clearSide && ChallengeCommitted)
                {
                    Shot counterShot = Tactics.SelectShot(
                        this, false, Situation.OpponentEta, HasClaim,
                        MathF.Min(deadline, 1.35f));
                    if (counterShot != null)
                    {
                        float counterContact = counterShot.Slice.Time - Game.Time;
                        bool directCounter = counterContact <= 0.82f &&
                            Tactics.GoalLaneOpen(
                                LivingOpponents, counterShot.Slice.Location, TheirGoal.Location);
                        if (directCounter || Tactics.PreferImmediateShot(this, counterShot, Situation))
                        {
                            Action = counterShot;
                            defensiveShot = null;
                            SetDecision("attack / counter-shot clear");
                            return;
                        }
                    }
                }

                // A formal clear is only legal from approximately goal-side geometry. The uploaded
                // match contained a probable own-goal acceleration from a wrong-side recovery dodge.
                if (clearSide)
                {
                    if (!(Action is Shot current) || !ReferenceEquals(Action, defensiveShot) ||
                        current.Slice.Time - Game.Time > deadline)
                    {
                        defensiveShot = Tactics.SelectShot(this, true, Situation.OpponentEta,
                            _ => false, deadline);
                        Action = defensiveShot;
                    }
                }
                else if (Action is Shot)
                {
                    Action = null;
                    defensiveShot = null;
                }

                // Only the shot selected above is a clearance. A persistent recovery/intercept
                // drive must be replanned as the ball path changes, even while danger stays active.
                if (Action is Shot && ReferenceEquals(Action, defensiveShot))
                {
                    SetDecision(emergency
                        ? "defend / emergency clear"
                        : "defend / counter clear");
                    return;
                }

                if (Defense.TryDefensiveIntercept(
                    Me, Ball.Prediction, OurGoal.Location, Game.Time, deadline,
                    out Vec3 block))
                {
                    DriveTo(block, Car.MaxSpeed, true, false);
                    SetDecision(emergency
                        ? "defend / emergency intercept"
                        : "defend / counter intercept");
                    return;
                }

                if (!emergency)
                {
                    Vec3 counterReference = Defense.ReferenceBall(
                        Ball.Prediction, Ball.Location, OurGoal.Location, Game.Time);
                    bool counterGoalSide = Defense.IsGoalSide(
                        Me.Location, Ball.Location, OurGoal.Location, 20f);
                    Vec3 route = counterGoalSide
                        ? Defense.ShadowTarget(
                            counterReference, OurGoal.Location, DefensiveRole.Shadow,
                            MathF.Min(pressureTime, 0.35f), Ball.Velocity)
                        : Defense.RecoveryTarget(Me.Location, counterReference, OurGoal.Location);
                    Vec3 counterSupport = Tactics.GoalReturnTarget(
                        Me, route, OurGoal.Location);
                    bool fastCounterRecovery = !counterGoalSide &&
                        Defense.CanFastRecover(
                            Me, Ball.Location, counterSupport, OurGoal.Location);
                    GuardTo(counterSupport, 2250f, 650f, false,
                        allowDodges: fastCounterRecovery);
                    SetDecision(counterGoalSide
                        ? "defend / counter shadow"
                        : "defend / counter recover");
                    return;
                }

                float crossingTime = Game.Time + MathF.Max(0f, threat);
                if (Action is GoalLineSave save && !save.Finished)
                {
                    save.Crossing = crossing;
                    save.CrossingTime = crossingTime;
                }
                else
                    Action = new GoalLineSave(Me, crossing, crossingTime);

                SetDecision("defend / goal-line save");
                return;
            }

            bool canChallenge = ChallengeCommitted;
            float attackDeadline = Defense.AttackDeadline(Situation, controlledPossession);

            bool canOwnAttack = canChallenge || controlledPossession;
            Shot priorityAttack = canOwnAttack
                ? Tactics.SelectShot(
                    this, false, Situation.OpponentEta, HasClaim, attackDeadline)
                : null;
            bool finishNow = Tactics.PreferImmediateShot(
                this, priorityAttack, Situation);

            if (DemolitionRuns && TryDemolition(controlledPossession, finishNow))
                return;

            if (Action is IPossessionAction possession)
            {
                if (finishNow)
                {
                    Action = priorityAttack;
                    SetDecision("attack / finish now");
                    return;
                }

                // A flip reset flies upside down under the ball, which no roof or nose possession
                // test recognises: keep it until it finishes unless an opponent is about to arrive.
                bool retain = Action switch
                {
                    GroundCatch => Situation.TeamRank == 0 && (canChallenge || Situation.FreeTime >= -0.12f),
                    FlipReset => Situation.OpponentEta > 0.6f,
                    _ => PossessionControl.ShouldRetainPossession(Situation, Me, Ball.MainBall, OurGoal.Location),
                };

                if (retain && !possession.Finished)
                    return;
                Action = null;
            }

            if (Action is Shot shot)
            {
                if (canChallenge && shot.IsPredictionValid() &&
                    shot.Slice.Time - Game.Time <= attackDeadline &&
                    !HasTeammateEarlierShot(shot.Slice.Time))
                    return;
                Action = null;
            }

            // A pad trip in progress stays alive across plans; the routing below keeps or drops it.
            if (Action is GetBoost { Finished: true })
                Action = null;

            if (!(Action is Drive) && !(Action is DefensiveDrive) && !(Action is GetBoost))
                Action = null;

            if (!Me.IsGrounded)
            {
                if (finishNow)
                {
                    Action = priorityAttack;
                    SetDecision("attack / airborne finish");
                    return;
                }

                bool canPossessAir = Options.AerialCarry &&
                    PossessionControl.CanAcquireAir(Situation, Me, Ball.MainBall, OurGoal.Location);
                if (canPossessAir &&
                    AerialCarry.CanStart(Me, Ball.MainBall, Situation.OpponentEta))
                {
                    Action = new AerialCarry();
                    SetDecision(underPressure
                        ? "mechanic / pressured aerial carry"
                        : "mechanic / aerial carry");
                    return;
                }

                Shot aerial = priorityAttack;
                Action = aerial ?? (IAction)new Recover();
                SetDecision(aerial == null
                    ? "recover / landing surface"
                    : "attack / airborne intercept");
                return;
            }

            bool canPossessGround = Options.GroundControl &&
                PossessionControl.CanAcquireGround(
                    Situation, Me, Ball.MainBall, OurGoal.Location);
            bool canDribble = canPossessGround &&
                GroundDribble.CanStart(Me, Ball.MainBall, Situation.FreeTime);

            // A direct scoring contact outranks continuing a dribble. Otherwise preserve controlled
            // possession and use its pressure-triggered outplays.
            if (finishNow)
            {
                Action = priorityAttack;
                SetDecision("attack / finish now");
                return;
            }

            if (controlledPossession && canDribble)
            {
                Action = new GroundDribble();
                SetDecision(underPressure
                    ? "mechanic / pressured ground carry"
                    : "mechanic / ground carry");
                return;
            }

            Shot attack = priorityAttack;
            CatchPlan catchPlan = canPossessGround
                ? GroundCatch.FindCatch(Me, ControlMath.FlatUnit(TheirGoal.Location - Ball.Location, Me.Forward))
                : null;

            // Prefer a real scoring/clearing contact when it is imminent or contested. With time and a
            // descending ball, keep the softer catch available instead of forcing every touch.
            if (attack != null &&
                (underPressure || catchPlan == null || attack.Slice.Time - Game.Time <= 0.72f))
            {
                if (TryPadBeforeShot(attack, pressureTime))
                    return;
                Action = attack;
                SetDecision(underPressure
                    ? "attack / pressured intercept"
                    : "attack / economical intercept");
                return;
            }

            if (canDribble)
            {
                Action = new GroundDribble();
                SetDecision(underPressure
                    ? "mechanic / pressured ground carry"
                    : "mechanic / ground carry");
                return;
            }

            if (catchPlan != null)
            {
                Action = new GroundCatch(catchPlan);
                SetDecision(underPressure
                    ? "mechanic / contested cushion catch"
                    : "mechanic / cushion catch");
                return;
            }

            if (attack != null)
            {
                if (TryPadBeforeShot(attack, pressureTime))
                    return;
                Action = attack;
                SetDecision("attack / economical intercept");
                return;
            }

            if (canChallenge)
            {
                if (underPressure || Situation.FreeTime < 0.35f)
                {
                    Vec3 contact = Tactics.PressureChallengeTarget(
                        Me, Ball.Prediction, Ball.MainBall, TheirGoal.Location,
                        Game.Time, Situation.MyEta);
                    DriveTo(contact, Car.MaxSpeed, false);
                    SetDecision("attack / pressure challenge");
                    return;
                }

                Vec3 lane = PossessionControl.AttackingLane(
                    Me, Ball.MainBall, LivingOpponents,
                    TheirGoal.Location, OurGoal.Location);
                DriveTo(Field.LimitToNearestSurface(Ball.Location - lane * 300f),
                    1750f, false);
                SetDecision("possess / approach behind ball");
                return;
            }

            DefensiveRole role = Situation.TeamCount == 1 || (FirstManShadow && Situation.TeamRank == 0)
                ? DefensiveRole.Shadow
                : Defense.ShouldAnchor(Situation)
                    ? DefensiveRole.Anchor
                    : DefensiveRole.Support;

            Vec3 reference = Defense.ReferenceBall(Ball.Prediction, Ball.Location,
                OurGoal.Location, Game.Time);
            bool goalSide = Defense.IsGoalSide(Me.Location, Ball.Location, OurGoal.Location, 20f);
            bool recoveringGoalSide = !goalSide;

            Vec3 rawSupport = recoveringGoalSide
                ? Defense.RecoveryTarget(Me.Location, reference, OurGoal.Location)
                : Defense.ShadowTarget(reference, OurGoal.Location, role, pressureTime, Ball.Velocity);
            Vec3 support = Tactics.GoalReturnTarget(Me, rawSupport, OurGoal.Location);
            bool exitingGoal = support.FlatDist(rawSupport) > 1f;

            if (!exitingGoal && TryBoostRoute(support, recoveringGoalSide, pressureTime))
                return;

            float cruise;
            float terminal;
            bool hold;
            if (recoveringGoalSide || exitingGoal)
            {
                cruise = 2200f;
                terminal = 500f;
                hold = false;
            }
            else if (role == DefensiveRole.Shadow)
            {
                cruise = underPressure ? 2050f : 1850f;
                terminal = Defense.ShadowTerminalSpeed(Ball.MainBall, OurGoal.Location, underPressure);
                hold = false;
            }
            else if (role == DefensiveRole.Support)
            {
                cruise = 2050f;
                terminal = underPressure ? 700f : 500f;
                hold = false;
            }
            else
            {
                cruise = underPressure ? 1900f : 1700f;
                terminal = 0f;
                hold = true;
            }

            bool fastRecovery = recoveringGoalSide && !exitingGoal &&
                Defense.CanFastRecover(
                    Me, Ball.Location, support, OurGoal.Location);
            GuardTo(support, cruise, terminal, hold,
                allowDodges: fastRecovery);

            if (exitingGoal)
                SetDecision("defend / exit net");
            else if (recoveringGoalSide)
                SetDecision("defend / recover behind ball");
            else if (role == DefensiveRole.Shadow)
                SetDecision(underPressure ? "defend / solo shadow" : "defend / moving shadow");
            else if (role == DefensiveRole.Anchor)
                SetDecision(underPressure ? "defend / ball-goal anchor" : "support / goal-cover anchor");
            else
                SetDecision(underPressure ? "defend / second-man support" : "support / wide lane");
        }

        private bool HasClaim(float sliceTime) => HasTeammateEarlierShot(sliceTime);

        /// <summary>Quality a planned clearance needs to be taken ahead of a block.</summary>
        public static float ComfortableClearQuality = 0.2f;
        /// <summary>Spare time a planned clearance needs to be taken ahead of a block.</summary>
        public static float ComfortableClearSlack = 0.1f;
        /// <summary>Quality below which a planned clearance is worse than the scripted save.</summary>
        public static float LastResortClearQuality = -0.5f;

        /// <summary>
        /// Physics-planned save for a ball that will otherwise go in: a comfortable ground or aerial
        /// clearance, else a block in the ball's path, else any clearance, else a best-effort block.
        /// Returns false to fall back to the scripted intercept and goal-line save.
        /// </summary>
        private bool PlannedSave(float threat)
        {
            if (Action is Block running && !running.Finished)
            {
                SetDecision("defend / block");
                return true;
            }
            if (Action is IStrike strike && !strike.Finished && strike.Plan.Clear)
                return true;

            var path = new BallPath(Ball.Prediction.Slices);
            var clearOptions = new StrikePlanner.Options
            {
                MaxTime = MathF.Max(0.1f, threat - 0.05f), TimePenalty = 0.6f, AimTolerance = 0.6f,
            };
            StrikePlan clear = StrikePlanner.Plan(Me, path, Game.Time, StrikeGoal.ClearFrom(Team, LivingOpponents), clearOptions);
            if (clear != null && clear.Quality > ComfortableClearQuality && clear.Slack > ComfortableClearSlack &&
                clear.Kind is StrikeKind.Ground or StrikeKind.Aerial)
                return Strike(clear, "defend / planned clear");

            BlockPlan block = BlockPlanner.Plan(Me, path, Game.Time, threat + 0.1f);
            if (Options.TraceSaves)
                Console.WriteLine(FormattableString.Invariant(
                    $"stardust t={Game.Time:F3} car={Index} save threat={threat:F2} clear={clear?.ToString() ?? "none"} block={block?.ToString() ?? "none"}"));
            if (block != null && block.Feasible)
                return Guard(block, "defend / block");
            if (clear != null && clear.Quality > LastResortClearQuality)
                return Strike(clear, "defend / planned clear");
            return block != null && Guard(block, "defend / desperate block");
        }

        private bool Strike(StrikePlan plan, string decision)
        {
            Action = plan.Kind == StrikeKind.Aerial ? new AerialStrike(plan) : new DrivenStrike(Me, plan);
            SetDecision(decision);
            return true;
        }

        private bool Guard(BlockPlan plan, string decision)
        {
            Action = new Block(plan);
            SetDecision(decision);
            return true;
        }

        /// <summary>Runs a supersonic car into the opponent that matters most, when one can be met in time.</summary>
        private bool TryDemolition(bool controlledPossession, bool finishNow)
        {
            if (Action is DemoAttack running && !running.Finished)
            {
                if (!finishNow)
                {
                    SetDecision("attack / demolition");
                    return true;
                }
                Action = null;
            }

            // A touch this car is about to make or is making outranks any demolition.
            if (finishNow || controlledPossession || Action is IPossessionAction || Action is Shot ||
                !Me.IsGrounded || Me.Boost < Demolition.MinBoost)
                return false;
            if (Situation.TeamRank == 0 && Situation.FreeTime > DemolitionFreeTime)
                return false;

            var pick = Demolition.Choose(Me, LivingOpponents, Ball.Location, OurGoal.Location, LivingTeammates);
            if (pick == null)
                return false;

            Action = new DemoAttack(pick.Value.Target);
            SetDecision("attack / demolition");
            return true;
        }

        /// <summary>
        /// Spends the time a won race leaves before a planned touch on a pad along the way. The shot
        /// is re-planned once the pad is taken, so the touch is only ever delayed within the slack.
        /// </summary>
        private bool TryPadBeforeShot(Shot shot, float pressureTime)
        {
            if (!Me.IsGrounded || shot?.Slice == null || Ball.Velocity.Length() > PadBeforeShotBallSpeed)
                return false;

            float contact = shot.Slice.Time - Game.Time;
            float slack = BoostEconomy.SlackBeforeContact(Situation, contact,
                Ball.Location.y * Field.Side(Team), pressureTime);
            Boost pad = BoostEconomy.Choose(Me, Field.Boosts, Ball.Location, shot.Slice.Location, Team, slack,
                LivingOpponents, null, CurrentPad, LivingTeammates);
            return FollowPad(pad, slack, "boost / pad before touch");
        }

        /// <summary>Bends the trip to <paramref name="destination"/> through a pad when the play leaves time for it.</summary>
        private bool TryBoostRoute(Vec3 destination, bool recovering, float pressureTime)
        {
            if (!Me.IsGrounded || !BoostEconomy.Wanted(Me.Boost))
                return false;

            float toDestination = Drive.GetEta(Me, destination);
            float slack = BoostEconomy.Slack(Situation, toDestination,
                Ball.Location.y * Field.Side(Team), recovering, pressureTime);
            Boost pad = BoostEconomy.Choose(Me, Field.Boosts, Ball.Location, destination, Team, slack,
                LivingOpponents, null, CurrentPad, LivingTeammates);
            return FollowPad(pad, slack, "boost / pad on route");
        }

        /// <summary>The pad this car is driving to, when a trip is under way.</summary>
        private Boost CurrentPad => Action is GetBoost { Finished: false } trip ? trip.ChosenBoost : null;

        /// <summary>
        /// Drives to <paramref name="pad"/>, keeping a trip already headed there. A trip may only dodge
        /// while the slack covers the flip, because a flip cannot be called off.
        /// </summary>
        private bool FollowPad(Boost pad, float slack, string decision)
        {
            BoostSlack = slack;
            if (pad == null)
                return false;

            bool dodges = slack >= BoostEconomy.DodgeSlack;
            if (!(Action is GetBoost trip) || trip.Finished || trip.ChosenBoost?.Index != pad.Index)
                Action = new GetBoost(Me, pad.Index, interruptible: true, allowDodges: dodges);
            else
                trip.AllowDodges = dodges;
            SetDecision(decision);
            return true;
        }

        private void GuardTo(Vec3 destination, float cruiseSpeed, float terminalSpeed,
            bool holdPosition, bool allowDodges = false)
        {
            if (!ControlMath.Finite(destination))
                destination = OurGoal.Location;

            if (Action is DefensiveDrive guard)
            {
                guard.Target = destination;
                guard.CruiseSpeed = cruiseSpeed;
                guard.TerminalSpeed = terminalSpeed;
                guard.HoldPosition = holdPosition;
                guard.AllowDodges = allowDodges;
            }
            else
            {
                Action = new DefensiveDrive(Me, destination, cruiseSpeed,
                    terminalSpeed, holdPosition, allowDodges);
            }
        }

        private void DriveTo(Vec3 destination, float speed, bool allowDodges, bool allowHandbrake = true,
            bool urgentBoost = false)
        {
            if (!ControlMath.Finite(destination))
                destination = OurGoal.Location;

            if (Action is Drive drive)
            {
                drive.Target = destination;
                drive.TargetSpeed = speed;
                drive.AllowDodges = allowDodges;
                drive.AllowHandbrake = allowHandbrake;
                drive.WasteBoost = urgentBoost;
            }
            else
            {
                Action = new Drive(Me, destination, speed, allowDodges, wasteBoost: urgentBoost)
                {
                    AllowHandbrake = allowHandbrake
                };
            }
        }

        private void SetDecision(string decision)
        {
            if (Decision == decision)
                return;

            string previous = Decision;
            Decision = decision;
            telemetry.Decision(this, previous);
            if (Options.Trace)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"stardust t={Game.Time:F3} car={Index} decision={Decision} rank={Situation.TeamRank}/{Situation.TeamCount} eta={Situation.MyEta:F2} opponent={Situation.OpponentEta:F2} pressure={Situation.PressureTime:F2} last_back={Situation.LastBack} cover={Situation.HasCover} goal_side={Defense.IsGoalSide(Me.Location, Ball.Location, OurGoal.Location)}"));
            }
        }

        /// <summary>Refreshes <see cref="Situation"/> (arrival times, ranks, pressure) from the current world.</summary>
        protected void Assess(float pressureTime)
        {
            Situation = Tactics.Evaluate(this);
            Situation.PressureTime = pressureTime;
        }

        protected override void OnOutputReady() => telemetry.Sample(this);

        // Retained for compatibility with the original Shadow action.
        public bool IsBack() => CanDefend(Me, OurGoal.Location) || Situation.FirstMan == Index;

        public static bool CanBlock(Car car, Vec3 location) =>
            ControlMath.Unit(location - car.Location, Vec3.Up)
                .Dot(ControlMath.Unit(car.Location - Ball.Location, Vec3.Up)) > 0.7f;

        public static bool CanDefend(Car car, Vec3 location)
        {
            if (CanBlock(car, location))
                return true;

            float eta = Drive.GetEta(car, location);
            float speed = MathF.Max(
                Ball.Velocity.Dot(ControlMath.Unit(location - Ball.Location, Vec3.Up)),
                1500f);
            return float.IsFinite(eta) && eta < Ball.Location.Dist(location) / speed;
        }
    }
}
