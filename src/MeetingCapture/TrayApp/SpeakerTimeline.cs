using MeetingCapture.Transcription;

namespace MeetingCapture.TrayApp;

/// <summary>A talking interval on the shared meeting axis; both ends are fractions of the meeting length (0..1).</summary>
public readonly record struct SpeakerTimelineSpan(double Start, double End);

/// <summary>One speaker's row on the timeline: talking spans and total talk time.</summary>
public sealed record SpeakerTimelineRow(
    string SpeakerId,
    string DisplayName,
    IReadOnlyList<SpeakerTimelineSpan> Spans,
    TimeSpan TalkTime);

/// <summary>The meeting length and one row per speaker.</summary>
public sealed record SpeakerTimelineModel(TimeSpan Duration, IReadOnlyList<SpeakerTimelineRow> Rows)
{
    public static SpeakerTimelineModel Empty { get; } = new(TimeSpan.Zero, []);
}

/// <summary>
/// Projects transcript lines onto a per-speaker timeline. A transcript line only carries the time it
/// finished, so its span is estimated from the word count and bounded by the previous line.
/// </summary>
public static class SpeakerTimeline
{
    public const string UnknownSpeakerId = "Unknown";
    private const double SecondsPerWord = 0.4;
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSpan = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CoalesceGap = TimeSpan.FromSeconds(1.5);

    /// <param name="segments">Lines in spoken order.</param>
    /// <param name="startedAt">Meeting start; the left edge of the axis.</param>
    /// <param name="endedAt">Meeting end, or null while recording (the axis then ends at <paramref name="now"/>).</param>
    public static SpeakerTimelineModel Compute(
        IReadOnlyList<TranscriptionSegment> segments,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0) return SpeakerTimelineModel.Empty;

        var end = endedAt ?? Max(now, segments.Max(segment => segment.Timestamp));
        var duration = end - startedAt;
        var order = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var intervals = new Dictionary<string, List<(DateTimeOffset Start, DateTimeOffset End)>>(
            StringComparer.OrdinalIgnoreCase);

        var previousEnd = startedAt;
        foreach (var segment in segments)
        {
            var id = string.IsNullOrWhiteSpace(segment.SpeakerId) ? UnknownSpeakerId : segment.SpeakerId;
            if (!intervals.TryGetValue(id, out var list))
            {
                intervals[id] = list = [];
                order.Add(id);
            }
            if (!string.IsNullOrWhiteSpace(segment.SpeakerDisplayName)) names[id] = segment.SpeakerDisplayName;

            var spanEnd = Min(segment.Timestamp, end);
            var spanStart = Max(Max(spanEnd - EstimateSpan(segment.Text), previousEnd), startedAt);
            previousEnd = Max(previousEnd, spanEnd);
            if (spanEnd <= spanStart) continue;

            if (list.Count > 0 && spanStart - list[^1].End <= CoalesceGap)
                list[^1] = (list[^1].Start, Max(list[^1].End, spanEnd));
            else
                list.Add((spanStart, spanEnd));
        }

        var rows = order.Select(id =>
        {
            var talk = intervals[id].Aggregate(TimeSpan.Zero, (sum, item) => sum + (item.End - item.Start));
            var spans = duration <= TimeSpan.Zero
                ? []
                : intervals[id].Select(item => new SpeakerTimelineSpan(
                    (item.Start - startedAt) / duration, (item.End - startedAt) / duration)).ToArray();
            return new SpeakerTimelineRow(id, names.GetValueOrDefault(id, id), spans, talk);
        }).ToArray();
        return new SpeakerTimelineModel(duration < TimeSpan.Zero ? TimeSpan.Zero : duration, rows);
    }

    private static TimeSpan EstimateSpan(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var estimate = TimeSpan.FromSeconds(words * SecondsPerWord);
        return estimate < MinSpan ? MinSpan : estimate > MaxSpan ? MaxSpan : estimate;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
