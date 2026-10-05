using System.Runtime.CompilerServices;

namespace MeetingCapture.Regression;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = CoreChecks.Cases().Concat(DetectionChecks.Cases()).Concat(PipelineChecks.Cases())
            .Concat(UiStateChecks.Cases()).Concat(MeetingTitleChecks.Cases()).Concat(SignInChecks.Cases())
            .Concat(WorkIqChecks.Cases()).Concat(WorkIqAuthSnapshotChecks.Cases())
            .Concat(CallSignalChecks.Cases()).Concat(ShutdownChecks.Cases())
            .Concat(AudioMixingChecks.Cases()).Concat(RelatedContextChecks.Cases())
            .Concat(VisualizationChecks.Cases()).Concat(AiUsageChecks.Cases())
            .Concat(AiDiagnosticsChecks.Cases()).Concat(StartupFailureChecks.Cases()).ToArray();
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run().WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"PASS {name}");
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine($"FAIL {name}: deadlock watchdog expired; stopping to avoid overlapping tests.");
                return 1;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {ex}");
            }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} offline behavioral checks passed.");
        Console.WriteLine("NOT RUN: WinUI, WASAPI/Speech SDK, Graph/Copilot service integration, hardware performance/memory gates.");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Check
{
    public static void True([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value,
        [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value) throw new InvalidOperationException($"Assertion failed: {expression}");
    }

    public static void Equal<T>(T expected, T actual,
        [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Assertion failed: {expression}; expected {expected}, actual {actual}");
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "MeetingCapture.Regression-" + Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class AsyncGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task;
    public async Task WaitAsync(CancellationToken ct)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ct);
    }
    public void Release() => _release.TrySetResult();
}
