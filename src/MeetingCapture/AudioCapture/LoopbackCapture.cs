using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingCapture.AudioCapture;

/// <summary>
/// Captures system audio output (what the speakers are playing) via WASAPI loopback.
/// This is the "hooked audio stream" path — captures Teams/Zoom call audio without
/// requiring per-app API hooks.
///
/// Assumption: Windows default output device is used. If no audio is playing,
/// the loopback returns silence — this is intentional and correct WASAPI behavior.
/// </summary>
public sealed class LoopbackCapture : IAudioCapture
{
    private readonly AudioSettings _settings;
    private readonly ILogger<LoopbackCapture> _logger;
    private WasapiLoopbackCapture? _loopback;
    private MediaFoundationResampler? _resampler;
    private BufferedWaveProvider? _resamplerInput;
    private bool _capturing;
    private bool _disposed;

    public string Name => "System Audio (Loopback)";
    public bool IsCapturing => _capturing;
    public int SampleRate { get; }
    public int Channels { get; }

    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public LoopbackCapture(
        IOptions<AppSettings> settings,
        ILogger<LoopbackCapture> logger)
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

        _loopback = new WasapiLoopbackCapture();
        var targetFormat = new WaveFormat(SampleRate, 16, Channels);

        // Keep a direct reference to the buffer so we can feed it from DataAvailable.
        _resamplerInput = new BufferedWaveProvider(_loopback.WaveFormat)
        {
            DiscardOnBufferOverflow = true
        };

        _resampler = new MediaFoundationResampler(_resamplerInput, targetFormat)
        {
            ResamplerQuality = 60
        };

        _loopback.DataAvailable += OnDataAvailable;
        _loopback.RecordingStopped += OnRecordingStopped;

        _logger.LogInformation(
            "Starting loopback capture. Device format: {DeviceFormat} → target {Rate}Hz/{Ch}ch",
            _loopback.WaveFormat, SampleRate, Channels);

        _loopback.StartRecording();
        _capturing = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_capturing || _loopback is null)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping loopback capture");
        _loopback.StopRecording();
        _capturing = false;
        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        // Feed into the resampler's buffered provider
        _resamplerInput?.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Read resampled output
        if (_resampler is null)
            return;

        var outputBuffer = new byte[e.BytesRecorded * 2]; // over-allocate for resampled output
        int read = _resampler.Read(outputBuffer, 0, outputBuffer.Length);
        if (read > 0)
            DataAvailable?.Invoke(this, new AudioDataEventArgs(outputBuffer, read));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception, "Loopback capture stopped unexpectedly");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loopback?.Dispose();
        _resampler?.Dispose();
        _loopback = null;
        _resampler = null;
        _resamplerInput = null;
    }
}
