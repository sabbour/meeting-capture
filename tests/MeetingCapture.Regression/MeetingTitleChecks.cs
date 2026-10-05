using MeetingCapture.MeetingDetection;
using MeetingCapture.TrayApp;

namespace MeetingCapture.Regression;

internal static class MeetingTitleChecks
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static MeetingTitleCandidate Event(string subject = "Synthetic planning", string? provider = "TeamsForBusiness",
        string? join = null, int startsInMinutes = -5, int endsInMinutes = 30)
        => new(subject, Now.AddMinutes(startsInMinutes), Now.AddMinutes(endsInMinutes), provider, join,
            IsOnlineMeeting: true, IsOrganizer: true,
            Attendees: [new("Synthetic attendee", "attendee@example.invalid")]);

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("title mismatch distinguishes absent events from missing platform metadata", () => Run(() =>
        {
            var result = MeetingTitleResolver.ResolveWithReason("ms-teams", Now,
                [Event(provider: "unknown"), Event("Another", provider: "unknown")]);
            Check.True(result.Title is null);
            Check.True(result.Detail.Contains("none has verified Microsoft Teams"));
            var later = MeetingTitleResolver.ResolveWithReason("ms-teams", Now,
                [Event(startsInMinutes: 20)]);
            Check.Equal("WorkIQ found no online meetings overlapping this signal.", later.Detail);
        }));
        yield return ("meeting title uses unique current platform calendar subject", () => Run(() =>
        {
            Check.Equal("Synthetic planning", MeetingTitleResolver.Resolve("ms-teams", Now, [Event()]));
            Check.Equal("Synthetic planning", MeetingTitleResolver.Resolve("Teams", Now,
                [Event(), Event("Unrelated Zoom", null, "https://tenant.zoom.us/j/123")]));
            Check.Equal("Teams meeting", MeetingTitleResolver.FallbackTitle("MS-TEAMS"));
        }));
        yield return ("meeting title rejects overlapping same-platform candidates including blank subjects", () => Run(() =>
        {
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(), Event("Other")]) is null);
            Check.Equal(2, MeetingTitleResolver.MatchingCandidates("Teams", Now, [Event(), Event("Other")]).Count);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(), Event("")]) is null);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(), Event("Soon", startsInMinutes: 5)]) is null);
        }));
        yield return ("meeting prompt prioritizes enriched identity and privacy-safe calendar metadata", () => Run(() =>
        {
            var meeting = Event("Synthetic planning") with
            {
                Organizer = new("Meeting Owner", "owner@example.invalid"),
                Attendees =
                [
                    new("First Invitee", "first@example.invalid"),
                    new("Second Invitee", "second@example.invalid"),
                    new("Third Invitee", "third@example.invalid")
                ]
            };
            var session = new MeetingSession
            {
                Title = "Teams meeting",
                SourceProcess = "Teams",
                StartedAt = Now
            };
            Check.True(session.TrySetPendingTitle(session.Id, "Active call title",
                "Title verified from the active Teams joined-call surface."));
            Check.True(session.TryAttachCalendarMeeting(session.Id, meeting));
            var prompt = MeetingPromptPresentation.Create(session, session.TitleSource);
            Check.Equal("Active call title", prompt.Title);
            Check.True(prompt.CalendarState.Contains("active Teams joined-call surface"));
            Check.True(prompt.Schedule!.Contains("Scheduled:"));
            Check.Equal("Organizer: Meeting Owner", prompt.Organizer);
            Check.True(prompt.Invitees!.Contains("Invitees: 3 - First Invitee, Second Invitee +1 more"));

            var unmatched = new MeetingSession
            {
                Title = "Teams meeting",
                SourceProcess = "Teams",
                StartedAt = Now
            };
            var signedOut = MeetingPromptPresentation.Create(
                unmatched, "WorkIQ is signed out. Manual selection is available.");
            Check.True(signedOut.CalendarState.Contains("signed out"));
            Check.True(signedOut.Schedule is null && signedOut.Organizer is null && signedOut.Invitees is null);

            var emailOnly = meeting with
            {
                Organizer = new(null, "owner@example.invalid"),
                Attendees = [new(null, "invitee@example.invalid")]
            };
            var privateSession = new MeetingSession { SourceProcess = "Teams" };
            Check.True(privateSession.TryAttachCalendarMeeting(privateSession.Id, emailOnly));
            var privatePrompt = MeetingPromptPresentation.Create(privateSession, "Matched.");
            Check.True(privatePrompt.Organizer is null);
            Check.Equal("Invitees: 1", privatePrompt.Invitees);
        }));
        yield return ("meeting title permits only near-upcoming valid timed events", () => Run(() =>
        {
            Check.Equal("Synthetic planning", MeetingTitleResolver.Resolve("Teams", Now, [Event(startsInMinutes: 5)]));
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(startsInMinutes: 6)]) is null);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(endsInMinutes: 0)]) is null);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event() with { IsCancelled = true }]) is null);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event() with { IsAllDay = true }]) is null);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(startsInMinutes: 3, endsInMinutes: 1)]) is null);
        }));
        yield return ("meeting title requires trustworthy platform metadata, not random calendar subject", () => Run(() =>
        {
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event(provider: null)]) is null);
            Check.True(MeetingTitleResolver.Resolve("unknown", Now, [Event()]) is null);
            Check.Equal("Synthetic planning", MeetingTitleResolver.Resolve("Zoom", Now,
                [Event(provider: null, join: "https://tenant.zoom.us/j/123")]));
            Check.True(MeetingTitleResolver.Resolve("Zoom", Now,
                [Event(provider: null, join: "https://zoom.us.example.invalid/j/123")]) is null);
            Check.True(MeetingTitleResolver.Resolve("Zoom", Now,
                [Event(provider: null, join: "http://zoom.us/j/123")]) is null);
            Check.Equal("Synthetic planning", MeetingTitleResolver.Resolve("webex", Now,
                [Event(provider: null, join: "https://synthetic.webex.com/meet/123")]));
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, []) is null);
            Check.True(MeetingTitleResolver.Resolve("Teams", Now, [Event("(no subject)")]) is null);
        }));
        yield return ("meeting title preserves identity and rejects stale dismissed ended or confirmed enrichment", () => Run(() =>
        {
            var session = new MeetingSession { Title = "Microsoft Teams activity detected", SourceProcess = "Teams" };
            var id = session.Id;
            Check.True(!session.TrySetPendingTitle(null, "Late result"));
            Check.True(!session.TrySetPendingTitle("other-session", "Late result"));
            Check.Equal("Microsoft Teams activity detected", session.Title);
            Check.True(session.TrySetPendingTitle(id, "Synthetic subject"));
            Check.True(session.TryAttachCalendarMeeting(id, Event("Synthetic subject")));
            Check.Equal("Synthetic attendee", session.InviteParticipants.Single().DisplayName);
            Check.Equal(id, session.Id);
            Check.Equal("Synthetic subject", session.Title);
            session.FreezeTitle();
            Check.True(!session.TrySetPendingTitle(id, "After confirmation"));
            Check.Equal("Synthetic subject", session.Title);
            var ended = new MeetingSession { Title = "Original title" };
            ended.End();
            Check.True(!ended.TrySetPendingTitle(ended.Id, "After exit"));
            Check.Equal("Original title", ended.Title);
        }));
        yield return ("meeting title accepted subject persists through capture snapshot and saved notes", async () =>
        {
            await using var f = new PipelineFixture();
            var session = new MeetingSession { Title = "Microsoft Teams activity detected", SourceProcess = "Teams" };
            f.Detector.Start(session);
            Check.True(session.TrySetPendingTitle(session.Id, "Synthetic resolved subject"));
            Check.True(session.TryAttachCalendarMeeting(session.Id, Event("Synthetic resolved subject")));
            await f.Pipeline.ConfirmAndStartAsync(session, CancellationToken.None);
            Check.True(!session.TrySetPendingTitle(session.Id, "Late calendar result"));
            Check.Equal("Synthetic resolved subject", f.Pipeline.CurrentTranscript!.Title);
            f.Transcription.Emit("Synthetic retained transcript.");
            await f.Pipeline.StopManualAsync();
            Check.Equal(session.Id, f.Detector.CompletedSessions.Single());
            Check.True(f.Detector.ActiveSession is null);
            Check.Equal(session.Id, f.Pipeline.LastCompletedTranscript!.SessionId);
            Check.Equal("Synthetic resolved subject", f.Pipeline.LastCompletedTranscript.Title);
            Check.True(f.Storage.Writes.All(notes => notes.MeetingTitle == "Synthetic resolved subject"));
            Check.True((await File.ReadAllTextAsync(f.Storage.Paths.Last())).Contains("Synthetic resolved subject"));
        });
        yield return ("manual calendar start carries invite roster into the active session", async () =>
        {
            await using var f = new PipelineFixture();
            var meeting = Event("Synthetic roster");
            await f.Pipeline.StartManualAsync("Synthetic roster", meeting, CancellationToken.None);
            Check.Equal("Synthetic attendee", f.Pipeline.CurrentSession!.InviteParticipants.Single().DisplayName);
            await f.Pipeline.StopManualAsync();
        });
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
}
