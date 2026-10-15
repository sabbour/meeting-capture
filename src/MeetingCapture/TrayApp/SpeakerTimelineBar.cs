using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace MeetingCapture.TrayApp;

/// <summary>
/// A light-gray track spanning the whole meeting with this speaker's talking intervals drawn on top.
/// Spans are fractions of the meeting length so every row shares the same time axis.
/// </summary>
public sealed class SpeakerTimelineBar : Grid
{
    private const double BarHeight = 10;
    private const double MinSegmentWidth = 2;

    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans), typeof(object), typeof(SpeakerTimelineBar),
        new PropertyMetadata(null, (d, _) => ((SpeakerTimelineBar)d).Render()));

    public static readonly DependencyProperty SegmentBrushProperty = DependencyProperty.Register(
        nameof(SegmentBrush), typeof(Brush), typeof(SpeakerTimelineBar),
        new PropertyMetadata(null, (d, _) => ((SpeakerTimelineBar)d).Render()));

    private readonly Canvas _segments = new() { Height = BarHeight };

    public SpeakerTimelineBar()
    {
        Height = BarHeight;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(BarHeight / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x9A, 0x9A, 0x9A)),
        });
        Children.Add(_segments);
        SizeChanged += (_, _) => Render();
    }

    public object? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    public Brush? SegmentBrush
    {
        get => (Brush?)GetValue(SegmentBrushProperty);
        set => SetValue(SegmentBrushProperty, value);
    }

    private void Render()
    {
        // Rebuild only the small overlay of talk intervals; the background track remains in place.
        _segments.Children.Clear();
        var width = ActualWidth;
        if (width <= 0 || Spans is not IEnumerable<SpeakerTimelineSpan> spans) return;

        foreach (var span in spans)
        {
            // Clamp model fractions to the track and keep very short intervals visible at this scale.
            var left = Math.Clamp(span.Start, 0, 1) * width;
            var right = Math.Clamp(span.End, 0, 1) * width;
            var segmentWidth = Math.Min(Math.Max(right - left, MinSegmentWidth), width - left);
            if (segmentWidth <= 0) continue;
            var rectangle = new Rectangle
            {
                Width = segmentWidth,
                Height = BarHeight,
                RadiusX = BarHeight / 2,
                RadiusY = BarHeight / 2,
                Fill = SegmentBrush,
            };
            Canvas.SetLeft(rectangle, left);
            _segments.Children.Add(rectangle);
        }
    }
}
