using System;
using RedUtils.Math;
using RedUtils.Physics;

namespace RedUtils.Planning
{
    /// <summary>Where and when a struck ball crosses a goal plane.</summary>
    public readonly struct GoalCrossing
    {
        public readonly bool Reaches;      // crosses the goal-line plane before stopping or leaving the field
        public readonly bool OnTarget;     // inside the posts and under the crossbar (with ball radius)
        public readonly Vec3 Point;
        public readonly float Time;
        public readonly float FrameMargin; // clearance of the ball's path from the nearest post/crossbar (negative = outside)

        public GoalCrossing(bool reaches, bool onTarget, Vec3 point, float time, float frameMargin)
        {
            Reaches = reaches;
            OnTarget = onTarget;
            Point = point;
            Time = time;
            FrameMargin = frameMargin;
        }
    }

    /// <summary>
    /// Fast flight model for a ball after a planned touch: gravity, drag, and floor bounces
    /// (restitution 0.6, a little horizontal loss). Walls are ignored, so a crossing is a
    /// direct-shot estimate rather than a full prediction.
    /// </summary>
    public static class BallFlight
    {
        public const float GoalHalfWidth = 892.755f;
        public const float GoalHeight = 642.775f;
        public const float GoalLine = 5120f;

        public static GoalCrossing ToGoal(Vec3 position, Vec3 velocity, int goalSide, float maxTime = 4f, float dt = 1f / 30f)
        {
            Vec3 p = position, v = velocity;
            float t = 0f;
            for (int i = 0; i < 400 && t < maxTime; i++)
            {
                Vec3 previous = p;
                v += new Vec3(0, 0, RL.Gravity * dt);
                v *= 1f - RL.BallDrag * dt;
                p += v * dt;
                if (p.z < RL.BallRestZ && v.z < 0f)
                {
                    p.z = RL.BallRestZ + (RL.BallRestZ - p.z) * RL.BallRestitution;
                    v.z = -v.z * RL.BallRestitution;
                    if (v.z < 60f) v.z = 0f;
                    v.x *= 0.92f;
                    v.y *= 0.92f;
                }
                t += dt;
                if (MathF.Abs(p.x) > 4096f - RL.BallRadius) break;
                float before = previous.y * goalSide, after = p.y * goalSide;
                if (before < GoalLine && after >= GoalLine)
                {
                    float f = (GoalLine - before) / MathF.Max(after - before, 1e-4f);
                    Vec3 point = previous + (p - previous) * f;
                    // Clearance of the ball's path from the posts and crossbar, measured square to
                    // the path: an oblique ball clips a post it would miss head-on.
                    float along = MathF.Abs(v.y);
                    float flat = MathF.Max(MathF.Sqrt(v.x * v.x + v.y * v.y), 1f);
                    float rising = MathF.Max(MathF.Sqrt(v.z * v.z + v.y * v.y), 1f);
                    float lateral = (GoalHalfWidth - MathF.Abs(point.x)) * along / flat - RL.BallRadius;
                    float vertical = (GoalHeight - point.z) * along / rising - RL.BallRadius;
                    float margin = MathF.Min(lateral, vertical);
                    return new GoalCrossing(true, margin > 0f, point, t - dt + f * dt, margin);
                }
                if (v.Flatten().Length() < 150f && p.z <= RL.BallRestZ + 1f) break;
            }
            return new GoalCrossing(false, false, p, t, float.NegativeInfinity);
        }
    }
}
