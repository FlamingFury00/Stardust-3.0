namespace Stardust.Simulator;

/// <summary>Bounded execution of long-lived, blocking match sessions.</summary>
public static class MatchWorkers
{
    public static void Run(int games, int parallel, Action<int> play)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(games);
        ArgumentNullException.ThrowIfNull(play);
        int next = -1;
        // Each match waits synchronously on bot sockets for its entire lifetime. Keeping these
        // loops off the pool lets Process output readers and other async I/O callbacks drain even
        // under CPU pressure; otherwise a bot can block on its full stdout pipe before sending input.
        Task[] workers = Enumerable.Range(0, Math.Min(games, Math.Max(1, parallel)))
            .Select(_ => Task.Factory.StartNew(() =>
            {
                int game;
                while ((game = Interlocked.Increment(ref next)) < games) play(game);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(workers);
    }
}
