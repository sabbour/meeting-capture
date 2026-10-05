using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace MeetingCapture.WorkIq;

public sealed class WorkIqService : IDisposable, IRelatedContextSource
{
    public const string ClientId = "ba081686-5d24-4bc6-a0d6-d034ecffed87";
    public static readonly string[] Scopes = ["fdcc1f02-fc51-4226-8753-f668596af7f7/WorkIQAgent.Ask"];
    internal static readonly string[] RelatedContextCapabilities =
        ["Meetings", "TeamsMessages", "Email"];
    private const int MaxPhotoCacheEntries = 64;
    private sealed record AuthStateSnapshot(bool IsAuthenticated, long Revision);
    private readonly ILogger<WorkIqService> _logger;
    private readonly ProtectedWorkIqCache _cache;
    private readonly AiUsageMonitor _usage;
    private readonly AiDiagnosticsStream _diagnostics;
    private readonly BoundedSignIn _signIn;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _contextRequests = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkIqProfilePhoto?> _photoCache = new(StringComparer.OrdinalIgnoreCase);
    private (long Revision, WorkIqCurrentIdentity Identity)? _identityCache;
    private IPublicClientApplication? _app;
    private long _revision;
    private AuthStateSnapshot _authStateSnapshot = new(false, 0);
    private bool _disposed;
    public (bool IsAuthenticated, long Revision) AuthState
    {
        get
        {
            var snapshot = Volatile.Read(ref _authStateSnapshot);
            return (snapshot.IsAuthenticated, snapshot.Revision);
        }
    }
    public bool IsAuthenticated => AuthState.IsAuthenticated;
    public bool IsAvailable => IsAuthenticated;
    public bool IsSignInBusy => _signIn.IsBusy;
    public long Revision => Volatile.Read(ref _authStateSnapshot).Revision;
    public event EventHandler? AuthStateChanged;
    public event EventHandler? BrowserOpened;

    public WorkIqService(
        ILogger<WorkIqService> logger,
        ProtectedWorkIqCache? cache = null,
        AiUsageMonitor? usage = null,
        AiDiagnosticsStream? diagnostics = null)
    {
        _logger = logger;
        _cache = cache ?? new ProtectedWorkIqCache();
        _usage = usage ?? new AiUsageMonitor();
        _diagnostics = diagnostics ?? new AiDiagnosticsStream();
        _signIn = new BoundedSignIn(TimeSpan.FromSeconds(90),
            error => _logger.LogWarning("WorkIQ sign-in cleanup: {Type}", error.GetType().Name));
    }

    private static IPublicClientApplication CreateApp() => PublicClientApplicationBuilder.Create(ClientId)
        .WithAuthority("https://login.microsoftonline.com/organizations")
        .WithRedirectUri("http://localhost:12798")
        .Build();

