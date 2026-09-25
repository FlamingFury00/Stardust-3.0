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
        public bool Trace { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TRACE") is "1" or "2";
        /// <summary>STARDUST_TRACE=2 adds per-tick strike execution diagnostics to the trace.</summary>
        public bool StrikeDiagnostics { get; init; } = Environment.GetEnvironmentVariable("STARDUST_TRACE") == "2";
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

    /// <summary>
    /// Stardust: physically planned touches chosen by <see cref="Brain.Brain"/>, with kickoffs
    /// handled separately.
    /// </summary>
    public class Stardust : RUBot
    {
        public StardustOptions Options { get; } = new();
        public Brain.Brain Brain { get; } = new();
        public TacticalFrame Situation { get; private set; } = new();
        public string Decision { get; private set; } = "startup";

        public bool RawCanChallenge => Brain.Role == global::Bot.Brain.Role.Attacker;
        public bool ChallengeCommitted => Action is Shot;
        public float CounterThreatTime => Brain.Analysis?.ThreatTime ?? float.PositiveInfinity;

        private readonly StardustTelemetry telemetry;

        public Stardust(string defaultAgentId = null) : base(defaultAgentId)
        {
            if (Options.StrikeDiagnostics)
            {
                DrivenStrike.Diagnostics = message => Console.WriteLine($"stardust car={Index} {message}");
                AerialStrike.Diagnostics = message => Console.WriteLine($"stardust car={Index} {message}");
                Block.Diagnostics = message => Console.WriteLine($"stardust car={Index} {message}");
            }
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
                Brain.Reset();
                telemetry.Reset();
            }

            if (IsKickoff)
            {
                RunKickoff();
                return;
            }

            if (Options.Trace) TraceActionEnd();
            if (Options.Trace && OwnTouchThisTick && Action is IStrike strike)
                Trace($"touch planned_t={strike.Plan.ContactTime:F3} predicted={strike.Plan.Contact.BallVelocity} actual={Ball.Velocity}");
            if (Options.Trace && OwnTouchThisTick && Action is Block touchedBlock)
                Trace($"touch planned_t={touchedBlock.Plan.ContactTime:F3} predicted=(0, 0, 0) actual={Ball.Velocity}");

            Brain.Think(this);
            UpdateSituation();
            if (Options.Trace) TraceActionStart();
        }

        private IAction tracedAction;

        /// <summary>Logs a strike that ended since the last tick, with the reason it stood down.</summary>
        private void TraceActionEnd()
        {
            if (tracedAction is IStrike ended && !ReferenceEquals(ended, Action))
                Trace($"strike end kind={ended.Plan.Kind} planned_t={ended.Plan.ContactTime:F3} status={ended.Status}");
            if (tracedAction is Block block && !ReferenceEquals(block, Action))
                Trace($"strike end kind=Block planned_t={block.Plan.ContactTime:F3} status={block.Status}");
            if (!ReferenceEquals(tracedAction, Action)) tracedAction = Action;
        }

        private void TraceActionStart()
        {
            if (ReferenceEquals(tracedAction, Action)) return;
            tracedAction = Action;
            if (Action is IStrike started)
                Trace($"strike start decision={Decision} plan={started.Plan}");
            if (Action is Block block)
                Trace($"strike start decision={Decision} plan={block.Plan}");
        }

        private void Trace(FormattableString message) =>
            Console.WriteLine($"stardust t={Game.Time.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} car={Index} " + FormattableString.Invariant(message));

        /// <summary>Whether a teammate has claimed a touch earlier than <paramref name="sliceTime"/>.</summary>
        public bool Claimed(float sliceTime) => HasTeammateEarlierShot(sliceTime);

        private void RunKickoff()
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
                var travel = Action as Travel ?? new Travel(default);
                travel.Target = new RedUtils.Physics.DriveTarget(target, (Ball.Location - target).Flatten()) { ArrivalSpeed = rank == 1 ? 900f : 0f };
                Action = travel;
                SetDecision(rank == 1 ? "kickoff / cheat" : "kickoff / cover");
            }
        }

        /// <summary>Summarises the brain's analysis in the legacy frame used by telemetry.</summary>
        private void UpdateSituation()
        {
            Brain.Analysis a = Brain.Analysis;
            if (a == null) return;
            float Eta(in Brain.Intercept i) => i.Reachable ? i.Time - a.Now : 6f;
            Situation = new TacticalFrame
            {
                MyEta = Eta(a.Mine),
                OpponentEta = Eta(a.FirstOpponent),
                TeammateEta = Eta(a.FirstTeammate),
                TeamCount = a.Teammates.Count + 1,
                TeamRank = (int)Brain.Role,
                FirstMan = Brain.Role == global::Bot.Brain.Role.Attacker ? Index : a.FirstTeammate.Index,
                LastBack = Brain.Role == global::Bot.Brain.Role.Anchor || a.Teammates.Count == 0,
                HasCover = a.Teammates.Count > 0 && Brain.Role != global::Bot.Brain.Role.Anchor,
            };
        }

        public void SetDecision(string decision)
        {
            if (Decision == decision)
                return;

            string previous = Decision;
            Decision = decision;
            telemetry.Decision(this, previous);
            if (Options.Trace)
            {
                Brain.Analysis a = Brain.Analysis;
                string race = a == null ? "" : FormattableString.Invariant(
                    $" me={a.Mine.Time - a.Now:F2} opp={a.FirstOpponent.Time - a.Now:F2} threat={a.ThreatTime:F2}");
                Trace($"decision={Decision} action={Action?.GetType().Name ?? "none"} role={Brain.Role}{race}");
            }
        }

        protected override void OnOutputReady()
        {
            telemetry.Sample(this);
            if (Options.Trace) TraceBoostUse();
        }

        private readonly System.Collections.Generic.Dictionary<string, float> boostSeconds = new();
        private float nextBoostReport = 60f;

        /// <summary>Accumulates boosting time per action type and reports it once a game-minute.</summary>
        private void TraceBoostUse()
        {
            if (Controller.Boost && Me.Boost > 0f)
            {
                string key = Action?.GetType().Name ?? "none";
                boostSeconds[key] = (boostSeconds.TryGetValue(key, out float seconds) ? seconds : 0f) + DeltaTime;
            }
            if (Game.Time < nextBoostReport) return;
            nextBoostReport = Game.Time + 60f;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var pair in boostSeconds) parts.Add(FormattableString.Invariant($"{pair.Key}={pair.Value:F1}s"));
            Trace($"boost use {string.Join(" ", parts)}");
        }
    }
}
