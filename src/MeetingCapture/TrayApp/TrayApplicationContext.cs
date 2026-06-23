using MeetingCapture.Pipeline;
using Microsoft.Extensions.Logging;

namespace MeetingCapture.TrayApp;

/// <summary>
/// WinForms ApplicationContext that hosts the system tray icon and routes
/// user actions to the MeetingPipeline.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext, IAsyncDisposable
{
    private readonly MeetingPipeline _pipeline;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startManualItem;
    private readonly ToolStripMenuItem _stopManualItem;

    public TrayApplicationContext(
        MeetingPipeline pipeline,
        ILogger<TrayApplicationContext> logger)
    {
        _pipeline = pipeline;
        _logger = logger;

        _statusItem = new ToolStripMenuItem("Watching for meetings...")
        {
            Enabled = false
        };

        _startManualItem = new ToolStripMenuItem("Start Manual Recording", null, OnStartManual);
        _stopManualItem = new ToolStripMenuItem("Stop Recording", null, OnStopManual)
        {
            Enabled = false
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_startManualItem);
        contextMenu.Items.Add(_stopManualItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Open Notes Folder", null, OnOpenNotesFolder);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, OnExit);

        _notifyIcon = new NotifyIcon
        {
            Text = "Meeting Capture",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        // Attempt to load icon; fall back to application icon gracefully
        TrySetIcon();

        _pipeline.StatusChanged += OnPipelineStatusChanged;
        _pipeline.NotesSaved += OnNotesSaved;

        // Start the detector without blocking the constructor
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartDetectorAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start meeting detector");
                UpdateStatus($"Detector error: {ex.Message}");
            }
        });
    }

    private void OnPipelineStatusChanged(object? sender, string status)
    {
        InvokeOnUiThread(() =>
        {
            UpdateStatus(status);
            _stopManualItem.Enabled = _pipeline.IsRunning;
            _startManualItem.Enabled = !_pipeline.IsRunning;
        });
    }

    private void OnNotesSaved(object? sender, string path)
    {
        InvokeOnUiThread(() =>
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                "Meeting Notes Saved",
                $"Notes written to:\n{Path.GetFileName(path)}",
                ToolTipIcon.Info);
        });
    }

    private void OnStartManual(object? sender, EventArgs e)
    {
        var title = $"Manual Recording — {DateTime.Now:yyyy-MM-dd HH:mm}";
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartManualAsync(title, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual start failed");
                InvokeOnUiThread(() => MessageBox.Show(
                    $"Failed to start recording:\n{ex.Message}",
                    "Meeting Capture",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error));
            }
        });
    }

    private void OnStopManual(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StopManualAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual stop failed");
                InvokeOnUiThread(() => MessageBox.Show(
                    $"Failed to stop recording:\n{ex.Message}",
                    "Meeting Capture",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error));
            }
        });
    }

    private void OnOpenNotesFolder(object? sender, EventArgs e)
    {
        // NotesPath is configured in AppSettings; we open the root meeting folder
        try
        {
            var path = AppDomain.CurrentDomain.BaseDirectory; // fallback
            // Read from DI-resolved storage if accessible; for now use env-based resolution
            var notesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Microsoft Scout", "Meetings");

            if (Directory.Exists(notesPath))
                System.Diagnostics.Process.Start("explorer.exe", notesPath);
            else
                MessageBox.Show("Notes folder does not exist yet.", "Meeting Capture",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open notes folder");
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _ = Task.Run(async () =>
        {
            await _pipeline.StopDetectorAsync(CancellationToken.None);
            await _pipeline.DisposeAsync();
        });
        Application.Exit();
    }

    private void UpdateStatus(string text)
    {
        _statusItem.Text = text.Length > 60 ? text[..57] + "..." : text;
        _notifyIcon.Text = $"Meeting Capture — {(text.Length > 40 ? text[..37] + "..." : text)}";
    }

    private void TrySetIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "tray.ico");
            if (File.Exists(iconPath))
                _notifyIcon.Icon = new Icon(iconPath);
            else
                _notifyIcon.Icon = SystemIcons.Application;
        }
        catch
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }
    }

    private static void InvokeOnUiThread(Action action)
    {
        if (Application.OpenForms.Count > 0)
        {
            var form = Application.OpenForms[0];
            if (form?.InvokeRequired == true)
            {
                form.Invoke(action);
                return;
            }
        }
        action();
    }

    public async ValueTask DisposeAsync()
    {
        _pipeline.StatusChanged -= OnPipelineStatusChanged;
        _pipeline.NotesSaved -= OnNotesSaved;
        await _pipeline.DisposeAsync();
        _notifyIcon.Dispose();
    }
}
