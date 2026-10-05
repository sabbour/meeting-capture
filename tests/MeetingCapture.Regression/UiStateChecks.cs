using MeetingCapture.Intelligence;
using MeetingCapture.TrayApp;
using MeetingCapture.Pipeline;
using MeetingCapture.WorkIq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeetingCapture.Regression;

internal static class UiStateChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("panel guard rejects overlapping and unchanged requests but permits explicit retry", () => Run(SingleFlight));
        yield return ("panel invalidation rejects stale session/config results without releasing active guard", () => Run(Invalidation));
        yield return ("panel peers fail independently and preserve last-success status", () => Run(IndependentPanels));
        yield return ("panel rejects older applied revision but permits growing-transcript results", () => Run(Revisions));
        yield return ("transcript viewport bounds 500 rows and preserves paused scrollback", () => Run(Viewport));
        yield return ("transcript rows update incrementally without resetting stable identities", () => Run(TranscriptRows));
        yield return ("repeated partial transcript refreshes preserve rows avatars and follow only structural progress", () => Run(TranscriptRefreshPattern));
        yield return ("window placement scales defaults, preserves user size and clamps to work area", () => Run(WindowBounds));
        yield return ("single-instance activation cannot leave a headless process waiting on a stale registration", () => Run(SingleInstanceActivation));
        yield return ("high-rate transcript and AI events coalesce before entering the UI dispatcher", () => Run(DispatcherCoalescing));
        yield return ("visualization expansion is responsive and stable updates animate only once", () => Run(VisualizationExpansion));
        yield return ("desktop manifest enables per-monitor native rendering without elevation", () => Run(() =>
        {
            var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "app.manifest"));
            XNamespace dpi = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
            XNamespace security = "urn:schemas-microsoft-com:asm.v3";
            Check.Equal("PerMonitorV2", manifest.Descendants(dpi + "dpiAwareness").Single().Value);
            var level = manifest.Descendants(security + "requestedExecutionLevel").Single();
            Check.Equal("asInvoker", (string?)level.Attribute("level"));
            Check.Equal("false", (string?)level.Attribute("uiAccess"));
        }));
        yield return ("meeting detection prompt foregrounds meeting identity and accessible actions", () => Run(() =>
        {
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Control(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            Check.Equal("Take notes for this meeting?", (string?)Control("MeetingPrompt").Attribute("Title"));
            foreach (var name in new[]
                     {
                         "MeetingPromptSubject", "MeetingPromptSource", "TakeNotesButton",
                         "ChooseCalendarMeetingButton"
                     })
                Check.True(!string.IsNullOrWhiteSpace((string?)Control(name).Attribute("AutomationProperties.Name")));
            Check.Equal("Collapsed", (string?)Control("MeetingPromptSchedule").Attribute("Visibility"));
            Check.Equal("Collapsed", (string?)Control("MeetingPromptOrganizer").Attribute("Visibility"));
            Check.Equal("Collapsed", (string?)Control("MeetingPromptInvitees").Attribute("Visibility"));
            Check.True(!document.Descendants().Any(e =>
                (string?)e.Attribute(x + "Name") is "MeetingPromptSignal" or
                    "MeetingPromptExplanation" or "MeetingPromptConfidence"));
            var template = document.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "MeetingRowTemplate");
            Check.True(template.Descendants().Any(e => (string?)e.Attribute("Text") == "{Binding DetailsText}"));
            Check.True(template.Descendants().Any(e =>
                (string?)e.Attribute("AutomationProperties.Name") == "{Binding AccessibleName}"));
        }));
        yield return ("recording layout reserves transcript space without an in-body settings panel", () => Run(RecordingSpace));
        yield return ("all settings share the main page with fixed navigation and save actions", () => Run(UnifiedSettings));
        yield return ("save review countdown reflects its deadline and closes decisions at expiry", () => Run(SaveReviewCountdown));
        yield return ("focus loss keeps active capture visible unless explicitly closed", () => Run(PinBehavior));
        yield return ("title header is draggable without intercepting header controls", () => Run(DraggableTitleBar));
        yield return ("speaker avatar fallback uses initials only after explicit resolution", () => Run(SpeakerAvatarFallback));
        yield return ("tray context menu keeps its labels readable", () => Run(TrayMenuWidth));
        yield return ("context suggestions expose freshness and one-action external source affordances", () => Run(ContextFlyout));
        yield return ("AI activity shows typed states reported metrics and quiet accessible transitions", () => Run(AiActivity));
        yield return ("meeting context participants stay a bounded preview separated by verification", () => Run(ParticipantsPreview));
        yield return ("roster refresh reports a fresh observation and check time on every outcome", () => Run(RosterRefreshFeedback));
        yield return ("catch me up owns a visible surface with bounded grounded context", () => Run(CatchMeUpSurface));
        yield return ("AI diagnostics defaults to metadata and gates session-only payload details", () => Run(AiDiagnostics));
        yield return ("related context labels the actual queried topic after a live reword", () => Run(RelatedContextTopicLabel));
        yield return ("long summaries still update visualization while related context stays bounded", () => Run(LongVisualizationTopic));
        yield return ("topic timeline preserves relations chronology selection navigation and scroll", () => Run(TopicTimeline));
        yield return ("brain map nodes own Canvas insertion without re-adding the root", () => Run(BrainMapCanvasOwnership));
        yield return ("topic timeline projects confirmed speaker corrections without changing canonical topics", () => Run(TopicTimelineSpeakerProjection));
        yield return ("checklist completion is reversible and retained items are not reopened", () => Run(ChecklistCompletion));
        yield return ("recording view hides AI retries and pagination before finalized speech", () => Run(() =>
        {
            var empty = RecordingViewState.Create(PipelineState.Recording, 0, true, false, false);
            Check.True(empty.ShowCatchUp && !empty.CanSummarize && !empty.ShowSummaryRetry &&
                !empty.ShowPaging && !empty.ShowLatest);
            Check.Equal("Waiting for recognized speech...", empty.EmptyMessage);
            var failedWithoutSpeech = RecordingViewState.Create(PipelineState.Recording, 0, true, true, false);
            Check.True(failedWithoutSpeech.ShowCatchUp &&
                !failedWithoutSpeech.CanSummarize && !failedWithoutSpeech.ShowSummaryRetry);
            var stopped = RecordingViewState.Create(PipelineState.Saved, 0, true, false, false);
            Check.True(!stopped.ShowCatchUp);
            Check.Equal("No finalized speech was recognized.", stopped.EmptyMessage);
        }));
        yield return ("recording view exposes useful AI and scrollback actions only", () => Run(() =>
        {
            var speech = RecordingViewState.Create(PipelineState.Recording, 1, true, false, false);
            Check.True(speech.ShowCatchUp && speech.CanSummarize &&
                !speech.ShowSummaryRetry && !speech.ShowPaging);
            var failed = RecordingViewState.Create(PipelineState.Recording, 1, true, true, false);
            Check.True(failed.CanSummarize && failed.ShowSummaryRetry);
            var busy = RecordingViewState.Create(PipelineState.Recording, 1, true, true, true);
            Check.True(!busy.CanSummarize);
            var paused = RecordingViewState.Create(PipelineState.Recording, 1, false, false, false);
            Check.True(paused.ShowLatest && !paused.ShowPaging);
            Check.True(!RecordingViewState.Create(PipelineState.Recording, 500, true, false, false).ShowPaging);
            Check.True(RecordingViewState.Create(PipelineState.Recording, 501, true, false, false).ShowPaging);
            Check.True(!RecordingViewState.Create(PipelineState.Saved, 501, true, true, false).CanSummarize);
        }));
        yield return ("recording notice opens once per newly recording session", () => Run(() =>
        {
            Check.True(!RecordingViewState.ShouldShowNotice(PipelineState.Starting, "session-a", null));
            Check.True(RecordingViewState.ShouldShowNotice(PipelineState.Recording, "session-a", null));
            Check.True(!RecordingViewState.ShouldShowNotice(PipelineState.Recording, "session-a", "session-a"));
            Check.True(RecordingViewState.ShouldShowNotice(PipelineState.Recording, "session-b", "session-a"));
            Check.True(!RecordingViewState.ShouldShowNotice(PipelineState.Saved, "session-b", "session-a"));
        }));
        yield return ("rolling questions and actions retain prior snapshots without duplicates", () => Run(() =>
        {
            var retained = new List<string> { "First item" };
            retained.AddRange(InsightRetention.NewItems(retained, ["first item", " Second item ", ""]));
            Check.Equal("First item|Second item", string.Join("|", retained));
            retained.AddRange(InsightRetention.NewItems(retained, ["Third item"]));
            Check.Equal("First item|Second item|Third item", string.Join("|", retained));
            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "First item" };
            retained.Remove("First item");
            var newItems = InsightRetention.NewItems(retained.Concat(completed), ["FIRST ITEM", "Fourth item"]);
            Check.Equal("Fourth item", string.Join("|", newItems));
        }));
        yield return ("recording workspace keeps transcript checklists speaker naming Stop and auth cancel accessible", () => Run(() =>
        {
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Control(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            var panel = Control("RecordingPanel");
            string? RowOf(string name) => (string?)Control(name).AncestorsAndSelf()
                .First(e => e.Parent == panel).Attribute("Grid.Row");
            Check.Equal("0", RowOf("RecordingTitleText"));
            Check.Equal("1", RowOf("InsightSections"));
            Check.Equal("2", RowOf("SpeakerBalancePanel"));
            Check.Equal("4", RowOf("TranscriptList"));
            Check.Equal("3", RowOf("RecordingUtilityActions"));
            Check.Equal("5", RowOf("StopButton"));
            Check.True(!Control("StopButton").Ancestors().Any(e => e.Name.LocalName == "ScrollViewer"));
            Check.True(Control("TranscriptList").Ancestors().Contains(Control("TranscriptExpander")));
            Check.Equal("True", (string?)Control("TranscriptExpander").Attribute("IsExpanded"));
            Check.Equal("Live transcript",
                (string?)Control("TranscriptExpander").Attribute("AutomationProperties.Name"));
            Check.True(Control("TranscriptHeaderText").Ancestors().Contains(Control("TranscriptExpander")));
            Check.True(Control("TldrChip").Ancestors().Contains(panel));
            Check.Equal("Current topic", document.Descendants()
                .Single(e => (string?)e.Attribute("Text") == "Current topic").Attribute("Text")!.Value);
            Check.Equal("Normal", (string?)Control("TldrChip").Attribute("FontWeight"));
            Check.Equal("16", (string?)Control("TldrChip").Attribute("FontSize"));
            Check.True(Control("TldrChip").Attribute("MaxLines") is null);
            Check.True(Control("TldrChip").Attribute("TextTrimming") is null);
            Check.Equal("{ThemeResource ControlFillColorSecondaryBrush}",
                (string?)Control("CurrentTopicSection").Attribute("Background"));
            Check.Equal("{StaticResource RecordingInsightExpanderStyle}",
                (string?)Control("OpenQuestionsExpander").Attribute("Style"));
            Check.Equal("{ThemeResource CardBackgroundFillColorDefaultBrush}",
                (string?)Control("OpenQuestionsExpander").Attribute("Background"));
            Check.Equal("{ThemeResource CardBackgroundFillColorSecondaryBrush}",
                (string?)Control("ActionsExpander").Attribute("Background"));
            Check.True(Control("OpenQuestionsExpander").Parent == Control("InsightSections"));
            Check.True(Control("ActionsExpander").Parent == Control("InsightSections"));
            Check.True(!Control("InsightSections").Ancestors().Contains(Control("RecordingOverviewScroll")));
            Check.True(Control("RecordingOverviewScroll").ElementsAfterSelf()
                .Contains(Control("InsightSections")));
            Check.Equal("8", (string?)Control("InsightSections").Attribute("RowSpacing"));
            Check.Equal(1, Control("InsightSections")
                .Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements().Count());
            Check.Equal(2, Control("InsightSections")
                .Elements().Single(e => e.Name.LocalName == "Grid.RowDefinitions")
                .Elements().Count());
            Check.Equal("0", (string?)Control("OpenQuestionsExpander").Attribute("Grid.Column"));
            Check.Equal("0", (string?)Control("OpenQuestionsExpander").Attribute("Grid.Row"));
            Check.Equal("0", (string?)Control("ActionsExpander").Attribute("Grid.Column"));
            Check.Equal("1", (string?)Control("ActionsExpander").Attribute("Grid.Row"));
            Check.Equal("Top", (string?)Control("OpenQuestionsExpander").Attribute("VerticalAlignment"));
            Check.Equal("Top", (string?)Control("ActionsExpander").Attribute("VerticalAlignment"));
            Check.Equal("Wrap", (string?)Control("OpenQuestionsHeader").Attribute("TextWrapping"));
            Check.Equal("2", (string?)Control("OpenQuestionsHeader").Attribute("MaxLines"));
            Check.Equal("Wrap", (string?)Control("ActionsHeader").Attribute("TextWrapping"));
            Check.Equal("2", (string?)Control("ActionsHeader").Attribute("MaxLines"));
            Check.True(Control("OpenQuestionsItemsScroll").Ancestors().Contains(Control("OpenQuestionsExpander")));
            Check.True(Control("ActionsItemsScroll").Ancestors().Contains(Control("ActionsExpander")));
            Check.Equal(2, document.Descendants().Count(e => (string?)e.Attribute("Click") == "OnChecklistItemToggled"));
            foreach (var name in new[] { "QuestionChecklistToggle", "ActionChecklistToggle" })
            {
                var toggle = Control(name);
                Check.Equal("CheckBox", toggle.Name.LocalName);
                Check.Equal("0", (string?)toggle.Attribute("MinWidth"));
                Check.Equal("4,2", (string?)toggle.Attribute("Padding"));
                Check.Equal("Stretch", (string?)toggle.Attribute("HorizontalAlignment"));
                Check.Equal("Left", (string?)toggle.Attribute("HorizontalContentAlignment"));
                Check.Equal("{Binding Text}", (string?)toggle.Attribute("AutomationProperties.Name"));
                Check.Equal("Wrap", (string?)toggle.Elements().Single().Attribute("TextWrapping"));
            }
            foreach (var name in new[] { "OpenQuestionsItemsScroll", "ActionsItemsScroll" })
            {
                Check.Equal("156", (string?)Control(name).Attribute("MaxHeight"));
                Check.Equal("Disabled", (string?)Control(name).Attribute("HorizontalScrollBarVisibility"));
            }
            foreach (var name in new[] { "OpenQuestionsList", "ActionsList" })
                Check.Equal("Stretch", (string?)Control(name).Attribute("HorizontalContentAlignment"));
            var mainSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            Check.True(!mainSource.Contains("Updated from the live transcript.", StringComparison.Ordinal));
            Check.True(mainSource.Contains(
                "is PanelState.SucceededEmpty or PanelState.SucceededWithResults",
                StringComparison.Ordinal));
            Check.True(!mainSource.Contains("No new questions in the latest conversation.", StringComparison.Ordinal));
            Check.True(!mainSource.Contains("No new actions in the latest conversation.", StringComparison.Ordinal));
            Check.True(!document.Descendants().Any(e => (string?)e.Attribute("Click") == "OnQuestionAnsweredClick"));
            Check.True(!document.Descendants().Any(e => (string?)e.Attribute("Click") == "OnActionCompletedClick"));
            Check.Equal("True", (string?)Control("RecordingNotice").Attribute("IsClosable"));
            Check.Equal("Transcription is on", (string?)Control("RecordingNotice").Attribute("Title"));
            Check.Equal("OnCopyRecordingNoticeClick", (string?)Control("CopyRecordingNoticeButton").Attribute("Click"));
            Check.True(Control("RecordingNotice").Parent == Control("RecordingNoticeHost"));
            Check.True(Control("CopyRecordingNoticeButton").Parent == Control("RecordingNoticeHost"));
            Check.True(!Control("CopyRecordingNoticeButton").Ancestors().Contains(Control("RecordingNotice")));
            Check.Equal("Collapsed", (string?)Control("RecordingNoticeHost").Attribute("Visibility"));
            Check.Equal("32", (string?)Control("CopyRecordingNoticeButton").Attribute("Width"));
            Check.Equal("32", (string?)Control("CopyRecordingNoticeButton").Attribute("Height"));
            Check.Equal("0", (string?)Control("CopyRecordingNoticeButton").Attribute("Padding"));
            Check.Equal("Right", (string?)Control("CopyRecordingNoticeButton").Attribute("HorizontalAlignment"));
            Check.Equal("Top", (string?)Control("CopyRecordingNoticeButton").Attribute("VerticalAlignment"));
            Check.Equal("0,8,48,0", (string?)Control("CopyRecordingNoticeButton").Attribute("Margin"));
            Check.Equal("Copy transcription notice",
                (string?)Control("CopyRecordingNoticeButton").Attribute("AutomationProperties.Name"));
            Check.Equal("Copy transcription notice",
                (string?)Control("CopyRecordingNoticeButton").Attribute("ToolTipService.ToolTip"));
            Check.True(!Control("RecordingNotice").Elements().Any(e => e.Name.LocalName == "InfoBar.Content"));
            Check.True(!Control("RecordingBadge").Ancestors().Contains(Control("RecordingNotice")));
            Check.True(document.Descendants().Any(e => (string?)e.Attribute("Text") == "Catch me up"));
            Check.Equal("OnWhatDidIMissClick", (string?)Control("WhatDidIMissButton").Attribute("Click"));
            Check.Equal("Catch me up on this recording",
                (string?)Control("WhatDidIMissButton").Attribute("AutomationProperties.Name"));
            Check.True(!Control("WhatDidIMissButton").Ancestors().Any(e => e.Name.LocalName == "ScrollViewer"));
            Check.Equal("Search attendees or type a name", (string?)Control("SpeakerNameSearch").Attribute("PlaceholderText"));
            Check.Equal("OnSpeakerNameSearchTextChanged", (string?)Control("SpeakerNameSearch").Attribute("TextChanged"));
            Check.Equal("OnSpeakerNameApplyClick", (string?)Control("SpeakerNameApply").Attribute("Click"));
            Check.True(document.Descendants().Any(e => (string?)e.Attribute("Click") == "OnSpeakerNameCancelClick"));
            Check.Equal("Collapsed", (string?)Control("SpeakerNamingPanel").Attribute("Visibility"));
            Check.Equal("2", RowOf("SpeakerNamingPanel"));
            Check.Equal("False", (string?)Control("OpenQuestionsExpander").Attribute("IsExpanded"));
            Check.Equal("False", (string?)Control("ActionsExpander").Attribute("IsExpanded"));
            Check.Equal("320", (string?)Control("RecordingOverviewScroll").Attribute("MaxHeight"));
            var transcriptRow = Control("RecordingTranscriptRow");
            Check.Equal("*", (string?)transcriptRow.Attribute("Height"));
            Check.True(transcriptRow.Attribute("MinHeight") is null);
            var transcriptSurface = Control("TranscriptList").Ancestors()
                .First(e => e.Name.LocalName == "Border");
            Check.Equal("{ThemeResource AccentFillColorDefaultBrush}",
                (string?)transcriptSurface.Attribute("BorderBrush"));
            Check.Equal("1", (string?)transcriptSurface.Attribute("BorderThickness"));
            Check.True(!Control("RecordingWorkIqCancelButton").Ancestors().Any(e => e.Name.LocalName == "Expander"));
            Check.Equal("0", RowOf("RecordingWorkIqCancelButton"));
            Check.True(!document.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "RecordingDetailsExpander"));
            Check.True(!document.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "RecordingWorkIqStateText"));
            foreach (var name in new[] { "HealthText", "WorkIqStateText", "WorkIqSignOutButton" })
                Check.True(Control(name).Ancestors().Contains(Control("PreferencesPanel")));
            Check.True(Control("RecordingSettingsButton").Ancestors().Any(e => e.Name.LocalName == "Border"));
            Check.True(Control("PreferencesStopButton").Ancestors().Contains(Control("PreferencesPanel")));
            Check.True(!Control("PreferencesStopButton").Ancestors().Any(e => e.Name.LocalName == "ScrollViewer"));
            Check.Equal("OnStopClick", (string?)Control("PreferencesStopButton").Attribute("Click"));
            Check.Equal("PersonPicture", Control("TranscriptSpeakerAvatar").Name.LocalName);
            Check.Equal("28", (string?)Control("TranscriptSpeakerAvatar").Attribute("Width"));
            Check.Equal("28", (string?)Control("TranscriptSpeakerAvatar").Attribute("Height"));
            Check.Equal("{Binding AvatarInitials}",
                (string?)Control("TranscriptSpeakerAvatar").Attribute("Initials"));
            Check.Equal("{Binding AvatarImage}",
                (string?)Control("TranscriptSpeakerAvatar").Attribute("ProfilePicture"));
            Check.Equal("{Binding AvatarAccessibleName}",
                (string?)Control("TranscriptSpeakerAvatar").Attribute("AutomationProperties.Name"));
            Check.True(Control("TranscriptSpeakerAvatar").Ancestors().Contains(Control("TranscriptList")));
            Check.True(Control("TranscriptSpeakerAvatar").Attribute("DisplayName") is null);
            Check.True(Control("TranscriptSpeakerButton").Parent == Control("TranscriptTimestampText").Parent);
            Check.Equal("0", (string?)Control("TranscriptSpeakerButton").Attribute("MinWidth"));
            Check.Equal("{Binding Timestamp}", (string?)Control("TranscriptTimestampText").Attribute("Text"));
            Check.Equal("1", (string?)Control("TranscriptBodyText").Attribute("Grid.Row"));
            Check.Equal("{Binding Text}", (string?)Control("TranscriptBodyText").Attribute("Text"));
            Check.True(!Control("TranscriptBodyText").Elements().Any());
            Check.True(mainSource.Contains("Speaker = display,", StringComparison.Ordinal));
            Check.True(mainSource.Contains("Timestamp = ts,", StringComparison.Ordinal));
            Check.True(mainSource.Contains(
                "WhatDidIMissButton.Visibility = state.ShowCatchUp ? Visibility.Visible : Visibility.Collapsed;",
                StringComparison.Ordinal));
            Check.Equal("5", RowOf("SaveDecisionPanel"));
            Check.Equal("Collapsed", (string?)Control("SaveDecisionPanel").Attribute("Visibility"));
            Check.True(!Control("SaveDecisionPanel").Ancestors().Any(e => e.Name.LocalName == "ScrollViewer"));
            Check.Equal("OnDiscardRecordingClick", (string?)Control("DiscardRecordingButton").Attribute("Click"));
            Check.Equal("Discard recording and transcript",
                (string?)Control("DiscardRecordingButton").Attribute("AutomationProperties.Name"));
            Check.Equal("D", (string?)Control("DiscardRecordingButton").Attribute("AccessKey"));
            Check.Equal("OnSaveRecordingNowClick", (string?)Control("SaveRecordingNowButton").Attribute("Click"));
            Check.Equal("Save recording now",
                (string?)Control("SaveRecordingNowButton").Attribute("AutomationProperties.Name"));
            Check.Equal("S", (string?)Control("SaveRecordingNowButton").Attribute("AccessKey"));
            Check.Equal("0", (string?)Control("SaveCountdownFillTransform").Attribute("ScaleX"));
            Check.True(Control("SaveCountdownFill").Ancestors().Contains(Control("SaveRecordingNowButton")));
            Check.True(Control("SaveCountdownButtonLabel").Ancestors().Contains(Control("SaveRecordingNowButton")));
            Check.True(Control("SaveRecordingNowButton").DescendantsAndSelf()
                .Attributes().All(a => a.Name.LocalName != "AutomationProperties.LiveSetting"));
            foreach (var name in new[] { "TranscriptNavigation", "SummaryRetryButton", "WhatDidIMissButton" })
                Check.Equal("Collapsed", (string?)Control(name).Attribute("Visibility"));
            foreach (var removed in new[] { "CalendarSource", "SignInButton", "AdvancedGraphSection" })
                Check.True(!document.Descendants().Any(e => (string?)e.Attribute(x + "Name") == removed));
        }));
        yield return ("home view excludes diagnostic controls and keeps complete calendar groups accessible", () => Run(() =>
        {
            var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Control(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            var preferences = Control("PreferencesPanel");
            foreach (var name in new[] { "PreviewButton", "PreviewHealthText", "WorkIqStateText", "WorkIqSignOutButton" })
                Check.True(Control(name).Ancestors().Contains(preferences));
            foreach (var name in new[] { "TodaysMeetingsList", "OtherMeetingsList", "PastMeetingsList" })
                Check.True(Control(name).Ancestors().Contains(Control("IdlePanel")));
            Check.True(Control("TodaysMeetingsList").Ancestors().Contains(Control("CalendarScroll")));
            Check.True(!Control("TodaysMeetingsList").Ancestors().Any(e => e.Name.LocalName == "Expander"));
            Check.Equal("False", (string?)Control("PastMeetingsExpander").Attribute("IsExpanded"));
            Check.True(Control("OtherMeetingsList").Ancestors().Contains(Control("OtherMeetingsExpander")));
            Check.True(Control("OtherMeetingsExpander").Ancestors().Contains(Control("CalendarScroll")));
            Check.True(!Control("BackToMeetingsButton").Ancestors().Any(e => e.Name.LocalName == "ScrollViewer"));
            Check.True(!document.Descendants().Attributes().Any(a => a.Value.Contains("up to 5")));
        }));
        yield return ("recording view does not rebuild the hidden idle calendar", () => Run(() =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            Check.True(source.Contains(
                "if (IdlePanel.Visibility == Visibility.Visible &&\n" +
                "                _calendarMeetings.Count > 0 &&\n" +
                "                DateTimeOffset.Now - _calendarRenderedAt >= TimeSpan.FromMinutes(1))",
                StringComparison.Ordinal));
        }));
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    private static void SaveReviewCountdown()
    {
        var now = new DateTimeOffset(2026, 9, 23, 17, 51, 0, TimeSpan.Zero);
        var deadline = now.AddSeconds(5);

        var initial = SaveReviewPresentation.Create(true, deadline, now, false);
        Check.True(initial.IsVisible && initial.CanDecide);
        Check.Equal(5, initial.RemainingSeconds);
        Check.Equal(0d, initial.Progress);
        Check.Equal("Save now (5s)", initial.ButtonText);
        Check.Equal("Save recording now. Automatic save in 5 seconds.", initial.AccessibleName);

        var rounded = SaveReviewPresentation.Create(true, deadline, now.AddMilliseconds(250), false);
        Check.Equal(5, rounded.RemainingSeconds);
        Check.Equal(0.05d, Math.Round(rounded.Progress, 2));
        var halfway = SaveReviewPresentation.Create(true, deadline, now.AddSeconds(2.5), false);
        Check.Equal(0.5d, halfway.Progress);
        Check.Equal("Save now (3s)", halfway.ButtonText);
        var finalSecond = SaveReviewPresentation.Create(true, deadline, now.AddSeconds(4.2), false);
        Check.Equal(1, finalSecond.RemainingSeconds);
        Check.Equal(0.84d, Math.Round(finalSecond.Progress, 2));
        Check.Equal("Save now (1s)", finalSecond.ButtonText);

        var expired = SaveReviewPresentation.Create(true, deadline, deadline, false);
        Check.True(expired.IsVisible && !expired.CanDecide);
        Check.Equal(0, expired.RemainingSeconds);
        Check.Equal(1d, expired.Progress);
        Check.Equal("Saving...", expired.ButtonText);

        var submitted = SaveReviewPresentation.Create(true, deadline, now, true);
        Check.True(submitted.IsVisible && !submitted.CanDecide);
        var hidden = SaveReviewPresentation.Create(false, deadline, now, false);
        Check.True(!hidden.IsVisible && !hidden.CanDecide);
    }

    private static void PinBehavior()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var pin = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PinButton");
        Check.Equal("OnPinClick", (string?)pin.Attribute("Click"));
        Check.Equal("Pin panel above other windows", (string?)pin.Attribute("AutomationProperties.Name"));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains("_presenter.IsAlwaysOnTop = requested;", StringComparison.Ordinal));
        Check.True(source.Contains("_isPinned = _presenter.IsAlwaysOnTop;", StringComparison.Ordinal));
        Check.True(source.Contains("PinButton.IsChecked = _isPinned;", StringComparison.Ordinal));
        Check.True(source.Contains(
            "&& !_isPinned\n            && !_pipeline.CanStop)\n            AppWindow.Hide();",
            StringComparison.Ordinal));
        Check.True(source.Contains("args.Cancel = true;\n        AppWindow.Hide();",
            StringComparison.Ordinal));
        Check.True(source.Contains("_presenter.IsResizable = true;", StringComparison.Ordinal));
        Check.True(source.Contains("_presenter.SetBorderAndTitleBar(true, false);", StringComparison.Ordinal));
    }

    private static void DraggableTitleBar()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var titleBar = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "AppTitleBar");
        Check.Equal("Drag to move the Meeting Capture window",
            (string?)titleBar.Attribute("ToolTipService.ToolTip"));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains("SetTitleBar(AppTitleBar);", StringComparison.Ordinal));
        Check.True(source.Contains(
            "SetRegionRects(NonClientRegionKind.Passthrough, rects);",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "if (_titleBarInteractiveRects.AsSpan().SequenceEqual(rects))\n            return;",
            StringComparison.Ordinal));
        Check.True(source.Contains("TitleBarActions.LayoutUpdated += OnTitleBarActionsLayoutUpdated;",
            StringComparison.Ordinal));
        foreach (var control in new[]
        {
            "ContextButton",
            "OpenVisualizationButton",
            "TopicMapToggleButton",
            "RecordingSettingsButton",
            "PinButton",
        })
        {
            Check.True(source.Contains($"            {control},", StringComparison.Ordinal));
        }
    }

    private static void SpeakerAvatarFallback()
    {
        var unknown = SpeakerAvatarPresentation.Create("Guest-2", isResolved: false);
        Check.Equal("?", unknown.Initials);
        Check.Equal("Unknown speaker", unknown.AccessibleName);

        var named = SpeakerAvatarPresentation.Create("Ahmed Sabbour", isResolved: true);
        Check.Equal("AS", named.Initials);
        Check.Equal("Ahmed Sabbour profile picture", named.AccessibleName);

        var direct = SpeakerAvatarPresentation.Create("Cher", isResolved: true);
        Check.Equal("C", direct.Initials);
        var unicode = SpeakerAvatarPresentation.Create("Élodie 王", isResolved: true);
        Check.Equal("É王", unicode.Initials);

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains("var photo = await Task.Run(", StringComparison.Ordinal));
        Check.True(source.Contains("? _workIq.GetCurrentUserPhotoAsync(token)", StringComparison.Ordinal));
        Check.True(source.Contains(": _workIq.GetProfilePhotoAsync(person.Email, token)", StringComparison.Ordinal));
        Check.True(source.Contains("_speakerAvatarRevisions.GetValueOrDefault(speakerId) != revision",
            StringComparison.Ordinal));
        Check.True(source.Contains("SpeakerNameOption? SelectedSpeakerOption", StringComparison.Ordinal));
        Check.True(source.Contains("DecodePixelWidth = 96", StringComparison.Ordinal));
        Check.True(source.IndexOf("_speakerAvatarRevisions.GetValueOrDefault(speakerId) != revision",
                StringComparison.Ordinal) <
            source.IndexOf("var image = await CreateProfileImageAsync(photo);", StringComparison.Ordinal));
    }

    private static void TrayMenuWidth()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains("new Style(typeof(MenuFlyoutPresenter))", StringComparison.Ordinal));
        Check.True(source.Contains(
            "new Setter(FrameworkElement.MinWidthProperty, 280d)",
            StringComparison.Ordinal));
        Check.True(source.Contains("MenuFlyoutPresenterStyle = presenterStyle", StringComparison.Ordinal));
        Check.True(source.Contains("ContextMenuMode = ContextMenuMode.SecondWindow", StringComparison.Ordinal));
    }

    private static void ContextFlyout()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Control(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);

        Check.True(Control("ContextButton").Ancestors().Any(e =>
            e.Name.LocalName == "Border" && (string?)e.Attribute("Grid.Row") == "0"));
        Check.True(!Control("ContextButton").Ancestors().Any(e => (string?)e.Attribute(x + "Name") == "RecordingPanel"));
        Check.Equal("400", (string?)Control("ContextFlyout").Descendants()
            .First(e => e.Name.LocalName == "Grid").Attribute("Width"));
        Check.Equal("520", (string?)Control("ContextFlyout").Descendants()
            .First(e => e.Name.LocalName == "Grid").Attribute("MaxHeight"));
        Check.Equal("OnRelatedContextRefreshClick",
            (string?)Control("RelatedContextRefreshButton").Attribute("Click"));
        Check.Equal("Refresh suggestions",
            (string?)Control("RelatedContextRefreshButton").Attribute("Content"));
        Check.Equal("OnRosterRefreshClick",
            (string?)Control("RosterRefreshButton").Attribute("Click"));
        Check.Equal("Refresh roster",
            (string?)Control("RosterRefreshButton").Attribute("Content"));
        Check.True(!document.Descendants().Any(element =>
            (string?)element.Attribute(x + "Name") == "ContextRefreshButton"));
        Check.Equal("OnOpenContextSourceClick",
            Control("ContextFlyout").Descendants().Attributes("Click")
                .Single(a => a.Value == "OnOpenContextSourceClick").Value);
        Check.True(Control("RelatedContextList").Ancestors().Contains(Control("ContextFlyout")));
        Check.True(Control("LiveParticipantsList").Ancestors().Contains(Control("ContextFlyout")));
        Check.True(Control("ScheduledInviteesList").Ancestors().Contains(Control("ContextFlyout")));
        Check.True(!Control("LiveParticipantsList").Ancestors().Contains(Control("TranscriptList")));

        // The flyout keeps a one-line AI summary and defers all telemetry to Settings.
        Check.True(!document.Descendants().Any(element =>
            (string?)element.Attribute(x + "Name") is "AiUsageCapabilityList" or "AiUsageEmptyText"));
        Check.True(Control("AiUsageStatusText").Ancestors().Contains(Control("ContextFlyout")));
        Check.Equal("OnOpenAiDiagnosticsClick",
            (string?)Control("AiDiagnosticsLink").Attribute("Click"));
        Check.True(!Control("ContextFlyout").Descendants().Any(element =>
            (string?)element.Attribute("Text") is "{Binding Quota}" or "{Binding TotalNanoAiu}" or
                "{Binding InputTokens}" or "{Binding ProviderCost}" or "{Binding CalculatedCost}"));

        // Participants are a bounded chip preview; neither roster nor invitees are dumped.
        foreach (var list in new[] { "LiveParticipantsList", "ScheduledInviteesList" })
        {
            Check.Equal("{StaticResource ParticipantChipTemplate}",
                (string?)Control(list).Attribute("ItemTemplate"));
            Check.Equal("{StaticResource ParticipantChipPanel}",
                (string?)Control(list).Attribute("ItemsPanel"));
        }
        Check.Equal("Collapsed", (string?)Control("ScheduledInviteesExpander").Attribute("Visibility"));
        Check.Equal("False", (string?)Control("ScheduledInviteesExpander").Attribute("IsExpanded"));
        Check.True(Control("ScheduledInviteesAllText").Ancestors()
            .Contains(Control("ScheduledInviteesExpander")));

        Check.True(document.Descendants().Any(element =>
            (string?)element.Attribute("Text") ==
            "Suggestions from WorkIQ are not verified meeting facts."));
        Check.Equal("Polite",
            (string?)Control("RelatedContextStatusText").Attribute("AutomationProperties.LiveSetting"));
        Check.Equal("Polite",
            (string?)Control("RosterStatusText").Attribute("AutomationProperties.LiveSetting"));
        Check.Equal("36", (string?)Control("RelatedContextList").Descendants()
            .Single(element => element.Name.LocalName == "Button").Attribute("Width"));
        Check.Equal("{Binding ExternalActionName}",
            (string?)Control("RelatedContextList").Descendants()
                .Single(element => element.Name.LocalName == "Button")
                .Attribute("AutomationProperties.Name"));
        Check.Equal("Open externally",
            (string?)Control("RelatedContextList").Descendants()
                .Single(element => element.Name.LocalName == "Button")
                .Attribute("ToolTipService.ToolTip"));
        Check.Equal("2", (string?)Control("RelatedContextTopicText").Attribute("MaxLines"));

        var viewModels = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "FlyoutViewModels.cs.txt"));
        Check.True(!viewModels.Contains("\"Historical fact\"", StringComparison.Ordinal));
        Check.True(!viewModels.Contains("\"Possible connection\"", StringComparison.Ordinal));
        Check.True(viewModels.Contains("\"Date unavailable\"", StringComparison.Ordinal));
        Check.True(viewModels.Contains("ExternalActionName", StringComparison.Ordinal));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains("uri.Scheme != Uri.UriSchemeHttps", StringComparison.Ordinal));
        Check.True(source.Contains("!string.IsNullOrEmpty(uri.UserInfo)", StringComparison.Ordinal));
        Check.True(source.Contains(
            "normalizedTopic.Length is > 0 and <= 240",
            StringComparison.Ordinal));
        Check.True(source
            .Replace("_relatedContext?.Invalidate();", "_relatedContext.Invalidate();", StringComparison.Ordinal)
            .Split("_relatedContext.Invalidate();", StringSplitOptions.None).Length >= 4);
        Check.True(Regex.IsMatch(source, @"snapshot\.Segments\.Count,\n\s+ct\);"));
        Check.True(
            source.Contains(
                "snapshot?.Availability == ActiveRosterAvailability.VerifiedLive",
                StringComparison.Ordinal) ||
            source.Contains(
                "snapshot.Availability == ActiveRosterAvailability.VerifiedLive",
                StringComparison.Ordinal));
        Check.True(source.Contains("if (!ReferenceEquals(status, _relatedContext.Status) ||",
            StringComparison.Ordinal));
        Check.True(source.Contains("status.SourceRevision != _workIq.Revision",
            StringComparison.Ordinal));
        Check.True(source.Contains("ReleaseRosterRefreshAsync(refresh)", StringComparison.Ordinal));
        Check.True(source.Contains("private async void OnRelatedContextRefreshClick",
            StringComparison.Ordinal));
        Check.True(source.Contains("private async void OnRosterRefreshClick",
            StringComparison.Ordinal));
        Check.True(source.Contains("UpdateRelatedContextRefreshButton()", StringComparison.Ordinal));
        Check.True(source.Contains("UpdateRosterRefreshButton()", StringComparison.Ordinal));
        Check.True(source.Contains("_rosterCheckedAt = DateTimeOffset.Now;", StringComparison.Ordinal));
        Check.True(source.Contains(
            "RosterStatusText.Text = \"Checking the joined call for a verified roster...\";",
            StringComparison.Ordinal));
        Check.True(!source.Contains("SpeakerResolver.Resolve(id, member", StringComparison.Ordinal));
    }


    private static void ParticipantsPreview()
    {
        var joined = new[] { "Ada Lovelace", "Grace Hopper", "Alan Turing", "Ken Thompson",
            "Barbara Liskov", "Margaret Hamilton", "Donald Knuth" };
        var preview = ParticipantPresentation.Preview(joined, isVerified: true);
        Check.Equal(5, preview.Chips.Count);
        Check.Equal("+2", preview.Overflow);
        Check.Equal("AL", preview.Chips[0].Initials);
        Check.True(preview.Chips[0].IsVerified);
        Check.Equal("Ada Lovelace, verified joined participant", preview.Chips[0].AccessibleName);

        var invitees = ParticipantPresentation.Preview(joined, isVerified: false);
        Check.True(invitees.Chips.All(chip => !chip.IsVerified));
        Check.Equal("Ada Lovelace, scheduled invitee", invitees.Chips[0].AccessibleName);
        Check.Equal("7 scheduled invitees \u00b7 not verified as joined.",
            ParticipantPresentation.InviteeSummary(joined));
        Check.Equal("No scheduled invitees are available.",
            ParticipantPresentation.InviteeSummary([]));
        Check.Equal(string.Empty, ParticipantPresentation.Preview(["Solo Person"], true).Overflow);
        Check.Equal("SP", ParticipantPresentation.Preview(["Solo Person"], true).Chips[0].Initials);
        // The full invitee list exists only behind the explicit expansion.
        Check.True(ParticipantPresentation.InviteeList(joined).Contains("Donald Knuth", StringComparison.Ordinal));
    }

    private static void RosterRefreshFeedback()
    {
        var observed = new DateTimeOffset(2026, 9, 24, 11, 16, 5, TimeSpan.Zero);
        var checkedAt = new DateTimeOffset(2026, 9, 24, 11, 16, 42, TimeSpan.Zero);
        var later = checkedAt.AddSeconds(31);

        var unavailable = new ActiveRosterSnapshot(
            ActiveRosterAvailability.Unavailable, ActiveRosterSource.TeamsUiAutomation, observed,
            [], null, true,
            "A joined call is visible. Open the Teams Participants pane to make its displayed roster available.");
        var first = ParticipantPresentation.Roster(unavailable, 0, checkedAt);
        var second = ParticipantPresentation.Roster(unavailable, 0, later);
        Check.Equal("No joined meeting roster is available.", first.Status);
        Check.True(first.Freshness.Contains("last checked", StringComparison.OrdinalIgnoreCase));
        // An unchanged outcome must still visibly move after a real re-probe.
        Check.True(first.Freshness != second.Freshness);
        Check.True(first.Guidance.EndsWith("Then choose Refresh roster.", StringComparison.Ordinal));

        var verified = new ActiveRosterSnapshot(
            ActiveRosterAvailability.VerifiedLive, ActiveRosterSource.TeamsUiAutomation, observed,
            [new("Ada Lovelace"), new("Grace Hopper")], null, true, "Displayed rows only.");
        var live = ParticipantPresentation.Roster(verified, 2, checkedAt);
        Check.True(live.Status.Contains("Verified joined", StringComparison.Ordinal));
        Check.True(live.Status.Contains("2 displayed participant rows", StringComparison.Ordinal));
        Check.True(live.Freshness.StartsWith("Teams accessibility", StringComparison.Ordinal));
        Check.True(live.Freshness.Contains("partial coverage", StringComparison.Ordinal));

        var indeterminate = ParticipantPresentation.Roster(
            verified with { Availability = ActiveRosterAvailability.Indeterminate }, 0, checkedAt);
        Check.Equal("Live participants could not be verified.", indeterminate.Status);
        Check.Equal("Live roster has not been checked.",
            ParticipantPresentation.Roster(null, 0, null).Status);
    }

    private static void CatchMeUpSurface()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Control(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);

        // Catch me up has its own surface inside the recording panel, above Stop, not in Current topic.
        var panel = Control("CatchUpPanel");
        Check.Equal("RecordingPanel", (string?)panel.Parent!.Attribute(x + "Name"));
        Check.Equal("0", (string?)panel.Attribute("Grid.Row"));
        Check.Equal("5", (string?)panel.Attribute("Grid.RowSpan"));
        Check.Equal("Collapsed", (string?)panel.Attribute("Visibility"));
        Check.True(!Control("RecordingFooter").Ancestors().Contains(panel));
        Check.Equal("Polite", (string?)Control("CatchUpStatusText").Attribute("AutomationProperties.LiveSetting"));
        Check.Equal("OnCatchUpCloseClick", (string?)Control("CatchUpCloseButton").Attribute("Click"));
        Check.Equal("OnWhatDidIMissClick", (string?)Control("CatchUpRetryButton").Attribute("Click"));
        Check.True(Control("CatchUpSummaryText").Ancestors().Contains(Control("CatchUpScroll")));
        // One scroller only: the result never nests scrolling regions.
        Check.True(!Control("CatchUpScroll").Descendants().Any(e => e.Name.LocalName == "ScrollViewer"));
        Check.True(Control("TldrChip").Ancestors().All(e => (string?)e.Attribute(x + "Name") != "CatchUpPanel"));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains("var renderedSummary = catchUp ? ProjectVisualizationText(summary) : summary;",
            StringComparison.Ordinal));
        Check.True(source.Contains("RenderCatchUp(renderedSummary);", StringComparison.Ordinal));
        Check.True(!source.Contains("ShowAutoInfoBar(\"Catch me up\", renderedSummary", StringComparison.Ordinal));
        Check.True(source.Contains("if (!_catchUpRendered)", StringComparison.Ordinal));

        // The composed result carries bounded prior context from state the session already holds.
        var now = DateTimeOffset.UtcNow;
        var visualization = new MeetingVisualizationDocument(
            1, "session", "Meeting", now, null, 1, false,
            [], [],
            [], 
            [
                new("occ-1", "topic-1", 1, 1, "Model routing"),
                new("occ-2", "topic-2", 1, 2, "Pivot path adoption"),
                new("occ-3", "topic-2", 1, 3, "Pivot path adoption")
            ],
            [
                new("q-1", "topic-1", "Which model handles long context?", QuestionState.Open, []),
                new("q-2", "topic-1", "Answered already", QuestionState.Answered, [])
            ],
            [], [])
        {
            Decisions = [new("d-1", "topic-1", "Adopt the auto model router", [])],
            Actions = [new("a-1", "topic-1", "Send the routing guidance", [])],
        };
        var sections = CatchUpPresentation.Sections(
            visualization,
            "Pivot path adoption",
            ["Which model handles long context?", "Who owns the rollout?"],
            ["Send the routing guidance", "Book the follow-up"]);
        Check.Equal("Earlier points|Decisions|Unresolved questions|Actions",
            string.Join("|", sections.Select(section => section.Heading)));
        Check.Equal("Model routing", string.Join("|", sections[0].Items));
        Check.Equal("Which model handles long context?|Who owns the rollout?",
            string.Join("|", sections[2].Items));
        Check.Equal("Send the routing guidance|Book the follow-up",
            string.Join("|", sections[3].Items));
        Check.True(!sections[2].Items.Contains("Answered already"));
        Check.Equal(0, CatchUpPresentation.Sections(null, null, [], []).Count);
        var bounded = CatchUpPresentation.Sections(
            null, null, Enumerable.Range(1, 12).Select(index => $"Question {index}"), []);
        Check.Equal(CatchUpPresentation.SectionItemLimit, bounded.Single().Items.Count);

        // A multi-sentence recap survives parsing; the one-line topic bound would have rejected it.
        var recap = string.Join(" ", Enumerable.Repeat(
            "The group is comparing routing costs against latency budgets.", 6));
        var payload = "{\"topic\": \"" + recap + "\"}";
        Check.Equal(recap, IntelligenceResponseParser.ParseCatchUp(payload, "copilot", "gpt-5"));
        Check.True(recap.Length > MeetingVisualizationSession.TopicLabelCharacterLimit);
        Check.Throws<IntelligenceException>(
            () => IntelligenceResponseParser.ParseTopic(payload, "copilot", "gpt-5"));
    }

    private static void LongVisualizationTopic()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var triggerStart = source.IndexOf("private void TriggerRelatedContext(", StringComparison.Ordinal);
        var triggerEnd = source.IndexOf(
            "private async Task RefreshVisualizationAnalysisAsync",
            triggerStart,
            StringComparison.Ordinal);
        var trigger = source[triggerStart..triggerEnd];

        var accept = trigger.IndexOf("_visualization.AcceptTopic(", StringComparison.Ordinal);
        var analyze = trigger.IndexOf("_ = RefreshVisualizationAnalysisAsync(", StringComparison.Ordinal);
        var contextGate = trigger.IndexOf(
            "if (!IsRelatedContextTopicEligible(normalizedTopic))",
            StringComparison.Ordinal);
        var contextRefresh = trigger.IndexOf("_ = RefreshRelatedContextAsync(", StringComparison.Ordinal);
        Check.True(accept >= 0 && analyze > accept);
        Check.True(contextGate > analyze && contextRefresh > contextGate);
        Check.True(trigger[..accept].IndexOf("> 240", StringComparison.Ordinal) < 0);
        Check.True(source.Contains(
            "normalizedTopic.Length is > 0 and <= 240",
            StringComparison.Ordinal));

        var manualRefreshStart = source.IndexOf(
            "private async void OnRelatedContextRefreshClick",
            StringComparison.Ordinal);
        var manualRefreshEnd = source.IndexOf(
            "private void RenderRelatedContext",
            manualRefreshStart,
            StringComparison.Ordinal);
        Check.True(source[manualRefreshStart..manualRefreshEnd].Contains(
            "IsRelatedContextTopicEligible(normalizedTopic)",
            StringComparison.Ordinal));
        Check.True(source[manualRefreshStart..manualRefreshEnd].Contains(
            "_loadedVisualization is not null",
            StringComparison.Ordinal));
        Check.True(source[manualRefreshStart..manualRefreshEnd].Contains(
            "_selectedVisualizationTopicId",
            StringComparison.Ordinal));
        Check.True(source[manualRefreshStart..manualRefreshEnd].Contains(
            "TopicId: topicId",
            StringComparison.Ordinal));

        var authStart = source.IndexOf(
            "private void OnWorkIqAuthStateChanged",
            StringComparison.Ordinal);
        var authEnd = source.IndexOf(
            "private async Task RestoreWorkIqAsync",
            authStart,
            StringComparison.Ordinal);
        var authHandler = source[authStart..authEnd];
        Check.True(!authHandler.Contains("_acceptedRelatedContextTopic = null", StringComparison.Ordinal));
        Check.True(authHandler.Contains("ForceRefresh: auth.IsAuthenticated", StringComparison.Ordinal));
        Check.True(authHandler.Contains("_loadedVisualization is null", StringComparison.Ordinal));
        Check.True(authHandler.Contains(
            "var currentTopicId = ResolveLiveCanonicalTopicId(_currentVisualizationTopicId);",
            StringComparison.Ordinal));
        Check.True(authHandler.Contains("TopicId: currentTopicId", StringComparison.Ordinal));
        Check.True(authHandler.Contains("RefreshRelatedContextAsync(", StringComparison.Ordinal));
    }

    private static void RelatedContextTopicLabel()
    {
        var reworded = RelatedContextPresentation.Topic(
            "Release readiness",
            "Customer follow-up");
        Check.True(reworded.IsEarlierTopic);
        Check.Equal("Earlier topic · Release readiness", reworded.Label);
        Check.True(reworded.Disclosure.Contains("prior topic", StringComparison.Ordinal));
        Check.True(reworded.Disclosure.Contains("stale", StringComparison.Ordinal));

        var same = RelatedContextPresentation.Topic(
            "  Release   readiness ",
            "release readiness");
        Check.True(!same.IsEarlierTopic);
        Check.Equal("Topic · Release readiness", same.Label);

        var empty = RelatedContextPresentation.Topic(null, "Current");
        Check.Equal(string.Empty, empty.Label);

        var waiting = RelatedContextPresentation.Status(
            null, null, topicEligible: false, workIqAuthenticated: false, itemCount: 0);
        Check.Equal("Waiting for a discussion topic.", waiting.Text);
        var signedOut = RelatedContextPresentation.Status(
            null, "Release readiness", topicEligible: true, workIqAuthenticated: false, itemCount: 0);
        Check.True(signedOut.Text.Contains("signed out", StringComparison.Ordinal));
        var querying = RelatedContextPresentation.Status(
            new(RelatedContextState.Refreshing, "session", "Release readiness", 1, 1, null, null),
            "Release readiness", topicEligible: true, workIqAuthenticated: true, itemCount: 0);
        Check.True(querying.IsQuerying && querying.Text.Contains("Querying WorkIQ", StringComparison.Ordinal));
        var cached = RelatedContextPresentation.Status(
            new(RelatedContextState.Cached, "session", "Release readiness", 2, 1,
                new(DateTimeOffset.UtcNow, [], RelatedContextAvailability.Available), null),
            "Release readiness", topicEligible: true, workIqAuthenticated: true, itemCount: 2);
        Check.True(!cached.IsQuerying && cached.Text.Contains("cached", StringComparison.Ordinal));
        var available = RelatedContextPresentation.Status(
            new(RelatedContextState.Available, "session", "Release readiness", 2, 1,
                new(DateTimeOffset.UtcNow, [], RelatedContextAvailability.Available), null),
            "Release readiness", topicEligible: true, workIqAuthenticated: true, itemCount: 2);
        Check.True(available.Text.Contains("related source suggestions", StringComparison.Ordinal));
        Check.True(!available.Text.Contains("grounded", StringComparison.OrdinalIgnoreCase));
        var emptyResult = RelatedContextPresentation.Status(
            new(RelatedContextState.NoEvidence, "session", "Release readiness", 2, 1,
                new(DateTimeOffset.UtcNow, [], RelatedContextAvailability.NoEvidence), null),
            "Release readiness", topicEligible: true, workIqAuthenticated: true, itemCount: 0);
        Check.True(emptyResult.Text.Contains("no related suggestions", StringComparison.Ordinal));
        var failed = RelatedContextPresentation.Status(
            new(RelatedContextState.Failed, "session", "Release readiness", 2, 1, null, null),
            "Release readiness", topicEligible: true, workIqAuthenticated: true, itemCount: 0);
        Check.True(failed.Text.Contains("could not be loaded", StringComparison.Ordinal));
        var loadingChip = RelatedContextPresentation.TopicStatus(RelatedContextState.Refreshing)!;
        Check.True(loadingChip.IsLoading);
        Check.Equal(TopicSemanticInfoState.Refreshing, loadingChip.State);
        Check.Equal("Refreshing related workplace context", loadingChip.Text);
        var noEvidenceChip = RelatedContextPresentation.TopicStatus(RelatedContextState.NoEvidence)!;
        Check.True(!noEvidenceChip.IsLoading);
        Check.Equal("No verified related context", noEvidenceChip.Text);
        Check.Equal(
            TopicSemanticInfoState.Available,
            RelatedContextPresentation.TopicStatus(RelatedContextState.Available).State);
        Check.Equal("Team sync", ContextTitlePresentation.Clean("<Event>Team sync</Event>"));
        Check.Equal("Budget review", ContextTitlePresentation.Clean("<Email>Budget review</Email>"));
        Check.Equal("Project plan", ContextTitlePresentation.Clean("<File> Project plan </File>"));
        Check.Equal("Untouched <Event>text</Event> suffix",
            ContextTitlePresentation.Clean("Untouched <Event>text</Event> suffix"));
        Check.Equal(string.Empty, ContextTitlePresentation.UniqueDetail(
            "WorkIQ retrieval identified this event as relevant to the discussion topic.",
            "Team sync"));
        Check.Equal(string.Empty, ContextTitlePresentation.UniqueDetail(
            "Team sync",
            "<Event>Team sync</Event>"));
        Check.Equal(string.Empty, ContextTitlePresentation.UniqueDetail(
            "<Email>Budget review</Email>",
            "Budget review"));
        Check.Equal("Agenda changed to Thursday.", ContextTitlePresentation.UniqueDetail(
            "<Event>Agenda changed to Thursday.</Event>",
            "Team sync"));
        Check.Equal("Agenda changed to Thursday.", ContextTitlePresentation.UniqueDetail(
            "Agenda changed to Thursday.",
            "<Event>Team sync</Event>"));
    }

    private static void TopicTimeline()
    {
        var document = new MeetingVisualizationDocument(
            1, "session", "Review", DateTimeOffset.Parse("2026-09-23T18:00:00-07:00"),
            null, 2, false,
            [new("seg-000001", 1, "Release readiness supports customer follow-up.",
                DateTimeOffset.Parse("2026-09-23T18:00:10-07:00"), "Guest-1", 0.98)],
            [new("Guest-1", "Avery", true)],
            [
                new("topic-0001", "Guest-1 release readiness", ["Guest-1 release readiness"], 1, 1),
                new("topic-0002", "Customer follow-up", ["Customer follow-up"], 2, 2),
            ],
            [
                new("occ-000001", "topic-0001", 1, 1, "Guest-1 release readiness"),
                new("occ-000002", "topic-0002", 2, 2, "Customer follow-up"),
            ],
            [new("question-1", "topic-0001", "Is Guest-1 ready?", QuestionState.Open, ["evidence-link"])],
            [
                new("evidence-1", EvidenceOrigin.RelatedContext,
                    VisualizationProvenance.RelatedSourceSuggestion,
                    null, "topic-0001", "Review notes", "https://example.test/review",
                    ContextSourceType.Document,
                    DateTimeOffset.Parse("2026-09-22T09:30:00-07:00"),
                    "The deployment boundary was recorded.", null,
                    "Matches the selected release-readiness topic.",
                    "Verified document author.",
                    InvolvementRole: ContextInvolvementRole.TeamsAuthor,
                    InvolvementMatchKind: ContextIdentityMatchKind.ObjectId),
                new("evidence-link", EvidenceOrigin.Transcript, VisualizationProvenance.TranscriptFact,
                    1, "topic-0001", null, null, ContextSourceType.LocalTranscript, null,
                    "Release readiness supports customer follow-up.", "Customer follow-up link")
            ],
            [
                new("topic-link-1", "topic-0001", "topic-0002", ExplainedTopicLinkKind.Supports,
                    VisualizationProvenance.TranscriptFact, ["evidence-link"], "Not shown in the timeline"),
                new("topic-link-2", "topic-0002", "topic-0001", ExplainedTopicLinkKind.Contradicts,
                    VisualizationProvenance.Inference, ["evidence-link"], null),
                new("topic-link-3", "topic-0001", "topic-0002", ExplainedTopicLinkKind.DependsOn,
                    VisualizationProvenance.Inference, ["evidence-link"], null),
                new("topic-link-4", "topic-0002", "topic-0001", ExplainedTopicLinkKind.BuildsOn,
                    VisualizationProvenance.TranscriptFact, ["evidence-link"], null),
            ])
        {
            Actions =
            [
                new("action-1", "topic-0001", "Send the release checklist", ["evidence-link"]),
                new("action-ungrounded", "topic-0001", "Do not map this", []),
            ],
            Decisions =
            [
                new("decision-1", "topic-0001", "Proceed with the release", ["evidence-link"]),
                new("decision-ungrounded", "topic-0001", "Do not map this", []),
            ],
        };

        var projection = TopicMapState.Project(document, "topic-0001", "topic-0002");
        Check.Equal("topic-0001", projection.SelectedTopicId);
        Check.Equal("topic-0002", projection.CurrentTopicId);
        Check.Equal(2, projection.Trail.Count);
        Check.Equal("Avery release readiness", projection.Trail[0].Label);
        Check.True(projection.Trail[0].IsSelected && !projection.Trail[0].IsCurrent);
        Check.True(projection.Trail[1].IsCurrent && !projection.Trail[1].IsSelected);
        Check.Equal("topic-0001", projection.TopicBrief!.TopicId);
        Check.Equal("Avery release readiness", projection.TopicBrief.Label);
        Check.Equal("Earlier topic you selected", projection.TopicBrief.Status);
        Check.True(projection.TopicBrief.DiscussedAt.Contains("segment 1", StringComparison.Ordinal));
        Check.True(projection.Trail[0].HasNext && !projection.Trail[1].HasNext);
        Check.True(projection.Trail[1].AccessibleName.Contains("Current topic", StringComparison.Ordinal));
        Check.True(projection.Trail[0].AccessibleName.Contains("Selected", StringComparison.Ordinal));
        Check.Equal(
            "Depends on → Customer follow-up · Supports → Customer follow-up · +2 more relations",
            projection.Trail[0].RelationText);
        Check.Equal(
            "Builds on → Avery release readiness · Contradicts → Avery release readiness · +2 more relations",
            projection.Trail[1].RelationText);
        Check.True(!projection.Trail.Any(item =>
            item.AccessibleName.Contains("WorkIQ", StringComparison.Ordinal) ||
            item.AccessibleName.Contains("Review notes", StringComparison.Ordinal) ||
            item.AccessibleName.Contains("Not shown in the timeline", StringComparison.Ordinal) ||
            item.AccessibleName.Contains("Related topics:", StringComparison.Ordinal)));
        Check.Equal("evidence-1", projection.TopicBrief!.WorkIqSources.Single().Id);
        Check.Equal("topic-0001", projection.BrainMap!.TopicId);
        Check.Equal("Avery release readiness", projection.BrainMap.Label);
        Check.True(!projection.BrainMap.IsCurrent);
        Check.Equal("Customer follow-up", projection.BrainMap.CurrentTopicLabel);
        Check.Equal(9, projection.BrainMap.Branches.Count);
        Check.True(projection.BrainMap.Branches.Any(item =>
            item.Id == "evidence-link" &&
            item.Kind == "Transcript fact" &&
            item.Title == "Customer follow-up link"));
        Check.True(projection.BrainMap.Branches.Any(item =>
            item.Id == "question-1" && item.Kind == "Open question"));
        Check.True(projection.BrainMap.Branches.Count(item =>
            item.Kind == "Explained relationship") == 4);
        Check.True(projection.BrainMap.Branches.Any(item =>
            item.Id == "decision-1" && item.Kind == "Decision"));
        Check.True(projection.BrainMap.Branches.Any(item =>
            item.Id == "action-1" && item.Kind == "Open action"));
        Check.True(projection.BrainMap.Branches.Any(item =>
            item.Id == "evidence-1" &&
            item.Kind == "Sourced context" &&
            item.Detail.Contains("Suggestion—not meeting evidence", StringComparison.Ordinal)));
        Check.True(!projection.BrainMap.Branches.Any(item =>
            item.Id is "action-ungrounded" or "decision-ungrounded"));
        Check.Equal(projection.BrainMap.Branches.Count,
            projection.BrainMap.Branches.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());

        var loadingContext = TopicMapState.Project(
            document,
            "topic-0002",
            "topic-0002",
            new Dictionary<string, RelatedContextState>
            {
                ["topic-0002"] = RelatedContextState.Pending,
            });
        Check.Equal("Related workplace suggestions are ready to find",
            loadingContext.Trail[1].ContextStatus!.Text);
        Check.Equal(TopicSemanticInfoState.Loading, loadingContext.Trail[1].ContextStatus!.State);
        Check.True(!loadingContext.Trail[1].ContextStatus!.IsLoading);
        Check.Equal(
            loadingContext.Trail[1].ContextStatus,
            loadingContext.TopicBrief!.ContextStatus);
        Check.Equal(0, loadingContext.TopicBrief.WorkIqSources.Count);
        var refreshing = RelatedContextPresentation.TopicStatus(RelatedContextState.Refreshing);
        Check.Equal(TopicSemanticInfoState.Refreshing, refreshing.State);
        Check.True(refreshing.IsLoading);
        Check.Equal("Refreshing related workplace context", refreshing.Text);
        var refreshingWithRetainedData = TopicMapState.Project(
            document,
            "topic-0001",
            "topic-0002",
            new Dictionary<string, RelatedContextState>
            {
                ["topic-0001"] = RelatedContextState.Refreshing,
            });
        Check.Equal("Refreshing suggestions · previous results remain visible",
            refreshingWithRetainedData.TopicBrief!.ContextStatus.Text);
        Check.Equal(1, refreshingWithRetainedData.TopicBrief.WorkIqSources.Count);
        var failedWithRetainedData = TopicMapState.Project(
            document,
            "topic-0001",
            "topic-0002",
            new Dictionary<string, RelatedContextState>
            {
                ["topic-0001"] = RelatedContextState.Failed,
            });
        Check.Equal("Refresh failed · previous suggestions remain visible",
            failedWithRetainedData.TopicBrief!.ContextStatus.Text);
        Check.Equal(1, failedWithRetainedData.TopicBrief.WorkIqSources.Count);
        Check.Equal(
            TopicSemanticInfoState.Cached,
            RelatedContextPresentation.TopicStatus(RelatedContextState.Cached).State);
        Check.Equal(
            TopicSemanticInfoState.Available,
            RelatedContextPresentation.TopicStatus(RelatedContextState.Available).State);
        var noContext = TopicMapState.Project(
            document,
            "topic-0002",
            "topic-0002",
            new Dictionary<string, RelatedContextState>
            {
                ["topic-0002"] = RelatedContextState.NoEvidence,
            });
        Check.Equal("No verified related context", noContext.Trail[1].ContextStatus!.Text);
        Check.Equal(TopicSemanticInfoState.NoEvidence, noContext.TopicBrief!.ContextStatus.State);
        Check.True(!noContext.TopicBrief!.ContextStatus!.IsLoading);
        Check.Equal(
            TopicSemanticInfoState.Unavailable,
            RelatedContextPresentation.TopicStatus(RelatedContextState.Unavailable).State);
        Check.Equal(
            TopicSemanticInfoState.Failed,
            RelatedContextPresentation.TopicStatus(RelatedContextState.Failed).State);
        Check.Equal(
            "Related workplace context could not be loaded",
            RelatedContextPresentation.TopicStatus(RelatedContextState.Failed).Text);
        Check.Equal(
            TopicSemanticInfoState.Available,
            projection.TopicBrief!.ContextStatus.State);
        var signedOutContext = TopicMapState.Project(
            document with { Evidence = document.Evidence.Where(item => item.TopicId != "topic-0002").ToArray() },
            "topic-0002",
            "topic-0002",
            workIqAuthenticated: false);
        Check.Equal("WorkIQ is not signed in", signedOutContext.TopicBrief!.ContextStatus.Text);
        var savedWithoutContext = TopicMapState.Project(
            document with { Evidence = document.Evidence.Where(item => item.TopicId != "topic-0002").ToArray() },
            "topic-0002",
            "topic-0002",
            workIqAuthenticated: false,
            isReadOnly: true);
        Check.Equal(TopicSemanticInfoState.NoEvidence,
            savedWithoutContext.TopicBrief!.ContextStatus.State);
        Check.Equal("No verified related context in this saved meeting",
            savedWithoutContext.TopicBrief.ContextStatus.Text);

        var currentMoved = TopicMapState.Project(document, "topic-0001", "topic-0001");
        Check.True(projection.Trail.Select(item => item.Id)
            .SequenceEqual(currentMoved.Trail.Select(item => item.Id)));
        Check.True(currentMoved.Trail[0].IsCurrent && currentMoved.Trail[0].IsSelected);
        Check.Equal("Now · Current topic", currentMoved.TopicBrief!.Status);
        Check.True(!currentMoved.Trail[1].IsCurrent && !currentMoved.Trail[1].IsSelected);
        Check.True(!projection.TopicBrief!.IsCurrent);
        Check.Equal("evidence-link", projection.TopicBrief.Facts.Single().Id);
        Check.Equal("Customer follow-up link", projection.TopicBrief.Facts.Single().Title);
        Check.Equal("Release readiness supports customer follow-up.",
            projection.TopicBrief.Facts.Single().EvidenceText);
        Check.True(projection.TopicBrief.Facts.Single().Metadata.StartsWith(
            "Segment 1 · ", StringComparison.Ordinal));
        Check.Equal(4, projection.TopicBrief.Relationships.Count);
        Check.True(projection.TopicBrief.Relationships.All(item =>
            item.Detail.Contains("segment 1", StringComparison.Ordinal)));
        Check.True(projection.TopicBrief.Questions.Single() is { Id: "question-1", Answered: false });

        var repeatedCurrentDocument = document with
        {
            TopicClusters =
            [
                .. document.TopicClusters,
                new("topic-0003", "Deployment validation", ["Deployment validation"], 3, 3),
            ],
            TopicTrail =
            [
                .. document.TopicTrail,
                new("occ-000003", "topic-0003", 3, 3, "Deployment validation"),
            ],
        };
        foreach (var currentTopicId in new[] { "topic-0002", "topic-0003", "topic-0002" })
        {
            var pinned = TopicMapState.Project(
                repeatedCurrentDocument, "topic-0001", currentTopicId);
            Check.Equal("topic-0001", pinned.SelectedTopicId);
            Check.Equal("topic-0001", pinned.BrainMap!.TopicId);
            Check.Equal(currentTopicId, pinned.CurrentTopicId);
        }

        var explicitNodeSelection = TopicMapState.Project(
            repeatedCurrentDocument, "topic-0002", "topic-0003");
        Check.Equal("topic-0002", explicitNodeSelection.BrainMap!.TopicId);
        var explicitGoToCurrent = TopicMapState.Project(
            repeatedCurrentDocument, "topic-0003", "topic-0003");
        Check.True(explicitGoToCurrent.BrainMap is
            { TopicId: "topic-0003", IsCurrent: true, CurrentTopicLabel: "Deployment validation" });

        var removedSelection = TopicMapState.Project(repeatedCurrentDocument with
        {
            TopicClusters = repeatedCurrentDocument.TopicClusters
                .Where(item => item.Id != "topic-0001").ToArray(),
            TopicTrail = repeatedCurrentDocument.TopicTrail
                .Where(item => item.TopicId != "topic-0001").ToArray(),
            Questions = [],
            Actions = [],
            Decisions = [],
            Evidence = [],
            ExplainedTopicLinks = [],
        }, "topic-0001", "topic-0003");
        Check.Equal("topic-0003", removedSelection.SelectedTopicId);
        Check.Equal("topic-0003", removedSelection.BrainMap!.TopicId);

        var workIqSession = new MeetingVisualizationSession();
        workIqSession.Begin("workiq", "Review", DateTimeOffset.UtcNow);
        workIqSession.AppendOrUpdateSegment(1, new(
            "Release readiness was reviewed.", DateTimeOffset.UtcNow, SpeakerId: "Guest-1"));
        workIqSession.AppendOrUpdateSegment(2, new(
            "Customer follow-up was planned.", DateTimeOffset.UtcNow, SpeakerId: "Guest-1"));
        var workIqTopic = workIqSession.AcceptTopic("Release readiness", 1, 1);
        var workIqOther = workIqSession.AcceptTopic("Customer follow-up", 1, 2);
        workIqSession.ApplyAnalysisDelta(new([], [
            new("workiq-fact-1", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                1, workIqTopic.Id, null, null, null, null,
                "Release readiness was reviewed.", "Release readiness review"),
            new("workiq-fact-2", EvidenceOrigin.Analysis, VisualizationProvenance.TranscriptFact,
                2, workIqOther.Id, null, null, null, null,
                "Customer follow-up was planned.", "Customer follow-up plan"),
            new("workiq-source", EvidenceOrigin.RelatedContext,
                VisualizationProvenance.RelatedSourceSuggestion,
                null, workIqTopic.Id, "Prior release thread", "https://example.test/thread",
                ContextSourceType.TeamsMessage, null, "A prior Teams thread mentions the release.")
        ], []));
        Check.Throws<InvalidOperationException>(() => workIqSession.ApplyAnalysisDelta(
            new([], [], [
                new("workiq-link", workIqTopic.Id, workIqOther.Id,
                    ExplainedTopicLinkKind.BuildsOn, VisualizationProvenance.Inference,
                    ["workiq-source", "workiq-fact-2"], "Suggested by an earlier thread")
            ])));
        var workIqProjection = TopicMapState.Project(workIqSession.Snapshot(), workIqTopic.Id);
        var suggestion = workIqProjection.TopicBrief!.WorkIqSources.Single();
        Check.Equal("workiq-source", suggestion.Id);
        Check.Equal("Teams message", suggestion.Family);
        Check.Equal("Suggestion—not meeting evidence · Teams message", suggestion.Detail);
        Check.True(suggestion.AccessibleName.Contains(
            "Suggestion—not meeting evidence", StringComparison.Ordinal));
        Check.True(suggestion.CanOpen);
        Check.Equal("https://example.test/thread", suggestion.SourceUrl);
        Check.Equal(1, workIqProjection.TopicBrief.WorkIqVerifiedTotal);

        var detailedSuggestion = projection.TopicBrief.WorkIqSources.Single();
        Check.Equal("The deployment boundary was recorded.", detailedSuggestion.Snippet);
        Check.True(detailedSuggestion.HasTimestamp);
        Check.Equal("Why relevant: Matches the selected release-readiness topic.",
            detailedSuggestion.RelevanceText);
        Check.Equal("Your involvement: Teams author",
            detailedSuggestion.InvolvementText);

        var typedSources = TopicMapState.Project(document with
        {
            Evidence =
            [
                new("typed-mail", EvidenceOrigin.RelatedContext,
                    VisualizationProvenance.RelatedSourceSuggestion,
                    null, "topic-0001", "Budget review", "https://example.test/mail",
                    ContextSourceType.OutlookMail, null, "Mail thread"),
                new("typed-doc", EvidenceOrigin.RelatedContext,
                    VisualizationProvenance.RelatedSourceSuggestion,
                    null, "topic-0001", "Budget review", "https://example.test/doc",
                    ContextSourceType.Document, null, "Document"),
                new("typed-channel", EvidenceOrigin.RelatedContext,
                    VisualizationProvenance.RelatedSourceSuggestion,
                    null, "topic-0001", "Budget review", "https://example.test/channel",
                    ContextSourceType.TeamsChannel, null, "Teams channel"),
            ]
        }, "topic-0001").TopicBrief!;
        Check.Equal("\uE715", typedSources.WorkIqSources.Single(item => item.Id == "typed-mail").Glyph);
        Check.Equal("\uE8A5", typedSources.WorkIqSources.Single(item => item.Id == "typed-doc").Glyph);
        Check.Equal("\uE8BD", typedSources.WorkIqSources.Single(item => item.Id == "typed-channel").Glyph);

        var unsafeSource = TopicMapState.Project(document with
        {
            Evidence =
            [
                new("unsafe", EvidenceOrigin.RelatedContext,
                    VisualizationProvenance.RelatedSourceSuggestion,
                    null, "topic-0002", "Unsafe", "http://example.test/not-secure",
                    ContextSourceType.Document, null, "Not linkable")
            ]
        }, "topic-0002").TopicBrief!.WorkIqSources.Single();
        Check.True(!unsafeSource.CanOpen);
        Check.Equal(string.Empty, unsafeSource.SourceUrl);

        static VisualizationEvidence MakeBoundedSource(string id, ContextSourceType type) => new(
            id, EvidenceOrigin.RelatedContext, VisualizationProvenance.RelatedSourceSuggestion,
            null, "bounded-topic", $"Source {id}", $"https://example.test/{id}", type, null,
            $"Detail {id}");
        var boundedBrief = TopicMapState.Project(document with
        {
            TopicClusters = [new("bounded-topic", "Bounded topic", ["Bounded topic"], 1, 1)],
            TopicTrail = [new("occ-bounded", "bounded-topic", 1, 1, "Bounded topic")],
            Questions = [],
            ExplainedTopicLinks = [],
            Evidence =
            [
                MakeBoundedSource("bounded-teams-1", ContextSourceType.TeamsMessage),
                MakeBoundedSource("bounded-teams-2", ContextSourceType.TeamsMessage),
                MakeBoundedSource("bounded-teams-3", ContextSourceType.TeamsMessage),
                MakeBoundedSource("bounded-mail-1", ContextSourceType.OutlookMail),
                MakeBoundedSource("bounded-mail-2", ContextSourceType.OutlookMail),
                MakeBoundedSource("bounded-mail-3", ContextSourceType.OutlookMail),
                MakeBoundedSource("bounded-doc-1", ContextSourceType.Document),
            ],
        }, "bounded-topic").TopicBrief!;
        Check.Equal(5, boundedBrief.WorkIqSources.Count);
        Check.Equal(7, boundedBrief.WorkIqVerifiedTotal);
        Check.Equal(2, boundedBrief.WorkIqSources.Count(item => item.Family == "Teams message"));
        Check.Equal(2, boundedBrief.WorkIqSources.Count(item => item.Family == "Outlook mail"));

        var longTopic = "A long topic title remains intact in the timeline card and its accessible name.";
        var repeated = TopicMapState.Project(document with
        {
            TopicClusters =
            [
                new("topic-0001", longTopic, [longTopic], 1, 1),
                new("topic-0002", "Customer follow-up", ["Customer follow-up"], 2, 2),
            ],
            TopicTrail =
            [
                new("occ-a1", "topic-0001", 1, 1, longTopic),
                new("occ-a2", "topic-0001", 2, 2, "Readiness details"),
                new("occ-b1", "topic-0002", 3, 3, "Customer follow-up"),
                new("occ-b2", "topic-0002", 4, 4, "Follow-up details"),
                new("occ-a3", "topic-0001", 5, 5, "Readiness returns"),
            ]
        }, "topic-0001");
        Check.Equal(3, repeated.Trail.Count);
        Check.Equal(longTopic, repeated.Trail[0].Label);
        Check.True(repeated.Trail[0].Detail.Contains("2 consecutive updates collapsed", StringComparison.Ordinal));
        Check.True(repeated.Trail[2].Detail.StartsWith("Returned", StringComparison.Ordinal));
        Check.True(!repeated.Trail[0].IsRevisit && repeated.Trail[2].IsRevisit);
        Check.True(repeated.Trail[2].IsCurrent && repeated.Trail[2].IsSelected);
        Check.True(!repeated.Trail[0].IsCurrent && !repeated.Trail[0].IsSelected);
        Check.True(repeated.Trail[0].HasNext && repeated.Trail[1].HasNext && !repeated.Trail[2].HasNext);

        var appendedSameTopic = TopicMapState.Project(document with
        {
            TopicTrail = document.TopicTrail.Append(
                new("occ-000003", "topic-0002", 3, 3, "Follow-up details")).ToArray(),
        }, "topic-0001", "topic-0002");
        Check.Equal("occ-000001|occ-000002",
            string.Join("|", appendedSameTopic.Trail.Select(item => item.Id)));
        Check.True(appendedSameTopic.Trail[1].Detail.Contains(
            "2 consecutive updates collapsed", StringComparison.Ordinal));

        var appendedTopicDocument = document with
        {
            TopicClusters = document.TopicClusters.Append(
                new("topic-0003", "Risk review", ["Risk review"], 3, 3)).ToArray(),
            TopicTrail = document.TopicTrail.Append(
                new("occ-000003", "topic-0003", 3, 3, "Risk review")).ToArray(),
        };
        var appendedTopic = TopicMapState.Project(appendedTopicDocument, "topic-0001", "topic-0003");
        Check.Equal("occ-000001|occ-000002|occ-000003",
            string.Join("|", appendedTopic.Trail.Select(item => item.Id)));
        var displayedTrail = new System.Collections.ObjectModel.ObservableCollection<TrailItemViewModel>(
            projection.Trail);
        VisualizationCollectionState.Reconcile(
            displayedTrail, appendedTopic.Trail, item => item.Id,
            item => $"{item.Id}:{item.TopicId}:{item.Sequence}:{item.Label}:{item.Detail}:{item.RelationText}:{item.IsCurrent}:{item.IsSelected}:{item.HasNext}");
        Check.Equal("occ-000001|occ-000002|occ-000003",
            string.Join("|", displayedTrail.Select(item => item.Id)));
        Check.True(displayedTrail[0].IsSelected && !displayedTrail[0].IsCurrent);
        Check.True(displayedTrail[2].IsCurrent && !displayedTrail[2].IsSelected);

        var twentyTopics = TopicMapState.Project(document with
        {
            TopicClusters = Enumerable.Range(1, 20)
                .Select(index => new TopicCluster(
                    $"topic-{index:D4}", $"Topic {index}", [$"Topic {index}"], index, index))
                .ToArray(),
            TopicTrail = Enumerable.Range(1, 20)
                .Select(index => new TopicOccurrence(
                    $"occ-{index:D6}", $"topic-{index:D4}", index, index, $"Topic {index}"))
                .ToArray(),
            Questions = [],
            Actions = [],
            Decisions = [],
            Evidence = [],
            ExplainedTopicLinks = [],
        }, "topic-0007", "topic-0020");
        Check.Equal(20, twentyTopics.Trail.Count);
        Check.Equal("topic-0007", twentyTopics.SelectedTopicId);
        Check.Equal("topic-0020", twentyTopics.CurrentTopicId);
        Check.True(twentyTopics.Trail[6].IsSelected);
        Check.True(twentyTopics.Trail[19].IsCurrent);
        Check.Equal("occ-000001|occ-000002|occ-000003",
            string.Join("|", twentyTopics.Trail.Take(3).Select(item => item.Id)));

        var emptyProjection = TopicMapState.Project(document with
        {
            TopicClusters = [], TopicTrail = [], Questions = [], Evidence = [], ExplainedTopicLinks = [],
        }, null);
        Check.Equal(0, emptyProjection.Trail.Count);
        Check.True(emptyProjection.BrainMap is null);
        Check.True(emptyProjection.TopicBrief is null);
        Check.True(emptyProjection.EmptyMessage.Contains("timeline", StringComparison.OrdinalIgnoreCase));

        var noBranches = TopicMapState.Project(document with
        {
            Questions = [],
            Actions = [],
            Decisions = [],
            Evidence = [],
            ExplainedTopicLinks = [],
        }, "topic-0001");
        Check.Equal(0, noBranches.BrainMap!.Branches.Count);
        Check.True(noBranches.BrainMap.EmptyMessage.StartsWith(
            "No connected topics yet", StringComparison.Ordinal));

        var compactMap = BrainMapLayout.Calculate(420, 5);
        Check.Equal(5, compactMap.Branches.Count);
        Check.True(compactMap.Branches.Select(item => item.X).Distinct().Count() == 1);
        Check.True(compactMap.Branches.All(item => item.Y > compactMap.Root.Y + compactMap.Root.Height));
        var mediumMap = BrainMapLayout.Calculate(700, 5);
        Check.Equal(2, mediumMap.Branches.Select(item => item.X).Distinct().Count());
        Check.True(mediumMap.Branches.All(item => item.Width >= 300));
        Check.True(mediumMap.Branches.All(item => item.Y > mediumMap.Root.Y + mediumMap.Root.Height));
        var wideMap = BrainMapLayout.Calculate(1200, 5);
        Check.Equal(3, wideMap.Branches.Select(item => item.X).Distinct().Count());
        Check.True(wideMap.Branches.All(item => item.Width >= 300));
        Check.True(wideMap.Branches.All(item => item.Y > wideMap.Root.Y + wideMap.Root.Height));
        Check.True(wideMap.Branches.Min(item => item.X) > 0);
        Check.True(wideMap.Branches.Max(item => item.X + item.Width) < wideMap.Width);

        var changedDecision = TopicMapState.Project(document with
        {
            Decisions = [new("decision-1", "topic-0001", "Ship it", [])],
        }, "topic-0001", "topic-0002");
        Check.True(!StringComparer.Ordinal.Equals(projection.Fingerprint, changedDecision.Fingerprint));
        var changedRelation = TopicMapState.Project(document with
        {
            ExplainedTopicLinks = document.ExplainedTopicLinks
                .Select(link => link.Id == "topic-link-1"
                    ? link with { Kind = ExplainedTopicLinkKind.BuildsOn }
                    : link)
                .ToArray(),
        }, "topic-0001", "topic-0002");
        Check.True(!StringComparer.Ordinal.Equals(projection.Fingerprint, changedRelation.Fingerprint));
        Check.True(CopilotNotesService.VisualizationPrompt.Contains(
            "specific two-to-five-word noun phrase", StringComparison.Ordinal));

        var xamlPath = Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml");
        var xamlDocument = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Control(string name) => xamlDocument.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == name);
        var xamlNames = xamlDocument.Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Check.Equal("1", (string?)Control("TopicMapPanel").Attribute("Grid.Column"));
        Check.Equal("Topic map", (string?)Control("VisualizationTitleText").Attribute("Text"));
        Check.True(!xamlNames.Contains("VisualizationViewSelector"));
        Check.True(!xamlDocument.Descendants().Any(element =>
            element.Name.LocalName == "SelectorBarItem"));
        Check.True(xamlNames.Contains("BrainMapCanvas"));
        Check.True(xamlNames.Contains("BrainMapEmptyText"));
        Check.True(xamlNames.Contains("BrainMapNowText"));
        Check.Equal("Grounded spatial brain map",
            (string?)Control("BrainMapView").Attribute("AutomationProperties.Name"));
        Check.Equal("Grounded topic branches",
            (string?)Control("BrainMapCanvas").Attribute("AutomationProperties.Name"));
        Check.True(Control("BrainMapCanvas").Ancestors().Contains(Control("TopicMapCombinedView")));

        var timeline = Control("TopicTimelineView");
        Check.True(Control("TrailList").Ancestors().Contains(timeline));
        Check.True(Control("TrailList").Ancestors().Contains(Control("TopicMapCombinedView")));
        Check.Equal("0", (string?)timeline.Attribute("Grid.Row"));
        Check.Equal("Auto", (string?)timeline.Attribute("HorizontalScrollBarVisibility"));
        Check.Equal("Disabled", (string?)timeline.Attribute("VerticalScrollBarVisibility"));
        Check.Equal("100", (string?)timeline.Attribute("Height"));
        var combinedRows = Control("TopicMapCombinedView").Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions").Elements().ToArray();
        var combinedColumns = Control("TopicMapCombinedView").Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions").Elements().ToArray();
        Check.Equal("106", (string?)combinedRows[0].Attribute("Height"));
        Check.Equal("*", (string?)combinedRows[1].Attribute("Height"));
        Check.Equal("Auto", (string?)combinedRows[2].Attribute("Height"));
        Check.Equal("*", (string?)combinedColumns[0].Attribute("Width"));
        Check.Equal("0", (string?)combinedColumns[1].Attribute("Width"));
        Check.Equal("1", (string?)Control("BrainMapView").Attribute("Grid.Row"));
        var timelinePanel = Control("TrailList").Descendants()
            .Single(element =>
                element.Name.LocalName == "StackPanel" &&
                element.Parent?.Name.LocalName == "ItemsPanelTemplate");
        Check.Equal("Horizontal", (string?)timelinePanel.Attribute("Orientation"));
        var topicButton = Control("TrailList").Descendants().Single(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Click") == "OnVisualizationTopicClick");
        Check.Equal("{Binding AccessibleName}", (string?)topicButton.Attribute("AutomationProperties.Name"));
        Check.Equal("{Binding TopicId}", (string?)topicButton.Attribute("Tag"));
        Check.True(!Control("TrailList").Descendants().Any(element =>
            (string?)element.Attribute("Text") == "{Binding RelationText}"));
        Check.True(!Control("TrailList").Descendants().Attributes()
            .Any(attribute => attribute.Value.Contains("WorkIq", StringComparison.OrdinalIgnoreCase)));
        Check.Equal("{Binding Detail}", (string?)topicButton.Attribute("AutomationProperties.HelpText"));
        Check.Equal("{Binding AccessibleName}", (string?)topicButton.Attribute("ToolTipService.ToolTip"));
        Check.Equal("220", (string?)topicButton.Attribute("Width"));
        Check.Equal("84", (string?)topicButton.Attribute("Height"));
        var topicLabel = topicButton.Descendants().Single(element =>
            (string?)element.Attribute("Text") == "{Binding Label}");
        Check.Equal("1", (string?)topicLabel.Attribute("MaxLines"));
        Check.Equal("NoWrap", (string?)topicLabel.Attribute("TextWrapping"));
        Check.Equal("WordEllipsis", (string?)topicLabel.Attribute("TextTrimming"));
        Check.True(topicButton.Descendants().Any(element =>
            (string?)element.Attribute("Text") == "Returned" &&
            (string?)element.Parent?.Attribute("Visibility") ==
            "{Binding IsRevisit, Converter={StaticResource BoolToVisibleConverter}}"));
        Check.True(Control("TrailList").Descendants().Any(element =>
            (string?)element.Attribute("Visibility") ==
            "{Binding IsCurrent, Converter={StaticResource BoolToVisibleConverter}}"));
        Check.True(Control("TrailList").Descendants().Any(element =>
            (string?)element.Attribute("Visibility") ==
            "{Binding IsSelected, Converter={StaticResource BoolToVisibleConverter}}"));
        var guide = (string?)Control("TopicTimelineGuideText").Attribute("Text");
        foreach (var phrase in new[]
                 { "chronological strip", "Returned", "center", "Now", "Go to current topic" })
            Check.True(guide?.Contains(phrase, StringComparison.OrdinalIgnoreCase) == true);
        Check.True(!xamlNames.Contains("TrailEmptyText"));
        Check.Equal("Show meeting visualization", (string?)Control("TopicMapToggleButton")
            .Attribute("ToolTipService.ToolTip"));
        Check.Equal("OnTopicMapToggleClick", (string?)Control("TopicMapToggleButton").Attribute("Click"));

        Check.True(Control("TopicBriefFactsList").Ancestors().Contains(Control("TopicDetailPanel")));
        Check.True(Control("TopicBriefRelationshipsList").Ancestors().Contains(Control("TopicDetailPanel")));
        Check.True(Control("TopicBriefQuestionsList").Ancestors().Contains(Control("TopicDetailPanel")));
        Check.True(Control("TopicBriefActionsList").Ancestors().Contains(Control("TopicDetailPanel")));
        Check.True(Control("TopicBriefDecisionsList").Ancestors().Contains(Control("TopicDetailPanel")));
        Check.True(Control("TopicBriefSourcesList").Ancestors().Contains(Control("TopicDetailPanel")));
        Check.True(Control("TopicDetailPanel").Ancestors().Contains(Control("TopicMapCombinedView")));
        Check.Equal(1, Control("TopicDetailPanel").Descendants()
            .Count(element => element.Name.LocalName == "ScrollViewer"));
        foreach (var name in new[]
                 { "TopicBriefFactsSection", "TopicBriefRelationshipsSection", "TopicBriefQuestionsSection",
                     "TopicBriefActionsSection", "TopicBriefSourcesSection" })
            Check.Equal("Collapsed", (string?)Control(name).Attribute("Visibility"));
        var xamlRaw = File.ReadAllText(xamlPath);
        var briefTitleIndex = xamlRaw.IndexOf("x:Name=\"TopicBriefTitleText\"", StringComparison.Ordinal);
        var briefFactsIndex = xamlRaw.IndexOf("x:Name=\"TopicBriefFactsSection\"", StringComparison.Ordinal);
        Check.True(briefTitleIndex >= 0 && briefFactsIndex > briefTitleIndex);
        Check.True(!xamlNames.Contains("TopicBriefView"));
        Check.True(!xamlNames.Contains("TopicBriefBackToTimelineButton"));
        Check.True(!xamlRaw.Contains("Back to timeline", StringComparison.OrdinalIgnoreCase));
        Check.True(((string?)Control("TopicBriefAnalysisText").Attribute("Text"))?.StartsWith(
            "Still analyzing the current topic.", StringComparison.Ordinal) == true);
        Check.Equal("Selected topic details",
            (string?)Control("TopicDetailView").Attribute("AutomationProperties.Name"));
        var factTemplate = Control("TopicBriefFactsList").Descendants().ToArray();
        Check.True(factTemplate.Any(element =>
            (string?)element.Attribute("Text") == "{Binding EvidenceText}"));
        Check.True(factTemplate.Any(element =>
            (string?)element.Attribute("Text") == "{Binding Metadata}"));
        var relationshipTemplate = Control("TopicBriefRelationshipsList").Descendants().ToArray();
        Check.True(relationshipTemplate.Any(element =>
            (string?)element.Attribute("Text") == "{Binding Title}"));
        Check.True(relationshipTemplate.Any(element =>
            (string?)element.Attribute("Text") == "{Binding Detail}"));
        var sourceSection = Control("TopicBriefSourcesSection");
        Check.True(Control("TopicBriefContent").Descendants().Any(element =>
            (string?)element.Attribute("Text") == "From this meeting"));
        Check.True(sourceSection.Descendants().Any(element =>
            (string?)element.Attribute("Text") == "Related workplace suggestions"));
        Check.True(sourceSection.Descendants().Any(element =>
            (string?)element.Attribute("Text") == "Suggestions—not meeting evidence"));
        foreach (var binding in new[]
                 { "{Binding Detail}", "{Binding Title}", "{Binding Snippet}", "{Binding Timestamp}",
                     "{Binding RelevanceText}", "{Binding InvolvementText}" })
            Check.True(sourceSection.Descendants().Any(element =>
                (string?)element.Attribute("Text") == binding));
        var openSourceButton = sourceSection.Descendants().Single(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Click") == "OnOpenContextSourceClick");
        Check.Equal("Open source", (string?)openSourceButton.Attribute("Content"));
        Check.Equal("{Binding CanOpen, Converter={StaticResource BoolToVisibleConverter}}",
            (string?)openSourceButton.Attribute("Visibility"));
        Check.Equal("{Binding OpenActionName}",
            (string?)openSourceButton.Attribute("AutomationProperties.Name"));
        var contextAction = Control("TopicBriefContextActionButton");
        Check.Equal("Find related work", (string?)contextAction.Attribute("Content"));
        Check.Equal("OnRelatedContextRefreshClick", (string?)contextAction.Attribute("Click"));
        Check.Equal("Find or retry related workplace suggestions",
            (string?)contextAction.Attribute("AutomationProperties.Name"));
        Check.True(!Control("TopicBriefSourcesList").Descendants().Any(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("AutomationProperties.Name") == "{Binding Title}"));
        Check.True(Control("TrailList").Descendants().Any(element =>
            (string?)element.Attribute("Text") == "{Binding ContextStatus.Text}"));
        Check.Equal("Collapsed", (string?)Control("TopicBriefContextStatus").Attribute("Visibility"));
        Check.True(Control("TopicBriefContextStatusText").Ancestors()
            .Contains(Control("TopicDetailPanel")));
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var topicBriefStart = source.IndexOf(
            "private void RenderTopicBrief(VisualizationProjection projection)", StringComparison.Ordinal);
        var speakerSectionStart = source.IndexOf(
            "// ── Speaker assignment", topicBriefStart, StringComparison.Ordinal);
        Check.True(topicBriefStart >= 0 && speakerSectionStart > topicBriefStart);
        var topicBriefRender = source[topicBriefStart..speakerSectionStart];
        Check.True(topicBriefRender.Contains("TopicBriefDetailText.Text = brief.Status",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefDiscussedAtText.Text = brief.DiscussedAt",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains(
            "brief.IsCurrent && Volatile.Read(ref _visualizationAnalysisPending) > 0",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefFactsSection.Visibility = brief.Facts.Count > 0",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains(
            "TopicBriefRelationshipsSection.Visibility = brief.Relationships.Count > 0",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefQuestionsSection.Visibility = brief.Questions.Count > 0",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefActionsSection.Visibility = brief.Actions.Count > 0",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefSourcesSection.Visibility",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefNoDetailsText.Visibility",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefTitleText.Text = brief.Label",
            StringComparison.Ordinal));
        Check.True(topicBriefRender.Contains("TopicBriefContextStatusText.Text = contextStatus.Text",
            StringComparison.Ordinal));
        var clickStart = source.IndexOf("private void OnVisualizationTopicClick(", StringComparison.Ordinal);
        var currentStart = source.IndexOf("private void OnGoToCurrentTopicClick(", clickStart);
        Check.True(clickStart >= 0 && currentStart > clickStart);
        var clickHandler = source[clickStart..currentStart];
        Check.True(clickHandler.Contains("_selectedVisualizationTopicId = topicId;", StringComparison.Ordinal));
        Check.True(clickHandler.Contains("RefreshVisualization();", StringComparison.Ordinal));
        Check.True(!clickHandler.Contains("Focus(FocusState.Programmatic)", StringComparison.Ordinal));
        Check.True(!clickHandler.Contains("_currentVisualizationTopicId =", StringComparison.Ordinal));
        Check.True(!clickHandler.Contains("TopicMapState.LayoutBandedMap", StringComparison.Ordinal));
        var currentHandlerStart = currentStart;
        var revealStart = source.IndexOf("private void RevealTimelineTopic(", currentStart);
        Check.True(revealStart > currentHandlerStart);
        var currentHandler = source[currentHandlerStart..revealStart];
        Check.True(currentHandler.Contains("_selectedVisualizationTopicId = currentTopicId;",
            StringComparison.Ordinal));
        Check.True(currentHandler.Contains("RevealTimelineTopic(currentTopicId);", StringComparison.Ordinal));
        Check.True(!currentHandler.Contains("Focus(FocusState.Programmatic)", StringComparison.Ordinal));
        Check.Equal(2, source.Split("RevealTimelineTopic(", StringSplitOptions.None).Length - 1);
        var revealEnd = source.IndexOf("private async void OnOpenVisualizationClick(", revealStart,
            StringComparison.Ordinal);
        Check.True(revealEnd > revealStart);
        var revealHandler = source[revealStart..revealEnd];
        Check.True(revealHandler.Contains("container.StartBringIntoView();", StringComparison.Ordinal));
        Check.True(!revealHandler.Contains("ScrollTo", StringComparison.Ordinal));
        Check.True(!source.Contains("OnTopicBriefBackToTimelineClick", StringComparison.Ordinal));
        Check.True(!source.Contains("VisualizationSelectedViewIndex", StringComparison.Ordinal));
        Check.True(!source.Contains("VisualizationViewSelector", StringComparison.Ordinal));
        var refreshStart = source.IndexOf("private void RefreshVisualization(", StringComparison.Ordinal);
        var clearStart = source.IndexOf("private void ClearVisualizationPresentation(", refreshStart, StringComparison.Ordinal);
        Check.True(refreshStart >= 0 && clearStart > refreshStart);
        var refreshPath = source[refreshStart..clearStart];
        Check.True(refreshPath.Contains("VisualizationCollectionState.Reconcile(", StringComparison.Ordinal));
        Check.True(refreshPath.Contains(
            "var persistSelection = TopicMapPanel.Visibility == Visibility.Visible ||",
            StringComparison.Ordinal));
        Check.True(refreshPath.IndexOf(
            "ResolveLiveCanonicalTopicId(_selectedVisualizationTopicId)",
            StringComparison.Ordinal) < refreshPath.IndexOf(
            "TopicMapState.Project(",
            StringComparison.Ordinal));
        Check.True(refreshPath.Contains("_workIq.IsAuthenticated", StringComparison.Ordinal));
        Check.True(!refreshPath.Contains("StartBringIntoView", StringComparison.Ordinal));
        Check.True(!refreshPath.Contains("ScrollTo", StringComparison.Ordinal));
        Check.True(!refreshPath.Contains("ChangeView", StringComparison.Ordinal));
        var changedStart = source.IndexOf("private void OnVisualizationChanged(", StringComparison.Ordinal);
        var topicClickAfterChanged = source.IndexOf("private void OnVisualizationTopicClick(", changedStart,
            StringComparison.Ordinal);
        Check.True(changedStart >= 0 && topicClickAfterChanged > changedStart);
        var appendPath = source[changedStart..topicClickAfterChanged];
        Check.True(!appendPath.Contains("StartBringIntoView", StringComparison.Ordinal));
        Check.True(!appendPath.Contains("ScrollTo", StringComparison.Ordinal));
        Check.True(!appendPath.Contains("ChangeView", StringComparison.Ordinal));
        Check.True(!source.Contains("FocusTimelineTopic(", StringComparison.Ordinal));
        Check.True(!source.Contains("_selectedVisualizationTopicId ??= accepted.Id;",
            StringComparison.Ordinal));
        Check.True(source.Contains("TopicMapCombinedView.Visibility = Visibility.Visible",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "status.State is PipelineState.Saved or PipelineState.Failed &&",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "_workIq.IsAuthenticated, _loadedVisualization is not null)",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "var currentTopicId = ResolveLiveCanonicalTopicId(_currentVisualizationTopicId);",
            StringComparison.Ordinal));
        Check.True(source.Contains("TopicId: currentTopicId)", StringComparison.Ordinal));
        Check.True(source.Contains(
            "if (_sessionId is null || _contextCts is null || _loadedVisualization is not null)",
            StringComparison.Ordinal));
        Check.True(source.Contains("RefreshVisualization(force: true);", StringComparison.Ordinal));
        Check.True(source.Contains("\"Completed meeting · retained after recording\"",
            StringComparison.Ordinal));
        Check.True(source.Contains("? \"Saved meeting\"", StringComparison.Ordinal));
        Check.True(source.Contains("? \"Retained meeting\"", StringComparison.Ordinal));
        Check.True(source.Contains("TimelineEmptyText.Visibility = _visualizationTrail.Count == 0",
            StringComparison.Ordinal));
        Check.True(source.Contains("TopicTimelineView.Visibility = _visualizationTrail.Count > 0",
            StringComparison.Ordinal));
        Check.True(source.Contains("GoToCurrentTopicButton.Visibility = canGoToCurrentTopic",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "GoToCurrentTopicButton.Visibility = canGoToCurrentTopic\n            ? Visibility.Visible\n            : Visibility.Collapsed;",
            StringComparison.Ordinal));
        Check.True(source.Contains("private void RenderBrainMap(", StringComparison.Ordinal));
        Check.True(source.Contains("BrainMapLayout.Calculate(", StringComparison.Ordinal));
        Check.True(source.Contains("_brainMapConnectors.TryGetValue(branch.Id, out var connector)",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "figure.Segments.Add(new BezierSegment",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "Point3 = new(branchCenterX, branchY)",
            StringComparison.Ordinal));
        Check.True(source.Contains("_brainMapNodes.TryGetValue(key, out var button)",
            StringComparison.Ordinal));
        Check.True(source.Contains("private void OnBrainMapNodeClick(", StringComparison.Ordinal));
        Check.True(source.Contains(
            "AutomationProperties.SetAutomationId(button, $\"BrainMapNode_{nodeId}\");",
            StringComparison.Ordinal));
        var nodeClickStart = source.IndexOf("private void OnBrainMapNodeClick(", StringComparison.Ordinal);
        var nodeClickEnd = source.IndexOf("private static string? FocusedAutomationIdWithin(", nodeClickStart,
            StringComparison.Ordinal);
        Check.True(nodeClickStart >= 0 && nodeClickEnd > nodeClickStart);
        Check.True(!source[nodeClickStart..nodeClickEnd].Contains(
            "Focus(FocusState.Programmatic)", StringComparison.Ordinal));
        Check.True(!source.Contains("_brainMapRootButton?.Focus(FocusState.Programmatic);",
            StringComparison.Ordinal));
        Check.True(!source.Contains("TopicDetailView.Focus(FocusState.Programmatic);",
            StringComparison.Ordinal));
        Check.True(!source.Contains("LayoutBandedMap(", StringComparison.Ordinal));
        Check.True(source.Contains(
            "_visualization.ApplyRelatedContext(mappedTopicId, snapshot);",
            StringComparison.Ordinal));
        Check.True(source.Contains("await _pipeline.RefreshVisualizationAnalysisAsync(cancellationToken);",
            StringComparison.Ordinal));
        Check.True(source.Contains("_visualizationStorage.LoadAsync(file.Path", StringComparison.Ordinal));
        Check.True(source.Contains("_visualization.ResolveCanonicalTopicId(topicId)", StringComparison.Ordinal));
    }

    private static void BrainMapCanvasOwnership()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brainMapView = xaml.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "BrainMapView");
        Check.Equal("OnBrainMapViewSizeChanged", (string?)brainMapView.Attribute("SizeChanged"));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        var resizeStart = source.IndexOf(
            "private void OnBrainMapViewSizeChanged(", StringComparison.Ordinal);
        var renderStart = source.IndexOf("private void RenderBrainMap(", resizeStart,
            StringComparison.Ordinal);
        Check.True(resizeStart >= 0 && renderStart > resizeStart);
        var resizeHandler = source[resizeStart..renderStart];
        Check.True(resizeHandler.Contains(
            "Math.Abs(e.NewSize.Width - e.PreviousSize.Width) <= 1",
            StringComparison.Ordinal));
        Check.True(resizeHandler.Contains("RenderBrainMap(projection);", StringComparison.Ordinal));

        Check.True(source.Contains("_brainMapRootButton = UpsertBrainMapNode(", StringComparison.Ordinal));
        Check.True(!source.Contains(
            "BrainMapCanvas.Children.Add(_brainMapRootButton);",
            StringComparison.Ordinal));

        var upsertStart = source.IndexOf("private Button UpsertBrainMapNode(", StringComparison.Ordinal);
        var clickStart = source.IndexOf("private void OnBrainMapNodeClick(", upsertStart, StringComparison.Ordinal);
        Check.True(upsertStart >= 0 && clickStart > upsertStart);
        var upsert = source[upsertStart..clickStart];
        Check.Equal(1, Regex.Matches(
            upsert,
            @"BrainMapCanvas\.Children\.Add\(button\);",
            RegexOptions.CultureInvariant).Count);
        Check.True(source.Contains(
            "BrainMapCanvas.Children.Add(connector);",
            StringComparison.Ordinal));
    }

    private static void TopicTimelineSpeakerProjection()
    {
        var session = new MeetingVisualizationSession();
        session.Begin("one", "Review", DateTimeOffset.UtcNow);
        var first = session.AcceptTopic("Guest-1 reviews Guest-10 readiness", 0, 0);
        session.ApplySpeakerAssignment("Guest-1", "Avery");
        var renamed = TopicMapState.Project(session.Snapshot(), first.Id);
        Check.Equal("Avery reviews Guest-10 readiness",
            renamed.Trail.Single().Label);
        Check.Equal(first.Id, renamed.SelectedTopicId);

        session.ApplySpeakerAssignment("Guest-1", "Morgan");
        var corrected = TopicMapState.Project(session.Snapshot(), first.Id);
        Check.Equal("Morgan reviews Guest-10 readiness",
            corrected.Trail.Single().Label);
        Check.Equal(first.Id, corrected.SelectedTopicId);

        var late = session.AcceptTopic("Guest-1 confirms the rollout", 0, 0);
        var lateProjection = TopicMapState.Project(session.Snapshot(), first.Id, late.Id);
        Check.Equal(first.Id, lateProjection.SelectedTopicId);
        Check.Equal(late.Id, lateProjection.CurrentTopicId);
        Check.Equal(2, lateProjection.Trail.Count);
        Check.Equal("Morgan confirms the rollout", lateProjection.Trail
            .Single(item => item.TopicId == late.Id).Label);
        Check.True(lateProjection.Trail[1].IsCurrent && !lateProjection.Trail[1].IsSelected);

        session.Clear();
        session.Begin("two", "New review", DateTimeOffset.UtcNow);
        var isolated = session.AcceptTopic("Guest-1 starts a new session", 0, 0);
        var isolatedProjection = TopicMapState.Project(session.Snapshot(), isolated.Id);
        Check.Equal("Guest-1 starts a new session",
            isolatedProjection.Trail.Single().Label);

        var exact = TopicMapState.ProjectSpeakers(
            "Guest-1 checks Guest-10",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Guest-1"] = "Avery"
            });
        Check.Equal("Avery checks Guest-10", exact);
    }
    private static void UnifiedSettings()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Control(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);

        var preferences = Control("PreferencesPanel");
        var settingsScroll = preferences.Elements().Single(e => e.Name.LocalName == "ScrollViewer");
        foreach (var name in new[]
                 {
                     "TEndpoint", "TRegion", "TLanguage", "TApiKey", "IProvider",
                     "ISynthesisModel", "IIntelligenceModel", "IEndpoint", "IApiVersion",
                     "IApiKey", "ISynthesisDeployment", "IIntelligenceDeployment",
                     "CaptureMic", "CaptureLoopback", "NotesPath",
                 })
        {
            var control = Control(name);
            Check.True(control.Ancestors().Contains(settingsScroll));
            Check.True(control.Attribute("Width") is null);
        }

        foreach (var name in new[] { "PreferencesStopButton", "CancelButton", "SaveButton" })
            Check.True(!Control(name).Ancestors().Any(e => e.Name.LocalName == "ScrollViewer"));
        foreach (var name in new[] { "ISynthesisModel", "IIntelligenceModel" })
        {
            Check.Equal("ComboBox", Control(name).Name.LocalName);
            Check.Equal("Display", (string?)Control(name).Attribute("DisplayMemberPath"));
            Check.Equal("Id", (string?)Control(name).Attribute("SelectedValuePath"));
        }
        Check.Equal("TextBox", Control("ISynthesisDeployment").Name.LocalName);
        Check.Equal("TextBox", Control("IIntelligenceDeployment").Name.LocalName);
        Check.Equal("OnRefreshCopilotModelsClick",
            (string?)Control("RefreshCopilotModelsButton").Attribute("Click"));
        Check.Equal("Polite",
            (string?)Control("CopilotModelCatalogStatus").Attribute("AutomationProperties.LiveSetting"));
        Check.Equal("2", (string?)Control("SaveButton").Parent?.Parent?.Attribute("Grid.Row"));
        Check.Equal("OnSettingsCancelClick", (string?)Control("CancelButton").Attribute("Click"));
        Check.Equal("OnSaveClick", (string?)Control("SaveButton").Attribute("Click"));
        Check.True(!document.Descendants().Attributes().Any(a => a.Value == "OnSettingsClick"));

        var mainSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var normalizedMainSource = mainSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        var settingsSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.Settings.cs.txt"));
        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(mainSource.Contains("OpenSettingsEditor();", StringComparison.Ordinal));
        Check.True(mainSource.Contains("CancelSettingsEditor();", StringComparison.Ordinal));
        Check.True(normalizedMainSource.Contains(
            "if (PreferencesPanel.Visibility == Visibility.Visible)\n            return;",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains("string.IsNullOrEmpty(IApiKey.Password) ? savedApiKey", StringComparison.Ordinal));
        Check.True(settingsSource.Contains("TranscriptionApiKey = TApiKey.Password", StringComparison.Ordinal));
        Check.True(settingsSource.Contains("CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)", StringComparison.Ordinal));
        Check.True(settingsSource.Contains("CancelSettingsEditorLifetime();", StringComparison.Ordinal));
        Check.True(settingsSource.Contains("_copilotModelCatalog.GetModelsAsync(forceRefresh, lifetime.Token)",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains("_copilotModelLoadLifetime == lifetime", StringComparison.Ordinal));
        Check.True(settingsSource.Contains("StringComparison.OrdinalIgnoreCase", StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "ISynthesisModel.ItemsSource = _synthesisCopilotModels;",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "IIntelligenceModel.ItemsSource = _intelligenceCopilotModels;",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "CurrentCopilotModel(ISynthesisModel, _synthesisModelSelection)",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "CurrentCopilotModel(IIntelligenceModel, _intelligenceModelSelection)",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "selector.SelectedItem = CopilotModelPresentation.FindSelection(",
            StringComparison.Ordinal));
        Check.True(!settingsSource.Contains("ISynthesisModel.Text.Trim()", StringComparison.Ordinal));
        Check.True(!settingsSource.Contains("IIntelligenceModel.Text.Trim()", StringComparison.Ordinal));
        var choices = CopilotModelPresentation.Merge(
            [
                new("gpt-5.6-sol", "GPT-5.6 Sol"),
                new("gpt-5.6-terra", "GPT-5.6 Terra"),
            ],
            "gpt-5.6-sol",
            "configured-missing");
        Check.Equal("GPT-5.6 Sol (gpt-5.6-sol)",
            choices.Single(choice => choice.Id == "gpt-5.6-sol").Display);
        Check.True(choices.Single(choice => choice.Id == "gpt-5.6-sol").IsAvailable);
        Check.True(!choices.Single(choice => choice.Id == "configured-missing").IsAvailable);
        Check.True(choices.Single(choice => choice.Id == "configured-missing").Display
            .Contains("configured; not currently available", StringComparison.Ordinal));
        Check.Equal(1, choices.Count(choice => choice.Id == "gpt-5.6-sol"));
        Check.Equal("gpt-5.6-sol",
            CopilotModelPresentation.FindSelection(choices, "GPT-5.6-SOL")!.Id);
        Check.Equal("configured-missing",
            CopilotModelPresentation.FindSelection(choices, "configured-missing")!.Id);
        Check.True(CopilotModelPresentation.FindSelection(choices, string.Empty) is null);
        Check.True(appSource.Contains("_mainWindow.ShowSettings();", StringComparison.Ordinal));
        Check.True(appSource.Contains("services.AddSingleton<CopilotModelCatalog>();", StringComparison.Ordinal));
        Check.True(!appSource.Contains("SettingsWindow", StringComparison.Ordinal));
    }

    private static void SingleFlight()
    {
        var panel = new PanelRefreshState();
        panel.Invalidate("session-a", 1);
        Check.Equal(PanelState.Pending, panel.Status.State);
        Check.True(panel.TryBegin("other-session", 10, 1, false) is null);
        Check.True(panel.TryBegin("session-a", 10, 2, false) is null);
        var generation = panel.TryBegin("session-a", 10, 1, false)!.Value;
        Check.True(panel.IsBusy);
        Check.Equal(PanelState.Refreshing, panel.Status.State);
        Check.True(panel.Status.LastAttemptAt is not null);
        Check.True(panel.TryBegin("session-a", 11, 1, true) is null);
        Check.True(panel.Complete(generation, 10, hasResults: false, failure: null));
        Check.Equal(PanelState.SucceededEmpty, panel.Status.State);
        Check.True(panel.Status.LastSuccessAt is not null);
        Check.True(panel.IsBusy);
        panel.End(generation);
        Check.True(!panel.IsBusy);
        Check.True(panel.TryBegin("session-a", 10, 1, false) is null);
        Check.True(panel.TryBegin("session-a", 9, 1, false) is null);
        var retry = panel.TryBegin("session-a", 10, 1, true)!.Value;
        Check.True(panel.Complete(retry, 10, true, null));
        panel.End(retry);
        Check.Equal(PanelState.SucceededWithResults, panel.Status.State);
        var canceled = panel.TryBegin("session-a", 11, 1, false)!.Value;
        panel.End(canceled);
        Check.Equal(PanelState.Pending, panel.Status.State);
        Check.True(!panel.IsBusy);
        Check.True(panel.TryBegin("session-a", 11, 1, false) is not null);
    }

    private static void Invalidation()
    {
        var panel = new PanelRefreshState();
        panel.Invalidate("session-a", 1);
        var old = panel.TryBegin("session-a", 10, 1, false)!.Value;
        panel.Invalidate("session-a", 2);
        Check.True(panel.IsBusy);
        Check.True(panel.TryBegin("session-a", 10, 2, true) is null);
        Check.True(!panel.Complete(old, 10, true, null));
        Check.Equal(PanelState.Pending, panel.Status.State);
        Check.Equal(2L, panel.Status.ConfigurationRevision);
        panel.End(old);
        var current = panel.TryBegin("session-a", 10, 2, false)!.Value;
        Check.True(current != old);
        Check.True(!panel.Complete(old, 99, true, null));
        panel.Invalidate("session-b", 2);
        Check.True(!panel.Complete(current, 10, true, null));
        panel.End(current);
        Check.Equal("session-b", panel.Status.SessionId);
        Check.Equal(-1L, panel.Status.TranscriptRevision);
        var next = panel.TryBegin("session-b", 1, 2, false)!.Value;
        Check.True(panel.Complete(next, 1, true, null));
        panel.End(next);
        Check.Equal("session-b", panel.Status.SessionId);
        Check.Equal(1L, panel.Status.TranscriptRevision);
    }

    private static void IndependentPanels()
    {
        var summary = new PanelRefreshState();
        var questions = new PanelRefreshState();
        var related = new PanelRefreshState();
        foreach (var panel in new[] { summary, questions, related })
            panel.Invalidate("session", 1);
        var summaryRequest = summary.TryBegin("session", 1, 1, false)!.Value;
        var questionRequest = questions.TryBegin("session", 1, 1, false)!.Value;
        var relatedRequest = related.TryBegin("session", 1, 1, false)!.Value;
        var unavailable = IntelligenceErrors.Create(IntelligenceFailureKind.ModelUnavailable, "github", "test").Failure;
        Check.True(summary.Complete(summaryRequest, 1, false, unavailable));
        Check.True(questions.Complete(questionRequest, 1, false, null));
        Check.True(related.Complete(relatedRequest, 1, true, null));
        summary.End(summaryRequest);
        questions.End(questionRequest);
        related.End(relatedRequest);
        Check.Equal(PanelState.Failed, summary.Status.State);
        Check.Equal(PanelState.SucceededEmpty, questions.Status.State);
        Check.Equal(PanelState.SucceededWithResults, related.Status.State);
        Check.True(summary.Status.LastSuccessAt is null);
        var priorSuccess = related.Status.LastSuccessAt;
        var refresh = related.TryBegin("session", 2, 1, false)!.Value;
        var network = IntelligenceErrors.Create(IntelligenceFailureKind.Network, "graph").Failure;
        related.Complete(refresh, 2, false, network);
        related.End(refresh);
        Check.Equal(priorSuccess, related.Status.LastSuccessAt);
        Check.Equal(IntelligenceFailureKind.Network, related.Status.Failure!.Kind);
        foreach (var kind in new[] { IntelligenceFailureKind.Disabled, IntelligenceFailureKind.SignedOut })
        {
            var request = related.TryBegin("session", 2, 1, true)!.Value;
            related.Complete(request, 2, false, IntelligenceErrors.Create(kind, "graph").Failure);
            related.End(request);
            Check.Equal(PanelState.Unavailable, related.Status.State);
            Check.Equal(kind, related.Status.Failure!.Kind);
        }
        Check.Equal(PanelState.SucceededEmpty, questions.Status.State);
    }

    private static void Revisions()
    {
        var panel = new PanelRefreshState();
        panel.Invalidate("session", 1);
        var generation = panel.TryBegin("session", 10, 1, false)!.Value;
        Check.True(panel.Complete(generation, 10, true, null));
        panel.End(generation);
        var next = panel.TryBegin("session", 12, 1, false)!.Value;
        Check.True(panel.Complete(next, 12, true, null));
        panel.End(next);
        Check.True(!panel.Complete(generation, 10, true, null));
        Check.Equal(12L, panel.Status.TranscriptRevision);
        var growing = panel.TryBegin("session", 13, 1, false)!.Value;
        // A captured revision stays useful while new transcript text arrives.
        Check.True(panel.Complete(growing, 13, true, null));
        panel.End(growing);
        Check.Equal(13L, panel.Status.TranscriptRevision);
    }

    private static void Viewport()
    {
        var viewport = new TranscriptViewport();
        Check.Equal(500, TranscriptViewport.Capacity);
        Check.Equal((0, 0), viewport.Update(0));
        Check.Equal((0, 1), viewport.Update(1));
        Check.Equal((0, 500), viewport.Update(500));
        Check.Equal((1, 500), viewport.Update(501));
        Check.Equal((701, 500), viewport.Update(1201));
        viewport.Pause();
        Check.True(!viewport.FollowLatest);
        Check.Equal((701, 500), viewport.Update(1301));
        viewport.Earlier();
        Check.Equal((201, 500), viewport.Update(1301));
        viewport.Earlier();
        Check.Equal((0, 500), viewport.Update(1301));
        viewport.Earlier();
        Check.Equal((0, 500), viewport.Update(1301));
        viewport.Later(1301);
        Check.Equal((500, 500), viewport.Update(1301));
        viewport.Later(1301);
        Check.Equal((801, 500), viewport.Update(1301));
        viewport.Later(1301);
        Check.Equal((801, 500), viewport.Update(1301));
        viewport.Latest();
        Check.True(viewport.FollowLatest);
        Check.Equal((802, 500), viewport.Update(1302));
        Check.Equal((0, 0), viewport.Update(0));
    }

    private static void TranscriptRows()
    {
        var state = new TranscriptCollectionState<FakeTranscriptRow>(500);
        var collectionChanges = 0;
        state.Rows.CollectionChanged += (_, _) => collectionChanges++;
        var snapshot = new[]
        {
            new FakeTranscriptSegment("Guest-1", "First"),
            new FakeTranscriptSegment("Guest-1", "Duplicate"),
            new FakeTranscriptSegment("Guest-1", "Duplicate"),
        };
        state.Reconcile(
            snapshot,
            1,
            value => new() { SpeakerId = value.SpeakerId, Text = value.Text },
            UpdateFakeRow);
        var first = state.Rows[0];
        var duplicateOne = state.Rows[1];
        var duplicateTwo = state.Rows[2];
        collectionChanges = 0;

        var unchanged = state.Reconcile(
            snapshot,
            1,
            value => new() { SpeakerId = value.SpeakerId, Text = value.Text },
            UpdateFakeRow);
        Check.True(!unchanged && collectionChanges == 0);
        Check.True(ReferenceEquals(first, state.Rows[0]));
        Check.True(ReferenceEquals(duplicateOne, state.Rows[1]));
        Check.True(ReferenceEquals(duplicateTwo, state.Rows[2]));

        var corrected = snapshot.ToArray();
        corrected[0] = corrected[0] with { Text = "Corrected first" };
        Check.True(state.Reconcile(
            corrected,
            1,
            value => new() { SpeakerId = value.SpeakerId, Text = value.Text },
            UpdateFakeRow));
        Check.True(ReferenceEquals(first, state.Rows[0]));
        Check.Equal("Corrected first", first.Text);

        var partial = state.UpsertPartial(
            "Guest-2",
            () => new() { SpeakerId = "Guest-2", Text = "Part" },
            row => SetFakeText(row, "Part"));
        var partialRow = partial.Row;
        var finalized = state.ApplyFinal(
            4,
            () => new() { SpeakerId = "Guest-2", Text = "Final" },
            row => SetFakeText(row, "Final"));
        Check.True(finalized.FinalizedPartial);
        Check.True(ReferenceEquals(partialRow, finalized.Row));
        Check.Equal("Final", finalized.Row.Text);

        finalized.Row.Speaker = "Avery";
        Check.True(ReferenceEquals(partialRow, state.Rows[^1]));
        Check.Equal("Avery", state.Rows[^1].Speaker);

        var correctionState = new TranscriptCollectionState<FakeTranscriptRow>(500);
        var finalOne = correctionState.ApplyFinal(
            1,
            () => new() { SpeakerId = "Guest-1", Text = "Original one" },
            row => SetFakeText(row, "Original one")).Row;
        var nextPartial = correctionState.UpsertPartial(
            "Guest-2",
            () => new() { SpeakerId = "Guest-2", Text = "Next partial" },
            row => SetFakeText(row, "Next partial")).Row;
        var correctedOne = correctionState.ApplyFinal(
            1,
            () => new() { SpeakerId = "Guest-1", Text = "Corrected one" },
            row => SetFakeText(row, "Corrected one"));
        Check.True(ReferenceEquals(finalOne, correctedOne.Row));
        Check.Equal("Corrected one", finalOne.Text);
        Check.True(ReferenceEquals(nextPartial, correctionState.Partial));
        Check.Equal("Next partial", nextPartial.Text);
        Check.Equal(2, correctionState.Rows.Count);

        var finalTwo = correctionState.ApplyFinal(
            2,
            () => new() { SpeakerId = "Guest-2", Text = "Final two" },
            row => SetFakeText(row, "Final two"));
        Check.True(finalTwo.FinalizedPartial);
        Check.True(ReferenceEquals(nextPartial, finalTwo.Row));
        Check.Equal("Final two", nextPartial.Text);
        Check.True(correctionState.Partial is null);
        Check.Equal(2, correctionState.Rows.Count);

        state.Reset();
        Check.Equal(0, state.Rows.Count);
        Check.True(state.Partial is null);

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        Check.True(source.Contains("if (_viewport.FollowLatest && _transcript.Count > 0)",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "if (Interlocked.Exchange(ref _transcriptScrollQueued, 1) != 0)",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "Interlocked.Exchange(ref _transcriptScrollQueued, 0);",
            StringComparison.Ordinal));
        Check.True(!source.Contains(
            "TranscriptList.ScrollIntoView(_transcript[^1], ScrollIntoViewAlignment.Leading)",
            StringComparison.Ordinal));
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var partialStart = source.IndexOf("private void OnPartialTranscriptReceived(", StringComparison.Ordinal);
        var queueStart = source.IndexOf("private void QueuePartialTranscriptRender(", partialStart);
        var renderStart = source.IndexOf("private void RenderLatestPartialTranscript(", queueStart);
        var scheduleStart = source.IndexOf("private void SchedulePartialTranscriptRetry(", renderStart);
        var removeStart = source.IndexOf("private void RemovePartialItem(", scheduleStart);
        Check.True(partialStart >= 0 && queueStart > partialStart && renderStart > queueStart &&
            scheduleStart > renderStart && removeStart > scheduleStart);
        var receivePartial = source[partialStart..queueStart];
        Check.True(receivePartial.Contains("string.IsNullOrWhiteSpace(update.Partial.Text)", StringComparison.Ordinal));
        Check.True(receivePartial.Contains("Interlocked.Exchange(ref _pendingPartialTranscript, update);",
            StringComparison.Ordinal));
        var renderPartial = source[renderStart..scheduleStart];
        Check.True(renderPartial.Contains(
            "Interlocked.CompareExchange(ref _pendingPartialTranscript, update, null)",
            StringComparison.Ordinal));
        Check.True(renderPartial.Contains(
            "SchedulePartialTranscriptRetry(TimeSpan.FromMilliseconds(remainingMilliseconds));",
            StringComparison.Ordinal));
        Check.True(renderPartial.Contains("if (result.RequiresFollow)", StringComparison.Ordinal));
        Check.True(!renderPartial.Contains("result.Changed", StringComparison.Ordinal));
        Check.True(!renderPartial.Contains("RemovePartialItem()", StringComparison.Ordinal));
        var retryTimer = source[scheduleStart..removeStart];
        Check.True(retryTimer.Contains("timer.Tick", StringComparison.Ordinal));
        Check.True(retryTimer.Contains("QueuePartialTranscriptRender();", StringComparison.Ordinal));
        var appendStart = source.IndexOf("private void AppendTranscriptSegment(", StringComparison.Ordinal);
        var createTranscriptLineStart = source.IndexOf("private TranscriptLine CreateTranscriptLine(", appendStart);
        Check.True(appendStart >= 0 && createTranscriptLineStart > appendStart);
        var appendFinal = source[appendStart..createTranscriptLineStart];
        Check.True(appendFinal.Contains("if (result.RequiresFollow)", StringComparison.Ordinal));
        Check.True(!appendFinal.Contains("if (result.Changed)", StringComparison.Ordinal));
    }

    private static void TranscriptRefreshPattern()
    {
        var state = new TranscriptCollectionState<FakeTranscriptRow>(500);
        for (var revision = 1; revision <= 7; revision++)
        {
            var avatar = new object();
            var update = state.ApplyFinal(
                revision,
                () => new()
                {
                    SpeakerId = "Guest-1",
                    Text = $"Final {revision}",
                    Avatar = avatar,
                },
                row => SetFakeText(row, $"Final {revision}"));
            Check.True(update.RequiresFollow);
        }

        var stableRows = state.Rows.ToArray();
        var stableAvatars = stableRows.Select(row => row.Avatar).ToArray();
        var collectionChanges = 0;
        state.Rows.CollectionChanged += (_, _) => collectionChanges++;

        var partialAvatar = new object();
        var partial = state.UpsertPartial(
            "Guest-1",
            () => new() { SpeakerId = "Guest-1", Text = "P", Avatar = partialAvatar },
            row => SetFakeText(row, "P"));
        Check.True(partial.Added && partial.RequiresFollow);
        Check.Equal(1, collectionChanges);

        for (var refresh = 1; refresh <= 24; refresh++)
        {
            var text = $"Partial refresh {refresh}";
            var update = state.UpsertPartial(
                "Guest-1",
                () => throw new InvalidOperationException("A repeated partial must reuse its row."),
                row => SetFakeText(row, text));
            Check.True(update.Changed);
            Check.True(!update.Added && !update.RequiresFollow);
            Check.True(ReferenceEquals(partial.Row, update.Row));
            Check.True(ReferenceEquals(partialAvatar, update.Row.Avatar));
            Check.Equal(1, collectionChanges);
            for (var index = 0; index < stableRows.Length; index++)
            {
                Check.True(ReferenceEquals(stableRows[index], state.Rows[index]));
                Check.True(ReferenceEquals(stableAvatars[index], state.Rows[index].Avatar));
            }
        }

        var finalized = state.ApplyFinal(
            8,
            () => throw new InvalidOperationException("Finalization must reuse the provisional row."),
            row => SetFakeText(row, "Final 8"));
        Check.True(finalized.FinalizedPartial && finalized.RequiresFollow);
        Check.True(ReferenceEquals(partial.Row, finalized.Row));
        Check.True(ReferenceEquals(partialAvatar, finalized.Row.Avatar));
        Check.Equal(1, collectionChanges);

        var corrected = state.ApplyFinal(
            8,
            () => throw new InvalidOperationException("A correction must reuse the finalized row."),
            row => SetFakeText(row, "Corrected final 8"));
        Check.True(corrected.Changed);
        Check.True(!corrected.Added && !corrected.FinalizedPartial && !corrected.RequiresFollow);
        Check.True(ReferenceEquals(finalized.Row, corrected.Row));
        Check.True(ReferenceEquals(partialAvatar, corrected.Row.Avatar));
        Check.Equal(1, collectionChanges);

        for (var revision = 9; revision <= 12; revision++)
        {
            var appended = state.ApplyFinal(
                revision,
                () => new()
                {
                    SpeakerId = "Guest-2",
                    Text = $"Final {revision}",
                    Avatar = new object(),
                },
                row => SetFakeText(row, $"Final {revision}"));
            Check.True(appended.Added && appended.RequiresFollow);
            Check.Equal(revision, state.Rows.Count);
            Check.True(ReferenceEquals(appended.Row, state.Rows[^1]));
            for (var index = 0; index < stableRows.Length; index++)
            {
                Check.True(ReferenceEquals(stableRows[index], state.Rows[index]));
                Check.True(ReferenceEquals(stableAvatars[index], state.Rows[index].Avatar));
            }
        }

        Check.Equal(5, collectionChanges);

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(source.Contains(
            "_transcriptScroller.ViewChanged += OnTranscriptViewChanged;",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "if (atBottom && !_viewport.FollowLatest)\n        {\n            _viewport.Latest();\n            RenderTranscriptPage();",
            StringComparison.Ordinal));
        Check.True(!source.Contains(
            "scroller.ScrollableHeight - scroller.VerticalOffset > 80",
            StringComparison.Ordinal));
    }

    private static bool UpdateFakeRow(FakeTranscriptRow row, FakeTranscriptSegment value)
    {
        var changed = row.SpeakerId != value.SpeakerId || row.Text != value.Text;
        row.SpeakerId = value.SpeakerId;
        row.Text = value.Text;
        return changed;
    }

    private static bool SetFakeText(FakeTranscriptRow row, string text)
    {
        var changed = row.Text != text;
        row.Text = text;
        return changed;
    }

    private sealed record FakeTranscriptSegment(string SpeakerId, string Text);

    private sealed class FakeTranscriptRow : ITranscriptRowState
    {
        public long Revision { get; set; }
        public string SpeakerId { get; set; } = string.Empty;
        public string Speaker { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public object Avatar { get; init; } = new();
    }

    private static void WindowBounds()
    {
        var normalWorkArea = new WindowBounds(0, 0, 1920, 1040);
        Check.Equal((600, 900), WindowPlacement.DefaultSize(96, normalWorkArea));
        Check.Equal((600, 900), WindowPlacement.DefaultSize(0, normalWorkArea));
        Check.Equal((900, 1350), WindowPlacement.DefaultSize(144, new(0, 0, 1920, 1440)));

        var resized = WindowPlacement.Anchor(normalWorkArea, 840, 720, 96, taskbarAtTop: false);
        Check.Equal(new WindowBounds(1072, 312, 840, 720), resized);

        var topTaskbar = WindowPlacement.Anchor(new(100, 140, 1200, 760), 700, 650, 96, taskbarAtTop: true);
        Check.Equal(new WindowBounds(592, 148, 700, 650), topTaskbar);

        var smallWorkArea = WindowPlacement.Anchor(new(0, 0, 500, 700), 900, 1000, 144, taskbarAtTop: false);
        Check.Equal(new WindowBounds(12, 12, 476, 676), smallWorkArea);

        Check.True(WindowPlacement.TryExpandForSidePanel(
            resized,
            normalWorkArea,
            340,
            96,
            out var expanded));
        Check.Equal(new WindowBounds(732, 312, 1180, 720), expanded);
        Check.Equal(resized, WindowPlacement.CollapseSidePanel(
            expanded,
            normalWorkArea,
            expanded.Width - resized.Width,
            96));
        Check.True(!WindowPlacement.TryExpandForSidePanel(
            new(8, 8, 1180, 700),
            new(0, 0, 1200, 800),
            340,
            96,
            out _));
    }

    private static void SingleInstanceActivation()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Program.cs.txt"));
        Check.True(source.Contains(
            "private static readonly TimeSpan ActivationRedirectTimeout = TimeSpan.FromSeconds(3);",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "Task.WhenAny(redirect, Task.Delay(ActivationRedirectTimeout))",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "starting a recovery window",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "mainInstance.Activated += OnReactivated;",
            StringComparison.Ordinal));

        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var launchStart = appSource.IndexOf(
            "protected override void OnLaunched(LaunchActivatedEventArgs args)",
            StringComparison.Ordinal);
        var launchEnd = appSource.IndexOf(
            "    private void OnInitialViewReady",
            launchStart,
            StringComparison.Ordinal);
        Check.True(launchStart >= 0 && launchEnd > launchStart);
        var launchSource = appSource[launchStart..launchEnd];
        Check.True(launchSource.Contains("ShowMainWindow();", StringComparison.Ordinal));
        Check.True(!launchSource.Contains("ShowFromTray();", StringComparison.Ordinal));
        Check.True(!launchSource.Contains("BuildTrayIcon();", StringComparison.Ordinal));
        Check.True(launchSource.Contains(
            "_mainWindow.InitialViewReady += OnInitialViewReady;",
            StringComparison.Ordinal));
        Check.True(launchSource.Contains(
            "TryEnqueue(DispatcherQueuePriority.Low, StartDetectorInBackground)",
            StringComparison.Ordinal));
        Check.True(!launchSource.Contains("StartDetectorAsync(", StringComparison.Ordinal));
        Check.True(appSource.Contains(
            "LeftClickCommand = new TrayApp.RelayCommand(() => ShowMainWindow(fromTray: true))",
            StringComparison.Ordinal));
        Check.True(appSource.Contains(
            "MenuFlyoutPresenterStyle = presenterStyle",
            StringComparison.Ordinal));
        Check.True(appSource.Contains(
            "ContextMenuMode = ContextMenuMode.SecondWindow,",
            StringComparison.Ordinal));
        Check.True(!appSource.Contains(
            "ContextMenuMode = ContextMenuMode.PopupMenu,",
            StringComparison.Ordinal));
        Check.True(appSource.Contains(
            "_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, Show)",
            StringComparison.Ordinal));
        Check.True(appSource.Contains(
            "_mainWindow.InitialViewReady -= OnInitialViewReady;",
            StringComparison.Ordinal));
        Check.True(appSource.Contains(
            "if (_trayIcon is null && !_exiting)\n            BuildTrayIcon();",
            StringComparison.Ordinal));

        var mainSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Check.True(mainSource.Contains(
            "public event EventHandler<EventArgs>? InitialViewReady;",
            StringComparison.Ordinal));
        var loadedStart = mainSource.IndexOf(
            "private void OnRootLayoutLoaded",
            StringComparison.Ordinal);
        var loadedEnd = mainSource.IndexOf(
            "private void OnVisualizationViewportChanged",
            loadedStart,
            StringComparison.Ordinal);
        Check.True(loadedStart >= 0 && loadedEnd > loadedStart);
        var loadedSource = mainSource[loadedStart..loadedEnd];
        var layoutIndex = loadedSource.IndexOf("UpdateVisualizationLayout();", StringComparison.Ordinal);
        var readyIndex = loadedSource.IndexOf("InitialViewReady?.Invoke(this, EventArgs.Empty);",
            StringComparison.Ordinal);
        Check.True(layoutIndex >= 0 && readyIndex > layoutIndex);
        Check.True(loadedSource.Contains("if (!_initialViewReadyRaised)", StringComparison.Ordinal));
        var trayStart = mainSource.IndexOf("public void ShowFromTray()", StringComparison.Ordinal);
        var trayEnd = mainSource.IndexOf("private async Task ReleaseSuppressAsync", trayStart,
            StringComparison.Ordinal);
        Check.True(trayStart >= 0 && trayEnd > trayStart);
        var traySource = mainSource[trayStart..trayEnd];
        Check.True(traySource.Contains("_suppressReleaseCts?.Cancel();", StringComparison.Ordinal));
        Check.True(traySource.Contains("_suppressAutoDismiss = true;", StringComparison.Ordinal));
        Check.True(traySource.Contains("AppWindow.Show(activateWindow: true);", StringComparison.Ordinal));
        Check.True(!traySource.Contains("AppWindow.Show(activateWindow: false);", StringComparison.Ordinal));
        Check.True(traySource.Contains(
            "DispatcherQueuePriority.Low,\n                RefreshTodaysMeetings",
            StringComparison.Ordinal));
        var showIndex = traySource.IndexOf("AppWindow.Show(activateWindow: true);", StringComparison.Ordinal);
        var activateIndex = traySource.IndexOf("Activate();", showIndex, StringComparison.Ordinal);
        var foregroundIndex = traySource.IndexOf("NativeMethods.SetForegroundWindow(hwnd);", activateIndex,
            StringComparison.Ordinal);
        Check.True(showIndex >= 0 && activateIndex > showIndex && foregroundIndex > activateIndex);
        Check.True(mainSource.Contains(
            "if (PreferencesPanel.Visibility == Visibility.Visible)\n            {\n                UpdateDiagnostics();",
            StringComparison.Ordinal));
        Check.True(mainSource.Contains(
            "else if (_workIqCts is not null)\n                UpdateWorkIqControls();",
            StringComparison.Ordinal));
    }

    private static void DispatcherCoalescing()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        var partialStart = source.IndexOf(
            "private void OnPartialTranscriptReceived",
            StringComparison.Ordinal);
        var partialEnd = source.IndexOf(
            "private void RemovePartialItem",
            partialStart,
            StringComparison.Ordinal);
        Check.True(partialStart >= 0 && partialEnd > partialStart);
        var partialSource = source[partialStart..partialEnd];
        Check.True(partialSource.Contains(
            "Interlocked.Exchange(ref _pendingPartialTranscript, update);",
            StringComparison.Ordinal));
        Check.True(partialSource.Contains(
            "Interlocked.Exchange(ref _partialUiUpdateQueued, 1)",
            StringComparison.Ordinal));
        Check.True(!partialSource.Contains("InvokeOnUi(", StringComparison.Ordinal));
        Check.True(partialSource.Contains(
            "RenderLatestPartialTranscript",
            StringComparison.Ordinal));

        var aiUsageStart = source.IndexOf(
            "private void OnAiUsageChanged",
            StringComparison.Ordinal);
        var transcriptStart = source.IndexOf(
            "private void OnTranscriptSegmentReceived",
            aiUsageStart,
            StringComparison.Ordinal);
        Check.True(aiUsageStart >= 0 && transcriptStart > aiUsageStart);
        var aiUsageSource = source[aiUsageStart..transcriptStart];
        Check.True(aiUsageSource.Contains(
            "Interlocked.Exchange(ref _aiUsageRenderQueued, 1)",
            StringComparison.Ordinal));
        Check.True(!aiUsageSource.Contains("InvokeOnUi(", StringComparison.Ordinal));
    }

    private static void VisualizationExpansion()
    {
        foreach (var (width, normalPrimary, normalMap, expandedPrimary, expandedMap) in new[]
                 {
                     (900d, 480d, 420d, 480d, 420d),
                     (1200d, 696d, 504d, 480d, 720d),
                     (1600d, 928d, 672d, 480d, 1120d),
                     (2496d, 1776d, 720d, 480d, 2016d),
                 })
        {
            Check.Equal(
                new VisualizationLayoutAllocation(normalPrimary, normalMap),
                VisualizationLayout.Calculate(width, expanded: false));
            Check.Equal(
                new VisualizationLayoutAllocation(expandedPrimary, expandedMap),
                VisualizationLayout.Calculate(width, expanded: true));
        }
        Check.Equal(new VisualizationLayoutAllocation(240d, 210d),
            VisualizationLayout.Calculate(450d, expanded: true));
        foreach (var width in new[] { 600d, 900d, 1280d, 1920d, 2560d })
        {
            var expanded = VisualizationLayout.Calculate(width, expanded: true);
            Check.True(expanded.PrimaryWidth > 0);
            Check.True(expanded.VisualizationWidth > 0);
            Check.Equal(width, expanded.PrimaryWidth + expanded.VisualizationWidth);
        }
        Check.Equal(480d, VisualizationLayout.MinimumPrimaryWidth);

        var current = new System.Collections.ObjectModel.ObservableCollection<FakeVisualizationItem>
        {
            new("topic-a", "Alpha"),
            new("topic-b", "Beta"),
        };
        var originalFirst = current[0];
        var collectionChanges = 0;
        current.CollectionChanged += (_, _) => collectionChanges++;
        var unchanged = VisualizationCollectionState.Reconcile(
            current,
            [new("topic-a", "Alpha"), new("topic-b", "Beta")],
            item => item.Id,
            item => item.Text);
        Check.Equal(0, unchanged.Added.Count);
        Check.Equal(0, unchanged.Changed.Count);
        Check.Equal(0, collectionChanges);
        Check.True(ReferenceEquals(originalFirst, current[0]));

        var updated = VisualizationCollectionState.Reconcile(
            current,
            [new("topic-a", "Alpha revised"), new("topic-b", "Beta"), new("topic-c", "Gamma")],
            item => item.Id,
            item => item.Text);
        Check.Equal("topic-c", updated.Added.Single().Id);
        Check.Equal("topic-a", updated.Changed.Single().Id);
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expandButton = xaml.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "VisualizationExpandButton");
        var closeButton = xaml.Descendants(presentation + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "VisualizationCloseButton");
        var expandIcon = xaml.Descendants(presentation + "FontIcon")
            .Single(element => (string?)element.Attribute(x + "Name") == "VisualizationExpandIcon");
        var expandText = xaml.Descendants(presentation + "TextBlock")
            .Single(element => (string?)element.Attribute(x + "Name") == "VisualizationExpandText");
        Check.Equal("OnVisualizationExpandClick", (string?)expandButton.Attribute("Click"));
        Check.Equal("Expand visualization to the window",
            (string?)expandButton.Attribute("AutomationProperties.Name"));
        Check.Equal("88", (string?)expandButton.Attribute("MinWidth"));
        Check.Equal("1", (string?)expandButton.Attribute("Grid.Column"));
        Check.Equal("2", (string?)closeButton.Attribute("Grid.Column"));
        Check.True(ReferenceEquals(expandButton.Parent, closeButton.Parent));
        Check.True(expandIcon.Ancestors().Contains(expandButton));
        Check.True(expandText.Ancestors().Contains(expandButton));
        Check.Equal("\uE740", (string?)expandIcon.Attribute("Glyph"));
        Check.Equal("Expand", (string?)expandText.Attribute("Text"));
        Check.Equal(3, expandButton.Parent!.Elements(presentation + "Grid.ColumnDefinitions")
            .Single().Elements(presentation + "ColumnDefinition").Count());
        Check.Equal("32", (string?)closeButton.Attribute("Width"));
        Check.Equal("32", (string?)closeButton.Attribute("Height"));
        Check.Equal("Close visualization",
            (string?)closeButton.Attribute("AutomationProperties.Name"));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        Check.True(source.Contains("new UISettings().AnimationsEnabled", StringComparison.Ordinal));
        Check.True(source.Contains("VisualizationExpandText.Text = \"Restore\";", StringComparison.Ordinal));
        Check.True(source.Contains("VisualizationExpandIcon.Glyph = \"\\uE73F\";", StringComparison.Ordinal));
        Check.True(source.Contains("private void AnimateChangedItems<T>", StringComparison.Ordinal));
        Check.True(source.Contains("AnimateChangedItems(TrailList, trailUpdate.Changed)", StringComparison.Ordinal));
        Check.True(source.Contains("if (!_animationsEnabled || changed.Count == 0)", StringComparison.Ordinal));
        Check.True(source.Contains("AnimateOpacity(container, 0.68, 1, 160)", StringComparison.Ordinal));
        Check.True(source.Contains(
            "TrailList.ItemContainerTransitions = [new AddDeleteThemeTransition()]",
            StringComparison.Ordinal));
        Check.True(source.Contains("RootLayout.XamlRoot?.Size.Width", StringComparison.Ordinal));
        Check.True(source.Contains("SetColumnWidth(", StringComparison.Ordinal));
        Check.True(source.Contains("PrimaryContentColumn,", StringComparison.Ordinal));
        Check.True(source.Contains("TopicMapColumn,", StringComparison.Ordinal));
        Check.True(source.Contains(
            "new GridLength(allocation.PrimaryWidth)",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "new GridLength(1, GridUnitType.Star)",
            StringComparison.Ordinal));
        Check.True(source.Contains("private void UpdateTopicDetailLayout(double totalWidth)",
            StringComparison.Ordinal));
        Check.True(source.Contains("var wide = totalWidth >= 1380;",
            StringComparison.Ordinal));
        Check.True(source.Contains("TopicDetailColumn.Width = wide ? new GridLength(360)",
            StringComparison.Ordinal));
        Check.True(source.Contains("Grid.SetRow(TopicDetailPanel, wide ? 1 : 2);",
            StringComparison.Ordinal));
        Check.True(source.Contains("TopicDetailView.MaxHeight = wide ? double.PositiveInfinity : 300;",
            StringComparison.Ordinal));
        Check.True(!source.Contains(
            "VisualizationLayout.Calculate(RootLayout.ActualWidth",
            StringComparison.Ordinal));

        var expandStart = source.IndexOf("private void OnVisualizationExpandClick(", StringComparison.Ordinal);
        var restoreStart = source.IndexOf("private void RestoreVisualizationLayout()", expandStart,
            StringComparison.Ordinal);
        var layoutStart = source.IndexOf("private void UpdateVisualizationLayout()", restoreStart,
            StringComparison.Ordinal);
        Check.True(expandStart >= 0 && restoreStart > expandStart && layoutStart > restoreStart);
        var expandHandler = source[expandStart..restoreStart];
        var restoreHandler = source[restoreStart..layoutStart];
        var expandAndRestore = source[expandStart..layoutStart];
        Check.True(expandAndRestore.Contains("RestoreVisualizationLayout();", StringComparison.Ordinal));
        Check.True(Regex.IsMatch(
            expandHandler,
            @"if \(_presenter\.State == Microsoft\.UI\.Windowing\.OverlappedPresenterState\.Restored\)\s*\{\s*_presenter\.Maximize\(\);\s*_visualizationMaximizedWindow = true;\s*\}",
            RegexOptions.CultureInvariant));
        Check.True(Regex.IsMatch(
            restoreHandler,
            @"if \(_visualizationMaximizedWindow\)\s*\{\s*_presenter\.Restore\(\);\s*_visualizationMaximizedWindow = false;\s*\}",
            RegexOptions.CultureInvariant));
        Check.True(!expandAndRestore.Contains("restoreWindow", StringComparison.Ordinal));
        Check.True(!expandAndRestore.Contains("MoveAndResize", StringComparison.Ordinal));
        Check.True(!expandAndRestore.Contains("CurrentWorkArea", StringComparison.Ordinal));
        Check.True(!expandAndRestore.Contains("CurrentWindowBounds", StringComparison.Ordinal));
        Check.True(!expandAndRestore.Contains("Focus(FocusState.Programmatic)", StringComparison.Ordinal));
        Check.True(!source.Contains("_visualizationRestoreBounds", StringComparison.Ordinal));
    }

    private sealed record FakeVisualizationItem(string Id, string Text);

    private static void RecordingSpace()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = source.IndexOf("private void UpdateRecordingLayout()", StringComparison.Ordinal);
        var end = source.IndexOf("private void OnChecklistItemToggled(", start, StringComparison.Ordinal);
        Check.True(start >= 0 && end > start);
        var layout = source[start..end];
        Check.True(layout.Contains(
            "if (Math.Abs(RecordingOverviewScroll.MaxHeight - allocation.OverviewMaxHeight) > 1)\n" +
            "            RecordingOverviewScroll.MaxHeight = allocation.OverviewMaxHeight;",
            StringComparison.Ordinal));
        Check.True(layout.Contains(
            "if (Math.Abs(RecordingTranscriptRow.MinHeight - allocation.TranscriptMinHeight) > 1)\n" +
            "            RecordingTranscriptRow.MinHeight = allocation.TranscriptMinHeight;",
            StringComparison.Ordinal));

        var normal = RecordingLayout.Calculate(750, 140);
        Check.True(normal.TranscriptMinHeight >= 140);
        Check.True(normal.OverviewMaxHeight + normal.TranscriptMinHeight <= 610);
        Check.Equal(RecordingLayout.DesiredOverviewHeight, normal.OverviewMaxHeight);

        var small = RecordingLayout.Calculate(500, 140);
        Check.True(small.TranscriptMinHeight >= RecordingLayout.MinimumTranscriptHeight);
        Check.True(small.OverviewMaxHeight + small.TranscriptMinHeight <= 360);
        Check.True(small.OverviewMaxHeight >= 200);
    }

    private static void ChecklistCompletion()
    {
        var item = new ChecklistItemViewModel { Text = "Follow up" };
        var changes = new List<string?>();
        item.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        item.IsCompleted = true;
        Check.True(item.IsCompleted);
        Check.Equal(0.6, item.TextOpacity);
        Check.Equal("Questions (0 open, 1 done)", ChecklistPresentation.Header("Questions", [item]));
        Check.True(changes.Contains(nameof(ChecklistItemViewModel.IsCompleted)));

        var retained = new Dictionary<string, ChecklistItemViewModel>(StringComparer.OrdinalIgnoreCase)
        {
            [item.Text] = item
        };
        Check.Equal("New action", string.Join("|",
            InsightRetention.NewItems(retained.Keys, ["follow up", "New action"])));

        item.IsCompleted = false;
        Check.True(!item.IsCompleted);
        Check.Equal(1.0, item.TextOpacity);
        Check.Equal("Questions (1 open, 0 done)", ChecklistPresentation.Header("Questions", [item]));
    }

    private static void AiActivity()
    {
        var now = DateTimeOffset.UtcNow;
        var active = AiUsagePresentation.Create(new(
            "session",
            now.AddMinutes(-1),
            now,
            [
                Usage(
                    AiCapability.Visualization,
                    requests: 1,
                    calls: 2,
                    input: new(1234, 1, 1),
                    output: new(0, 0, 2),
                    cacheRead: new(20, 1, 1),
                    cacheWrite: new(10, 1, 1),
                    reasoning: new(5, 1, 1),
                    providerCost: new(new(1.5, 1, 1), "USD", "request"),
                    nanoAiu: new(420.25, 1, 1),
                    quota: new(
                        [new(
                            "premium_interactions",
                            300,
                            false,
                            0,
                            false,
                            86,
                            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
                            false,
                            42)],
                        1,
                        1))
            ],
            [
                new(
                    1,
                    AiCapability.Visualization,
                    AiRequestState.Running,
                    now.AddSeconds(-12),
                    null,
                    TimeSpan.FromSeconds(12),
                    2,
                    AiRequestFailure.None,
                    null,
                    1234,
                    null,
                    null,
                    1_500_000_000,
                    EmptyQuota())
            ]));
        Check.Equal(AiActivityVisualState.Active, active.State);
        Check.Equal("AI active · Topics · 12s", active.Header);
        Check.Equal("AI is active: Topic analysis.", active.Announcement);
        var elapsedOnly = AiUsagePresentation.Create(new(
            "session",
            now.AddMinutes(-1),
            now.AddSeconds(1),
            [
                Usage(
                    AiCapability.Visualization,
                    requests: 1,
                    calls: 2,
                    input: new(1234, 1, 1),
                    output: new(0, 0, 2),
                    providerCost: new(new(1.5, 1, 1), "USD", "request"),
                    nanoAiu: new(420.25, 1, 1))
            ],
            [
                new(
                    1,
                    AiCapability.Visualization,
                    AiRequestState.Running,
                    now.AddSeconds(-12),
                    null,
                    TimeSpan.FromSeconds(13),
                    2,
                    AiRequestFailure.None,
                    null,
                    1234,
                    null,
                    null,
                    1_500_000_000,
                    EmptyQuota())
            ]));
        Check.Equal(active.TransitionKey, elapsedOnly.TransitionKey);
        Check.Equal(active.Announcement, elapsedOnly.Announcement);
        Check.Equal("AI active · Topics · 13s", elapsedOnly.Header);

        var unreported = AiUsagePresentation.Create(new(
            "session",
            now,
            now,
            [Usage(AiCapability.WorkIqRetrieval, requests: 1, calls: 1)],
            [
                new(
                    2,
                    AiCapability.WorkIqRetrieval,
                    AiRequestState.SucceededEmpty,
                    now,
                    now,
                    TimeSpan.Zero,
                    1,
                    AiRequestFailure.None,
                    null,
                    null,
                    null,
                    null,
                    null,
                    EmptyQuota())
            ]));
        Check.Equal(AiActivityVisualState.Idle, unreported.State);

        var limited = AiUsagePresentation.Create(new(
            "session",
            now,
            now,
            [Usage(AiCapability.Intelligence, requests: 1, calls: 1, rateLimited: 1)],
            [
                new(
                    3,
                    AiCapability.Intelligence,
                    AiRequestState.RateLimited,
                    now,
                    now,
                    TimeSpan.Zero,
                    1,
                    AiRequestFailure.RateLimited,
                    TimeSpan.FromSeconds(30),
                    null,
                    null,
                    null,
                    null,
                    EmptyQuota())
            ]));
        Check.Equal(AiActivityVisualState.RateLimited, limited.State);
        Check.Equal("AI rate limited · Q&A", limited.Header);
        Check.Equal("AI is rate limited for Questions and actions. Retry after 30 sec.", limited.Status);

        var unavailable = AiUsagePresentation.Create(new(
            "session",
            now,
            now,
            [Usage(AiCapability.Synthesis, requests: 1, calls: 1, failed: 1)],
            [
                new(
                    4,
                    AiCapability.Synthesis,
                    AiRequestState.Failed,
                    now,
                    now,
                    TimeSpan.Zero,
                    1,
                    AiRequestFailure.Unsupported,
                    null,
                    null,
                    null,
                    null,
                    null,
                    EmptyQuota())
            ]));
        Check.Equal(AiActivityVisualState.Unavailable, unavailable.State);
        Check.Equal("AI unavailable · Notes", unavailable.Header);

        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Control(string name) => document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == name);
        Check.Equal("150", (string?)Control("AiActivityIndicator").Attribute("MaxWidth"));
        Check.Equal("Polite",
            (string?)Control("AiActivityAnnouncementText").Attribute("AutomationProperties.LiveSetting"));
        Check.True(Control("AiActivityIndicator").Descendants()
            .All(element => element.Name.LocalName != "ProgressRing"));
        // The flyout carries one status line; the full metric breakdown lives in AI diagnostics.
        Check.True(Control("AiUsageStatusText").Ancestors().Contains(Control("ContextFlyout")));
        Check.True(!Control("ContextFlyout").Descendants().Any(element =>
            element.Name.LocalName == "ItemsControl" &&
            (string?)element.Attribute("ItemsSource") == "{x:Bind}"));
        Check.True(Control("AiDiagnosticsList").Ancestors().Contains(Control("AiDiagnosticsExpander")));
        Check.True(!Control("AiDiagnosticsList").Ancestors().Contains(Control("ContextFlyout")));
        Check.True(Control("AiDiagnosticsList").Descendants().Any(element =>
            (string?)element.Attribute("Text") == "{Binding Quota}"));
        Check.True(Control("AiDiagnosticsList").Descendants().Any(element =>
            (string?)element.Attribute("Text") == "{Binding Usage}"));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        Check.True(source.Contains("_pipeline.AiUsage.Changed += OnAiUsageChanged;", StringComparison.Ordinal));
        Check.True(source.Contains("_pipeline.AiUsage.Changed -= OnAiUsageChanged;", StringComparison.Ordinal));
        Check.True(source.Contains("if (_pipeline.AiUsage.GetSnapshot().HasActiveRequests)", StringComparison.Ordinal));
        Check.True(source.Contains("RenderAiUsage(refreshDetails: false, announceTransition: false);",
            StringComparison.Ordinal));
        Check.True(source.Contains(
            "string.Equals(_aiUsageTransitionKey, presentation.TransitionKey",
            StringComparison.Ordinal));
    }

    private static void AiDiagnostics()
    {
        var now = new DateTimeOffset(2026, 9, 24, 1, 30, 0, TimeSpan.FromHours(-7));
        var presented = AiDiagnosticsPresentation.Event(new(
            7,
            "session",
            42,
            "correlation-42",
            19,
            now,
            TimeSpan.FromMilliseconds(1250),
            AiCapability.WorkIqRetrieval,
            "WorkIQ",
            null,
            AiDiagnosticEventKind.Failed,
            AiRequestState.RateLimited,
            AiRequestFailure.RateLimited,
            TimeSpan.FromSeconds(30),
            2,
            1200,
            null,
            0.25,
            75.5,
            [new(
                "premium",
                300,
                false,
                0,
                false,
                86,
                now.AddDays(1),
                false,
                42)],
            "prompt",
            "request",
            "response",
            true));
        Check.Equal("Related suggestions", presented.Capability);
        Check.Equal("WorkIQ · Model not reported", presented.ProviderModel);
        Check.True(presented.Status.Contains("RateLimited", StringComparison.Ordinal));
        Check.True(presented.Status.Contains("retry after 30 s", StringComparison.Ordinal));
        Check.True(presented.RequestIdentity.Contains("Request 42", StringComparison.Ordinal));
        Check.True(presented.RequestIdentity.Contains("Revision 19", StringComparison.Ordinal));
        Check.True(presented.Usage.Contains("Input tokens: 1,200", StringComparison.Ordinal));
        Check.True(presented.Usage.Contains("Output tokens: Not reported", StringComparison.Ordinal));
        Check.True(presented.Usage.Contains(
            "Cached read tokens: Not reported",
            StringComparison.Ordinal));
        Check.True(presented.Usage.Contains(
            "Cached write tokens: Not reported",
            StringComparison.Ordinal));
        Check.True(presented.Usage.Contains(
            "Reasoning tokens: Not reported",
            StringComparison.Ordinal));
        Check.True(presented.Usage.Contains(
            "Provider cost (SDK-reported): 0.25",
            StringComparison.Ordinal));
        Check.True(presented.Usage.Contains(
            "Calculated cost: Not reported (required token rates are not reported)",
            StringComparison.Ordinal));
        Check.True(presented.Usage.Contains(
            "Total Nano AIUs: 75.5 · AIC equivalent: 0.0000000755 AIC · " +
            "AIC USD equivalent: $0.000000000755 USD",
            StringComparison.Ordinal));
        Check.True(presented.Quota.Contains("42/300 requests", StringComparison.Ordinal));
        Check.True(presented.HasSensitivePayload);
        Check.True(presented.PayloadTruncated);

        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Control(string name) => document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == name);
        Check.Equal("AI diagnostics", (string?)Control("AiDiagnosticsExpander").Attribute("Header"));
        Check.Equal("Capture sensitive prompt and response content",
            (string?)Control("AiDiagnosticsSensitiveToggle").Attribute("Header"));
        Check.Equal("OnAiDiagnosticsSensitiveToggle",
            (string?)Control("AiDiagnosticsSensitiveToggle").Attribute("Toggled"));
        Check.Equal("OnAiDiagnosticsClearClick",
            (string?)Control("AiDiagnosticsClearButton").Attribute("Click"));
        Check.Equal("460", (string?)Control("AiDiagnosticsList").Attribute("MaxHeight"));
        Check.Equal("None", (string?)Control("AiDiagnosticsList").Attribute("SelectionMode"));
        Check.Equal("AI diagnostic event timeline",
            (string?)Control("AiDiagnosticsList").Attribute("AutomationProperties.Name"));
        Check.Equal("Polite",
            (string?)Control("AiDiagnosticsAnnouncementText").Attribute("AutomationProperties.LiveSetting"));
        Check.True(document.Descendants().Any(element =>
            ((string?)element.Attribute("Text"))?.Contains(
                "may include private meeting or Microsoft 365 content",
                StringComparison.Ordinal) == true));
        foreach (var name in new[]
                 {
                     "Captured prompt payload",
                     "Captured request payload",
                     "Captured response payload",
                 })
        {
            var payload = document.Descendants().Single(element =>
                (string?)element.Attribute("AutomationProperties.Name") == name);
            Check.Equal("True", (string?)payload.Attribute("IsReadOnly"));
            Check.Equal("True", (string?)payload.Attribute("AcceptsReturn"));
            Check.Equal("Cascadia Mono, Consolas", (string?)payload.Attribute("FontFamily"));
        }

        var settingsSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "MainWindow.Settings.cs.txt"));
        Check.True(settingsSource.Contains(
            "_pipeline.AiDiagnostics.SetSensitiveContentCapture(enabled);",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "Interlocked.Exchange(ref _aiDiagnosticsRenderQueued, 1) != 0",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "PreferencesPanel.Visibility == Visibility.Visible",
            StringComparison.Ordinal));
        Check.True(settingsSource.Contains("_pipeline.AiDiagnostics.Clear();", StringComparison.Ordinal));
        Check.True(settingsSource.Contains("_pipeline.AiDiagnostics.GetSnapshot();", StringComparison.Ordinal));
        Check.True(settingsSource.Contains(".OrderBy(item => item.Sequence)", StringComparison.Ordinal));
        Check.True(settingsSource.Contains(
            "SensitivePayloadVisibility = showSensitive ? Visibility.Visible : Visibility.Collapsed;",
            StringComparison.Ordinal));
        var diagnosticsStart = settingsSource.IndexOf(
            "private void OnAiDiagnosticsChanged", StringComparison.Ordinal);
        var diagnosticsEnd = settingsSource.IndexOf(
            "private void CancelSettingsEditor", diagnosticsStart, StringComparison.Ordinal);
        Check.True(diagnosticsStart >= 0 && diagnosticsEnd > diagnosticsStart);
        var diagnosticsSource = settingsSource[diagnosticsStart..diagnosticsEnd];
        Check.True(!diagnosticsSource.Contains("_settingsManager", StringComparison.Ordinal));
        Check.True(!diagnosticsSource.Contains("NotesPath", StringComparison.Ordinal));
        Check.True(!diagnosticsSource.Contains("WriteAll", StringComparison.Ordinal));

        var mainSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml.cs.txt"));
        Check.True(mainSource.Contains(
            "_pipeline.AiDiagnostics.Changed += OnAiDiagnosticsChanged;",
            StringComparison.Ordinal));
        Check.True(mainSource.Contains(
            "_pipeline.AiDiagnostics.Changed -= OnAiDiagnosticsChanged;",
            StringComparison.Ordinal));
    }

    private static AiCapabilityUsage Usage(
        AiCapability capability,
        int requests,
        int calls,
        int failed = 0,
        int rateLimited = 0,
        AiReportedLong? input = null,
        AiReportedLong? output = null,
        AiReportedLong? cacheRead = null,
        AiReportedLong? cacheWrite = null,
        AiReportedLong? reasoning = null,
        AiProviderCost? providerCost = null,
        AiReportedDouble? nanoAiu = null,
        AiQuotaSnapshots? quota = null) =>
        new(
            capability,
            requests,
            calls,
            0,
            0,
            failed,
            0,
            rateLimited,
            input ?? new(0, 0, calls),
            output ?? new(0, 0, calls),
            providerCost ?? new(new(0, 0, calls), null, null),
            nanoAiu ?? new(0, 0, calls),
            quota ?? EmptyQuota(),
            cacheRead ?? new(0, 0, calls),
            cacheWrite ?? new(0, 0, calls),
            reasoning ?? new(0, 0, calls));

    private static AiQuotaSnapshots EmptyQuota() => new([], 0, 0);
}
