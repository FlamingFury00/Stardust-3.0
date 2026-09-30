using System;
using System.Collections.Generic;
using System.Globalization;

namespace Bot
{
    /// <summary>
    /// What a bot process can be told at start-up: diagnostics and parameter variants for the
    /// simulator. Nothing here switches a mechanic on or off, and nothing is read from the
    /// environment; every mechanic always runs.
    /// </summary>
    public sealed class StardustOptions
    {
        /// <summary>Log strategy transitions and ETA estimates.</summary>
        public bool Trace { get; init; }
        /// <summary>Also log the save options weighed on every planning tick (verbose).</summary>
        public bool TraceSaves { get; init; }
        /// <summary>Telemetry on or off; null leaves it to the default: on in a real match, off in a simulated instance.</summary>
        public bool? Telemetry { get; init; }
        /// <summary>Also print every telemetry line.</summary>
        public bool TelemetryConsole { get; init; }
        /// <summary>Telemetry file; the default is next to the executable.</summary>
        public string TelemetryFile { get; init; }
        /// <summary>Telemetry samples per second, clamped to 1–30.</summary>
        public int TelemetryHz { get; init; } = 10;
        /// <summary>Tuning assignments <c>Type.Field=value,...</c> for parameter variants; only JIT builds can apply them.</summary>
        public string Tune { get; init; }

        /// <summary>
        /// Parses <c>--trace</c>, <c>--trace-saves</c>, <c>--telemetry</c>, <c>--no-telemetry</c>,
        /// <c>--telemetry-console</c>, <c>--telemetry-file=PATH</c>, <c>--telemetry-hz=N</c> and
        /// <c>--tune=Type.Field=value,...</c>. Anything else is reported through <paramref name="unknown"/>.
        /// </summary>
        public static StardustOptions FromArguments(IEnumerable<string> arguments, Action<string> unknown = null)
        {
            bool trace = false, traceSaves = false, console = false;
            bool? telemetry = null;
            string file = null, tune = null;
            int hz = 10;
            foreach (string argument in arguments ?? Array.Empty<string>())
            {
                int equals = argument.IndexOf('=');
                string name = equals < 0 ? argument : argument[..equals];
                string value = equals < 0 ? null : argument[(equals + 1)..];
                switch (name)
                {
                    case "--trace": trace = true; break;
                    case "--trace-saves": traceSaves = true; break;
                    case "--telemetry": telemetry = true; break;
                    case "--no-telemetry": telemetry = false; break;
                    case "--telemetry-console": console = true; break;
                    case "--telemetry-file" when !string.IsNullOrEmpty(value): file = value; break;
                    case "--telemetry-hz" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                        hz = Math.Clamp(parsed, 1, 30);
                        break;
                    case "--tune" when !string.IsNullOrEmpty(value): tune = value; break;
                    default: unknown?.Invoke(argument); break;
                }
            }

            return new StardustOptions
            {
                Trace = trace, TraceSaves = traceSaves, Telemetry = telemetry, TelemetryConsole = console,
                TelemetryFile = file, TelemetryHz = hz, Tune = tune,
            };
        }
    }
}
