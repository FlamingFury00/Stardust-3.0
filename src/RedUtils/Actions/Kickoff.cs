using System;
using RedUtils.Math;

namespace RedUtils
{
    /// <summary>A kickoff action, which performs a speedflip kickoff.</summary>
    public class Kickoff : IAction
    {
        /// <summary>
        /// The speedflip approach is physically committed, but the final contact dodge becomes
        /// interruptible. Any ball touch ends kickoff state, so a stale post-contact dodge must not
        /// continue owning the car after the 50/50 has already been decided.
        /// </summary>
        public bool Interruptible => _finalDodge != null;

        /// <summary>Whether or not the kickoff period has ended.</summary>
        public bool Finished { get; set; }

        /// <summary>Whether or not this kickoff is diagonal.</summary>
        private bool _isDiagonal;
        /// <summary>How much time the car has spent on the ground after the speedflip.</summary>
        private float _timeOnGround;
        /// <summary>Whether or not we have speedflipped.</summary>
        private bool _speedFlipped;
        /// <summary>The speedflip sub action.</summary>
        private SpeedFlip _speedFlip;
        /// <summary>
        /// Final contact dodge. Keep it inside Kickoff rather than replacing bot.Action so the
        /// first ball touch can cancel the kickoff immediately.
        /// </summary>
        private Dodge _finalDodge;

        public bool FinalDodgeActive => _finalDodge != null && !_finalDodge.Finished;

        /// <summary>Initializes a new kickoff action.</summary>
        public Kickoff()
        {
            Finished = false;
        }

        /// <summary>Performs this kickoff action.</summary>
        public void Run(RUBot bot)
        {
            // The match leaves kickoff state on the first ball touch. Never keep a speedflip/final
            // dodge alive into active play: fresh telemetry showed the opponent touching first,
            // then Stardust's stale non-interruptible dodge adding a second touch that launched the
            // ball high into its own half and carried the car upfield.
            if (!bot.IsKickoff)
            {
                Finished = true;
                return;
            }

            if (_finalDodge != null)
            {
                _finalDodge.Run(bot);
                if (_finalDodge.Finished)
                {
                    _finalDodge = null;
                    _timeOnGround = 0f;
                }
                return;
            }

            if (bot.Me.Velocity.Length() < 200)
                _isDiagonal = MathF.Abs(bot.Me.Location.x) > 1000;

            if (_speedFlipped && bot.Me.IsGrounded)
                _timeOnGround += bot.DeltaTime;

            if (_speedFlip != null && !_speedFlip.Finished)
            {
                bot.Controller.Boost = true;
                _speedFlip.Run(bot);
                return;
            }

            bot.Throttle(Car.MaxSpeed);

            // Aim at a point slightly offset from the ball so the approach still uses the existing
            // kickoff 50/50 geometry. The lifecycle change above is deliberately independent of
            // spawn/timing tuning.
            if (!_isDiagonal || _speedFlipped)
            {
                bot.AimAt(Ball.Location - Ball.Location.Direction(bot.TheirGoal.Location) *
                    (!_speedFlipped ? 2600 : 170));
                bot.Controller.Steer *= (!_isDiagonal && !_speedFlipped ? 0.4f : 1f);
            }
            else if (bot.Me.Velocity.Length() > 500)
            {
                bot.AimAt(Ball.Location);
            }

            if (bot.Me.Velocity.Length() >
                    (_isDiagonal ? 600 : 700 + MathF.Abs(bot.Me.Location.x) * 3) &&
                !_speedFlipped)
            {
                _speedFlipped = true;
                _speedFlip = new SpeedFlip(
                    bot.Me.Location.FlatDirection(
                        Ball.Location - Ball.Location.Direction(bot.TheirGoal.Location) *
                        (_isDiagonal ? 250 : -1000)));
            }
            else if (bot.Me.Location.Dist(Ball.Location) < 800 && _timeOnGround > 0.1f)
            {
                _finalDodge = new Dodge(
                    Ball.Location.Direction(bot.TheirGoal.Location), 0.18f);
                _finalDodge.Run(bot);
            }
        }
    }
}
