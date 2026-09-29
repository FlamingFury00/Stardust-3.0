using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    /// <summary>
    /// Boost as a routed resource. A pro rotation is never a straight line to a position: it bends
    /// through the pads it passes, and takes a big pad whenever the play leaves time for one. Every
    /// pad is priced in seconds. What a pad is worth falls as the tank fills (the fifth unit of
    /// boost saves more time than the ninetieth), and a pad is taken when that worth exceeds the
    /// extra travel and the extra travel fits the slack the situation leaves.
    /// </summary>
    public static class BoostEconomy
    {
        /// <summary>
        /// Seconds of detour one unit of boost is worth for an empty tank. A full tank burned on the
        /// straights saves about 1.9 s of travel; the rest covers what boost adds to a touch.
        /// </summary>
        public static float SecondsPerBoost = 0.03f;
        /// <summary>How quickly the value of a unit of boost falls as the tank fills.</summary>
        public static float ValueCurve = 1.4f;
        /// <summary>Time (s) kept in hand ahead of the opponent's arrival at the ball.</summary>
        public static float ReserveTime = 0.45f;
        /// <summary>Extra reserve (s) when the ball is deep in our half, where lateness costs a goal.</summary>
        public static float DepthReserve = 0.6f;
        /// <summary>Slack (s) a covered support car gains from the first man's contest, and an uncovered one.</summary>
        public static float CoveredSupportSlack = 0.9f, UncoveredSupportSlack = 0.4f;
        /// <summary>Detour (s) any pad may cost while the opponent is not on the ball: a pad beside the route is free.</summary>
        public static float FreeDetour = 0.08f;
        /// <summary>Extra reserve (s) when an opponent is about to touch the ball.</summary>
        public static float PressureReserve = 0.25f;
        /// <summary>Share of the reserve a support or anchor car keeps: the first man's contest, not its own, holds the ball.</summary>
        public static float SupportReserveScale = 1f;
        /// <summary>Longest detour (s) any pad may cost.</summary>
        public static float MaxSlack = 3f;
        /// <summary>Boost at or above which no pad is worth a trip.</summary>
        public static float FullEnough = 96f;
        /// <summary>A pad already being driven to keeps its place unless another is this much better.</summary>
        public static float Continuation = 1.4f;
        /// <summary>Net worth (s) below which a pad is not worth the decision.</summary>
        public static float MinimumNet = 0.03f;
        /// <summary>A pad the opponent reaches this much earlier is theirs.</summary>
        public static float ContestMargin = 0.15f;
        /// <summary>Pads examined exactly after the cheap geometric filter.</summary>
        private const int Shortlist = 6;
        /// <summary>Speed (uu/s) assumed for the leg from the pad to the destination.</summary>
        private const float ExitSpeed = 1300f;

        private static float SmoothStep(float value)
        {
            value = System.Math.Clamp(value, 0f, 1f);
            return value * value * (3f - 2f * value);
        }

        /// <summary>
        /// Seconds of travel a pad's boost is worth to a car holding <paramref name="boost"/>:
        /// the integral of the falling marginal value over the units it actually adds.
        /// </summary>
        public static float Worth(float boost, bool large)
        {
            float have = System.Math.Clamp(float.IsFinite(boost) ? boost : 0f, 0f, 100f);
            float gained = MathF.Min(100f - have, large ? 100f : 12f);
            if (gained <= 0f)
                return 0f;

            float exponent = ValueCurve + 1f;
            float Level(float amount) => MathF.Pow(1f - amount / 100f, exponent);
            return SecondsPerBoost * 100f / exponent * (Level(have) - Level(have + gained));
        }

        /// <summary>
        /// Extra travel time (s) the situation can absorb before the car is late for its job. The job
        /// is to stand at <paramref name="destination"/> by the time the opponent can act on the ball,
        /// less a reserve that grows as the ball nears our net and while the car is out of position.
        /// </summary>
        /// <param name="toDestination">Time (s) the car needs to reach its destination directly.</param>
        /// <param name="ballDepth">Ball distance (uu) into our half; negative in theirs.</param>
        /// <param name="pressureTime">Time (s) until an opponent's likely touch, when one is coming.</param>
        public static float Slack(TacticalFrame frame, float toDestination, float ballDepth, bool recovering,
            float pressureTime = float.PositiveInfinity)
        {
            if (frame == null || !float.IsFinite(toDestination))
                return 0f;

            float opponent = float.IsFinite(frame.OpponentEta) ? frame.OpponentEta : 6f;
            if (float.IsFinite(pressureTime))
                opponent = MathF.Min(opponent, pressureTime);
            float reserve = ReserveTime + DepthReserve * SmoothStep((ballDepth - 1200f) / 3300f) +
                (recovering ? 0.35f : 0f);
            bool support = frame.TeamCount > 1 && frame.TeamRank > 0;
            if (support)
                reserve *= SupportReserveScale;
            float shared = support ? (frame.HasCover ? CoveredSupportSlack : UncoveredSupportSlack) : 0f;
            float slack = System.Math.Clamp(opponent + shared - toDestination - reserve, 0f, MaxSlack);
            return opponent >= 0.4f ? MathF.Max(slack, FreeDetour) : slack;
        }

        /// <summary>
        /// Spare time (s) before a planned touch: how long the touch could wait and still beat the
        /// opponent to the ball by the reserve. A car that wins the race by a second can spend most
        /// of it on a pad along the way instead of braking into the touch.
        /// </summary>
        /// <param name="contactTime">Time (s) until the planned touch.</param>
        public static float SlackBeforeContact(TacticalFrame frame, float contactTime, float ballDepth, bool pressured)
        {
            if (frame == null || !float.IsFinite(contactTime))
                return 0f;

            float opponent = float.IsFinite(frame.OpponentEta) ? frame.OpponentEta : 6f;
            float reserve = ReserveTime + (pressured ? PressureReserve : 0f) +
                DepthReserve * SmoothStep((ballDepth - 1200f) / 3300f);
            return System.Math.Clamp(opponent - contactTime - reserve, 0f, MaxSlack);
        }

        /// <summary>A pad with its worth net of the detour it costs (s).</summary>
        private readonly record struct Candidate(Boost Pad, float Net);

        /// <summary>
        /// The pad worth driving to on the way to <paramref name="destination"/>, or null. Pads must
        /// stay goal-side of the ball, be active by arrival, and not be the opponent's to take.
        /// </summary>
        /// <param name="travelTime">Time (s) for a car to reach a point; defaults to the drive model.</param>
        /// <param name="current">The pad already being driven to, which keeps a hysteresis bonus.</param>
        public static Boost Choose(Car car, IEnumerable<Boost> pads, Vec3 ball, Vec3 destination, int team,
            float slack, IEnumerable<Car> opponents, Func<Car, Vec3, float> travelTime = null, Boost current = null)
        {
            if (pads == null || car == null || car.IsDemolished || !float.IsFinite(slack) || slack <= 0f ||
                !float.IsFinite(car.Boost) || car.Boost >= FullEnough || !ControlMath.Finite(destination))
                return null;

            travelTime ??= (c, target) => Drive.GetEta(c, target);
            float direct = travelTime(car, destination);
            if (!float.IsFinite(direct))
                return null;

            float side = Field.Side(team);
            float ballDepth = ball.y * side;
            Vec3 start = car.Location;

            // Cheap filter: the straight-line detour must be affordable before any drive model runs.
            var shortlist = new List<(Boost Pad, float Rough)>();
            foreach (Boost pad in pads)
            {
                if (pad == null || !ControlMath.Finite(pad.Location))
                    continue;
                // Refills stay goal-side of the ball.
                if (pad.Location.y * side < ballDepth - 100f)
                    continue;

                float rough = (start.FlatDist(pad.Location) + pad.Location.FlatDist(destination) -
                    start.FlatDist(destination)) / ExitSpeed;
                if (rough > slack + 0.1f)
                    continue;
                if (Worth(car.Boost, pad.IsLarge) - rough < MinimumNet && !ReferenceEquals(pad, current))
                    continue;
                shortlist.Add((pad, rough));
            }
            shortlist.Sort((a, b) => (Worth(car.Boost, b.Pad.IsLarge) - b.Rough)
                .CompareTo(Worth(car.Boost, a.Pad.IsLarge) - a.Rough));

            Candidate best = default;
            float currentNet = float.NegativeInfinity;
            for (int i = 0; i < shortlist.Count && i < Shortlist; i++)
            {
                Boost pad = shortlist[i].Pad;
                float toPad = travelTime(car, pad.Location);
                if (!float.IsFinite(toPad) || toPad < 0f || toPad > MaxSlack + 2f)
                    continue;
                // A pad which respawns before arrival is a valid pickup; one that will still be dark is not.
                if (!pad.IsActive && pad.TimeUntilActive > toPad + 0.05f)
                    continue;
                if (OpponentTakesFirst(opponents, pad, toPad, travelTime))
                    continue;

                float onward = pad.Location.FlatDist(destination) / ExitSpeed + 0.1f;
                float detour = MathF.Max(0f, toPad + onward - direct);
                if (detour > slack)
                    continue;

                float net = Worth(car.Boost, pad.IsLarge) - detour;
                if (ReferenceEquals(pad, current))
                    currentNet = net;
                if (net > best.Net || best.Pad == null)
                    best = new Candidate(pad, net);
            }

            if (best.Pad == null)
                return null;
            if (float.IsFinite(currentNet) && currentNet > 0f && !ReferenceEquals(best.Pad, current) &&
                best.Net < currentNet * Continuation)
                return current;
            return best.Net >= MinimumNet ? best.Pad : null;
        }

        private static bool OpponentTakesFirst(IEnumerable<Car> opponents, Boost pad, float mine,
            Func<Car, Vec3, float> travelTime)
        {
            if (opponents == null)
                return false;
            foreach (Car opponent in opponents)
            {
                if (opponent == null || opponent.IsDemolished || !ControlMath.Finite(opponent.Location))
                    continue;
                float theirs = travelTime(opponent, pad.Location);
                if (float.IsFinite(theirs) && theirs + ContestMargin < mine)
                    return true;
            }
            return false;
        }
    }
}
