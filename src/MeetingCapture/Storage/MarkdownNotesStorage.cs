using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingCapture.Configuration;
using MeetingCapture.Intelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.Storage;

/// <summary>
/// Persists full session records atomically with a readable name and stable session identity.
/// </summary>
public sealed class MarkdownNotesStorage : INotesStorage
{
    private const int MaxFilenameLabelLength = 72;
    private readonly StorageSettings _settings;
    private readonly ILogger<MarkdownNotesStorage> _logger;

    public MarkdownNotesStorage(
        IOptions<AppSettings> settings,
        ILogger<MarkdownNotesStorage> logger)
    {
        _settings = settings.Value.Storage;
        _logger = logger;
    }

    public async Task<string> SaveAsync(MeetingNotes notes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (string.IsNullOrWhiteSpace(notes.SessionId))
            throw new ArgumentException("A session ID is required to save notes.", nameof(notes));
        cancellationToken.ThrowIfCancellationRequested();
        var folder = _settings.NotesPath;
        Directory.CreateDirectory(folder);
        // Session identity, rather than the title, keeps retries and later speaker edits on one stable file.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notes.SessionId)));
        var fullPath = ResolvePath(folder, notes, identity);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var markdown = BuildMarkdown(notes);
        try
        {
            // Flush the complete temporary file before swapping it into place, so readers see either
            // the previous notes or the full replacement.
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(markdown.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
                File.Replace(temporary, fullPath, null);
            else
                File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        _logger.LogInformation("Session notes and transcript saved.");
        return fullPath;
    }

    private static string ResolvePath(string folder, MeetingNotes notes, string identity)
    {
        // Reuse the existing session file even if its meeting title or generated topics later change.
        var existing = Directory.EnumerateFiles(folder, $"*_{identity}.md", SearchOption.TopDirectoryOnly)
            .Take(2).ToArray();
        if (existing.Length > 1)
            throw new IOException("Multiple note files exist for the same session.");
        if (existing.Length == 1)
            return existing[0];

        var label = RepresentativeLabel(notes);
        return Path.Combine(folder, $"{notes.CreatedAt.LocalDateTime:yyyy-MM-dd_HH-mm}_{label}_{identity}.md");
    }

    private static string RepresentativeLabel(MeetingNotes notes)
    {
        var title = notes.MeetingTitle.Trim();
        if (!IsGenericTitle(title))
            return SanitizeFilename(title);

        // Generic detected titles are not useful filenames; prefer a generated topic, then transcript text.
        var grounded = notes.Topics.FirstOrDefault(topic => !string.IsNullOrWhiteSpace(topic))
            ?? notes.RawTranscript;
        return SanitizeFilename(grounded);
    }

    private static bool IsGenericTitle(string title)
        => string.IsNullOrWhiteSpace(title) ||
           title.Equals("Meeting", StringComparison.OrdinalIgnoreCase) ||
           title.StartsWith("Manual Recording", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFilename(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? ' ' : character)
            .ToArray());
        safe = Regex.Replace(safe, @"\s+", " ").Trim(' ', '.');
        if (safe.Length > MaxFilenameLabelLength)
        {
            // Avoid cutting a word in half when a shorter boundary is reasonably close to the limit.
            safe = safe[..MaxFilenameLabelLength].TrimEnd();
            var wordBoundary = safe.LastIndexOf(' ');
            if (wordBoundary >= MaxFilenameLabelLength / 2)
                safe = safe[..wordBoundary];
        }
        return string.IsNullOrWhiteSpace(safe) ? "Recording" : safe;
    }

    private static string BuildMarkdown(MeetingNotes notes)
    {
        var sb = new StringBuilder();

        // YAML frontmatter lets tools index meeting metadata without parsing the full prose below.
        sb.AppendLine("---");
        sb.AppendLine($"session_id: {YamlString(notes.SessionId)}");
        sb.AppendLine($"title: {YamlString(MeetingNotesText.WithoutSegmentOrdinals(notes.MeetingTitle))}");
        sb.AppendLine($"date: {notes.CreatedAt.LocalDateTime:yyyy-MM-dd}");
        sb.AppendLine($"duration_minutes: {notes.EstimatedDurationMinutes}");
        sb.AppendLine($"meeting_type: {YamlString(notes.MeetingType)}");
        sb.AppendLine($"sentiment: {YamlString(notes.Sentiment)}");
        var outcome = notes.IntelligenceFailure is not null ? "failed"
            : string.IsNullOrWhiteSpace(notes.Summary) ? "not_generated" : "succeeded";
        sb.AppendLine($"intelligence_outcome: {outcome}");

        if (notes.Participants.Count > 0)
        {
            sb.AppendLine("participants:");
            foreach (var p in notes.Participants)
            {
                sb.AppendLine($"  - name: {YamlString(p.Name)}");
                sb.AppendLine($"    role: {(p.Role is null ? "null" : YamlString(p.Role))}");
            }
        }
        else
        {
            sb.AppendLine("participants: []");
        }

        sb.AppendLine($"topics: [{string.Join(", ", notes.Topics.Select(MeetingNotesText.WithoutSegmentOrdinals).Select(YamlString))}]");
        sb.AppendLine($"has_open_actions: {(notes.ActionItems.Count > 0 ? "true" : "false")}");
        sb.AppendLine($"has_decisions: {(notes.Decisions.Count > 0 ? "true" : "false")}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {MeetingNotesText.WithoutSegmentOrdinals(notes.MeetingTitle).Replace("\r", " ").Replace("\n", " ")}");
        sb.AppendLine();
        // Keep AI status explicit so a saved transcript is not mistaken for successfully generated notes.
        sb.AppendLine("## Intelligence Status");
        sb.AppendLine();
        if (notes.IntelligenceFailure is { } failure)
        {
            sb.AppendLine($"AI generation failed ({failure.Kind}). {IntelligenceErrors.Message(failure.Kind)}");
            sb.AppendLine("The captured transcript is preserved below. Retry AI in the app while this completed session remains available.");
        }
        else
            sb.AppendLine(outcome == "succeeded" ? "AI notes generated." : "AI notes have not been generated. The captured transcript is preserved below.");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes.Summary) ? "_No summary._" : MeetingNotesText.WithoutSegmentOrdinals(notes.Summary));
        sb.AppendLine();

