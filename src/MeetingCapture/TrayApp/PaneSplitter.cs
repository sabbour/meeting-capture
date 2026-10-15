using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeetingCapture.TrayApp;

/// <summary>Thin vertical drag handle that reports where its left edge should move, in parent coordinates.</summary>
internal sealed partial class PaneSplitter : Grid
{
    private double _grabOffset;
    private bool _dragging;

    public event EventHandler<double>? LeftEdgeRequested;
    public event EventHandler<double>? NudgeRequested;

    public PaneSplitter()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        KeyDown += OnKeyDown;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
            return;
        _dragging = true;
        _grabOffset = e.GetCurrentPoint(this).Position.X;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Parent is not UIElement parent)
            return;
        LeftEdgeRequested?.Invoke(this, e.GetCurrentPoint(parent).Position.X - _grabOffset);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var direction = e.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            _ => 0,
        };
        if (direction == 0)
            return;
        NudgeRequested?.Invoke(this, direction * TopicPaneSplit.KeyboardStep);
        e.Handled = true;
    }
}
