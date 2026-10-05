using MeetingCapture.AudioCapture;
using MeetingCapture.Configuration;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using MeetingCapture.TrayApp;
using MeetingCapture.WorkIq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using H.NotifyIcon;

namespace MeetingCapture;

/// <summary>
/// WinUI 3 application entry point. Builds the DI container, hosts the system tray
/// icon (H.NotifyIcon), routes user actions and pipeline events, and owns the
/// hidden MainWindow which the user opens from the tray.
/// </summary>
public partial class App : Application
{
    private IServiceProvider _services = null!;
    private DispatcherQueue _dispatcherQueue = null!;

    private MeetingPipeline _pipeline = null!;
    private ILogger<App> _logger = null!;
    private AppSettings _settings = null!;
    private IActiveMeetingIdentityProvider _meetingIdentity = null!;

    private TaskbarIcon? _trayIcon;
    private MenuFlyout _contextMenu = null!;
    private MenuFlyoutItem _statusItem = null!;
    private MenuFlyoutItem _startManualItem = null!;
    private MenuFlyoutItem _stopManualItem = null!;
    private MenuFlyoutItem _takeNotesItem = null!;
    private MenuFlyoutItem _dismissItem = null!;
    private MenuFlyoutSeparator _pendingSeparator = null!;

    private MainWindow? _mainWindow;
    private MeetingSession? _pendingSession;
    private DispatcherQueueTimer? _dismissTimer;
    private CancellationTokenSource? _pendingTitleCts;
    private bool _recordingNotificationShown; // guard: show "Recording started" only once per session
    private long _statusSequence = -1;
    private bool _exiting;

    /// <summary>Convenience accessor for the running application's service provider.</summary>
    public static IServiceProvider Services => ((App)Current)._services;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _services = BuildServiceProvider();

        _pipeline = _services.GetRequiredService<MeetingPipeline>();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _settings = _services.GetRequiredService<IOptions<AppSettings>>().Value;
        _meetingIdentity = _services.GetRequiredService<IActiveMeetingIdentityProvider>();

        ValidateAndWarnSettings(_settings, _services.GetRequiredService<ILogger<App>>());

        // MainWindow: create without hiding. The tray icon is published only after the
        // first view load so its callbacks never compete with WinUI startup.
        // Do NOT call AppWindow.Hide() before the first Show — doing so pauses the
        // WinUI 3 / DirectComposition compositor; the subsequent AppWindow.Show() call
        // in ShowFromTray() resumes it cleanly. Never use SetWindowPos-only to show a
        // window that was hidden via AppWindow.Hide().
        _mainWindow = _services.GetRequiredService<MainWindow>();
        _mainWindow.MeetingConfirmRequested += (_, session) => ConfirmPendingSession(session);
        _mainWindow.MeetingDismissRequested += (_, _) => DismissPendingSession();
        _mainWindow.MeetingSnoozeRequested += (_, _) => SnoozeDetection();
        _mainWindow.InitialViewReady += OnInitialViewReady;

        _pipeline.StateChanged += OnPipelineStatusChanged;
        _pipeline.NotesSaved += OnNotesSaved;
        _pipeline.MeetingDetected += OnMeetingDetected;
        _pipeline.PendingMeetingCancelled += OnPendingMeetingCancelled;

