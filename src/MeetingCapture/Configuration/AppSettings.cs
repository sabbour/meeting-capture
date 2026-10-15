namespace MeetingCapture.Configuration;

/// <summary>Root of the application settings, bound from configuration and user settings.</summary>
public sealed class AppSettings
{
    public TranscriptionSettings Transcription { get; init; } = new();
    public IntelligenceSettings Intelligence { get; init; } = new();
    public StorageSettings Storage { get; init; } = new();
    public AudioSettings Audio { get; init; } = new();
    public MeetingDetectionSettings MeetingDetection { get; init; } = new();
}

/// <summary>Settings for the speech-to-text service.</summary>
public sealed class TranscriptionSettings
{
    /// <summary>
    /// "azure" for Azure Speech SDK via Foundry, "npu" for local Windows ML / Whisper NPU path.
    /// </summary>
    public string Provider { get; init; } = "azure";

    /// <summary>
    /// Azure AI Foundry / Cognitive Services endpoint.
    /// Region is extracted automatically from the URL; falls back to endpoint-based config.
    /// E.g. https://my-resource.cognitiveservices.azure.com/
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    /// Foundry API key. Read from AZURE_SPEECH_KEY env var if empty.
    /// Store via `dotnet user-secrets` — do not commit to appsettings.json.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Azure region for SpeechConfig.FromSubscription (e.g. "swedencentral").
    /// When set, takes precedence over URL-based region extraction.
    /// </summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>Recognition language tag, for example "en-US".</summary>
    public string Language { get; init; } = "en-US";
}

/// <summary>Settings for the AI provider that creates live insights and final notes.</summary>
public sealed class IntelligenceSettings
{
    /// <summary>
    /// Inference provider. "github" = GitHub Copilot API (gh CLI auth, no extra credentials).
    /// "azure" = BYOC via Azure AI Foundry (requires Endpoint + ApiKey below).
    /// </summary>
    public string Provider { get; init; } = "azure";

    // ── Azure BYOC — shared across both uses ─────────────────────────────────────────

    /// <summary>Azure AI Foundry / OpenAI endpoint. E.g. https://my-resource.openai.azure.com/</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Foundry API key. Read from AZURE_FOUNDRY_KEY env var if empty.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Azure OpenAI API version for the versioned deployment route.</summary>
    public string ApiVersion { get; init; } = "2025-04-01-preview";

    // ── Synthesis model — transcript → structured notes (runs once per meeting) ──────

    /// <summary>
    /// Azure deployment name for final note synthesis.
    /// This is the name you gave the deployment in Azure AI Studio (e.g. "gpt-4o").
    /// </summary>
    public string SynthesisDeployment { get; init; } = string.Empty;

    /// <summary>GitHub Copilot model for synthesis (e.g. "gpt-4o", "claude-sonnet-4.5").</summary>
    public string SynthesisModel { get; init; } = string.Empty;

    // ── Intelligence model — real-time topic / questions / actions (runs periodically) ─

    /// <summary>
    /// Azure deployment name for real-time intelligence.
    /// This is the name you gave the deployment in Azure AI Studio (e.g. "gpt-4o-mini").
    /// </summary>
    public string IntelligenceDeployment { get; init; } = string.Empty;

    /// <summary>GitHub Copilot model for real-time intelligence (e.g. "gpt-4o-mini", "claude-haiku-4.5").</summary>
    public string IntelligenceModel { get; init; } = string.Empty;
}

/// <summary>Settings for where meeting output is saved.</summary>
public sealed class StorageSettings
{
    /// <summary>Root directory where meeting notes are persisted.</summary>
    public string NotesPath { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Microsoft Scout", "Meetings");
}

/// <summary>Settings for which audio sources are captured and in what PCM format.</summary>
public sealed class AudioSettings
{
    /// <summary>Capture microphone input.</summary>
    public bool CaptureMicrophone { get; init; } = true;

    /// <summary>Capture system audio using WASAPI loopback.</summary>
    public bool CaptureLoopback { get; init; } = true;

    /// <summary>
    /// Capture loopback AND microphone simultaneously via <c>DualSourceCapture</c>.
    /// This is what lets the user's own voice be transcribed during Teams/Zoom calls — the
    /// loopback stream is echo-cancelled to exclude the local talker, so the mic supplies it.
    /// When true (and both sources are enabled) the pipeline uses the dual source; otherwise it
    /// falls back to whichever single source is available.
    /// </summary>
    public bool DualCapture { get; init; } = true;

    /// <summary>
    /// PCM sample rate used for all capture paths. Azure Speech SDK requires 16000 Hz.
    /// </summary>
    public int SampleRate { get; init; } = 16000;

    /// <summary>Mono channel for Speech SDK compatibility.</summary>
    public int Channels { get; init; } = 1;
}

/// <summary>Settings for detecting that a meeting may be in progress.</summary>
public sealed class MeetingDetectionSettings
{
    /// <summary>
    /// Known app process names eligible for sustained microphone-activity suggestions. Never auto-records.
    /// </summary>
    public string[] KnownMeetingProcesses { get; init; } =
    [
        "Teams",
        "ms-teams",
        "Zoom",
        "WebexMeetings",
        "WebexTeams",
        "lync",
        "msteams"
    ];

    /// <summary>Polling interval for process identity and microphone-session evidence.</summary>
    public int PollIntervalSeconds { get; init; } = 10;
}
