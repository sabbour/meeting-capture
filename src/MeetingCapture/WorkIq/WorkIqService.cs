using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingCapture.Diagnostics;
using MeetingCapture.Graph;
using MeetingCapture.Intelligence;
using MeetingCapture.MeetingDetection;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace MeetingCapture.WorkIq;

public sealed class WorkIqService : IDisposable, IRelatedContextSource
{
    public const string ClientId = "ba081686-5d24-4bc6-a0d6-d034ecffed87";
    internal const string RedirectUri = "http://localhost";
    public static readonly string[] Scopes = ["fdcc1f02-fc51-4226-8753-f668596af7f7/WorkIQAgent.Ask"];
    internal static readonly string[] RelatedContextCapabilities =
        ["Meetings", "TeamsMessages", "Email"];
    private const int MaxPhotoCacheEntries = 64;
    private sealed record AuthStateSnapshot(bool IsAuthenticated, long Revision);
    private readonly ILogger<WorkIqService> _logger;
    private readonly ProtectedWorkIqCache _cache;
    private readonly AiUsageMonitor _usage;
    private readonly AiDiagnosticsStream _diagnostics;
    private readonly ResponsivenessDiagnostics? _responsiveness;
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
        AiDiagnosticsStream? diagnostics = null,
        ResponsivenessDiagnostics? responsiveness = null)
    {
        _logger = logger;
        _cache = cache ?? new ProtectedWorkIqCache();
        _usage = usage ?? new AiUsageMonitor();
        _diagnostics = diagnostics ?? new AiDiagnosticsStream();
        _responsiveness = responsiveness;
        _signIn = new BoundedSignIn(TimeSpan.FromSeconds(90),
            error => _logger.LogWarning("WorkIQ sign-in cleanup: {Type}", error.GetType().Name));
    }

    private static IPublicClientApplication CreateApp() => PublicClientApplicationBuilder.Create(ClientId)
        .WithAuthority("https://login.microsoftonline.com/organizations")
        .WithRedirectUri(RedirectUri)
        .Build();

    private static WorkIqMcpClient CreateClient(TimeSpan? requestTimeout = null) => new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    }, requestTimeout);

    public async Task SignInAsync(CancellationToken ct)
    {
        var revision = Revision;
        WorkIqException? serviceProblem = null;
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
                try { await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false); }
                catch (WorkIqException ex) when (ex.Kind is WorkIqFailure.ServiceUnavailable)
                {
                    // The account signed in; only the service check failed. Keep the sign-in so
                    // later requests can succeed without another browser sign-in.
                    serviceProblem = ex;
                }
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
            // Sign-in is saved; report the service problem so the window can explain it.
            if (serviceProblem is not null) throw serviceProblem;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw Failure(ex); }
    }

    /// <summary>Restore only this application's DPAPI-protected cache; never opens a browser.</summary>
    public Task RestoreAsync(CancellationToken ct) => RestoreAsync(RestoreApplicationAsync, ct);

    internal async Task RestoreAsync(
        Func<CancellationToken, Task<IPublicClientApplication?>> restoreApplication,
        CancellationToken ct)
    {
        using var phase = _responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqRestoreWait);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var revision = Revision;
        var published = false;
        try
        {
            await _signIn.RunAsync(async operationToken =>
            {
                using var workerPhase = _responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqRestore);
                await _requests.WaitAsync(operationToken).ConfigureAwait(false);
                try
                {
                    var app = await restoreApplication(operationToken).ConfigureAwait(false);
                    if (app is not null)
                    {
                        // Cache I/O stays inside the owned worker, not the bounded caller's publication.
                        lock (_gate)
                        {
                            operationToken.ThrowIfCancellationRequested();
                            CheckRevision(revision);
                            using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCacheSave))
                                _cache.Save(app);
                        }
                    }
                    return new Prepared(app);
                }
                finally { _requests.Release(); }
            }, prepared =>
            {
                if (prepared.Application is not { } app) return;
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    CheckRevision(revision);
                    _app = app;
                    _photoCache.Clear();
                    Interlocked.Increment(ref _revision);
                    PublishAuthStateLocked();
                    published = true;
                }
            }, token).ConfigureAwait(false);
            if (published) AuthStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            phase?.Fail(ex);
            throw;
        }
        catch (Exception ex)
        {
            phase?.Fail(ex);
            throw Failure(ex);
        }
    }

    private async Task<IPublicClientApplication?> RestoreApplicationAsync(CancellationToken token)
    {
        var app = CreateApp();
        using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCacheLoad))
            _cache.Load(app);
        IAccount[] accounts;
        using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCachedAccounts))
            accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
        if (accounts.Length == 0) return null;
        if (accounts.Length != 1) throw new WorkIqException(WorkIqFailure.Authentication);
        AuthenticationResult auth;
        using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqSilentAuthentication))
            auth = await app.AcquireTokenSilent(Scopes, accounts[0]).ExecuteAsync(token).ConfigureAwait(false);
        using var client = CreateClient();
        using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqConnect))
        {
            // The saved sign-in is still valid when only the service check fails; keep it.
            try { await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false); }
            catch (WorkIqException ex) when (ex.Kind is WorkIqFailure.ServiceUnavailable) { }
        }
        return app;
    }

    public async Task<List<MeetingTitleCandidate>> GetCalendarAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        using var phase = _responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCalendar);
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
            IAccount[] accounts;
            using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCachedAccounts))
                accounts = (await app.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (accounts.Length != 1) throw new WorkIqException(WorkIqFailure.SignedOut);
            AuthenticationResult auth;
            using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqSilentAuthentication))
                auth = await app.AcquireTokenSilent(Scopes, accounts[0]).ExecuteAsync(token).ConfigureAwait(false);
            using var client = CreateClient();
            using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqConnect))
                await client.InitializeAsync(auth.AccessToken, token).ConfigureAwait(false);
            var response = await client.FetchCalendarResultAsync(auth.AccessToken, start, end, token).ConfigureAwait(false);
            var meetings = WorkIqMcpClient.ParseCalendar(response);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                CheckRevision(revision);
                using (_responsiveness?.BeginBackgroundPhase(ResponsivenessPhase.WorkIqCacheSave))
                    _cache.Save(app);
            }
            return meetings;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            phase?.Fail(ex);
            throw;
        }
        catch (Exception ex)
        {
            phase?.Fail(ex);
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
            foreach (var _ in RelatedContextCapabilities)
            {
                observation.CallStarted();
                diagnostic.CallStarted();
            }
            var familySnapshots = await RetrieveFamiliesAsync(
                RelatedContextCapabilities,
                async (capability, familyToken) =>
                {
                    using var familyPhase = _responsiveness?.BeginBackgroundPhase(FamilyPhase(capability));
                    try
                    {
                        var response = await client.RetrieveRelatedContextResultAsync(
                            auth.AccessToken, query.Topic, [capability], familyToken).ConfigureAwait(false);
                        var family = WorkIqMcpClient.ParseRetrievedContext(response, retrievedAt, currentIdentity);
                        _logger.LogInformation(
                            "WorkIQ related-context family completed: {Capability} {Availability} {Count}.",
                            capability, family.Availability, family.Items.Count);
                        return family;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        var familyFailure = Failure(ex);
                        familyPhase?.Fail(familyFailure, DiagnosticFailureReason(familyFailure));
                        throw familyFailure;
                    }
                },
                RelatedContextFamilyTimeout,
                retrievedAt,
                token).ConfigureAwait(false);
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
            if (failure.Kind is WorkIqFailure.ServiceDeclined or WorkIqFailure.Network or
                WorkIqFailure.ServiceUnavailable or
                WorkIqFailure.Timeout or WorkIqFailure.Malformed or WorkIqFailure.Unknown)
            {
                return new RelatedContextSnapshot(
                    DateTimeOffset.UtcNow,
                    [],
                    RelatedContextAvailability.Unavailable,
                    RelatedContextUnavailableMessage(failure));
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
        finally { if (entered) _contextRequests.Release(); }
    }

    internal static readonly TimeSpan RelatedContextFamilyTimeout = TimeSpan.FromSeconds(60);

    private static ResponsivenessPhase FamilyPhase(string capability) => capability switch
    {
        "Meetings" => ResponsivenessPhase.WorkIqRelatedMeetings,
        "TeamsMessages" => ResponsivenessPhase.WorkIqRelatedTeamsMessages,
        "Email" => ResponsivenessPhase.WorkIqRelatedEmail,
        _ => ResponsivenessPhase.WorkIqRelatedOther
    };

    internal static string DiagnosticFailureReason(WorkIqException failure)
        => string.IsNullOrWhiteSpace(failure.Code)
            ? failure.Kind.ToString()
            : $"{failure.Kind}:{failure.Code}";

    internal static string RelatedContextUnavailableMessage(WorkIqException failure) => failure.Kind switch
    {
        WorkIqFailure.ServiceDeclined => "Work IQ declined the related-sources request.",
        WorkIqFailure.Network =>
            "Work IQ could not be reached.",
        WorkIqFailure.ServiceUnavailable =>
            "The Work IQ service is having a problem right now.",
        WorkIqFailure.Timeout => "Work IQ timed out while finding related suggestions.",
        WorkIqFailure.Authentication or WorkIqFailure.SignedOut => "Work IQ is not signed in.",
        WorkIqFailure.Malformed =>
            "Work IQ sent a reply this app could not read.",
        WorkIqFailure.Denied =>
            "Your Work IQ account cannot access related suggestions.",
        WorkIqFailure.Unsupported =>
            "Work IQ does not support related suggestions.",
        _ => "Work IQ request failed. See Diagnostics/related.log for the error type.",
    };

    /// <summary>
    /// Queries every source family at the same time, each with its own time limit. One slow,
    /// denied or malformed family no longer discards the others: it only fails the whole
    /// query when no family returned a result. Sign-in failures still fail the query so the
    /// caller can publish the signed-out state.
    /// </summary>
    internal static async Task<List<RelatedContextSnapshot>> RetrieveFamiliesAsync(
        IReadOnlyList<string> capabilities,
        Func<string, CancellationToken, Task<RelatedContextSnapshot>> retrieveFamily,
        TimeSpan familyTimeout,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        var attempts = capabilities.Select(async capability =>
        {
            using var familyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            familyDeadline.CancelAfter(familyTimeout);
            try
            {
                return (Snapshot: await retrieveFamily(capability, familyDeadline.Token).ConfigureAwait(false),
                    Error: (Exception?)null);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                return (Snapshot: (RelatedContextSnapshot?)null, Error: ex);
            }
        }).ToArray();
        var results = await Task.WhenAll(attempts).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = new List<RelatedContextSnapshot>();
        Exception? firstFailure = null;
        foreach (var (snapshot, error) in results)
        {
            if (snapshot is not null) { snapshots.Add(snapshot); continue; }
            if (error is WorkIqException { Kind: WorkIqFailure.Unsupported })
            {
                snapshots.Add(new RelatedContextSnapshot(
                    retrievedAt,
                    [],
                    RelatedContextAvailability.Unavailable,
                    "Work IQ does not support related suggestions."));
                continue;
            }
            if (error is WorkIqException { Kind: WorkIqFailure.Authentication or WorkIqFailure.SignedOut })
                throw error;
            firstFailure ??= error;
            snapshots.Add(new RelatedContextSnapshot(
                retrievedAt,
                [],
                RelatedContextAvailability.Unavailable,
                error is WorkIqException failure
                    ? RelatedContextUnavailableMessage(failure)
                    : "Work IQ could not be reached."));
        }
        if (firstFailure is not null &&
            !snapshots.Any(item => item.Availability != RelatedContextAvailability.Unavailable))
            throw firstFailure!;
        return snapshots;
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
        MeetingCapture.Diagnostics.RelatedDiagnostics.Record("related", IsAuthenticated, failure);
        return failure;
    }

    private sealed class Prepared(IPublicClientApplication? app) : IDisposable
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
