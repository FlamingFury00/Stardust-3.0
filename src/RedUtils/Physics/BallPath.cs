using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>Time-indexed view of a ball prediction with interpolation between slices.</summary>
    public sealed class BallPath
    {
        private readonly BallSlice[] slices;

        public BallPath(BallSlice[] slices)
        {
            this.slices = slices ?? Array.Empty<BallSlice>();
        }

        public int Count => slices.Length;
        public BallSlice this[int index] => slices[index];
        public float StartTime => slices.Length > 0 ? slices[0].Time : float.NaN;

        /// <summary>Index of the last slice at or before <paramref name="time"/> (0 when earlier than the path).</summary>
        public int IndexAt(float time)
        {
            int lo = 0, hi = slices.Length - 1;
            if (hi < 0) return -1;
            if (time <= slices[0].Time) return 0;
            if (time >= slices[hi].Time) return hi;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (slices[mid].Time <= time) lo = mid; else hi = mid;
            }
            return lo;
        }

        public Vec3 PositionAt(float time)
        {
            if (slices.Length == 0) return Ball.Location;
            int i = IndexAt(time);
            if (i >= slices.Length - 1 || time <= slices[0].Time) return slices[i].Location;
            BallSlice a = slices[i], b = slices[i + 1];
            float span = b.Time - a.Time;
            float t = span > 1e-6f ? (time - a.Time) / span : 0f;
            return a.Location + (b.Location - a.Location) * t;
        }

        public BallSlice SliceAt(float time)
        {
            if (slices.Length == 0) return new BallSlice(time, Ball.Location, Ball.Velocity);
            int i = IndexAt(time);
            if (i >= slices.Length - 1 || time <= slices[0].Time) return slices[i];
            BallSlice a = slices[i], b = slices[i + 1];
            float span = b.Time - a.Time;
            float t = span > 1e-6f ? System.Math.Clamp((time - a.Time) / span, 0f, 1f) : 0f;
            return new BallSlice(time, a.Location + (b.Location - a.Location) * t,
                a.Velocity + (b.Velocity - a.Velocity) * t, a.AngularVelocity + (b.AngularVelocity - a.AngularVelocity) * t);
        }
    }
}
