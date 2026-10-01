using System;
using System.Collections.Generic;
using RedUtils;
using RedUtils.Math;

namespace Bot
{
    public static class RoutePlanner
    {
        /// <summary>Boost at or above which no refill is sought.</summary>
        public static float RefillBelow = 35f;
        /// <summary>Boost below which a big pad is worth a real excursion.</summary>
        public static float FullPadBelow = 20f;
        /// <summary>Longest extra path (uu) a big-pad excursion may add.</summary>
        public static float FullPadDetour = 2600f;
        /// <summary>Time (s) the opponent must still need to reach the ball after the pickup.</summary>
        public static float FullPadWindow = 1.0f;

        /// <summary>Whether shadow and recovery drives pass over pads lying almost on their route.</summary>
        public static bool PassPads = false;
        /// <summary>Boost at or above which pads are no longer passed over.</summary>
        public static float PassBelow = 70f;
        /// <summary>Longest extra path (uu) a pass-over pad may add.</summary>
        public static float PassDetour = 180f;
        /// <summary>Largest turn (rad) off the car's heading toward a pass-over pad.</summary>
        public static float PassTurn = 0.6f;

        public static float Detour(Vec3 start, Vec3 pad, Vec3 destination) =>
            MathF.Max(0, start.FlatDist(pad) + pad.FlatDist(destination) - start.FlatDist(destination));

        /// <summary>
        /// Select a covered refill. Small pads remain strict on-route pickups; a critically low support
        /// car may make a bounded full-pad excursion only when the opponent window covers the pickup.
        /// </summary>
        public static Boost SelectBoost(Car car, IEnumerable<Boost> pads, Vec3 ball, Vec3 destination,
            int team, float opponentEta, Func<Car, Vec3, float> travelTime = null)
        {
            if (pads == null || car.IsDemolished || car.Boost >= RefillBelow || !float.IsFinite(opponentEta) || opponentEta < 1)
                return null;

            travelTime ??= (c, target) => Drive.GetEta(c, target);
            bool critical = car.Boost < FullPadBelow;
            Boost best = null;
            float bestCost = float.PositiveInfinity;

            foreach (Boost pad in pads)
            {
                if (pad == null || !ControlMath.Finite(pad.Location)) continue;
                // Defensive/support refills stay goal-side of the ball.
                if (pad.Location.y * Field.Side(team) < ball.y * Field.Side(team)) continue;

                float eta = travelTime(car, pad.Location);
                if (!float.IsFinite(eta) || eta < 0) continue;
                // A pad which respawns before arrival is a valid pickup; do not require it active now.
                if (!pad.IsActive && pad.TimeUntilActive > eta + 0.12f) continue;

                float detour = Detour(car.Location, pad.Location, destination);
                bool deliberateFull = pad.IsLarge && critical && detour <= FullPadDetour &&
                    opponentEta >= eta + FullPadWindow;

                if (!deliberateFull)
                {
                    if (detour > 450 || eta + 0.5f > opponentEta) continue;
                }

                float usefulBoost = MathF.Min(100 - car.Boost, pad.IsLarge ? 100 : 12);
                float value = (pad.IsLarge ? 9f : 5f) * usefulBoost;
                float cost = detour + 120 * eta - value;
                if (cost < bestCost)
                {
                    best = pad;
                    bestCost = cost;
                }
            }
            return best;
        }

        /// <summary>
        /// The nearest pad ahead of a grounded car that lies almost on its straight route to
        /// <paramref name="destination"/> and is active when the car gets there: driving over it costs
        /// under a tenth of a second, where a pro takes every such pad on the way back.
        /// </summary>
        public static Boost PassPad(Car car, IEnumerable<Boost> pads, Vec3 destination)
        {
            if (pads == null || car == null || car.IsDemolished || !car.IsGrounded ||
                car.Boost >= PassBelow || !ControlMath.Finite(destination))
                return null;

            float routeLength = car.Location.FlatDist(destination);
            Vec3 velocity = car.Velocity.Flatten();
            Vec3 heading = velocity.Length() > 500f ? velocity : car.Forward.Flatten();
            float speed = MathF.Max(velocity.Length(), 1000f);
            Boost best = null;
            float bestDistance = float.PositiveInfinity;

            foreach (Boost pad in pads)
            {
                if (pad == null || !ControlMath.Finite(pad.Location)) continue;
                Vec3 toPad = (pad.Location - car.Location).Flatten();
                float distance = toPad.Length();
                // Close enough to be collected already, or at/after the end of the route.
                if (distance < 100f || distance > routeLength - 150f) continue;
                if (!pad.IsActive && pad.TimeUntilActive > distance / speed) continue;
                if (heading.Angle(toPad) > PassTurn) continue;
                if (Detour(car.Location, pad.Location, destination) > PassDetour) continue;

                if (distance < bestDistance)
                {
                    best = pad;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }
}
