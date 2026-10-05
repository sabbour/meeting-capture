using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingCapture.AudioCapture;

public sealed class LoopbackCapture : IAudioCapture
{
    private readonly WasapiCaptureWorker _worker;
    private bool _disposed;
    public string Name => "System Audio (Loopback)";
    public bool IsCapturing => _worker.IsCapturing;
    public int SampleRate { get; }
    public int Channels { get; }
    public IReadOnlyList<AudioSourceHealth> Health => Array.AsReadOnly(new[] { _worker.Health });
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public LoopbackCapture(IOptions<AppSettings> settings, ILogger<LoopbackCapture> logger)
    {
        SampleRate = settings.Value.Audio.SampleRate;
        Channels = settings.Value.Audio.Channels;
        _worker = new("loopback", () => new EventSynchronizedLoopbackCapture(),
            (bytes, count) => DataAvailable?.Invoke(this, new(bytes, count)), logger);
    }

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SampleRate != 16000 || Channels != 1)
            throw new InvalidOperationException("Loopback capture supports 16000 Hz mono PCM.");
        return _worker.StartAsync(ct);
    }
    public Task StopAsync(CancellationToken ct) => _worker.StopAsync(ct);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}

internal sealed class EventSynchronizedLoopbackCapture()
    : WasapiCapture(WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(), true, 100)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}
