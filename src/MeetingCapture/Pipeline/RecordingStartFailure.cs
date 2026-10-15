using MeetingCapture.AudioCapture;
using MeetingCapture.Transcription;

namespace MeetingCapture.Pipeline;

/// <summary>Typed reason a recording could not start, used to give specific user guidance.</summary>
public enum RecordingStartFailureKind
{
    Unknown,
    Canceled,
    AlreadyInProgress,
    ExitInProgress,
    PendingVisualizationRetry,
    PipelineNotReady,
    DetectionExpired,
    IneligibleCalendarMeeting,
    NoAudioSource,
    UnsupportedAudioFormat,
    MicrophoneUnavailable,
    LoopbackUnavailable,
    AudioUnavailable,
    RetainedCaptureOwnership,
    SpeechCredentialsMissing,
    SpeechCredentialsRejected,
    SpeechConfigurationInvalid,
    SpeechUnreachable,
    SpeechStartFailed
}

/// <summary>A sanitized, user-facing description of a startup failure. Never carries secrets or SDK text.</summary>
public sealed record RecordingStartFailure(RecordingStartFailureKind Kind, string Message);

/// <summary>Thrown by the pipeline when a recording cannot start, carrying typed guidance.</summary>
public sealed class RecordingStartException : InvalidOperationException
{
    public RecordingStartException(RecordingStartFailureKind kind, Exception? inner = null)
        : base(RecordingStartFailures.Describe(kind).Message, inner) =>
        Failure = RecordingStartFailures.Describe(kind);

    public RecordingStartFailure Failure { get; }
    public RecordingStartFailureKind Kind => Failure.Kind;
}

/// <summary>Maps startup faults and capture/transcription health to specific, actionable guidance.</summary>
public static class RecordingStartFailures
{
    public static RecordingStartFailure Describe(RecordingStartFailureKind kind) => new(kind, kind switch
    {
        RecordingStartFailureKind.Canceled =>
            "Recording startup was canceled before capture began. Start recording again when ready.",
        RecordingStartFailureKind.AlreadyInProgress =>
            "A recording, audio preview, or save is already in progress. Wait for it to finish, then start again.",
        RecordingStartFailureKind.ExitInProgress =>
            "Meeting Capture is exiting. Reopen it before recording again.",
        RecordingStartFailureKind.PendingVisualizationRetry =>
            "The previous meeting's visualization data has not been saved yet. Use Retry AI on that meeting, " +
            "or exit and reopen Meeting Capture, before recording again.",
        RecordingStartFailureKind.PipelineNotReady =>
            "The previous meeting is still finishing. Wait for it to finish saving, then start again.",
        RecordingStartFailureKind.DetectionExpired =>
            "The detected meeting is no longer available. Start a manual recording instead.",
        RecordingStartFailureKind.IneligibleCalendarMeeting =>
            "That calendar item is not an eligible online meeting. Choose another meeting or start a manual recording.",
        RecordingStartFailureKind.NoAudioSource =>
            "No audio source is enabled. Turn on the microphone or system audio in Settings, then start again.",
        RecordingStartFailureKind.UnsupportedAudioFormat =>
            "Transcription requires 16000 Hz mono PCM capture. Correct the audio settings, then start again.",
        RecordingStartFailureKind.MicrophoneUnavailable =>
            "The microphone is unavailable or in use by another app. Close that app, or choose a working default " +
            "microphone in Windows sound settings, then start again.",
        RecordingStartFailureKind.LoopbackUnavailable =>
            "System audio (output loopback) could not start on the default playback device. Choose a working " +
            "output device in Windows sound settings, then start again.",
        RecordingStartFailureKind.AudioUnavailable =>
            "Neither the microphone nor system audio could start. Check that the Windows input and output devices " +
            "are available and not in use by another app, then start again.",
        RecordingStartFailureKind.RetainedCaptureOwnership =>
            "Capture resources could not be released. Restart Meeting Capture before recording again.",
        RecordingStartFailureKind.SpeechCredentialsMissing =>
            "Speech credentials are not configured. Add the transcription key and endpoint in Settings, then start again.",
        RecordingStartFailureKind.SpeechCredentialsRejected =>
            "Speech rejected the configured credentials. Check the transcription key and resource region in Settings, " +
            "then start again.",
        RecordingStartFailureKind.SpeechConfigurationInvalid =>
            "The transcription endpoint, region, or language is not valid. Correct the transcription settings, " +
            "then start again.",
        RecordingStartFailureKind.SpeechUnreachable =>
            "The speech service could not be reached. Check the network or VPN connection, then start again.",
        RecordingStartFailureKind.SpeechStartFailed =>
            "Transcription could not start. Check the transcription settings, then start again.",
        _ => "Recording could not start. Check the audio devices and transcription settings, then start again."
    });

    /// <summary>Classifies a startup fault using the fault itself and the capture/transcription health it left behind.</summary>
    public static RecordingStartFailure Classify(
        Exception? error, IReadOnlyList<AudioSourceHealth> audio, TranscriptionHealth? transcription,
        bool resourcesRetained = false)
    {
        // Prefer a typed startup error, then a retained-resource warning, before inferring from health snapshots.
        if (error is RecordingStartException typed) return typed.Failure;
        if (resourcesRetained) return Describe(RecordingStartFailureKind.RetainedCaptureOwnership);
        switch (error)
        {
            case OperationCanceledException: return Describe(RecordingStartFailureKind.Canceled);
            case TranscriptionStartException speech: return Describe(SpeechKind(speech.Kind));
            case ArgumentException: return Describe(RecordingStartFailureKind.IneligibleCalendarMeeting);
        }
        var sources = audio ?? [];
        // Device-level failures give the most actionable guidance and are more specific than the mixer summary.
        var microphone = Failed(sources, "microphone");
        var loopback = Failed(sources, "loopback");
        if (microphone && loopback) return Describe(RecordingStartFailureKind.AudioUnavailable);
        if (microphone) return Describe(RecordingStartFailureKind.MicrophoneUnavailable);
        if (loopback) return Describe(RecordingStartFailureKind.LoopbackUnavailable);
        if (sources.Count > 0 && sources.All(source => source.State == CaptureHealthState.Disabled))
            return Describe(RecordingStartFailureKind.NoAudioSource);
        if (sources.Any(source => source.State == CaptureHealthState.Failed))
            return Describe(RecordingStartFailureKind.AudioUnavailable);
        // The transcription state is considered only after audio failures, which should be fixed first.
        if (transcription is { State: CaptureHealthState.Failed })
            return Describe(RecordingStartFailureKind.SpeechStartFailed);
        return Describe(RecordingStartFailureKind.Unknown);
    }

    private static RecordingStartFailureKind SpeechKind(TranscriptionStartFailureKind kind) => kind switch
    {
        TranscriptionStartFailureKind.CredentialsMissing => RecordingStartFailureKind.SpeechCredentialsMissing,
        TranscriptionStartFailureKind.CredentialsRejected => RecordingStartFailureKind.SpeechCredentialsRejected,
        TranscriptionStartFailureKind.ConfigurationInvalid => RecordingStartFailureKind.SpeechConfigurationInvalid,
        TranscriptionStartFailureKind.ServiceUnreachable => RecordingStartFailureKind.SpeechUnreachable,
        _ => RecordingStartFailureKind.SpeechStartFailed
    };

    private static bool Failed(IReadOnlyList<AudioSourceHealth> sources, string name) =>
        sources.Any(source =>
            string.Equals(source.Source, name, StringComparison.OrdinalIgnoreCase) &&
            source.State == CaptureHealthState.Failed);
}