        // Startup must use the activating window path. The tray flyout path intentionally
        // auto-hides on focus loss, which can make a fresh launch immediately headless.
        ShowMainWindow();
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, StartDetectorInBackground))
            StartDetectorInBackground();
        RecoverPendingVisualizationsInBackground();
    }

    private void OnInitialViewReady(object? sender, EventArgs e)
    {
        if (_mainWindow is not null)
            _mainWindow.InitialViewReady -= OnInitialViewReady;
        if (_trayIcon is null && !_exiting)
            BuildTrayIcon();
    }

    /// <summary>Retries visualization sidecars that a previous exit could not write.</summary>
    private void RecoverPendingVisualizationsInBackground()
    {
        var journal = _services.GetRequiredService<IPendingVisualizationJournal>();
        var storage = _services.GetRequiredService<IMeetingVisualizationStorage>();
        _ = Task.Run(async () =>
        {
            try
            {
                var recovered = await journal.DrainAsync(storage, CancellationToken.None);
                if (recovered.Count > 0)
                    _logger.LogInformation(
                        "Recovered {Count} deferred meeting visualization file(s) from the previous session.",
                        recovered.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Deferred meeting visualization recovery did not finish ({Type}); it will be retried later.",
                    ex.GetType().Name);
            }
        });
    }

    // ── DI container ────────────────────────────────────────────────────────────

    private void StartDetectorInBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartDetectorAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError("Meeting detector startup failed ({Type}).", ex.GetType().Name);
                InvokeOnUi(() => UpdateStatus("Meeting detector failed. Manual recording is still available."));
            }
        });
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var userSettingsPath = UserSettingsManager.SettingsFilePath;

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(userSettingsPath, optional: true, reloadOnChange: false)
            .AddUserSecrets<App>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IConfigurationRoot>(config);

        services.Configure<AppSettings>(config);

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IMeetingDetector, ProcessMeetingDetector>();

        var audioSettings = config.GetSection("Audio").Get<AudioSettings>() ?? new AudioSettings();
        if (audioSettings.DualCapture && audioSettings.CaptureLoopback && audioSettings.CaptureMicrophone)
        {
            // Dual capture: a single IAudioCapture that owns both child sources and mixes
            // their PCM on one clock. The children are registered as concrete types for constructor injection.
            services.AddSingleton<LoopbackCapture>();
            services.AddSingleton<MicrophoneCapture>();
            services.AddSingleton<IAudioCapture, DualSourceCapture>();
        }
        else
        {
            if (audioSettings.CaptureMicrophone)
                services.AddSingleton<IAudioCapture, MicrophoneCapture>();
            if (audioSettings.CaptureLoopback)
                services.AddSingleton<IAudioCapture, LoopbackCapture>();
        }

        services.AddSingleton<ITranscriptionService, AzureSpeechTranscriptionService>();
        services.AddSingleton<AiUsageMonitor>();
        services.AddSingleton<AiDiagnosticsStream>();
        services.AddSingleton<CopilotNotesService>();
        services.AddSingleton<INotesIntelligence>(sp => sp.GetRequiredService<CopilotNotesService>());
        services.AddSingleton<IMeetingVisualizationIntelligence>(
            sp => sp.GetRequiredService<CopilotNotesService>());
        services.AddSingleton<INotesStorage, MarkdownNotesStorage>();
        services.AddSingleton<MeetingVisualizationSession>();
        services.AddSingleton<IMeetingVisualizationStorage, JsonMeetingVisualizationStorage>();
        services.AddSingleton<IPendingVisualizationJournal>(sp =>
            new FilePendingVisualizationJournal(diagnostics: sp.GetRequiredService<AiDiagnosticsStream>()));
        services.AddSingleton<CopilotModelCatalog>();

        services.AddSingleton<MeetingCapture.Graph.SpeakerResolver>();
        services.AddSingleton<WorkIqService>();
        services.AddSingleton<IRelatedContextSource>(sp => sp.GetRequiredService<WorkIqService>());
        services.AddSingleton<RelatedContextCoordinator>();
        services.AddSingleton<IActiveRosterProvider, TeamsUiAutomationRosterProvider>();
        services.AddSingleton<IActiveMeetingIdentityProvider, TeamsUiAutomationMeetingIdentityProvider>();
        services.AddSingleton<Func<CancellationToken, Task<string?>>>(_ => _ => Task.FromResult<string?>(null));

        services.AddSingleton<MeetingPipeline>();
        services.AddSingleton(_ => new UserSettingsManager(config));
        services.AddSingleton<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<MeetingPipeline>(),
            sp.GetRequiredService<WorkIqService>(),
            sp.GetRequiredService<IOptions<AppSettings>>(),
            sp.GetRequiredService<ILogger<MainWindow>>(),
            sp.GetRequiredService<INotesIntelligence>(),
            sp.GetRequiredService<UserSettingsManager>(),
            sp.GetRequiredService<RelatedContextCoordinator>(),
            sp.GetRequiredService<IActiveRosterProvider>(),
            sp.GetRequiredService<MeetingVisualizationSession>(),
            sp.GetRequiredService<IMeetingVisualizationStorage>(),
            sp.GetRequiredService<CopilotModelCatalog>()));

        return services.BuildServiceProvider();
    }

    // ── Tray icon ─────────────────────────────────────────────────────────────

    private void BuildTrayIcon()
    {
        System.Windows.Input.ICommand Command(Action action) => new RelayCommand(() => InvokeOnUi(action));
        _statusItem = new MenuFlyoutItem { Text = "Watching for meetings...", IsEnabled = false };

        _startManualItem = new MenuFlyoutItem { Text = "Start Manual Recording" };
        _startManualItem.Command = Command(OnStartManual);

        _stopManualItem = new MenuFlyoutItem { Text = "Stop Recording", IsEnabled = false };
        _stopManualItem.Command = Command(OnStopManual);

        _takeNotesItem = new MenuFlyoutItem { Text = "Take notes for: meeting", Visibility = Visibility.Collapsed };
        _takeNotesItem.Command = Command(() =>
        {
            if (_pendingSession is not null)
                ConfirmPendingSession(_pendingSession);
        });

        _dismissItem = new MenuFlyoutItem { Text = "Not a meeting (10 min)", Visibility = Visibility.Collapsed };
        _dismissItem.Command = Command(DismissPendingSession);

        _pendingSeparator = new MenuFlyoutSeparator { Visibility = Visibility.Collapsed };

        var openItem = new MenuFlyoutItem { Text = "Open Meeting Capture" };
        openItem.Command = Command(() => ShowMainWindow());

        var openNotesItem = new MenuFlyoutItem { Text = "Open Notes Folder" };
        openNotesItem.Command = Command(OnOpenNotesFolder);

        var settingsItem = new MenuFlyoutItem { Text = "Settings" };
        settingsItem.Command = Command(ShowMainWindowThenSettings);

        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Command = Command(OnExit);

        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 280d));
        _contextMenu = new MenuFlyout
        {
            AreOpenCloseAnimationsEnabled = false,
            MenuFlyoutPresenterStyle = presenterStyle
        };
        _contextMenu.Items.Add(_statusItem);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        _contextMenu.Items.Add(openItem);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        _contextMenu.Items.Add(_takeNotesItem);
        _contextMenu.Items.Add(_dismissItem);
        _contextMenu.Items.Add(_pendingSeparator);
        _contextMenu.Items.Add(_startManualItem);
        _contextMenu.Items.Add(_stopManualItem);
        var snoozeItem = new MenuFlyoutItem { Text = "Snooze detection for 30 minutes" };
        snoozeItem.Command = Command(SnoozeDetection);
        var resumeItem = new MenuFlyoutItem { Text = "Resume meeting detection" };
        resumeItem.Command = Command(() => _pipeline.ResumeDetection());
        _contextMenu.Items.Add(snoozeItem);
        _contextMenu.Items.Add(resumeItem);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        _contextMenu.Items.Add(openNotesItem);
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(new MenuFlyoutSeparator());
        _contextMenu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Meeting Capture \u2014 Watching...",
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = _contextMenu,
            LeftClickCommand = new TrayApp.RelayCommand(() => ShowMainWindow(fromTray: true)),
        };

        TrySetTrayIconSource();

        _trayIcon.ForceCreate();
    }

    private void TrySetTrayIconSource()
    {
        if (_trayIcon is null) return;
        // H.NotifyIcon reads the stream and calls new Icon(stream, size) — ICO format required.
        // PNG throws a silent exception in the async partial void handler.
        // ms-appx scheme: H.NotifyIcon prepends AppContext.BaseDirectory to LocalPath.
        _trayIcon.IconSource = new BitmapImage(new Uri("ms-appx:///Resources/tray.ico"));
    }

    // ── Window helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a manual recording for a calendar meeting chosen from the flyout's
    /// "Today's meetings" list, pre-filling the session title with the meeting subject.
    /// </summary>
    public void StartRecordingForMeeting(string title)
    {
        if (!_pipeline.CanStart || _exiting) return;
        var recordingTitle = string.IsNullOrWhiteSpace(title)
            ? $"Manual Recording \u2014 {DateTime.Now:yyyy-MM-dd HH:mm}"
            : title;

        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartManualAsync(recordingTitle, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ReportStartFailure("Start recording", ex);
            }
        });
    }

    public DispatcherQueue AppDispatcherQueue => _dispatcherQueue;

    public void ShowMainWindow(bool fromTray = false)
    {
        if (_mainWindow is null) return;
        void Show()
        {
            if (fromTray)
                _mainWindow.ShowFromTray();
            else
            {
                _mainWindow.PositionFlyout();
                _mainWindow.AppWindow.Show();
                _mainWindow.Activate();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                NativeMethods.SetForegroundWindow(hwnd);
                _mainWindow.RefreshTodaysMeetings();
            }
        }

        if (_dispatcherQueue.HasThreadAccess)
            Show();
        else if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, Show))
            _logger.LogWarning("Window activation could not be queued.");
    }

    private void ShowMainWindowThenSettings()
    {
        if (_mainWindow is null) return;
        InvokeOnUi(() =>
        {
            _mainWindow.PositionFlyout();
            _mainWindow.AppWindow.Show();
            _mainWindow.Activate();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
            NativeMethods.SetForegroundWindow(hwnd);
            _mainWindow.ShowSettings();
        });
    }

    // ── Pipeline event handlers ─────────────────────────────────────────────────

    private void OnPipelineStatusChanged(object? sender, PipelineStatus status)
    {
        InvokeOnUi(() =>
        {
            if (status.Sequence <= _statusSequence) return;
            _statusSequence = status.Sequence;
            UpdateStatus(status.Message);
            _stopManualItem.IsEnabled = _pipeline.CanStop && !_exiting;
            _startManualItem.IsEnabled = _pipeline.CanStart && !_exiting;

            if (status.State == PipelineState.Recording && !_recordingNotificationShown)
            {
                _recordingNotificationShown = true;
                _trayIcon?.ShowNotification("Meeting Capture", "Recording started");
            }
            else if (status.State is PipelineState.Saved or PipelineState.Failed)
            {
                _recordingNotificationShown = false;
                if (status.Error is not null || status.IntelligenceFailure is not null)
                {
                    _trayIcon?.ShowNotification("Meeting Capture \u2014 Attention", status.Message);
                }
            }
        });
    }

    private void OnNotesSaved(object? sender, NotesSavedInfo saved)
    {
        InvokeOnUi(() =>
        {
            if (saved.SessionId != _pipeline.Status.SessionId) return;
            _trayIcon?.ShowNotification("Meeting Notes Saved", Path.GetFileName(saved.Path));
        });
    }

    private void OnMeetingDetected(object? sender, MeetingSession session)
    {
        InvokeOnUi(() =>
        {
            if (!_pipeline.CanStart || _exiting || !session.IsActive || _pendingSession?.Id == session.Id)
                return;

            CancelPendingTitleLookup();
            _pendingSession = session;

            _takeNotesItem.Text = $"Take notes for: {session.Title}";
            _takeNotesItem.Visibility = Visibility.Visible;
            _dismissItem.Visibility = Visibility.Visible;
            _pendingSeparator.Visibility = Visibility.Visible;

            _mainWindow?.ShowMeetingPrompt(session);

            _dismissTimer?.Stop();
            _dismissTimer = _dispatcherQueue.CreateTimer();
            _dismissTimer.Interval = TimeSpan.FromSeconds(60);
            _dismissTimer.IsRepeating = false;
            _dismissTimer.Tick += (_, _) => DismissPendingSession();
            _dismissTimer.Start();
            _pendingTitleCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _ = ResolvePendingTitleAsync(session, _pendingTitleCts.Token);
        });
    }

    private async Task ResolvePendingTitleAsync(MeetingSession session, CancellationToken ct)
    {
        var window = _mainWindow!;
        var revision = window.CalendarRevision;
        string? title = null;
        string? titleSource = null;
        var titleFromActiveCall = false;
        MeetingTitleCandidate? calendarMeeting = null;
        string? unavailable = null;
        if (MeetingTitleResolver.AppLabel(session.SourceProcess) == "Microsoft Teams")
        {
            var identity = await _meetingIdentity.GetSnapshotAsync(ct).ConfigureAwait(false);
            if (identity.IsVerified)
            {
                title = identity.Title;
                titleSource = identity.Limitation;
                titleFromActiveCall = true;
            }
        }
        try
        {
            var meetings = await window.GetCalendarCandidatesAsync(ct).ConfigureAwait(false);
            var resolution = MeetingTitleResolver.ResolveWithReason(session.SourceProcess, session.StartedAt, meetings);
            calendarMeeting = resolution.Meeting;
            if (title is null)
            {
                title = resolution.Title;
                titleSource = resolution.Detail;
            }
            else if (calendarMeeting is null)
                unavailable = resolution.Detail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            unavailable = "Calendar lookup cancelled or timed out; meeting name unavailable.";
            _logger.LogInformation("Pending meeting title lookup cancelled or timed out.");
        }
        catch (Exception ex)
        {
            unavailable = ex is WorkIqException workIq ? workIq.Message
                : "WorkIQ calendar is unavailable. Check calendar sign-in and access.";
            _logger.LogWarning("Pending meeting title lookup failed ({Type}).", ex.GetType().Name);
        }
        InvokeOnUi(() =>
        {
            // Dismissal, replacement, confirmation and process exit all invalidate this exact prompt.
            if (!ReferenceEquals(_pendingSession, session) || !session.IsActive || !_pipeline.CanStart || _exiting)
                return;
            if (revision != window.CalendarRevision)
            {
                if (!titleFromActiveCall) title = null;
                calendarMeeting = null;
                unavailable = "Calendar connection changed; meeting name unavailable.";
            }
            if (!ct.IsCancellationRequested && title is not null)
                session.TrySetPendingTitle(_pendingSession.Id, title, titleSource ?? session.TitleSource);
            if (!ct.IsCancellationRequested && calendarMeeting is not null)
                session.TryAttachCalendarMeeting(_pendingSession.Id, calendarMeeting);
            _takeNotesItem.Text = $"Take notes for: {session.Title}";
            var source = unavailable is null ? session.TitleSource
                : $"{session.TitleSource} {unavailable}";
            _mainWindow?.UpdateMeetingPrompt(session, source);
            // One notification per detection, after bounded enrichment; updates never issue another prompt.
            var notification = $"{session.Title}\n{source}";
            _trayIcon?.ShowNotification($"Possible meeting in {MeetingTitleResolver.AppLabel(session.SourceProcess)}",
                notification.Length > 240 ? notification[..237] + "..." : notification);
        });
    }

    private void CancelPendingTitleLookup()
    {
        _pendingTitleCts?.Cancel();
        _pendingTitleCts?.Dispose();
        _pendingTitleCts = null;
    }

    private void OnPendingMeetingCancelled(object? sender, MeetingSession session)
    {
        InvokeOnUi(() => { if (_pendingSession?.Id == session.Id) HidePendingPrompt(); });
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    private void ConfirmPendingSession(MeetingSession session)
    {
        if (!_pipeline.CanStart || _exiting || _pendingSession?.Id != session.Id) return;
        session.FreezeTitle();
        HidePendingPrompt();
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.ConfirmAndStartAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ReportStartFailure("Start confirmed recording", ex);
            }
        });
    }

    private void DismissPendingSession()
    {
        if (_pendingSession is { } pending)
            _pipeline.DismissPendingMeeting(pending.Id, TimeSpan.FromMinutes(10));
        HidePendingPrompt();
    }

    private void SnoozeDetection()
    {
        _pipeline.SnoozeDetection(TimeSpan.FromMinutes(30));
        HidePendingPrompt();
    }

    private void HidePendingPrompt()
    {
        InvokeOnUi(() =>
        {
            CancelPendingTitleLookup();
            _pendingSession = null;
            _mainWindow?.HideMeetingPrompt();
            _takeNotesItem.Visibility = Visibility.Collapsed;
            _dismissItem.Visibility = Visibility.Collapsed;
            _pendingSeparator.Visibility = Visibility.Collapsed;
            _dismissTimer?.Stop();
            _dismissTimer = null;
        });
    }

    private void OnStartManual()
    {
        if (!_pipeline.CanStart || _exiting) return;
        _startManualItem.IsEnabled = false;
        var title = $"Manual Recording \u2014 {DateTime.Now:yyyy-MM-dd HH:mm}";
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartManualAsync(title, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ReportStartFailure("Start recording", ex);
            }
            finally { InvokeOnUi(() => _startManualItem.IsEnabled = _pipeline.CanStart && !_exiting); }
        });
    }

    private void OnStopManual()
    {
        if (!_pipeline.CanStop || _exiting) return;
        _stopManualItem.IsEnabled = false;
        UpdateStatus("Stopping capture...");
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StopManualAsync();
            }
            catch (Exception ex)
            {
                ReportActionFailure("Stop recording", ex);
            }
            finally { InvokeOnUi(() => _stopManualItem.IsEnabled = _pipeline.CanStop && !_exiting); }
        });
    }

    private void OnOpenNotesFolder()
    {
        try
        {
            var path = _settings.Storage.NotesPath;
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
            else
                _trayIcon?.ShowNotification("Meeting Capture", "Notes folder does not exist yet.");
        }
        catch (Exception ex)
        {
            ReportActionFailure("Open notes folder", ex);
        }
    }

    private void OnExit()
    {
        if (_exiting) return;
        _exiting = true;
        _startManualItem.IsEnabled = _stopManualItem.IsEnabled = false;
        HidePendingPrompt();
        _mainWindow?.PrepareForExit();
        UpdateStatus("Finishing capture before exit...");
        _ = Task.Run(async () =>
        {
            try
            {
                var readiness = await _pipeline.PrepareForExitAsync();
                if (readiness.HasDeferredWork)
                {
                    _logger.LogWarning(
                        "Exit completed with deferred visualization work ({Deferral}, {ErrorType}).",
                        readiness.Deferral, readiness.ErrorType ?? "unknown");
                    InvokeOnUi(() => _trayIcon?.ShowNotification("Meeting Capture", readiness.Message!));
                }
            }
            catch (Exception ex)
            {
                ReportActionFailure("Shutdown", ex);
                InvokeOnUi(() =>
                {
                    _exiting = false;
                    _mainWindow?.ExitFailed();
                    _startManualItem.IsEnabled = _pipeline.CanStart;
                    _stopManualItem.IsEnabled = _pipeline.CanStop;
                });
                return;
            }
            try
            {
                await ShutdownCleanup.RunAsync(async () =>
                {
                    await _pipeline.StopDetectorAsync(CancellationToken.None);
                    if (_services is IAsyncDisposable asyncServices)
                        await asyncServices.DisposeAsync();
                    else if (_services is IDisposable services)
                        services.Dispose();
                }, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Shutdown cleanup did not finish after capture was saved ({Type}).", ex.GetType().Name);
                InvokeOnUi(() => _trayIcon?.ShowNotification("Meeting Capture",
                    "Transcript saved. Background cleanup did not finish; closing the app."));
            }
            InvokeOnUi(() =>
            {
                _pipeline.StateChanged -= OnPipelineStatusChanged;
                _pipeline.NotesSaved -= OnNotesSaved;
                _pipeline.MeetingDetected -= OnMeetingDetected;
                _pipeline.PendingMeetingCancelled -= OnPendingMeetingCancelled;
                _trayIcon?.Dispose();
                _trayIcon = null;
                _mainWindow?.CloseForReal();
                Current.Exit();
            });
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateStatus(string text)
    {
        var shortText = text.Length > 60 ? text[..57] + "..." : text;
        _statusItem.Text = shortText;
        var prefix = _pipeline.State == PipelineState.Recording ? "\uD83D\uDD34 Recording" : "Meeting Capture";
        var tooltip = $"{prefix} \u2014 {(text.Length > 40 ? text[..37] + "..." : text)}";
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private void InvokeOnUi(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }

    /// <summary>Surfaces the specific, sanitized reason a recording could not start.</summary>
    private void ReportStartFailure(string action, Exception error)
    {
        var failure = _pipeline.DescribeStartFailure(error);
        _logger.LogError("{Action} failed ({Type}) classified as {StartFailureKind}.",
            action, error.GetType().Name, failure.Kind);
        InvokeOnUi(() => _trayIcon?.ShowNotification("Meeting Capture", failure.Message));
    }

    private void ReportActionFailure(string action, Exception error)
    {
        _logger.LogError("{Action} failed ({Type}).", action, error.GetType().Name);
        InvokeOnUi(() => _trayIcon?.ShowNotification("Meeting Capture", $"{action} failed. Check the recording status and settings."));
    }

    private static void ValidateAndWarnSettings(AppSettings settings, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(settings.Transcription.ApiKey)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")))
        {
            logger.LogWarning(
                "No Foundry Speech key found. Set Transcription:ApiKey via user-secrets or AZURE_SPEECH_KEY env var. " +
                "Transcription will fail at runtime.");
        }

        if (string.IsNullOrWhiteSpace(settings.Transcription.Endpoint))
        {
            logger.LogWarning(
                "No Foundry Speech endpoint configured. Set Transcription:Endpoint in appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(settings.Intelligence.ApiKey)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_FOUNDRY_KEY"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")))
        {
            logger.LogWarning(
                "No Foundry key found for notes intelligence. " +
                "Set Intelligence:ApiKey via user-secrets, or AZURE_FOUNDRY_KEY / AZURE_OPENAI_API_KEY env var. " +
                "Note synthesis will fail at runtime.");
        }

        if (string.IsNullOrWhiteSpace(settings.Intelligence.Endpoint))
        {
            logger.LogWarning(
                "No Foundry endpoint configured for notes intelligence. " +
                "Set Intelligence:Endpoint in appsettings.json.");
        }

        if (!Directory.Exists(settings.Storage.NotesPath))
        {
            try
            {
                Directory.CreateDirectory(settings.Storage.NotesPath);
                logger.LogInformation("Created notes directory: {Path}", settings.Storage.NotesPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Cannot create notes directory {Path}", settings.Storage.NotesPath);
            }
        }
    }
}
