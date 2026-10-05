using MeetingCapture.Configuration;
using MeetingCapture.MeetingDetection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeetingCapture.Regression;

internal static class DetectionChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("detection new process replaces pending, confirmation pins identity", () => Run(Pinning));
        yield return ("detection PID reuse is a distinct lifetime", () => Run(ProcessReuse));
        yield return ("detection dismissal cooldown suppresses only selected lifetime", () => Run(Dismissal));
        yield return ("detection snooze expires and resume clears suppression", () => Run(Snooze));
        yield return ("detection enumeration failure retains session and rejects invalid durations", () => Run(EnumerationFailure));
        yield return ("detection cancellation terminates polling without false prompts", PollCancellation);
        yield return ("detection confirmed process exit offers remaining process and preserves clock identity", () => Run(ConfirmedExit));
        yield return ("detection confirmed session survives muted signal loss past grace", () => Run(ConfirmedMuteSurvival));
        yield return ("detection dismissed PID reuse bypasses prior lifetime cooldown", () => Run(DismissedProcessReuse));
        yield return ("detection invalid durations cannot mutate prompt or snooze state", () => Run(InvalidDurations));
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public Clock Clock { get; } = new();
        public IReadOnlyList<MeetingProcessSnapshot> Processes { get; set; } = [];
        public Exception? EnumerationError { get; set; }
        public List<MeetingSession> Started { get; } = [];
        public List<MeetingSession> Ended { get; } = [];
        public ProcessMeetingDetector Detector { get; }

        public Fixture()
        {
            Detector = new ProcessMeetingDetector(Options.Create(new AppSettings()),
                NullLogger<ProcessMeetingDetector>.Instance,
                () => EnumerationError is null ? Processes : throw EnumerationError, Clock, TimeSpan.Zero);
            Detector.MeetingStarted += (_, session) => Started.Add(session);
            Detector.MeetingEnded += (_, session) => Ended.Add(session);
        }

        public MeetingProcessSnapshot Process(int pid, int startedSeconds = 0, string name = "Teams")
            => new(pid, name, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(startedSeconds), HasActiveCapture: true);
        public void Dispose() => Detector.Dispose();
    }

    private static void Pinning()
    {
        using var f = new Fixture();
        var first = f.Process(1);
        var second = f.Process(2, 1, "Zoom");
        f.Processes = [first];
        f.Detector.PollOnce();
        var pending = f.Detector.ActiveSession!;
        Check.Equal("Teams meeting", pending.Title);
        Check.Equal(f.Clock.Now, pending.SignalStartedAt);
        f.Detector.PollOnce();
        Check.Equal(1, f.Started.Count);
        f.Processes = [first, second];
        f.Detector.PollOnce();
        var confirmed = f.Detector.ActiveSession!;
        Check.True(pending.Id != confirmed.Id);
        Check.Equal("Zoom", confirmed.SourceProcess);
        Check.Equal(pending.Id, f.Ended.Single().Id);
        Check.True(f.Detector.Confirm(confirmed.Id));
        f.Processes = [first, second, f.Process(3, 2)];
        f.Detector.PollOnce();
        Check.Equal(confirmed.Id, f.Detector.ActiveSession!.Id);
        Check.Equal(2, f.Started.Count);
        Check.True(!f.Detector.Dismiss(confirmed.Id, TimeSpan.FromMinutes(10)));
        f.Detector.Snooze(TimeSpan.FromMinutes(30));
        Check.Equal(confirmed.Id, f.Detector.ActiveSession!.Id);
        f.Processes = [first];
        f.Detector.PollOnce();
        Check.Equal(confirmed.Id, f.Ended.Last().Id);
        Check.True(f.Detector.ActiveSession is null);
    }

    private static void ProcessReuse()
    {
        using var f = new Fixture();
        f.Processes = [f.Process(7)];
        f.Detector.PollOnce();
        var original = f.Detector.ActiveSession!;
        Check.True(f.Detector.Confirm(original.Id));
        f.Processes = [f.Process(7, 60)];
        f.Detector.PollOnce();
        Check.Equal(original.Id, f.Ended.Single().Id);
        Check.True(!original.IsActive);
        Check.True(original.Id != f.Detector.ActiveSession!.Id);
        Check.True(!f.Detector.Confirm(original.Id));
    }

    private static void Dismissal()
    {
        using var f = new Fixture();
        var first = f.Process(1);
        f.Processes = [first];
        f.Detector.PollOnce();
        var dismissed = f.Detector.ActiveSession!;
        Check.True(f.Detector.Dismiss(dismissed.Id, TimeSpan.FromMinutes(10)));
        Check.True(f.Detector.ActiveSession is null);
        Check.True(!f.Detector.Dismiss(dismissed.Id, TimeSpan.FromMinutes(10)));
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is null);
        f.Processes = [first, f.Process(2, 1)];
        f.Detector.PollOnce();
        Check.Equal(2, f.Started.Count);
        Check.True(f.Detector.Dismiss(f.Detector.ActiveSession!.Id, TimeSpan.FromMinutes(10)));
        f.Processes = [first];
        f.Clock.Now = f.Clock.Now.AddMinutes(10).AddTicks(-1);
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is null);
        f.Clock.Now = f.Clock.Now.AddTicks(1);
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is not null);
        Check.True(f.Detector.ActiveSession.Id != dismissed.Id);
    }

    private static void Snooze()
    {
        using var f = new Fixture();
        f.Processes = [f.Process(1)];
        f.Detector.PollOnce();
        f.Detector.Snooze(TimeSpan.FromMinutes(30));
        Check.True(f.Detector.ActiveSession is null);
        Check.Equal(f.Clock.Now.AddMinutes(30), f.Detector.SnoozedUntil!.Value);
        f.Clock.Now = f.Clock.Now.AddMinutes(30).AddTicks(-1);
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is null);
        f.Clock.Now = f.Clock.Now.AddTicks(1);
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is not null);
        Check.True(f.Detector.SnoozedUntil is null);
        f.Detector.Dismiss(f.Detector.ActiveSession.Id, TimeSpan.FromMinutes(10));
        f.Detector.Snooze(TimeSpan.FromMinutes(30));
        f.Detector.Resume();
        Check.True(f.Detector.SnoozedUntil is null);
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is not null);
    }

    private static void EnumerationFailure()
    {
        using var f = new Fixture();
        f.Processes = [f.Process(1), f.Process(2, 2, "Outlook"), f.Process(3, 3, "Slack")];
        f.Detector.PollOnce();
        var session = f.Detector.ActiveSession!;
        Check.Equal("Teams", session.SourceProcess);
        f.EnumerationError = new IOException("Synthetic enumeration fault");
        Check.Throws<IOException>(f.Detector.PollOnce);
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        Check.Equal(0, f.Ended.Count);
        Check.Throws<ArgumentOutOfRangeException>(() => f.Detector.Snooze(TimeSpan.Zero));
        Check.Throws<ArgumentOutOfRangeException>(() => f.Detector.Snooze(TimeSpan.FromSeconds(-1)));
        Check.Throws<ArgumentOutOfRangeException>(() => f.Detector.Dismiss(session.Id, TimeSpan.Zero));
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        f.EnumerationError = null;
        f.Processes = [];
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Ended.Single().Id);
        Check.True(f.Detector.ActiveSession is null);
    }

    private static void ConfirmedMuteSurvival()
    {
        using var f = new Fixture();
        f.Processes = [f.Process(1, 0, "Teams") with { HasActiveCapture = true }];
        f.Detector.PollOnce();
        var session = f.Detector.ActiveSession!;
        Check.True(f.Detector.Confirm(session.Id));
        // Mute: the process stays present but capture activity drops out. Confirmed sessions
        // must never auto-end from this (spec: no prompt for temporary signal loss or mute).
        f.Processes = [f.Process(1, 0, "Teams") with { HasActiveCapture = false }];
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        Check.Equal(0, f.Ended.Count);
        f.Clock.Now = f.Clock.Now.AddMinutes(5); // Far past the unconfirmed signal-loss grace.
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        Check.Equal(0, f.Ended.Count);
        // Capture activity returns; still the same confirmed lifetime, never ended.
        f.Processes = [f.Process(1, 0, "Teams") with { HasActiveCapture = true }];
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        Check.Equal(0, f.Ended.Count);
    }

    private static void ConfirmedExit()
    {
        using var f = new Fixture();
        var first = f.Process(1);
        var second = f.Process(2, 1, "Zoom");
        f.Processes = [first];
        f.Detector.PollOnce();
        var session = f.Detector.ActiveSession!;
        Check.Equal(f.Clock.Now, session.StartedAt);
        Check.True(f.Detector.Confirm(session.Id));
        f.Processes = [first, second];
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        f.Detector.Snooze(TimeSpan.FromMinutes(30));
        Check.Equal(session.Id, f.Detector.ActiveSession.Id);
        f.Detector.Resume();
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        f.Processes = [second];
        f.Detector.PollOnce();
        Check.Equal(session.Id, f.Ended.Single().Id);
        Check.Equal(f.Clock.Now, session.EndedAt!.Value);
        Check.Equal("Zoom", f.Detector.ActiveSession!.SourceProcess);
        Check.True(f.Detector.ActiveSession.Id != session.Id);
        var endedAt = session.EndedAt;
        session.End();
        Check.Equal(endedAt, session.EndedAt);
    }

    private static void DismissedProcessReuse()
    {
        using var f = new Fixture();
        f.Processes = [f.Process(7)];
        f.Detector.PollOnce();
        var dismissed = f.Detector.ActiveSession!;
        Check.True(f.Detector.Dismiss(dismissed.Id, TimeSpan.FromMinutes(10)));
        f.Processes = [f.Process(7, 1)];
        f.Detector.PollOnce();
        Check.True(f.Detector.ActiveSession is not null);
        Check.True(f.Detector.ActiveSession.Id != dismissed.Id);
        Check.Equal(2, f.Started.Count);
    }

    private static void InvalidDurations()
    {
        using var f = new Fixture();
        f.Processes = [f.Process(1)];
        f.Detector.PollOnce();
        var session = f.Detector.ActiveSession!;
        foreach (var duration in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue })
        {
            Check.Throws<ArgumentOutOfRangeException>(() => f.Detector.Dismiss(session.Id, duration));
            Check.Throws<ArgumentOutOfRangeException>(() => f.Detector.Snooze(duration));
            Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
            Check.True(f.Detector.SnoozedUntil is null);
            Check.Equal(0, f.Ended.Count);
        }
        f.Detector.Snooze(TimeSpan.FromMinutes(30));
        var until = f.Detector.SnoozedUntil;
        Check.Throws<ArgumentOutOfRangeException>(() => f.Detector.Snooze(TimeSpan.MaxValue));
        Check.Equal(until, f.Detector.SnoozedUntil);
    }

    private static async Task PollCancellation()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var detector = new ProcessMeetingDetector(Options.Create(new AppSettings()),
            NullLogger<ProcessMeetingDetector>.Instance,
            () => { observed.TrySetResult(); return []; }, TimeProvider.System);
        await detector.StartAsync(CancellationToken.None);
        await observed.Task;
        await detector.StopAsync(CancellationToken.None);
        Check.True(detector.ActiveSession is null);
        await detector.StartAsync(CancellationToken.None);
        await detector.StopAsync(CancellationToken.None);
        Check.True(detector.ActiveSession is null);
    }
}
