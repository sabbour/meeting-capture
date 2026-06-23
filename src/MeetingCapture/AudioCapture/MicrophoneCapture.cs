using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;

namespace MeetingCapture.AudioCapture;

/// <summary>
/// Captures microphone (default input device) using NAudio WaveInEvent.
/// Resamples to the configured sample rate + mono for Speech SDK compatibility.
/// </summary>
public sealed class MicrophoneCapture : IAudioCapture
{
    private readonly AudioSettings _settings;
    private readonly ILogger<MicrophoneCapture> _logger;
    private WaveInEvent? _waveIn;
    private bool _capturing;
    private bool _disposed;

    public string Name => "Microphone";
    public bool IsCapturing => _capturing;
    public int SampleRate { get; }
    public int Channels { get; }

    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public MicrophoneCapture(
        IOptions<AppSettings> settings,
        ILogger<MicrophoneCapture> logger)
    {
        _settings = settings.Value.Audio;
        _logger = logger;
        SampleRate = _settings.SampleRate;
        Channels = _settings.Channels;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capturing)
            return Task.CompletedTask;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 100
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _logger.LogInformation("Starting microphone capture at {Rate}Hz/{Channels}ch",
            SampleRate, Channels);
        _waveIn.StartRecording();
        _capturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_capturing || _waveIn is null)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping microphone capture");
        _waveIn.StopRecording();
        _capturing = false;
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0)
            DataAvailable?.Invoke(this, new AudioDataEventArgs(e.Buffer, e.BytesRecorded));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception, "Microphone recording stopped unexpectedly");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _waveIn?.Dispose();
        _waveIn = null;
    }
}
