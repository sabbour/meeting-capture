using System.ComponentModel;

namespace MeetingCapture.TrayApp;

/// <summary>A checklist row (such as an action item) that raises change notifications when its completed state changes.</summary>
public sealed class ChecklistItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isCompleted;

    public required string Text { get; init; }

    public bool IsCompleted
    {
        get => _isCompleted;
        set
        {
            if (_isCompleted == value) return;
            _isCompleted = value;
            // The text style also depends on completion, so notify both bound values.
            Notify(nameof(IsCompleted));
            Notify(nameof(TextOpacity));
        }
    }

    public double TextOpacity => IsCompleted ? 0.6 : 1.0;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Builds checklist header text, such as the open and done counts.</summary>
internal static class ChecklistPresentation
{
    internal static string Header(string label, IEnumerable<ChecklistItemViewModel> items)
    {
        // Count completed items once, then derive the open count from the full list.
        var completed = items.Count(item => item.IsCompleted);
        return $"{label} ({items.Count() - completed} open, {completed} done)";
    }
}
