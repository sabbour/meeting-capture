using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions;
using MeetingCapture.Intelligence;

namespace MeetingCapture.Graph;

/// <summary>A single calendar event for "today", used by the idle-state flyout list.</summary>
public sealed record TodaysMeeting(
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsNow,
    bool IsUpcoming,
    string? OnlineMeetingProvider = null,
    string? JoinUrl = null,
    bool IsCancelled = false,
    bool IsAllDay = false);

/// <summary>
/// Optional Microsoft Graph wrapper using explicit browser sign-in and silent background refresh.
/// Exposes:
///   • <see cref="GetCurrentUserAsync"/>                   → GET /me
///   • <see cref="GetCurrentMeetingParticipantsAsync"/>    → GET /me/calendarView (current event)
///   • <see cref="GetTodaysMeetingsAsync"/>                → GET /me/calendarView (today)
///
/// Design constraints:
///   • Graph is OPTIONAL. Missing settings disable it; request failures remain explicit.
///   • <see cref="SignInAsync"/> is the ONLY path that triggers interactive auth. Background
///     Graph calls do silent token refresh only and return null/empty if no token exists.
///   • Tokens are cached in memory only. Nothing is written to disk.
///   • Requires your own Azure AD app registration (see README — the Office client ID d3590ed6
///     is blocked by AADSTS65002 for third-party use).
/// </summary>
public sealed class GraphService : IAsyncDisposable
{
    // YOUR Azure AD app registration client ID.
    // Register at https://portal.azure.com → Azure Active Directory → App registrations:
    //   • Supported account types: "Accounts in this organizational directory only (single tenant)"
    //   • Platform: Mobile and desktop application → redirect URI: http://localhost
    //   • API permissions (delegated): User.Read, Calendars.Read
    // Then put the Application (client) ID in appsettings.json under "Graph:ClientId",
    // and your tenant ID under "Graph:TenantId".
    private static readonly string[] Scopes =
        { "User.Read", "Calendars.Read", "Mail.Read", "Files.Read", "Chat.Read" };

    private readonly ILogger<GraphService> _logger;
    private readonly string _clientId;
    private readonly string _authority;
    private IPublicClientApplication? _pca;
    private readonly object _stateGate = new();
    private readonly BoundedSignIn _signIn;

    private GraphServiceClient? _graph;
    private string? _signedInUserDisplayName;
    private volatile bool _disposed;
    private volatile bool _authenticated;
    private long _authRevision;

    // HWND stored for API compatibility; not used now that WAM is removed.
    private IntPtr _parentHwnd = IntPtr.Zero;

    public GraphService(ILogger<GraphService> logger, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _logger = logger;
        _signIn = new BoundedSignIn(TimeSpan.FromSeconds(90),
            error => _logger.LogWarning("Graph sign-in cleanup observed {Type}.", error.GetType().Name));
        _clientId = config["Graph:ClientId"] ?? string.Empty;
        var tenant = config["Graph:TenantId"] ?? string.Empty;
        _authority = string.Empty;
        if (!Guid.TryParse(_clientId, out var clientId) || clientId == Guid.Empty ||
            !(Guid.TryParse(tenant, out var tenantId) && tenantId != Guid.Empty ||
              tenant.Contains('.') && Uri.CheckHostName(tenant) == UriHostNameType.Dns))
        {
            ConfigurationError = "Graph requires an application client ID and a tenant-specific GUID or domain. Configure Graph and restart.";
            return;
        }
        _authority = $"https://login.microsoftonline.com/{tenant}";
        IsConfigured = true;
    }

    public bool IsConfigured { get; }
    public string? ConfigurationError { get; }

    /// <summary>Kept for API compatibility; no-op now that WAM is removed.</summary>
    public void SetParentWindow(IntPtr hwnd) => _parentHwnd = hwnd;

    /// <summary>True once a token has been acquired at least once this session.</summary>
    public bool IsAuthenticated => _authenticated && !_disposed;
    public bool IsSignInBusy => _signIn.IsBusy;
    public string? SignedInUserDisplayName => IsAuthenticated ? _signedInUserDisplayName : null;

    /// <summary>
    /// Raised when the user successfully signs in (carries their display name)
    /// or signs out (carries <c>null</c>).
    /// </summary>
    public event EventHandler<string?>? AuthStateChanged;

