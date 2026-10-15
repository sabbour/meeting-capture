using MeetingCapture.Pipeline;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingCapture.TrayApp;

public sealed partial class MainWindow
{
    private bool _combineSpeakersDialogOpen;

    /// <summary>Recomputes every speaker's timeline row from the pipeline transcript, updating rows in place.</summary>
    private void RefreshSpeakerTimeline()
    {
        if (_sessionId is null ||
            (_pipeline.CurrentTranscript ?? _pipeline.LastCompletedTranscript) is not { } transcript ||
            transcript.SessionId != _sessionId)
            return;

        var model = SpeakerTimeline.Compute(
            transcript.Segments, transcript.StartedAt, transcript.EndedAt, DateTimeOffset.UtcNow);
        var resolver = _pipeline.SpeakerResolver;
        var existing = _speakerBalance.ToDictionary(row => row.SpeakerId, StringComparer.OrdinalIgnoreCase);
        var rows = new List<SpeakerBalance>(model.Rows.Count);
        foreach (var item in model.Rows)
        {
            if (!existing.TryGetValue(item.SpeakerId, out var row))
                row = new SpeakerBalance { SpeakerId = item.SpeakerId };
            var name = resolver.GetDisplayName(item.SpeakerId);
            var avatar = SpeakerAvatarPresentation.Create(name, resolver.IsResolved(item.SpeakerId));
            row.Name = name;
            row.AvatarInitials = avatar.Initials;
            row.AvatarAccessibleName = avatar.AccessibleName;
            row.AvatarImage = _speakerAvatarImages.GetValueOrDefault(item.SpeakerId);
            row.Brush = GetSpeakerBrush(item.SpeakerId);
            row.Spans = item.Spans;
            row.TalkTimeText = $"{name}: {FormatTalkTime(item.TalkTime)} speaking";
            rows.Add(row);
        }

        if (_speakerBalance.Select(row => row.SpeakerId).SequenceEqual(
                rows.Select(row => row.SpeakerId), StringComparer.OrdinalIgnoreCase))
            return;
        _speakerBalance.Clear();
        foreach (var row in rows)
            _speakerBalance.Add(row);
    }

    private static string FormatTalkTime(TimeSpan time) =>
        time.TotalMinutes < 1 ? $"{Math.Max(0, (int)Math.Round(time.TotalSeconds))} s"
            : $"{(int)time.TotalMinutes} min {time.Seconds} s";

