using MeetingCapture.Pipeline;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeetingCapture.TrayApp;

/// <summary>Speaker timeline part of the main window.</summary>
public sealed partial class MainWindow
{
    private bool _combineSpeakersDialogOpen;

    /// <summary>Recomputes every speaker's timeline row from the pipeline transcript, updating rows in place.</summary>
    private void RefreshSpeakerTimeline()
    {
        // Use only transcript data belonging to the active UI session; a late completion from another session is ignored.
        if (_sessionId is null ||
            (_pipeline.CurrentTranscript ?? _pipeline.LastCompletedTranscript) is not { } transcript ||
            transcript.SessionId != _sessionId)
            return;

        var model = SpeakerTimeline.Compute(
            transcript.Segments, transcript.StartedAt, transcript.EndedAt, DateTimeOffset.UtcNow);
        var resolver = _pipeline.SpeakerResolver;
        var existing = _speakerBalance.ToDictionary(row => row.SpeakerId, StringComparer.OrdinalIgnoreCase);
        var rows = new List<SpeakerBalance>(model.Rows.Count);
        // Reuse row view models so changing spans or a resolved name does not rebuild the whole panel.
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

        // Rows are collected first; the bound collection is only touched when the speaker set or order changed.
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
        // Merging is offered in states where the transcript can be updated, and needs at least two speaker ids other
        // than the shared Unknown placeholder (which is not a distinct identity); those ids may still be unresolved.
        var canCombine = _pipeline.State is PipelineState.Recording or PipelineState.Saved or
            PipelineState.Failed or PipelineState.Idle && !_retryBusy && !_preparingExit &&
            _speakerBalance.Count(row => row.SpeakerId != SpeakerTimeline.UnknownSpeakerId) >= 2;
        CombineSpeakersButton.Visibility = canCombine ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCombineSpeakersClick(object sender, RoutedEventArgs e)
    {
        // Guard against re-entry (a dialog is already open) and against a window that has no XamlRoot yet.
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
            // Preserve the user's current choice when possible; otherwise keep a resolved name before an unknown label.
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

        // Block a second dialog while this one is awaiting a result.
        _combineSpeakersDialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _combineSpeakersDialogOpen = false; }
        // Cancel/close, or a missing "keep" selection, leaves everything untouched.
        if (result != ContentDialogResult.Primary ||
            (keep.SelectedItem as ComboBoxItem)?.Tag is not SpeakerBalance kept)
            return;

        await CombineSpeakersAsync(Chosen().Select(row => row.SpeakerId).ToArray(), kept.SpeakerId);
    }

    private async Task CombineSpeakersAsync(IReadOnlyList<string> speakerIds, string keptId)
    {
        var plan = SpeakerMergePlan.Create(speakerIds, keptId);
        if (plan is null) return;
        // Capture the session now: if a different meeting is shown when the merge finishes, don't apply speaker-row changes to it.
        var sessionId = _sessionId;
        Exception? failure = null;
        // The pipeline owns transcript/storage changes; update this window only after the resolver confirms the merge.
        try { await _pipeline.MergeSpeakersAsync(speakerIds, keptId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex; }

        var resolver = _pipeline.SpeakerResolver;
        // Success is judged from the resolver, not just the absence of an exception: the in-memory alias can apply
        // even if persisting the saved transcript later failed (the "merged but not saved" warning below).
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
        // Repoint existing rows instead of recreating them so transcript order and current scroll position stay intact.
        var resolver = _pipeline.SpeakerResolver;
        var display = resolver.GetDisplayName(plan.KeptId);
        var avatar = SpeakerAvatarPresentation.Create(display, resolver.IsResolved(plan.KeptId));
        var brush = GetSpeakerBrush(plan.KeptId);
        var image = _speakerAvatarImages.GetValueOrDefault(plan.KeptId);

        // Transcript lines, their word counts and avatar caches all key off speaker id, so each must be remapped.
        var lines = _transcript.ToList();
        if (_partialItem is { } partial && !lines.Contains(partial)) lines.Add(partial);
        foreach (var line in lines.Where(line => plan.Absorbs(line.SpeakerId)))
        {
            line.SpeakerId = plan.KeptId;
            // Partial (provisional) lines are dimmed; keep the label so they remain visibly unfinalised.
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

        // Drop absorbed ids from the queue of speakers waiting for a name prompt, preserving order of the rest.
        var pending = _pendingUnknownSpeakers.Where(id => !plan.Absorbs(id)).ToArray();
        _pendingUnknownSpeakers.Clear();
        foreach (var id in pending) _pendingUnknownSpeakers.Enqueue(id);
        if (_currentPromptSpeakerId is not null && plan.Absorbs(_currentPromptSpeakerId))
        {
            // The prompt on screen refers to a speaker that no longer exists; close it and advance to the next one.
            _currentPromptSpeakerId = null;
            SpeakerNamingPanel.Visibility = Visibility.Collapsed;
            _suppressAutoDismiss = false;
            ShowNextSpeakerPrompt();
        }

        // Re-derive dependent views last, after all row/count state is consistent.
        RefreshSpeakerTimeline();
        RefreshVisualization();
        UpdateRecordingPresentation();
    }
}
