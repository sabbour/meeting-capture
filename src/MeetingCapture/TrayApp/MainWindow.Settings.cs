using MeetingCapture.Configuration;
using MeetingCapture.Intelligence;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;

namespace MeetingCapture.TrayApp;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _settingsEditorLifetime;
    private long _settingsFormRevision;
    private bool _testingSynthesis;
    private bool _testingIntelligence;
    private CancellationTokenSource? _copilotModelLoadLifetime;
    private bool _loadingSettingsValues;
    private readonly ObservableCollection<CopilotModelChoice> _synthesisCopilotModels = new();
    private readonly ObservableCollection<CopilotModelChoice> _intelligenceCopilotModels = new();
    private readonly ObservableCollection<AiDiagnosticEventRow> _aiDiagnosticEvents = new();
    private string _synthesisModelSelection = string.Empty;
    private string _intelligenceModelSelection = string.Empty;
    private bool _loadingAiDiagnostics;
    private int _aiDiagnosticsRenderQueued;

    private sealed class AiDiagnosticEventRow
    {
        internal AiDiagnosticEventRow(AiDiagnosticEventPresentation value, bool allowSensitive)
        {
            Sequence = value.Sequence;
            Time = value.Time;
            Capability = value.Capability;
            ProviderModel = value.ProviderModel;
            Status = value.Status;
            Duration = value.Duration;
            RequestLabel = value.RequestLabel;
            RequestIdentity = value.RequestIdentity;
            Usage = value.Usage;
            Quota = value.Quota;
            AutomationName = value.AutomationName;
            var showSensitive = allowSensitive && value.HasSensitivePayload;
            SensitivePayloadVisibility = showSensitive ? Visibility.Visible : Visibility.Collapsed;
            TruncationVisibility = showSensitive && value.PayloadTruncated
                ? Visibility.Visible
                : Visibility.Collapsed;
            TruncationNotice = value.PayloadTruncated
                ? "One or more captured payloads were truncated at the in-memory size limit."
                : string.Empty;
            PromptPayload = value.PromptPayload ?? "Not captured for this event.";
            RequestPayload = value.RequestPayload ?? "Not captured for this event.";
            ResponsePayload = value.ResponsePayload ?? "Not captured for this event.";
        }

        public long Sequence { get; }
        public string Time { get; }
        public string Capability { get; }
        public string ProviderModel { get; }
        public string Status { get; }
        public string Duration { get; }
        public string RequestLabel { get; }
        public string RequestIdentity { get; }
        public string Usage { get; }
        public string Quota { get; }
        public string AutomationName { get; }
        public Visibility SensitivePayloadVisibility { get; }
        public Visibility TruncationVisibility { get; }
        public string TruncationNotice { get; }
        public string PromptPayload { get; }
        public string RequestPayload { get; }
        public string ResponsePayload { get; }
    }

    private void InitializeSettingsEditor()
    {
        foreach (var field in new[]
                 {
                     IEndpoint, IApiVersion, ISynthesisDeployment, IIntelligenceDeployment,
                 })
            field.TextChanged += (_, _) => InvalidateModelTests();

        ISynthesisModel.ItemsSource = _synthesisCopilotModels;
        IIntelligenceModel.ItemsSource = _intelligenceCopilotModels;
        ISynthesisModel.SelectionChanged += (_, _) =>
        {
            RetainCopilotModelSelection(ISynthesisModel, ref _synthesisModelSelection);
            InvalidateModelTests();
        };
        IIntelligenceModel.SelectionChanged += (_, _) =>
        {
            RetainCopilotModelSelection(IIntelligenceModel, ref _intelligenceModelSelection);
            InvalidateModelTests();
        };
        IApiKey.PasswordChanged += (_, _) => InvalidateModelTests();
        AiDiagnosticsList.ItemsSource = _aiDiagnosticEvents;
        RenderAiDiagnostics();
    }

    private void OpenSettingsEditor()
    {
        CancelSettingsEditorLifetime();
        _settingsEditorLifetime = new CancellationTokenSource();
        SaveButton.IsEnabled = true;
        SaveStatus.IsOpen = false;

        try
        {
            _loadingSettingsValues = true;
            LoadSettingsValues();
            _loadingSettingsValues = false;
            InvalidateModelTests();
            if (IProvider.SelectedIndex == 0)
                _ = RefreshCopilotModelsAsync(forceRefresh: false);
            RenderAiDiagnostics();
        }

        catch (Exception ex)
        {
            _loadingSettingsValues = false;
            _logger.LogWarning("Settings load failed ({Type}).", ex.GetType().Name);
            ShowSettingsStatus(
                "Saved settings could not be read. Resolve the settings file error before saving.",
                InfoBarSeverity.Error);
            SaveButton.IsEnabled = false;
        }
    }

    private void OnAiDiagnosticsChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _aiDiagnosticsRenderQueued, 1) != 0)
            return;
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _aiDiagnosticsRenderQueued, 0);
                if (PreferencesPanel.Visibility == Visibility.Visible)
                    RenderAiDiagnostics();
            }))
        {
            Interlocked.Exchange(ref _aiDiagnosticsRenderQueued, 0);
        }
    }

    private void OnAiDiagnosticsSensitiveToggle(object sender, RoutedEventArgs e)
    {
        if (_loadingAiDiagnostics)
            return;
        var enabled = AiDiagnosticsSensitiveToggle.IsOn;
        _pipeline.AiDiagnostics.SetSensitiveContentCapture(enabled);
        AiDiagnosticsAnnouncementText.Text = enabled
            ? "Sensitive AI diagnostic content capture enabled for this session."
            : "Sensitive AI diagnostic content capture disabled. Captured payloads were removed.";
    }

    private void OnAiDiagnosticsClearClick(object sender, RoutedEventArgs e)
    {
        _pipeline.AiDiagnostics.Clear();
        AiDiagnosticsAnnouncementText.Text = "AI diagnostic events cleared.";
    }

    private void RenderAiDiagnostics()
    {
        try
        {
            var snapshot = _pipeline.AiDiagnostics.GetSnapshot();
            _loadingAiDiagnostics = true;
            AiDiagnosticsSensitiveToggle.IsOn = snapshot.SensitiveContentCaptureEnabled;
            _loadingAiDiagnostics = false;
            var rows = snapshot.Events
                .OrderBy(item => item.Sequence)
                .Select(item => new AiDiagnosticEventRow(
                    AiDiagnosticsPresentation.Event(item),
                    snapshot.SensitiveContentCaptureEnabled))
                .ToArray();
            VisualizationCollectionState.Reconcile(
                _aiDiagnosticEvents,
                rows,
                item => item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item => string.Join('\u001f',
                    item.Status,
                    item.Duration,
                    item.Usage,
                    item.Quota,
                    item.SensitivePayloadVisibility,
                    item.TruncationNotice,
                    item.PromptPayload,
                    item.RequestPayload,
                    item.ResponsePayload));
            AiDiagnosticsList.Visibility = rows.Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            AiDiagnosticsEmptyText.Text =
                "No AI diagnostic events yet. Metadata appears when an AI or WorkIQ request starts.";
            AiDiagnosticsEmptyText.Visibility = rows.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            AiDiagnosticsClearButton.IsEnabled = rows.Length > 0;
            AiDiagnosticsStatusText.Text = snapshot.SessionId is null
                ? "No active diagnostics session. Metadata will appear when a recording starts."
                : rows.Length == 0
                    ? "Diagnostics session active. No AI or WorkIQ events have been captured."
                    : $"{rows.Length} event{(rows.Length == 1 ? "" : "s")} · " +
                      $"updated {snapshot.CapturedAt.ToLocalTime():T} · " +
                      (snapshot.SensitiveContentCaptureEnabled
                          ? "sensitive content capture on"
                          : "metadata only");
        }
        catch (Exception ex)
        {
            _loadingAiDiagnostics = false;
            _logger.LogWarning("AI diagnostics presentation failed ({Type}).", ex.GetType().Name);
            _aiDiagnosticEvents.Clear();
            AiDiagnosticsList.Visibility = Visibility.Collapsed;
            AiDiagnosticsEmptyText.Visibility = Visibility.Visible;
            AiDiagnosticsEmptyText.Text =
                "AI diagnostics could not be displayed. Recording and AI requests continue.";
            AiDiagnosticsStatusText.Text = "AI diagnostics unavailable.";
            AiDiagnosticsClearButton.IsEnabled = false;
            AiDiagnosticsAnnouncementText.Text = "AI diagnostics could not be displayed.";
        }
    }

    private void CancelSettingsEditor()
    {
        CancelSettingsEditorLifetime();
        SaveStatus.IsOpen = false;
    }

    private void CancelSettingsEditorLifetime()
    {
        var lifetime = Interlocked.Exchange(ref _settingsEditorLifetime, null);
        lifetime?.Cancel();
        lifetime?.Dispose();
        _testingSynthesis = false;
        _testingIntelligence = false;
    }

    private void LoadSettingsValues()
    {
        var saved = _settingsManager.ReadSaved();

        TEndpoint.Text = saved?.TranscriptionEndpoint ?? _settings.Transcription.Endpoint;
        TRegion.Text = saved?.TranscriptionRegion ?? _settings.Transcription.Region;
        TLanguage.Text = saved?.TranscriptionLanguage ?? _settings.Transcription.Language;
        TApiKey.Password = string.Empty;
        TApiKey.PlaceholderText = HasSettingValue(_settings.Transcription.ApiKey)
            ? "Configured - type new value to replace"
            : "Optional - enter to enable transcription";

        var active = _settingsManager.GetIntelligenceSnapshot().Settings;
        var isGitHub = string.Equals(active.Provider, "github", StringComparison.OrdinalIgnoreCase);
        IProvider.SelectedIndex = isGitHub ? 0 : 1;
        ApplyProviderVisibility(isGitHub);

        SetConfiguredCopilotModels(active.SynthesisModel, active.IntelligenceModel);
        IEndpoint.Text = active.Endpoint;
        IApiVersion.Text = active.ApiVersion;
        IApiKey.Password = string.Empty;
        IApiKey.PlaceholderText = HasSettingValue(active.ApiKey)
            ? "Configured - type new value to replace"
            : "Optional - enter to enable note synthesis";
        ISynthesisDeployment.Text = active.SynthesisDeployment;
        IIntelligenceDeployment.Text = active.IntelligenceDeployment;

        CaptureMic.IsOn = saved?.CaptureMicrophone ?? _settings.Audio.CaptureMicrophone;
        CaptureLoopback.IsOn = saved?.CaptureLoopback ?? _settings.Audio.CaptureLoopback;
        NotesPath.Text = saved?.NotesPath ?? _settings.Storage.NotesPath;
    }

    private void ApplyProviderVisibility(bool isGitHub)
    {
        GitHubIntelligenceFields.Visibility = isGitHub ? Visibility.Visible : Visibility.Collapsed;
        AzureIntelligenceFields.Visibility = isGitHub ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GitHubIntelligenceFields is null || AzureIntelligenceFields is null)
            return;

        ApplyProviderVisibility(IProvider.SelectedIndex == 0);
        if (!_loadingSettingsValues && IProvider.SelectedIndex == 0)
            _ = RefreshCopilotModelsAsync(forceRefresh: false);
        InvalidateModelTests();
    }

    private async void OnRefreshCopilotModelsClick(object sender, RoutedEventArgs e)
        => await RefreshCopilotModelsAsync(forceRefresh: true);

    private async Task RefreshCopilotModelsAsync(bool forceRefresh)
    {
        var lifetime = _settingsEditorLifetime;
        if (lifetime is null || lifetime.IsCancellationRequested)
            return;
        if (_copilotModelLoadLifetime == lifetime)
            return;

        var synthesis = CurrentCopilotModel(ISynthesisModel, _synthesisModelSelection);
        var intelligence = CurrentCopilotModel(IIntelligenceModel, _intelligenceModelSelection);
        _copilotModelLoadLifetime = lifetime;
        RefreshCopilotModelsButton.IsEnabled = false;
        ISynthesisModel.IsEnabled = false;
        IIntelligenceModel.IsEnabled = false;
        CopilotModelCatalogStatus.Text = "Loading available GitHub Copilot models...";
        try
        {
            var snapshot = await _copilotModelCatalog.GetModelsAsync(forceRefresh, lifetime.Token);
            if (_settingsEditorLifetime != lifetime || lifetime.IsCancellationRequested)
                return;

            ReplaceCopilotModels(CopilotModelPresentation.Merge(
                snapshot.Models,
                synthesis,
                intelligence));
            SelectCopilotModel(ISynthesisModel, synthesis);
            SelectCopilotModel(IIntelligenceModel, intelligence);
            var unavailable = _synthesisCopilotModels
                .Where(model => !model.IsAvailable)
                .Select(model => model.Id)
                .ToArray();
            CopilotModelCatalogStatus.Text = unavailable.Length == 0
                ? $"{snapshot.Models.Count} account model{(snapshot.Models.Count == 1 ? "" : "s")} available."
                : $"{snapshot.Models.Count} account model{(snapshot.Models.Count == 1 ? "" : "s")} available. " +
                  $"Configured selection{(unavailable.Length == 1 ? "" : "s")} " +
                  $"{string.Join(", ", unavailable)} not currently available; retained until changed.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("GitHub Copilot model catalog refresh failed ({Type}).", ex.GetType().Name);
            CopilotModelCatalogStatus.Text =
                "Available models could not be loaded. Check GitHub CLI sign-in and connection, then refresh. " +
                "Configured selections are retained.";
        }
        finally
        {
            if (_copilotModelLoadLifetime == lifetime)
            {
                _copilotModelLoadLifetime = null;
                if (_settingsEditorLifetime == lifetime)
                {
                    RefreshCopilotModelsButton.IsEnabled = true;
                    ISynthesisModel.IsEnabled = true;
                    IIntelligenceModel.IsEnabled = true;
                }
            }
        }
    }

    private void SetConfiguredCopilotModels(string synthesis, string intelligence)
    {
        _synthesisModelSelection = synthesis.Trim();
        _intelligenceModelSelection = intelligence.Trim();
        ReplaceCopilotModels(CopilotModelPresentation.Merge([], synthesis, intelligence));
        SelectCopilotModel(ISynthesisModel, synthesis);
        SelectCopilotModel(IIntelligenceModel, intelligence);
    }

    private void ReplaceCopilotModels(IReadOnlyList<CopilotModelChoice> choices)
    {
        _synthesisCopilotModels.Clear();
        _intelligenceCopilotModels.Clear();
        foreach (var choice in choices)
        {
            _synthesisCopilotModels.Add(choice);
            _intelligenceCopilotModels.Add(choice);
        }
    }

    private static string SelectedCopilotModel(ComboBox selector)
        => selector.SelectedValue as string ?? string.Empty;

    private static string CurrentCopilotModel(ComboBox selector, string retained)
    {
        var selected = SelectedCopilotModel(selector);
        return selected.Length > 0 ? selected : retained;
    }

    private static void RetainCopilotModelSelection(ComboBox selector, ref string retained)
    {
        var selected = SelectedCopilotModel(selector);
        if (selected.Length > 0)
            retained = selected;
    }

    private static void SelectCopilotModel(ComboBox selector, string id)
        => selector.SelectedItem = CopilotModelPresentation.FindSelection(
            selector.Items.OfType<CopilotModelChoice>(),
            id);

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                NotesPath.Text = folder.Path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Folder picker failed ({Type}).", ex.GetType().Name);
            ShowSettingsStatus("Unable to open the folder picker.", InfoBarSeverity.Error);
        }
    }

    private async void OnTestSynthesisClick(object sender, RoutedEventArgs e)
    {
        var isGitHub = IProvider.SelectedIndex == 0;
        await RunModelTestAsync(
            isSynthesis: true,
            isGitHub ? GhTestSynthesisBtn : AzTestSynthesisBtn,
            isGitHub ? GhTestSynthesisResult : AzTestSynthesisResult);
    }

    private async void OnTestIntelligenceClick(object sender, RoutedEventArgs e)
    {
        var isGitHub = IProvider.SelectedIndex == 0;
        await RunModelTestAsync(
            isSynthesis: false,
            isGitHub ? GhTestIntelligenceBtn : AzTestIntelligenceBtn,
            isGitHub ? GhTestIntelligenceResult : AzTestIntelligenceResult);
    }

    private async Task RunModelTestAsync(bool isSynthesis, Button button, TextBlock resultText)
    {
        if (isSynthesis ? _testingSynthesis : _testingIntelligence)
            return;

        var lifetime = _settingsEditorLifetime;
        if (lifetime is null || lifetime.IsCancellationRequested)
            return;

        if (isSynthesis)
            _testingSynthesis = true;
        else
            _testingIntelligence = true;

        var revision = _settingsFormRevision;
        button.IsEnabled = false;
        resultText.Text = "Testing...";
        resultText.Visibility = Visibility.Visible;

        try
        {
            var formSettings = BuildIntelligenceSettingsFromForm();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(75));
            var (ok, detail) = await Task.Run(
                () => _intelligence.ValidateModelAsync(isSynthesis, formSettings, cts.Token),
                cts.Token);
            if (revision == _settingsFormRevision && !lifetime.IsCancellationRequested)
                resultText.Text = ok ? $"Success: {detail}" : $"Failed: {detail}";
        }
        catch (OperationCanceledException)
        {
            if (revision == _settingsFormRevision && !lifetime.IsCancellationRequested)
                resultText.Text = "Test cancelled or timed out (75-second limit).";
            _logger.LogInformation("Model validation cancelled or timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Model validation failed ({Type}).", ex.GetType().Name);
            if (revision == _settingsFormRevision && !lifetime.IsCancellationRequested)
                resultText.Text = ex is IntelligenceException typed
                    ? typed.Failure.Message
                    : "Model test failed. Check model, credentials and connection.";
        }
        finally
        {
            if (_settingsEditorLifetime == lifetime)
                button.IsEnabled = true;
            if (isSynthesis)
                _testingSynthesis = false;
            else
                _testingIntelligence = false;
        }
    }

    private Configuration.IntelligenceSettings BuildIntelligenceSettingsFromForm()
    {
        var savedApiKey = _settingsManager.GetIntelligenceSnapshot().Settings.ApiKey;
        return new Configuration.IntelligenceSettings
        {
            Provider = IProvider.SelectedIndex == 0 ? "github" : "azure",
            SynthesisModel = CurrentCopilotModel(ISynthesisModel, _synthesisModelSelection),
            IntelligenceModel = CurrentCopilotModel(IIntelligenceModel, _intelligenceModelSelection),
            Endpoint = IEndpoint.Text.Trim(),
            ApiVersion = IApiVersion.Text.Trim(),
            ApiKey = string.IsNullOrEmpty(IApiKey.Password) ? savedApiKey : IApiKey.Password,
            SynthesisDeployment = ISynthesisDeployment.Text.Trim(),
            IntelligenceDeployment = IIntelligenceDeployment.Text.Trim(),
        };
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            if (!CaptureMic.IsOn && !CaptureLoopback.IsOn)
                throw new InvalidOperationException("Select at least one audio source.");

            var result = await _settingsManager.SaveAsync(new EditableSettings
            {
                TranscriptionEndpoint = TEndpoint.Text.Trim(),
                TranscriptionRegion = TRegion.Text.Trim(),
                TranscriptionLanguage = TLanguage.Text.Trim(),
                TranscriptionApiKey = TApiKey.Password,
                IntelligenceProvider = IProvider.SelectedIndex == 0 ? "github" : "azure",
                IntelligenceSynthesisModel = CurrentCopilotModel(
                    ISynthesisModel,
                    _synthesisModelSelection),
                IntelligenceModel = CurrentCopilotModel(
                    IIntelligenceModel,
                    _intelligenceModelSelection),
                IntelligenceEndpoint = IEndpoint.Text.Trim(),
                IntelligenceApiVersion = IApiVersion.Text.Trim(),
                IntelligenceApiKey = IApiKey.Password,
                IntelligenceSynthesisDeployment = ISynthesisDeployment.Text.Trim(),
                IntelligenceDeployment = IIntelligenceDeployment.Text.Trim(),
                CaptureMicrophone = CaptureMic.IsOn,
                CaptureLoopback = CaptureLoopback.IsOn,
                NotesPath = NotesPath.Text.Trim(),
            });

            var restart = result.RestartRequiredSections.Count == 0
                ? " No restart required."
                : $" Restart required for {string.Join(", ", result.RestartRequiredSections)}; current capture is unchanged.";
            ShowSettingsStatus(
                "Saved. Intelligence and synthesis changes apply to new requests." + restart +
                " Higher-precedence environment or user-secret overrides remain effective.",
                InfoBarSeverity.Success);
            TApiKey.Password = string.Empty;
            IApiKey.Password = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Settings save failed ({Type}).", ex.GetType().Name);
            ShowSettingsStatus(
                !CaptureMic.IsOn && !CaptureLoopback.IsOn
                    ? "Select at least one audio source."
                    : "Settings could not be saved or applied. Check the settings file and permissions; active settings may be unchanged.",
                InfoBarSeverity.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void OnSettingsCancelClick(object sender, RoutedEventArgs e)
    {
        CancelSettingsEditor();
        ShowState(_preferencesFromRecording);
    }

    private void InvalidateModelTests()
    {
        _settingsFormRevision++;
        foreach (var result in new[]
                 {
                     GhTestSynthesisResult, GhTestIntelligenceResult,
                     AzTestSynthesisResult, AzTestIntelligenceResult,
                 })
        {
            result.Text = "Settings changed - test again.";
            result.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSettingsStatus(string message, InfoBarSeverity severity)
    {
        SaveStatus.Severity = severity;
        SaveStatus.Message = message;
        SaveStatus.IsOpen = true;
    }

    private static bool HasSettingValue(string value) => !string.IsNullOrWhiteSpace(value);
}
