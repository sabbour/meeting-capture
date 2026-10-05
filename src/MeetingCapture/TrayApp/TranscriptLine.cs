using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace MeetingCapture.TrayApp;

/// <summary>
/// View model for a single line in the live transcript list. Implements
/// <see cref="INotifyPropertyChanged"/> so partial results can be mutated
/// in-place without triggering a CollectionChanged + full ListView layout pass.
/// </summary>
public sealed class TranscriptLine : INotifyPropertyChanged, ITranscriptRowState
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _speaker = string.Empty;
    private string _text = string.Empty;
    private Brush? _speakerBrush;
    private string _avatarInitials = "?";
    private string _avatarAccessibleName = "Unknown speaker";
    private ImageSource? _avatarImage;
    private double _opacity = 1.0;
    private string _timestamp = string.Empty;
    private string _speakerId = string.Empty;
    private long _revision;

    public required string Speaker
    {
        get => _speaker;
        set { if (_speaker != value) { _speaker = value; OnPropertyChanged(); } }
    }

    public required string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(); } }
    }

    public required Brush SpeakerBrush
    {
        get => _speakerBrush!;
        set { if (!ReferenceEquals(_speakerBrush, value)) { _speakerBrush = value; OnPropertyChanged(); } }
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

    /// <summary>Optional inline timestamp shown muted before the text, e.g. "[14:03:21]".</summary>
    public string Timestamp
    {
        get => _timestamp;
        set { if (_timestamp != value) { _timestamp = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Raw diarization speaker ID (e.g. "Guest-2") this line belongs to. Used as the stable key for
    /// speaker colouring and for retroactively relabelling lines when a speaker is resolved to a
    /// real name. The visible <see cref="Speaker"/> text may differ (e.g. "Ahmed:").
    /// </summary>
    public string SpeakerId
    {
        get => _speakerId;
        set { if (_speakerId != value) { _speakerId = value; OnPropertyChanged(); } }
    }

    public long Revision
    {
        get => _revision;
        set => _revision = value;
    }

    /// <summary>Always upright; partial vs final is conveyed via <see cref="Opacity"/>.</summary>
    public FontStyle FontStyle { get; init; } = FontStyle.Normal;

    /// <summary>Wall-clock time the line was created; used to window the last N seconds
    /// of transcript for the live "What did I miss?" catch-up summary.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>1.0 for final lines, 0.65 for live/partial lines.</summary>
    public double Opacity
    {
        get => _opacity;
        set { if (_opacity != value) { _opacity = value; OnPropertyChanged(); } }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