    private static WorkIqMcpClient CreateClient(TimeSpan? requestTimeout = null) => new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    }, requestTimeout);

    public async Task SignInAsync(CancellationToken ct)
    {
        var revision = Revision;
        try
        {
            await _signIn.RunAsync(async token =>
            {
                var app = CreateApp();
                _cache.Attach(app);
                var auth = await app.AcquireTokenInteractive(Scopes)
                    .WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(new SystemWebViewOptions
                    {
                        HtmlMessageSuccess = "<html><body>Sign-in response received. Return to Meeting Capture for connection status.</body></html>",
                        HtmlMessageError = "<html><body>Sign-in did not complete. Return to Meeting Capture.</body></html>",
                        OpenBrowserAsync = uri =>
                        {
                            using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                            { UseShellExecute = true });
                            BrowserOpened?.Invoke(this, EventArgs.Empty);
                            return Task.CompletedTask;
                        }
                    }).ExecuteAsync(token).ConfigureAwait(false);
                using var client = CreateClient();
                await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false);
                return new Prepared(app);
            }, prepared =>
            {
                lock (_gate)
                {
                    CheckRevision(revision);
                    var app = prepared.Application ?? throw new WorkIqException(WorkIqFailure.Authentication);
                    _cache.Save(app);
                    _app = app;
                    _photoCache.Clear();
                    Interlocked.Increment(ref _revision);
                    PublishAuthStateLocked();
                }
            }, ct).ConfigureAwait(false);
            AuthStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Failure(ex); }
    }

    /// <summary>Restore only this application's DPAPI-protected cache; never opens a browser.</summary>
    public async Task RestoreAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var revision = Revision;
        var entered = false;
        try
        {
            await _requests.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            var app = CreateApp();
            _cache.Load(app);
            var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (accounts.Length == 0) return;
            if (accounts.Length != 1) throw new WorkIqException(WorkIqFailure.Authentication);
            var auth = await app.AcquireTokenSilent(Scopes, accounts[0]).ExecuteAsync(token).ConfigureAwait(false);
            using var client = CreateClient();
            await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                CheckRevision(revision);
                _cache.Save(app);
                _app = app;
                _photoCache.Clear();
                Interlocked.Increment(ref _revision);
                PublishAuthStateLocked();
            }
            AuthStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Failure(ex); }
        finally { if (entered) _requests.Release(); }
    }

    public async Task<List<MeetingTitleCandidate>> GetCalendarAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var revision = Revision;
        var entered = false;
        try
        {
            await _requests.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            IPublicClientApplication app;
            lock (_gate)
            {
                CheckRevision(revision);
                app = _app ?? throw new WorkIqException(WorkIqFailure.SignedOut);
            }
            var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (accounts.Length != 1) throw new WorkIqException(WorkIqFailure.SignedOut);
            var auth = await app.AcquireTokenSilent(Scopes, accounts[0]).ExecuteAsync(token).ConfigureAwait(false);
            using var client = CreateClient();
            await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false);
            var response = await client.FetchCalendarResultAsync(auth.AccessToken, start, end, token).ConfigureAwait(false);
            var meetings = WorkIqMcpClient.ParseCalendar(response);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                CheckRevision(revision);
                _cache.Save(app);
            }
            return meetings;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var failure = Failure(ex);
            if (failure.Kind is WorkIqFailure.Authentication or WorkIqFailure.SignedOut)
            {
                var changed = false;
                lock (_gate)
                {
                    if (revision == _revision)
                    {
                        _app = null;
                        Interlocked.Increment(ref _revision);
                        PublishAuthStateLocked();
                        changed = true;
                    }
                }
                if (changed) AuthStateChanged?.Invoke(this, EventArgs.Empty);
            }
            throw failure;
        }
        finally { if (entered) _requests.Release(); }
    }

    /// <summary>Returns a photo only for a selected attendee's verified email address.</summary>
    public Task<WorkIqProfilePhoto?> GetProfilePhotoAsync(string? verifiedEmail, CancellationToken ct)
    {
        var email = verifiedEmail?.Trim();
        if (string.IsNullOrWhiteSpace(email) ||
            !System.Net.Mail.MailAddress.TryCreate(email, out var parsed) ||
            !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<WorkIqProfilePhoto?>(null);
        return GetProfilePhotoCoreAsync("user:" + email, email, currentUser: false, ct);
    }

    /// <summary>Returns the authenticated WorkIQ user's photo without exposing their directory ID.</summary>
    public Task<WorkIqProfilePhoto?> GetCurrentUserPhotoAsync(CancellationToken ct)
        => GetProfilePhotoCoreAsync("$me", null, currentUser: true, ct);

    public async Task<RelatedContextSnapshot> GetRelatedContextAsync(
        ContextQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var observation = _usage.Begin(AiCapability.WorkIqRetrieval, query.SessionId);
        var diagnostic = _diagnostics.Begin(
            AiCapability.WorkIqRetrieval,
            "WorkIQ",
            null,
            query.TranscriptRevision,
            null,
            query.SessionId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        var token = deadline.Token;
        var revision = Revision;
        var entered = false;
        try
        {
            await _contextRequests.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            IPublicClientApplication app;
            lock (_gate)
            {
                CheckRevision(revision);
                app = _app ?? throw new WorkIqException(WorkIqFailure.SignedOut);
            }
            var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (accounts.Length != 1) throw new WorkIqException(WorkIqFailure.SignedOut);
            var auth = await app.AcquireTokenSilent(Scopes, accounts[0]).ExecuteAsync(token).ConfigureAwait(false);
            using var client = CreateClient(TimeSpan.FromSeconds(70));
            await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false);
            var currentIdentity = await ResolveCurrentIdentityAsync(
                client, auth.AccessToken, revision, token).ConfigureAwait(false);
            _logger.LogInformation("WorkIQ related-context query started.");
            var retrievedAt = DateTimeOffset.UtcNow;
            var familySnapshots = new List<RelatedContextSnapshot>();
            foreach (var capability in RelatedContextCapabilities)
            {
                try
                {
                    observation.CallStarted();
                    diagnostic.CallStarted();
                    var response = await client.RetrieveRelatedContextResultAsync(
                        auth.AccessToken, query.Topic, [capability], token).ConfigureAwait(false);
                    var family = WorkIqMcpClient.ParseRetrievedContext(
                        response,
                        retrievedAt,
                        currentIdentity);
                    familySnapshots.Add(family);
                    _logger.LogInformation(
                        "WorkIQ related-context family completed: {Capability} {Availability} {Count}.",
                        capability, family.Availability, family.Items.Count);
                }
                catch (Exception ex)
                {
                    var familyFailure = Failure(ex);
                    if (familyFailure.Kind != WorkIqFailure.Unsupported)
                        throw familyFailure;
                    familySnapshots.Add(new RelatedContextSnapshot(retrievedAt, [],
                        RelatedContextAvailability.Unavailable));
                }
            }
            var snapshot = WorkIqMcpClient.MergeRetrievedContexts(familySnapshots, retrievedAt);
            _logger.LogInformation(
                "WorkIQ related-context query completed: {Availability} {Count}.",
                snapshot.Availability, snapshot.Items.Count);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                CheckRevision(revision);
                _cache.Save(app);
            }
            if (snapshot.Availability == RelatedContextAvailability.Unavailable)
            {
                observation.Fail(AiRequestFailure.Unsupported);
                diagnostic.Fail(AiRequestFailure.Unsupported);
            }
            else
            {
                observation.Complete(snapshot.Availability == RelatedContextAvailability.Available);
                diagnostic.Complete(snapshot.Availability == RelatedContextAvailability.Available);
            }
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            observation.Cancel();
            diagnostic.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            var failure = Failure(ex);
            if (failure.Kind == WorkIqFailure.Denied)
            {
                observation.Fail(AiRequestFailure.Authorization);
                diagnostic.Fail(AiRequestFailure.Authorization);
                return new RelatedContextSnapshot(DateTimeOffset.UtcNow, [],
                    RelatedContextAvailability.Unavailable,
                    "WorkIQ related context is unavailable for this account.");
            }
            if (failure.Kind == WorkIqFailure.Unsupported)
            {
                observation.Fail(AiRequestFailure.Unsupported);
                diagnostic.Fail(AiRequestFailure.Unsupported);
                return new RelatedContextSnapshot(DateTimeOffset.UtcNow, [],
                    RelatedContextAvailability.Unavailable,
                    "WorkIQ related-context retrieval is not supported by the connected service.");
            }
            var diagnosticFailure = failure.Kind switch
            {
                WorkIqFailure.SignedOut or WorkIqFailure.Authentication =>
                    AiRequestFailure.Authentication,
                WorkIqFailure.Network => AiRequestFailure.Network,
                WorkIqFailure.Timeout => AiRequestFailure.Timeout,
                _ => AiRequestFailure.Unknown
            };
            observation.Fail(diagnosticFailure);
            diagnostic.Fail(diagnosticFailure);
            if (failure.Kind is WorkIqFailure.Authentication or WorkIqFailure.SignedOut)
            {
                var changed = false;
                lock (_gate)
                {
                    if (revision == _revision)
                    {
                        _app = null;
                        _photoCache.Clear();
                        Interlocked.Increment(ref _revision);
                        PublishAuthStateLocked();
                        changed = true;
                    }
                }
                if (changed) AuthStateChanged?.Invoke(this, EventArgs.Empty);
            }
            throw failure;
        }
        finally { if (entered) _contextRequests.Release(); }
    }

    /// <summary>Resolves the signed-in tenant identity (object ID plus verified mail/UPN aliases)
    /// used to prove WorkIQ result participation. Cached per auth revision; the MSAL account
    /// username is not accepted because only `/me` mail/UPN values are verified aliases.</summary>
    private async Task<WorkIqCurrentIdentity> ResolveCurrentIdentityAsync(
        WorkIqMcpClient client, string accessToken, long revision, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_identityCache is { } cached && cached.Revision == revision)
                return cached.Identity;
        }
        var result = await client.FetchCurrentUserIdentityResultAsync(accessToken, ct).ConfigureAwait(false);
        var identity = WorkIqMcpClient.ParseCurrentUserIdentity(result);
        lock (_gate)
            _identityCache = (revision, identity);
        return identity;
    }

    private async Task<WorkIqProfilePhoto?> GetProfilePhotoCoreAsync(
        string cacheKey, string? userIdentity, bool currentUser, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var revision = Revision;
        var entered = false;
        try
        {
            await _requests.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            IPublicClientApplication app;
            lock (_gate)
            {
                CheckRevision(revision);
                if (_photoCache.TryGetValue(cacheKey, out var cached))
                    return cached;
                if (_photoCache.Count >= MaxPhotoCacheEntries)
                    return null;
                app = _app ?? throw new WorkIqException(WorkIqFailure.SignedOut);
            }

            var accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (accounts.Length != 1) throw new WorkIqException(WorkIqFailure.SignedOut);
            var auth = await app.AcquireTokenSilent(Scopes, accounts[0]).ExecuteAsync(token).ConfigureAwait(false);
            using var client = CreateClient();
            await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false);
            var identity = currentUser
                ? WorkIqMcpClient.ParseCurrentUserId(
                    await client.FetchCurrentUserResultAsync(auth.AccessToken, token).ConfigureAwait(false))
                : WorkIqMcpClient.ParseUserId(
                    await client.FetchUserResultAsync(
                        auth.AccessToken, userIdentity!, token).ConfigureAwait(false));
            var metadata = await client.FetchProfilePhotoMetadataResultAsync(
                auth.AccessToken, identity, token).ConfigureAwait(false);
            WorkIqProfilePhoto? photo = null;
            if (WorkIqMcpClient.HasProfilePhoto(metadata))
            {
                photo = WorkIqMcpClient.ParseProfilePhoto(
                    await client.FetchProfilePhotoResultAsync(auth.AccessToken, identity, token).ConfigureAwait(false));
            }

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                CheckRevision(revision);
                _photoCache[cacheKey] = photo;
                _cache.Save(app);
            }
            return photo;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var failure = Failure(ex);
            if (failure.Kind is WorkIqFailure.Denied or WorkIqFailure.Unsupported)
            {
                lock (_gate)
                    if (!_disposed && revision == _revision && _photoCache.Count < MaxPhotoCacheEntries)
                        _photoCache[cacheKey] = null;
                return null;
            }
            if (failure.Kind is WorkIqFailure.Authentication or WorkIqFailure.SignedOut)
            {
                var changed = false;
                lock (_gate)
                {
                    if (revision == _revision)
                    {
                        _app = null;
                        _photoCache.Clear();
                        Interlocked.Increment(ref _revision);
                        PublishAuthStateLocked();
                        changed = true;
                    }
                }
                if (changed) AuthStateChanged?.Invoke(this, EventArgs.Empty);
            }
            throw failure;
        }
        finally { if (entered) _requests.Release(); }
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _app = null;
            _photoCache.Clear();
            Interlocked.Increment(ref _revision);
            try { _cache.Clear(); }
            finally { PublishAuthStateLocked(); }
        }
        AuthStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishAuthStateLocked()
    {
        Volatile.Write(ref _authStateSnapshot,
            new AuthStateSnapshot(_app is not null && !_disposed, Interlocked.Read(ref _revision)));
    }

    private void CheckRevision(long revision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (revision != _revision) throw new OperationCanceledException("WorkIQ connection changed.");
    }

    private WorkIqException Failure(Exception error)
    {
        var failure = error switch
        {
            WorkIqException known => known,
            MsalUiRequiredException => new WorkIqException(WorkIqFailure.SignedOut),
            MsalServiceException { StatusCode: 403 } => new WorkIqException(WorkIqFailure.Denied),
            MsalException msal => new WorkIqException(WorkIqFailure.Authentication,
                Regex.Match(msal.Message, @"AADSTS\d+").Value),
            TimeoutException or OperationCanceledException => new WorkIqException(WorkIqFailure.Timeout),
            HttpRequestException => new WorkIqException(WorkIqFailure.Network),
            JsonException or KeyNotFoundException or InvalidOperationException => new WorkIqException(WorkIqFailure.Malformed),
            _ => new WorkIqException(WorkIqFailure.Unknown)
        };
        _logger.LogWarning("WorkIQ request failed: {Kind} {Code} ({Type})", failure.Kind, failure.Code, error.GetType().Name);
        return failure;
    }

    private sealed class Prepared(IPublicClientApplication app) : IDisposable
    {
        public IPublicClientApplication? Application { get; private set; } = app;
        public void Dispose() => Application = null;
    }

    public void Dispose()
    {
        _signIn.Dispose();
        lock (_gate)
        {
            _disposed = true;
            _app = null;
            _photoCache.Clear();
            Interlocked.Increment(ref _revision);
            PublishAuthStateLocked();
        }
    }
}
