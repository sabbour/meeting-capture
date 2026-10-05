using MeetingCapture.Configuration;
using MeetingCapture.MeetingDetection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeetingCapture.Regression;

internal static class CallSignalChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("open Teams without capture activity never suggests a meeting", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(false);
            f.Advance(300, false);
            Check.Equal(0, f.Started.Count);
            Check.True(f.Detector.ActiveSession is null);
        }));
        yield return ("meeting suggestion needs sustained capture, not a short microphone blip", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Advance(19, true);
            Check.Equal(0, f.Started.Count);
            f.Advance(1, false);
            f.Advance(1, true);
            f.Advance(19, true);
            Check.Equal(0, f.Started.Count);
            f.Advance(1, true);
            Check.Equal(1, f.Started.Count);
            Check.Equal("Teams meeting", f.Started[0].Title);
        }));
        yield return ("unconfirmed signal loss has grace and permits consecutive calls in the same process", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Advance(20, true);
            var first = f.Detector.ActiveSession!;
            f.Advance(1, false);
            f.Advance(29, false);
            Check.Equal(first.Id, f.Detector.ActiveSession!.Id);
            f.Advance(1, false);
            Check.True(f.Detector.ActiveSession is null && !first.IsActive);
            f.Advance(1, true);
            f.Advance(20, true);
            Check.Equal(2, f.Started.Count);
            Check.True(first.Id != f.Detector.ActiveSession!.Id);
        }));
        yield return ("short signal loss preserves suggestion identity", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Advance(20, true);
            var first = f.Detector.ActiveSession!;
            f.Advance(1, false);
            f.Advance(10, true);
            f.Advance(20, true);
            Check.Equal(first.Id, f.Detector.ActiveSession!.Id);
            Check.Equal(1, f.Started.Count);
            Check.Equal(0, f.Ended.Count);
        }));
        yield return ("muted confirmed recording survives signal loss until explicit completion", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Advance(20, true);
            var first = f.Detector.ActiveSession!;
            Check.True(f.Detector.Confirm(first.Id));
            f.Advance(1, false);
            f.Advance(3600, false);
            Check.Equal(first.Id, f.Detector.ActiveSession!.Id);
            Check.Equal(0, f.Ended.Count);
            f.Detector.Complete(first.Id);
            Check.Equal(1, f.Ended.Count);
            f.Advance(1, true);
            f.Advance(20, true);
            Check.Equal(2, f.Started.Count);
        }));
        yield return ("completed capture does not reprompt during same microphone episode", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Advance(20, true);
            var first = f.Detector.ActiveSession!;
            Check.True(f.Detector.Confirm(first.Id));
            f.Detector.Complete(first.Id);
            f.Advance(300, true);
            Check.True(f.Detector.ActiveSession is null);
            f.Advance(1, false);
            f.Advance(1, true);
            f.Advance(20, true);
            Check.Equal(2, f.Started.Count);
        }));
        yield return ("capture-session enumeration failure does not fabricate an end or new call", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Advance(20, true);
            var session = f.Detector.ActiveSession!;
            f.Fail = true;
            Check.Throws<IOException>(() => f.Advance(60, false));
            Check.Equal(session.Id, f.Detector.ActiveSession!.Id);
            Check.Equal(0, f.Ended.Count);
        }));
        yield return ("unknown capture intervals do not count toward debounce", () => Run(() =>
        {
            using var f = new Fixture();
            f.Poll(true);
            f.Fail = true;
            Check.Throws<IOException>(() => f.Advance(60, true));
            f.Fail = false;
            f.Poll(true);
            Check.Equal(0, f.Started.Count);
            f.Advance(20, true);
            Check.Equal(1, f.Started.Count);
        }));
    }

    private static Task Run(Action run) { run(); return Task.CompletedTask; }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly Clock _clock = new();
        private readonly DateTimeOffset _processStarted = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        private bool _capture;
        public bool Fail;
        public ProcessMeetingDetector Detector { get; }
        public List<MeetingSession> Started { get; } = [];
        public List<MeetingSession> Ended { get; } = [];
        public Fixture()
        {
            Detector = new ProcessMeetingDetector(Options.Create(new AppSettings()),
                NullLogger<ProcessMeetingDetector>.Instance,
                () => Fail ? throw new IOException("Synthetic capture-session enumeration failure") :
                    [new MeetingProcessSnapshot(42, "ms-teams", _processStarted, _capture)], _clock);
            Detector.MeetingStarted += (_, session) => Started.Add(session);
            Detector.MeetingEnded += (_, session) => Ended.Add(session);
        }
        public void Poll(bool capture) { _capture = capture; Detector.PollOnce(); }
        public void Advance(int seconds, bool capture) { _clock.Now = _clock.Now.AddSeconds(seconds); Poll(capture); }
        public void Dispose() => Detector.Dispose();
    }
}
