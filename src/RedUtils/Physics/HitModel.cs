using System;
using RedUtils.Math;

namespace RedUtils.Physics
{
    /// <summary>Oriented car pose used by the contact model.</summary>
    public readonly struct CarPose
    {
        public readonly Vec3 Position, Forward, Right, Up, Velocity, AngularVelocity;
        public readonly Vec3 HitboxSize, HitboxOffset;

        public CarPose(Vec3 position, Vec3 forward, Vec3 right, Vec3 up, Vec3 velocity, Vec3 angularVelocity,
            Vec3 hitboxSize, Vec3 hitboxOffset)
        {
            Position = position;
            Forward = forward;
            Right = right;
            Up = up;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
            HitboxSize = hitboxSize;
            HitboxOffset = hitboxOffset;
        }

        /// <summary>Octane collision box used by RocketSim and the game.</summary>
        public static readonly Vec3 OctaneHitbox = new(120.507f, 86.6994f, 38.6591f);
        public static readonly Vec3 OctaneOffset = new(13.8757f, 0f, 20.755f);

        public static CarPose From(Car car) => new(car.Location, car.Forward, car.Right, car.Up, car.Velocity,
            car.AngularVelocity, car.HitboxSize.Length() > 1 ? car.HitboxSize : OctaneHitbox,
            car.HitboxSize.Length() > 1 ? car.HitboxOffset : OctaneOffset);

        public Vec3 HitboxCenter => Position + Forward * HitboxOffset.x + Right * HitboxOffset.y + Up * HitboxOffset.z;

        public Vec3 Local(Vec3 world) => new(Forward.Dot(world), Right.Dot(world), Up.Dot(world));
        public Vec3 World(Vec3 local) => Forward * local.x + Right * local.y + Up * local.z;

        /// <summary>Closest point of the collision box to <paramref name="point"/>.</summary>
        public Vec3 ClosestPoint(Vec3 point)
        {
            Vec3 center = HitboxCenter;
            Vec3 local = Local(point - center);
            Vec3 half = HitboxSize * 0.5f;
            local = new Vec3(System.Math.Clamp(local.x, -half.x, half.x), System.Math.Clamp(local.y, -half.y, half.y),
                System.Math.Clamp(local.z, -half.z, half.z));
            return center + World(local);
        }
    }

    /// <summary>
    /// Single-impact car/ball collision model: an inelastic impulse with Coulomb friction (car-ball
    /// friction 2.0, restitution 0) followed by Rocket League's extra "power" impulse along the
    /// z-flattened, forward-softened centre line. Validated against RocketSim.
    /// </summary>
    public static class HitModel
    {
        private static readonly float BallInverseInertia = 1f / (0.4f * RL.BallMass * RL.BallRadius * RL.BallRadius);

        public readonly struct Result
        {
            public readonly Vec3 Velocity, AngularVelocity, ContactPoint;
            public readonly bool Contact;

            public Result(Vec3 velocity, Vec3 angularVelocity, Vec3 contactPoint, bool contact)
            {
                Velocity = velocity;
                AngularVelocity = angularVelocity;
                ContactPoint = contactPoint;
                Contact = contact;
            }
        }

        /// <summary>Ball state immediately after contact. Returns the input velocity when the bodies do not touch.</summary>
        public static Result Collide(in CarPose car, Vec3 ballPosition, Vec3 ballVelocity, Vec3 ballAngularVelocity,
            float contactTolerance = 8f)
        {
            Vec3 contact = car.ClosestPoint(ballPosition);
            Vec3 toContact = contact - ballPosition;
            float separation = toContact.Length();
            if (separation > RL.BallRadius + contactTolerance || separation < 1e-3f)
                return new Result(ballVelocity, ballAngularVelocity, contact, false);

            Vec3 normal = toContact / separation;
            Vec3 rBall = contact - ballPosition;
            Vec3 rCar = contact - car.Position;

            // Relative velocity of the car's contact point with respect to the ball's.
            Vec3 carPoint = car.Velocity + car.AngularVelocity.Cross(rCar);
            Vec3 ballPoint = ballVelocity + ballAngularVelocity.Cross(rBall);
            Vec3 relative = carPoint - ballPoint;

            // Effective mass matrix K = (1/mb + 1/mc) I - [rb]x Ib^-1 [rb]x - [rc]x Ic^-1 [rc]x.
            Matrix3 k = Matrix3.Identity * (1f / RL.BallMass + 1f / RL.CarMass);
            Matrix3 skewBall = Matrix3.Skew(rBall);
            k -= skewBall * skewBall * BallInverseInertia;
            Matrix3 skewCar = Matrix3.Skew(rCar);
            Matrix3 inverseInertiaCar = CarInverseInertiaWorld(car);
            k -= skewCar * inverseInertiaCar * skewCar;

            Vec3 impulse = k.Inverse() * relative;
            float normalPart = MathF.Min(impulse.Dot(normal), -1f);
            Vec3 normalImpulse = normal * normalPart;
            Vec3 tangentImpulse = impulse - normalImpulse;
            float tangentMagnitude = MathF.Max(tangentImpulse.Length(), 1e-3f);
            float frictionScale = MathF.Min(1f, RL.CarBallFriction * MathF.Abs(normalPart) / tangentMagnitude);
            impulse = normalImpulse + tangentImpulse * frictionScale;

            Vec3 velocity = ballVelocity + impulse / RL.BallMass;
            Vec3 angular = ballAngularVelocity + rBall.Cross(impulse) * BallInverseInertia;

            velocity += ExtraImpulse(car, ballPosition, ballVelocity);

            float speed = velocity.Length();
            if (speed > RL.BallMaxSpeed) velocity *= RL.BallMaxSpeed / speed;
            float spin = angular.Length();
            if (spin > RL.BallMaxAngularSpeed) angular *= RL.BallMaxAngularSpeed / spin;
            return new Result(velocity, angular, contact, true);
        }

