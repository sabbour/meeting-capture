namespace MeetingCapture.Pipeline;

public static class ShutdownCleanup
{
    // Begin the deadline before invoking cleanup: even DisposeAsync can block before returning a task.
    public static Task RunAsync(Func<Task> cleanup, TimeSpan timeout)
        => Task.Run(cleanup).WaitAsync(timeout);
}
