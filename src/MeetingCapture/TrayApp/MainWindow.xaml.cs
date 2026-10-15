using MeetingCapture.Configuration;
using MeetingCapture.Diagnostics;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using MeetingCapture.WorkIq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MeetingCapture.TrayApp;

/// <summary>
/// Primary application window – shows the live transcript and manual recording
/// controls using WinUI 3 / FluentUI 2 controls. Closing (X) hides the window;
/// the app continues to live in the system tray.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly MeetingPipeline _pipeline;
    private readonly ResponsivenessDiagnostics _responsiveness;
    private readonly WorkIqService _workIq;
    private CancellationTokenSource? _workIqCts;
    private CancellationTokenSource? _calendarCts;
    private bool _workIqRestoring;
    private bool _calendarBusy;
    private long _calendarGeneration;
    private readonly WorkIqConnectionStatus _workIqConnection = new();
    private WorkIqControlState? _renderedWorkIqControls;
    public long CalendarRevision => Interlocked.Read(ref _calendarGeneration);
    private readonly AppSettings _settings;
    private readonly ILogger<MainWindow> _logger;
    private readonly INotesIntelligence _intelligence;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly InputNonClientPointerSource _titleBarPointerSource;
    private RectInt32[] _titleBarInteractiveRects = [];
    private readonly UserSettingsManager _settingsManager;
    private readonly RelatedContextCoordinator _relatedContext;
    private readonly IActiveRosterProvider _activeRoster;
    private readonly MeetingVisualizationSession _visualization;
    private readonly IMeetingVisualizationStorage _visualizationStorage;
    private readonly CopilotModelCatalog _copilotModelCatalog;
    private readonly PanelRefreshState _summaryState = new();
    private readonly PanelRefreshState _questionsState = new();
    private readonly PanelRefreshState _actionsState = new();
    private readonly Dictionary<PanelRefreshState, DateTimeOffset> _panelTranscriptThrough = new();
    private bool _hasSummary;
    private readonly TranscriptViewport _viewport = new();
    private ScrollViewer? _transcriptScroller;
    private int _transcriptScrollQueued;
    private string? _sessionId;
    private DateTimeOffset? _startedAt;
    private long _statusSequence = -1;
    private long _transcriptRevision = -1;
    private int _finalizedSegmentCount;
    private string? _lastFinalSpeakerId;
    private SessionPartialTranscript? _pendingPartialTranscript;
    private int _partialUiUpdateQueued;
    private DispatcherQueueTimer? _partialTranscriptRetryTimer;
    private bool _preview;
    private bool _previewBusy;
    private bool _retryBusy;
    private CancellationTokenSource? _retryCts;
    private string? _notesPath;
    private DispatcherQueueTimer? _healthTimer;
    private string? _aiUsageTransitionKey;
    private int _aiUsageRenderQueued;

    private readonly TranscriptCollectionState<TranscriptLine> _transcriptState =
        new(TranscriptViewport.Capacity);
    private ObservableCollection<TranscriptLine> _transcript => _transcriptState.Rows;
    private readonly ObservableCollection<ContextEvidenceSourceViewModel> _relatedContextItems = new();
    private readonly ObservableCollection<ParticipantChip> _liveParticipants = new();
    private readonly ObservableCollection<ParticipantChip> _scheduledInvitees = new();
    private readonly ObservableCollection<ContextEvidenceSourceViewModel> _catchUpSources = new();
    private readonly ObservableCollection<CatchUpSection> _catchUpSections = new();
    private DateTimeOffset? _rosterCheckedAt;
    private bool _catchUpRendered;
    private CancellationTokenSource? _contextCts;
    private Task? _rosterRefreshTask;
    private bool _rosterChecked;
    private string? _acceptedRelatedContextTopic;
    private int _topicMapAddedWidth;
    private const int TopicMapPanelWidth = 420;
    private MeetingVisualizationDocument? _loadedVisualization;
    private MeetingVisualizationDocument? _displayedVisualizationDocument;
    private VisualizationProjection? _visualizationProjection;
    private string? _selectedVisualizationTopicId;
    private string? _renderedVisualizationFingerprint;
    private string? _currentVisualizationTopicId;
    private int _visualizationAnalysisPending;
    private bool _visualizationExpanded;
    private bool _visualizationMaximizedWindow;
    private readonly bool _animationsEnabled;
    private readonly ObservableCollection<TrailItemViewModel> _visualizationTrail = new();
    private readonly ObservableCollection<MeetingTopicNotesViewModel> _meetingNotes = new();
    private readonly HashSet<string> _collapsedTopicIds = new(StringComparer.Ordinal);
    private Button? _brainMapRootButton;
    private readonly Dictionary<string, Button> _brainMapNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Microsoft.UI.Xaml.Shapes.Path> _brainMapConnectors =
        new(StringComparer.Ordinal);
    private bool _visualizationViewportSubscribed;
    private int _meetingNotesProjectionRevision;
    private string? _meetingNotesSessionId;
    private bool _initialViewReadyRaised;
    private readonly Dictionary<string, RelatedContextState> _relatedContextStatesByTopicId =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _appliedRelatedContextSnapshots = new(StringComparer.Ordinal);

    // Recording-state intelligence panels.
    private readonly ObservableCollection<SpeakerBalance> _speakerBalance = new();
    private readonly ObservableCollection<ChecklistItemViewModel> _questions = new();
    private readonly Dictionary<string, ChecklistItemViewModel> _allQuestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ChecklistItemViewModel> _actions = new();
    private readonly Dictionary<string, ChecklistItemViewModel> _allActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _speakerWordCounts = new(StringComparer.OrdinalIgnoreCase);
    private int _totalWords;

    // Idle-state calendar list.
    private readonly ObservableCollection<MeetingRow> _todaysMeetings = new();
    private readonly ObservableCollection<MeetingRow> _otherMeetings = new();
    private readonly ObservableCollection<MeetingRow> _pastMeetings = new();
    private List<MeetingTitleCandidate> _calendarMeetings = [];
    private readonly object _calendarCandidateGate = new();
    private List<MeetingTitleCandidate> _latestCalendarCandidates = [];
    private DateTimeOffset _calendarRenderedAt;
    private bool _showCompletedSession = true;
    private bool _preferencesFromRecording;

    // Live time indicator + auto-dismissing status InfoBar timers.
    private DispatcherQueueTimer? _recordingTimer;
    private DispatcherQueueTimer? _tldrTimer;
    private DispatcherQueueTimer? _intelligenceTimer;
    private DispatcherQueueTimer? _statusBarDismissTimer;
    private DispatcherQueueTimer? _saveReviewTimer;
    private DateTimeOffset? _saveReviewDeadline;
    private string? _saveReviewSessionId;
    private bool _saveDecisionSubmitted;
    private CancellationTokenSource? _recordingCts;
    private string? _recordingNoticeSessionId;
    private const string RecordingNoticeText =
        "Transcription is on. This meeting is being transcribed to create notes.";

    // Speaker-assignment banner state. Unknown speakers are queued and prompted one at a time.
    private readonly Queue<string> _pendingUnknownSpeakers = new();
    private readonly List<SpeakerNameOption> _speakerNameOptions = [];
    private readonly Dictionary<string, ImageSource> _speakerAvatarImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _speakerAvatarRevisions = new(StringComparer.OrdinalIgnoreCase);
    private long _speakerNameRevision;
    private SpeakerNameOption? _selectedSpeakerNameOption;
    private string? _currentPromptSpeakerId;

    // Per-speaker colour assignment so each diarized speaker (Guest-1, Guest-2, …)
    // is visually distinct. Colours are handed out in order of first appearance.
    private readonly Dictionary<string, Brush> _speakerBrushes = new();
    private static readonly Color[] SpeakerPalette =
    {
        Color.FromArgb(255, 0x4F, 0x9C, 0xF5), // blue
        Color.FromArgb(255, 0x5B, 0xC2, 0x86), // green
        Color.FromArgb(255, 0xE0, 0x8C, 0x3B), // orange
        Color.FromArgb(255, 0xB4, 0x7B, 0xE0), // purple
        Color.FromArgb(255, 0xE0, 0x5B, 0x7B), // pink
        Color.FromArgb(255, 0x3B, 0xC4, 0xC4), // teal
    };

    private MeetingSession? _promptSession;
    private bool _allowClose;
    private bool _preparingExit;
    private bool _suppressAutoDismiss;
    private bool _isPinned;
    private readonly Microsoft.UI.Windowing.OverlappedPresenter _presenter;
    private static readonly UIntPtr WindowSubclassId = new(1);
    // Kept in a field so the native callback is not garbage collected while the window lives.
    private NativeMethods.SubclassProc? _windowMessageProc;
    private CancellationTokenSource? _suppressReleaseCts; // cancels stale suppress-release timers

    /// <summary>Raised when the user confirms the pending meeting prompt.</summary>
    public event EventHandler<MeetingSession>? MeetingConfirmRequested;

    /// <summary>Raised when the user dismisses the pending meeting prompt.</summary>
    public event EventHandler<EventArgs>? MeetingDismissRequested;
    public event EventHandler<EventArgs>? MeetingSnoozeRequested;
    public event EventHandler<EventArgs>? InitialViewReady;

    public MainWindow(
        MeetingPipeline pipeline,
        WorkIqService workIq,
        IOptions<AppSettings> settings,
        ILogger<MainWindow> logger,
        INotesIntelligence intelligence,
        UserSettingsManager settingsManager,
        RelatedContextCoordinator relatedContext,
        IActiveRosterProvider activeRoster,
        MeetingVisualizationSession visualization,
        IMeetingVisualizationStorage visualizationStorage,
        CopilotModelCatalog copilotModelCatalog,
        ResponsivenessDiagnostics responsiveness)
    {
        _responsiveness = responsiveness;
        _pipeline = pipeline;
        _workIq = workIq;
        _settings = settings.Value;
        _logger = logger;
        _intelligence = intelligence;
        _settingsManager = settingsManager;
        _relatedContext = relatedContext;
        _activeRoster = activeRoster;
        _visualization = visualization;
        _visualizationStorage = visualizationStorage;
        _copilotModelCatalog = copilotModelCatalog;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _animationsEnabled = new UISettings().AnimationsEnabled;

        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowInitialize))
            InitializeComponent();
        UpdateVisualizationViewVisibility();
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.SettingsInitialize))
            InitializeSettingsEditor();

        TranscriptList.ItemsSource = _transcript;
        TranscriptList.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _viewport.Pause()), true);
        TranscriptList.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, e) =>
            {
                if (e.GetCurrentPoint(TranscriptList).Properties.MouseWheelDelta > 0) _viewport.Pause();
            }), true);
        TranscriptList.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.Home)
                _viewport.Pause();
        };
        SpeakerBalancePanel.ItemsSource = _speakerBalance;
        OpenQuestionsList.ItemsSource = _questions;
        ActionsList.ItemsSource = _actions;
        TodaysMeetingsList.ItemsSource = _todaysMeetings;
        OtherMeetingsList.ItemsSource = _otherMeetings;
        PastMeetingsList.ItemsSource = _pastMeetings;
        RelatedContextList.ItemsSource = _relatedContextItems;
        LiveParticipantsList.ItemsSource = _liveParticipants;
        ScheduledInviteesList.ItemsSource = _scheduledInvitees;
        CatchUpSectionsList.ItemsSource = _catchUpSections;
        CatchUpSourcesList.ItemsSource = _catchUpSources;
        TrailList.ItemsSource = _visualizationTrail;
        MeetingNotesList.ItemsSource = _meetingNotes;

        ConfigureVisualizationTransitions();
        RootLayout.Loaded += OnRootLayoutLoaded;

        _questions.CollectionChanged += (_, _) => UpdateExpanderHeaders();
        _actions.CollectionChanged += (_, _) => UpdateExpanderHeaders();
        UpdateExpanderHeaders();

        RecordingBodyScroll.Loaded += (_, _) => UpdateRecordingLayout();
        RecordingBodyScroll.SizeChanged += (_, _) => UpdateRecordingLayout();
        InsightSections.SizeChanged += (_, _) => UpdateRecordingLayout();
        SpeakerSection.SizeChanged += (_, _) => UpdateRecordingLayout();
        RecordingUtilityActions.SizeChanged += (_, _) => UpdateRecordingLayout();
        foreach (var expander in new[] { OpenQuestionsExpander, ActionsExpander })
            expander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty,
                (_, _) => _dispatcherQueue.TryEnqueue(UpdateRecordingLayout));
        RecordingNotice.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
            RecordingNoticeHost.Visibility = RecordingNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed);

        // Keep the compact shell, retain the native resize border, and make the header draggable.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
        _presenter.IsResizable = true;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(_presenter);
        _titleBarPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        AppTitleBar.SizeChanged += OnTitleBarSizeChanged;
        TitleBarActions.LayoutUpdated += OnTitleBarActionsLayoutUpdated;
        UpdateTitleBarInteractiveRegions();

        // Set taskbar/titlebar icon for this window.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _windowMessageProc = OnWindowMessage;
        if (!NativeMethods.SetWindowSubclass(hwnd, _windowMessageProc, WindowSubclassId, UIntPtr.Zero))
            _windowMessageProc = null;
        AppWindow.Resize(CalculateDefaultSize(hwnd));
        NativeMethods.SetWindowIcon(hwnd, System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "tray.ico"));

        Activated += OnWindowActivated;
        AppWindow.Closing += OnAppWindowClosing;
        MeetingPrompt.CloseButtonClick += (_, _) => MeetingDismissRequested?.Invoke(this, EventArgs.Empty);
        MeetingEndPrompt.CloseButtonClick += (_, _) => MeetingEndPrompt.IsOpen = false;

        _pipeline.StateChanged += OnPipelineStatusChanged;
        _pipeline.TranscriptSegmentReceived += OnTranscriptSegmentReceived;
        _pipeline.PartialTranscriptReceived += OnPartialTranscriptReceived;
        _pipeline.MeetingEndedWhileRecording += OnMeetingEndedWhileRecording;
        _pipeline.SpeakerResolver.UnknownSpeakerDetected += OnUnknownSpeakerDetected;
        _pipeline.SpeakerResolver.SpeakerResolved += OnSpeakerResolved;
        _pipeline.AiUsage.Changed += OnAiUsageChanged;
        _pipeline.AiDiagnostics.Changed += OnAiDiagnosticsChanged;
        _relatedContext.StatusChanged += OnRelatedContextStatusChanged;
        _visualization.Changed += OnVisualizationChanged;

        _workIq.AuthStateChanged += OnWorkIqAuthStateChanged;
        _settingsManager.SettingsSaved += OnSettingsSaved;
        _healthTimer = _dispatcherQueue.CreateTimer();
        _healthTimer.Interval = TimeSpan.FromSeconds(1);
        _healthTimer.Tick += (_, _) =>
        {
            if (PreferencesPanel.Visibility == Visibility.Visible)
                UpdateDiagnostics();
            UpdateWorkIqControls();
            if (_pipeline.AiUsage.GetSnapshot().HasActiveRequests)
                RenderAiUsage(refreshDetails: false, announceTransition: false);
            if (IdlePanel.Visibility == Visibility.Visible &&
                _calendarMeetings.Count > 0 &&
                DateTimeOffset.Now - _calendarRenderedAt >= TimeSpan.FromMinutes(1))
                RenderCalendarMeetings();
        };
        _healthTimer.Start();

        UpdateControlState();
        SetChipStatus("Idle", false);
        ShowState(recording: false);
        InvalidatePanels();
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTitleBarInteractiveRegions();
    }

    private void OnTitleBarActionsLayoutUpdated(object? sender, object e)
    {
        UpdateTitleBarInteractiveRegions();
    }

    private void UpdateTitleBarInteractiveRegions()
    {
        // Layout still runs while the window closes; the pointer source is already closed then.
        if (_allowClose) return;
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1d;
        var interactiveRects = new List<RectInt32>();
        foreach (var control in new FrameworkElement[]
        {
            ContextButton,
            OpenVisualizationButton,
            TopicMapToggleButton,
            RecordingSettingsButton,
            PinButton,
        })
        {
            if (control.ActualWidth <= 0 || control.ActualHeight <= 0)
                continue;

            var bounds = control.TransformToVisual(null).TransformBounds(
                new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            interactiveRects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale),
                (int)Math.Ceiling(bounds.Height * scale)));
        }

        var rects = interactiveRects.ToArray();
        if (_titleBarInteractiveRects.AsSpan().SequenceEqual(rects))
            return;

        _titleBarInteractiveRects = rects;
        _titleBarPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, rects);
    }

    // ── Public API used by App (tray host) ──────────────────────────────────────

    /// <summary>Updates the recording-state TL;DR chip. Called by the
    /// realtime-intelligence service as the current topic is summarized.</summary>
    public void UpdateTldr(string text)
        => InvokeOnUi(() => TldrChip.Text = text);

    private void TriggerRelatedContext(
        string sessionId,
        string topic,
        long transcriptRevision,
        int throughSegmentOrdinal,
        CancellationToken sessionCancellationToken)
    {
        var normalizedTopic = NormalizeRelatedContextTopic(topic);
        if (sessionId != _sessionId ||
            !IsActiveVisualizationSession(sessionId) ||
            normalizedTopic.Length == 0 ||
            transcriptRevision < 0 || throughSegmentOrdinal < 0)
            return;

        if (!string.Equals(
            NormalizeRelatedContextTopic(_acceptedRelatedContextTopic),
            normalizedTopic,
            StringComparison.Ordinal))
            RenderRelatedContext(null);
        _acceptedRelatedContextTopic = topic;
        Interlocked.Increment(ref _visualizationAnalysisPending);
        string acceptedTopicId;
        try
        {
            var accepted = _visualization.AcceptTopic(
                topic,
                transcriptRevision,
                throughSegmentOrdinal);
            acceptedTopicId = accepted.Id;
            _relatedContextStatesByTopicId[accepted.Id] = RelatedContextState.Pending;
            _currentVisualizationTopicId = accepted.Id;
            RefreshVisualization(force: true);
            _ = RefreshVisualizationAnalysisAsync(sessionCancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Interlocked.Decrement(ref _visualizationAnalysisPending);
            _logger.LogWarning("Visualization topic update was rejected ({Type}).", ex.GetType().Name);
            return;
        }
        TldrChip.Text = ProjectVisualizationText(topic);
        if (!IsRelatedContextTopicEligible(normalizedTopic))
        {
            if (_currentVisualizationTopicId is not null)
                _relatedContextStatesByTopicId[_currentVisualizationTopicId] =
                    RelatedContextState.Unavailable;
            RefreshVisualization(force: true);
            RelatedContextStatusText.Text =
                "Related sources are skipped for long summaries; the topic map continues updating.";
            return;
        }
        _ = RefreshRelatedContextAsync(
            new ContextQuery(sessionId, topic, transcriptRevision, TopicId: acceptedTopicId),
            sessionCancellationToken);
    }

    private static bool IsRelatedContextTopicEligible(string normalizedTopic)
        => normalizedTopic.Length is > 0 and <= 240;

    private async Task RefreshVisualizationAnalysisAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.RefreshVisualizationAnalysisAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                "Visualization analysis was skipped because the recording ended ({Type}).",
                ex.GetType().Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Visualization analysis failed ({Type}).", ex.GetType().Name);
        }
        finally
        {
            if (Interlocked.Decrement(ref _visualizationAnalysisPending) == 0)
                InvokeOnUi(() => RefreshVisualization(force: true));
        }
    }

    private async Task RefreshRelatedContextAsync(ContextQuery query, CancellationToken cancellationToken)
    {
        try
        {
            await _relatedContext.RefreshAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning("Related context refresh failed ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Reloads "Today's meetings" for the idle-state calendar list when the
    /// flyout is opened. No-op while recording. Best-effort: hides the section on failure.</summary>
    /// <summary>
    /// Shows the flyout from a tray-icon click using a single atomic Win32 SetWindowPos call
    /// (position + show in one compositor pass) rather than the slower
    /// AppWindow.Resize / Move / Show / Activate sequence.
    /// Also cancels any pending suppress-release timer so rapid double-clicks don't
    /// prematurely clear _suppressAutoDismiss while the window is still settling.
    /// </summary>
    public void ShowFromTray()
    {
        if (_allowClose) return;
        // Cancel any previous release timer — rapid clicks must not clear suppress early.
        _suppressReleaseCts?.Cancel();
        _suppressReleaseCts?.Dispose();
        _suppressReleaseCts = new CancellationTokenSource();
        _suppressAutoDismiss = true;

        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowPosition))
            AppWindow.MoveAndResize(CalculateFlyoutBounds());

        // Use AppWindow.Show() so WinUI 3 properly resumes the compositor after Hide().
        // The previous SetWindowPos(SWP_SHOWWINDOW) approach only made the Win32 window
        // visible at the user32 level — it did NOT notify the WinUI 3 / DComp layer to
        // resume rendering. After AppWindow.Hide(), the compositor is paused; bypassing
        // AppWindow.Show() left it paused → black client area on every tray-icon click.
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowShow))
            AppWindow.Show(activateWindow: true);
        _responsiveness.SetWindowState(visible: AppWindow.IsVisible);
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowActivate))
            Activate();

        // Best-effort foreground promotion after explicit WinUI activation.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowForeground))
            NativeMethods.SetForegroundWindow(hwnd);

        if (!_dispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                RefreshTodaysMeetings))
            RefreshTodaysMeetings();
        _ = ReleaseSuppressAsync(_suppressReleaseCts.Token);
    }

    private async Task ReleaseSuppressAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(600, ct);
            _dispatcherQueue.TryEnqueue(() => _suppressAutoDismiss = false);
        }
        catch (OperationCanceledException) { /* replaced by a newer show — keep suppress alive */ }
    }

    public void RefreshTodaysMeetings()
    {
        if (!_pipeline.CanStart)
            return;
        _ = LoadTodaysMeetingsAsync();
    }

    private async Task LoadTodaysMeetingsAsync(bool allowWhileRecording = false)
    {
        if (_allowClose || _preparingExit || _calendarBusy || (!_pipeline.CanStart && !allowWhileRecording)) return;
        if (!_workIq.IsAuthenticated)
        {
            CalendarStatusText.Text = "Connect your calendar in Settings.";
            return;
        }
        var generation = CalendarRevision;
        var session = _pipeline.Status.SessionId;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _calendarCts = lifetime;
        _calendarBusy = true;
        _workIqConnection.BeginCalendar(_todaysMeetings.Count);
        CalendarStatusText.Text = "Refreshing meetings...";
        UpdateWorkIqControls();
        try
        {
            var meetings = await GetCalendarCandidatesAsync(lifetime.Token).WaitAsync(lifetime.Token);
            if (_allowClose || _preparingExit || generation != CalendarRevision) return;
            _workIqConnection.CalendarSucceeded(meetings.Count, DateTimeOffset.Now);
            UpdateWorkIqControls();
            _calendarMeetings = meetings;
            RenderCalendarMeetings();
        }
        catch (Exception ex)
        {
            if (_allowClose || _preparingExit || generation != CalendarRevision) return;
            _logger.LogWarning("Calendar refresh failed ({Type}).", ex.GetType().Name);
            _todaysMeetings.Clear();
            _otherMeetings.Clear();
            _pastMeetings.Clear();
            _calendarMeetings.Clear();
            lock (_calendarCandidateGate) _latestCalendarCandidates.Clear();
            OtherMeetingsExpander.Visibility = PastMeetingsExpander.Visibility = Visibility.Collapsed;
            CalendarSection.Visibility = Visibility.Collapsed;
            CalendarStatusText.Text = ex is WorkIqException workIq ? WorkIqMessage(workIq)
                : ex is OperationCanceledException ? "Calendar lookup cancelled or timed out."
                : "WorkIQ calendar could not refresh. Check sign-in and access.";
            _workIqConnection.CalendarFailed(CalendarStatusText.Text);
            UpdateWorkIqControls();
            if (_pipeline.State == PipelineState.Recording)
                ShowStatusBar(CalendarStatusText.Text, InfoBarSeverity.Warning);
        }
        finally
        {
            if (ReferenceEquals(_calendarCts, lifetime)) _calendarCts = null;
            _calendarBusy = false;
            if (!_allowClose && generation != CalendarRevision)
                _ = LoadTodaysMeetingsAsync(allowWhileRecording: true);
        }
    }

    private void RenderCalendarMeetings()
    {
        var now = DateTimeOffset.Now;
        _calendarRenderedAt = now;
        _todaysMeetings.Clear();
        _otherMeetings.Clear();
        _pastMeetings.Clear();
        foreach (var meeting in CalendarMeetingList.Select(_calendarMeetings, now))
        {
            var row = MeetingRow.From(meeting, now);
            var target = CalendarMeetingList.IsAroundNow(meeting, now) ? _todaysMeetings
                : meeting.Start > now ? _otherMeetings : _pastMeetings;
            target.Add(row);
        }
        CalendarSection.Visibility = _todaysMeetings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherMeetingsExpander.Visibility = _otherMeetings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PastMeetingsExpander.Visibility = _pastMeetings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CalendarStatusText.Text = _todaysMeetings.Count > 0 ? "Calendar connected"
            : "No meetings around now.";
    }

    public async Task<List<MeetingTitleCandidate>> GetCalendarCandidatesAsync(CancellationToken ct)
    {
        var start = new DateTimeOffset(DateTime.Today);
        var end = new DateTimeOffset(DateTime.Today.AddDays(1));
        var meetings = await Task.Run(() => _workIq.GetCalendarAsync(start, end, ct), ct).ConfigureAwait(false);
        lock (_calendarCandidateGate) _latestCalendarCandidates = [.. meetings];
        return meetings;
    }

    private void InvalidateCalendar()
    {
        Interlocked.Increment(ref _calendarGeneration);
        _calendarCts?.Cancel();
        _todaysMeetings.Clear();
        _otherMeetings.Clear();
        _pastMeetings.Clear();
        _calendarMeetings.Clear();
        CalendarSection.Visibility = Visibility.Collapsed;
        OtherMeetingsExpander.Visibility = PastMeetingsExpander.Visibility = Visibility.Collapsed;
    }

    private void OnRefreshCalendarClick(object sender, RoutedEventArgs e) => RefreshTodaysMeetings();

    private static string WorkIqMessage(WorkIqException error)
        => string.IsNullOrEmpty(error.Code) ? error.Message : $"{error.Message} ({error.Code})";

    private void OnWorkIqAuthStateChanged(object? sender, EventArgs e) => InvokeOnUi(() =>
    {
        if (_allowClose) return;
        var auth = _workIq.AuthState;
        if (!_workIqConnection.AuthenticationChanged(auth.IsAuthenticated, auth.Revision)) return;
        UpdateWorkIqControls();
        InvalidateCalendar();
        _relatedContext.Invalidate();
        _relatedContextStatesByTopicId.Clear();
        _appliedRelatedContextSnapshots.Clear();
        RefreshVisualization();
        RenderRelatedContext(_relatedContext.Status);
        var currentTopicId = ResolveLiveCanonicalTopicId(_currentVisualizationTopicId);
        if (_sessionId is not null && _contextCts is not null &&
            _loadedVisualization is null && auth.IsAuthenticated)
        {
            var topics = _visualization.Snapshot().TopicClusters
                .OrderByDescending(topic => topic.Id == currentTopicId);
            foreach (var topic in topics)
                if (IsRelatedContextTopicEligible(NormalizeRelatedContextTopic(topic.Label)))
                    _ = RefreshRelatedContextAsync(
                        new ContextQuery(
                            _sessionId,
                            topic.Label,
                            _transcriptRevision,
                            ForceRefresh: auth.IsAuthenticated,
                            TopicId: topic.Id),
                        _contextCts.Token);
        }
        _ = LoadTodaysMeetingsAsync(allowWhileRecording: true);
    });

    private async Task RestoreWorkIqAsync()
    {
        if (_allowClose || _preparingExit || _workIqCts is not null ||
            _workIq.IsSignInBusy || _workIq.IsAuthenticated) return;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = lifetime.Token;
        _workIqCts = lifetime;
        _workIqRestoring = true;
        UpdateWorkIqControls();
        var restore = Task.Run(() => _workIq.RestoreAsync(token), token);
        try { await restore.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            _ = restore.ContinueWith(fault =>
                _logger.LogWarning("WorkIQ restore finished after cancellation ({Type}).",
                    fault.Exception!.GetBaseException().GetType().Name),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            SetWorkIqAuthFailure(lifetime, "WorkIQ restore cancelled or timed out. Sign in when ready.");
        }
        catch (WorkIqException ex) { SetWorkIqAuthFailure(lifetime, WorkIqMessage(ex)); }
        finally
        {
            if (ReferenceEquals(_workIqCts, lifetime)) _workIqCts = null;
            _workIqRestoring = false;
            if (!_allowClose) UpdateWorkIqControls();
        }
    }

    private async void OnWorkIqSignInClick(object sender, RoutedEventArgs e)
    {
        if (_workIq.IsAuthenticated || _workIqCts is not null || _workIq.IsSignInBusy) return;
        using var lifetime = new CancellationTokenSource();
        _workIqCts = lifetime;
        _workIqConnection.SetMessage("Finish WorkIQ sign-in in the browser (90-second limit), or cancel. Recording remains available.");
        UpdateWorkIqControls();
        try { await _workIq.SignInAsync(lifetime.Token); }
        catch (OperationCanceledException) { SetWorkIqAuthFailure(lifetime, "WorkIQ sign-in cancelled. You can close the browser tab."); }
        catch (WorkIqException ex) when (ex.Kind is WorkIqFailure.ServiceUnavailable && _workIq.IsAuthenticated)
        {
            var message = "Signed in to WorkIQ, but the WorkIQ service is having a problem right now. " +
                "Your connection is working; try again in a few minutes." +
                (string.IsNullOrEmpty(ex.Code) ? string.Empty : $" ({ex.Code})");
            _workIqConnection.SetMessage(message);
            ShowStatusBar(message, InfoBarSeverity.Warning);
        }
        catch (WorkIqException ex) { SetWorkIqAuthFailure(lifetime, WorkIqMessage(ex)); }
        finally
        {
            if (ReferenceEquals(_workIqCts, lifetime)) _workIqCts = null;
            if (!_allowClose) UpdateWorkIqControls();
        }
    }

    private void OnWorkIqCancelClick(object sender, RoutedEventArgs e)
    {
        _workIqCts?.Cancel();
        UpdateWorkIqControls();
    }

    private void SetWorkIqAuthFailure(CancellationTokenSource lifetime, string message)
    {
        if (!_allowClose && !_preparingExit && ReferenceEquals(_workIqCts, lifetime) && !_workIq.IsAuthenticated)
        {
            _workIqConnection.SetMessage(message);
            ShowStatusBar(message, lifetime.IsCancellationRequested ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
        }
    }

    private void OnWorkIqSignOutClick(object sender, RoutedEventArgs e)
    {
        _workIqCts?.Cancel();
        try { _workIq.SignOut(); }
        catch (WorkIqException ex) { _workIqConnection.SetMessage(WorkIqMessage(ex)); }
        finally
        {
            InvalidateCalendar();
            RefreshTodaysMeetings();
            UpdateWorkIqControls();
        }
    }

    private void UpdateWorkIqControls()
    {
        if (_allowClose) return;
        var authenticated = _workIq.IsAuthenticated;
        var signInBusy = _workIq.IsSignInBusy;
        var pending = _workIqCts is not null;
        _responsiveness.SetWorkIqState(authenticated, pending || signInBusy, _calendarBusy);
        var state = _workIqConnection.Controls(authenticated, signInBusy, pending,
            _workIqCts is { IsCancellationRequested: true }, _workIqRestoring);
        if (_renderedWorkIqControls == state) return;
        var previous = _renderedWorkIqControls;
        var actions = state.Actions;
        if (previous?.Actions.SignIn != actions.SignIn)
        {
            WorkIqSignInButton.Visibility = actions.SignIn ? Visibility.Visible : Visibility.Collapsed;
            WorkIqSignInButton.IsEnabled = actions.SignIn;
            MeetingNotesWorkIqSignInButton.Visibility = actions.SignIn
                ? Visibility.Visible : Visibility.Collapsed;
            MeetingNotesWorkIqSignInButton.IsEnabled = actions.SignIn;
        }
        if (previous?.Actions.SignOut != actions.SignOut)
        {
            WorkIqSignOutButton.Visibility = actions.SignOut ? Visibility.Visible : Visibility.Collapsed;
            WorkIqSignOutButton.IsEnabled = actions.SignOut;
        }
        if (previous?.Actions.Cancel != actions.Cancel)
        {
            WorkIqCancelButton.Visibility = RecordingWorkIqCancelButton.Visibility =
                actions.Cancel ? Visibility.Visible : Visibility.Collapsed;
            RecordingAuthProgress.Visibility = actions.Cancel ? Visibility.Visible : Visibility.Collapsed;
        }
        if (previous?.CancelEnabled != state.CancelEnabled)
            WorkIqCancelButton.IsEnabled = RecordingWorkIqCancelButton.IsEnabled = state.CancelEnabled;
        if (previous?.ProgressText != state.ProgressText)
            RecordingAuthProgressText.Text = state.ProgressText;
        if (previous?.StateText != state.StateText)
            WorkIqStateText.Text = state.StateText;
        _renderedWorkIqControls = state;
    }

    public void ShowSettings()
    {
        if (PreferencesPanel.Visibility == Visibility.Visible)
            return;

        _preferencesFromRecording = RecordingPanel.Visibility == Visibility.Visible;
        CloseTopicMap();
        IdlePanel.Visibility = RecordingPanel.Visibility = Visibility.Collapsed;
        RecordingSettingsButton.Visibility = Visibility.Collapsed;
        ContextButton.Visibility = Visibility.Collapsed;
        TopicMapToggleButton.Visibility = Visibility.Collapsed;
        PreferencesPanel.Visibility = Visibility.Visible;
        OpenSettingsEditor();
    }

    private void OnPreferencesClick(object sender, RoutedEventArgs e) => ShowSettings();

    private void OnPreferencesBackClick(object sender, RoutedEventArgs e)
    {
        CancelSettingsEditor();
        ShowState(_preferencesFromRecording);
    }

    private void OnBackToMeetingsClick(object sender, RoutedEventArgs e)
    {
        if (_preparingExit || _retryBusy || _previewBusy || !_pipeline.CanStart) return;
        _showCompletedSession = false;
        ShowState(false);
        RefreshTodaysMeetings();
    }

    public void ShowMeetingPrompt(MeetingSession session)
    {
        _promptSession = session;
        InvokeOnUi(() =>
        {
            SetMeetingPromptContent(session, _workIq.IsAuthenticated
                ? "Checking WorkIQ calendar for a matching meeting..."
                : "WorkIQ is signed out. You can still take notes or choose a meeting manually.");
            MeetingPrompt.IsOpen = true;
        });
    }

    public void UpdateMeetingPrompt(MeetingSession session, string source)
    {
        InvokeOnUi(() =>
        {
            if (!ReferenceEquals(_promptSession, session) || !MeetingPrompt.IsOpen || !session.IsActive)
                return;
            List<MeetingTitleCandidate> candidates;
            lock (_calendarCandidateGate) candidates = [.. _latestCalendarCandidates];
            var resolution = MeetingTitleResolver.ResolveWithReason(
                session.SourceProcess, session.StartedAt, candidates);
            if (resolution.Meeting is { } match &&
                string.Equals(resolution.Title, session.Title, StringComparison.OrdinalIgnoreCase))
                session.TryAttachCalendarMeeting(session.Id, match);
            SetMeetingPromptContent(session, source);
        });
    }

    private void SetMeetingPromptContent(MeetingSession session, string source)
    {
        var content = MeetingPromptPresentation.Create(session, source);
        MeetingPromptSubject.Text = content.Title;
        MeetingPromptSource.Text = content.CalendarState;
        SetOptionalPromptText(MeetingPromptSchedule, content.Schedule);
        SetOptionalPromptText(MeetingPromptOrganizer, content.Organizer);
        SetOptionalPromptText(MeetingPromptInvitees, content.Invitees);
        AutomationProperties.SetName(TakeNotesButton, $"Take notes for {content.Title}");
        List<MeetingTitleCandidate> candidates;
        lock (_calendarCandidateGate) candidates = [.. _latestCalendarCandidates];
        var matchCount = MeetingTitleResolver.MatchingCandidates(
            session.SourceProcess, session.StartedAt, candidates).Count;
        ChooseCalendarMeetingButton.Content = matchCount > 1
            ? $"Choose from {matchCount} matching meetings"
            : "Choose a meeting";
        AutomationProperties.SetName(ChooseCalendarMeetingButton,
            matchCount > 1 ? $"Choose from {matchCount} matching calendar meetings"
            : "Choose a meeting from the calendar");
    }

    private static void SetOptionalPromptText(TextBlock target, string? text)
    {
        target.Text = text ?? "";
        target.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void HideMeetingPrompt()
    {
        _promptSession = null;
        InvokeOnUi(() => MeetingPrompt.IsOpen = false);
    }

    public bool IsClosing => _allowClose;

    public void CloseForReal()
    {
        if (!_preparingExit) PrepareForExit();
        _allowClose = true;
        AppTitleBar.SizeChanged -= OnTitleBarSizeChanged;
        TitleBarActions.LayoutUpdated -= OnTitleBarActionsLayoutUpdated;
        Activated -= OnWindowActivated;
        _workIq.AuthStateChanged -= OnWorkIqAuthStateChanged;
        _healthTimer?.Stop();
        StopSaveReview();
        _settingsManager.SettingsSaved -= OnSettingsSaved;
        _pipeline.StateChanged -= OnPipelineStatusChanged;
        _pipeline.TranscriptSegmentReceived -= OnTranscriptSegmentReceived;
        _pipeline.PartialTranscriptReceived -= OnPartialTranscriptReceived;
        _pipeline.MeetingEndedWhileRecording -= OnMeetingEndedWhileRecording;
        _pipeline.SpeakerResolver.UnknownSpeakerDetected -= OnUnknownSpeakerDetected;
        _pipeline.SpeakerResolver.SpeakerResolved -= OnSpeakerResolved;
        _pipeline.AiUsage.Changed -= OnAiUsageChanged;
        _pipeline.AiDiagnostics.Changed -= OnAiDiagnosticsChanged;
        _relatedContext.StatusChanged -= OnRelatedContextStatusChanged;
        _visualization.Changed -= OnVisualizationChanged;
        _contextCts?.Cancel();
        _contextCts?.Dispose();
        _suppressReleaseCts?.Cancel();
        Close();
    }

    public async Task<bool> ConfirmCloseAfterSavedPartialAsync()
    {
        if (RootLayout.XamlRoot is not { } xamlRoot) return false;
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowShow))
            AppWindow.Show(activateWindow: true);
        _responsiveness.SetWindowState(visible: AppWindow.IsVisible);
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowActivate))
            Activate();
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Speech transcription is still active",
            Content = "The recognized transcript has been saved, but a Speech operation has not returned. " +
                "Closing the app will end this session without disposing or reusing that Speech session. " +
                "Audio that did not reach transcription is not available for recovery.",
            PrimaryButtonText = "Close app",
            CloseButtonText = "Keep app open",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void PrepareForExit()
    {
        _preparingExit = true;
        _healthTimer?.Stop();
        StopRecordingTimer();
        StopSaveReview();
        _retryCts?.Cancel();
        _workIqCts?.Cancel();
        _calendarCts?.Cancel();
        ShowStatusBar("Exiting: finishing capture and saving the transcript. Optional AI work is cancelled.",
            InfoBarSeverity.Informational);
        UpdateControlState();
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowShow))
            AppWindow.Show();
        _responsiveness.SetWindowState(visible: AppWindow.IsVisible);
    }

    public void ExitFailed()
    {
        _preparingExit = false;
        _healthTimer?.Start();
        if (_pipeline.State == PipelineState.Recording && _recordingCts is null) StartRecordingTimer();
        UpdateControlState();
        ShowStatusBar("Exit could not safely save the transcript or release capture. The app remains open. Check the recording/storage error, then retry Exit.",
            InfoBarSeverity.Error);
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowShow))
            AppWindow.Show();
        _responsiveness.SetWindowState(visible: AppWindow.IsVisible);
    }

    // ── Window lifecycle ────────────────────────────────────────────────────────

    /// <summary>
    /// Positions the flyout anchored to the taskbar, right-aligned within the work area.
    /// Called when showing via the non-tray path (context menu, API).
    /// </summary>
    public void PositionFlyout()
    {
        using var phase = _responsiveness.BeginUiPhase(ResponsivenessPhase.WindowPosition);
        AppWindow.MoveAndResize(CalculateFlyoutBounds());
    }

    /// <summary>Keeps the flyout inside the current monitor's usable work area.</summary>
    private Windows.Graphics.RectInt32 CalculateFlyoutBounds()
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var bounds = WindowPlacement.Anchor(
            new(workArea.X, workArea.Y, workArea.Width, workArea.Height),
            AppWindow.Size.Width,
            AppWindow.Size.Height,
            (int)NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)),
            workArea.Y > displayArea.OuterBounds.Y);
        return new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private Windows.Graphics.SizeInt32 CalculateDefaultSize(IntPtr hwnd)
    {
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var size = WindowPlacement.DefaultSize(
            (int)NativeMethods.GetDpiForWindow(hwnd),
            new(workArea.X, workArea.Y, workArea.Width, workArea.Height));
        return new(size.Width, size.Height);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        _responsiveness.SetWindowState(
            visible: AppWindow.IsVisible,
            active: e.WindowActivationState != WindowActivationState.Deactivated);
        // Auto-dismiss only while idle; active capture must remain visible on focus loss.
        if (e.WindowActivationState == WindowActivationState.Deactivated
            && !_preparingExit
            && !_suppressAutoDismiss
            && !_isPinned
            && !_pipeline.CanStop)
        {
            using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowHide))
                AppWindow.Hide();
            _responsiveness.SetWindowState(visible: AppWindow.IsVisible);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        SetPinned(PinButton.IsChecked == true);
    }

    private void SetPinned(bool requested)
    {
        _presenter.IsAlwaysOnTop = requested;
        _isPinned = _presenter.IsAlwaysOnTop;
        PinButton.IsChecked = _isPinned;
        var label = _isPinned ? "Unpin panel" : "Pin panel above other windows";
        ToolTipService.SetToolTip(PinButton, label);
        AutomationProperties.SetName(PinButton, label);
    }

    private IntPtr OnWindowMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (msg == NativeMethods.WmNcDestroy && _windowMessageProc is { } proc)
            NativeMethods.RemoveWindowSubclass(hWnd, proc, idSubclass);
        var result = NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
        if (msg == NativeMethods.WmGetMinMaxInfo)
            NativeMethods.ConstrainMaximizedBoundsToWorkArea(hWnd, lParam);
        return result;
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            CancelSettingsEditorLifetime();
            return;
        }

        args.Cancel = true;
        using (_responsiveness.BeginUiPhase(ResponsivenessPhase.WindowHide))
            AppWindow.Hide();
        _responsiveness.SetWindowState(visible: AppWindow.IsVisible);
    }

    // ── Button handlers ─────────────────────────────────────────────────────────

    private void OnTakeNotesClick(object sender, RoutedEventArgs e)
    {
        if (_promptSession is not null)
            MeetingConfirmRequested?.Invoke(this, _promptSession);
    }

    private void OnChooseCalendarMeetingClick(object sender, RoutedEventArgs e)
    {
        if (!_pipeline.CanStart) return;
        var prompt = _promptSession;
        List<MeetingTitleCandidate> candidates;
        lock (_calendarCandidateGate) candidates = [.. _latestCalendarCandidates];
        MeetingDismissRequested?.Invoke(this, EventArgs.Empty);
        _showCompletedSession = false;
        ShowState(false);
        var matches = prompt is null ? [] : MeetingTitleResolver.MatchingCandidates(
            prompt.SourceProcess, prompt.StartedAt, candidates);
        if (matches.Count > 0)
        {
            _calendarMeetings = [.. matches];
            RenderCalendarMeetings();
            CalendarStatusText.Text = matches.Count == 1
                ? "One platform-matched meeting is available."
                : $"Choose the correct meeting from {matches.Count} platform-matched calendar events.";
        }
        else if (candidates.Count > 0)
        {
            _calendarMeetings = candidates;
            RenderCalendarMeetings();
            CalendarStatusText.Text =
                "No calendar event was verified as this call. Choose manually from the available meetings.";
        }
        else
        {
            CalendarStatusText.Text = _workIq.IsAuthenticated
                ? "Refreshing WorkIQ calendar; manual recording remains available."
                : "WorkIQ is signed out. Manual recording remains available.";
            RefreshTodaysMeetings();
        }
    }

    /// <summary>Shows the specific, sanitized reason a recording could not start.</summary>
    private void ShowStartFailure(string action, Exception error)
    {
        var failure = _pipeline.DescribeStartFailure(error);
        _logger.LogError("{Action} failed ({Type}) classified as {StartFailureKind}.",
            action, error.GetType().Name, failure.Kind);
        ShowStatusBar(failure.Message, InfoBarSeverity.Error);
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_preparingExit || _retryBusy || _previewBusy || !_pipeline.CanStart)
        {
            UpdateControlState();
            ShowStatusBar(RecordingViewState.NewRecordingAction(_pipeline.Status, _pipeline.CanStart,
                _preparingExit, _retryBusy, _previewBusy).Message, InfoBarSeverity.Informational);
            return;
        }
        StartButton.IsEnabled = NewRecordingButton.IsEnabled = false;
        var title = $"Manual Recording \u2014 {DateTime.Now:yyyy-MM-dd HH:mm}";
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartManualAsync(title, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ShowStartFailure("Manual start", ex);
            }
            finally { InvokeOnUi(UpdateControlState); }
        });
    }

    /// <summary>
    /// The detected call ended while this recording is still running; offer to stop instead of
    /// stopping automatically. Non-blocking, announced once, and dismissable without side effects.
    /// </summary>
    private void OnMeetingEndedWhileRecording(object? sender, MeetingSession session)
    {
        InvokeOnUi(() =>
        {
            if (!ReferenceEquals(_pipeline.CurrentSession, session)) return;
            MeetingEndPrompt.IsOpen = true;
        });
    }

    private void OnMeetingEndStopClick(object sender, RoutedEventArgs e)
    {
        MeetingEndPrompt.IsOpen = false;
        OnStopClick(sender, e);
    }

    private void OnMeetingEndKeepClick(object sender, RoutedEventArgs e)
    {
        MeetingEndPrompt.IsOpen = false;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (!_pipeline.CanStop) return;
        StopButton.IsEnabled = PreferencesStopButton.IsEnabled = false;
        LifecycleText.Text = "Stopping capture...";
        StopRecordingTimer();
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StopManualAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Manual stop failed ({Type}).", ex.GetType().Name);
                ShowStatusBar("Recording could not finish. Retained transcript remains available.", InfoBarSeverity.Error);
            }
            finally { InvokeOnUi(UpdateControlState); }
        });
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _settings.Storage.NotesPath;
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
            else
                ShowStatusBar($"Notes folder does not exist yet: {path}", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _logger.LogError("Open notes folder failed ({Type}).", ex.GetType().Name);
            ShowStatusBar("Unable to open the notes folder.", InfoBarSeverity.Error);
        }
    }

    private void OnMeetingRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MeetingRow row } || !_pipeline.CanStart) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.StartManualAsync(row.Title, row.Meeting, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ShowStartFailure("Calendar recording start", ex);
            }
            finally { InvokeOnUi(UpdateControlState); }
        });
    }

    private async void OnWhatDidIMissClick(object sender, RoutedEventArgs e)
    {
        OpenCatchUp();
        await RefreshPanelAsync(_summaryState, force: true, catchUp: true);
        if (!_catchUpRendered)
            RenderCatchUpFailure(_summaryState.Status.Failure?.Message);
    }

    private void OnCatchUpCloseClick(object sender, RoutedEventArgs e) => CloseCatchUp();

    /// <summary>Shows the dedicated catch-up surface in its loading state.</summary>
    private void OpenCatchUp()
    {
        _catchUpRendered = false;
        CatchUpPanel.Visibility = Visibility.Visible;
        CatchUpBody.Visibility = Visibility.Collapsed;
        CatchUpRetryButton.Visibility = Visibility.Collapsed;
        CatchUpProgress.IsActive = true;
        CatchUpProgress.Visibility = Visibility.Visible;
        CatchUpStatusText.Text = "Reading the recent conversation...";
        CatchUpScroll.ChangeView(null, 0, null, disableAnimation: true);
        CatchUpCloseButton.Focus(FocusState.Programmatic);
    }

    private void CloseCatchUp()
    {
        if (CatchUpPanel is null)
            return;
        CatchUpPanel.Visibility = Visibility.Collapsed;
        CatchUpProgress.IsActive = false;
        CatchUpProgress.Visibility = Visibility.Collapsed;
        _catchUpRendered = false;
        if (WhatDidIMissButton.Visibility == Visibility.Visible && WhatDidIMissButton.IsEnabled)
            WhatDidIMissButton.Focus(FocusState.Programmatic);
    }

    private void RenderCatchUpFailure(string? detail)
    {
        if (CatchUpPanel.Visibility != Visibility.Visible)
            return;
        CatchUpProgress.IsActive = false;
        CatchUpProgress.Visibility = Visibility.Collapsed;
        CatchUpBody.Visibility = Visibility.Collapsed;
        CatchUpRetryButton.Visibility = Visibility.Visible;
        CatchUpStatusText.Text = string.IsNullOrWhiteSpace(detail)
            ? "Catch me up could not be generated. Recording continues; try again when ready."
            : detail;
    }

    /// <summary>
    /// Renders the catch-up recap beside the grounded state this session already holds, so
    /// the result carries enough context without a second retrieval or a new backend.
    /// </summary>
    private void RenderCatchUp(string summary)
    {
        _catchUpRendered = true;
        CatchUpPanel.Visibility = Visibility.Visible;
        CatchUpProgress.IsActive = false;
        CatchUpProgress.Visibility = Visibility.Collapsed;
        CatchUpRetryButton.Visibility = Visibility.Collapsed;
        CatchUpStatusText.Text = $"Updated {DateTimeOffset.Now:h:mm:ss tt} \u00b7 from this recording only.";
        CatchUpBody.Visibility = Visibility.Visible;

        CatchUpCurrentTopicText.Text = _hasSummary && !string.IsNullOrWhiteSpace(TldrChip.Text)
            ? TldrChip.Text
            : "Topic not established yet.";
        CatchUpSummaryText.Text = summary;

        _catchUpSections.Clear();
        foreach (var section in CatchUpPresentation.Sections(
                     _displayedVisualizationDocument,
                     _acceptedRelatedContextTopic,
                     _questions.Where(item => !item.IsCompleted).Select(item => item.Text),
                     _actions.Where(item => !item.IsCompleted).Select(item => item.Text)))
            _catchUpSections.Add(section);

        _catchUpSources.Clear();
        foreach (var source in _relatedContextItems.Take(3))
            _catchUpSources.Add(source);
        CatchUpSourcesSection.Visibility = _catchUpSources.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        CatchUpGroundingText.Text = _catchUpSections.Count > 0
            ? "Points, decisions, questions and actions come from this session\u0027s accepted transcript analysis."
            : "No decisions, questions or actions have been established yet in this recording.";
        CatchUpScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnCopyRecordingNoticeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(RecordingNoticeText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            CopyRecordingNoticeIcon.Glyph = "\uE73E";
            ToolTipService.SetToolTip(CopyRecordingNoticeButton, "Copied");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogWarning("Could not copy transcription notice ({Type}).", ex.GetType().Name);
            ShowStatusBar("Could not copy the notice. Try Copy message again.", InfoBarSeverity.Warning);
        }
    }

    private void ShowAutoInfoBar(string title, string message, bool autoDismiss = true)
    {
        InvokeOnUi(() =>
        {
            StatusBar.Title = title;
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;

            _statusBarDismissTimer?.Stop();
            _statusBarDismissTimer = null;

            if (!autoDismiss)
                return;

            _statusBarDismissTimer = _dispatcherQueue.CreateTimer();
            _statusBarDismissTimer.Interval = TimeSpan.FromSeconds(15);
            _statusBarDismissTimer.IsRepeating = false;
            _statusBarDismissTimer.Tick += (_, _) => StatusBar.IsOpen = false;
            _statusBarDismissTimer.Start();
        });
    }

    // ── Pipeline event handlers ─────────────────────────────────────────────────

    private void OnPipelineStatusChanged(object? sender, PipelineStatus status)
    {
        InvokeOnUi(() =>
        {
            _responsiveness.SetCaptureState(_pipeline.CanStop);
            HandleStatusChange(status);
        });
    }

    private void OnAiUsageChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            RenderAiUsage(refreshDetails: true, announceTransition: true);
            return;
        }

        if (Interlocked.Exchange(ref _aiUsageRenderQueued, 1) != 0)
            return;
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _aiUsageRenderQueued, 0);
                RenderAiUsage(refreshDetails: true, announceTransition: true);
            }))
        {
            Interlocked.Exchange(ref _aiUsageRenderQueued, 0);
        }
    }

    private void OnTranscriptSegmentReceived(object? sender, SessionTranscriptSegment update)
    {
        InvokeOnUi(() =>
        {
            if (update.SessionId != _sessionId || update.Revision < _transcriptRevision) return;
            var isNewRevision = update.Revision > _transcriptRevision;
            _transcriptRevision = Math.Max(_transcriptRevision, update.Revision);
            if (isNewRevision)
                _finalizedSegmentCount++;
            AppendTranscriptSegment(update.Revision, update.Segment, isNewRevision);
            UpdateRecordingPresentation();
        });
    }

    private TranscriptLine? _partialItem => _transcriptState.Partial;
    private DateTime _lastPartialUiUpdate = DateTime.MinValue;
    private void OnPartialTranscriptReceived(object? sender, SessionPartialTranscript update)
    {
        if (string.IsNullOrWhiteSpace(update.Partial.Text))
            return;
        Interlocked.Exchange(ref _pendingPartialTranscript, update);
        QueuePartialTranscriptRender();
    }

    private void QueuePartialTranscriptRender()
    {
        if (Interlocked.Exchange(ref _partialUiUpdateQueued, 1) != 0)
            return;
        if (!_dispatcherQueue.TryEnqueue(RenderLatestPartialTranscript))
            Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
    }

    private void RenderLatestPartialTranscript()
    {
        _partialTranscriptRetryTimer?.Stop();
        var update = Interlocked.Exchange(ref _pendingPartialTranscript, null);
        if (update is null)
        {
            Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
            return;
        }

        if (update.SessionId != _sessionId || !_viewport.FollowLatest ||
            _pipeline.State != PipelineState.Recording)
        {
            Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
            if (Volatile.Read(ref _pendingPartialTranscript) is not null)
                QueuePartialTranscriptRender();
            return;
        }

        var now = DateTime.UtcNow;
        var remainingMilliseconds = 150 - (now - _lastPartialUiUpdate).TotalMilliseconds;
        if (remainingMilliseconds > 0)
        {
            Interlocked.CompareExchange(ref _pendingPartialTranscript, update, null);
            SchedulePartialTranscriptRetry(TimeSpan.FromMilliseconds(remainingMilliseconds));
            return;
        }

        Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
        _lastPartialUiUpdate = now;
        var p = update.Partial;
        var rawSpeaker = string.IsNullOrWhiteSpace(p.SpeakerId) ? "Unknown" : p.SpeakerId;
        var display = _pipeline.SpeakerResolver.GetDisplayName(rawSpeaker);
        var result = _transcriptState.UpsertPartial(
            rawSpeaker,
            () => CreatePartialTranscriptLine(rawSpeaker, display, p.Text),
            line => UpdatePartialTranscriptLine(line, rawSpeaker, display, p.Text));

        EmptyStateLabel.Visibility = Visibility.Collapsed;
        TranscriptList.Visibility = Visibility.Visible;
        UpdateRecordingPresentation();
        if (result.RequiresFollow)
            ScrollToLatest();

        if (Volatile.Read(ref _pendingPartialTranscript) is not null)
            QueuePartialTranscriptRender();
    }

    private void SchedulePartialTranscriptRetry(TimeSpan delay)
    {
        var timer = _partialTranscriptRetryTimer;
        if (timer is null)
        {
            timer = _dispatcherQueue.CreateTimer();
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
                QueuePartialTranscriptRender();
            };
            _partialTranscriptRetryTimer = timer;
        }
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay.TotalMilliseconds));
        timer.Start();
    }

    private void RemovePartialItem()
    {
        _transcriptState.RemovePartial();
    }

    // ── UI updates ──────────────────────────────────────────────────────────────

    private void HandleStatusChange(PipelineStatus status)
    {
        if (status.Sequence <= _statusSequence) return;
        _statusSequence = status.Sequence;
        if (status.SessionId != null && status.SessionId != _sessionId)
        {
            StopRecordingTimer();
            _sessionId = status.SessionId;
            _showCompletedSession = true;
            var snapshot = _pipeline.CurrentTranscript;
            _startedAt = snapshot?.StartedAt;
            RecordingTitleText.Text = snapshot?.Title ?? "Meeting notes";
            _transcriptRevision = -1;
            Interlocked.Exchange(ref _pendingPartialTranscript, null);
            _partialTranscriptRetryTimer?.Stop();
            Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
            _notesPath = null;
            _viewport.Latest();
            _transcriptState.Reset();
            _speakerBrushes.Clear();
            ResetSpeakerPrompts();
            TranscriptList.Visibility = Visibility.Collapsed;
            EmptyStateLabel.Visibility = Visibility.Visible;
            EmptyStateLabel.Text = "Waiting for recognized speech...";
            RecordingNotice.IsOpen = false;
            CopyRecordingNoticeIcon.Glyph = "\uE8C8";
            ToolTipService.SetToolTip(CopyRecordingNoticeButton, "Copy transcription notice");
            _recordingNoticeSessionId = null;
            OnRecordingStarted();
            InvalidatePanels();
        }
        if (RecordingViewState.ShouldShowNotice(status.State, _sessionId, _recordingNoticeSessionId))
        {
            _recordingNoticeSessionId = _sessionId;
            RecordingNotice.IsOpen = true;
        }
        if (status.State == PipelineState.ReviewingSave)
            StartSaveReview(status);
        else
            StopSaveReview();
        if (status.State == PipelineState.Recording && _recordingCts is null)
            StartRecordingTimer();
        if (status.State is not (PipelineState.Starting or PipelineState.Recording))
        {
            RecordingNotice.IsOpen = false;
            StopRecordingTimer();
            Interlocked.Exchange(ref _pendingPartialTranscript, null);
            _partialTranscriptRetryTimer?.Stop();
            Interlocked.Exchange(ref _partialUiUpdateQueued, 0);
            RemovePartialItem();
            if (_transcript.Count == 0) EmptyStateLabel.Text = "No speech was recognized.";
        }
        if (status.State is PipelineState.Starting or PipelineState.Recording)
        {
            _preview = false;
            PreviewButton.Content = "Start audio preview";
        }
        SetChipStatus(status.State.ToString(), status.State == PipelineState.Recording);
        LifecycleText.Text = status.Message;
        if (status.NotesPath is not null) _notesPath = status.NotesPath;
        SavedPathText.Text = _notesPath is null ? "" : "Notes saved";
        if (status.IntelligenceFailure is { } failure)
            ShowStatusBar($"{failure.Provider}: {failure.Kind}. {failure.Message} Use Settings, then Retry AI.", InfoBarSeverity.Warning);
        else if (status.Error is not null)
            ShowStatusBar(status.Error,
                status.State == PipelineState.Saved ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        if (status.State == PipelineState.Discarded)
        {
            ClearDiscardedSessionUi();
            SetChipStatus("Idle", false);
            LifecycleText.Text = status.Message;
            ShowState(false);
            RenderPanelStatuses();
            UpdateControlState();
            RefreshTodaysMeetings();
            RenderAiUsage(refreshDetails: true, announceTransition: false);
            return;
        }
        ShowState(_sessionId is not null && _showCompletedSession);
        if (status.State is PipelineState.Saved or PipelineState.Failed &&
            _displayedVisualizationDocument is not null)
            RefreshVisualization(force: true);
        RenderAiUsage(refreshDetails: true, announceTransition: false);
        RenderPanelStatuses();
        UpdateControlState();
    }

    // ── Idle / recording state switching ────────────────────────────────────────

    private void ShowState(bool recording)
    {
        if (PreferencesPanel.Visibility == Visibility.Visible)
            CancelSettingsEditorLifetime();
        PreferencesPanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        IdlePanel.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        RecordingSettingsButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        ContextButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        AiActivityIndicator.Visibility = recording && IsCurrentAiUsageSession()
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopicMapToggleButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (!recording)
        {
            CloseTopicMap();
            CloseCatchUp();
        }
        if (!recording && _pipeline.CanStart)
        {
            SetChipStatus("Idle", false);
            CalendarScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
    }

    private void OnRecordingStarted()
    {
        _hasSummary = false;
        _finalizedSegmentCount = 0;
        _lastFinalSpeakerId = null;
        _lastPartialUiUpdate = DateTime.MinValue;
        _panelTranscriptThrough.Clear();
        _speakerWordCounts.Clear();
        _totalWords = 0;
        _speakerBalance.Clear();
        _speakerAvatarImages.Clear();
        _speakerAvatarRevisions.Clear();
        _selectedSpeakerNameOption = null;
        _contextCts?.Cancel();
        _contextCts?.Dispose();
        _relatedContext.Invalidate();
        _acceptedRelatedContextTopic = null;
        _relatedContextStatesByTopicId.Clear();
        _appliedRelatedContextSnapshots.Clear();
        _loadedVisualization = null;
        ClearVisualizationPresentation();
        CloseTopicMap();
        _contextCts = new CancellationTokenSource();
        _rosterRefreshTask = null;
        _rosterChecked = false;
        _relatedContextItems.Clear();
        _liveParticipants.Clear();
        _rosterCheckedAt = null;
        CloseCatchUp();
        PopulateScheduledInvitees();
        RenderRelatedContext(_relatedContext.Status);
        RenderRoster(null);
        _aiUsageTransitionKey = null;
        RenderAiUsage(refreshDetails: true, announceTransition: false);
        _questions.Clear();
        _allQuestions.Clear();
        _actions.Clear();
        _allActions.Clear();
        UpdateExpanderHeaders();

        TldrChip.Text = "Listening for speech...";
    }

    private void UpdateExpanderHeaders()
    {
        OpenQuestionsHeader.Text = ChecklistPresentation.Header("Questions", _questions);
        ActionsHeader.Text = ChecklistPresentation.Header("Actions", _actions);
        OpenQuestionsItemsScroll.Visibility = _questions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionsItemsScroll.Visibility = _actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRecordingLayout()
    {
        if (RecordingBodyScroll.ActualHeight <= 0) return;

        var fixedHeight = InsightSections.ActualHeight + SpeakerSection.ActualHeight +
            RecordingUtilityActions.ActualHeight + 24;
        var allocation = RecordingLayout.Calculate(
            RecordingBodyScroll.ActualHeight,
            fixedHeight);

        if (Math.Abs(RecordingOverviewScroll.MaxHeight - allocation.OverviewMaxHeight) > 1)
            RecordingOverviewScroll.MaxHeight = allocation.OverviewMaxHeight;
        if (Math.Abs(TranscriptList.MaxHeight - allocation.TranscriptMaxHeight) > 1)
            TranscriptList.MaxHeight = allocation.TranscriptMaxHeight;
    }

    private void OnChecklistItemToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ChecklistItemViewModel item } checkBox)
        {
            item.IsCompleted = checkBox.IsChecked == true;
            UpdateExpanderHeaders();
        }
    }

    private void OnChecklistItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChecklistItemViewModel.IsCompleted))
            UpdateExpanderHeaders();
    }

    // ── Live time indicator ─────────────────────────────────────────────────────

    private void StartRecordingTimer()
    {
        StopRecordingTimer();
        _recordingCts = new CancellationTokenSource();
        UpdateTimeIndicator();

        _recordingTimer = _dispatcherQueue.CreateTimer();
        _recordingTimer.Interval = TimeSpan.FromSeconds(30);
        _recordingTimer.IsRepeating = true;
        _recordingTimer.Tick += (_, _) => UpdateTimeIndicator();
        _recordingTimer.Start();

        _tldrTimer = _dispatcherQueue.CreateTimer();
        _tldrTimer.Interval = TimeSpan.FromSeconds(30);
        _tldrTimer.IsRepeating = true;
        _tldrTimer.Tick += (_, _) => { var cts = _recordingCts; if (cts is not null) _ = RefreshTldrAsync(cts.Token); };
        _tldrTimer.Start();

        _intelligenceTimer = _dispatcherQueue.CreateTimer();
        _intelligenceTimer.Interval = TimeSpan.FromSeconds(90);
        _intelligenceTimer.IsRepeating = true;
        _intelligenceTimer.Tick += (_, _) => { var cts = _recordingCts; if (cts is not null) _ = RefreshIntelligenceAsync(cts.Token); };
        _intelligenceTimer.Start();

    }

    private void StopRecordingTimer()
    {
        _recordingTimer?.Stop();
        _recordingTimer = null;

        _tldrTimer?.Stop();
        _tldrTimer = null;

        _intelligenceTimer?.Stop();
        _intelligenceTimer = null;
        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        _recordingCts = null;
    }

    private void StartSaveReview(PipelineStatus status)
    {
        if (_saveReviewSessionId != status.SessionId)
            _saveDecisionSubmitted = false;

        _saveReviewSessionId = status.SessionId;
        _saveReviewDeadline = status.SaveDeadline;
        _saveReviewTimer ??= CreateSaveReviewTimer();
        UpdateSaveReviewPresentation();
        _saveReviewTimer.Start();
    }

    private DispatcherQueueTimer CreateSaveReviewTimer()
    {
        var timer = _dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => UpdateSaveReviewPresentation();
        return timer;
    }

    private void StopSaveReview()
    {
        _saveReviewTimer?.Stop();
        _saveReviewTimer = null;
        _saveReviewDeadline = null;
        _saveReviewSessionId = null;
        _saveDecisionSubmitted = false;
        if (SaveDecisionPanel is not null)
            SaveDecisionPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateSaveReviewPresentation()
    {
        var presentation = SaveReviewPresentation.Create(
            _pipeline.State == PipelineState.ReviewingSave,
            _saveReviewDeadline,
            DateTimeOffset.UtcNow,
            _saveDecisionSubmitted);
        SaveDecisionPanel.Visibility = presentation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        SaveCountdownFillTransform.ScaleX = presentation.Progress;
        SaveCountdownButtonLabel.Text = presentation.ButtonText;
        AutomationProperties.SetName(SaveRecordingNowButton, presentation.AccessibleName);
        AutomationProperties.SetHelpText(SaveRecordingNowButton, presentation.AccessibleName);
        var canDecide = !_preparingExit && presentation.CanDecide && _pipeline.CanReviewSave;
        DiscardRecordingButton.IsEnabled = canDecide;
        SaveRecordingNowButton.IsEnabled = canDecide;
    }

    private void OnDiscardRecordingClick(object sender, RoutedEventArgs e)
    {
        if (_saveDecisionSubmitted || _saveReviewSessionId is not { } sessionId)
            return;

        _saveDecisionSubmitted = true;
        _saveReviewTimer?.Stop();
        _pipeline.DiscardPendingRecording(sessionId);
        RefreshSaveReviewFromPipelineStatus(sessionId);
        UpdateSaveReviewPresentation();
    }

    private void OnSaveRecordingNowClick(object sender, RoutedEventArgs e)
    {
        if (_saveDecisionSubmitted || _saveReviewSessionId is not { } sessionId)
            return;

        _saveDecisionSubmitted = true;
        _saveReviewTimer?.Stop();
        _pipeline.SaveNow(sessionId);
        RefreshSaveReviewFromPipelineStatus(sessionId);
        UpdateSaveReviewPresentation();
    }

    private void RefreshSaveReviewFromPipelineStatus(string sessionId)
    {
        var status = _pipeline.Status;
        if (status.State == PipelineState.ReviewingSave && status.SessionId == sessionId)
        {
            _saveReviewSessionId = status.SessionId;
            _saveReviewDeadline = status.SaveDeadline;
            return;
        }

        StopSaveReview();
    }

    private void ClearDiscardedSessionUi()
    {
        _showCompletedSession = false;
        _sessionId = null;
        _startedAt = null;
        _notesPath = null;
        _transcriptRevision = -1;
        _finalizedSegmentCount = 0;
        _hasSummary = false;
        _transcriptState.Reset();
        _speakerBrushes.Clear();
        _speakerWordCounts.Clear();
        _speakerBalance.Clear();
        _speakerAvatarImages.Clear();
        _speakerAvatarRevisions.Clear();
        _selectedSpeakerNameOption = null;
        _contextCts?.Cancel();
        _contextCts?.Dispose();
        _relatedContext.Invalidate();
        _acceptedRelatedContextTopic = null;
        _relatedContextStatesByTopicId.Clear();
        _appliedRelatedContextSnapshots.Clear();
        _loadedVisualization = null;
        ClearVisualizationPresentation();
        CloseTopicMap();
        _contextCts = null;
        _rosterRefreshTask = null;
        _rosterChecked = false;
        _relatedContextItems.Clear();
        _liveParticipants.Clear();
        _scheduledInvitees.Clear();
        _totalWords = 0;
        _panelTranscriptThrough.Clear();
        _questions.Clear();
        _allQuestions.Clear();
        _actions.Clear();
        _allActions.Clear();
        ResetSpeakerPrompts();
        UpdateExpanderHeaders();
        RecordingTitleText.Text = "Meeting notes";
        TldrChip.Text = string.Empty;
        SummaryStatusText.Text = string.Empty;
        SavedPathText.Text = string.Empty;
        TranscriptList.Visibility = Visibility.Collapsed;
        EmptyStateLabel.Visibility = Visibility.Visible;
        EmptyStateLabel.Text = "Waiting for a recording.";
        InvalidatePanels();
    }

    private void UpdateTimeIndicator()
    {
        var start = _startedAt ?? DateTimeOffset.Now;
        var elapsedMin = Math.Max(0, (int)(DateTimeOffset.Now - start).TotalMinutes);

        TimeIndicator.Text = $"{elapsedMin} min elapsed";
    }

    // ── Real-time intelligence panel updates ────────────────────────────────────

    private Task RefreshTldrAsync(CancellationToken ct) => RefreshPanelAsync(_summaryState);
    private Task RefreshIntelligenceAsync(CancellationToken ct)
        => Task.WhenAll(RefreshPanelAsync(_questionsState), RefreshPanelAsync(_actionsState));

    private async void OnSummaryRetryClick(object sender, RoutedEventArgs e) => await RefreshPanelAsync(_summaryState, true);
    private async void OnQuestionsRetryClick(object sender, RoutedEventArgs e) => await RefreshPanelAsync(_questionsState, true);
    private async void OnActionsRetryClick(object sender, RoutedEventArgs e) => await RefreshPanelAsync(_actionsState, true);

    private TranscriptSnapshot? DisplayedSnapshot
        => _pipeline.CurrentTranscript is { } current && current.SessionId == _sessionId ? current
            : _pipeline.LastCompletedTranscript is { } completed && completed.SessionId == _sessionId ? completed : null;

    private void InvalidatePanels()
    {
        var revision = _settingsManager.GetIntelligenceSnapshot().Revision;
        foreach (var panel in new[] { _summaryState, _questionsState, _actionsState })
            panel.Invalidate(_sessionId ?? "", revision);
        RenderPanelStatuses();
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        InvokeOnUi(() =>
        {
            if (e.IntelligenceChanged)
            {
                _retryCts?.Cancel();
                StopRecordingTimer();
                InvalidatePanels();
                if (_pipeline.State == PipelineState.Recording) StartRecordingTimer();
            }
            var restart = e.RestartRequiredSections.Count == 0 ? ""
                : $" Restart required for: {string.Join(", ", e.RestartRequiredSections)}.";
            ShowStatusBar("Intelligence settings are applied to new requests. Retry uses the retained transcript." + restart,
                InfoBarSeverity.Informational);
        });
    }

    private async Task RefreshPanelAsync(PanelRefreshState panel, bool force = false, bool catchUp = false)
    {
        var cts = _recordingCts;
        var snapshot = DisplayedSnapshot;
        if (_pipeline.State != PipelineState.Recording || cts is null || snapshot is null) return;
        var config = _settingsManager.GetIntelligenceSnapshot();
        if (snapshot.Segments.Count == 0)
        {
            RenderPanelStatuses();
            return;
        }
        var request = panel.TryBegin(snapshot.SessionId, snapshot.Revision, config.Revision, force);
        if (request is not { } generation) return;
        var speakerNameRevision = Volatile.Read(ref _speakerNameRevision);
        var ct = cts.Token;
        if (panel == _summaryState && !_hasSummary && !catchUp)
        {
            TldrChip.Text = "Updating from the latest conversation...";
        }
        RenderPanelStatuses();
        try
        {
            var recent = catchUp
                ? snapshot.Segments
                    .Where(s => s.Timestamp >= DateTimeOffset.Now.AddMinutes(-10))
                    .TakeLast(150)
                : snapshot.Segments.TakeLast(50);
            var text = string.Join("\n", recent.Select(s => $"{s.SpeakerDisplayName ?? s.SpeakerId}: {s.Text}"));
            Action apply;
            bool hasResults;
            if (panel == _summaryState)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    apply = catchUp
                        ? () => RenderCatchUpFailure(
                            "No recognized speech in the last 10 minutes to catch up on.")
                        : () => ShowAutoInfoBar("Catch me up", "No recognized speech to summarize.");
                    hasResults = false;
                }
                else
                {
                    var summary = await Task.Run(
                        () => catchUp
                            ? _intelligence.CatchUpAsync(text, ct)
                            : _intelligence.SummarizeRecentAsync(text, ct),
                        ct);
                    var renderedSummary = catchUp ? ProjectVisualizationText(summary) : summary;
                    hasResults = !string.IsNullOrWhiteSpace(renderedSummary);
                    apply = () =>
                    {
                        // Catch-up is prose for its own surface; it never replaces Current topic.
                        if (catchUp)
                        {
                            RenderCatchUp(renderedSummary);
                            return;
                        }
                        TldrChip.Text = renderedSummary;
                        _hasSummary = true;
                        TriggerRelatedContext(
                            snapshot.SessionId,
                            renderedSummary,
                            snapshot.Revision,
                            snapshot.Segments.Count,
                            ct);
                    };
                }
            }
            else if (panel == _questionsState)
            {
                var questions = await Task.Run(() => _intelligence.GetOpenQuestionsAsync(text, ct), ct);
                hasResults = questions.Count > 0;
                apply = () =>
                {
                    foreach (var question in InsightRetention.NewItems(_allQuestions.Keys, questions))
                    {
                        var item = new ChecklistItemViewModel { Text = question };
                        item.PropertyChanged += OnChecklistItemChanged;
                        _allQuestions.Add(question, item);
                        _questions.Add(item);
                    }
                };
            }
            else
            {
                var actions = await Task.Run(() => _intelligence.GetActionsAsync(text, ct), ct);
                hasResults = actions.Count > 0;
                apply = () =>
                {
                    foreach (var action in InsightRetention.NewItems(_allActions.Keys, actions))
                    {
                        var item = new ChecklistItemViewModel { Text = action };
                        item.PropertyChanged += OnChecklistItemChanged;
                        _allActions.Add(action, item);
                        _actions.Add(item);
                    }
                };
            }
            if (!ct.IsCancellationRequested &&
                speakerNameRevision == Volatile.Read(ref _speakerNameRevision) &&
                _sessionId == snapshot.SessionId &&
                panel.Complete(generation, snapshot.Revision, hasResults, null))
            {
                _panelTranscriptThrough[panel] = snapshot.Segments[^1].Timestamp;
                apply();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var failure = ex is IntelligenceException typed ? typed.Failure
                : new IntelligenceFailure(ex is OperationCanceledException ? IntelligenceFailureKind.Timeout : IntelligenceFailureKind.Unknown,
                    config.Settings.Provider,
                    config.Settings.Provider == "github"
                        ? config.Settings.IntelligenceModel : config.Settings.IntelligenceDeployment,
                    "Request failed. Check settings, account access and connection, then retry.");
            _logger.LogWarning("Panel refresh failed: {Kind} ({Type}).", failure.Kind, ex.GetType().Name);
            if (!ct.IsCancellationRequested && _sessionId == snapshot.SessionId)
                panel.Complete(generation, snapshot.Revision, false, failure);
        }
        finally
        {
            panel.End(generation);
            RenderPanelStatuses();
        }
    }

    private void RenderPanelStatuses()
    {
        if (_allowClose) return;
        static string CompactStatus(PanelRefreshState panel)
        {
            return panel.Status.State switch
            {
                PanelState.Pending => "Waiting for conversation.",
                PanelState.Refreshing => "Updating...",
                PanelState.SucceededEmpty => string.Empty,
                PanelState.SucceededWithResults => string.Empty,
                PanelState.Unavailable or PanelState.Failed =>
                    panel.Status.Failure?.Message ?? "Could not update. Retry when ready.",
                _ => "Could not update. Retry when ready."
            };
        }
        QuestionsStatusText.Text = CompactStatus(_questionsState);
        ActionsStatusText.Text = CompactStatus(_actionsState);
        QuestionsStatusText.Visibility = _questionsState.Status.State
            is PanelState.SucceededEmpty or PanelState.SucceededWithResults
            ? Visibility.Collapsed
            : Visibility.Visible;
        ActionsStatusText.Visibility = _actionsState.Status.State
            is PanelState.SucceededEmpty or PanelState.SucceededWithResults
            ? Visibility.Collapsed
            : Visibility.Visible;
        var summaryFailure = _summaryState.Status.Failure ??
            (_pipeline.State == PipelineState.Recording ? null : _pipeline.Status.IntelligenceFailure);
        SummaryStatusText.Text = _pipeline.State == PipelineState.Generating
            ? "Your transcript is saved before AI notes are generated."
            : _summaryState.Status.State switch
        {
            PanelState.Pending => "Waiting for enough conversation.",
            PanelState.Refreshing => "Updating from the latest transcript...",
            PanelState.SucceededEmpty => "No clear topic yet.",
            PanelState.SucceededWithResults => "Up to date.",
            PanelState.Unavailable or PanelState.Failed =>
                _summaryState.Status.Failure?.Message ?? "Topics could not be updated. Retry when ready.",
            _ => "Topics could not be updated. Retry when ready."
        };
        if (summaryFailure is not null)
        {
            if (_hasSummary)
            {
                SummaryStatusText.Text = "Keeping the last grounded topic while analysis reconnects.";
            }
            else
            {
                TldrChip.Text = summaryFailure.Kind switch
                {
                    IntelligenceFailureKind.ModelUnavailable =>
                        "Topic analysis needs an AI model connection.",
                    IntelligenceFailureKind.RateLimited =>
                        "Topic analysis is paused by a Copilot rate limit.",
                    IntelligenceFailureKind.QuotaExceeded =>
                        "Topic analysis is unavailable because Copilot quota is exhausted.",
                    _ => "Topic analysis is temporarily unavailable."
                };
                SummaryStatusText.Text = "Topic analysis is temporarily unavailable.";
            }
        }
        else if (!_hasSummary && _transcriptRevision >= 0)
            TldrChip.Text = _pipeline.State switch
            {
                PipelineState.Recording => "Listening for enough conversation...",
                PipelineState.Saved => "Transcript saved - open notes",
                PipelineState.Failed => "Transcript retained - check recording status",
                _ => "Transcript captured - finalizing notes"
            };
        var recording = _pipeline.State == PipelineState.Recording;
        QuestionsRetryButton.IsEnabled = recording && _finalizedSegmentCount > 0 && !_questionsState.IsBusy;
        ActionsRetryButton.IsEnabled = recording && _finalizedSegmentCount > 0 && !_actionsState.IsBusy;
        QuestionsRetryButton.Visibility = recording && _finalizedSegmentCount > 0 && _questionsState.Status.Failure is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ActionsRetryButton.Visibility = recording && _finalizedSegmentCount > 0 && _actionsState.Status.Failure is not null
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateRecordingPresentation();
        UpdateExpanderHeaders();
    }

    // ── Speaker balance ─────────────────────────────────────────────────────────

    private void UpdateSpeakerBalance(string speakerId, string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0)
            return;

        _speakerWordCounts.TryGetValue(speakerId, out var count);
        _speakerWordCounts[speakerId] = count + words;
        _totalWords += words;
        _lastFinalSpeakerId = speakerId;

        if (_totalWords == 0)
            return;

        var top = _speakerWordCounts
            .OrderByDescending(kv => kv.Value)
            .Take(4)
            .Select(kv => new SpeakerBalance
            {
                SpeakerId = kv.Key,
                Name = _pipeline.SpeakerResolver.GetDisplayName(kv.Key),
                Percent = Math.Round(100.0 * kv.Value / _totalWords),
            })
            .ToList();

        _speakerBalance.Clear();
        foreach (var s in top)
            _speakerBalance.Add(s);
    }

    private void AppendTranscriptSegment(
        long revision,
        TranscriptionSegment segment,
        bool isNewRevision)
    {
        if (!_hasSummary && _summaryState.Status.Failure is null)
            TldrChip.Text = "Listening for enough conversation...";
        if (isNewRevision)
        {
            UpdateSpeakerBalance(
                string.IsNullOrWhiteSpace(segment.SpeakerId) ? "Unknown" : segment.SpeakerId,
                segment.Text);
            if (_totalWords >= 30 && _summaryState.Status.LastAttemptAt is null)
                _ = RefreshPanelAsync(_summaryState);
        }
        if (!_viewport.FollowLatest)
        {
            var existing = _transcript.FirstOrDefault(line => line.Revision == revision);
            if (existing is not null)
                UpdateTranscriptLine(existing, segment, revision);
            LatestButton.Content = "New content - latest";
            return;
        }
        EmptyStateLabel.Visibility = Visibility.Collapsed;
        TranscriptList.Visibility = Visibility.Visible;
        var result = _transcriptState.ApplyFinal(
            revision,
            () => CreateTranscriptLine(segment, revision),
            line => UpdateTranscriptLine(line, segment, revision));
        _viewport.Update((int)Math.Min(int.MaxValue, _transcriptRevision));
        if (result.RequiresFollow)
            ScrollToLatest();
    }

    private TranscriptLine CreateTranscriptLine(TranscriptionSegment segment, long revision)
    {
        var rawSpeaker = string.IsNullOrWhiteSpace(segment.SpeakerId) ? "Unknown" : segment.SpeakerId;
        var display = _pipeline.SpeakerResolver.GetDisplayName(rawSpeaker);
        var avatar = SpeakerAvatarPresentation.Create(
            display,
            _pipeline.SpeakerResolver.IsResolved(rawSpeaker));
        var ts = segment.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        return new TranscriptLine
        {
            Speaker = display,
            SpeakerId = rawSpeaker,
            Revision = revision,
            Timestamp = ts,
            Text = segment.Text,
            SpeakerBrush = GetSpeakerBrush(rawSpeaker),
            AvatarInitials = avatar.Initials,
            AvatarAccessibleName = avatar.AccessibleName,
            AvatarImage = _speakerAvatarImages.GetValueOrDefault(rawSpeaker),
            Opacity = 1.0,
        };
    }

    private TranscriptLine CreatePartialTranscriptLine(
        string speakerId,
        string display,
        string text)
    {
        var avatar = SpeakerAvatarPresentation.Create(
            display,
            _pipeline.SpeakerResolver.IsResolved(speakerId));
        return new()
        {
            Speaker = $"{display} (provisional)",
            SpeakerId = speakerId,
            Text = text,
            SpeakerBrush = GetSpeakerBrush(speakerId),
            AvatarInitials = avatar.Initials,
            AvatarAccessibleName = avatar.AccessibleName,
            AvatarImage = _speakerAvatarImages.GetValueOrDefault(speakerId),
            Opacity = 0.65,
        };
    }

    private bool UpdatePartialTranscriptLine(
        TranscriptLine line,
        string speakerId,
        string display,
        string text)
    {
        var avatar = SpeakerAvatarPresentation.Create(
            display,
            _pipeline.SpeakerResolver.IsResolved(speakerId));
        var brush = GetSpeakerBrush(speakerId);
        var image = _speakerAvatarImages.GetValueOrDefault(speakerId);
        var changed = line.Speaker != $"{display} (provisional)" ||
            line.SpeakerId != speakerId ||
            line.Text != text ||
            !ReferenceEquals(line.SpeakerBrush, brush) ||
            line.AvatarInitials != avatar.Initials ||
            line.AvatarAccessibleName != avatar.AccessibleName ||
            !ReferenceEquals(line.AvatarImage, image) ||
            line.Opacity != 0.65;
        line.Speaker = $"{display} (provisional)";
        line.SpeakerId = speakerId;
        line.Text = text;
        line.SpeakerBrush = brush;
        line.AvatarInitials = avatar.Initials;
        line.AvatarAccessibleName = avatar.AccessibleName;
        line.AvatarImage = image;
        line.Opacity = 0.65;
        return changed;
    }

    private bool UpdateTranscriptLine(
        TranscriptLine line,
        TranscriptionSegment segment,
        long revision)
    {
        var rawSpeaker = string.IsNullOrWhiteSpace(segment.SpeakerId) ? "Unknown" : segment.SpeakerId;
        var display = _pipeline.SpeakerResolver.GetDisplayName(rawSpeaker);
        var avatar = SpeakerAvatarPresentation.Create(
            display,
            _pipeline.SpeakerResolver.IsResolved(rawSpeaker));
        var brush = GetSpeakerBrush(rawSpeaker);
        var image = _speakerAvatarImages.GetValueOrDefault(rawSpeaker);
        var timestamp = $"{segment.Timestamp.LocalDateTime:HH:mm:ss}";
        var changed = line.Revision != revision ||
            line.Speaker != display ||
            line.SpeakerId != rawSpeaker ||
            line.Timestamp != timestamp ||
            line.Text != segment.Text ||
            !ReferenceEquals(line.SpeakerBrush, brush) ||
            line.AvatarInitials != avatar.Initials ||
            line.AvatarAccessibleName != avatar.AccessibleName ||
            !ReferenceEquals(line.AvatarImage, image) ||
            line.Opacity != 1;
        line.Revision = revision;
        line.Speaker = display;
        line.SpeakerId = rawSpeaker;
        line.Timestamp = timestamp;
        line.Text = segment.Text;
        line.SpeakerBrush = brush;
        line.AvatarInitials = avatar.Initials;
        line.AvatarAccessibleName = avatar.AccessibleName;
        line.AvatarImage = image;
        line.Opacity = 1;
        return changed;
    }

    private void OnTranscriptLoaded(object sender, RoutedEventArgs e)
    {
        if (_transcriptScroller is not null) return;
        _transcriptScroller = FindScroller(TranscriptList);
        if (_transcriptScroller is not null)
            _transcriptScroller.ViewChanged += OnTranscriptViewChanged;
    }

    private void OnTranscriptViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroller ||
            Volatile.Read(ref _transcriptScrollQueued) != 0)
            return;

        var atBottom = scroller.ScrollableHeight - scroller.VerticalOffset <= 8;
        if (atBottom && !_viewport.FollowLatest)
        {
            _viewport.Latest();
            RenderTranscriptPage();
        }
        else if (!atBottom && _viewport.FollowLatest)
        {
            _viewport.Pause();
            UpdateRecordingPresentation();
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    private void ScrollToLatest()
    {
        if (!_viewport.FollowLatest || _transcript.Count == 0) return;
        if (Interlocked.Exchange(ref _transcriptScrollQueued, 1) != 0)
            return;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_viewport.FollowLatest && _transcript.Count > 0)
                    TranscriptList.ScrollIntoView(_transcript[^1]);
            }
            finally
            {
                Interlocked.Exchange(ref _transcriptScrollQueued, 0);
            }
        }))
        {
            Interlocked.Exchange(ref _transcriptScrollQueued, 0);
        }
    }

    private void RenderTranscriptPage()
    {
        var snapshot = DisplayedSnapshot;
        if (snapshot is null) return;
        var page = _viewport.Update(snapshot.Segments.Count);
        var segments = snapshot.Segments.Skip(page.Start).Take(page.Count).ToArray();
        _transcriptState.Reconcile(
            segments,
            page.Start + 1L,
            segment => CreateTranscriptLine(segment, 0),
            (line, segment) => UpdateTranscriptLine(line, segment, line.Revision));
        TranscriptList.Visibility = page.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateLabel.Visibility = page.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        _finalizedSegmentCount = snapshot.Segments.Count;
        TranscriptHeaderText.Text = $"Transcript {page.Start + (page.Count > 0 ? 1 : 0)}-{page.Start + page.Count} / {snapshot.Segments.Count}";
        LatestButton.Content = "Latest";
        UpdateRecordingPresentation();
        if (_viewport.FollowLatest) ScrollToLatest();
        else if (_transcript.Count > 0) TranscriptList.ScrollIntoView(_transcript[0]);
    }

    private void OnEarlierClick(object sender, RoutedEventArgs e) { _viewport.Earlier(); RenderTranscriptPage(); }
    private void OnLaterClick(object sender, RoutedEventArgs e) { _viewport.Later(DisplayedSnapshot?.Segments.Count ?? 0); RenderTranscriptPage(); }
    private void OnLatestClick(object sender, RoutedEventArgs e) { _viewport.Latest(); RenderTranscriptPage(); }

    private void UpdateDiagnostics()
    {
        var diagnostics = _pipeline.GetDiagnostics();
        if (diagnostics.SessionId is not null && diagnostics.SessionId != _sessionId) return;
        var sources = diagnostics.Audio.Select(source =>
            $"{source.Source}: {source.State}, level {(source.Level is { } level ? $"{level:P0}" : "unavailable")}, " +
            $"{source.ReceivedBytes:N0} bytes / {source.ReceivedBuffers:N0} buffers, dropped {source.DroppedBuffers}, " +
            $"last audio {(source.LastAudioAt is { } time ? time.ToLocalTime().ToString("T") : "none")}" +
            (source.Error is null ? "" : $". {source.Error}"));
        var audio = string.Join("\n", sources);
        var microphone = diagnostics.Audio.FirstOrDefault(source => source.Source == "microphone");
        var loopback = diagnostics.Audio.FirstOrDefault(source => source.Source == "loopback");
        MicrophoneMeter.IsEnabled = microphone?.Level is not null;
        MicrophoneMeter.Value = microphone?.Level ?? 0;
        LoopbackMeter.IsEnabled = loopback?.Level is not null;
        LoopbackMeter.Value = loopback?.Level ?? 0;
        var speech = diagnostics.Transcription;
        HealthText.Text = audio + $"\nSpeech: {speech.State}, {speech.ReceivedBytes:N0} bytes, " +
            $"last transcript {(speech.LastTranscriptAt is { } last ? last.ToLocalTime().ToString("T") : "none")}, " +
            $"push {speech.PushWriteCount:N0}, last {speech.LastPushWriteMilliseconds:F1} ms / p95 {speech.PushWriteP95Milliseconds:F1} ms" +
            (speech.Error is null ? "" : $". {speech.Error}");
        PreviewHealthText.Text = audio;
        if (_pipeline.State == PipelineState.Recording) UpdateTimeIndicator();
        UpdateRecordingPresentation();
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_previewBusy || (!_preview && !_pipeline.CanStart)) return;
        _previewBusy = true;
        UpdateControlState();
        try
        {
            if (_preview) await Task.Run(() => _pipeline.StopAudioPreviewAsync(CancellationToken.None));
            else await Task.Run(() => _pipeline.StartAudioPreviewAsync(CancellationToken.None));
            _preview = !_preview;
            PreviewButton.Content = _preview ? "Stop audio preview" : "Start audio preview";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Audio preview failed ({Type}).", ex.GetType().Name);
            ShowStatusBar("Audio preview could not change state. Check audio settings and device access.", InfoBarSeverity.Error);
        }
        finally { _previewBusy = false; UpdateControlState(); }
    }

    private void OnDismissMeetingClick(object sender, RoutedEventArgs e)
        => MeetingDismissRequested?.Invoke(this, EventArgs.Empty);
    private void OnSnoozeClick(object sender, RoutedEventArgs e)
    {
        MeetingSnoozeRequested?.Invoke(this, EventArgs.Empty);
        ShowStatusBar("Meeting detection snoozed for 30 minutes.", InfoBarSeverity.Informational);
    }
    private void OnResumeDetectionClick(object sender, RoutedEventArgs e)
    {
        _pipeline.ResumeDetection();
        ShowStatusBar("Meeting detection resumed.", InfoBarSeverity.Informational);
    }

    private void OnOpenSavedClick(object sender, RoutedEventArgs e)
    {
        if (_notesPath is not null)
            OpenMarkdownFile(_notesPath);
    }

    private bool OpenMarkdownFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Open saved notes failed ({Type}).", ex.GetType().Name);
            ShowStatusBar("Unable to open saved notes. The saved path is shown above.", InfoBarSeverity.Error);
            return false;
        }
    }

    private async void OnRetrySavedClick(object sender, RoutedEventArgs e)
    {
        if (_retryBusy || !_pipeline.CanStart || _sessionId is null) return;
        var sessionId = _sessionId;
        _retryBusy = true;
        using var retryCts = new CancellationTokenSource();
        _retryCts = retryCts;
        UpdateControlState();
        try
        {
            var path = await Task.Run(() => _pipeline.RetryIntelligenceAsync(sessionId, retryCts.Token));
            if (_sessionId == sessionId)
            {
                _notesPath = path;
                SavedPathText.Text = $"Saved: {path}";
            }
        }
        catch (OperationCanceledException) when (retryCts.IsCancellationRequested)
        {
            if (_sessionId == sessionId)
                ShowStatusBar("AI retry cancelled. Retained transcript and saved notes remain available.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Completed AI retry failed ({Type}).", ex.GetType().Name);
            if (_sessionId == sessionId)
                ShowStatusBar(ex is IntelligenceException typed ? typed.Failure.Message
                    : "AI retry failed. Retained transcript and any saved notes remain available.", InfoBarSeverity.Error);
        }
        finally { _retryCts = null; _retryBusy = false; UpdateControlState(); }
    }

    private void OnCancelRetryClick(object sender, RoutedEventArgs e)
    {
        _retryCts?.Cancel();
        CancelRetryButton.IsEnabled = false;
        LifecycleText.Text = "Cancelling AI retry; waiting for safe cleanup...";
    }

    /// <summary>Returns a stable colour brush for a speaker, assigning palette colours on first sight.</summary>
    private Brush GetSpeakerBrush(string speaker)
    {
        if (_speakerBrushes.TryGetValue(speaker, out var brush))
            return brush;

        var color = SpeakerPalette[_speakerBrushes.Count % SpeakerPalette.Length];
        brush = new SolidColorBrush(color);
        _speakerBrushes[speaker] = brush;
        return brush;
    }

    private void SetChipStatus(string text, bool isRecording)
    {
        StatusText.Text = text;
        RecordingBadge.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateControlState()
    {
        var newRecording = RecordingViewState.NewRecordingAction(_pipeline.Status, _pipeline.CanStart,
            _preparingExit, _retryBusy, _previewBusy);
        StartButton.IsEnabled = NewRecordingButton.IsEnabled = newRecording.Enabled;
        BackToMeetingsButton.IsEnabled = StartButton.IsEnabled;
        StopButton.IsEnabled = PreferencesStopButton.IsEnabled = !_preparingExit && _pipeline.CanStop;
        PreferencesStopButton.Visibility = _pipeline.CanStop ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.IsEnabled = !_preparingExit && (_preview || _pipeline.CanStart) && !_previewBusy && !_retryBusy;
        OpenSavedButton.IsEnabled = _notesPath is not null;
        OpenSavedButton.Visibility = _notesPath is not null ? Visibility.Visible : Visibility.Collapsed;
        SavedPathText.Visibility = _notesPath is not null ? Visibility.Visible : Visibility.Collapsed;
        RetrySavedButton.IsEnabled = !_preparingExit && _pipeline.CanStart && !_retryBusy &&
            _pipeline.LastCompletedTranscript is { Segments.Count: > 0 } completed && completed.SessionId == _sessionId;
        RetrySavedButton.Visibility = RetrySavedButton.IsEnabled || _retryBusy ? Visibility.Visible : Visibility.Collapsed;
        NewRecordingButton.Visibility = NewRecordingHint.Visibility = newRecording.Visible ? Visibility.Visible : Visibility.Collapsed;
        NewRecordingHint.Text = newRecording.Message;
        AutomationProperties.SetHelpText(NewRecordingButton, newRecording.Message);
        BackToMeetingsButton.Visibility = _pipeline.CanStart ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = _pipeline.CanStop ? Visibility.Visible : Visibility.Collapsed;
        CancelRetryButton.Visibility = _retryBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelRetryButton.IsEnabled = _retryCts is { IsCancellationRequested: false };
        RecordingUtilityActions.Visibility =
            OpenSavedButton.Visibility == Visibility.Visible ||
            RetrySavedButton.Visibility == Visibility.Visible ||
            CancelRetryButton.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
        UpdateSaveReviewPresentation();
        UpdateRecordingPresentation();
    }

    private void UpdateRecordingPresentation()
    {
        if (_allowClose || _sessionId is null) return;
        var state = RecordingViewState.Create(_pipeline.State, _finalizedSegmentCount,
            _viewport.FollowLatest, _summaryState.Status.Failure is not null, _summaryState.IsBusy);
        TranscriptNavigation.Visibility = state.ShowPaging || state.ShowLatest ? Visibility.Visible : Visibility.Collapsed;
        EarlierButton.Visibility = LaterButton.Visibility = state.ShowPaging ? Visibility.Visible : Visibility.Collapsed;
        EarlierButton.IsEnabled = _viewport.Start > 0;
        LaterButton.IsEnabled = _viewport.Start + TranscriptViewport.Capacity < _finalizedSegmentCount;
        TranscriptList.Visibility = _transcript.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateLabel.Visibility = _transcript.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateLabel.Text = state.EmptyMessage;
        if (_viewport.FollowLatest)
            TranscriptHeaderText.Text = _pipeline.State == PipelineState.Recording ? "Live transcript" : "Transcript";
        SummaryRetryButton.Visibility = state.ShowSummaryRetry ? Visibility.Visible : Visibility.Collapsed;
        SummaryRetryButton.IsEnabled = WhatDidIMissButton.IsEnabled = !_preparingExit && state.CanSummarize;
        WhatDidIMissButton.Visibility = state.ShowCatchUp ? Visibility.Visible : Visibility.Collapsed;
        LifecycleText.Visibility = _pipeline.State is PipelineState.Starting or PipelineState.Recording
            ? Visibility.Collapsed : Visibility.Visible;
        SpeakerActivityText.Visibility = _totalWords > 0 || _partialItem is not null ? Visibility.Visible : Visibility.Collapsed;
        SpeakerActivityText.Text = _totalWords > 0
            ? $"{_speakerWordCounts.Count} speaker{(_speakerWordCounts.Count == 1 ? "" : "s")}"
            : "Recognizing speech...";
        if (_pipeline.State is PipelineState.Saved or PipelineState.Failed)
        {
            if (!_hasSummary) TldrChip.Text = _finalizedSegmentCount == 0 ? "No speech was recognized." : "Recording ended";
            if (_finalizedSegmentCount == 0) SummaryStatusText.Text = "";
            else if (_summaryState.Status.State is PanelState.Pending or PanelState.Refreshing)
                SummaryStatusText.Text = _notesPath is not null
                    ? "Live topics stopped. Open saved notes for the final result."
                    : "Live topics stopped. Check the recording error.";
        }
    }

    private void ShowStatusBar(string message, InfoBarSeverity severity)
    {
        InvokeOnUi(() =>
        {
            StatusBar.Severity = severity;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        });
    }

    // ── Related context and roster flyout ──────────────────────────────────────

    private void OnTopicMapToggleClick(object sender, RoutedEventArgs e)
    {
        if (TopicMapToggleButton.IsChecked == true)
            OpenTopicMap();
        else
            CloseTopicMap();
    }

    private void OnTopicMapCloseClick(object sender, RoutedEventArgs e) => CloseTopicMap();

    private void OpenTopicMap()
    {
        if (TopicMapPanel.Visibility == Visibility.Visible)
            return;
        if (RecordingPanel.Visibility != Visibility.Visible && _loadedVisualization is null)
        {
            TopicMapToggleButton.IsChecked = false;
            ShowStatusBar(
                "Start a recording or open a saved meeting before showing the visualization.",
                InfoBarSeverity.Informational);
            return;
        }

        var current = CurrentWindowBounds();
        var expanded = current;
        if (_presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Restored
            && !WindowPlacement.TryExpandForSidePanel(
                current,
                CurrentWorkArea(),
                TopicMapPanelWidth,
                CurrentWindowDpi(),
                out expanded))
        {
            TopicMapToggleButton.IsChecked = false;
            ShowStatusBar(
                "The meeting visualization needs more horizontal screen space. Resize or move the window to a larger work area.",
                InfoBarSeverity.Informational);
            return;
        }

        _topicMapAddedWidth = Math.Max(0, expanded.Width - current.Width);
        if (_topicMapAddedWidth > 0)
            AppWindow.MoveAndResize(new(expanded.X, expanded.Y, expanded.Width, expanded.Height));
        TopicMapColumn.Width = new GridLength(TopicMapPanelWidth);
        TopicMapPanel.Visibility = Visibility.Visible;
        UpdateVisualizationLayout();
        AutomationProperties.SetName(TopicMapToggleButton, "Hide meeting visualization");
        RefreshVisualization(force: true);
    }

    private void CloseTopicMap()
    {
        TopicMapToggleButton.IsChecked = false;
        AutomationProperties.SetName(TopicMapToggleButton, "Show meeting visualization");
        if (TopicMapPanel.Visibility != Visibility.Visible)
            return;

        RestoreVisualizationLayout();
        TopicMapPanel.Visibility = Visibility.Collapsed;
        PrimaryContentColumn.Width = new GridLength(1, GridUnitType.Star);
        TopicMapColumn.Width = new GridLength(0);
        if (_loadedVisualization is not null)
        {
            _loadedVisualization = null;
            _displayedVisualizationDocument = null;
            _selectedVisualizationTopicId = null;
            _currentVisualizationTopicId = null;
            _renderedVisualizationFingerprint = null;
        }
        if (_topicMapAddedWidth <= 0)
            return;

        if (_presenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
        {
            _topicMapAddedWidth = 0;
            return;
        }

        var current = CurrentWindowBounds();
        var collapsed = WindowPlacement.CollapseSidePanel(
            current,
            CurrentWorkArea(),
            _topicMapAddedWidth,
            CurrentWindowDpi());
        _topicMapAddedWidth = 0;
        AppWindow.MoveAndResize(new(
            collapsed.X,
            collapsed.Y,
            collapsed.Width,
            collapsed.Height));
    }

    private void OnVisualizationExpandClick(object sender, RoutedEventArgs e)
    {
        if (_visualizationExpanded)
        {
            RestoreVisualizationLayout();
            return;
        }

        _visualizationExpanded = true;
        VisualizationExpandText.Text = "Restore";
        VisualizationExpandIcon.Glyph = "\uE73F";
        ToolTipService.SetToolTip(VisualizationExpandButton, "Restore visualization to the side pane");
        AutomationProperties.SetName(VisualizationExpandButton, "Restore visualization to the side pane");

        if (_presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Restored)
        {
            _presenter.Maximize();
            _visualizationMaximizedWindow = true;
        }

        UpdateVisualizationLayout();
    }

    private void RestoreVisualizationLayout()
    {
        if (!_visualizationExpanded)
            return;

        if (_visualizationMaximizedWindow)
        {
            _presenter.Restore();
            _visualizationMaximizedWindow = false;
        }

        _visualizationExpanded = false;
        VisualizationExpandText.Text = "Expand";
        VisualizationExpandIcon.Glyph = "\uE740";
        ToolTipService.SetToolTip(VisualizationExpandButton, "Expand visualization to the window");
        AutomationProperties.SetName(VisualizationExpandButton, "Expand visualization to the window");

        UpdateVisualizationLayout();
    }

    private void UpdateVisualizationLayout()
    {
        var viewportWidth = RootLayout.XamlRoot?.Size.Width ?? 0;
        if (TopicMapPanel.Visibility != Visibility.Visible || viewportWidth <= 0)
            return;

        var allocation = VisualizationLayout.Calculate(viewportWidth, _visualizationExpanded);
        if (_visualizationExpanded)
        {
            SetColumnWidth(
                PrimaryContentColumn,
                new GridLength(allocation.PrimaryWidth));
            SetColumnWidth(
                TopicMapColumn,
                new GridLength(1, GridUnitType.Star));
        }
        else
        {
            SetColumnWidth(
                PrimaryContentColumn,
                new GridLength(1, GridUnitType.Star));
            SetColumnWidth(
                TopicMapColumn,
                new GridLength(allocation.VisualizationWidth));
        }
        UpdateTopicDetailLayout(viewportWidth);
        if (_visualizationProjection is { } projection)
            RenderVisualizationSurface(projection);
    }

    private void UpdateTopicDetailLayout(double totalWidth)
    {
        var wide = totalWidth >= 1380;
        TopicDetailColumn.Width = wide ? new GridLength(360) : new GridLength(0);
        Grid.SetRow(TopicDetailPanel, wide ? 1 : 2);
        Grid.SetColumn(TopicDetailPanel, wide ? 1 : 0);
        Grid.SetColumnSpan(TopicDetailPanel, wide ? 1 : 2);
        TopicDetailPanel.Margin = wide
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0, 8, 0, 0);
        TopicDetailView.MaxHeight = wide ? double.PositiveInfinity : 300;
    }

    private static bool SetColumnWidth(ColumnDefinition column, GridLength width)
    {
        if (column.Width.GridUnitType == width.GridUnitType &&
            Math.Abs(column.Width.Value - width.Value) <= 1)
            return false;
        column.Width = width;
        return true;
    }

    private void OnRootLayoutLoaded(object sender, RoutedEventArgs e)
    {
        using var phase = _responsiveness.BeginUiPhase(ResponsivenessPhase.WindowLoad);
        if (!_visualizationViewportSubscribed && RootLayout.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed += OnVisualizationViewportChanged;
            _visualizationViewportSubscribed = true;
        }
        UpdateVisualizationLayout();
        if (!_initialViewReadyRaised)
        {
            _initialViewReadyRaised = true;
            InitialViewReady?.Invoke(this, EventArgs.Empty);
            if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _ = RestoreWorkIqAsync()))
                _logger.LogWarning("WorkIQ startup restore could not be queued.");
        }
    }

    private void OnVisualizationViewportChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => UpdateVisualizationLayout();

    private WindowBounds CurrentWindowBounds()
        => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    private WindowBounds CurrentWorkArea()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        return new(area.X, area.Y, area.Width, area.Height);
    }

    private int CurrentWindowDpi()
        => (int)NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));

    private void OnVisualizationChanged(object? sender, long revision)
        => InvokeOnUi(() =>
        {
            if (_loadedVisualization is null)
                RefreshVisualization();
        });

    private void OnVisualizationTopicClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string topicId } || topicId.Length == 0)
            return;
        _selectedVisualizationTopicId = topicId;
        RefreshVisualization();
    }

    private void OnGoToCurrentTopicClick(object sender, RoutedEventArgs e)
    {
        var projection = _visualizationProjection;
        if (projection is null ||
            TopicMapState.CurrentTopicSelection(projection) is not { Length: > 0 } currentTopicId)
            return;
        _selectedVisualizationTopicId = currentTopicId;
        RefreshVisualization();
        RevealMapTopic(currentTopicId);
        ScrollNotesToTopic(currentTopicId);
    }

    private async void OnOpenVisualizationClick(object sender, RoutedEventArgs e)
    {
        if (!_pipeline.CanStart)
        {
            ShowStatusBar(
                "Finish or discard the active recording before opening a saved meeting visualization.",
                InfoBarSeverity.Informational);
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".md");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;
        if (!_pipeline.CanStart)
        {
            ShowStatusBar(
                "A recording started while the file picker was open. Finish or discard it before opening saved visualization data.",
                InfoBarSeverity.Informational);
            return;
        }

        MeetingVisualizationLoadResult result;
        try
        {
            result = await _visualizationStorage.LoadAsync(file.Path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saved meeting visualization could not be opened.");
            ShowStatusBar("The saved meeting visualization could not be opened.", InfoBarSeverity.Error);
            return;
        }
        if (!_pipeline.CanStart)
        {
            ShowStatusBar(
                "A recording started while the saved meeting was loading, so the result was not displayed.",
                InfoBarSeverity.Informational);
            return;
        }

        if (result.Kind != MeetingVisualizationLoadKind.Valid || result.Document is null)
        {
            if (_loadedVisualization is not null)
                CloseTopicMap();
            if (result.Kind == MeetingVisualizationLoadKind.LegacyMissing &&
                !OpenMarkdownFile(file.Path))
                return;
            var message = result.Kind switch
            {
                MeetingVisualizationLoadKind.LegacyMissing =>
                    "Opened the Markdown notes. This older recording has no companion visualization data.",
                MeetingVisualizationLoadKind.Corrupt =>
                    "The companion visualization data is corrupt and was not opened.",
                MeetingVisualizationLoadKind.FutureSchema =>
                    "This visualization was created by a newer app version and was not opened.",
                MeetingVisualizationLoadKind.StaleMismatch =>
                    "The visualization does not match these Markdown notes and was not opened.",
                _ => "The saved meeting visualization could not be opened.",
            };
            ShowStatusBar(message, InfoBarSeverity.Warning);
            return;
        }

        _loadedVisualization = result.Document;
        _selectedVisualizationTopicId = null;
        _currentVisualizationTopicId = null;
        _renderedVisualizationFingerprint = null;
        TopicMapToggleButton.IsChecked = true;
        OpenTopicMap();
        RefreshVisualization(force: true);
    }

    private void ConfigureVisualizationTransitions()
    {
        if (!_animationsEnabled)
            return;

        TrailList.ItemContainerTransitions = [new AddDeleteThemeTransition()];
    }

    private void AnimateChangedItems<T>(ItemsControl list, IReadOnlyList<T> changed)
    {
        if (!_animationsEnabled || changed.Count == 0)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            foreach (var item in changed)
            {
                if (list.ContainerFromItem(item) is UIElement container)
                    AnimateOpacity(container, 0.68, 1, 160);
            }
        });
    }

    private static void AnimateOpacity(UIElement target, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new ExponentialEase
            {
                EasingMode = EasingMode.EaseOut,
                Exponent = 5,
            },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static string TrailFingerprint(TrailItemViewModel item)
        => string.Join(
            '\u001f',
            item.TopicId, item.Sequence, item.Label, item.Detail, item.RelationText,
            item.IsRevisit, item.IsCurrent, item.IsSelected, item.HasNext);

    /// <summary>
    /// Maps a remembered topic id through canonical redirects so a merged provisional topic keeps
    /// its selection instead of silently falling back to the current topic.
    /// </summary>
    private string? ResolveLiveCanonicalTopicId(string? topicId)
    {
        if (string.IsNullOrEmpty(topicId) || _loadedVisualization is not null)
            return topicId;
        try
        {
            return _visualization.ResolveCanonicalTopicId(topicId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return topicId;
        }
    }
    private void RefreshVisualization(bool force = false)
    {
        MeetingVisualizationDocument? document = _loadedVisualization;
        if (document is null)
        {
            try
            {
                document = _visualization.Snapshot();
            }
            catch (InvalidOperationException)
            {
                document = null;
            }
        }

        if (document is null)
        {
            ClearVisualizationPresentation();
            return;
        }

        if (!StringComparer.Ordinal.Equals(_meetingNotesSessionId, document.SessionId) ||
            document.TopicClusters.Count == 0)
        {
            _meetingNotesSessionId = document.SessionId;
            _meetingNotes.Clear();
        }

        _displayedVisualizationDocument = document;
        var persistSelection = TopicMapPanel.Visibility == Visibility.Visible ||
            _selectedVisualizationTopicId is not null;
        var selectedTopicId = ResolveLiveCanonicalTopicId(_selectedVisualizationTopicId);
        var currentTopicId = ResolveLiveCanonicalTopicId(_currentVisualizationTopicId) ??
            document.TopicTrail.LastOrDefault()?.TopicId;
        var projection = TopicMapState.Project(
            document, selectedTopicId, currentTopicId, _relatedContextStatesByTopicId,
            _workIq.IsAuthenticated, _loadedVisualization is not null,
            buildMeetingNotes: false);
        var fingerprint =
            $"{projection.Fingerprint}|{projection.SelectedTopicId}|{projection.CurrentTopicId}";
        if (!force && fingerprint == _renderedVisualizationFingerprint)
            return;
        _visualizationProjection = projection;
        if (persistSelection)
            _selectedVisualizationTopicId = projection.SelectedTopicId.Length == 0
                ? null
                : projection.SelectedTopicId;
        _currentVisualizationTopicId = projection.CurrentTopicId.Length == 0
            ? null
            : projection.CurrentTopicId;
        _renderedVisualizationFingerprint = fingerprint;

        VisualizationTitleText.Text = _loadedVisualization is null
            ? "Topic map"
            : $"{document.MeetingTitle} · read-only";
        var completed = _loadedVisualization is not null ||
            _pipeline.State is PipelineState.Saved or PipelineState.Failed;
        VisualizationSubtitleText.Text = _loadedVisualization is not null
            ? "Saved meeting · read-only"
            : completed
                ? "Completed meeting · retained after recording"
                : "Live meeting · topics appear as they are accepted";
        var freshness = _loadedVisualization is not null
            ? "Saved snapshot"
            : _pipeline.State == PipelineState.Saved
                ? "Saved meeting"
                : _pipeline.State == PipelineState.Failed
                    ? "Retained meeting"
            : Volatile.Read(ref _visualizationAnalysisPending) > 0
                ? "Analyzing current topic"
                : "Live";
        TopicMapStatusText.Text = projection.EmptyMessage.Length > 0
            ? $"{freshness} · {projection.EmptyMessage}"
            : $"{freshness} · the latest topic is marked Now.";

        var canGoToCurrentTopic = projection.CurrentTopicId.Length > 0 &&
            !StringComparer.Ordinal.Equals(projection.CurrentTopicId, projection.SelectedTopicId);
        GoToCurrentTopicButton.IsEnabled = canGoToCurrentTopic;
        MeetingNotesEmptyText.Text = _meetingNotes.Count > 0
            ? string.Empty
            : projection.MeetingBrainMap?.Topics.Count > 0
                ? "Preparing transcript-grounded meeting notes..."
                : projection.MeetingNotes?.EmptyMessage ??
                    projection.MeetingBrainMap?.EmptyMessage ??
                    "Meeting notes are unavailable.";
        var notesProjectionRevision = Interlocked.Increment(ref _meetingNotesProjectionRevision);
        UpdateVisualizationViewVisibility();
        RenderVisualizationSurface(projection);
        if (TopicMapPanel.Visibility == Visibility.Visible)
            _ = PopulateMeetingNotesAsync(document, projection, notesProjectionRevision);
        else
            Interlocked.Increment(ref _meetingNotesProjectionRevision);
    }

    private async Task PopulateMeetingNotesAsync(
        MeetingVisualizationDocument document,
        VisualizationProjection projection,
        int projectionRevision)
    {
        IReadOnlyList<MeetingTopicNotes> topicNotes;
        try
        {
            topicNotes = await Task.Run(() => MeetingTopicNotesBuilder.Build(document))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Meeting notes projection failed ({ErrorType}).", ex.GetType().Name);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (projectionRevision == Volatile.Read(ref _meetingNotesProjectionRevision) &&
                    StringComparer.Ordinal.Equals(
                        _visualizationProjection?.Fingerprint,
                        projection.Fingerprint) &&
                    _meetingNotes.Count == 0)
                    MeetingNotesEmptyText.Text = "Meeting notes could not be prepared.";
            });
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (projectionRevision != Volatile.Read(ref _meetingNotesProjectionRevision) ||
                !StringComparer.Ordinal.Equals(
                    _visualizationProjection?.Fingerprint,
                    projection.Fingerprint))
                return;
            var notes = TopicMapState.BuildMeetingNotesViewModel(
                topicNotes,
                document,
                projection.SelectedTopicId,
                projection.CurrentTopicId);
            notes = notes with { Topics = TopicCollapseState.Apply(notes.Topics, _collapsedTopicIds) };
            _visualizationProjection = projection with { MeetingNotes = notes };
            var notesUpdate = VisualizationCollectionState.Reconcile(
                _meetingNotes,
                notes.Topics,
                item => item.Id,
                item => $"{item.Heading}|{string.Join("|", item.Bullets)}|" +
                    $"{item.IsCurrent}|{item.IsSelected}|{item.IsCollapsed}|" +
                    string.Join("|", item.Suggestions.Select(source =>
                        $"{source.Id}:{source.Title}:{source.SourceType}:{source.SourceUrl}")));
            MeetingNotesEmptyText.Text = notes.EmptyMessage;
            AnimateChangedItems(MeetingNotesList, notesUpdate.Changed);
            UpdateVisualizationViewVisibility();
            UpdateMeetingNotesHeadingStyles(notes.Topics);
            ScrollNotesToTopic(projection.SelectedTopicId);
        });
    }

    private void ClearVisualizationPresentation()
    {
        Interlocked.Increment(ref _meetingNotesProjectionRevision);
        _visualizationProjection = null;
        _displayedVisualizationDocument = null;
        _selectedVisualizationTopicId = null;
        _currentVisualizationTopicId = null;
        _renderedVisualizationFingerprint = null;
        _meetingNotesSessionId = null;
        VisualizationTitleText.Text = "Topic map";
        VisualizationSubtitleText.Text = "Live meeting · topics appear as they are accepted";
        TopicMapStatusText.Text = "No visualization data is available.";
        MeetingNotesEmptyText.Text = "No visualization data is available.";
        GoToCurrentTopicButton.IsEnabled = false;
        GoToCurrentTopicButton.Visibility = Visibility.Collapsed;
        _visualizationTrail.Clear();
        _meetingNotes.Clear();
        _collapsedTopicIds.Clear();
        BrainMapCanvas.Children.Clear();
        _brainMapNodes.Clear();
        _brainMapConnectors.Clear();
        _brainMapRootButton = null;
        UpdateVisualizationViewVisibility();
        RestoreVisualizationLayout();
    }

    private void UpdateVisualizationViewVisibility()
    {
        TopicTimelineGuideText.Visibility = Visibility.Collapsed;
        TopicMapCombinedView.Visibility = Visibility.Visible;
        MeetingNotesView.Visibility = Visibility.Visible;
        TopicDetailPanel.Visibility = Visibility.Collapsed;
        MeetingNotesEmptyText.Visibility = _meetingNotes.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        GoToCurrentTopicButton.Visibility = GoToCurrentTopicButton.IsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        BrainMapView.Visibility = Visibility.Visible;
        TopicTimelineView.Visibility = Visibility.Collapsed;
        TimelineEmptyText.Visibility = Visibility.Collapsed;
    }

    private void RenderVisualizationSurface(VisualizationProjection projection)
    {
        if (TopicMapPanel.Visibility != Visibility.Visible)
            return;
        RenderBrainMap(projection);
        RenderMeetingNotesWorkIqStatus();
        UpdateMeetingNotesHeadingStyles(_meetingNotes);
    }

    private void OnBrainMapViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 ||
            Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 1 ||
            TopicMapPanel.Visibility != Visibility.Visible ||
            _visualizationProjection is not { } projection)
            return;
        RenderBrainMap(projection);
    }

    private void RenderBrainMap(VisualizationProjection projection)
    {
        _brainMapRootButton = null;
        if (projection.MeetingBrainMap is not { } map)
        {
            BrainMapCanvas.Children.Clear();
            _brainMapNodes.Clear();
            _brainMapConnectors.Clear();
            BrainMapNowText.Text = "Now · waiting for a current topic";
            BrainMapEmptyText.Text = "No accepted topics yet. The topic map will grow as topics are accepted.";
            BrainMapEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var layout = MeetingBrainMapLayout.Calculate(
            Math.Max(0, BrainMapView.ActualWidth - 20),
            TopicCollapseState.LeafCounts(map, _collapsedTopicIds));
        BrainMapCanvas.Width = layout.Width;
        BrainMapCanvas.Height = layout.Height;
        var currentTopic = map.Topics.FirstOrDefault(item => item.Id == map.CurrentTopicId);
        BrainMapNowText.Text = currentTopic is null
            ? "Now · waiting for a current topic"
            : $"Now · {currentTopic.Title}";
        BrainMapEmptyText.Text = map.EmptyMessage;
        BrainMapEmptyText.Visibility = map.EmptyMessage.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        var activeConnectorIds = layout.Connectors
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _brainMapConnectors.Keys
                     .Where(id => !activeConnectorIds.Contains(id)).ToArray())
        {
            BrainMapCanvas.Children.Remove(_brainMapConnectors[staleId]);
            _brainMapConnectors.Remove(staleId);
        }

        foreach (var connector in layout.Connectors)
            UpdateBrainMapConnector(
                connector.Id,
                connector.Start.X,
                connector.Start.Y,
                connector.End.X,
                connector.End.Y);

        var activeNodeIds = map.Topics
            .Select(item => $"topic:{item.Id}")
            .Concat(map.Topics.Select(item => $"toggle:{item.Id}"))
            .Concat(map.Topics
                .Where(topic => !_collapsedTopicIds.Contains(topic.Id))
                .SelectMany(topic => topic.Leaves.Select(leaf => $"leaf:{topic.Id}:{leaf.Id}")))
            .Append("root:meeting")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _brainMapNodes.Keys
                     .Where(id => !activeNodeIds.Contains(id)).ToArray())
        {
            BrainMapCanvas.Children.Remove(_brainMapNodes[staleId]);
            _brainMapNodes.Remove(staleId);
        }

        _brainMapRootButton = UpsertBrainMapNode(
            "root:meeting",
            "meeting",
            string.Empty,
            map.MeetingTitle,
            map.Detail,
            "Meeting",
            layout.Root,
            accent: false,
            selected: false,
            accessibleName: map.CurrentTopicId.Length == 0
                ? $"{map.MeetingTitle}. Whole-meeting topic map."
                : $"{map.MeetingTitle}. Whole-meeting topic map. Select to view meeting notes.");

        for (var index = 0; index < map.Topics.Count; index++)
        {
            var topic = map.Topics[index];
            UpsertBrainMapNode(
                $"topic:{topic.Id}",
                topic.Id,
                topic.Id,
                topic.Title,
                topic.Detail,
                "Topic",
                layout.Topics[index].Topic,
                accent: topic.Id == map.CurrentTopicId,
                selected: topic.Id == projection.SelectedTopicId,
                accessibleName: $"Topic: {topic.Title}. {topic.Detail}. Select to view matching meeting notes.");
            UpsertBrainMapToggle(topic, layout.Topics[index].Topic);
            for (var leafIndex = 0;
                 leafIndex < layout.Topics[index].Leaves.Count && leafIndex < topic.Leaves.Count;
                 leafIndex++)
            {
                var leaf = topic.Leaves[leafIndex];
                UpsertBrainMapNode(
                    $"leaf:{topic.Id}:{leaf.Id}",
                    leaf.Id,
                    leaf.TopicId,
                    leaf.Title,
                    leaf.Detail,
                    leaf.Kind,
                    layout.Topics[index].Leaves[leafIndex],
                    accent: false,
                    selected: false,
                    accessibleName: leaf.AccessibleName);
            }
        }
    }

    private void UpsertBrainMapToggle(MeetingBrainMapTopicViewModel topic, BrainMapRect bounds)
    {
        var collapsed = _collapsedTopicIds.Contains(topic.Id);
        var key = $"toggle:{topic.Id}";
        if (!_brainMapNodes.TryGetValue(key, out var button))
        {
            button = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Content = new FontIcon
                {
                    FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                    FontSize = 12,
                },
            };
            button.Click += OnBrainMapToggleClick;
            Canvas.SetZIndex(button, 2);
            _brainMapNodes.Add(key, button);
            BrainMapCanvas.Children.Add(button);
        }

        var name = collapsed ? $"Expand topic: {topic.Title}" : $"Collapse topic: {topic.Title}";
        ((FontIcon)button.Content).Glyph = collapsed ? "\uE76C" : "\uE70D";
        button.Tag = topic.Id;
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, $"BrainMapToggle_{topic.Id}");
        ToolTipService.SetToolTip(button, name);
        Canvas.SetLeft(button, bounds.X + bounds.Width - 34);
        Canvas.SetTop(button, bounds.Y + 6);
    }

    private void OnBrainMapToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string topicId } && topicId.Length > 0)
            ToggleTopicCollapsed(topicId);
    }

    private void OnNotesTopicToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string topicId } && topicId.Length > 0)
            ToggleTopicCollapsed(topicId);
    }

    private void ToggleTopicCollapsed(string topicId)
    {
        TopicCollapseState.Toggle(_collapsedTopicIds, topicId);
        if (_visualizationProjection is not { } projection)
            return;
        if (projection.MeetingNotes is { } notes)
        {
            var updated = notes with { Topics = TopicCollapseState.Apply(notes.Topics, _collapsedTopicIds) };
            _visualizationProjection = projection = projection with { MeetingNotes = updated };
            VisualizationCollectionState.Reconcile(
                _meetingNotes,
                updated.Topics,
                item => item.Id,
                item => $"{item.Heading}|{string.Join("|", item.Bullets)}|" +
                    $"{item.IsCurrent}|{item.IsSelected}|{item.IsCollapsed}|" +
                    string.Join("|", item.Suggestions.Select(source =>
                        $"{source.Id}:{source.Title}:{source.SourceType}:{source.SourceUrl}")));
            UpdateMeetingNotesHeadingStyles(updated.Topics);
        }
        RenderBrainMap(projection);
    }

    private void OnTopicMapBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var sideBySide = e.NewSize.Width >= 640;
        TopicMapMainColumn.Width = new GridLength(sideBySide ? 3 : 1, GridUnitType.Star);
        TopicNotesColumn.MinWidth = sideBySide ? 260 : 0;
        TopicNotesColumn.Width = sideBySide ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        TopicMapMainRow.Height = new GridLength(sideBySide ? 1 : 3, GridUnitType.Star);
        TopicNotesRow.Height = sideBySide ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetRow(MeetingNotesView, sideBySide ? 0 : 1);
        Grid.SetColumn(MeetingNotesView, sideBySide ? 1 : 0);
        MeetingNotesView.Margin = sideBySide ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
    }

    private void UpdateBrainMapConnector(
        string connectorId,
        double startX,
        double startY,
        double endX,
        double endY)
    {
        if (!_brainMapConnectors.TryGetValue(connectorId, out var connector))
        {
            connector = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(150, 150, 150, 150)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            Canvas.SetZIndex(connector, 0);
            _brainMapConnectors.Add(connectorId, connector);
            BrainMapCanvas.Children.Add(connector);
        }

        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new(startX, startY) };
        figure.Segments.Add(new LineSegment { Point = new(endX, endY) });
        geometry.Figures.Add(figure);
        connector.Data = geometry;
    }

    private Button UpsertBrainMapNode(
        string key,
        string nodeId,
        string topicId,
        string title,
        string detail,
        string kind,
        BrainMapRect bounds,
        bool accent,
        bool selected,
        string? accessibleName = null)
    {
        if (!_brainMapNodes.TryGetValue(key, out var button))
        {
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160)),
            });
            content.Children.Add(new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
            });
            content.Children.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
            });
            button = new()
            {
                Padding = new Thickness(12, 10, 12, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = content,
            };
            button.Click += OnBrainMapNodeClick;
            Canvas.SetZIndex(button, 1);
            _brainMapNodes.Add(key, button);
            BrainMapCanvas.Children.Add(button);
        }

        var labels = (StackPanel)button.Content;
        ((TextBlock)labels.Children[0]).Text = kind;
        ((TextBlock)labels.Children[0]).Foreground = new SolidColorBrush(Color.FromArgb(255, 182, 192, 209));
        ((TextBlock)labels.Children[1]).Text = title;
        var textColor = TopicMapState.NodeTextColor;
        ((TextBlock)labels.Children[1]).Foreground = new SolidColorBrush(
            Color.FromArgb(255, textColor.Red, textColor.Green, textColor.Blue));
        ((TextBlock)labels.Children[2]).Text = detail;
        ((TextBlock)labels.Children[2]).Foreground = new SolidColorBrush(Color.FromArgb(255, 200, 207, 220));
        button.Tag = topicId;
        button.Width = bounds.Width;
        button.Height = bounds.Height;
        var background = kind == "Topic"
            ? TopicMapState.TopicNodeBackground
            : TopicMapState.LeafNodeBackground;
        button.Background = new SolidColorBrush(
            Color.FromArgb(255, background.Red, background.Green, background.Blue));
        button.BorderBrush = new SolidColorBrush(accent
            ? Color.FromArgb(255, 121, 190, 255)
            : selected
                ? Color.FromArgb(255, 94, 164, 235)
                : Color.FromArgb(255, 89, 97, 113));
        button.BorderThickness = new Thickness(accent || selected ? 2 : 1);
        button.Shadow = accent ? new ThemeShadow() : null;
        button.Translation = new System.Numerics.Vector3(0, 0, accent ? 8 : 0);
        AutomationProperties.SetName(
            button,
            accessibleName ?? $"{kind}: {title}. {detail}. Select to open topic details.");
        AutomationProperties.SetAutomationId(button, $"BrainMapNode_{nodeId}");
        ToolTipService.SetToolTip(button, title);
        Canvas.SetLeft(button, bounds.X);
        Canvas.SetTop(button, bounds.Y);
        return button;
    }

    private void OnBrainMapNodeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;
        if (AutomationProperties.GetAutomationId(button) == "BrainMapNode_meeting")
        {
            MeetingNotesView.ChangeView(null, 0, null);
            return;
        }
        if (button.Tag is not string topicId || topicId.Length == 0)
            return;
        _selectedVisualizationTopicId = topicId;
        RefreshVisualization();
        ScrollNotesToTopic(topicId);
    }

    private void RevealMapTopic(string topicId)
    {
        if (_brainMapNodes.TryGetValue($"topic:{topicId}", out var button))
            button.StartBringIntoView();
    }

    private void ScrollNotesToTopic(string topicId)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var topic = _meetingNotes.FirstOrDefault(item => item.Id == topicId);
            if (topic is not null &&
                MeetingNotesList.ContainerFromItem(topic) is FrameworkElement container)
                container.StartBringIntoView();
        });
    }

    private void UpdateMeetingNotesHeadingStyles(
        IEnumerable<MeetingTopicNotesViewModel> topics)
    {
        foreach (var topic in topics)
        {
            if (MeetingNotesList.ContainerFromItem(topic) is not DependencyObject container ||
                FindTaggedBorder(container, topic.Id) is not { } heading)
                continue;
            heading.BorderBrush = new SolidColorBrush(topic.IsSelected
                ? Color.FromArgb(255, 94, 164, 235)
                : topic.IsCurrent
                    ? Color.FromArgb(255, 121, 190, 255)
                    : Color.FromArgb(255, 89, 97, 113));
            heading.BorderThickness = new Thickness(topic.IsSelected ? 2 : 1);
            heading.Background = new SolidColorBrush(topic.IsCurrent
                ? Color.FromArgb(255, 39, 52, 69)
                : Color.FromArgb(255, 38, 44, 58));
            heading.Shadow = topic.IsCurrent ? new ThemeShadow() : null;
            heading.Translation = new System.Numerics.Vector3(0, 0, topic.IsCurrent ? 8 : 0);
        }
    }

    private static Border? FindTaggedBorder(DependencyObject root, string tag)
    {
        if (root is Border border && border.Tag is string value && value == tag)
            return border;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindTaggedBorder(VisualTreeHelper.GetChild(root, index), tag) is { } match)
                return match;
        return null;
    }

    private void RenderMeetingNotesWorkIqStatus()
    {
        RelatedContextStatus? status = _relatedContext.Status;
        if (_displayedVisualizationDocument is not { } document ||
            status?.SessionId != document.SessionId)
            status = null;
        var message = _displayedVisualizationDocument is { TopicClusters.Count: > 0 }
            ? RelatedContextPresentation.MeetingFailureMessage(status, _workIq.IsAuthenticated)
            : string.Empty;
        MeetingNotesWorkIqStatusText.Text = message;
        MeetingNotesWorkIqStatusText.Visibility = message.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string? FocusedAutomationIdWithin(FrameworkElement container)
    {
        if (container.XamlRoot is null ||
            FocusManager.GetFocusedElement(container.XamlRoot) is not DependencyObject focused)
            return null;
        for (DependencyObject? current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, container))
                return focused is FrameworkElement element
                    ? AutomationProperties.GetAutomationId(element)
                    : null;
        return null;
    }

    private static void RestoreFocusByAutomationId(Panel container, string? automationId)
    {
        if (string.IsNullOrEmpty(automationId))
            return;
        foreach (var child in container.Children.OfType<Control>())
            if (AutomationProperties.GetAutomationId(child) == automationId)
            {
                child.Focus(FocusState.Keyboard);
                return;
            }
    }

    private string ProjectVisualizationText(string? text)
    {
        try
        {
            var speakers = _visualization.Snapshot().Speakers.ToDictionary(
                speaker => speaker.SpeakerId,
                speaker => speaker.DisplayName,
                StringComparer.OrdinalIgnoreCase);
            return TopicMapState.ProjectSpeakers(text, speakers);
        }
        catch (InvalidOperationException)
        {
            return text ?? string.Empty;
        }
    }

    private void OnRelatedContextStatusChanged(object? sender, RelatedContextStatus status)
        => InvokeOnUi(() =>
        {
            if (!IsCurrentRelatedContextStatus(status))
                return;
            RenderRelatedContext(status);
        });

    private void OnContextFlyoutOpening(object sender, object e)
    {
        RenderAiUsage(refreshDetails: true, announceTransition: false);
        PopulateScheduledInvitees();
        var status = _relatedContext.Status;
        RenderRelatedContext(IsCurrentRelatedContextStatus(status) ? status : null);
        if (!_rosterChecked && _rosterRefreshTask is null)
            _ = RefreshRosterAsync();
    }

    private bool IsCurrentAiUsageSession()
    {
        var snapshot = _pipeline.AiUsage.GetSnapshot();
        return _sessionId is not null &&
               string.Equals(snapshot.SessionId, _sessionId, StringComparison.Ordinal);
    }

    private void RenderAiUsage(bool refreshDetails, bool announceTransition)
    {
        var snapshot = _pipeline.AiUsage.GetSnapshot();
        if (_sessionId is null ||
            !string.Equals(snapshot.SessionId, _sessionId, StringComparison.Ordinal))
        {
            AiActivityIndicator.Visibility = Visibility.Collapsed;
            if (refreshDetails)
                AiUsageStatusText.Text = "AI activity is unavailable for this recording.";
            return;
        }

        var presentation = AiUsagePresentation.Create(snapshot);
        AiActivityHeaderText.Text = presentation.Header;
        AutomationProperties.SetName(AiActivityHeaderText, presentation.Status);
        AiActivityIndicator.Visibility =
            RecordingPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

        if (refreshDetails)
            AiUsageStatusText.Text = presentation.Status;

        if (!announceTransition ||
            string.Equals(_aiUsageTransitionKey, presentation.TransitionKey, StringComparison.Ordinal))
            return;

        _aiUsageTransitionKey = presentation.TransitionKey;
        AiActivityAnnouncementText.Text = presentation.Announcement;
    }

    private async void OnRelatedContextRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_sessionId is null || _contextCts is null || _loadedVisualization is not null)
            return;

        var topicId = ResolveLiveCanonicalTopicId(_selectedVisualizationTopicId) ??
            ResolveLiveCanonicalTopicId(_currentVisualizationTopicId);
        var topic = topicId is null
            ? null
            : _visualization.Snapshot().TopicClusters
                .FirstOrDefault(candidate => candidate.Id == topicId)?.Label;
        var normalizedTopic = NormalizeRelatedContextTopic(topic);
        if (topicId is null || string.IsNullOrWhiteSpace(topic) ||
            !IsRelatedContextTopicEligible(normalizedTopic))
        {
            RelatedContextStatusText.Text = "Waiting for a discussion topic.";
            UpdateRelatedContextRefreshButton();
            return;
        }

        RelatedContextRefreshButton.IsEnabled = false;
        try
        {
            await _relatedContext.RefreshAsync(
                new ContextQuery(
                    _sessionId,
                    topic,
                    _transcriptRevision,
                    ForceRefresh: true,
                    TopicId: topicId),
                _contextCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Session, authentication, discard, or shutdown invalidated the request.
        }
        finally
        {
            UpdateRelatedContextRefreshButton();
        }
    }

    private async void OnRosterRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshRosterAsync();

    /// <summary>Full per-request AI telemetry lives in Settings, not in the meeting aid.</summary>
    private void OnOpenAiDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        ContextFlyout.Hide();
        ShowSettings();
        AiDiagnosticsExpander.IsExpanded = true;
        AiDiagnosticsExpander.StartBringIntoView();
    }

    private void RenderRelatedContext(RelatedContextStatus? status)
    {
        _relatedContextItems.Clear();
        var snapshot = status?.Snapshot;
        UpdateRelatedContextTopicPresentation(status);
        string? mappedTopicId = null;
        if (status is not null &&
            IsActiveVisualizationSession(status.SessionId) &&
            ResolveLiveCanonicalTopicId(status.TopicId) is { } topicId)
        {
            mappedTopicId = topicId;
            _relatedContextStatesByTopicId[mappedTopicId] = status.State;
        }
        if (mappedTopicId is not null && snapshot is not null)
        {
            var appliedKey = $"{mappedTopicId}\n{snapshot.RetrievedAt.UtcTicks}";
            if (_appliedRelatedContextSnapshots.Add(appliedKey))
            {
                try
                {
                    _visualization.ApplyRelatedContext(mappedTopicId, snapshot);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    _logger.LogWarning(
                        "Related context could not be attached to visualization topic {TopicId} ({Type}).",
                        mappedTopicId,
                        ex.GetType().Name);
                }
            }
        }
        RefreshVisualization();
        if (snapshot is not null)
            foreach (var item in snapshot.Items)
                foreach (var source in ContextEvidenceSourceViewModel.Create(item))
                    _relatedContextItems.Add(source);

        RelatedContextList.Visibility = _relatedContextItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var normalizedTopic = NormalizeRelatedContextTopic(_acceptedRelatedContextTopic);
        var presentation = RelatedContextPresentation.Status(
            status,
            _acceptedRelatedContextTopic,
            IsRelatedContextTopicEligible(normalizedTopic),
            _workIq.IsAuthenticated,
            _relatedContextItems.Count);
        RelatedContextProgress.IsActive = presentation.IsQuerying;
        RelatedContextProgress.Visibility = RelatedContextProgress.IsActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        RelatedContextStatusText.Text = presentation.Text;
        UpdateRelatedContextRefreshButton();
    }

    private bool IsActiveVisualizationSession(string? sessionId)
    {
        if (!_visualization.HasActiveSession || string.IsNullOrEmpty(sessionId))
            return false;
        try
        {
            return _visualization.Snapshot().SessionId == sessionId;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdateRelatedContextTopicPresentation(RelatedContextStatus? status)
    {
        var topic = RelatedContextPresentation.Topic(
            status?.Topic,
            _acceptedRelatedContextTopic,
            ProjectVisualizationText(status?.Topic));
        RelatedContextTopicText.Text = topic.Label;
        RelatedContextTopicDisclosureText.Text = topic.Disclosure;
        RelatedContextTopicExpander.Visibility = topic.Label.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool IsCurrentRelatedContextStatus(RelatedContextStatus status)
    {
        if (status.SourceRevision != _workIq.Revision)
            return false;
        return status.SessionId.Length == 0 || status.SessionId == _sessionId;
    }

    private static string NormalizeRelatedContextTopic(string? topic) =>
        string.Join(" ", (topic ?? string.Empty).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private Task RefreshRosterAsync()
    {
        if (_sessionId is null || _contextCts is null)
        {
            RenderRoster(null);
            return Task.CompletedTask;
        }
        if (_rosterRefreshTask is not null)
            return _rosterRefreshTask;
        var refresh = LoadRosterAsync();
        _rosterRefreshTask = refresh;
        UpdateRosterRefreshButton();
        _ = ReleaseRosterRefreshAsync(refresh);
        return refresh;
    }

    private async Task ReleaseRosterRefreshAsync(Task refresh)
    {
        try
        {
            await refresh;
        }
        finally
        {
            if (ReferenceEquals(_rosterRefreshTask, refresh))
                _rosterRefreshTask = null;
            UpdateRosterRefreshButton();
        }
    }

    private async Task LoadRosterAsync()
    {
        var sessionId = _sessionId;
        var context = _contextCts;
        var provider = _activeRoster;
        if (sessionId is null || context is null || provider is null)
            return;
        RosterStatusText.Text = "Checking the joined call for a verified roster...";
        RosterFreshnessText.Visibility = Visibility.Collapsed;
        RosterLimitationText.Visibility = Visibility.Collapsed;
        UpdateRosterRefreshButton();
        try
        {
            // Each probe spawns a fresh Teams accessibility inspection; nothing is served from cache.
            var snapshot = await provider.GetSnapshotAsync(context.Token);
            if (_sessionId == sessionId && !context.IsCancellationRequested)
            {
                _rosterChecked = true;
                _rosterCheckedAt = DateTimeOffset.Now;
                RenderRoster(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // Stale session/auth/discard results are intentionally ignored.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Active roster refresh failed ({Type}).", ex.GetType().Name);
            if (_sessionId == sessionId)
            {
                _rosterChecked = true;
                _rosterCheckedAt = DateTimeOffset.Now;
                _liveParticipants.Clear();
                LiveParticipantsPreview.Visibility = Visibility.Collapsed;
                RosterStatusText.Text = "Live participants could not be checked.";
                RosterFreshnessText.Text = $"Last checked {_rosterCheckedAt:h:mm:ss tt}.";
                RosterFreshnessText.Visibility = Visibility.Visible;
                RosterLimitationText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RenderRoster(ActiveRosterSnapshot? snapshot)
    {
        var members = snapshot is not null &&
            snapshot.Availability == ActiveRosterAvailability.VerifiedLive
                ? snapshot.Members.Select(member => member.DisplayName).ToArray()
                : [];
        var preview = ParticipantPresentation.Preview(members, isVerified: true);
        _liveParticipants.Clear();
        foreach (var chip in preview.Chips)
            _liveParticipants.Add(chip);
        LiveParticipantsOverflowText.Text = preview.Overflow;
        LiveParticipantsPreview.Visibility = _liveParticipants.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var presentation = ParticipantPresentation.Roster(snapshot, members.Length, _rosterCheckedAt);
        RosterStatusText.Text = presentation.Status;
        RosterFreshnessText.Text = presentation.Freshness;
        RosterFreshnessText.Visibility = presentation.Freshness.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RosterLimitationText.Text = presentation.Guidance;
        RosterLimitationText.Visibility = presentation.Guidance.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateRosterRefreshButton();
    }

    /// <summary>
    /// Calendar invitees stay a compact, clearly labelled preview: they are never promoted
    /// into the verified live roster and are never listed in full by default.
    /// </summary>
    private void PopulateScheduledInvitees()
    {
        var names = new List<string>();
        foreach (var attendee in _pipeline.CurrentSession?.InviteParticipants ?? [])
        {
            var name = string.IsNullOrWhiteSpace(attendee.DisplayName)
                ? "Unnamed invitee"
                : attendee.DisplayName.Trim();
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        var preview = ParticipantPresentation.Preview(names, isVerified: false);
        _scheduledInvitees.Clear();
        foreach (var chip in preview.Chips)
            _scheduledInvitees.Add(chip);
        ScheduledInviteesOverflowText.Text = preview.Overflow;
        ScheduledInviteesPreview.Visibility = _scheduledInvitees.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScheduledInviteesSummaryText.Text = ParticipantPresentation.InviteeSummary(names);
        ScheduledInviteesAllText.Text = ParticipantPresentation.InviteeList(names);
        ScheduledInviteesExpander.Visibility = names.Count > preview.Chips.Count
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (names.Count <= preview.Chips.Count)
            ScheduledInviteesExpander.IsExpanded = false;
    }

    private void UpdateRelatedContextRefreshButton()
    {
        if (RelatedContextRefreshButton is null)
            return;
        var topic = NormalizeRelatedContextTopic(_acceptedRelatedContextTopic);
        RelatedContextRefreshButton.IsEnabled =
            !_preparingExit &&
            _sessionId is not null &&
            _contextCts is not null &&
            _workIq.IsAuthenticated &&
            IsRelatedContextTopicEligible(topic) &&
            _relatedContext.Status.State != RelatedContextState.Refreshing;
    }

    private void UpdateRosterRefreshButton()
    {
        if (RosterRefreshButton is null)
            return;
        var isRefreshing = _rosterRefreshTask is not null;
        RosterRefreshButton.IsEnabled = !_preparingExit && _sessionId is not null && !isRefreshing;
        RosterProgress.IsActive = isRefreshing;
        RosterProgress.Visibility = isRefreshing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenContextSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            ShowStatusBar("This context source link is not a valid HTTPS address.",
                InfoBarSeverity.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Open related context source failed ({Type}).", ex.GetType().Name);
            ShowStatusBar("The related context source could not be opened.",
                InfoBarSeverity.Error);
        }
    }

    private void RenderTopicBrief(VisualizationProjection projection)
    {
        var brief = projection.TopicBrief;
        TopicBriefEmptyText.Visibility = brief is null ? Visibility.Visible : Visibility.Collapsed;
        TopicBriefContent.Visibility = brief is null ? Visibility.Collapsed : Visibility.Visible;
        if (brief is null)
            return;

        TopicBriefTitleText.Text = brief.Label;
        TopicBriefDetailText.Text = brief.Status;
        TopicBriefDiscussedAtText.Text = brief.DiscussedAt;
        var contextStatus = brief.ContextStatus;
        TopicBriefContextStatusText.Text = contextStatus.Text;
        AutomationProperties.SetName(
            TopicBriefContextStatus,
            contextStatus.AccessibleName);
        TopicBriefContextProgress.IsActive = contextStatus.IsLoading;
        TopicBriefContextProgress.Visibility = TopicBriefContextProgress.IsActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopicBriefContextStatus.Visibility = Visibility.Visible;
        var canRequestContext = _loadedVisualization is null &&
            _sessionId is not null &&
            _contextCts is not null &&
            _workIq.IsAuthenticated &&
            contextStatus.State is TopicSemanticInfoState.Loading or TopicSemanticInfoState.Failed;
        TopicBriefContextActionButton.Content =
            contextStatus.State == TopicSemanticInfoState.Failed
                ? "Retry"
                : "Find related work";
        TopicBriefContextActionButton.Visibility =
            canRequestContext ? Visibility.Visible : Visibility.Collapsed;
        TopicBriefContextActionButton.IsEnabled = canRequestContext;
        TopicBriefAnalysisText.Visibility =
            brief.IsCurrent && Volatile.Read(ref _visualizationAnalysisPending) > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        TopicBriefCurrentBadge.Visibility = brief.IsCurrent ? Visibility.Visible : Visibility.Collapsed;

        TopicBriefFactsList.ItemsSource = brief.Facts;
        TopicBriefFactsSection.Visibility = brief.Facts.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        TopicBriefRelationshipsList.ItemsSource = brief.Relationships;
        TopicBriefRelationshipsSection.Visibility = brief.Relationships.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        TopicBriefQuestionsList.ItemsSource = brief.Questions;
        TopicBriefQuestionsSection.Visibility = brief.Questions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        TopicBriefActionsList.ItemsSource = brief.Actions;
        TopicBriefDecisionsList.ItemsSource = brief.Decisions;
        TopicBriefActionsSection.Visibility = brief.Actions.Count > 0 || brief.Decisions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        TopicBriefNoDetailsText.Visibility =
            brief.Facts.Count == 0 && brief.Relationships.Count == 0 && brief.Questions.Count == 0 &&
            brief.Actions.Count == 0 && brief.Decisions.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        TopicBriefSourcesList.ItemsSource = brief.WorkIqSources;
        var overflow = brief.WorkIqVerifiedTotal - brief.WorkIqSources.Count;
        TopicBriefSourcesSection.Visibility = Visibility.Visible;
        TopicBriefSourcesOverflowText.Text = overflow > 0 ? $"+{overflow} verified suggestions" : string.Empty;
        TopicBriefSourcesOverflowText.Visibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Speaker assignment banner ───────────────────────────────────────────────

    private void OnUnknownSpeakerDetected(object? sender, string speakerId)
    {
        var sessionId = _pipeline.CurrentSessionId;
        InvokeOnUi(() =>
        {
            if (sessionId != _sessionId || sessionId is null) return;
            _pendingUnknownSpeakers.Enqueue(speakerId);
            if (_currentPromptSpeakerId is null)
                ShowNextSpeakerPrompt();
        });
    }

    private void OnSpeakerResolved(object? sender, SpeakerResolvedEventArgs e)
    {
        Interlocked.Increment(ref _speakerNameRevision);
        var sessionId = _pipeline.CurrentSessionId;
        InvokeOnUi(() =>
        {
            if (sessionId != _sessionId || sessionId is null) return;
            // Retroactively relabel any existing transcript lines for this speaker, in-place.
            foreach (var line in _transcript)
            {
                if (string.Equals(line.SpeakerId, e.SpeakerId, StringComparison.OrdinalIgnoreCase))
                {
                    line.Speaker = line.Opacity < 1 ? $"{e.DisplayName} (provisional):" : $"{e.DisplayName}:";
                    var avatar = SpeakerAvatarPresentation.Create(e.DisplayName, isResolved: true);
                    line.AvatarInitials = avatar.Initials;
                    line.AvatarAccessibleName = avatar.AccessibleName;
                    line.AvatarImage = null;
                }
            }
            foreach (var speaker in _speakerBalance.Where(s => string.Equals(s.SpeakerId, e.SpeakerId, StringComparison.OrdinalIgnoreCase)))
                speaker.Name = e.DisplayName;
            if (!string.IsNullOrWhiteSpace(_acceptedRelatedContextTopic))
                TldrChip.Text = ProjectVisualizationText(_acceptedRelatedContextTopic);
            var contextStatus = _relatedContext.Status;
            UpdateRelatedContextTopicPresentation(
                IsCurrentRelatedContextStatus(contextStatus) ? contextStatus : null);
            RefreshVisualization();
            if (_catchUpRendered && CatchUpPanel.Visibility == Visibility.Visible)
                CatchUpSummaryText.Text = ProjectVisualizationText(CatchUpSummaryText.Text);
            UpdateRecordingPresentation();

            // If we were prompting for this speaker (e.g. it got auto-resolved), close the banner.
            if (string.Equals(_currentPromptSpeakerId, e.SpeakerId, StringComparison.OrdinalIgnoreCase))
            {
                _currentPromptSpeakerId = null;
                SpeakerNamingPanel.Visibility = Visibility.Collapsed;
                _suppressAutoDismiss = false;
                ShowNextSpeakerPrompt();
            }
        });
    }

    private void ResetSpeakerPrompts()
    {
        _pendingUnknownSpeakers.Clear();
        _currentPromptSpeakerId = null;
        SpeakerNamingPanel.Visibility = Visibility.Collapsed;
        _suppressAutoDismiss = false;
    }

    private void ShowNextSpeakerPrompt()
    {
        // Skip any queued speakers that the user has already named.
        while (_pendingUnknownSpeakers.Count > 0)
        {
            var next = _pendingUnknownSpeakers.Dequeue();
            if (_pipeline.SpeakerResolver.IsResolved(next))
                continue;

            _currentPromptSpeakerId = next;
            SpeakerPromptTitleText.Text = "Name detected speaker";
            SpeakerTargetText.Text = $"Speaker: {next}";
            PopulateSpeakerCombo(next);
            _suppressAutoDismiss = true;
            SpeakerNamingPanel.Visibility = Visibility.Visible;
            return;
        }

        _currentPromptSpeakerId = null;
    }

    private void PopulateSpeakerCombo(string speakerId)
    {
        _speakerNameOptions.Clear();
        _selectedSpeakerNameOption = null;
        var localName = _pipeline.SpeakerResolver.LocalUserDisplayName;
        _speakerNameOptions.Add(new SpeakerNameOption(
            string.IsNullOrWhiteSpace(localName) ? "Me" : localName, null, IsLocal: true));
        foreach (var attendee in _pipeline.CurrentSession?.InviteParticipants ?? [])
            if (!_speakerNameOptions.Any(option =>
                (!string.IsNullOrWhiteSpace(attendee.Email) &&
                 string.Equals(option.Email, attendee.Email, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(option.Name, attendee.DisplayName, StringComparison.OrdinalIgnoreCase)))
                _speakerNameOptions.Add(new SpeakerNameOption(attendee.DisplayName, attendee.Email));
        SpeakerNameSearch.Text = string.Empty;
        SpeakerNameSearch.ItemsSource = _speakerNameOptions;
        SpeakerNameApply.IsEnabled = false;
    }

    private void OnSpeakerNameSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _selectedSpeakerNameOption = null;
        var query = sender.Text.Trim();
        sender.ItemsSource = query.Length == 0 ? _speakerNameOptions
            : _speakerNameOptions.Where(option => option.Matches(query)).ToArray();
        SpeakerNameApply.IsEnabled = query.Length > 0;
    }

    private void OnSpeakerNameSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs e)
    {
        if (e.SelectedItem is SpeakerNameOption option)
        {
            _selectedSpeakerNameOption = option;
            sender.Text = option.Name;
            SpeakerNameApply.IsEnabled = !string.IsNullOrWhiteSpace(option.Name);
        }
    }

    private void OnSpeakerNameQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        var option = e.ChosenSuggestion as SpeakerNameOption ??
            SelectedSpeakerOption(sender.Text);
        ResolveCurrentSpeaker(option?.Name ?? sender.Text, option);
    }

    private void OnSpeakerNameApplyClick(object sender, RoutedEventArgs e)
    {
        var option = SelectedSpeakerOption(SpeakerNameSearch.Text);
        ResolveCurrentSpeaker(option?.Name ?? SpeakerNameSearch.Text, option);
    }

    private void OnSpeakerNameCancelClick(object sender, RoutedEventArgs e) => SkipSpeakerPrompt();

    private SpeakerNameOption? SelectedSpeakerOption(string text)
    {
        var selected = _selectedSpeakerNameOption;
        return selected is not null &&
               string.Equals(selected.Name, text.Trim(), StringComparison.OrdinalIgnoreCase)
            ? selected
            : null;
    }

    private void ResolveCurrentSpeaker(string? displayName, SpeakerNameOption? person = null)
    {
        var id = _currentPromptSpeakerId;
        if (id is null || string.IsNullOrWhiteSpace(displayName))
            return;

        var sessionId = _sessionId;
        var revision = _speakerAvatarRevisions.GetValueOrDefault(id) + 1;
        _speakerAvatarRevisions[id] = revision;
        _speakerAvatarImages.Remove(id);
        _pipeline.SpeakerResolver.Resolve(id, displayName.Trim());
        if (sessionId is not null && person is not null &&
            (person.IsLocal || !string.IsNullOrWhiteSpace(person.Email)))
            _ = LoadSpeakerPhotoAsync(sessionId, id, revision, person);

        _suppressAutoDismiss = false; // re-enable auto-dismiss now that entry is done
        _currentPromptSpeakerId = null;
        _selectedSpeakerNameOption = null;
        SpeakerNamingPanel.Visibility = Visibility.Collapsed;
        ShowNextSpeakerPrompt();
    }

    private async Task LoadSpeakerPhotoAsync(
        string sessionId,
        string speakerId,
        long revision,
        SpeakerNameOption person)
    {
        try
        {
            var token = _recordingCts?.Token ?? CancellationToken.None;
            var photo = await Task.Run(
                () => person.IsLocal
                    ? _workIq.GetCurrentUserPhotoAsync(token)
                    : _workIq.GetProfilePhotoAsync(person.Email, token),
                token);
            if (photo is null)
                return;

            if (_sessionId != sessionId ||
                _speakerAvatarRevisions.GetValueOrDefault(speakerId) != revision)
                return;

            var image = await CreateProfileImageAsync(photo);
            if (_sessionId != sessionId ||
                _speakerAvatarRevisions.GetValueOrDefault(speakerId) != revision)
                return;

            _speakerAvatarImages[speakerId] = image;
            foreach (var line in _transcript.Where(line =>
                         string.Equals(line.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase)))
                line.AvatarImage = image;
        }
        catch (OperationCanceledException)
        {
            // Session ended or the app is exiting; initials remain visible.
        }
        catch (WorkIqException ex)
        {
            _logger.LogWarning("Speaker profile photo provider failed ({Kind}).", ex.Kind);
            ShowStatusBar($"Speaker photo unavailable. {ex.Message}", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Speaker profile photo unavailable ({Type}).", ex.GetType().Name);
            ShowStatusBar("Speaker photo could not be displayed. Initials remain available.",
                InfoBarSeverity.Warning);
        }
    }

    private static async Task<BitmapImage> CreateProfileImageAsync(WorkIqProfilePhoto photo)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(photo.Content);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage
        {
            DecodePixelWidth = 96,
            DecodePixelType = DecodePixelType.Physical,
        };
        await image.SetSourceAsync(stream);
        return image;
    }

    private void SkipSpeakerPrompt()
    {
        _suppressAutoDismiss = false;
        _currentPromptSpeakerId = null;
        SpeakerNamingPanel.Visibility = Visibility.Collapsed;
        ShowNextSpeakerPrompt();
    }

    private void OnSpeakerBalanceNameClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string speakerId })
            PromptSpeakerRename(speakerId);
    }

    private void OnTranscriptSpeakerClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string speakerId })
            PromptSpeakerRename(speakerId);
    }

    /// <summary>
    /// Opens the speaker-rename prompt for <paramref name="speakerId"/>, or queues it
    /// if another prompt is already open. Works for both unresolved and already-named speakers.
    /// Uses "Rename speaker" title (vs "New speaker detected" for auto-triggered prompts).
    /// </summary>
    private void PromptSpeakerRename(string speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || speakerId == "Unknown") return;

        if (_currentPromptSpeakerId != speakerId)
        {
            _currentPromptSpeakerId = speakerId;
            SpeakerPromptTitleText.Text = "Rename speaker";
            SpeakerTargetText.Text = $"Speaker: {speakerId}";
            PopulateSpeakerCombo(speakerId);
            _suppressAutoDismiss = true;
            SpeakerNamingPanel.Visibility = Visibility.Visible;
            SpeakerNameSearch.Focus(FocusState.Programmatic);
        }
    }

    private void InvokeOnUi(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }
}
