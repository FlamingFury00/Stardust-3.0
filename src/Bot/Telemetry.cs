using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Low-rate, structured in-game telemetry intended for replaying tactical decisions from logs.
    /// It is deliberately observational: no telemetry value feeds back into planning or controls.
    /// </summary>
    internal sealed class StardustTelemetry
    {
        public const string Prefix = "STARDUST_JSON ";

        private readonly bool enabled;
        private readonly float interval;
        private readonly JsonSerializerOptions json = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private float nextSample = float.NegativeInfinity;
        private long sequence;
        private bool faulted;

        public StardustTelemetry(bool enabled, float hz)
        {
            this.enabled = enabled;
            float safeHz = float.IsFinite(hz) ? System.Math.Clamp(hz, 1f, 30f) : 10f;
            interval = 1f / safeHz;
        }

        public void Reset()
        {
            nextSample = float.NegativeInfinity;
            sequence = 0;
        }

        public void Sample(Stardust bot)
        {
            if (!enabled || faulted || bot == null || !float.IsFinite(Game.Time))
                return;
            if (Game.Time + 0.0001f < nextSample)
                return;

            nextSample = Game.Time + interval;
            Write(bot, "frame", null);
        }

        public void Decision(Stardust bot, string previous)
        {
            if (!enabled || faulted || bot == null)
                return;
            Write(bot, "decision", previous);
        }

        private void Write(Stardust bot, string kind, string previousDecision)
        {
            try
            {
                var data = Build(bot, kind, previousDecision);
                Console.WriteLine(Prefix + JsonSerializer.Serialize(data, json));
            }
            catch (Exception error)
            {
                // Telemetry is diagnostic only. Disable it after one fault so logging can never
                // destabilize control or flood the console with repeated serialization failures.
                faulted = true;
                Console.Error.WriteLine(
                    $"STARDUST_TELEMETRY_ERROR {error.GetType().Name}: {error.Message}");
            }
        }

        private Dictionary<string, object> Build(Stardust bot, string kind, string previousDecision)
        {
            Car me = bot.Me;
            Ball ball = Ball.MainBall;
            Vec3 goal = bot.OurGoal.Location;
            TacticalFrame frame = bot.Situation ?? new TacticalFrame();

            float threat = Defense.GoalThreat(
                Ball.Prediction.Slices, goal, Game.Time, 2.5f, out Vec3 crossing);
            float goalProgress = Defense.GoalSideProgress(me.Location, ball.location, goal);
            bool canChallenge = Defense.CanChallenge(frame, me, ball.location, goal);

            bool hasTarget = TryActionTarget(bot.Action, out Vec3 target);
            float targetProgress = hasTarget
                ? Defense.GoalSideProgress(target, ball.location, goal)
                : float.NaN;

            Car attacker = NearestOpponentToBall(bot);
            object attackerData = null;
            if (attacker != null)
            {
                attackerData = new Dictionary<string, object>
                {
                    ["index"] = attacker.Index,
                    ["p"] = Vec(attacker.Location),
                    ["v"] = Vec(attacker.Velocity),
                    ["boost"] = Num(attacker.Boost),
                    ["grounded"] = attacker.IsGrounded,
                    ["ball_dist"] = Num(attacker.Location.Dist(ball.location)),
                    ["goal_side_progress"] = Num(
                        Defense.GoalSideProgress(attacker.Location, ball.location, goal))
                };
            }

            object targetData = null;
            if (hasTarget)
            {
                targetData = new Dictionary<string, object>
                {
                    ["p"] = Vec(target),
                    ["dist"] = Num(me.Location.FlatDist(target)),
                    ["goal_side_progress"] = Num(targetProgress)
                };
            }

            object actionDetail = ActionDetail(bot.Action);

            Vec3 reference = Defense.ReferenceBall(
                Ball.Prediction, ball.location, goal, Game.Time);

            var output = new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["seq"] = sequence++,
                ["kind"] = kind,
                ["t"] = Num(Game.Time, 3),
                ["dt"] = Num(bot.DeltaTime, 4),
                ["game_remaining"] = Num(Game.TimeRemaining, 2),
                ["team"] = bot.Team,
                ["car"] = bot.Index,
                ["score"] = new[] { bot.OurScore, bot.TheirScore },
                ["decision"] = bot.Decision,
                ["previous_decision"] = previousDecision,
                ["action"] = bot.Action?.GetType().Name,
                ["action_interruptible"] = bot.Action?.Interruptible,
                ["action_finished"] = bot.Action?.Finished,
                ["role"] = Role(bot.Decision),
                ["car_state"] = new Dictionary<string, object>
                {
                    ["p"] = Vec(me.Location),
                    ["v"] = Vec(me.Velocity),
                    ["speed"] = Num(me.Velocity.Length()),
                    ["forward_speed"] = Num(me.Velocity.Dot(me.Forward)),
                    ["boost"] = Num(me.Boost),
                    ["grounded"] = me.IsGrounded,
                    ["goal_side_progress"] = Num(goalProgress),
                    ["ball_dist"] = Num(me.Location.Dist(ball.location))
                },
                ["ball"] = new Dictionary<string, object>
                {
                    ["p"] = Vec(ball.location),
                    ["v"] = Vec(ball.velocity),
                    ["speed"] = Num(ball.velocity.Length()),
                    ["goal_dist"] = Num(ball.location.FlatDist(goal)),
                    ["reference_p"] = Vec(reference)
                },
                ["target"] = targetData,
                ["attacker"] = attackerData,
                ["tactics"] = new Dictionary<string, object>
                {
                    ["my_eta"] = Num(frame.MyEta),
                    ["opponent_eta"] = Num(frame.OpponentEta),
                    ["teammate_eta"] = Num(frame.TeammateEta),
                    ["free_time"] = Num(frame.FreeTime),
                    ["pressure_time"] = Num(frame.PressureTime),
                    ["goal_threat_time"] = Num(threat),
                    ["goal_crossing"] = float.IsFinite(threat) ? Vec(crossing) : null,
                    ["rank"] = frame.TeamRank,
                    ["team_count"] = frame.TeamCount,
                    ["first_man"] = frame.FirstMan,
                    ["last_back"] = frame.LastBack,
                    ["has_cover"] = frame.HasCover,
                    ["can_challenge"] = canChallenge
                },
                ["controller"] = new Dictionary<string, object>
                {
                    ["throttle"] = Num(bot.Controller.Throttle),
                    ["steer"] = Num(bot.Controller.Steer),
                    ["pitch"] = Num(bot.Controller.Pitch),
                    ["yaw"] = Num(bot.Controller.Yaw),
                    ["roll"] = Num(bot.Controller.Roll),
                    ["boost"] = bot.Controller.Boost,
                    ["jump"] = bot.Controller.Jump,
                    ["handbrake"] = bot.Controller.Handbrake
                },
                ["action_detail"] = actionDetail
            };

            return output;
        }

        private static Car NearestOpponentToBall(Stardust bot)
        {
            Car best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (Car opponent in bot.LivingOpponents)
            {
                if (opponent == null || !ControlMath.Finite(opponent.Location))
                    continue;
                float distance = opponent.Location.Dist(Ball.Location);
                if (distance < bestDistance)
                {
                    best = opponent;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static bool TryActionTarget(IAction action, out Vec3 target)
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
                default:
                    target = Vec3.Zero;
                    return false;
            }
        }

        private static object ActionDetail(IAction action)
        {
            switch (action)
            {
                case DefensiveDrive defense:
                    return new Dictionary<string, object>
                    {
                        ["cruise_speed"] = Num(defense.CruiseSpeed),
                        ["terminal_speed"] = Num(defense.TerminalSpeed),
                        ["hold_position"] = defense.HoldPosition,
                        ["holding"] = defense.Holding
                    };
                case Drive drive:
                    return new Dictionary<string, object>
                    {
                        ["target_speed"] = Num(drive.TargetSpeed),
                        ["backwards"] = drive.Backwards,
                        ["allow_dodges"] = drive.AllowDodges,
                        ["handbrake_allowed"] = drive.AllowHandbrake
                    };
                case GetBoost boost:
                    return new Dictionary<string, object>
                    {
                        ["pad_index"] = boost.BoostIndex,
                        ["large"] = boost.ChosenBoost?.IsLarge
                    };
                default:
                    return null;
            }
        }

        private static string Role(string decision)
        {
            if (string.IsNullOrEmpty(decision))
                return null;
            if (decision.Contains("shadow", StringComparison.Ordinal))
                return "shadow";
            if (decision.Contains("anchor", StringComparison.Ordinal))
                return "anchor";
            if (decision.Contains("second-man", StringComparison.Ordinal) ||
                decision.Contains("wide lane", StringComparison.Ordinal))
                return "support";
            if (decision.Contains("recover behind ball", StringComparison.Ordinal) ||
                decision.Contains("exit net", StringComparison.Ordinal) ||
                decision.Contains("predicted goal", StringComparison.Ordinal))
                return "recovery";
            return null;
        }

        private static float? Num(float value, int digits = 2) =>
            float.IsFinite(value) ? MathF.Round(value, digits) : null;

        private static float?[] Vec(Vec3 value) => new[]
        {
            Num(value.x, 1),
            Num(value.y, 1),
            Num(value.z, 1)
        };
    }
}
