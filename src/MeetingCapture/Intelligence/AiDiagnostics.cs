using System.Text.RegularExpressions;

namespace MeetingCapture.Intelligence;

/// <summary>What happened to an AI request in the diagnostics stream.</summary>
public enum AiDiagnosticEventKind
{
    Started,
    CallStarted,
    UsageReported,
    ResponseReceived,
    Completed,
    Failed,
    Canceled,
    Persistence
}

/// <summary>The step of AI work that a diagnostic event belongs to.</summary>
public enum AiDiagnosticStage
{
    CopilotRequest,
    GeneratedMarkdownRewrite,
    CompanionJsonWrite,
    CompanionJsonRetry,
    VisualizationJournal
}

/// <summary>Result of retrying or recovering a failed save of AI output.</summary>
public enum AiDiagnosticRetryOutcome
{
    RetryAvailable,
    RetryFailed,
    PendingExitRetry,
    QueuedForAppRecovery,
    RetrySucceeded,
    RecoveryUnavailable
}

/// <summary>One recorded AI request for diagnostics: timing, capability, provider and model, and outcome.</summary>
public sealed record AiDiagnosticEvent(
    long Sequence,
    string? SessionId,
    long RequestId,
    string CorrelationId,
    long? RequestRevision,
    DateTimeOffset Timestamp,
    TimeSpan Duration,
    AiCapability Capability,
    string Provider,
    string? Model,
    AiDiagnosticEventKind Kind,
    AiRequestState State,
    AiRequestFailure Failure,
    TimeSpan? RetryAfter,
    int CallsStarted,
    long? InputTokens,
    long? OutputTokens,
    double? ProviderCost,
    double? TotalNanoAiu,
    IReadOnlyList<AiQuotaSnapshot> QuotaSnapshots,
    string? SensitivePromptPayload,
    string? SensitiveRequestPayload,
    string? SensitiveResponsePayload,
    bool SensitivePayloadTruncated,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    long? ReasoningTokens = null,
    AiDiagnosticStage? Stage = null,
    string? ExceptionType = null,
    int? ErrorCode = null,
    AiDiagnosticRetryOutcome? RetryOutcome = null);

/// <summary>A copy of the recent AI diagnostic events at one time.</summary>
public sealed record AiDiagnosticsSnapshot(
    string? SessionId,
    DateTimeOffset CapturedAt,
    bool SensitiveContentCaptureEnabled,
    IReadOnlyList<AiDiagnosticEvent> Events);

/// <summary>
/// Keeps a bounded, in-memory list of recent AI diagnostic events for the current recording
/// session. Secrets are redacted from captured text, and prompt or response payloads are kept
/// only when sensitive-content capture is turned on. Raises <see cref="Changed"/> on updates.
/// </summary>
public sealed class AiDiagnosticsStream
{
    private const int DefaultCapacity = 256;
    private const int MaxSensitivePayloadLength = 256 * 1024;
    private static readonly Regex SecretAssignment = new(
        @"(?im)\b(authorization|proxy-authorization|api[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret)\b(\s*[:=]\s*)([^\r\n,}]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex JsonSecretAssignment = new(
        @"(?i)([""'](?:authorization|proxy-authorization|api[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret)[""']\s*:\s*)(""[^""]*""|'[^']*'|[^,}\r\n]+)",
        RegexOptions.CultureInvariant);
    private static readonly Regex BearerToken = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly int _capacity;
    private readonly Queue<AiDiagnosticEvent> _events;
    private string? _sessionId;
    private long _generation;
    private long _nextRequestId;
    private long _sequence;
    private bool _captureSensitiveContent;

    public AiDiagnosticsStream(
        int capacity = DefaultCapacity,
        Func<DateTimeOffset>? now = null)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _events = new Queue<AiDiagnosticEvent>(capacity);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? Changed;

    public bool SensitiveContentCaptureEnabled
    {
        get { lock (_gate) return _captureSensitiveContent; }
    }