    private void UpdateCombineSpeakersButton()
    {
        var canCombine = _pipeline.State is PipelineState.Recording or PipelineState.Saved or
            PipelineState.Failed or PipelineState.Idle && !_retryBusy && !_preparingExit &&
            _speakerBalance.Count(row => row.SpeakerId != SpeakerTimeline.UnknownSpeakerId) >= 2;
        CombineSpeakersButton.Visibility = canCombine ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCombineSpeakersClick(object sender, RoutedEventArgs e)
    {
        if (_combineSpeakersDialogOpen || RootLayout.XamlRoot is not { } xamlRoot) return;
        var candidates = _speakerBalance.Where(row => row.SpeakerId != SpeakerTimeline.UnknownSpeakerId).ToList();
        if (candidates.Count < 2) return;

        var resolver = _pipeline.SpeakerResolver;
        var checks = candidates.Select(row => new CheckBox { Content = row.Name, Tag = row }).ToList();
        var keep = new ComboBox { Header = "Keep this name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Combine speakers",
            PrimaryButtonText = "Combine",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        List<SpeakerBalance> Chosen() => checks.Where(check => check.IsChecked == true)
            .Select(check => (SpeakerBalance)check.Tag).ToList();

        void Sync()
        {
            var chosen = Chosen();
            var previous = (keep.SelectedItem as ComboBoxItem)?.Tag as SpeakerBalance;
            keep.Items.Clear();
            foreach (var row in chosen)
                keep.Items.Add(new ComboBoxItem { Content = row.Name, Tag = row });
            var preferred = chosen.FirstOrDefault(row => ReferenceEquals(row, previous)) ??
                chosen.FirstOrDefault(row => resolver.IsResolved(row.SpeakerId)) ?? chosen.FirstOrDefault();
            if (preferred is not null) keep.SelectedIndex = chosen.IndexOf(preferred);
            keep.IsEnabled = chosen.Count >= 2;
            dialog.IsPrimaryButtonEnabled = chosen.Count >= 2;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Pick two or more speakers to merge into one. Their transcript lines and timeline move to the name you keep.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var check in checks)
        {
            check.Checked += (_, _) => Sync();
            check.Unchecked += (_, _) => Sync();
            content.Children.Add(check);
        }
        content.Children.Add(keep);
        dialog.Content = new ScrollViewer { Content = content, MaxHeight = 360 };
        Sync();

        _combineSpeakersDialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _combineSpeakersDialogOpen = false; }
        if (result != ContentDialogResult.Primary ||
            (keep.SelectedItem as ComboBoxItem)?.Tag is not SpeakerBalance kept)
            return;

        await CombineSpeakersAsync(Chosen().Select(row => row.SpeakerId).ToArray(), kept.SpeakerId);
    }

    private async Task CombineSpeakersAsync(IReadOnlyList<string> speakerIds, string keptId)
    {
        var plan = SpeakerMergePlan.Create(speakerIds, keptId);
        if (plan is null) return;
        var sessionId = _sessionId;
        Exception? failure = null;
        try { await _pipeline.MergeSpeakersAsync(speakerIds, keptId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex; }

        var resolver = _pipeline.SpeakerResolver;
        var merged = plan.AbsorbedIds.All(id =>
            string.Equals(resolver.Canonical(id), plan.KeptId, StringComparison.OrdinalIgnoreCase));
        if (merged && sessionId == _sessionId)
            ApplySpeakerMergeToUi(plan);
        if (failure is null) return;

        _logger.LogWarning("Combining speakers did not complete cleanly ({ErrorType}).", failure.GetType().Name);
        ShowStatusBar(
            merged
                ? "Speakers were combined, but the saved meeting could not be updated. Try again after the meeting is saved."
                : failure is InvalidOperationException
                    ? "Speakers can be combined while recording or after the meeting is saved."
                    : "Speakers could not be combined.",
            InfoBarSeverity.Warning);
    }

    private void ApplySpeakerMergeToUi(SpeakerMergePlan plan)
    {
        var resolver = _pipeline.SpeakerResolver;
        var display = resolver.GetDisplayName(plan.KeptId);
        var avatar = SpeakerAvatarPresentation.Create(display, resolver.IsResolved(plan.KeptId));
        var brush = GetSpeakerBrush(plan.KeptId);
        var image = _speakerAvatarImages.GetValueOrDefault(plan.KeptId);

        var lines = _transcript.ToList();
        if (_partialItem is { } partial && !lines.Contains(partial)) lines.Add(partial);
        foreach (var line in lines.Where(line => plan.Absorbs(line.SpeakerId)))
        {
            line.SpeakerId = plan.KeptId;
            line.Speaker = line.Opacity < 1 ? $"{display} (provisional)" : display;
            line.SpeakerBrush = brush;
            line.AvatarInitials = avatar.Initials;
            line.AvatarAccessibleName = avatar.AccessibleName;
            line.AvatarImage = image;
        }

        plan.MergeCounts(_speakerWordCounts);
        foreach (var id in plan.AbsorbedIds)
        {
            // Bumping the revision discards any profile photo still loading for the absorbed speaker.
            _speakerAvatarRevisions[id] = _speakerAvatarRevisions.GetValueOrDefault(id) + 1;
            _speakerAvatarImages.Remove(id);
        }
        if (_lastFinalSpeakerId is not null) _lastFinalSpeakerId = plan.Remap(_lastFinalSpeakerId);

        var pending = _pendingUnknownSpeakers.Where(id => !plan.Absorbs(id)).ToArray();
        _pendingUnknownSpeakers.Clear();
        foreach (var id in pending) _pendingUnknownSpeakers.Enqueue(id);
        if (_currentPromptSpeakerId is not null && plan.Absorbs(_currentPromptSpeakerId))
        {
            _currentPromptSpeakerId = null;
            SpeakerNamingPanel.Visibility = Visibility.Collapsed;
            _suppressAutoDismiss = false;
            ShowNextSpeakerPrompt();
        }

        RefreshSpeakerTimeline();
        RefreshVisualization();
        UpdateRecordingPresentation();
    }
}