    /// <summary>
    /// Explicitly signs the user in via browser (http://localhost redirect).
    /// This is the ONLY path that triggers interactive authentication.
    /// Note: corporate tenant IT policy may require admin consent — if so this throws
    /// and the caller should surface a clear message to the user.
    /// </summary>
    public async Task<string?> SignInAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw IntelligenceErrors.Create(IntelligenceFailureKind.Disabled, "Microsoft Graph");
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? displayName = null;
        try
        {
            await _signIn.RunAsync(async ct =>
            {
                var pca = GetOrBuildPca();
                var token = await TryAcquireTokenSilentCoreAsync(pca, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (token is null)
                {
                    var result = await pca.AcquireTokenInteractive(Scopes)
                        .WithUseEmbeddedWebView(false)
                        .WithPrompt(Prompt.SelectAccount)
                        .ExecuteAsync(ct).ConfigureAwait(false);
                    token = result?.AccessToken;
                }
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(token))
                    throw IntelligenceErrors.Create(IntelligenceFailureKind.Authentication, "Microsoft Graph");

                var revision = Interlocked.Read(ref _authRevision) + 1;
                var candidate = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                    new MsalAccessTokenProvider(requestCt => TryAcquireTokenSilentCoreAsync(pca, requestCt, revision))));
                try
                {
                    var profile = await candidate.Me.GetAsync(request =>
                        request.QueryParameters.Select = new[] { "displayName" }, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(profile?.DisplayName))
                        throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (profile)");
                    return new PreparedSignIn(candidate, profile.DisplayName);
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }
            }, prepared =>
            {
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _graph?.Dispose();
                    _graph = prepared.Client;
                    _signedInUserDisplayName = displayName = prepared.DisplayName;
                    Interlocked.Increment(ref _authRevision);
                    _authenticated = true;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { throw IntelligenceErrors.Create(IntelligenceFailureKind.Timeout, "Microsoft Graph (sign-in)"); }
        catch (Exception ex) { throw GraphFailure(ex, "sign-in"); }
        AuthStateChanged?.Invoke(this, displayName);
        return displayName;
    }

    private sealed record PreparedSignIn(GraphServiceClient Client, string DisplayName) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private IPublicClientApplication GetOrBuildPca()
    {
        if (_pca is not null)
            return _pca;

        _pca = PublicClientApplicationBuilder
            .Create(_clientId)
            .WithAuthority(_authority)
            .WithRedirectUri("http://localhost")
            .Build();

        _logger.LogInformation("Graph browser authentication configured.");
        return _pca;
    }

