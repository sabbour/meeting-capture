using System.Text;
using MeetingCapture.Configuration;
using MeetingCapture.Intelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.Storage;

/// <summary>
/// Persists meeting notes as Markdown files under the configured OneDrive path.
/// File naming: YYYY-MM-DD_HH-mm_&lt;sanitized-title&gt;.md
///
/// Folder structure:
///   {NotesPath}/
///     YYYY-MM/
///       YYYY-MM-DD_HH-mm_MeetingTitle.md
/// </summary>
public sealed class MarkdownNotesStorage : INotesStorage
{
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
        var folder = Path.Combine(
            _settings.NotesPath,
            notes.CreatedAt.LocalDateTime.ToString("yyyy-MM"));

        Directory.CreateDirectory(folder);

        var safeTitle = SanitizeFilename(notes.MeetingTitle);
        var filename = $"{notes.CreatedAt.LocalDateTime:yyyy-MM-dd_HH-mm}_{safeTitle}.md";
        var fullPath = Path.Combine(folder, filename);

        var markdown = BuildMarkdown(notes);
        await File.WriteAllTextAsync(fullPath, markdown, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Notes saved: {Path}", fullPath);
        return fullPath;
    }

    private static string BuildMarkdown(MeetingNotes notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {notes.MeetingTitle}");
        sb.AppendLine();
        sb.AppendLine($"> Created: {notes.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(notes.Summary);
        sb.AppendLine();

        if (notes.Decisions.Count > 0)
        {
            sb.AppendLine("## Decisions");
            sb.AppendLine();
            foreach (var decision in notes.Decisions)
                sb.AppendLine($"- {decision}");
            sb.AppendLine();
        }

        if (notes.ActionItems.Count > 0)
        {
            sb.AppendLine("## Action Items");
            sb.AppendLine();
            foreach (var item in notes.ActionItems)
            {
                var owner = item.Owner is not null ? $" **({item.Owner})**" : string.Empty;
                var due = item.DueDate is not null ? $" — due {item.DueDate}" : string.Empty;
                sb.AppendLine($"- [ ] {item.Description}{owner}{due}");
            }
            sb.AppendLine();
        }

        if (notes.Topics.Count > 0)
        {
            sb.AppendLine("## Topics");
            sb.AppendLine();
            sb.AppendLine(string.Join(", ", notes.Topics.Select(t => $"`{t}`")));
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(notes.Nuances))
        {
            sb.AppendLine("## Nuances & Context");
            sb.AppendLine();
            sb.AppendLine(notes.Nuances);
            sb.AppendLine();
        }

        sb.AppendLine("## Raw Transcript");
        sb.AppendLine();
        sb.AppendLine("<details>");
        sb.AppendLine("<summary>Expand transcript</summary>");
        sb.AppendLine();
        sb.AppendLine(notes.RawTranscript);
        sb.AppendLine();
        sb.AppendLine("</details>");

        return sb.ToString();
    }

    private static string SanitizeFilename(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray());
        return safe.Length > 60 ? safe[..60] : safe;
    }
}
