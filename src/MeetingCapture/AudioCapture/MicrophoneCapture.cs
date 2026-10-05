using MeetingCapture.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;

namespace MeetingCapture.AudioCapture;

public sealed class MicrophoneCapture : IAudioCapture, IMicActivitySource
{
    private readonly WasapiCaptureWorker _worker;
    private RollingRms _rms;
    private long _micActivityTicks;
    private bool _disposed;
    public string Name => "Microphone";
    public bool IsCapturing => _worker.IsCapturing;
    public int SampleRate { get; }
    public int Channels { get; }
    public IReadOnlyList<AudioSourceHealth> Health => Array.AsReadOnly(new[] { _worker.Health });
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    public DateTime? MicActivityTimestamp
    {
        get
        {
            var ticks = Interlocked.Read(ref _micActivityTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }
    public MicrophoneCapture(IOptions<AppSettings> settings, ILogger<MicrophoneCapture> logger)
    {
        SampleRate = settings.Value.Audio.SampleRate;
        Channels = settings.Value.Audio.Channels;
        _rms = new RollingRms(SampleRate, 500);
        _worker = new("microphone",
            () => new WasapiCapture(WasapiCapture.GetDefaultCaptureDevice(), true, 100),
            OnPcm, logger);
    }

    private void OnPcm(byte[] bytes, int count)
    {
        _rms.Add(bytes, count);
        if (_rms.Rms >= 500)
            Interlocked.Exchange(ref _micActivityTicks, DateTime.UtcNow.Ticks);
        DataAvailable?.Invoke(this, new(bytes, count));
    }

    public Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing) throw new InvalidOperationException("The microphone is already owned.");
        if (SampleRate != 16000 || Channels != 1)
            throw new InvalidOperationException("Microphone capture supports 16000 Hz mono PCM.");
        _rms = new RollingRms(SampleRate, 500);
        Interlocked.Exchange(ref _micActivityTicks, 0);
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
