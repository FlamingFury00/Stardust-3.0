using System.Diagnostics;

namespace Stardust.Simulator.Protocol;

/// <summary>
/// Drains both child pipes independently of the thread pool. On Windows redirected pipes can use
/// blocking reads beneath async APIs; an empty stderr pipe must not starve a full stdout pipe.
/// </summary>
public sealed class ProcessOutputDrain(Process process, TextWriter? log)
{
    private readonly object gate = new();
    private Task[] readers = [];
    private volatile bool stopped;
    public Exception? LogError { get; private set; }

    public void Start()
    {
        if (readers.Length != 0) throw new InvalidOperationException("Output drainage already started.");
        readers = [Read(process.StandardOutput, ""), Read(process.StandardError, "ERR ")];
    }

    private Task Read(StreamReader source, string prefix) => Task.Factory.StartNew(() =>
    {
        try
        {
            string? line;
            while (!stopped && (line = source.ReadLine()) != null)
            {
                lock (gate)
                {
                    if (stopped || LogError != null) continue;
                    try { log?.WriteLine(prefix + line); }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
                    {
                        // A full disk or failed sink must not stop draining and block the bot's controls.
                        LogError = error;
                        Console.Error.WriteLine($"Bot output log failed; continuing to drain its pipes: {error.Message}");
                    }
                }
            }
        }
        catch (Exception error) when (stopped && error is IOException or ObjectDisposedException) { }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Join only after the process has exited or been killed; the sink stays open until then.</summary>
    public bool Complete(int timeoutMilliseconds = 3000)
    {
        if (Task.WaitAll(readers, timeoutMilliseconds)) return true;
        // A wrapper can exit while a surviving child retains its pipe handles. Detach the sink
        // under the same lock as writes, so cleanup can close it without waiting forever for EOF.
        lock (gate) stopped = true;
        Console.Error.WriteLine("Bot output drainage timed out after process exit; remaining output was detached.");
        return false;
    }
}
