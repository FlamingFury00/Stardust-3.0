using System;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>Independent feature switches support in-game ablation against the baseline.</summary>
    public sealed class StardustOptions
    {
        public bool GroundControl { get; init; } = Environment.GetEnvironmentVariable("STARDUST_GROUND_CONTROL") != "0";
        public bool AerialCarry { get; init; } = Environment.GetEnvironmentVariable("STARDUST_AERIAL_CARRY") != "0";
        public bool FlipResets { get; init; } = Environment.GetEnvironmentVariable("STARDUST_FLIP_RESETS") == "1";
        public bool Trace { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TRACE") == "1";
    }

    /// <summary>Threat-first planning with explicit goal-side recovery and moving shadow defense.</summary>
    public class Stardust : RUBot
    {
        public StardustOptions Options { get; } = new();
        public TacticalFrame Situation { get; private set; } = new();
        public string Decision { get; private set; } = "startup";
        public bool Shooting { get; set; }

        private float nextPlan = float.NegativeInfinity;
        private bool defending, pressured;
        private Shot defensiveShot;

        public Stardust(string defaultAgentId = null) : base(defaultAgentId) { }

        public override void Run()
        {
            if (ClockReset)
            {
                nextPlan = float.NegativeInfinity;
                defending = false;
                pressured = false;
                defensiveShot = null;
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

            float threat = Defense.GoalThreat(Ball.Prediction.Slices, OurGoal.Location,
                Game.Time, 2.5f, out Vec3 crossing);
            float pressureTime = Tactics.OpponentPressure(LivingOpponents,
                Ball.MainBall, OurGoal.Location);
            bool emergency = float.IsFinite(threat);
            bool underPressure = float.IsFinite(pressureTime);
            bool threatEdge = emergency != defending;
            bool pressureEdge = underPressure != pressured;

            if (threatEdge || pressureEdge)
                nextPlan = float.NegativeInfinity;

            // Do not acknowledge a tactical edge until a physically committed flip/dodge can be
            // interrupted. Otherwise the event is consumed while the old action keeps running.
            if (Action != null && !Action.Interruptible)
                return;

            defending = emergency;
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

            Situation = Tactics.Evaluate(this);
            Situation.PressureTime = pressureTime;
            nextPlan = Game.Time + (underPressure || emergency ? 0.05f : 0.12f);

            if (emergency)
            {
                float deadline = MathF.Max(0f, threat - 0.015f);
                if (!(Action is Shot current) || !ReferenceEquals(Action, defensiveShot) ||
                    current.Slice.Time - Game.Time > deadline)
                {
                    defensiveShot = Tactics.SelectShot(this, true, Situation.OpponentEta,
                        _ => false, deadline);
                    Action = defensiveShot;
                }

                if (Action == null)
                {
                    Vec3 rawGuard = Defense.EmergencyTarget(crossing, OurGoal.Location);
                    Vec3 guard = Tactics.GoalReturnTarget(Me, rawGuard, OurGoal.Location);
                    GuardTo(guard, Car.MaxSpeed, 0f, true);
                }

                SetDecision("defend / predicted goal");
                return;
            }

            bool canChallenge = Defense.CanChallenge(Situation, Me, Ball.Location, OurGoal.Location);
            float attackDeadline = Defense.AttackDeadline(Situation);

            if (Action is IPossessionAction)
            {
                if (canChallenge)
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

            if (Action is GetBoost refill)
            {
                if (!Defense.CanRefill(Situation, Me, Ball.Location, OurGoal.Location, underPressure))
                    Action = null;
                else if (!refill.Finished)
                {
                    SetDecision(refill.ChosenBoost?.IsLarge == true
                        ? "support / full-pad refill"
                        : "support / pad refill");
                    return;
                }
                else
                    Action = null;
            }

            if (!(Action is Drive) && !(Action is DefensiveDrive))
                Action = null;

            if (!Me.IsGrounded)
            {
                if (canChallenge && Options.AerialCarry &&
                    AerialCarry.CanStart(Me, Ball.MainBall, Situation.OpponentEta))
                {
                    Action = new AerialCarry();
                    SetDecision("mechanic / aerial carry");
                    return;
                }

                Shot aerial = canChallenge
                    ? Tactics.SelectShot(this, false, Situation.OpponentEta, HasClaim, attackDeadline)
                    : null;
                Action = aerial ?? (IAction)new Recover();
                SetDecision(aerial == null
                    ? "recover / landing surface"
                    : "attack / airborne intercept");
                return;
            }

            if (canChallenge)
            {
                if (!underPressure && Options.GroundControl &&
                    GroundDribble.CanStart(Me, Ball.MainBall, Situation.FreeTime))
                {
                    Action = new GroundDribble();
                    SetDecision("mechanic / ground carry");
                    return;
                }

                if (!underPressure && Options.GroundControl && Situation.FreeTime > 0.7f &&
                    Ball.Location.z > 200f && GroundCatch.FindCatch(Me) != null)
                {
                    Action = new GroundCatch();
                    SetDecision("mechanic / cushion catch");
                    return;
                }

                Shot attack = Tactics.SelectShot(this, false, Situation.OpponentEta,
                    HasClaim, attackDeadline);
                if (attack != null)
                {
                    Action = attack;
                    SetDecision("attack / economical intercept");
                    return;
                }

                if (Situation.FreeTime > 0.25f)
                {
                    Vec3 lane = ControlMath.FlatUnit(TheirGoal.Location - Ball.Location, Me.Forward);
                    DriveTo(Field.LimitToNearestSurface(Ball.Location - lane * 350f),
                        1500f, false);
                    SetDecision("possess / approach behind ball");
                    return;
                }
            }

            DefensiveRole role = Situation.TeamCount == 1
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
                : Defense.ShadowTarget(reference, OurGoal.Location, role, pressureTime);
            Vec3 support = Tactics.GoalReturnTarget(Me, rawSupport, OurGoal.Location);
            bool exitingGoal = support.FlatDist(rawSupport) > 1f;

            if (!recoveringGoalSide && !exitingGoal && role != DefensiveRole.Shadow &&
                Defense.CanRefill(Situation, Me, Ball.Location, OurGoal.Location, underPressure) &&
                TryBoostDetour(support))
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

            GuardTo(support, cruise, terminal, hold);

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

        private bool TryBoostDetour(Vec3 destination)
        {
            if (Ball.Location.y * Field.Side(Team) > 2500f)
                return false;

            Boost pad = RoutePlanner.SelectBoost(Me, Field.Boosts, Ball.Location,
                destination, Team, Situation.OpponentEta);
            if (pad == null)
                return false;

            Action = new GetBoost(Me, pad.Index, interruptible: true);
            SetDecision(pad.IsLarge
                ? "support / full-pad refill"
                : "support / small-pad route");
            return true;
        }

        private void GuardTo(Vec3 destination, float cruiseSpeed, float terminalSpeed, bool holdPosition)
        {
            if (!ControlMath.Finite(destination))
                destination = OurGoal.Location;

            if (Action is DefensiveDrive guard)
            {
                guard.Target = destination;
                guard.CruiseSpeed = cruiseSpeed;
                guard.TerminalSpeed = terminalSpeed;
                guard.HoldPosition = holdPosition;
            }
            else
            {
                Action = new DefensiveDrive(Me, destination, cruiseSpeed,
                    terminalSpeed, holdPosition);
            }
        }

        private void DriveTo(Vec3 destination, float speed, bool allowDodges, bool allowHandbrake = true)
        {
            if (!ControlMath.Finite(destination))
                destination = OurGoal.Location;

            if (Action is Drive drive)
            {
                drive.Target = destination;
                drive.TargetSpeed = speed;
                drive.AllowDodges = allowDodges;
                drive.AllowHandbrake = allowHandbrake;
                drive.WasteBoost = false;
            }
            else
            {
                Action = new Drive(Me, destination, speed, allowDodges, wasteBoost: false)
                {
                    AllowHandbrake = allowHandbrake
                };
            }
        }

        private void SetDecision(string decision)
        {
            if (Decision == decision)
                return;

            Decision = decision;
            if (Options.Trace)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"stardust t={Game.Time:F3} car={Index} decision={Decision} " +
                    $"rank={Situation.TeamRank}/{Situation.TeamCount} eta={Situation.MyEta:F2} " +
                    $"opponent={Situation.OpponentEta:F2} pressure={Situation.PressureTime:F2} " +
                    $"last_back={Situation.LastBack} cover={Situation.HasCover} " +
                    $"goal_side={Defense.IsGoalSide(Me.Location, Ball.Location, OurGoal.Location)}"));
            }
        }

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
