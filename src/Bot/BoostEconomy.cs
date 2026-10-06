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
        /// Seconds of detour one unit of boost is worth for an empty tank. The worth then falls as the
        /// tank fills, so a full pad taken from empty is worth <c>SecondsPerBoost * 100 / (ValueCurve + 1)</c>,
        /// about 1.25 s at the defaults.
        /// </summary>
        public static float SecondsPerBoost = 0.03f;
        /// <summary>How quickly the value of a unit of boost falls as the tank fills.</summary>
        public static float ValueCurve = 1.4f;
        /// <summary>Time (s) kept in hand ahead of the opponent's arrival at the ball.</summary>
        public static float ReserveTime = 0.45f;
        /// <summary>Extra reserve (s) when the ball is deep in our half, where lateness costs a goal.</summary>
        public static float DepthReserve = 0.6f;
        /// <summary>Extra reserve (s) while the car is out of position and recovering goal side.</summary>
        public static float RecoveryReserve = 0.35f;
        /// <summary>Slack (s) a covered support car gains from the first man's contest, and an uncovered one.</summary>
        public static float CoveredSupportSlack = 0.9f, UncoveredSupportSlack = 0.4f;
        /// <summary>Detour (s) any pad may cost while the opponent is not on the ball: a pad beside the route is free.</summary>
        public static float FreeDetour = 0.08f;
        /// <summary>Opponent arrival (s) from which the free detour is granted.</summary>
        public static float FreeDetourEta = 0.4f;
        /// <summary>Extra reserve (s) when an opponent is about to touch the ball.</summary>
        public static float PressureReserve = 0.25f;
        /// <summary>Share of the reserve a support car keeps: the first man's contest, not its own, holds the ball.</summary>
        public static float SupportReserveScale = 1f;
        /// <summary>Time (s) a pre-shot detour must leave before a teammate could reach the ball first.</summary>
        public static float TeammateReserve = 0.25f;
        /// <summary>Longest detour (s) any pad may cost.</summary>
        public static float MaxSlack = 3f;
        /// <summary>Slack (s) a trip needs before it may dodge: a flip commits the car for over a second.</summary>
        public static float DodgeSlack = 1.6f;

        /// <summary>Whether a trip with <paramref name="slack"/> seconds to spare may dodge or speed-flip.</summary>
        public static bool MayDodge(float slack) => slack >= DodgeSlack;
        /// <summary>A pad already being driven to keeps its place unless another is this much better.</summary>
        public static float Continuation = 1.4f;
        /// <summary>Net worth (s) below which a pad is not worth the decision.</summary>
        public static float MinimumNet = 0.03f;
        /// <summary>A pad the opponent or a teammate reaches this much earlier is theirs.</summary>
        public static float ContestMargin = 0.15f;
        /// <summary>Pads examined exactly after the cheap geometric filter.</summary>
        private const int Shortlist = 6;
        /// <summary>Speed (uu/s) assumed for the leg from the pad to the destination.</summary>
        private const float ExitSpeed = 1300f;
        /// <summary>Time (s) added to the leg from the pad to the destination for the turn out of it.</summary>
        private const float PadExitTime = 0.1f;
        /// <summary>Opponent arrival (s) assumed when no opponent is left to race.</summary>
        private const float NoOpponentEta = 6f;
        /// <summary>Ball depth (uu) at which the depth reserve starts to grow, and the depth span over which it does.</summary>
        private const float DepthStart = 1200f, DepthSpan = 3300f;

        /// <summary>The reserve (s) that grows as the ball nears our net.</summary>
        private static float DepthReserveAt(float ballDepth) =>
            DepthReserve * ControlMath.SmoothStep((ballDepth - DepthStart) / DepthSpan);

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

        /// <summary>Whether a car holding <paramref name="boost"/> could gain enough from any pad to bother routing.</summary>
        public static bool Wanted(float boost) => Worth(boost, true) >= MinimumNet;

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

            float opponent = float.IsFinite(frame.OpponentEta) ? frame.OpponentEta : NoOpponentEta;
            if (float.IsFinite(pressureTime))
                opponent = MathF.Min(opponent, pressureTime);
            float reserve = ReserveTime + DepthReserveAt(ballDepth) + (recovering ? RecoveryReserve : 0f);
            bool support = frame.TeamCount > 1 && frame.TeamRank > 0;
            if (support)
                reserve *= SupportReserveScale;
            float shared = support ? (frame.HasCover ? CoveredSupportSlack : UncoveredSupportSlack) : 0f;
            float slack = System.Math.Clamp(opponent + shared - toDestination - reserve, 0f, MaxSlack);
            return opponent >= FreeDetourEta ? MathF.Max(slack, FreeDetour) : slack;
        }

        /// <summary>
        /// Spare time (s) before a planned touch: how long the touch could wait and still beat the
        /// opponent to the ball by the reserve, and still leave the teammate no chance to take it.
        /// A car that wins the race by a second can spend most of it on a pad along the way instead
        /// of braking into the touch.
        /// </summary>
        /// <param name="contactTime">Time (s) until the planned touch.</param>
        /// <param name="ballDepth">Ball distance (uu) into our half; negative in theirs.</param>
        /// <param name="pressureTime">Time (s) until an opponent's likely touch, when one is coming.</param>
        public static float SlackBeforeContact(TacticalFrame frame, float contactTime, float ballDepth,
            float pressureTime = float.PositiveInfinity)
        {
            if (frame == null || !float.IsFinite(contactTime))
                return 0f;

            float opponent = float.IsFinite(frame.OpponentEta) ? frame.OpponentEta : NoOpponentEta;
            bool pressured = float.IsFinite(pressureTime);
            if (pressured)
                opponent = MathF.Min(opponent, pressureTime);
            float reserve = ReserveTime + (pressured ? PressureReserve : 0f) + DepthReserveAt(ballDepth);
            float slack = opponent - contactTime - reserve;
            // A late first man hands the ball to the teammate, who would then drop their own plan.
            if (frame.TeamCount > 1)
                slack = MathF.Min(slack, frame.TeammateEta - contactTime - TeammateReserve);
            return System.Math.Clamp(slack, 0f, MaxSlack);
        }

        /// <summary>A pad with its worth net of the detour it costs (s).</summary>
        private readonly record struct Candidate(Boost Pad, float Net);

        /// <summary>
        /// The pad worth driving to on the way to <paramref name="destination"/>, or null. Pads must
        /// stay goal-side of the ball, be active by arrival, and not be another car's to take.
        /// </summary>
        /// <param name="opponents">Cars that take a pad first when they reach it clearly earlier.</param>
        /// <param name="teammates">Cars whose own trips to a pad this car must not duplicate.</param>
        /// <param name="travelTime">Time (s) for a car to reach a point; defaults to the drive model.</param>
        /// <param name="current">The pad already being driven to, which keeps a hysteresis bonus.</param>
        public static Boost Choose(Car car, IEnumerable<Boost> pads, Vec3 ball, Vec3 destination, int team,
            float slack, IEnumerable<Car> opponents, Func<Car, Vec3, float> travelTime = null,
            Boost current = null, IEnumerable<Car> teammates = null)
        {
            if (pads == null || car == null || car.IsDemolished || !float.IsFinite(slack) || slack <= 0f ||
                !float.IsFinite(car.Boost) || !ControlMath.Finite(destination) || !Wanted(car.Boost))
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

            // The pad already being driven to is always examined, so its hysteresis cannot fall out
            // of a crowded shortlist.
            int currentAt = shortlist.FindIndex(entry => ReferenceEquals(entry.Pad, current));
            if (currentAt >= Shortlist)
                (shortlist[Shortlist - 1], shortlist[currentAt]) = (shortlist[currentAt], shortlist[Shortlist - 1]);

            Candidate best = default;
            float currentNet = float.NegativeInfinity;
            for (int i = 0; i < shortlist.Count && i < Shortlist; i++)
            {
                Boost pad = shortlist[i].Pad;
                float toPad = travelTime(car, pad.Location);
                if (!float.IsFinite(toPad) || toPad < 0f || toPad > MaxSlack + 2f)
                    continue;
                // A pad must be lit by the time the car arrives, which is also when the trip would end.
                if (!pad.IsActive && pad.TimeUntilActive > toPad)
                    continue;
                if (TakenFirst(opponents, pad, toPad, travelTime) || TakenFirst(teammates, pad, toPad, travelTime, car))
                    continue;

                float onward = pad.Location.FlatDist(destination) / ExitSpeed + PadExitTime;
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
            if (float.IsFinite(currentNet) && currentNet >= MinimumNet && !ReferenceEquals(best.Pad, current) &&
                best.Net < currentNet * Continuation)
                return current;
            return best.Net >= MinimumNet ? best.Pad : null;
        }

        /// <summary>
        /// The big pad this car reaches soonest that will be lit when it gets there and that no teammate
        /// who wants boost reaches clearly first, or null. A refuel goes for the tank, not for a pad on
        /// the way, so it is not priced against a detour.
        /// </summary>
        public static Boost SoonestLargePad(Car car, IEnumerable<Boost> pads, IEnumerable<Car> teammates,
            Func<Car, Vec3, float> travelTime = null)
        {
            if (car == null || pads == null || car.IsDemolished)
                return null;
            travelTime ??= (c, target) => Drive.GetEta(c, target);
            Boost best = null;
            float bestEta = float.PositiveInfinity;
            foreach (Boost pad in pads)
            {
                if (pad == null || !pad.IsLarge || !ControlMath.Finite(pad.Location))
                    continue;
                float eta = travelTime(car, pad.Location);
                if (!float.IsFinite(eta) || eta >= bestEta)
                    continue;
                if (!pad.IsActive && pad.TimeUntilActive > eta)
                    continue;
                if (TakenFirst(teammates, pad, eta, travelTime, car))
                    continue;
                best = pad;
                bestEta = eta;
            }
            return best;
        }

        /// <summary>
        /// Whether any of <paramref name="others"/> reaches the pad clearly before the car at
        /// <paramref name="mine"/> seconds. Among teammates (<paramref name="self"/> given) a car that
        /// no longer wants boost is not on its way to the pad, and of two cars within the margin the
        /// lower index takes it, so mirrored cars do not both go for it.
        /// </summary>
        private static bool TakenFirst(IEnumerable<Car> others, Boost pad, float mine,
            Func<Car, Vec3, float> travelTime, Car self = null)
        {
            if (others == null)
                return false;
            foreach (Car other in others)
            {
                if (other == null || other.IsDemolished || !ControlMath.Finite(other.Location) ||
                    (self != null && !Wanted(other.Boost)))
                    continue;
                float theirs = travelTime(other, pad.Location);
                if (!float.IsFinite(theirs))
                    continue;
                bool clearlyFirst = theirs + ContestMargin < mine;
                bool tie = self != null && MathF.Abs(theirs - mine) <= ContestMargin && other.Index < self.Index;
                if (clearlyFirst || tie)
                    return true;
            }
            return false;
        }
    }
}
