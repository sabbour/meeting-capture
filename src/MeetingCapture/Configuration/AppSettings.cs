namespace MeetingCapture.Configuration;

public sealed class AppSettings
{
    public TranscriptionSettings Transcription { get; init; } = new();
    public IntelligenceSettings Intelligence { get; init; } = new();
    public StorageSettings Storage { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public MeetingDetectionSettings MeetingDetection { get; init; } = new();
}

public sealed class TranscriptionSettings
{
    /// <summary>
    /// "azure" for Azure Speech SDK (cloud), "npu" for local Windows ML / Whisper NPU path.
    /// </summary>
    public string Provider { get; init; } = "azure";

    public string AzureSpeechKey { get; init; } = string.Empty;
    public string AzureSpeechRegion { get; init; } = "eastus";
    public string Language { get; init; } = "en-US";
}

public sealed class IntelligenceSettings
{
    /// <summary>
    /// GitHub Models endpoint or Azure OpenAI endpoint.
    /// Default targets GitHub Models (https://models.inference.ai.azure.com).
    /// </summary>
    public string Endpoint { get; init; } = "https://models.inference.ai.azure.com";

    /// <summary>
    /// PAT with models:read scope for GitHub Models, or Azure OpenAI key.
    /// Read from environment variable GITHUB_TOKEN if empty.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "gpt-4o";

    /// <summary>Maximum tokens for the notes synthesis response.</summary>
    public int MaxTokens { get; init; } = 2000;
}

public sealed class StorageSettings
{
    /// <summary>Root directory where meeting notes are persisted.</summary>
    public string NotesPath { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Microsoft Scout", "Meetings");
}

public sealed class AudioSettings
{
    /// <summary>Capture microphone input.</summary>
    public bool CaptureMicrophone { get; init; } = true;

    /// <summary>Capture system audio (WASAPI loopback — requires exclusive loopback access).</summary>
    public bool CaptureLoopback { get; init; } = true;

    /// <summary>
    /// PCM sample rate used for all capture paths. Azure Speech SDK requires 16000 Hz.
    /// </summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>Mono channel for Speech SDK compatibility.</summary>
    public int Channels { get; init; } = 1;
}

public sealed class MeetingDetectionSettings
{
    /// <summary>
    /// Known meeting application process names (without .exe) to trigger automatic session start.
    /// </summary>
    public string[] KnownMeetingProcesses { get; init; } =
    [
        "Teams",
        "ms-teams",
        "Zoom",
        "WebexMeetings",
        "WebexTeams",
        "Slack",
        "lync",
        "OUTLOOK",
        "msteams"
    ];

    /// <summary>Polling interval in seconds when process detection is used.</summary>
    public int PollIntervalSeconds { get; init; } = 10;
}