        sb.AppendLine("## Nuances and Context");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes.Nuances) ? "_None noted._" : MeetingNotesText.WithoutSegmentOrdinals(notes.Nuances));
        sb.AppendLine();

        // Topic summaries are grounded in the saved topic map; show an explicit placeholder when analysis
        // has not produced usable bullets yet.
        sb.AppendLine("## Meeting Notes");
        sb.AppendLine();
        if (notes.TopicNotes.Count > 0)
        {
            foreach (var topic in notes.TopicNotes)
            {
                sb.AppendLine($"### {MeetingNotesText.WithoutSegmentOrdinals(topic.Topic)}");
                sb.AppendLine();
                if (topic.Bullets.Count > 0)
                    foreach (var bullet in topic.Bullets)
                        sb.AppendLine($"- {MeetingNotesText.WithoutSegmentOrdinals(bullet)}");
                else
                    sb.AppendLine("_No transcript-grounded summary is available yet._");
                sb.AppendLine();
            }
        }
        else
            sb.AppendLine("_No accepted topics were recorded._");
        sb.AppendLine();

        // Separate the narrative title from its explanation when the generated text uses the expected delimiter.
        sb.AppendLine("## Topic-by-Topic Narrative");
        sb.AppendLine();
        if (notes.KeyDiscussionPoints.Count > 0)
            foreach (var discussionPoint in notes.KeyDiscussionPoints)
            {
                var point = MeetingNotesText.WithoutSegmentOrdinals(discussionPoint);
                var separator = point.IndexOf(" — ", StringComparison.Ordinal);
                if (separator > 0 && separator + 3 < point.Length)
                {
                    sb.AppendLine($"### {point[..separator]}");
                    sb.AppendLine();
                    sb.AppendLine(point[(separator + 3)..]);
                    sb.AppendLine();
                }
                else
                    sb.AppendLine($"- {point}");
            }
        else
            sb.AppendLine("_None recorded._");
        sb.AppendLine();

        sb.AppendLine("## Decisions Made");
        sb.AppendLine();
        if (notes.Decisions.Count > 0)
            foreach (var decision in notes.Decisions)
                sb.AppendLine($"- {MeetingNotesText.WithoutSegmentOrdinals(decision)}");
        else
            sb.AppendLine("_No decisions recorded._");
        sb.AppendLine();

        sb.AppendLine("## Action Items");
        sb.AppendLine();
        if (notes.ActionItems.Count > 0)
            foreach (var item in notes.ActionItems)
            {
                var owner = string.IsNullOrWhiteSpace(item.Owner) ? "unspecified" : item.Owner;
                var due = string.IsNullOrWhiteSpace(item.DueDate) ? "unspecified" : item.DueDate;
                sb.AppendLine($"- [ ] {MeetingNotesText.WithoutSegmentOrdinals(item.Description)} — **Owner:** {owner} — **Due:** {due}");
            }
        else
            sb.AppendLine("_None._");
        sb.AppendLine();

        sb.AppendLine("## Open Questions");
        sb.AppendLine();
        if (notes.OpenQuestions.Count > 0)
            foreach (var q in notes.OpenQuestions)
                sb.AppendLine($"- {MeetingNotesText.WithoutSegmentOrdinals(q)}");
        else
            sb.AppendLine("_None._");
        sb.AppendLine();

        sb.AppendLine("## Notable Quotes");
        sb.AppendLine();
        if (notes.NotableQuotes.Count > 0)
            foreach (var quote in notes.NotableQuotes)
                sb.AppendLine($"> {MeetingNotesText.WithoutSegmentOrdinals(quote)}");
        else
            sb.AppendLine("_None._");
        sb.AppendLine();

        sb.AppendLine("## Follow-up Meetings");
        sb.AppendLine();
        if (notes.FollowUpMeetings.Count > 0)
            foreach (var f in notes.FollowUpMeetings)
                sb.AppendLine($"- {MeetingNotesText.WithoutSegmentOrdinals(f)}");
        else
            sb.AppendLine("_None mentioned._");

        sb.AppendLine();
        sb.AppendLine();
        // Choose a fence longer than any run of backticks in user speech so the Markdown remains valid.
        sb.AppendLine("## Raw Transcript");
        sb.AppendLine();
        var longestFence = Regex.Matches(notes.RawTranscript, "`+").Select(match => match.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longestFence + 1));
        sb.AppendLine(fence + "text");
        sb.AppendLine(notes.RawTranscript);
        sb.AppendLine(fence);
        return sb.ToString();
    }

    private static string YamlString(string value)
        => JsonSerializer.Serialize(value);
}
