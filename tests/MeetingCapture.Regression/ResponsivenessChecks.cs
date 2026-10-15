using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MeetingCapture.Diagnostics;

namespace MeetingCapture.Regression;

internal static class ResponsivenessChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("UI probe measures five-second stalls with one pending callback", StallAndRecovery);
        yield return ("UI probe uses response time instead of late monitor time", LateMonitor);
        yield return ("UI probe rejects failed delivery without claiming a stall", RejectedProbe);
        yield return ("UI probe distinguishes a blocked post call from a blocked UI callback", BlockedPost);
        yield return ("Responsiveness logs persist only allowed metadata and nested phases", MetadataAndPhases);
        yield return ("Responsiveness monitor persists a stall while the dispatcher is blocked", PersistentHeartbeat);
        yield return ("Responsiveness monitoring and persistence continue without thread-pool progress", IndependentWorkers);
        yield return ("Responsiveness log queue and shutdown stay bounded during writer failure", BoundedWriterFailure);
        yield return ("Responsiveness log writer reports and recovers from file errors", WriterRecovery);
        yield return ("Responsiveness log retention keeps three files of at most one MiB", BoundedRetention);
        yield return ("Responsiveness logs start a valid file after an interrupted previous launch", InterruptedLaunch);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private long _utcOffset;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow()
            => new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)
                .AddTicks(GetTimestamp() + Interlocked.Read(ref _utcOffset));
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
        internal void ShiftUtc(TimeSpan elapsed) => Interlocked.Add(ref _utcOffset, elapsed.Ticks);
    }

    private static Task StallAndRecovery()
    {
        var clock = new Clock();
        var pending = new Queue<Action>();
        var events = new List<UiHeartbeat.Observation>();
        var heartbeat = new UiHeartbeat(clock, action => { pending.Enqueue(action); return true; }, events.Add);
        Poll(heartbeat);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Poll(heartbeat);
        Check.Equal(0, events.Count);
        Check.Equal(1, pending.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Poll(heartbeat);
        Check.Equal(ResponsivenessEventKind.UiStallStarted, events.Single().Kind);
        Check.Equal(TimeSpan.FromSeconds(5), events.Single().Delay);

        clock.Advance(TimeSpan.FromSeconds(10));
        Poll(heartbeat);
        Check.Equal(ResponsivenessEventKind.UiStallOngoing, events[^1].Kind);
        Check.Equal(1, pending.Count);
        clock.Advance(TimeSpan.FromSeconds(2));
        pending.Dequeue()();
        var acknowledgedAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(8));
        Poll(heartbeat);
        var recovery = events.Single(item => item.Kind == ResponsivenessEventKind.UiRecovered);
        Check.Equal(TimeSpan.FromSeconds(17), recovery.Delay);
        Check.Equal(acknowledgedAt, recovery.LastUiResponseUtc);
        Check.Equal(1, events.Count(item => item.Kind == ResponsivenessEventKind.UiStallStarted));
        Check.Equal(1, pending.Count);
        return Task.CompletedTask;
    }

    private static Task LateMonitor()
    {
        var clock = new Clock();
        var pending = new Queue<Action>();
        var events = new List<UiHeartbeat.Observation>();
        var heartbeat = new UiHeartbeat(clock, action => { pending.Enqueue(action); return true; }, events.Add);
        Poll(heartbeat);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        pending.Dequeue()();
        clock.Advance(TimeSpan.FromSeconds(12));
        Poll(heartbeat);
        Check.Equal(0, events.Count);
        clock.ShiftUtc(TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromSeconds(1));
        Poll(heartbeat);
        Check.Equal(0, events.Count);
        Check.Equal(1, pending.Count);
        pending.Dequeue()();
        clock.Advance(TimeSpan.FromSeconds(20));
        Poll(heartbeat);
        Check.Equal(ResponsivenessEventKind.UiCheckpoint, events.Single().Kind);

        var slowClock = new Clock();
        Action? slowResponse = null;
        var slowEvents = new List<UiHeartbeat.Observation>();
        var slow = new UiHeartbeat(slowClock, action => { slowResponse = action; return true; }, slowEvents.Add);
        Poll(slow);
        slowClock.Advance(TimeSpan.FromSeconds(6));
        slowResponse!();
        slowClock.Advance(TimeSpan.FromSeconds(4));
        Poll(slow);
        Check.True(slowEvents.Select(item => item.Kind).SequenceEqual(
            [ResponsivenessEventKind.UiStallStarted, ResponsivenessEventKind.UiRecovered]));
        Check.True(slowEvents.All(item => item.Delay == TimeSpan.FromSeconds(6)));
        return Task.CompletedTask;
    }

    private static Task RejectedProbe()
    {
        var clock = new Clock();
        var events = new List<UiHeartbeat.Observation>();
        var heartbeat = new UiHeartbeat(clock,
            _ => throw new ObjectDisposedException("private exception message"), events.Add);
        Poll(heartbeat);
        clock.Advance(TimeSpan.FromSeconds(20));
        Poll(heartbeat);
        Check.Equal(ResponsivenessEventKind.UiProbeRejected, events.Single().Kind);
        Check.Equal(nameof(ObjectDisposedException), events.Single().ExceptionType);
        Check.Equal(UiProbePostState.Rejected, events.Single().ProbePostState);
        Check.True(events.All(item => item.Kind != ResponsivenessEventKind.UiStallStarted));

        var accepted = true;
        var rejectedEvents = new List<UiHeartbeat.Observation>();
        var rejectsLater = new UiHeartbeat(clock, action =>
        {
            if (!accepted) return false;
            action();
            return true;
        }, rejectedEvents.Add);
        Poll(rejectsLater);
        accepted = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        Poll(rejectsLater);
        clock.Advance(TimeSpan.FromSeconds(40));
        Poll(rejectsLater);
        Check.Equal(ResponsivenessEventKind.UiProbeRejected, rejectedEvents.Single().Kind);
        return Task.CompletedTask;
    }

    private static async Task BlockedPost()
    {
        var clock = new Clock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new List<UiHeartbeat.Observation>();
        var posts = 0;
        var heartbeat = new UiHeartbeat(clock, action =>
        {
            Interlocked.Increment(ref posts);
            entered.Set();
            release.Wait();
            action();
            return true;
        }, events.Add);
        Check.True(heartbeat.Tick());
        var posting = Task.Factory.StartNew(heartbeat.PostPending, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Check.True(entered.Wait(TimeSpan.FromSeconds(2)));
            clock.Advance(TimeSpan.FromMilliseconds(4999));
            Check.True(!heartbeat.Tick());
            Check.Equal(0, events.Count);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            Check.True(!heartbeat.Tick());
            var delayed = events.Single();
            Check.Equal(ResponsivenessEventKind.UiProbePostDelayed, delayed.Kind);
            Check.Equal(TimeSpan.FromSeconds(5), delayed.Delay);
            Check.Equal(UiProbePostState.Posting, delayed.ProbePostState);
            Check.Equal(5000d, delayed.ProbePostDurationMs);
            Check.True(delayed.ProbePostCompletedUtc is null);
            clock.Advance(TimeSpan.FromSeconds(10));
            Check.True(!heartbeat.Tick());
            Check.Equal(2, events.Count);
            Check.Equal(1, Volatile.Read(ref posts));
            Check.True(events.All(item => item.Kind != ResponsivenessEventKind.UiStallStarted));
        }
        finally
        {
            release.Set();
            await posting;
        }
        Check.True(heartbeat.Tick());
        var resumed = events.Single(item => item.Kind == ResponsivenessEventKind.UiProbePostResumed);
        Check.Equal(UiProbePostState.Accepted, resumed.ProbePostState);
        Check.Equal(clock.GetUtcNow(), resumed.LastUiResponseUtc);
        Check.Equal(15000d, resumed.ProbePostDurationMs);
        Check.True(resumed.ProbePostCompletedUtc.HasValue);
        Check.Equal(1, Volatile.Read(ref posts));
        clock.Advance(TimeSpan.FromSeconds(5));
        Check.True(!heartbeat.Tick());
        var unscheduled = events.Single(item => item.ProbePostState == UiProbePostState.Pending);
        Check.Equal(ResponsivenessEventKind.UiProbePostDelayed, unscheduled.Kind);
        Check.Equal(TimeSpan.FromSeconds(5), unscheduled.Delay);
        Check.True(unscheduled.ProbePostStartedUtc is null && unscheduled.ProbePostDurationMs is null);
        Check.Equal(5000d, unscheduled.MonitorIntervalMs);
        heartbeat.PostPending();
        Check.True(heartbeat.Tick());
        Check.Equal(2, Volatile.Read(ref posts));
        Check.Equal(2, events.Count(item => item.Kind == ResponsivenessEventKind.UiProbePostResumed));
    }

    private static void Poll(UiHeartbeat heartbeat)
    {
        if (heartbeat.Tick()) heartbeat.PostPending();
    }

    private static async Task MetadataAndPhases()
    {
        const string privateContent = "private transcript, account, token, and recording path";
        using var temp = new TemporaryDirectory();
        var clock = new Clock();
        using var diagnostics = new ResponsivenessDiagnostics(42, temp.Path, clock);
        diagnostics.SetWindowState(visible: true, active: false);
        diagnostics.SetWorkIqState(authenticated: false, busy: true, calendarBusy: false);
        diagnostics.SetCaptureState(true);
        using (diagnostics.BeginUiPhase(ResponsivenessPhase.Startup))
        {
            using (diagnostics.BeginBackgroundPhase(ResponsivenessPhase.WorkIqRestore))
            {
                clock.Advance(TimeSpan.FromMilliseconds(5));
                using (diagnostics.BeginUiPhase(ResponsivenessPhase.WindowPosition))
                    clock.Advance(TimeSpan.FromMilliseconds(4));
                clock.Advance(TimeSpan.FromMilliseconds(6));
            }
            using var check = diagnostics.BeginUiPhase(ResponsivenessPhase.NotesDirectoryCheck);
            check.Fail(new IOException(privateContent));
        }
        diagnostics.Dispose();
        await diagnostics.Completion;
        var text = File.ReadAllText(Path.Combine(temp.Path, ResponsivenessDiagnostics.FileName));
        Check.True(!text.Contains(privateContent, StringComparison.Ordinal));
        Check.True(!text.Contains(temp.Path, StringComparison.Ordinal));
        var events = ReadEntries(temp.Path);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "sequence", "utc", "elapsedMs", "kind", "processId", "uiThreadId",
            "buildVersion", "buildId", "phase", "operationId", "durationMs", "exceptionType",
            "activeUiPhase", "activeUiPhaseElapsedMs", "lastCompletedUiPhase",
            "lastCompletedBackgroundPhase", "windowVisible", "windowActive", "workIqAuthenticated",
            "workIqBusy", "calendarBusy", "captureActive", "probeSentUtc", "lastUiResponseUtc",
            "uiResponseDelayMs", "probePostState", "probePostStartedUtc",
            "probePostCompletedUtc", "probePostDurationMs", "monitorIntervalMs", "droppedEvents"
        };
        Check.True(events.All(item => item.EnumerateObject().All(property => allowed.Contains(property.Name))));
        Check.True(events.All(item => item.GetProperty("schemaVersion").GetInt32() == 1 &&
            item.GetProperty("processId").GetInt32() == Environment.ProcessId &&
            item.GetProperty("uiThreadId").GetUInt32() == 42 &&
            item.GetProperty("buildVersion").GetString() ==
                typeof(ResponsivenessDiagnostics).Assembly.GetName().Version?.ToString() &&
            item.GetProperty("buildId").GetGuid() == typeof(ResponsivenessDiagnostics).Module.ModuleVersionId));
        var childEnd = events.Single(item => Kind(item) == "PhaseEnd" && Phase(item) == "WindowPosition");
        Check.Equal(4d, childEnd.GetProperty("durationMs").GetDouble());
        Check.Equal("Startup", childEnd.GetProperty("activeUiPhase").GetString());
        Check.True(childEnd.GetProperty("windowVisible").GetBoolean());
        Check.True(!childEnd.GetProperty("windowActive").GetBoolean());
        Check.True(childEnd.GetProperty("workIqBusy").GetBoolean());
        var backgroundEnd = events.Single(item => Kind(item) == "PhaseEnd" && Phase(item) == "WorkIqRestore");
        Check.Equal("Startup", backgroundEnd.GetProperty("activeUiPhase").GetString());
        Check.Equal("WorkIqRestore", backgroundEnd.GetProperty("lastCompletedBackgroundPhase").GetString());
        Check.Equal(nameof(IOException), events.Single(item => Kind(item) == "PhaseFailure")
            .GetProperty("exceptionType").GetString());
        Check.Equal(15d, events.Single(item => Kind(item) == "PhaseEnd" && Phase(item) == "Startup")
            .GetProperty("durationMs").GetDouble());
        Check.Equal("ProcessStopping", Kind(events[^1]));
        Check.Equal(0L, diagnostics.DroppedEvents);
    }

    private static async Task PersistentHeartbeat()
    {
        using var temp = new TemporaryDirectory();
        using var diagnostics = new ResponsivenessDiagnostics(42, temp.Path);
        using var phase = diagnostics.BeginUiPhase(ResponsivenessPhase.WindowShow);
        var pending = new ConcurrentQueue<Action>();
        var posts = 0;
        diagnostics.StartMonitor(action =>
        {
            pending.Enqueue(action);
            Interlocked.Increment(ref posts);
            return true;
        });
        await Until(() => ReadEntries(temp.Path).Any(item => Kind(item) == "UiStallStarted"));
        Check.Equal(1, Volatile.Read(ref posts));
        Check.Equal(1, pending.Count);
        Check.True(pending.TryDequeue(out var response));
        response();
        await Until(() => ReadEntries(temp.Path).Any(item => Kind(item) == "UiRecovered"));
        diagnostics.Dispose();
        await diagnostics.Completion;
        var events = ReadEntries(temp.Path);
        var stall = events.Single(item => Kind(item) == "UiStallStarted");
        Check.True(stall.GetProperty("uiResponseDelayMs").GetDouble() >= 5000);
        Check.Equal("WindowShow", stall.GetProperty("activeUiPhase").GetString());
        var recovered = events.Single(item => Kind(item) == "UiRecovered");
        Check.True(recovered.GetProperty("uiResponseDelayMs").GetDouble() >= 5000);
        Check.True(recovered.GetProperty("lastUiResponseUtc").GetDateTimeOffset() >=
            stall.GetProperty("probeSentUtc").GetDateTimeOffset().AddSeconds(5));
        Check.True(pending.Count <= 1);
    }

    private static async Task BoundedWriterFailure()
    {
        using var temp = new TemporaryDirectory();
        var blocked = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(blocked, "test-owned file");
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var diagnostics = new ResponsivenessDiagnostics(42, blocked, reportFailure: type =>
        {
            failure.TrySetResult(type);
            release.Wait();
        });
        try
        {
            Check.Equal(nameof(IOException), await failure.Task);
            var posts = 0;
            diagnostics.StartMonitor(action =>
            {
                action();
                Interlocked.Increment(ref posts);
                return true;
            });
            var enqueueTime = Stopwatch.StartNew();
            for (var index = 0; index < 2000; index++)
                using (diagnostics.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCalendar)) { }
            Check.True(enqueueTime.Elapsed < TimeSpan.FromSeconds(1));
            Check.Equal(ResponsivenessDiagnostics.QueueCapacity, diagnostics.PendingEvents);
            Check.True(diagnostics.DroppedEvents > 0);
            await Until(() => Volatile.Read(ref posts) >= 2);
            var stopTime = Stopwatch.StartNew();
            diagnostics.Dispose();
            Check.True(stopTime.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            diagnostics.Dispose();
            release.Set();
            await diagnostics.Completion;
        }
    }

    private static Task IndependentWorkers()
        => Task.Factory.StartNew(() =>
        {
            ThreadPool.GetMinThreads(out var previousMin, out var minIo);
            ThreadPool.GetMaxThreads(out var previousMax, out var maxIo);
            using var poolEntered = new ManualResetEventSlim();
            using var poolRelease = new ManualResetEventSlim();
            using var poolProgress = new ManualResetEventSlim();
            using var postEntered = new ManualResetEventSlim();
            using var firstRelease = new ManualResetEventSlim();
            using var secondEntered = new ManualResetEventSlim();
            using var secondRelease = new ManualResetEventSlim();
            using var temp = new TemporaryDirectory();
            ResponsivenessDiagnostics? diagnostics = null;
            var progressQueued = false;
            try
            {
                Check.True(ThreadPool.SetMinThreads(1, minIo));
                Check.True(ThreadPool.SetMaxThreads(1, maxIo));
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    poolEntered.Set();
                    poolRelease.Wait();
                });
                Check.True(poolEntered.Wait(TimeSpan.FromSeconds(2)));
                ThreadPool.QueueUserWorkItem(_ => poolProgress.Set());
                progressQueued = true;
                diagnostics = new ResponsivenessDiagnostics(42, temp.Path);
                var posts = 0;
                diagnostics.StartMonitor(action =>
                {
                    if (Interlocked.Increment(ref posts) == 1)
                    {
                        postEntered.Set();
                        firstRelease.Wait();
                        action();
                    }
                    else
                    {
                        secondEntered.Set();
                        secondRelease.Wait();
                    }
                    return true;
                });
                Check.True(postEntered.Wait(TimeSpan.FromSeconds(2)));
                UntilWithoutPool(() => ReadEntries(temp.Path)
                    .Any(item => Kind(item) == "UiProbePostDelayed"));
                var delayed = ReadEntries(temp.Path).Single(item => Kind(item) == "UiProbePostDelayed");
                Check.Equal("Posting", delayed.GetProperty("probePostState").GetString());
                Check.True(delayed.GetProperty("uiResponseDelayMs").GetDouble() >= 5000 &&
                    delayed.GetProperty("uiResponseDelayMs").GetDouble() < 7000);
                Check.True(delayed.GetProperty("monitorIntervalMs").GetDouble() < 2000);
                Check.Equal(1, Volatile.Read(ref posts));
                Check.True(!poolProgress.IsSet && !firstRelease.IsSet);
                firstRelease.Set();
                UntilWithoutPool(() => ReadEntries(temp.Path)
                    .Any(item => Kind(item) == "UiProbePostResumed"));
                var resumed = ReadEntries(temp.Path).Single(item => Kind(item) == "UiProbePostResumed");
                Check.Equal("Accepted", resumed.GetProperty("probePostState").GetString());
                Check.True(resumed.GetProperty("lastUiResponseUtc").GetDateTimeOffset() >=
                    delayed.GetProperty("probeSentUtc").GetDateTimeOffset().AddSeconds(5));
                Check.True(secondEntered.Wait(TimeSpan.FromSeconds(2)));
                Check.Equal(2, Volatile.Read(ref posts));
                Check.True(!poolProgress.IsSet);
                var stopTime = Stopwatch.StartNew();
                diagnostics.Dispose();
                Check.True(stopTime.Elapsed < TimeSpan.FromSeconds(1));
                Check.True(!diagnostics.Completion.IsCompleted);
            }
            finally
            {
                diagnostics?.Dispose();
                firstRelease.Set();
                secondRelease.Set();
                poolRelease.Set();
                Check.True(ThreadPool.SetMaxThreads(previousMax, maxIo));
                Check.True(ThreadPool.SetMinThreads(previousMin, minIo));
                if (progressQueued) Check.True(poolProgress.Wait(TimeSpan.FromSeconds(2)));
                diagnostics?.Completion.GetAwaiter().GetResult();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static async Task WriterRecovery()
    {
        using var temp = new TemporaryDirectory();
        var blocked = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(blocked, "test-owned file");
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var diagnostics = new ResponsivenessDiagnostics(42, blocked,
            reportFailure: type => failure.TrySetResult(type));
        Check.Equal(nameof(IOException), await failure.Task);
        File.Delete(blocked);
        Directory.CreateDirectory(blocked);
        await Until(() => ReadEntries(blocked).Any(item => Kind(item) == "ProcessStarted"));
        diagnostics.Dispose();
        await diagnostics.Completion;
        Check.True(ReadEntries(blocked).Any(item => Kind(item) == "WriterRecovered"));
    }

    private static async Task BoundedRetention()
    {
        using var temp = new TemporaryDirectory();
        using var diagnostics = new ResponsivenessDiagnostics(42, temp.Path);
        await Until(() => diagnostics.WrittenEvents == 1);
        for (var batch = 0; batch < 80; batch++)
        {
            var expected = diagnostics.WrittenEvents + 128;
            for (var index = 0; index < 64; index++)
                using (diagnostics.BeginUiPhase(ResponsivenessPhase.WindowShow)) { }
            await Until(() => diagnostics.WrittenEvents == expected);
        }
        diagnostics.Dispose();
        await diagnostics.Completion;
        var files = Directory.GetFiles(temp.Path, "*.jsonl");
        Check.Equal(3, files.Length);
        Check.True(files.All(path => new FileInfo(path).Length <= ResponsivenessDiagnostics.FileSizeLimit));
        Check.Equal(0L, diagnostics.DroppedEvents);
        var entries = files.SelectMany(ReadFile).ToArray();
        Check.True(entries.All(item => item.GetProperty("sequence").GetInt64() > 1));
        Check.True(entries.Any(item => Kind(item) == "ProcessStopping"));
    }

    private static async Task InterruptedLaunch()
    {
        using var temp = new TemporaryDirectory();
        const string interrupted = """{"schemaVersion":1,"kind":"PhaseBegin",""";
        File.WriteAllText(Path.Combine(temp.Path, ResponsivenessDiagnostics.FileName), interrupted);
        for (var launch = 0; launch < 3; launch++)
        {
            using var diagnostics = new ResponsivenessDiagnostics(42, temp.Path);
            diagnostics.Dispose();
            await diagnostics.Completion;
            Check.Equal("ProcessStarted", Kind(ReadEntries(temp.Path)[0]));
            if (launch == 0)
                Check.Equal(interrupted, File.ReadAllText(Path.Combine(temp.Path, "responsiveness.1.jsonl")));
        }
        var files = Directory.GetFiles(temp.Path, "*.jsonl");
        Check.Equal(3, files.Length);
        Check.True(files.SelectMany(ReadFile).All(item => item.GetProperty("schemaVersion").GetInt32() == 1));
    }

    private static string? Kind(JsonElement item) => item.GetProperty("kind").GetString();
    private static string? Phase(JsonElement item)
        => item.TryGetProperty("phase", out var phase) ? phase.GetString() : null;

    private static JsonElement[] ReadEntries(string directory)
    {
        var path = Path.Combine(directory, ResponsivenessDiagnostics.FileName);
        return File.Exists(path) ? ReadFile(path) : [];
    }

    private static JsonElement[] ReadFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n').SkipLast(1)
            .Where(line => line.Length > 0)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray();
    }

    private static async Task Until(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8))
            await Task.Delay(10);
        Check.True(condition(), "condition did not complete within eight seconds");
    }

    private static void UntilWithoutPool(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8))
            Thread.Sleep(10);
        Check.True(condition(), "condition did not complete within eight seconds");
    }
}
