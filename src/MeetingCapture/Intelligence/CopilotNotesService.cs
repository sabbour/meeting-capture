using System.ClientModel;
using System.Text.Json;
using MeetingCapture.Configuration;
using MeetingCapture.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace MeetingCapture.Intelligence;

/// <summary>
/// Uses the GitHub Models endpoint (or any OpenAI-compatible endpoint) via the
/// Azure.AI.OpenAI / OpenAI SDK to synthesize meeting notes from a transcript.
///
/// Assumptions:
/// - GITHUB_TOKEN env var or Intelligence:ApiKey contains a valid PAT with models:read scope
/// - If using Azure OpenAI directly, set Endpoint + ApiKey in appsettings.json
/// - GPT-4o is the default model; change via Intelligence:Model
/// </summary>
public sealed class CopilotNotesService : INotesIntelligence
{
    private readonly IntelligenceSettings _settings;
    private readonly ILogger<CopilotNotesService> _logger;

    private static readonly string SystemPrompt = """
        You are an expert meeting notes assistant. Given a meeting transcript, extract and return a JSON object with:
        - summary: string (2-4 sentence overview)
        - decisions: string[] (key decisions made)
        - actionItems: array of { description: string, owner: string|null, dueDate: string|null }
        - topics: string[] (main topics discussed)
        - nuances: string (tone, dynamics, notable context not captured elsewhere)

        Return ONLY valid JSON, no markdown fences, no preamble.
        """;

    public CopilotNotesService(
        IOptions<AppSettings> settings,
        ILogger<CopilotNotesService> logger)
    {
        _settings = settings.Value.Intelligence;
        _logger = logger;
    }

    public async Task<MeetingNotes> SynthesizeAsync(
        string meetingTitle,
        TranscriptionResult transcript,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "No API key configured for notes intelligence. " +
                "Set Intelligence:ApiKey in appsettings.json or the GITHUB_TOKEN environment variable.");

        if (transcript.WordCount == 0)
            throw new InvalidOperationException(
                "Cannot synthesize notes from an empty transcript.");

        var client = BuildChatClient(apiKey);

        _logger.LogInformation("Synthesizing notes for '{Title}' ({Words} words) via {Model}",
            meetingTitle, transcript.WordCount, _settings.Model);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(
                $"Meeting: {meetingTitle}\n\nTranscript:\n{transcript.FullText}")
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = _settings.MaxTokens
        };

        var response = await client.CompleteChatAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);

        var json = response.Value.Content[0].Text
            ?? throw new InvalidOperationException("LLM returned empty content");

        return ParseResponse(meetingTitle, transcript, json);
    }

    private ChatClient BuildChatClient(string apiKey)
    {
        var credential = new ApiKeyCredential(apiKey);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(_settings.Endpoint)
        };
        var openAiClient = new OpenAIClient(credential, clientOptions);
        return openAiClient.GetChatClient(_settings.Model);
    }

    private MeetingNotes ParseResponse(string title, TranscriptionResult transcript, string json)
    {
        var notes = new MeetingNotes
        {
            MeetingTitle = title,
            RawTranscript = transcript.FullText
        };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            notes.Summary = root.GetPropertyOrDefault("summary");
            notes.Nuances = root.GetPropertyOrDefault("nuances");

            if (root.TryGetProperty("decisions", out var decisions))
                foreach (var d in decisions.EnumerateArray())
                    notes.Decisions.Add(d.GetString() ?? string.Empty);

            if (root.TryGetProperty("topics", out var topics))
                foreach (var t in topics.EnumerateArray())
                    notes.Topics.Add(t.GetString() ?? string.Empty);

            if (root.TryGetProperty("actionItems", out var items))
                foreach (var item in items.EnumerateArray())
                    notes.ActionItems.Add(new ActionItem
                    {
                        Description = item.GetPropertyOrDefault("description"),
                        Owner = item.GetOptionalString("owner"),
                        DueDate = item.GetOptionalString("dueDate")
                    });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse LLM JSON response. Raw: {Json}", json);
            // Preserve raw transcript but surface the failure clearly
            notes.Summary = $"[Parse error — raw LLM response preserved in transcript field]";
        }

        return notes;
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            return _settings.ApiKey;
        return Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
    }
}

// Extension helpers to reduce JsonElement boilerplate
file static class JsonElementExtensions
{
    public static string GetPropertyOrDefault(this JsonElement el, string name, string @default = "")
        => el.TryGetProperty(name, out var prop) ? (prop.GetString() ?? @default) : @default;

    public static string? GetOptionalString(this JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null
            ? prop.GetString()
            : null;
}
