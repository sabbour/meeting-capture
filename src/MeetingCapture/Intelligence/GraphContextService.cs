using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MeetingCapture.Graph;
using Microsoft.Extensions.Logging;

namespace MeetingCapture.Intelligence;

/// <summary>
/// Searches M365 (email, Teams chats, OneDrive/SharePoint documents) for content
/// related to the current meeting topic using the Graph unified search API.
///
/// Missing consent and source failures are explicit, never presented as an empty successful search.
/// </summary>
public sealed class GraphContextService
{
    // Same scopes that GraphService now requests at interactive sign-in.
    private static readonly string[] SearchScopes =
        { "Mail.Read", "Files.Read", "Chat.Read" };

    private const string SearchEndpoint = "https://graph.microsoft.com/v1.0/search/query";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly GraphService _graphService;
    private readonly ILogger<GraphContextService> _logger;

    public GraphContextService(GraphService graphService, ILogger<GraphContextService> logger)
    {
        _graphService = graphService;
        _logger = logger;
    }

    public async Task<List<RelatedItem>> SearchRelatedAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_graphService.IsConfigured)
            throw IntelligenceErrors.Create(IntelligenceFailureKind.Disabled, "Microsoft Graph");
        if (!_graphService.IsAuthenticated)
            throw IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, "Microsoft Graph");
        if (string.IsNullOrWhiteSpace(query))
            throw IntelligenceErrors.Create(IntelligenceFailureKind.Configuration, "Microsoft Graph");

        var token = await _graphService.GetAccessTokenAsync(SearchScopes, ct).ConfigureAwait(false);
        if (token is null)
            throw IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, "Microsoft Graph");

        // These entity types cannot be mixed in a single Graph search request.
        var sources = await Task.WhenAll(
            SearchSourceAsync(query, "message", "email", token, ct),
            SearchSourceAsync(query, "chatMessage", "Teams", token, ct),
            SearchSourceAsync(query, "driveItem", "documents", token, ct)).ConfigureAwait(false);
        return sources.SelectMany(items => items)
            .OrderByDescending(item => item.Date ?? DateTimeOffset.MinValue).Take(9).ToList();
    }

    private static string BuildRequestBody(string query, string[] entityTypes)
        => JsonSerializer.Serialize(new
        {
            requests = new[]
            {
                new
                {
                    entityTypes,
                    query = new { queryString = query },
                    from = 0,
                    size = 9
                }
            }
        });

    private async Task<List<RelatedItem>> SearchSourceAsync(
        string query, string entityType, string source, string token, CancellationToken ct)
    {
        var provider = $"Microsoft Graph ({source})";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(BuildRequestBody(query, [entityType]), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw IntelligenceErrors.Create(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => IntelligenceFailureKind.Authentication,
                    HttpStatusCode.Forbidden => IntelligenceFailureKind.Authorization,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => IntelligenceFailureKind.Timeout,
                    _ => IntelligenceFailureKind.Unknown
                }, provider);
            return ParseSearchResponse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), provider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var failure = IntelligenceErrors.FromException(ex, provider);
            _logger.LogWarning("Graph search {Source} failed: {Kind}", source, failure.Failure.Kind);
            throw failure;
        }
    }

    public static List<RelatedItem> ParseSearchResponse(string json, string provider = "Microsoft Graph")
    {
        if (string.IsNullOrWhiteSpace(json))
            throw IntelligenceErrors.Create(IntelligenceFailureKind.EmptyResponse, provider);
        var results = new List<RelatedItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var valueArray = root.GetProperty("value");
            if (valueArray.GetArrayLength() == 0) throw new JsonException();

            foreach (var searchResponse in valueArray.EnumerateArray())
            {
                if (searchResponse.TryGetProperty("error", out _)) throw new JsonException();
                var containers = searchResponse.GetProperty("hitsContainers");
                if (containers.GetArrayLength() == 0) throw new JsonException();

                foreach (var container in containers.EnumerateArray())
                {
                    if (!container.TryGetProperty("hits", out var hits))
                    {
                        if (container.TryGetProperty("total", out var total) && total.GetInt32() == 0)
                            continue;
                        throw new JsonException();
                    }

                    foreach (var hit in hits.EnumerateArray())
                    {
                        var resource = hit.GetProperty("resource");

                        var odataType = resource.TryGetProperty("@odata.type", out var odataProp)
                            ? (odataProp.GetString() ?? string.Empty)
                            : string.Empty;

                        var item = odataType switch
                        {
                            "#microsoft.graph.message"     => ParseEmail(resource),
                            "#microsoft.graph.chatMessage" => ParseTeamsMessage(resource, hit),
                            "#microsoft.graph.driveItem"   => ParseDocument(resource),
                            _ => throw new JsonException()
                        };

                        if (item is not null)
                            results.Add(item);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, provider);
        }

        return results
            .OrderByDescending(r => r.Date ?? DateTimeOffset.MinValue)
            .Take(9)
            .ToList();
    }

    private static RelatedItem ParseEmail(JsonElement resource)
    {
        var subject = GetString(resource, "subject") ?? "(no subject)";
        var snippet = GetString(resource, "bodyPreview") ?? string.Empty;
        var webUrl  = GetString(resource, "webLink");
        var date    = TryParseDate(GetString(resource, "receivedDateTime"));
        return new RelatedItem("Email", subject, snippet, webUrl, date);
    }

    private static RelatedItem ParseTeamsMessage(JsonElement resource, JsonElement hit)
    {
        // Prefer the hit-level summary, fall back to body content.
        var summary = GetString(hit, "summary");
        if (string.IsNullOrWhiteSpace(summary) && resource.TryGetProperty("body", out var body))
            summary = GetString(body, "content") ?? string.Empty;

        var title   = string.IsNullOrWhiteSpace(summary) ? "(Teams message)"
                    : summary.Length > 60 ? summary[..60] + "\u2026"
                    : summary;
        var webUrl  = GetString(resource, "webUrl");
        var date    = TryParseDate(GetString(resource, "createdDateTime"));
        return new RelatedItem("Teams", title, summary ?? string.Empty, webUrl, date);
    }

    private static RelatedItem ParseDocument(JsonElement resource)
    {
        var name    = GetString(resource, "name") ?? "(document)";
        var snippet = GetString(resource, "summary") ?? string.Empty;
        var webUrl  = GetString(resource, "webUrl");
        var date    = TryParseDate(GetString(resource, "lastModifiedDateTime"));
        return new RelatedItem("Document", name, snippet, webUrl, date);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : throw new JsonException();
    }

    private static DateTimeOffset? TryParseDate(string? value)
        => value is null ? null : DateTimeOffset.TryParse(value, out var date) ? date : throw new JsonException();
}
