using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace MeetingCapture.TrayApp;

public sealed class CompletionTextDecorationsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? TextDecorations.Strikethrough : TextDecorations.None;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Converts a bool to Visible/Collapsed for lightweight per-item template markers.</summary>
public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// View model for a single speaker row in the recording-state speaker panel: avatar, name and the
/// speaker's talking timeline on the shared meeting axis. Properties raise change notifications so
/// rows update in place without rebuilding the list.
/// </summary>
public sealed class SpeakerBalance : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _name = string.Empty;
    private string _avatarInitials = "?";
    private string _avatarAccessibleName = "Unknown speaker";
    private ImageSource? _avatarImage;
    private Brush? _brush;
    private IReadOnlyList<SpeakerTimelineSpan> _spans = [];
    private string _talkTimeText = string.Empty;

    public string SpeakerId { get; set; } = string.Empty;

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public string AvatarInitials
    {
        get => _avatarInitials;
        set { if (_avatarInitials != value) { _avatarInitials = value; OnPropertyChanged(); } }
    }

    public string AvatarAccessibleName
    {
        get => _avatarAccessibleName;
        set { if (_avatarAccessibleName != value) { _avatarAccessibleName = value; OnPropertyChanged(); } }
    }

    public ImageSource? AvatarImage
    {
        get => _avatarImage;
        set { if (!ReferenceEquals(_avatarImage, value)) { _avatarImage = value; OnPropertyChanged(); } }
    }

    /// <summary>Colour of this speaker's segments; matches the speaker's transcript colour.</summary>
    public Brush? Brush
    {
        get => _brush;
        set { if (!ReferenceEquals(_brush, value)) { _brush = value; OnPropertyChanged(); } }
    }

    /// <summary>Talking intervals as fractions (0–1) of the meeting length.</summary>
    public IReadOnlyList<SpeakerTimelineSpan> Spans
    {
        get => _spans;
        set { _spans = value; OnPropertyChanged(); }
    }

    public string TalkTimeText
    {
        get => _talkTimeText;
        set { if (_talkTimeText != value) { _talkTimeText = value; OnPropertyChanged(); } }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// View model for a single entry in the idle-state "Today's meetings" calendar list.
/// </summary>
public sealed class MeetingRow
{
    /// <summary>Meeting subject (truncated in the UI).</summary>
    public required string Subject { get; init; }

    /// <summary>Segoe MDL2 glyph char: filled circle (live), timer (upcoming), history (past).</summary>
    public required string Glyph { get; init; }

    /// <summary>Foreground brush for the status glyph — red/blue/grey per meeting state.</summary>
    public required Brush GlyphForeground { get; init; }

    /// <summary>Scheduled time, not evidence of a joined call.</summary>
    public required string TimeText { get; init; }

    public required string DetailsText { get; init; }
    public required string AccessibleName { get; init; }

    /// <summary>Pre-filled recording title used when the user clicks [▶ Record].</summary>
    public required string Title { get; init; }
    public required MeetingTitleCandidate Meeting { get; init; }

    public static MeetingRow From(MeetingTitleCandidate meeting, DateTimeOffset now)
    {
        var start = meeting.Start.ToLocalTime();
        var end = meeting.End.ToLocalTime();
        var isNow = meeting.Start <= now && now < meeting.End;
        var isUpcoming = meeting.Start > now;
        // MDL2 glyphs: E9D9 = Record (filled circle), E823 = Timer, E81C = History
        var glyph = isNow ? "\uE9D9" : isUpcoming ? "\uE823" : "\uE81C";
        var brush = isNow
            ? new SolidColorBrush(Color.FromArgb(255, 196, 43,  28))   // red — live
            : isUpcoming
            ? new SolidColorBrush(Color.FromArgb(255,   0, 120, 212))  // blue — upcoming
            : new SolidColorBrush(Color.FromArgb(255, 136, 136, 136)); // grey — past
        var subject = string.IsNullOrWhiteSpace(meeting.Subject) ? "(no subject)" : meeting.Subject.Trim();
        var participants = MeetingTitleResolver.InviteParticipants(meeting);
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(meeting.Organizer?.Name))
            details.Add($"Organizer: {meeting.Organizer.Name.Trim()}");
        if (participants.Count > 0)
        {
            var preview = participants.Select(person => person.Name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(2)
                .ToArray();
            details.Add($"{participants.Count} invitee{(participants.Count == 1 ? "" : "s")}" +
                (preview.Length == 0 ? "" : $": {string.Join(", ", preview)}" +
                 (participants.Count > preview.Length ? $" +{participants.Count - preview.Length} more" : "")));
        }
        var timeText = $"{(isNow ? "Ongoing" : isUpcoming ? "Upcoming" : "Ended")}: {start:h:mm tt} - {end:h:mm tt}";
        var detailsText = string.Join(" | ", details);
        return new MeetingRow
        {
            Subject       = subject,
            Glyph         = glyph,
            GlyphForeground = brush,
            TimeText      = timeText,
            DetailsText   = detailsText,
            AccessibleName = string.Join(", ", new[] { subject, timeText, detailsText, "Record this meeting" }
                .Where(value => value.Length > 0)),
            Title         = $"{subject} \u2014 {DateTime.Now:yyyy-MM-dd HH:mm}",
            Meeting       = meeting,
        };
    }
}

public sealed record SpeakerNameOption(string Name, string? Email, bool IsLocal = false)
{
    public string Display => IsLocal ? $"{Name} (You)"
        : string.IsNullOrWhiteSpace(Email) ? Name : $"{Name} <{Email}>";
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Email?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    public override string ToString() => Display;
}

/// <summary>
/// View model for a single M365 related-content item shown in the recording-state
/// "Related" panel. Wraps <see cref="RelatedItem"/> and adds a Segoe MDL2 glyph.
/// </summary>
public sealed class RelatedItemViewModel
{
    public RelatedItemViewModel(RelatedItem item)
    {
        Title   = item.Title;
        Snippet = item.Snippet;
        WebUrl  = item.WebUrl;
        SourceGlyph = item.SourceType switch
        {
            "Email"      => "\uE715",
            "Teams"      => "\uE716",
            "Document"   => "\uE8A5",
            "Transcript" => "\uE8D4",
            _            => "\uE946", // Info / fallback
        };
    }

    public string Title { get; }
    public string Snippet { get; }
    public string? WebUrl { get; }
    public string SourceGlyph { get; }
}

public sealed class ContextEvidenceSourceViewModel
{
    public static IReadOnlyList<ContextEvidenceSourceViewModel> Create(RelatedContextItem item) =>
        item.Sources.Select((source, index) =>
            new ContextEvidenceSourceViewModel(
                source,
                item.Title,
                item.Summary,
                includeDetail: index == 0)).ToArray();

    public ContextEvidenceSourceViewModel(
        ContextEvidenceSource source,
        string itemTitle,
        string itemSummary,
        bool includeDetail)
    {
        var sourceTitle = ContextTitlePresentation.Clean(source.Title);
        var suggestionTitle = ContextTitlePresentation.Clean(itemTitle);
        Title = sourceTitle.Length > 0
            ? sourceTitle
            : suggestionTitle.Length > 0
                ? suggestionTitle
                : "Untitled source";
        Url = source.Url;
        Source = source.SourceType switch
        {
            ContextSourceType.TeamsMessage => "Teams message",
            ContextSourceType.TeamsGroupChat => "Teams group chat",
            ContextSourceType.TeamsChannel => "Teams channel",
            ContextSourceType.OutlookMail => "Outlook mail",
            ContextSourceType.Calendar => "Calendar",
            ContextSourceType.Document => "Document",
            ContextSourceType.MeetingArtifact => "Meeting artifact",
            ContextSourceType.LocalTranscript => "Prior transcript",
            _ => "Source",
        };
        Date = source.ActualSourceDate?.ToLocalTime().ToString("g") ?? "Date unavailable";
        Detail = includeDetail
            ? ContextTitlePresentation.UniqueDetail(itemSummary, itemTitle, source.Title)
            : string.Empty;
    }

    public string Title { get; }
    public string Url { get; }
    public string Source { get; }
    public string Date { get; }
    public string Detail { get; }
    public string Metadata => $"{Source} · {Date}";
    public string ExternalActionName => $"Open {Source}, {Date}, externally: {Title}";
}
