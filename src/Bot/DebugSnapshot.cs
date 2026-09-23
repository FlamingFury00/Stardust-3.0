using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    internal sealed class DebugLiveSnapshot
    {
        public int Schema { get; set; } = 1;
        public string Build { get; set; }
        public float T { get; set; }
        public float GameRemaining { get; set; }
        public int Team { get; set; }
        public int Car { get; set; }
        public uint[] Score { get; set; }
        public string Decision { get; set; }
        public string Action { get; set; }
        public string Role { get; set; }
        public string Objective { get; set; }
        public string Explanation { get; set; }
        public DebugCarSnapshot Me { get; set; }
        public DebugBallSnapshot Ball { get; set; }
        public List<DebugCarSnapshot> Cars { get; set; }
        public DebugTargetSnapshot Target { get; set; }
        public DebugTacticsSnapshot Tactics { get; set; }
        public DebugControllerSnapshot Controller { get; set; }
        public DebugChecksSnapshot Checks { get; set; }
        public object ActionDetail { get; set; }
    }

    internal sealed class DebugBallSnapshot
    {
        public float[] P { get; set; }
        public float[] V { get; set; }
        public float[] Av { get; set; }
        public float Speed { get; set; }
        public int? LatestTouchPlayer { get; set; }
        public uint? LatestTouchTeam { get; set; }
        public float? LatestTouchTime { get; set; }
    }

    internal sealed class DebugCarSnapshot
    {
        public int Index { get; set; }
        public uint Team { get; set; }
        public string Name { get; set; }
        public float[] P { get; set; }
        public float[] V { get; set; }
        public float[] Av { get; set; }
        public float[] Rotation { get; set; }
        public float[] Forward { get; set; }
        public float[] Up { get; set; }
        public float Speed { get; set; }
        public float ForwardSpeed { get; set; }
        public float Boost { get; set; }
        public bool Grounded { get; set; }
        public bool Jumped { get; set; }
        public bool DoubleJumped { get; set; }
        public bool Demolished { get; set; }
        public bool Supersonic { get; set; }
        public float GoalSideProgress { get; set; }
        public float GoalDepthProgress { get; set; }
        public float BallDistance { get; set; }
    }

    internal sealed class DebugTargetSnapshot
    {
        public float[] P { get; set; }
        public float Distance { get; set; }
        public float GoalSideProgress { get; set; }
    }

    internal sealed class DebugTacticsSnapshot
    {
        public float MyEta { get; set; }
        public float OpponentEta { get; set; }
        public float OpponentContactEta { get; set; }
        public float TeammateEta { get; set; }
        public float FreeTime { get; set; }
        public float EffectiveFreeTime { get; set; }
        public float SideThreatWeight { get; set; }
        public float? PressureTime { get; set; }
        public float? GoalThreatTime { get; set; }
        public float? CounterThreatTime { get; set; }
        public int Rank { get; set; }
        public int TeamCount { get; set; }
        public int FirstMan { get; set; }
        public bool LastBack { get; set; }
        public bool HasCover { get; set; }
        public bool RawCanChallenge { get; set; }
        public bool CanChallenge { get; set; }
    }

    internal sealed class DebugControllerSnapshot
    {
        public float Throttle { get; set; }
        public float Steer { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
        public float Roll { get; set; }
        public bool Boost { get; set; }
        public bool Jump { get; set; }
        public bool Handbrake { get; set; }
    }

    internal sealed class DebugChecksSnapshot
    {
        public bool GoalSide { get; set; }
        public bool ControlledPossession { get; set; }
        public bool AirControl { get; set; }
        public bool AcquireGround { get; set; }
        public bool GroundDribbleReady { get; set; }
        public bool AcquireAir { get; set; }
        public bool AirCarryReady { get; set; }
        public bool ShotToAirHandoff { get; set; }
        public bool UnderPressure { get; set; }
        public bool Emergency { get; set; }
        public bool CounterDanger { get; set; }
        public bool OpponentGoalLaneOpen { get; set; }
        public bool FastRecoveryAllowed { get; set; }
    }

    internal sealed class DebugBoostSnapshot
    {
        public int Index { get; set; }
        public float[] P { get; set; }
        public bool Large { get; set; }
        public bool Active { get; set; }
        public float TimeUntilActive { get; set; }
    }

    internal sealed class ScenarioFrame
    {
        public float T { get; set; }
        public uint OurScore { get; set; }
        public uint TheirScore { get; set; }
        public int MatchPhase { get; set; }
        public DebugLiveSnapshot Live { get; set; }
        public DebugBallSnapshot Ball { get; set; }
        public List<DebugCarSnapshot> Cars { get; set; }
        public List<DebugBoostSnapshot> Boosts { get; set; }
        public float[] Gravity { get; set; }
    }

    internal sealed class ScenarioCapture
    {
        public int Schema { get; set; } = 1;
        public string Id { get; set; }
        public string Reason { get; set; }
        public string CreatedUtc { get; set; }
        public string Build { get; set; }
        public int Team { get; set; }
        public int BotIndex { get; set; }
        public float TriggerTime { get; set; }
        public uint[] ScoreBefore { get; set; }
        public uint[] ScoreAfter { get; set; }
        public int ReplayFrameIndex { get; set; }
        public List<ScenarioFrame> Frames { get; set; }
    }

    internal static class StardustDebugSnapshot
    {
        public static DebugLiveSnapshot CaptureLive(Stardust bot)
        {
            Ball ball = Ball.MainBall;
            Vec3 ownGoal = bot.OurGoal.Location;
            TacticalFrame frame = bot.Situation ?? new TacticalFrame();
            float threat = Defense.GoalThreat(
                Ball.Prediction.Slices, ownGoal, Game.Time, 2.5f, out _);
            float counterThreat = float.IsFinite(bot.CounterThreatTime)
                ? bot.CounterThreatTime
                : float.PositiveInfinity;
            bool hasTarget = TryActionTarget(bot.Action, out Vec3 target);

            var cars = new List<DebugCarSnapshot>();
            foreach (Car car in Cars.AllCars)
                if (car != null)
                    cars.Add(CarSnapshot(car, ball.location, ownGoal));

            DebugCarSnapshot me = bot.Me != null
                ? CarSnapshot(bot.Me, ball.location, ownGoal)
                : null;

            DebugTargetSnapshot targetData = null;
            if (hasTarget)
            {
                targetData = new DebugTargetSnapshot
                {
                    P = V(target),
                    Distance = Safe(bot.Me.Location.FlatDist(target)),
                    GoalSideProgress = Safe(
                        Defense.GoalSideProgress(target, ball.location, ownGoal))
                };
            }

            bool goalSide = Defense.IsTacticallyGoalSide(
                bot.Me.Location, ball.location, ownGoal, 20f);
            bool controlled = PossessionControl.HasControlledPossession(
                bot.Me, ball);
            bool airControl = PossessionControl.HasAirControl(bot.Me, ball);
            float opponentContactEta = Defense.OpponentContactEta(frame);
            float effectiveFreeTime = Defense.EffectiveFreeTime(frame);
            bool acquireGround = PossessionControl.CanAcquireGround(
                frame, bot.Me, ball, ownGoal);
            bool groundDribbleReady = acquireGround &&
                GroundDribble.CanStart(bot.Me, ball, effectiveFreeTime);
            bool acquireAir = PossessionControl.CanAcquireAir(
                frame, bot.Me, ball, ownGoal);
            bool airCarryReady = acquireAir &&
                AerialCarry.CanStart(bot.Me, ball, opponentContactEta);
            bool shotToAirHandoff = bot.Action is JumpShot &&
                PossessionControl.CanHandoffShotToAirCarry(
                    bot.Me, ball, opponentContactEta);
            bool goalLaneOpen = Tactics.GoalLaneOpen(
                bot.LivingOpponents, ball.location, bot.TheirGoal.Location);
            bool fastRecovery = hasTarget &&
                Defense.CanFastRecover(bot.Me, ball.location, target, ownGoal);

            return new DebugLiveSnapshot
            {
                Build = typeof(Stardust).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                T = Safe(Game.Time),
                GameRemaining = Safe(Game.TimeRemaining),
                Team = bot.Team,
                Car = bot.Index,
                Score = new[] { bot.OurScore, bot.TheirScore },
                Decision = bot.Decision,
                Action = bot.Action?.GetType().Name,
                Role = Role(bot.Decision),
                Objective = Objective(bot.Decision),
                Explanation = Explain(bot.Decision),
                Me = me,
                Ball = BallSnapshot(ball),
                Cars = cars,
                Target = targetData,
                Tactics = new DebugTacticsSnapshot
                {
                    MyEta = Safe(frame.MyEta),
                    OpponentEta = Safe(frame.OpponentEta),
                    OpponentContactEta = Safe(opponentContactEta),
                    TeammateEta = Safe(frame.TeammateEta),
                    FreeTime = Safe(frame.FreeTime),
                    EffectiveFreeTime = Safe(effectiveFreeTime),
                    SideThreatWeight = Safe(Defense.SideThreatWeight(
                        frame, ball.location, ball.velocity, ownGoal)),
                    PressureTime = Nullable(frame.PressureTime),
                    GoalThreatTime = Nullable(threat),
                    CounterThreatTime = Nullable(counterThreat),
                    Rank = frame.TeamRank,
                    TeamCount = frame.TeamCount,
                    FirstMan = frame.FirstMan,
                    LastBack = frame.LastBack,
                    HasCover = frame.HasCover,
                    RawCanChallenge = bot.RawCanChallenge,
                    CanChallenge = bot.ChallengeCommitted
                },
                Controller = new DebugControllerSnapshot
                {
                    Throttle = Safe(bot.Controller.Throttle),
                    Steer = Safe(bot.Controller.Steer),
                    Pitch = Safe(bot.Controller.Pitch),
                    Yaw = Safe(bot.Controller.Yaw),
                    Roll = Safe(bot.Controller.Roll),
                    Boost = bot.Controller.Boost,
                    Jump = bot.Controller.Jump,
                    Handbrake = bot.Controller.Handbrake
                },
                Checks = new DebugChecksSnapshot
                {
                    GoalSide = goalSide,
                    ControlledPossession = controlled,
                    AirControl = airControl,
                    AcquireGround = acquireGround,
                    GroundDribbleReady = groundDribbleReady,
                    AcquireAir = acquireAir,
                    AirCarryReady = airCarryReady,
                    ShotToAirHandoff = shotToAirHandoff,
                    UnderPressure = frame.UnderPressure,
                    Emergency = float.IsFinite(threat),
                    CounterDanger = !float.IsFinite(threat) &&
                        float.IsFinite(counterThreat),
                    OpponentGoalLaneOpen = goalLaneOpen,
                    FastRecoveryAllowed = fastRecovery
                },
                ActionDetail = ActionDetail(bot.Action, bot.Me)
            };
        }

        public static ScenarioFrame CaptureScenarioFrame(Stardust bot)
        {
            var cars = new List<DebugCarSnapshot>();
            foreach (Car car in Cars.AllCars)
                if (car != null)
                    cars.Add(CarSnapshot(
                        car, Ball.Location, bot.OurGoal.Location));

            var boosts = new List<DebugBoostSnapshot>();
            foreach (Boost boost in Field.Boosts)
            {
                if (boost == null)
                    continue;
                boosts.Add(new DebugBoostSnapshot
                {
                    Index = boost.Index,
                    P = V(boost.Location),
                    Large = boost.IsLarge,
                    Active = boost.IsActive,
                    TimeUntilActive = Safe(boost.TimeUntilActive)
                });
            }

            return new ScenarioFrame
            {
                T = Safe(Game.Time),
                OurScore = bot.OurScore,
                TheirScore = bot.TheirScore,
                MatchPhase = (int)Game.MatchPhase,
                Live = CaptureLive(bot),
                Ball = BallSnapshot(Ball.MainBall),
                Cars = cars,
                Boosts = boosts,
                Gravity = V(Game.Gravity)
            };
        }

        private static DebugBallSnapshot BallSnapshot(Ball ball)
        {
            BallTouch touch = Ball.LatestTouch;
            return new DebugBallSnapshot
            {
                P = V(ball.location),
                V = V(ball.velocity),
                Av = V(ball.angularVelocity),
                Speed = Safe(ball.velocity.Length()),
                LatestTouchPlayer = touch?.PlayerIndex,
                LatestTouchTeam = touch?.Team,
                LatestTouchTime = touch == null ? null : Nullable(touch.Time)
            };
        }

        private static DebugCarSnapshot CarSnapshot(
            Car car, Vec3 ball, Vec3 ownGoal) => new()
        {
            Index = car.Index,
            Team = car.Team,
            Name = car.Name,
            P = V(car.Location),
            V = V(car.Velocity),
            Av = V(car.AngularVelocity),
            Rotation = V(car.Rotation),
            Forward = V(car.Forward),
            Up = V(car.Up),
            Speed = Safe(car.Velocity.Length()),
            ForwardSpeed = Safe(car.Velocity.Dot(car.Forward)),
            Boost = Safe(car.Boost),
            Grounded = car.IsGrounded,
            Jumped = car.HasJumped,
            DoubleJumped = car.HasDoubleJumped,
            Demolished = car.IsDemolished,
            Supersonic = car.IsSupersonic,
            GoalSideProgress = Safe(
                Defense.GoalSideProgress(car.Location, ball, ownGoal)),
            GoalDepthProgress = Safe(
                Defense.GoalDepthProgress(car.Location, ball, ownGoal)),
            BallDistance = Safe(car.Location.Dist(ball))
        };

        public static bool TryActionTarget(IAction action, out Vec3 target)
        {
            switch (action)
            {
                case DefensiveDrive defense:
                    target = defense.Target;
                    return ControlMath.Finite(target);
                case Drive drive:
                    target = drive.Target;
                    return ControlMath.Finite(target);
                case GetBoost boost when boost.DriveAction != null:
                    target = boost.DriveAction.Target;
                    return ControlMath.Finite(target);
                case Shadow shadow:
                    target = shadow.TargetLocation;
                    return ControlMath.Finite(target);
                case GoalLineSave save:
                    target = save.GuardTarget;
                    return ControlMath.Finite(target);
                case EmergencyClear clear:
                    target = clear.Target;
                    return ControlMath.Finite(target);
                case Shot shot:
                    target = shot.TargetLocation;
                    return ControlMath.Finite(target);
                default:
                    target = Vec3.Zero;
                    return false;
            }
        }

        private static object ActionDetail(IAction action, Car car)
        {
            switch (action)
            {
                case DefensiveDrive defense:
                    return new Dictionary<string, object>
                    {
                        ["cruise_speed"] = Safe(defense.CruiseSpeed),
                        ["terminal_speed"] = Safe(defense.TerminalSpeed),
                        ["hold"] = defense.HoldPosition,
                        ["holding"] = defense.Holding,
                        ["backwards"] = defense.Backwards,
                        ["allow_dodges"] = defense.AllowDodges,
                        ["allow_boost"] = defense.AllowBoost,
                        ["mobility"] = defense.MobilityAction
                    };
                case Drive drive:
                    return new Dictionary<string, object>
                    {
                        ["target_speed"] = Safe(drive.TargetSpeed),
                        ["backwards"] = drive.Backwards,
                        ["allow_dodges"] = drive.AllowDodges,
                        ["mobility"] = drive.Action?.GetType().Name
                    };
                case Shot shot:
                    return new Dictionary<string, object>
                    {
                        ["slice_time"] = shot.Slice == null
                            ? null
                            : Nullable(shot.Slice.Time),
                        ["contact"] = shot.Slice == null
                            ? null
                            : V(shot.Slice.Location),
                        ["shot_target"] = V(shot.ShotTarget),
                        ["shot_direction"] = V(shot.ShotDirection),
                        ["predicted_speed"] = shot.Slice == null
                            ? 0f
                            : Safe(Tactics.EstimateShotSpeed(car, shot.Slice, shot))
                    };
                case GoalLineSave save:
                    float saveDistance = car?.Location.FlatDist(save.GuardTarget) ??
                        float.PositiveInfinity;
                    float saveRemaining = save.CrossingTime - Game.Time;
                    return new Dictionary<string, object>
                    {
                        ["crossing"] = V(save.Crossing),
                        ["crossing_time"] = Safe(save.CrossingTime),
                        ["guard_distance"] = Safe(saveDistance),
                        ["time_remaining"] = Safe(saveRemaining),
                        ["required_travel_speed"] = Safe(
                            GoalLineSave.RequiredTravelSpeed(saveDistance, saveRemaining)),
                        ["jump_positioned"] = GoalLineSave.IsJumpPositioned(
                            car, save.GuardTarget),
                        ["jumping"] = save.Jumping,
                        ["double_jump"] = save.UsesDoubleJump,
                        ["fast_travel"] = save.FastTravel,
                        ["airborne_flight"] = save.AirborneFlight
                    };
                case EmergencyClear clear:
                    return new Dictionary<string, object>
                    {
                        ["target"] = V(clear.Target),
                        ["clear_direction"] = V(clear.ClearDirection),
                        ["committed"] = clear.Committed,
                        ["ground_block"] = clear.GroundBlock,
                        ["directional_dodge_allowed"] = clear.DirectionalDodgeAllowed,
                        ["neutral_second_jump"] = clear.NeutralSecondJump
                    };
                case GetBoost boost:
                    return new Dictionary<string, object>
                    {
                        ["pad"] = boost.BoostIndex,
                        ["large"] = boost.ChosenBoost?.IsLarge
                    };
                default:
                    return null;
            }
        }

        private static string Role(string decision)
        {
            if (string.IsNullOrWhiteSpace(decision))
                return null;
            if (decision.Contains("shadow", StringComparison.Ordinal))
                return "shadow";
            if (decision.Contains("anchor", StringComparison.Ordinal))
                return "anchor";
            if (decision.Contains("support", StringComparison.Ordinal) ||
                decision.Contains("wide lane", StringComparison.Ordinal))
                return "support";
            if (decision.Contains("recover", StringComparison.Ordinal) ||
                decision.Contains("goal-line", StringComparison.Ordinal))
                return "recovery";
            if (decision.Contains("attack", StringComparison.Ordinal) ||
                decision.Contains("finish", StringComparison.Ordinal))
                return "attack";
            if (decision.Contains("carry", StringComparison.Ordinal) ||
                decision.Contains("catch", StringComparison.Ordinal) ||
                decision.Contains("possess", StringComparison.Ordinal))
                return "possession";
            return null;
        }

        private static string Objective(string decision)
        {
            if (string.IsNullOrWhiteSpace(decision))
                return "Establish state";
            if (decision.Contains("finish now", StringComparison.Ordinal) ||
                decision.Contains("counter-shot", StringComparison.Ordinal))
                return "Score immediately";
            if (decision.Contains("carry", StringComparison.Ordinal) ||
                decision.Contains("catch", StringComparison.Ordinal) ||
                decision.Contains("possess", StringComparison.Ordinal))
                return "Keep ball ownership and create the next outplay";
            if (decision.Contains("recover behind ball", StringComparison.Ordinal) ||
                decision.Contains("counter recover", StringComparison.Ordinal))
                return "Restore goal-side position";
            if (decision.Contains("goal-line", StringComparison.Ordinal))
                return "Block the predicted goal crossing";
            if (decision.Contains("shadow", StringComparison.Ordinal))
                return "Delay the attacker while preserving a challenge angle";
            if (decision.Contains("intercept", StringComparison.Ordinal) ||
                decision.Contains("challenge", StringComparison.Ordinal))
                return "Win or force the next ball contact";
            if (decision.Contains("boost", StringComparison.Ordinal) ||
                decision.Contains("refill", StringComparison.Ordinal))
                return "Refill boost without surrendering coverage";
            return decision;
        }

        private static string Explain(string decision)
        {
            if (string.IsNullOrWhiteSpace(decision))
                return "No tactical decision has been published yet.";
            if (decision.Contains("finish now", StringComparison.Ordinal))
                return "A high-value direct scoring contact outranked keeping possession.";
            if (decision.Contains("counter-shot", StringComparison.Ordinal))
                return "A direct attack is being used as the safest clear of a developing threat.";
            if (decision.Contains("ground carry", StringComparison.Ordinal))
                return "The ball is controllable enough to preserve possession; pressure should trigger an outplay rather than abandonment.";
            if (decision.Contains("aerial carry", StringComparison.Ordinal))
                return "Car and ball flight are close enough to maintain aerial possession.";
            if (decision.Contains("recover behind ball", StringComparison.Ordinal))
                return "The car is ahead of the ball relative to its own goal, so it is routing back to goal-side.";
            if (decision.Contains("goal-line", StringComparison.Ordinal))
                return "The untouched prediction still scores and no earlier scripted clear/intercept was available.";
            if (decision.Contains("shadow", StringComparison.Ordinal))
                return "A direct challenge is not currently safe enough, so Stardust is preserving reaction distance.";
            if (decision.Contains("pressure challenge", StringComparison.Ordinal))
                return "The first defender has a sufficiently competitive contact window to force the play.";
            return "This is the currently selected supervisor action.";
        }

        private static float Safe(float value) =>
            float.IsFinite(value) ? MathF.Round(value, 4) : 0f;

        private static float? Nullable(float value) =>
            float.IsFinite(value) ? MathF.Round(value, 4) : null;

        private static float[] V(Vec3 v) => new[]
        {
            Safe(v.x), Safe(v.y), Safe(v.z)
        };
    }
}
