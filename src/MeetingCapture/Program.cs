using System.Runtime.InteropServices;
using MeetingCapture.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MeetingCapture;

/// <summary>
/// Custom entry point that enables single-instance behaviour: when the user clicks the
/// pinned taskbar icon while the app is already running, Windows launches a second process.
/// That second process detects the existing instance via <see cref="AppInstance"/>, sends a
/// redirect signal (so the main instance shows its flyout), and exits immediately — leaving
/// the user with one running copy of the app.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan ActivationRedirectTimeout = TimeSpan.FromSeconds(3);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--teams-roster-helper")
        {
            Console.Out.Write(WorkIq.TeamsUiAutomationRosterHelper.Inspect());
            return;
        }

        StartupPriority.StartKeepingNormalScheduling();
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var mainInstance = AppInstance.FindOrRegisterForKey("meeting-capture-main");

        if (!mainInstance.IsCurrent)
        {
            // A second instance was started (e.g. taskbar pinned icon clicked while running).
            // Redirect the activation to the main instance so it shows its flyout, then exit.
            // A crashed instance can leave an AppInstance registration behind briefly. Never
            // wait indefinitely for that stale activation target: start a recovery UI instead.
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var redirect = mainInstance.RedirectActivationToAsync(activationArgs).AsTask();
            if (Task.WhenAny(redirect, Task.Delay(ActivationRedirectTimeout))
                    .GetAwaiter().GetResult() == redirect)
            {
                redirect.GetAwaiter().GetResult();
                return;
            }

            System.Diagnostics.Trace.TraceWarning(
                "Meeting Capture activation redirect timed out; starting a recovery window.");
        }

        // We are the main instance. Listen for re-activation by subsequent instances.
        mainInstance.Activated += OnReactivated;

        using var responsiveness = new ResponsivenessDiagnostics(GetCurrentThreadId());
        using var startup = responsiveness.BeginUiPhase(ResponsivenessPhase.ApplicationStart);
        Application.Start(p =>
        {
            startup.Dispose();
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            responsiveness.StartMonitor(action =>
                dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, action.Invoke));
            var ctx = new DispatcherQueueSynchronizationContext(
                dispatcher);
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App(responsiveness);
        });
    }

    /// <summary>
    /// Called on a background thread when a second instance redirects its activation here.
    /// Marshals to the UI thread and shows the flyout.
    /// </summary>
    private static void OnReactivated(object? sender, AppActivationArguments e)
    {
        if (Application.Current is not App app)
            return;

        app.AppDispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Normal,
            () => app.ShowMainWindow());
    }
}