        /// <summary>Rocket League's additional hit velocity, independent of the rigid-body impulse.</summary>
        public static Vec3 ExtraImpulse(in CarPose car, Vec3 ballPosition, Vec3 ballVelocity)
        {
            float relativeSpeed = MathF.Min((ballVelocity - car.Velocity).Length(), RL.BallCarExtraMaxDelta);
            if (relativeSpeed <= 0f)
                return Vec3.Zero;
            Vec3 offset = ballPosition - car.Position;
            Vec3 direction = new Vec3(offset.x, offset.y, offset.z * RL.BallCarExtraZScale).Normalize();
            direction = (direction - car.Forward * (direction.Dot(car.Forward) * (1f - RL.BallCarExtraForwardScale))).Normalize();
            return direction * (relativeSpeed * RL.ExtraImpulseFactor(relativeSpeed));
        }

        private static Matrix3 CarInverseInertiaWorld(in CarPose car)
        {
            // Bullet box inertia for the collision box (full extents), without a parallel-axis term.
            Vec3 s = car.HitboxSize;
            float scale = RL.CarMass / 12f;
            Vec3 inertia = new(scale * (s.y * s.y + s.z * s.z), scale * (s.x * s.x + s.z * s.z), scale * (s.x * s.x + s.y * s.y));
            Matrix3 basis = Matrix3.FromColumns(car.Forward, car.Right, car.Up);
            Matrix3 local = Matrix3.Diagonal(1f / inertia.x, 1f / inertia.y, 1f / inertia.z);
            return basis * local * basis.Transpose();
        }
    }

    /// <summary>Minimal row-major 3x3 matrix for rigid-body algebra.</summary>
    public readonly struct Matrix3
    {
        public readonly float M00, M01, M02, M10, M11, M12, M20, M21, M22;

        public Matrix3(float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static Matrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);
        public static Matrix3 Diagonal(float a, float b, float c) => new(a, 0, 0, 0, b, 0, 0, 0, c);
        public static Matrix3 FromColumns(Vec3 a, Vec3 b, Vec3 c) => new(a.x, b.x, c.x, a.y, b.y, c.y, a.z, b.z, c.z);

        /// <summary>Cross-product matrix: Skew(a) * b == a x b.</summary>
        public static Matrix3 Skew(Vec3 a) => new(0, -a.z, a.y, a.z, 0, -a.x, -a.y, a.x, 0);

        public Matrix3 Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public static Matrix3 operator +(Matrix3 a, Matrix3 b) => new(
            a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02, a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
            a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

        public static Matrix3 operator -(Matrix3 a, Matrix3 b) => new(
            a.M00 - b.M00, a.M01 - b.M01, a.M02 - b.M02, a.M10 - b.M10, a.M11 - b.M11, a.M12 - b.M12,
            a.M20 - b.M20, a.M21 - b.M21, a.M22 - b.M22);

        public static Matrix3 operator *(Matrix3 a, float s) => new(
            a.M00 * s, a.M01 * s, a.M02 * s, a.M10 * s, a.M11 * s, a.M12 * s, a.M20 * s, a.M21 * s, a.M22 * s);

        public static Vec3 operator *(Matrix3 a, Vec3 v) => new(
            a.M00 * v.x + a.M01 * v.y + a.M02 * v.z,
            a.M10 * v.x + a.M11 * v.y + a.M12 * v.z,
            a.M20 * v.x + a.M21 * v.y + a.M22 * v.z);

        public static Matrix3 operator *(Matrix3 a, Matrix3 b) => new(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        public float Determinant =>
            M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

        public Matrix3 Inverse()
        {
            float det = Determinant;
            if (MathF.Abs(det) < 1e-12f) return Identity;
            float inv = 1f / det;
            return new Matrix3(
                (M11 * M22 - M12 * M21) * inv, (M02 * M21 - M01 * M22) * inv, (M01 * M12 - M02 * M11) * inv,
                (M12 * M20 - M10 * M22) * inv, (M00 * M22 - M02 * M20) * inv, (M02 * M10 - M00 * M12) * inv,
                (M10 * M21 - M11 * M20) * inv, (M01 * M20 - M00 * M21) * inv, (M00 * M11 - M01 * M10) * inv);
        }
    }
}
