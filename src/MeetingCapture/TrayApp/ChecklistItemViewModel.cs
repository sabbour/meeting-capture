using System.ComponentModel;

namespace MeetingCapture.TrayApp;

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
            Notify(nameof(IsCompleted));
            Notify(nameof(TextOpacity));
        }
    }

    public double TextOpacity => IsCompleted ? 0.6 : 1.0;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class ChecklistPresentation
{
    internal static string Header(string label, IEnumerable<ChecklistItemViewModel> items)
    {
        var completed = items.Count(item => item.IsCompleted);
        return $"{label} ({items.Count() - completed} open, {completed} done)";
    }
}
