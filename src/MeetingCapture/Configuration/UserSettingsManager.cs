namespace MeetingCapture.Configuration;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

/// <summary>The intelligence settings with a revision number that increases each time they are saved.</summary>
public sealed record IntelligenceConfiguration(long Revision, IntelligenceSettings Settings);

/// <summary>Raised after settings are saved: says whether intelligence changed and which sections need a restart.</summary>
public sealed record SettingsSavedEventArgs(
    long IntelligenceRevision,
    bool IntelligenceChanged,
    IReadOnlyList<string> RestartRequiredSections);

/// <summary>
/// Persists user-overridden settings to %AppData%\MeetingCapture\settings.user.json.
/// The file mirrors the appsettings.json structure so the config builder can overlay it.
/// Secrets are only written when the caller explicitly provides a non-empty value.
/// </summary>
public sealed class UserSettingsManager
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingCapture");

    public static string SettingsFilePath { get; } =
        Path.Combine(SettingsDirectory, "settings.user.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IConfigurationRoot _configuration;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _startupSections;
    private IntelligenceConfiguration _intelligence;

    public UserSettingsManager(IConfigurationRoot configuration, string? settingsFilePath = null)
    {
        _configuration = configuration;
        _filePath = Path.GetFullPath(settingsFilePath ?? SettingsFilePath);
        _intelligence = new(0, BindIntelligence());
        _startupSections = new[] { "Audio", "Transcription", "Storage", "MeetingDetection" }
            .ToDictionary(section => section, SectionValue);
    }

    public event EventHandler<SettingsSavedEventArgs>? SettingsSaved;

    public IntelligenceConfiguration GetIntelligenceSnapshot()
    {
        lock (_gate)
            return _intelligence;
    }

    public Task<SettingsSavedEventArgs> SaveAsync(EditableSettings settings)
        // Save performs synchronous file and configuration-provider work, so keep it off a UI caller.
        => Task.Run(() => Save(settings));

    public SettingsSavedEventArgs Save(EditableSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        SettingsSavedEventArgs result;
        lock (_gate)
        {
            // Serialize edits, file replacement, configuration reload, and revision updates as one operation.
            // A matching JSON provider is required so reloading preserves all higher-priority overrides.
            var provider = _configuration.Providers.OfType<JsonConfigurationProvider>()
                .FirstOrDefault(p => string.Equals(
                    p.Source.FileProvider?.GetFileInfo(p.Source.Path!).PhysicalPath,
                    _filePath, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                throw new InvalidOperationException("Settings cannot be applied: the user settings file is not registered in configuration.");

            JsonObject root;
            try
            {
                // Start from the existing object so settings this dialog does not edit remain intact.
                root = File.Exists(_filePath)
                    ? JsonNode.Parse(File.ReadAllText(_filePath)) as JsonObject
                        ?? throw new JsonException()
                    : new JsonObject();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Existing user settings could not be read. No settings were saved.");
            }

            var t = Section(root, "Transcription");
            t["Endpoint"] = s.TranscriptionEndpoint;
            t["Region"] = s.TranscriptionRegion;
            t["Language"] = s.TranscriptionLanguage;
            // An empty password field means "leave the stored key alone", not "erase it".
            if (!string.IsNullOrWhiteSpace(s.TranscriptionApiKey)) t["ApiKey"] = s.TranscriptionApiKey;

            var i = Section(root, "Intelligence");
            i["Provider"] = s.IntelligenceProvider;
            i["SynthesisDeployment"] = s.IntelligenceSynthesisDeployment;
            i["SynthesisModel"] = s.IntelligenceSynthesisModel;
            i["IntelligenceDeployment"] = s.IntelligenceDeployment;
            i["IntelligenceModel"] = s.IntelligenceModel;
            i["Endpoint"] = s.IntelligenceEndpoint;
            i["ApiVersion"] = s.IntelligenceApiVersion;
            if (!string.IsNullOrWhiteSpace(s.IntelligenceApiKey)) i["ApiKey"] = s.IntelligenceApiKey;

            var audio = Section(root, "Audio");
            audio["CaptureMicrophone"] = s.CaptureMicrophone;
            audio["CaptureLoopback"] = s.CaptureLoopback;
            if (!string.IsNullOrWhiteSpace(s.NotesPath))
                Section(root, "Storage")["NotesPath"] = s.NotesPath;

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Replace atomically so a crash or disk error cannot leave a half-written settings file.
                File.WriteAllText(temporary, root.ToJsonString(JsonOptions));
                if (File.Exists(_filePath))
                    File.Replace(temporary, _filePath, null);
                else
                    File.Move(temporary, _filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("User settings could not be saved. Check folder access and available disk space.");
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            try
            {
                // Reload to read the same effective values the rest of the app will now use.
                _configuration.Reload();
                var effective = BindIntelligence();
                // Only intelligence settings receive a live revision; audio and device changes need restart.
                var changed = JsonSerializer.Serialize(effective) != JsonSerializer.Serialize(_intelligence.Settings);
                var revision = _intelligence.Revision + (changed ? 1 : 0);
                var restart = _startupSections
                    .Where(pair => pair.Value != SectionValue(pair.Key))
                    .Select(pair => pair.Key).ToArray();
                _intelligence = new(revision, effective);
                result = new(revision, changed, Array.AsReadOnly(restart));
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Settings were saved but could not be applied. Restart Meeting Capture before retrying.");
            }
        }
        // Notify after releasing _gate so subscribers can safely read or save settings in response.
        SettingsSaved?.Invoke(this, result);
        return result;
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section) return section;
        // Do not silently replace a user-edited value of the wrong shape with an empty section.
        if (root.ContainsKey(name))
            throw new InvalidOperationException("Existing settings contain an invalid section. No settings were saved.");
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private string SectionValue(string name)
        => name switch
        {
            // Include environment-based secrets in startup comparisons, but never persist them here.
            "Audio" => JsonSerializer.Serialize(_configuration.GetSection(name).Get<AudioSettings>() ?? new()),
            "Transcription" => JsonSerializer.Serialize(new
            {
                Settings = _configuration.GetSection(name).Get<TranscriptionSettings>() ?? new(),
                FallbackKey = _configuration["AZURE_SPEECH_KEY"]
            }),
            "Storage" => JsonSerializer.Serialize(_configuration.GetSection(name).Get<StorageSettings>() ?? new()),
            "MeetingDetection" => JsonSerializer.Serialize(_configuration.GetSection(name).Get<MeetingDetectionSettings>() ?? new()),
            _ => JsonSerializer.Serialize(_configuration.GetSection(name).AsEnumerable()
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        };

    private IntelligenceSettings BindIntelligence()
    {
        // Bind the displayed non-secret settings, then resolve a key from configuration or environment.
        var settings = _configuration.GetSection("Intelligence").Get<IntelligenceSettings>() ?? new();
        return new IntelligenceSettings
        {
            Provider = settings.Provider,
            Endpoint = settings.Endpoint,
            ApiVersion = settings.ApiVersion,
            ApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey
                : _configuration["AZURE_FOUNDRY_KEY"] ?? _configuration["AZURE_OPENAI_API_KEY"] ?? string.Empty,
            SynthesisDeployment = settings.SynthesisDeployment,
            SynthesisModel = settings.SynthesisModel,
            IntelligenceDeployment = settings.IntelligenceDeployment,
            IntelligenceModel = settings.IntelligenceModel
        };
    }

    /// <summary>
    /// Returns effective non-secret values for the settings dialog, respecting configuration precedence.
    /// </summary>
    public EditableSettings? ReadSaved()
    {
        lock (_gate)
        {
            // Return effective non-secret values only; callers cannot read secrets back into the dialog.
            var effective = _configuration.Get<AppSettings>() ?? new();
            var i = _intelligence.Settings;
            return new EditableSettings
            {
                TranscriptionEndpoint = effective.Transcription.Endpoint,
                TranscriptionRegion = effective.Transcription.Region,
                TranscriptionLanguage = effective.Transcription.Language,
                IntelligenceProvider = i.Provider,
                IntelligenceSynthesisDeployment = i.SynthesisDeployment,
                IntelligenceSynthesisModel = i.SynthesisModel,
                IntelligenceDeployment = i.IntelligenceDeployment,
                IntelligenceModel = i.IntelligenceModel,
                IntelligenceEndpoint = i.Endpoint,
                IntelligenceApiVersion = i.ApiVersion,
                CaptureMicrophone = effective.Audio.CaptureMicrophone,
                CaptureLoopback = effective.Audio.CaptureLoopback,
                NotesPath = effective.Storage.NotesPath
            };
        }
    }
}

/// <summary>Mutable view of user-editable settings bound to the Settings dialog.</summary>
public sealed class EditableSettings
{
    public string TranscriptionEndpoint { get; set; } = string.Empty;
    public string TranscriptionRegion   { get; set; } = string.Empty;
    public string TranscriptionLanguage { get; set; } = string.Empty;
    /// <summary>Only saved to disk when non-empty (user explicitly typed a new key).</summary>
    public string TranscriptionApiKey   { get; set; } = string.Empty;

    public string IntelligenceProvider           { get; set; } = "azure";
    // Synthesis model
    public string IntelligenceSynthesisDeployment { get; set; } = string.Empty;
    public string IntelligenceSynthesisModel      { get; set; } = string.Empty;
    // Real-time intelligence model
    public string IntelligenceDeployment { get; set; } = string.Empty;
    public string IntelligenceModel      { get; set; } = string.Empty;
    // Shared Azure fields
    public string IntelligenceEndpoint             { get; set; } = string.Empty;
    public string IntelligenceApiVersion           { get; set; } = string.Empty;
    /// <summary>Only saved to disk when non-empty (user explicitly typed a new key).</summary>
    public string IntelligenceApiKey               { get; set; } = string.Empty;

    public bool CaptureMicrophone { get; set; } = true;
    public bool CaptureLoopback   { get; set; } = true;

    public string NotesPath { get; set; } = string.Empty;
}