    /// <summary>
    /// Resolves the signed-in user's display name and email via GET /me.
    /// Returns <c>null</c> when Graph is unavailable or the user declines sign-in.
    /// </summary>
    public async Task<(string DisplayName, string? Email)?> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var revision = Interlocked.Read(ref _authRevision);
        try
        {
            var graph = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            if (graph is null)
                return null;

            var me = await graph.Me.GetAsync(rc =>
            {
                rc.QueryParameters.Select = new[] { "displayName", "mail", "userPrincipalName" };
            }, cancellationToken).ConfigureAwait(false);

            if (me is null || string.IsNullOrWhiteSpace(me.DisplayName))
                throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (profile)");

            lock (_stateGate)
                if (!_disposed && revision == _authRevision && ReferenceEquals(graph, _graph))
                    _signedInUserDisplayName = me.DisplayName;
            var email = string.IsNullOrWhiteSpace(me.Mail) ? me.UserPrincipalName : me.Mail;
            return (me.DisplayName!, email);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw GraphFailure(ex, "profile"); }
    }

    /// <summary>
    /// Returns the attendee display names of the meeting currently in progress on the user's
    /// calendar (started &lt;= now &lt;= ended), excluding the signed-in user. Returns an empty
    /// list when no meeting is active or Graph is unavailable.
    /// </summary>
    public async Task<List<string>> GetCurrentMeetingParticipantsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var graph = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            if (graph is null)
                return new List<string>();

            var now = DateTimeOffset.UtcNow;
            var start = now.AddMinutes(-15);
            var end = now.AddHours(4);

            var response = await graph.Me.CalendarView.GetAsync(rc =>
            {
                rc.QueryParameters.StartDateTime = start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                rc.QueryParameters.EndDateTime = end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                rc.QueryParameters.Select = new[] { "subject", "attendees", "start", "end" };
                rc.QueryParameters.Top = 5;
                rc.QueryParameters.Orderby = new[] { "start/dateTime" };
            }, cancellationToken).ConfigureAwait(false);

            var events = response?.Value
                ?? throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (calendar)");
            if (events.Count == 0)
                return new List<string>();

            // Pick the event that is currently active (started <= now <= ended). calendarView
            // returns times in UTC when no Prefer:outlook.timezone header is sent.
            var active = events.FirstOrDefault(ev =>
            {
                var s = ParseGraphUtc(ev.Start?.DateTime);
                var e = ParseGraphUtc(ev.End?.DateTime);
                if (s is null || e is null)
                    throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (calendar participants)");
                return s <= now && now <= e;
            });

            if (active?.Attendees is null)
            {
                _logger.LogInformation("Graph: no active meeting found in calendar view.");
                return new List<string>();
            }

            var self = _signedInUserDisplayName;
            var names = active.Attendees
                .Select(a => a.EmailAddress?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .Where(n => self is null || !string.Equals(n, self, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return names;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw GraphFailure(ex, "calendar participants"); }
    }

    /// <summary>
    /// Returns the user's calendar events for today (local time), ordered by start time, for the
    /// idle-state flyout calendar list. Each entry is flagged as currently in-progress
    /// (<see cref="TodaysMeeting.IsNow"/>) or upcoming (<see cref="TodaysMeeting.IsUpcoming"/>).
    /// Returns an empty list when Graph is unavailable so the UI can hide the section.
    /// </summary>
    public async Task<List<TodaysMeeting>> GetTodaysMeetingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var graph = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            if (graph is null)
                return new List<TodaysMeeting>();

            var nowLocal = DateTimeOffset.Now;
            var startLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset);
            var endLocal = startLocal.AddDays(1).AddSeconds(-1);

            var response = await graph.Me.CalendarView.GetAsync(rc =>
            {
                rc.QueryParameters.StartDateTime = startLocal.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                rc.QueryParameters.EndDateTime = endLocal.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
                rc.QueryParameters.Select = new[] { "subject", "start", "end", "onlineMeetingProvider", "onlineMeeting", "isCancelled", "isAllDay" };
                rc.QueryParameters.Top = 100;
                rc.QueryParameters.Orderby = new[] { "start/dateTime" };
            }, cancellationToken).ConfigureAwait(false);

            var events = response?.Value
                ?? throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (calendar)");
            var nextLink = response?.OdataNextLink;
            for (var page = 1; !string.IsNullOrEmpty(nextLink); page++)
            {
                // Never select a supposedly unique meeting from an incomplete calendar.
                if (page >= 10)
                    throw IntelligenceErrors.Create(IntelligenceFailureKind.Unknown, "Microsoft Graph (calendar)");
                var next = await graph.Me.CalendarView.WithUrl(nextLink).GetAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                events.AddRange(next?.Value ??
                    throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (calendar)"));
                nextLink = next?.OdataNextLink;
            }
            if (events.Count == 0)
                return new List<TodaysMeeting>();

            var now = DateTimeOffset.UtcNow;
            var result = new List<TodaysMeeting>();
            foreach (var ev in events)
            {
                var s = ParseGraphUtc(ev.Start?.DateTime);
                var e = ParseGraphUtc(ev.End?.DateTime);
                if (s is null || e is null)
                    throw IntelligenceErrors.Create(IntelligenceFailureKind.MalformedResponse, "Microsoft Graph (calendar)");

                var isNow = s <= now && now <= e;
                var isUpcoming = s > now;
                var subject = string.IsNullOrWhiteSpace(ev.Subject) ? "(no subject)" : ev.Subject!.Trim();
                result.Add(new TodaysMeeting(subject, s.Value.ToLocalTime(), e.Value.ToLocalTime(), isNow, isUpcoming,
                    ev.OnlineMeetingProvider?.ToString(), ev.OnlineMeeting?.JoinUrl,
                    ev.IsCancelled == true, ev.IsAllDay == true));
            }

            _logger.LogInformation("Graph: today's calendar has {Count} event(s).", result.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw GraphFailure(ex, "calendar"); }
    }

    // ── Auth ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached Graph client when already authenticated, otherwise <c>null</c>.
    /// Never triggers an interactive prompt — call <see cref="SignInAsync"/> for that.
    /// </summary>
    private Task<GraphServiceClient?> GetClientAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(IsAuthenticated ? _graph : null);
    }

    /// <summary>
    /// Silently acquires an access token for the specified scopes.
    /// Returns <c>null</c> when the user is not signed in, when silent refresh requires
    /// interaction, or when the requested scopes have not been consented.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string[] scopes, CancellationToken cancellationToken)
    {
        var revision = Interlocked.Read(ref _authRevision);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConfigured) throw IntelligenceErrors.Create(IntelligenceFailureKind.Disabled, "Microsoft Graph");
        if (_pca is null || !IsAuthenticated)
            throw IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, "Microsoft Graph");
        try
        {
            var accounts = await _pca.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault();
            if (account is null)
                throw IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, "Microsoft Graph");

            var result = await _pca.AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result?.AccessToken))
                throw IntelligenceErrors.Create(IntelligenceFailureKind.Authentication, "Microsoft Graph");
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            InvalidateAuthentication(revision);
            throw IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, "Microsoft Graph");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw GraphFailure(ex, "authentication");
        }
    }

    /// <summary>
    /// Silent-only token refresh used for per-request bearer token renewal inside the
    /// Graph SDK's <see cref="MsalAccessTokenProvider"/>. Returns <c>null</c> (empty
    /// bearer) when the cached token has expired and no silent refresh is possible.
    /// The token provider surfaces that as a typed signed-out failure.
    /// </summary>
    private async Task<string?> TryAcquireTokenSilentCoreAsync(IPublicClientApplication pca, CancellationToken cancellationToken,
        long? revision = null)
    {
        try
        {
            var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault();
            if (account is null)
                return null;

            var result = await pca.AcquireTokenSilent(Scopes, account)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result?.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            // Session expired — user must call SignInAsync again.
            _logger.LogInformation("Graph: silent token refresh requires UI interaction — sign-in needed.");
            if (revision is { } current) InvalidateAuthentication(current);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw GraphFailure(ex, "authentication");
        }
    }

    private void InvalidateAuthentication(long revision)
    {
        lock (_stateGate)
        {
            if (_disposed || revision != _authRevision) return;
            _authenticated = false;
        }
        AuthStateChanged?.Invoke(this, null);
    }

    private IntelligenceException GraphFailure(Exception exception, string source)
    {
        var provider = $"Microsoft Graph ({source})";
        var failure = exception switch
        {
            MsalUiRequiredException => IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, provider),
            MsalServiceException { StatusCode: 401 } => IntelligenceErrors.Create(IntelligenceFailureKind.Authentication, provider),
            MsalServiceException { StatusCode: 403 } => IntelligenceErrors.Create(IntelligenceFailureKind.Authorization, provider),
            MsalException => IntelligenceErrors.Create(IntelligenceFailureKind.Authentication, provider),
            ApiException { ResponseStatusCode: 401 } => IntelligenceErrors.Create(IntelligenceFailureKind.Authentication, provider),
            ApiException { ResponseStatusCode: 403 } => IntelligenceErrors.Create(IntelligenceFailureKind.Authorization, provider),
            _ => IntelligenceErrors.FromException(exception, provider)
        };
        _logger.LogWarning("Graph {Source} request failed: {Kind}", source, failure.Failure.Kind);
        return failure;
    }

    public ValueTask DisposeAsync()
    {
        _signIn.Dispose();
        lock (_stateGate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _authenticated = false;
            _graph?.Dispose();
            _graph = null;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Bridges MSAL token acquisition to Kiota's <see cref="IAccessTokenProvider"/> so the Graph SDK
    /// can request a (silently refreshed) bearer token per request.
    /// </summary>
    private sealed class MsalAccessTokenProvider(Func<CancellationToken, Task<string?>> getToken)
        : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } =
            new(new[] { "graph.microsoft.com" });

        public async Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
        {
            var token = await getToken(cancellationToken).ConfigureAwait(false);
            return token ?? throw IntelligenceErrors.Create(IntelligenceFailureKind.SignedOut, "Microsoft Graph");
        }
    }

    private static DateTimeOffset? ParseGraphUtc(string? graphDateTime)
    {
        if (string.IsNullOrWhiteSpace(graphDateTime))
            return null;

        // Graph calendarView returns naive UTC date-times (no offset) by default.
        if (DateTime.TryParse(
                graphDateTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        return null;
    }
}
