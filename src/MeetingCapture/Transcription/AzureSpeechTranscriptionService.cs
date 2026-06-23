using MeetingCapture.Configuration;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingCapture.Transcription;

/// <summary>
/// Azure Cognitive Services Speech SDK transcription.
/// Uses push-stream mode so PCM bytes from NAudio flow directly in.
///
/// Assumptions:
/// - 16 kHz, 16-bit, mono PCM (matches AudioSettings defaults)
/// - Azure Speech key + region configured in appsettings.json or environment
/// - If key is empty, throws InvalidOperationException at start (explicit failure, no silent fallback)
/// </summary>
public sealed class AzureSpeechTranscriptionService : ITranscriptionService
{
    private readonly TranscriptionSettings _settings;
    private readonly ILogger<AzureSpeechTranscriptionService> _logger;

    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private readonly TranscriptionResult _result = new();
    private bool _transcribing;

    public event EventHandler<TranscriptionSegment>? SegmentRecognized;
    public bool IsTranscribing => _transcribing;

    public AzureSpeechTranscriptionService(
        IOptions<AppSettings> settings,
        ILogger<AzureSpeechTranscriptionService> logger)
    {
        _settings = settings.Value.Transcription;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_transcribing)
            return;

        var key = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Azure Speech key is not configured. Set Transcription:AzureSpeechKey in appsettings.json " +
                "or the AZURE_SPEECH_KEY environment variable.");

        var speechConfig = SpeechConfig.FromSubscription(key, _settings.AzureSpeechRegion);
        speechConfig.SpeechRecognitionLanguage = _settings.Language;
        speechConfig.EnableDictation();

        // 16 kHz, 16-bit, mono — must match AudioSettings
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(audioFormat);
        var audioConfig = AudioConfig.FromStreamInput(_pushStream);

        _recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        _recognizer.Recognized += OnRecognized;
        _recognizer.Canceled += OnCanceled;
        _recognizer.SessionStopped += (_, _) =>
            _logger.LogInformation("Azure Speech session stopped");

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        _transcribing = true;
        _logger.LogInformation("Azure Speech transcription started [{Region}/{Lang}]",
            _settings.AzureSpeechRegion, _settings.Language);
    }

    public Task PushAudioAsync(byte[] pcmData, int bytesRecorded, CancellationToken cancellationToken)
    {
        if (!_transcribing || _pushStream is null)
            return Task.CompletedTask;

        _pushStream.Write(pcmData, bytesRecorded);
        return Task.CompletedTask;
    }

    public async Task<TranscriptionResult> StopAsync(CancellationToken cancellationToken)
    {
        if (!_transcribing || _recognizer is null)
            return _result;

        _logger.LogInformation("Stopping Azure Speech transcription, flushing stream...");
        _pushStream?.Close();
        await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        _transcribing = false;
        return _result;
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason != ResultReason.RecognizedSpeech)
            return;

        var segment = new TranscriptionSegment(
            e.Result.Text,
            DateTimeOffset.UtcNow,
            ConfidenceScore: null);

        _result.Segments.Add(segment);
        _logger.LogDebug("Recognized: {Text}", segment.Text);
        SegmentRecognized?.Invoke(this, segment);
    }

    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs e)
    {
        _logger.LogError(
            "Azure Speech recognition canceled. Reason: {Reason}, Error: {Detail}",
            e.Reason, e.ErrorDetails);
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AzureSpeechKey))
            return _settings.AzureSpeechKey;
        return Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_recognizer is not null)
        {
            if (_transcribing)
            {
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
                _transcribing = false;
            }
            _recognizer.Dispose();
        }
        _pushStream?.Dispose();
    }
}
