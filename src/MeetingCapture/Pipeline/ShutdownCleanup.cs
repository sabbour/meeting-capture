namespace MeetingCapture.Pipeline;

/// <summary>Runs cleanup work with a time limit.</summary>
public static class ShutdownCleanup
{
    // Begin the deadline before invoking cleanup: even DisposeAsync can block before returning a task.
    /// <summary>Runs <paramref name="cleanup"/> on a worker thread and throws <see cref="TimeoutException"/> if it takes longer than <paramref name="timeout"/>.</summary>
    public static Task RunAsync(Func<Task> cleanup, TimeSpan timeout)
        => Task.Run(cleanup).WaitAsync(timeout);
}
