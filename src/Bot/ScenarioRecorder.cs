using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using RedUtils;

namespace Bot
{
    internal sealed class ScenarioFileInfo
    {
        public string Name { get; set; }
        public long Bytes { get; set; }
        public string ModifiedUtc { get; set; }
        public bool Replayable { get; set; }
    }

    internal sealed class StardustScenarioRecorder
    {
        private readonly bool enabled;
        private readonly float interval;
        private readonly float preSeconds;
        private readonly string directory;
        private readonly Queue<ScenarioFrame> frames = new();
        private readonly object commandGate = new();
        private readonly JsonSerializerOptions json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        private float nextSample = float.NegativeInfinity;
        private bool initializedScore;
        private uint lastOurScore;
        private uint lastTheirScore;
        private bool manualRequested;
        private string replayRequested;
        private string lastSaved;

        public bool Enabled => enabled;
        public string DirectoryPath => directory;
        public string LastSavedPath => lastSaved;

        public StardustScenarioRecorder(bool enabled, float hz, float preSeconds, string requestedDirectory)
        {
            this.enabled = enabled;
            float safeHz = float.IsFinite(hz)
                ? System.Math.Clamp(hz, 2f, 60f)
                : 20f;
            interval = 1f / safeHz;
            this.preSeconds = float.IsFinite(preSeconds)
                ? System.Math.Clamp(preSeconds, 2f, 20f)
                : 8f;
            directory = ResolveDirectory(requestedDirectory);

            if (!enabled)
                return;

            try
            {
                Directory.CreateDirectory(directory);
                Console.WriteLine(
                    $"STARDUST_SCENARIOS_READY dir=\"{directory}\" pre={this.preSeconds:0.#}s hz={safeHz:0.#}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"STARDUST_SCENARIO_ERROR init {error.GetType().Name}: {error.Message}");
            }
        }

        public void Reset()
        {
            frames.Clear();
            nextSample = float.NegativeInfinity;
            initializedScore = false;
        }

        public void RequestManualSave()
        {
            lock (commandGate)
                manualRequested = true;
        }

        public void RequestReplay(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;
            lock (commandGate)
                replayRequested = Path.GetFileName(fileName);
        }

        public void OnPacket(Stardust bot)
        {
            if (!enabled || bot == null || !float.IsFinite(Game.Time))
                return;

            if (bot.ClockReset)
                Reset();

            if (!initializedScore)
            {
                lastOurScore = bot.OurScore;
                lastTheirScore = bot.TheirScore;
                initializedScore = true;
            }

            if (Game.Time + 0.0001f >= nextSample)
            {
                nextSample = Game.Time + interval;
                AddFrame(StardustDebugSnapshot.CaptureScenarioFrame(bot));
            }

            bool conceded = bot.TheirScore > lastTheirScore;
            bool scored = bot.OurScore > lastOurScore;
            if (conceded)
                Save(bot, "conceded", lastOurScore, lastTheirScore);
            else if (scored)
                TrimAfterScore();

            lastOurScore = bot.OurScore;
            lastTheirScore = bot.TheirScore;

            bool save;
            string replay;
            lock (commandGate)
            {
                save = manualRequested;
                manualRequested = false;
                replay = replayRequested;
                replayRequested = null;
            }

            if (save)
                Save(bot, "manual", bot.OurScore, bot.TheirScore);
            if (!string.IsNullOrWhiteSpace(replay))
                Replay(bot, replay);
        }

        public List<ScenarioFileInfo> ListScenarios()
        {
            if (!enabled)
                return new List<ScenarioFileInfo>();

            try
            {
                Directory.CreateDirectory(directory);
                return Directory.EnumerateFiles(directory, "*.json")
                    .Select(path =>
                    {
                        var info = new FileInfo(path);
                        return new ScenarioFileInfo
                        {
                            Name = info.Name,
                            Bytes = info.Length,
                            ModifiedUtc = info.LastWriteTimeUtc.ToString("O"),
                            Replayable = info.Length > 32
                        };
                    })
                    .OrderByDescending(x => x.ModifiedUtc)
                    .Take(100)
                    .ToList();
            }
            catch
            {
                return new List<ScenarioFileInfo>();
            }
        }

        private void AddFrame(ScenarioFrame frame)
        {
            if (frame == null)
                return;

            frames.Enqueue(frame);
            float cutoff = frame.T - preSeconds;
            while (frames.Count > 1 && frames.Peek().T < cutoff)
                frames.Dequeue();
        }

        private void TrimAfterScore()
        {
            if (frames.Count <= 1)
                return;

            ScenarioFrame latest = frames.Last();
            frames.Clear();
            frames.Enqueue(latest);
        }

        private void Save(Stardust bot, string reason, uint beforeOur, uint beforeTheir)
        {
            if (frames.Count == 0)
                AddFrame(StardustDebugSnapshot.CaptureScenarioFrame(bot));

            try
            {
                Directory.CreateDirectory(directory);
                List<ScenarioFrame> timeline = frames.ToList();
                int replayFrame = FindReplayFrame(
                    timeline, beforeOur, beforeTheir);
                string id =
                    $"{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-t{Game.Time:000.000}-" +
                    $"{beforeOur}-{beforeTheir}_to_{bot.OurScore}-{bot.TheirScore}";
                string path = Path.Combine(directory, Sanitize(id) + ".json");

                var capture = new ScenarioCapture
                {
                    Id = id,
                    Reason = reason,
                    CreatedUtc = DateTime.UtcNow.ToString("O"),
                    Build = typeof(Stardust).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                    Team = bot.Team,
                    BotIndex = bot.Index,
                    TriggerTime = Game.Time,
                    ScoreBefore = new[] { beforeOur, beforeTheir },
                    ScoreAfter = new[] { bot.OurScore, bot.TheirScore },
                    ReplayFrameIndex = replayFrame,
                    Frames = timeline
                };

                string payload = JsonSerializer.Serialize(capture, json);
                File.WriteAllText(path, payload);
                lastSaved = path;
                Console.WriteLine(
                    $"STARDUST_SCENARIO_SAVED reason={reason} frames={timeline.Count} replay={replayFrame} file=\"{path}\"");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"STARDUST_SCENARIO_ERROR save {error.GetType().Name}: {error.Message}");
            }
        }

