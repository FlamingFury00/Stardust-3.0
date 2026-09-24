using System;
using RedUtils.Math;
using RedUtils.Physics;

namespace RedUtils.Planning
{
    /// <summary>Collision box of the car being planned for.</summary>
    public readonly struct CarGeometry
    {
        public readonly Vec3 HitboxSize, HitboxOffset;

        public CarGeometry(Vec3 size, Vec3 offset)
        {
            HitboxSize = size;
            HitboxOffset = offset;
        }

        public static CarGeometry Of(Car car) => car.HitboxSize.Length() > 1f
            ? new CarGeometry(car.HitboxSize, car.HitboxOffset)
            : new CarGeometry(CarPose.OctaneHitbox, CarPose.OctaneOffset);

        public float HalfWidth => HitboxSize.y * 0.5f;
        public float FrontReach => HitboxOffset.x + HitboxSize.x * 0.5f;
    }

    /// <summary>Result of solving a planned touch.</summary>
    public readonly struct ContactSolution
    {
        public readonly bool Valid;
        public readonly Vec3 CarPosition;   // car origin at first contact
        public readonly Vec3 Heading;       // car forward at contact
        public readonly float Lateral;      // car offset along its right axis relative to the ball
        public readonly Vec3 BallVelocity;  // predicted ball velocity just after the touch
        public readonly float AimError;     // flat angle between the predicted and desired ball direction

        public ContactSolution(Vec3 carPosition, Vec3 heading, float lateral, Vec3 ballVelocity, float aimError)
        {
            Valid = true;
            CarPosition = carPosition;
            Heading = heading;
            Lateral = lateral;
            BallVelocity = ballVelocity;
            AimError = aimError;
        }
    }

    /// <summary>
    /// Geometry of planned touches: where the car must be when it first touches the ball, and the
    /// lateral offset that sends the ball toward an aim point according to <see cref="HitModel"/>.
    /// </summary>
    public static class Contact
    {
        /// <summary>
        /// Car origin at first contact for a car at height <paramref name="carZ"/> with flat heading
        /// <paramref name="heading"/>, offset sideways by <paramref name="lateral"/>, closing on the ball.
        /// </summary>
        public static bool FirstTouch(Vec3 ball, Vec3 heading, float lateral, float carZ, CarGeometry geometry,
            out Vec3 carPosition)
        {
            heading = new Vec3(heading.x, heading.y, 0).Normalize();
            Vec3 right = new(-heading.y, heading.x, 0);
            Vec3 baseline = new Vec3(ball.x, ball.y, carZ) + right * lateral;

            float Gap(float s)
            {
                var pose = new CarPose(baseline - heading * s, heading, right, Vec3.Up, Vec3.Zero, Vec3.Zero,
                    geometry.HitboxSize, geometry.HitboxOffset);
                return (pose.ClosestPoint(ball) - ball).Length() - RL.BallRadius;
            }

            // The gap to a translating convex box is unimodal along the approach and smallest when
            // the ball is level with the box centre, so the first touch lies between there and far.
            float far = geometry.FrontReach + RL.BallRadius + 60f;
            float closest = geometry.HitboxOffset.x;
            carPosition = baseline - heading * far;
            if (Gap(far) <= 0f || Gap(closest) > 0f) return false;
            float lo = closest, hi = far;
            // A dozen halvings of the ~200 uu bracket locate the touch to a few hundredths of a unit.
            for (int i = 0; i < 12; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Gap(mid) > 0f) hi = mid; else lo = mid;
            }
            carPosition = baseline - heading * hi;
            return true;
        }

        /// <summary>
        /// Solves the lateral offset for a touch at car height <paramref name="carZ"/> with velocity
        /// <paramref name="carVelocity"/> that sends the ball toward <paramref name="aim"/>.
        /// </summary>
        public static ContactSolution Aim(Vec3 ball, Vec3 ballVelocity, Vec3 heading, Vec3 carVelocity, float carZ,
            Vec3 aim, CarGeometry geometry, Vec3? angularVelocity = null, Vec3? up = null)
        {
            heading = new Vec3(heading.x, heading.y, 0).Normalize();
            Vec3 desired = new Vec3(aim.x - ball.x, aim.y - ball.y, 0).Normalize();
            float reach = geometry.HalfWidth + RL.BallRadius * 0.65f;
            Vec3 w = angularVelocity ?? Vec3.Zero;
            Vec3 roof = up ?? Vec3.Up;

            ContactSolution Evaluate(float lateral)
            {
                if (!FirstTouch(ball, heading, lateral, carZ, geometry, out Vec3 position))
                    return default;
                Vec3 right = roof.Cross(heading).Normalize();
                var pose = new CarPose(position, heading, right, roof, carVelocity, w, geometry.HitboxSize, geometry.HitboxOffset);
                HitModel.Result hit = HitModel.Collide(pose, ball, ballVelocity, Vec3.Zero, 4f);
                if (!hit.Contact) return default;
                Vec3 flat = new(hit.Velocity.x, hit.Velocity.y, 0);
                float error = flat.Length() > 1f ? GroundModel.SignedAngle(desired, flat) : MathF.PI;
                return new ContactSolution(position, heading, lateral, hit.Velocity, error);
            }

            // Shifting the car right sends the ball further left, so the aim error is monotonic in
            // the offset over the useful range; bisect it.
            ContactSolution low = Evaluate(-reach), high = Evaluate(reach), centre = Evaluate(0f);
            if (!centre.Valid) return default;
            if (!low.Valid || !high.Valid || MathF.Sign(low.AimError) == MathF.Sign(high.AimError))
            {
                ContactSolution best = centre;
                foreach (ContactSolution c in new[] { low, high })
                    if (c.Valid && MathF.Abs(c.AimError) < MathF.Abs(best.AimError)) best = c;
                return best;
            }
            // Bracketed secant search (Illinois variant): converges in a few evaluations on this
            // smooth, monotonic error while never leaving the bracket.
            ContactSolution a = MathF.Sign(centre.AimError) == MathF.Sign(low.AimError) ? centre : low;
            ContactSolution b = MathF.Sign(centre.AimError) == MathF.Sign(low.AimError) ? high : centre;
            ContactSolution result = MathF.Abs(a.AimError) < MathF.Abs(b.AimError) ? a : b;
            float fa = a.AimError, fb = b.AimError;
            int side = 0;
            for (int i = 0; i < 10 && MathF.Abs(result.AimError) > 0.004f; i++)
            {
                float x = (a.Lateral * fb - b.Lateral * fa) / (fb - fa);
                if (!float.IsFinite(x)) x = 0.5f * (a.Lateral + b.Lateral);
                ContactSolution m = Evaluate(x);
                if (!m.Valid) break;
                if (MathF.Abs(m.AimError) < MathF.Abs(result.AimError)) result = m;
                if (MathF.Sign(m.AimError) == MathF.Sign(fa))
                {
                    a = m;
                    fa = m.AimError;
                    if (side == -1) fb *= 0.5f;
                    side = -1;
                }
                else
                {
                    b = m;
                    fb = m.AimError;
                    if (side == 1) fa *= 0.5f;
                    side = 1;
                }
            }
            return result;
        }
    }
}