    /// <summary>Clears all events and starts a new session. Later events from the old session are ignored.</summary>
    public void Reset(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            // Advancing the generation makes callbacks already in flight belong to the old
            // recording, so they cannot repopulate the freshly cleared event list.
            _generation++;
            _sessionId = sessionId;
            _nextRequestId = 0;
            _sequence = 0;
            _captureSensitiveContent = false;
            _events.Clear();
        }
        // Notify after releasing the lock because listeners may immediately read a snapshot.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            // Invalidate observations even though the session ID stays the same; Clear is also
            // used when the user wants to discard diagnostics without ending the recording.
            _generation++;
            _events.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetSensitiveContentCapture(bool enabled)
    {
        lock (_gate)
        {
            if (_captureSensitiveContent == enabled)
                return;
            _captureSensitiveContent = enabled;
            if (!enabled && _events.Count > 0)
            {
                // Removing payloads from existing events is necessary too: disabling capture
                // must not leave previously captured prompts available in the in-memory view.
                var redacted = _events.Select(item => item with
                {
                    SensitivePromptPayload = null,
                    SensitiveRequestPayload = null,
                    SensitiveResponsePayload = null,
                    SensitivePayloadTruncated = false
                }).ToArray();
                _events.Clear();
                foreach (var item in redacted)
                    _events.Enqueue(item);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AiDiagnosticsSnapshot GetSnapshot()
    {
        // Return an array copy so callers cannot observe later queue changes or mutate the queue.
        lock (_gate)
            return new(
                _sessionId,
                _now(),
                _captureSensitiveContent,
                _events.ToArray());
    }

    internal RequestObservation Begin(
        AiCapability capability,
        string provider,
        string? model,
        long? requestRevision = null,
        string? sensitiveRequestPayload = null,
        string? expectedSessionId = null,
        string? sensitivePromptPayload = null)
    {
        RequestState request;
        long generation;
        AiDiagnosticEvent? started = null;
        lock (_gate)
        {
            generation = _generation;
            // A request tied to a prior recording may still finish, but it must not appear in
            // the diagnostics for the new recording.
            var accepted = expectedSessionId is null ||
                string.Equals(expectedSessionId, _sessionId, StringComparison.Ordinal);
            request = new(
                ++_nextRequestId,
                Guid.NewGuid().ToString("N"),
                requestRevision,
                _now(),
                capability,
                CleanMetadata(provider, "unknown")!,
                CleanMetadata(model, null),
                accepted);
            if (accepted)
            {
                var promptPayload = CaptureLocked(sensitivePromptPayload);
                var requestPayload = CaptureLocked(sensitiveRequestPayload);
                started = AppendLocked(request, AiDiagnosticEventKind.Started,
                    AiRequestState.Running, AiRequestFailure.None, null,
                    promptPayload.Value, requestPayload.Value, null,
                    promptPayload.Truncated || requestPayload.Truncated);
            }
        }
        if (started is not null)
            Changed?.Invoke(this, EventArgs.Empty);
        return new(this, generation, request);
    }

    public void RecordPersistence(
        AiDiagnosticStage stage,
        Exception? exception,
        AiDiagnosticRetryOutcome retryOutcome)
    {
        AiDiagnosticEvent? recorded = null;
        lock (_gate)
        {
            var request = new RequestState(
                ++_nextRequestId,
                Guid.NewGuid().ToString("N"),
                null,
                _now(),
                AiCapability.Synthesis,
                "Local persistence",
                null,
                true);
            var root = exception?.GetBaseException();
            recorded = AppendLocked(
                request,
                AiDiagnosticEventKind.Persistence,
                root is null ? AiRequestState.Succeeded : AiRequestState.Failed,
                root is null ? AiRequestFailure.None : AiRequestFailure.Unknown,
                null,
                null,
                null,
                null,
                false,
                stage,
                root?.GetType().Name,
                root?.HResult,
                retryOutcome);
        }
        if (recorded is not null)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Update(
        long generation,
        RequestState request,
        AiDiagnosticEventKind kind,
        AiRequestState state,
        AiRequestFailure failure = AiRequestFailure.None,
        TimeSpan? retryAfter = null,
        string? sensitiveResponsePayload = null,
        Action<RequestState>? update = null,
        AiDiagnosticStage? stage = null,
        Exception? exception = null,
        AiDiagnosticRetryOutcome? retryOutcome = null)
    {
        var changed = false;
        lock (_gate)
        {
            // SDK events can arrive after reset or after the request ended. Late usage is the
            // only post-completion event retained because providers may report it last.
            if (generation != _generation || !request.Accepted ||
                (request.Ended && kind != AiDiagnosticEventKind.UsageReported))
                return;
            if (request.Ended)
            {
                state = request.FinalState;
                failure = request.FinalFailure;
                retryAfter = request.FinalRetryAfter;
            }
            update?.Invoke(request);
            if (state != AiRequestState.Running)
            {
                request.Ended = true;
                request.FinalState = state;
                request.FinalFailure = failure;
                request.FinalRetryAfter = retryAfter;
            }
            var payload = CaptureLocked(sensitiveResponsePayload);
            var root = exception?.GetBaseException();
            AppendLocked(request, kind, state, failure, retryAfter,
                null, null, payload.Value, payload.Truncated,
                stage, root?.GetType().Name, root?.HResult, retryOutcome);
            changed = true;
        }
        if (changed)
            // Event handlers run outside the lock so UI updates do not block diagnostic writes.
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private AiDiagnosticEvent AppendLocked(
        RequestState request,
        AiDiagnosticEventKind kind,
        AiRequestState state,
        AiRequestFailure failure,
        TimeSpan? retryAfter,
        string? promptPayload,
        string? requestPayload,
        string? responsePayload,
        bool truncated,
        AiDiagnosticStage? stage = null,
        string? exceptionType = null,
        int? errorCode = null,
        AiDiagnosticRetryOutcome? retryOutcome = null)
    {
        var timestamp = _now();
        // Each event carries the request's accumulated counters, not just the values from this
        // callback, so the latest row is useful even when earlier SDK events held the usage.
        var value = new AiDiagnosticEvent(
            ++_sequence,
            _sessionId,
            request.RequestId,
            request.CorrelationId,
            request.RequestRevision,
            timestamp,
            timestamp - request.StartedAt,
            request.Capability,
            request.Provider,
            request.Model,
            kind,
            state,
            failure,
            retryAfter,
            request.CallsStarted,
            request.InputTokens,
            request.OutputTokens,
            request.ProviderCost,
            request.TotalNanoAiu,
            request.QuotaSnapshots.Values.OrderBy(item => item.QuotaId).ToArray(),
            promptPayload,
            requestPayload,
            responsePayload,
            truncated,
            request.CacheReadTokens,
            request.CacheWriteTokens,
            request.ReasoningTokens,
            stage,
            CleanMetadata(exceptionType, null),
            errorCode,
            retryOutcome);
        _events.Enqueue(value);
        // Keep memory use predictable during long recordings by retaining only the newest rows.
        while (_events.Count > _capacity)
            _events.Dequeue();
        return value;
    }

    private (string? Value, bool Truncated) CaptureLocked(string? value)
    {
        if (!_captureSensitiveContent || value is null)
            return (null, false);
        // Sensitive capture is opt-in. Even then, remove common credential forms and cap the
        // stored text so diagnostic export cannot accidentally retain an unbounded payload.
        var sanitized = BearerToken.Replace(
            SecretAssignment.Replace(
                JsonSecretAssignment.Replace(value, "$1\"[REDACTED]\""),
                "$1$2[REDACTED]"),
            "Bearer [REDACTED]");
        return sanitized.Length <= MaxSensitivePayloadLength
            ? (sanitized, false)
            : (sanitized[..MaxSensitivePayloadLength], true);
    }

    private static string? CleanMetadata(string? value, string? fallback)
    {
        // Metadata is useful for grouping, but it should not carry blank or unexpectedly large
        // values into every event.
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return fallback;
        return cleaned.Length <= 160 ? cleaned : cleaned[..160];
    }

    /// <summary>Records lifecycle, result, and usage events for one AI diagnostics request.</summary>
    internal sealed class RequestObservation(
        AiDiagnosticsStream owner,
        long generation,
        RequestState request)
    {
        public long RequestId => request.RequestId;
        public string CorrelationId => request.CorrelationId;

        public void CallStarted()
            => owner.Update(generation, request, AiDiagnosticEventKind.CallStarted,
                AiRequestState.Running, update: current => current.CallsStarted++);

        public void ReportUsage(
            long? inputTokens,
            long? outputTokens,
            double? providerCost,
            double? totalNanoAiu,
            IReadOnlyList<AiQuotaSnapshot>? quotaSnapshots,
            long? cacheReadTokens = null,
            long? cacheWriteTokens = null,
            long? reasoningTokens = null)
            => owner.Update(generation, request, AiDiagnosticEventKind.UsageReported,
                AiRequestState.Running, update: current =>
                {
                    // One request can make several provider calls; add each report and keep the
                    // latest snapshot for each quota rather than duplicating quota rows.
                    current.InputTokens = Add(current.InputTokens, inputTokens);
                    current.OutputTokens = Add(current.OutputTokens, outputTokens);
                    current.ProviderCost = Add(current.ProviderCost, providerCost);
                    current.TotalNanoAiu = Add(current.TotalNanoAiu, totalNanoAiu);
                    current.CacheReadTokens = Add(current.CacheReadTokens, cacheReadTokens);
                    current.CacheWriteTokens = Add(current.CacheWriteTokens, cacheWriteTokens);
                    current.ReasoningTokens = Add(current.ReasoningTokens, reasoningTokens);
                    if (quotaSnapshots is not null)
                        foreach (var snapshot in quotaSnapshots)
                            current.QuotaSnapshots[snapshot.QuotaId] = snapshot;
                });

        public void ResponseReceived(string? sensitiveResponsePayload)
            => owner.Update(generation, request, AiDiagnosticEventKind.ResponseReceived,
                AiRequestState.Running, sensitiveResponsePayload: sensitiveResponsePayload);

        public void Complete(bool hasResults, string? sensitiveResponsePayload = null)
            => owner.Update(generation, request, AiDiagnosticEventKind.Completed,
                hasResults ? AiRequestState.Succeeded : AiRequestState.SucceededEmpty,
                sensitiveResponsePayload: sensitiveResponsePayload);

        public void Fail(
            AiRequestFailure failure,
            TimeSpan? retryAfter = null,
            string? sensitiveResponsePayload = null,
            AiDiagnosticStage? stage = null,
            Exception? exception = null,
            AiDiagnosticRetryOutcome? retryOutcome = null)
            => owner.Update(generation, request, AiDiagnosticEventKind.Failed,
                failure == AiRequestFailure.RateLimited
                    ? AiRequestState.RateLimited
                    : AiRequestState.Failed,
                failure, retryAfter, sensitiveResponsePayload,
                stage: stage, exception: exception, retryOutcome: retryOutcome);

        public void Cancel()
            => owner.Update(generation, request, AiDiagnosticEventKind.Canceled,
                AiRequestState.Canceled);

        private static long? Add(long? current, long? value) =>
            value is null ? current : checked((current ?? 0) + value.Value);

        private static double? Add(double? current, double? value) =>
            value is null ? current : (current ?? 0) + value.Value;
    }

    /// <summary>Accumulates the current diagnostic state and reported usage for one AI request.</summary>
    internal sealed class RequestState(
        long requestId,
        string correlationId,
        long? requestRevision,
        DateTimeOffset startedAt,
        AiCapability capability,
        string provider,
        string? model,
        bool accepted)
    {
        public long RequestId { get; } = requestId;
        public string CorrelationId { get; } = correlationId;
        public long? RequestRevision { get; } = requestRevision;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public AiCapability Capability { get; } = capability;
        public string Provider { get; } = provider;
        public string? Model { get; } = model;
        public bool Accepted { get; } = accepted;
        public bool Ended { get; set; }
        public AiRequestState FinalState { get; set; }
        public AiRequestFailure FinalFailure { get; set; }
        public TimeSpan? FinalRetryAfter { get; set; }
        public int CallsStarted { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public double? ProviderCost { get; set; }
        public double? TotalNanoAiu { get; set; }
        public long? CacheReadTokens { get; set; }
        public long? CacheWriteTokens { get; set; }
        public long? ReasoningTokens { get; set; }
        public Dictionary<string, AiQuotaSnapshot> QuotaSnapshots { get; } =
            new(StringComparer.Ordinal);
    }
}