        private static int FindReplayFrame(
            List<ScenarioFrame> timeline, uint beforeOur, uint beforeTheir)
        {
            for (int i = timeline.Count - 1; i >= 0; i--)
            {
                ScenarioFrame frame = timeline[i];
                if (frame.OurScore == beforeOur &&
                    frame.TheirScore == beforeTheir)
                    return i;
            }
            return Math.Max(0, timeline.Count - 1);
        }

        private void Replay(Stardust bot, string fileName)
        {
            string safe = Path.GetFileName(fileName);
            string path = Path.Combine(directory, safe);
            if (!File.Exists(path))
                return;

            try
            {
                string payload = File.ReadAllText(path);
                ScenarioCapture capture =
                    JsonSerializer.Deserialize<ScenarioCapture>(payload, json);
                if (capture?.Frames == null || capture.Frames.Count == 0)
                    return;

                int index = System.Math.Clamp(
                    capture.ReplayFrameIndex, 0, capture.Frames.Count - 1);
                ScenarioFrame frame = capture.Frames[index];
                if (frame?.Ball == null || frame.Cars == null)
                    return;

                var state = bot.GameStateBuilder()
                    .Ball(0, b => b
                        .Location(ToNumerics(frame.Ball.P))
                        .Velocity(ToNumerics(frame.Ball.V))
                        .AngularVelocity(ToNumerics(frame.Ball.Av)));

                foreach (DebugCarSnapshot car in frame.Cars)
                {
                    DebugCarSnapshot local = car;
                    state = state.Car(local.Index, c => c
                        .Location(ToNumerics(local.P))
                        .Velocity(ToNumerics(local.V))
                        .AngularVelocity(ToNumerics(local.Av))
                        .Rotation(ToNumerics(local.Rotation))
                        .Boost(local.Boost));
                }

                state.BuildAndSend();
                Console.WriteLine(
                    $"STARDUST_SCENARIO_REPLAY file=\"{safe}\" frame={index} t={frame.T:0.000}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"STARDUST_SCENARIO_ERROR replay {error.GetType().Name}: {error.Message}");
            }
        }

        private static System.Numerics.Vector3 ToNumerics(float[] value)
        {
            if (value == null || value.Length < 3)
                return System.Numerics.Vector3.Zero;
            return new System.Numerics.Vector3(value[0], value[1], value[2]);
        }

        private static string ResolveDirectory(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return Path.GetFullPath(requested.Trim());

            try
            {
                DirectoryInfo current = new(AppContext.BaseDirectory);
                for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
                {
                    if (File.Exists(Path.Combine(current.FullName, "Stardust.bot.toml")))
                        return Path.Combine(current.FullName, "scenarios", "captured");
                }
            }
            catch { }

            return Path.Combine(AppContext.BaseDirectory, "scenarios", "captured");
        }

        private static string Sanitize(string value)
        {
            foreach (char bad in Path.GetInvalidFileNameChars())
                value = value.Replace(bad, '-');
            return value.Replace(' ', '-');
        }
    }
}
