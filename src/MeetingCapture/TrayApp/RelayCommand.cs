using System.Windows.Input;

namespace MeetingCapture.TrayApp;

/// <summary>Minimal ICommand used to wire H.NotifyIcon command properties to handlers.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    // Tray commands are always available; each action checks its own current state before doing work.
    public bool CanExecute(object? parameter) => true;

#pragma warning disable CS0067 // Required by ICommand; not raised because CanExecute is constant.
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public void Execute(object? parameter) => _execute();
}
