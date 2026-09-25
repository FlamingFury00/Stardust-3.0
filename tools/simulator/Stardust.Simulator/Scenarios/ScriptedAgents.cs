using RLBot.Flat;
using Stardust.Simulator.Match;

namespace Stardust.Simulator.Scenarios;

/// <summary>Small vector helpers over the flatbuffer object types used by scripted agents.</summary>
internal static class Flat
{
    public static (float X, float Y, float Z) V(Vector3T v) => (v.X, v.Y, v.Z);

    public static (float X, float Y, float Z) Forward(RotatorT r)
    {
        float cp = MathF.Cos(r.Pitch);
        return (cp * MathF.Cos(r.Yaw), cp * MathF.Sin(r.Yaw), MathF.Sin(r.Pitch));
    }

    /// <summary>Steering toward a flat target with a proportional heading controller.</summary>
    public static float SteerToward(PlayerInfoT car, float tx, float ty)
    {
        var (fx, fy, _) = Forward(car.Physics.Rotation);
        float dx = tx - car.Physics.Location.X, dy = ty - car.Physics.Location.Y;
        float angle = MathF.Atan2(fx * dy - fy * dx, fx * dx + fy * dy);
        return Math.Clamp(angle * 3.5f, -1f, 1f);
    }

    public static float ForwardSpeed(PlayerInfoT car)
    {
        var (fx, fy, fz) = Forward(car.Physics.Rotation);
        var v = car.Physics.Velocity;
        return v.X * fx + v.Y * fy + v.Z * fz;
    }
}

/// <summary>
/// A disciplined, deterministic goalkeeper: it holds the goal line, shuffles to the predicted
/// crossing point, faces upfield, and jumps (or double jumps) at shots within reach. It never
/// leaves its box, which makes shot-quality fixtures repeatable.
/// </summary>
public sealed class GoalieAgent : ScriptedAgent
{
    private float jumpStarted = float.NaN;
    public override string Description => "goalie";

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        PlayerInfoT car = packet.Players[self.Index];
        float side = self.Team == 0 ? -1 : 1;
        float goalY = side * 5120;
        float now = packet.MatchInfo.SecondsElapsed;

        // Predicted crossing of our goal line within 3 s, else mirror the ball.
        float targetX = Math.Clamp(packet.Balls[0].Physics.Location.X * 0.35f, -700, 700);
        float crossingZ = 0, crossingTime = float.PositiveInfinity;
        foreach (PredictionSliceT slice in prediction.Slices)
        {
            if (slice.GameSeconds - now > 3f) break;
            if (slice.Physics.Location.Y * side > 5000)
            {
                targetX = Math.Clamp(slice.Physics.Location.X, -800, 800);
                crossingZ = slice.Physics.Location.Z;
                crossingTime = slice.GameSeconds - now;
                break;
            }
        }

        var controls = new ControllerStateT();
        float carX = car.Physics.Location.X, carY = car.Physics.Location.Y;
        bool onGround = car.AirState == AirState.OnGround;

        if (float.IsFinite(jumpStarted))
        {
            float t = now - jumpStarted;
            controls.Jump = t < 0.2f || (crossingZ > 300 && t > 0.24f && t < 0.3f);
            if (onGround && t > 0.4f) jumpStarted = float.NaN;
            return controls;
        }

        float lineY = goalY - side * 120;
        float dx = targetX - carX;
        // Stay square to the goal: drive along x, backing up or forward as needed.
        float along = MathF.Abs(dx);
        float depthError = lineY - carY;
        if (MathF.Abs(depthError) > 250 || along > 1200)
        {
            controls.Steer = Flat.SteerToward(car, targetX, lineY);
            controls.Throttle = 1;
        }
        else
        {
            // Face across the goal line and shuffle.
            float facing = Flat.Forward(car.Physics.Rotation).X;
            float dir = MathF.Sign(dx) * MathF.Sign(facing == 0 ? 1 : facing);
            controls.Throttle = Math.Clamp(dx / 300f * MathF.Sign(facing == 0 ? 1 : facing), -1, 1);
            controls.Steer = Math.Clamp(-Flat.Forward(car.Physics.Rotation).Y * 2f * dir, -1, 1);
        }

        if (onGround && crossingTime < 0.55f && MathF.Abs(targetX - carX) < 450 && crossingZ > 130)
        {
            jumpStarted = now;
            controls.Jump = true;
        }
        return controls;
    }
}

/// <summary>
/// An opponent that races to where the ball first comes down within reach and hits it there:
/// it boosts to the predicted landing point and jumps at a low bounce. It makes a lofted ball
/// contested, so the bot must win it in the air rather than wait for the bounce.
/// </summary>
public sealed class ChaserAgent : ScriptedAgent
{
    private float jumpStarted = float.NaN;
    public override string Description => "chaser";

    protected override ControllerStateT Act(Participant self, GamePacketT packet, BallPredictionT prediction)
    {
        PlayerInfoT car = packet.Players[self.Index];
        float now = packet.MatchInfo.SecondsElapsed;
        var controls = new ControllerStateT();
        if (float.IsFinite(jumpStarted))
        {
            controls.Jump = now - jumpStarted < 0.2f;
            if (car.AirState == AirState.OnGround && now - jumpStarted > 0.4f) jumpStarted = float.NaN;
            return controls;
        }

        var ball = packet.Balls[0].Physics.Location;
        float tx = ball.X, ty = ball.Y, tz = ball.Z;
        foreach (PredictionSliceT slice in prediction.Slices)
        {
            if (slice.Physics.Location.Z < 180)
            {
                tx = slice.Physics.Location.X;
                ty = slice.Physics.Location.Y;
                tz = slice.Physics.Location.Z;
                break;
            }
        }
        controls.Steer = Flat.SteerToward(car, tx, ty);
        controls.Throttle = 1;
        controls.Boost = MathF.Abs(controls.Steer) < 0.3f;
        float dx = ball.X - car.Physics.Location.X, dy = ball.Y - car.Physics.Location.Y;
        if (car.AirState == AirState.OnGround && dx * dx + dy * dy < 350 * 350 && ball.Z > 110 && ball.Z < 260)
        {
            jumpStarted = now;
            controls.Jump = true;
        }
        return controls;
    }
}
