using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Low-rate structured in-game telemetry for reconstructing tactical decisions from a real match.
    /// Diagnostics are observational only: telemetry never feeds back into planning or controls.
    /// </summary>
    internal sealed class StardustTelemetry
    {
        public const string Prefix = "STARDUST_JSON ";
        private const long MaxFileBytes = 64L * 1024L * 1024L;

        private readonly bool enabled;
        private readonly bool console;
        private readonly float interval;
        private readonly string requestedPath;
        private readonly string defaultPath;
        private readonly JsonSerializerOptions json = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private StreamWriter file;
        private string activePath;
        private float nextSample = float.NegativeInfinity;
        private long sequence;
        private int rotation;
        private bool serializationFaulted;

        public string ActivePath => activePath;

        public StardustTelemetry(bool enabled, float hz, bool console = false, string requestedPath = null)
        {
            this.enabled = enabled;
            this.console = console;
            this.requestedPath = string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath.Trim();
            float safeHz = float.IsFinite(hz) ? System.Math.Clamp(hz, 1f, 30f) : 10f;
            interval = 1f / safeHz;

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            defaultPath = Path.Combine(
                AppContext.BaseDirectory,
                "logs",
                $"stardust-telemetry-{stamp}-pid{Environment.ProcessId}.jsonl");

            if (!enabled)
                return;

            EnsureFile();
            string destination = activePath ?? "console-fallback";
            Console.WriteLine($"STARDUST_TELEMETRY_READY hz={safeHz:0.#} file=\"{destination}\"");
        }

        public void Reset()
        {
            nextSample = float.NegativeInfinity;
        }

        public void Sample(Stardust bot)
        {
            if (!enabled || serializationFaulted || bot == null || !float.IsFinite(Game.Time))
                return;
            if (Game.Time + 0.0001f < nextSample)
                return;

            nextSample = Game.Time + interval;
            Write(bot, "frame", null);
        }

        public void Decision(Stardust bot, string previous)
        {
            if (!enabled || serializationFaulted || bot == null)
                return;
            Write(bot, "decision", previous);
        }

        private void Write(Stardust bot, string kind, string previousDecision)
        {
            string line;
            try
            {
                var data = Build(bot, kind, previousDecision);
                line = JsonSerializer.Serialize(data, json);
            }
            catch (Exception error)
            {
                serializationFaulted = true;
                Console.Error.WriteLine(
                    $"STARDUST_TELEMETRY_ERROR serialize {error.GetType().Name}: {error.Message}");
                return;
            }

            bool persisted = false;
            try
            {
                EnsureFile();
                if (file != null)
                {
                    RotateIfNeeded();
                    file.WriteLine(line);
                    persisted = true;
                }
            }
            catch (Exception error)
            {
                CloseFile();
                Console.Error.WriteLine(
                    $"STARDUST_TELEMETRY_ERROR file {error.GetType().Name}: {error.Message}");
            }

            if (console || !persisted)
                Console.WriteLine(Prefix + line);
        }

        private void EnsureFile()
        {
            if (!enabled || file != null)
                return;

            string primary = requestedPath ?? defaultPath;
            if (TryOpen(primary))
                return;

            string fallback = Path.Combine(
                Path.GetTempPath(),
                "stardust",
                Path.GetFileName(defaultPath));
            TryOpen(fallback);
        }

        private bool TryOpen(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                file = new StreamWriter(new FileStream(
                    full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };
                activePath = full;
                return true;
            }
            catch
            {
                file = null;
                activePath = null;
                return false;
            }
        }

        private void RotateIfNeeded()
        {
            if (file == null || file.BaseStream.Length < MaxFileBytes)
                return;

            string previous = activePath;
            CloseFile();
            rotation++;

            string directory = Path.GetDirectoryName(previous) ?? AppContext.BaseDirectory;
            string stem = Path.GetFileNameWithoutExtension(previous);
            string extension = Path.GetExtension(previous);
            string rotated = Path.Combine(directory, $"{stem}.{rotation}{extension}");
            if (!TryOpen(rotated))
                EnsureFile();
        }

        private void CloseFile()
        {
            try { file?.Dispose(); }
            catch { }
            file = null;
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

            object touchData = null;
            BallTouch touch = Ball.LatestTouch;
            if (touch != null)
            {
                touchData = new Dictionary<string, object>
                {
                    ["t"] = Num(touch.Time, 3),
                    ["player"] = touch.PlayerIndex,
                    ["team"] = touch.Team,
                    ["name"] = touch.PlayerName,
                    ["ball"] = touch.BallIndex,
                    ["p"] = Vec(touch.Location)
                };
            }

            var output = new Dictionary<string, object>
            {
                ["schema"] = 3,
                ["build"] = typeof(Stardust).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
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
                ["possession"] = new Dictionary<string, object>
                {
                    ["roof_quality"] = Num(PossessionControl.RoofControlQuality(me, ball), 3),
                    ["controlled"] = PossessionControl.HasControlledPossession(me, ball),
                    ["retain"] = PossessionControl.ShouldRetainPossession(frame, me, ball, goal)
                },
                ["car_state"] = new Dictionary<string, object>
                {
                    ["p"] = Vec(me.Location),
                    ["v"] = Vec(me.Velocity),
                    ["forward"] = Vec(me.Forward),
                    ["up"] = Vec(me.Up),
                    ["angular_v"] = Vec(me.AngularVelocity),
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
                    ["reference_p"] = Vec(reference),
                    ["latest_touch"] = touchData,
                    ["own_touch_this_tick"] = bot.OwnTouchThisTick
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
                    ["counter_threat_time"] = Num(bot.CounterThreatTime),
                    ["goal_crossing"] = float.IsFinite(threat) ? Vec(crossing) : null,
                    ["rank"] = frame.TeamRank,
                    ["team_count"] = frame.TeamCount,
                    ["first_man"] = frame.FirstMan,
                    ["last_back"] = frame.LastBack,
                    ["has_cover"] = frame.HasCover,
                    ["raw_can_challenge"] = bot.RawCanChallenge,
                    ["can_challenge"] = bot.ChallengeCommitted
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
                case Shot shot:
                    return new Dictionary<string, object>
                    {
                        ["slice_time"] = shot.Slice == null ? null : Num(shot.Slice.Time, 3),
                        ["contact_p"] = shot.Slice == null ? null : Vec(shot.Slice.Location),
                        ["shot_target"] = Vec(shot.ShotTarget),
                        ["target_p"] = Vec(shot.TargetLocation),
                        ["shot_dir"] = Vec(shot.ShotDirection)
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
