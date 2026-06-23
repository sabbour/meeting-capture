using MeetingCapture.AudioCapture;
using MeetingCapture.Configuration;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using MeetingCapture.Pipeline;
using MeetingCapture.Storage;
using MeetingCapture.Transcription;
using MeetingCapture.TrayApp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Tray apps must run on the UI thread with STA
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

ApplicationConfiguration.Initialize();

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();

// Configuration
services.Configure<AppSettings>(config);

// Logging
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Meeting detection
services.AddSingleton<IMeetingDetector, ProcessMeetingDetector>();

// Audio capture — conditionally register based on settings
var audioSettings = config.GetSection("Audio").Get<AudioSettings>() ?? new AudioSettings();
if (audioSettings.CaptureMicrophone)
    services.AddSingleton<IAudioCapture, MicrophoneCapture>();
if (audioSettings.CaptureLoopback)
    services.AddSingleton<IAudioCapture, LoopbackCapture>();

// Transcription — extensible: swap provider here when NPU path is ready
services.AddSingleton<ITranscriptionService, AzureSpeechTranscriptionService>();

// Intelligence
services.AddSingleton<INotesIntelligence, CopilotNotesService>();

// Storage
services.AddSingleton<INotesStorage, MarkdownNotesStorage>();

// Pipeline + tray
services.AddSingleton<MeetingPipeline>();
services.AddSingleton<TrayApplicationContext>();

var sp = services.BuildServiceProvider();

// Validate critical config on startup and log warnings rather than crashing silently
var logger = sp.GetRequiredService<ILogger<Program>>();
var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
ValidateAndWarnSettings(settings, logger);

var trayContext = sp.GetRequiredService<TrayApplicationContext>();
Application.Run(trayContext);

static void ValidateAndWarnSettings(AppSettings settings, ILogger logger)
{
    if (string.IsNullOrWhiteSpace(settings.Transcription.AzureSpeechKey)
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")))
    {
        logger.LogWarning(
            "No Azure Speech key found. Set Transcription:AzureSpeechKey or AZURE_SPEECH_KEY env var. " +
            "Transcription will fail at runtime.");
    }

    if (string.IsNullOrWhiteSpace(settings.Intelligence.ApiKey)
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
    {
        logger.LogWarning(
            "No LLM API key found. Set Intelligence:ApiKey or GITHUB_TOKEN env var. " +
            "Note synthesis will fail at runtime.");
    }

    if (!Directory.Exists(settings.Storage.NotesPath))
    {
        try
        {
            Directory.CreateDirectory(settings.Storage.NotesPath);
            logger.LogInformation("Created notes directory: {Path}", settings.Storage.NotesPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot create notes directory {Path}", settings.Storage.NotesPath);
        }
    }
}
