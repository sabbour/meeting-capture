using System.Collections;
using System.Reflection;
using GitHub.Copilot;

namespace MeetingCapture.Intelligence;

public enum AiCapability
{
    Synthesis,
    Intelligence,
    Visualization,
    CatchUp,
    WorkIqRetrieval
}

public enum AiRequestState
{
    Running,
    Succeeded,
    SucceededEmpty,
    Failed,
    Canceled,
    RateLimited
}

public enum AiRequestFailure
{
    None,
    Configuration,
    Authentication,
    Authorization,
    ModelUnavailable,
    RateLimited,
    QuotaExceeded,
    Network,
    Timeout,
    Unsupported,
    Unknown
}

public sealed record AiReportedLong(long Total, int ReportedCalls, int UnreportedCalls)
{
    public bool IsReported => ReportedCalls > 0;
}

public sealed record AiReportedDouble(double Total, int ReportedCalls, int UnreportedCalls)
{
    public bool IsReported => ReportedCalls > 0;
}

public sealed record AiProviderCost(
    AiReportedDouble Amount,
    string? Currency,
    string? Unit);

public sealed record AiQuotaSnapshot(
    string QuotaId,
    long EntitlementRequests,
    bool IsUnlimitedEntitlement,
    double Overage,
    bool OverageAllowedWithExhaustedQuota,
    double RemainingPercentage,
    DateTimeOffset? ResetDate,
    bool UsageAllowedWithExhaustedQuota,
    long UsedRequests);

public sealed record AiQuotaSnapshots(
    IReadOnlyList<AiQuotaSnapshot> Values,
    int ReportedCalls,
    int UnreportedCalls)
{
    public bool IsReported => ReportedCalls > 0;
}

public sealed record AiCapabilityUsage(
    AiCapability Capability,
    int RequestsStarted,
    int CallsStarted,
    int Succeeded,
    int SucceededEmpty,
    int Failed,
    int Canceled,
    int RateLimited,
    AiReportedLong InputTokens,
    AiReportedLong OutputTokens,
    AiProviderCost ProviderCost,
    AiReportedDouble TotalNanoAiu,
    AiQuotaSnapshots QuotaSnapshots,
    AiReportedLong CacheReadTokens,
    AiReportedLong CacheWriteTokens,
    AiReportedLong ReasoningTokens);

public sealed record AiRequestUsage(
    long RequestId,
    AiCapability Capability,
    AiRequestState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    TimeSpan Elapsed,
    int CallsStarted,
    AiRequestFailure Failure,
    TimeSpan? RetryAfter,
    long? InputTokens,
    long? OutputTokens,
    double? ProviderCost,
    double? TotalNanoAiu,
    AiQuotaSnapshots QuotaSnapshots,
    long? CacheReadTokens = null,
    long? CacheWriteTokens = null,
    long? ReasoningTokens = null);

public sealed record AiUsageSnapshot(
    string? SessionId,
    DateTimeOffset? SessionStartedAt,
    DateTimeOffset CapturedAt,
    IReadOnlyList<AiCapabilityUsage> Capabilities,
    IReadOnlyList<AiRequestUsage> Requests)
{
    public bool HasActiveRequests => Requests.Any(request => request.State == AiRequestState.Running);
}

internal sealed record AiProviderUsage(
    long? InputTokens,
    long? OutputTokens,
    double? ProviderCost,
    double? TotalNanoAiu,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    long? ReasoningTokens,
    IReadOnlyList<AiQuotaSnapshot>? QuotaSnapshots);

internal static class CopilotUsageMapper
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo? QuotaSnapshotsProperty =
        typeof(AssistantUsageData).GetProperty("QuotaSnapshots", InstanceMembers);

    public static AiProviderUsage Map(AssistantUsageData data)
    {
#pragma warning disable GHCP001
        return new(
            data.InputTokens,
            data.OutputTokens,
            data.Cost,
            data.CopilotUsage?.TotalNanoAiu,
            data.CacheReadTokens,
            data.CacheWriteTokens,
            data.ReasoningTokens,
            ReadQuotaSnapshots(data));
#pragma warning restore GHCP001
    }

    private static IReadOnlyList<AiQuotaSnapshot>? ReadQuotaSnapshots(AssistantUsageData data)
    {
        if (QuotaSnapshotsProperty is null)
            return null;

        IDictionary? snapshots;
        try
        {
            snapshots = QuotaSnapshotsProperty.GetValue(data) as IDictionary;
        }
        catch (Exception exception) when (
            exception is MethodAccessException or TargetInvocationException)
        {
            return null;
        }

        if (snapshots is null)
            return null;

        var result = new List<AiQuotaSnapshot>(snapshots.Count);
        foreach (DictionaryEntry entry in snapshots)
        {
            if (entry.Key is not string quotaId || entry.Value is null ||
                !TryMapQuotaSnapshot(quotaId, entry.Value, out var snapshot))
                return null;
            result.Add(snapshot);
        }
        return result;
    }

    private static bool TryMapQuotaSnapshot(
        string quotaId,
        object value,
        out AiQuotaSnapshot snapshot)
    {
        snapshot = default!;
        try
        {
            var type = value.GetType();
            snapshot = new(
                quotaId,
                Read<long>(type, value, "EntitlementRequests"),
                Read<bool>(type, value, "IsUnlimitedEntitlement"),
                Read<double>(type, value, "Overage"),
                Read<bool>(type, value, "OverageAllowedWithExhaustedQuota"),
                Read<double>(type, value, "RemainingPercentage"),
                Read<DateTimeOffset?>(type, value, "ResetDate"),
                Read<bool>(type, value, "UsageAllowedWithExhaustedQuota"),
                Read<long>(type, value, "UsedRequests"));
            return true;
        }
        catch (Exception exception) when (
            exception is MissingMemberException or InvalidCastException or
            MethodAccessException or TargetInvocationException)
        {
            return false;
        }
    }

    private static T Read<T>(Type type, object value, string propertyName)
    {
        var property = type.GetProperty(propertyName, InstanceMembers)
            ?? throw new MissingMemberException(type.FullName, propertyName);
        return (T)property.GetValue(value)!;
    }
}

public sealed class AiUsageMonitor
{
    private const int MaxRequests = 32;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<AiCapability, CapabilityState> _capabilities = [];
    private readonly List<RequestState> _requests = [];
    private string? _sessionId;
    private DateTimeOffset? _sessionStartedAt;
    private long _generation;
    private long _nextRequestId;

    public AiUsageMonitor(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        foreach (var capability in Enum.GetValues<AiCapability>())
            _capabilities[capability] = new CapabilityState(capability);
    }

    public event EventHandler? Changed;

    public void Reset(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A recording session ID is required.", nameof(sessionId));
        lock (_gate)
        {
            _generation++;
            _sessionId = sessionId;
            _sessionStartedAt = _now();
            _nextRequestId = 0;
            _requests.Clear();
            foreach (var capability in _capabilities.Values)
                capability.Reset();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AiUsageSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = _now();
            return new(
                _sessionId,
                _sessionStartedAt,
                now,
                _capabilities.Values
                    .OrderBy(value => value.Capability)
                    .Select(value => value.Snapshot())
                    .ToArray(),
                _requests
                    .Select(request => request.Snapshot(now))
                    .ToArray());
        }
    }

    internal RequestObservation Begin(AiCapability capability, string? expectedSessionId = null)
    {
        RequestState request;
        long generation;
        lock (_gate)
        {
            generation = _generation;
            var accepted = _sessionId is not null &&
                (expectedSessionId is null ||
                 string.Equals(expectedSessionId, _sessionId, StringComparison.Ordinal));
            request = new RequestState(
                ++_nextRequestId,
                capability,
                _now(),
                accepted);
            if (accepted)
            {
                _capabilities[capability].RequestsStarted++;
                _requests.Insert(0, request);
                if (_requests.Count > MaxRequests)
                {
                    var removable = _requests.FindLastIndex(
                        item => item.State != AiRequestState.Running);
                    if (removable >= 0)
                        _requests.RemoveAt(removable);
                }
            }
        }
        if (request.Accepted)
            Changed?.Invoke(this, EventArgs.Empty);
        return new RequestObservation(this, generation, request);
    }

    private void Update(
        long generation,
        RequestState request,
        Action<RequestState, CapabilityState> update)
    {
        var changed = false;
        lock (_gate)
        {
            if (generation != _generation || !request.Accepted ||
                request.State != AiRequestState.Running || !_requests.Contains(request))
                return;
            update(request, _capabilities[request.Capability]);
            if (_requests.Count > MaxRequests && request.State != AiRequestState.Running)
            {
                var removable = _requests.FindLastIndex(
                    item => item.State != AiRequestState.Running);
                if (removable >= 0)
                    _requests.RemoveAt(removable);
            }
            changed = true;
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class RequestObservation(
        AiUsageMonitor owner,
        long generation,
        RequestState request)
    {
        public void CallStarted() => owner.Update(generation, request, (current, aggregate) =>
        {
            current.CallsStarted++;
            aggregate.CallsStarted++;
        });

        public void ReportUsage(
            long? inputTokens,
            long? outputTokens,
            double? providerCost,
            double? totalNanoAiu,
            IReadOnlyList<AiQuotaSnapshot>? quotaSnapshots,
            long? cacheReadTokens = null,
            long? cacheWriteTokens = null,
            long? reasoningTokens = null)
            => owner.Update(generation, request, (current, _) =>
            {
                current.InputTokens = Add(current.InputTokens, inputTokens);
                current.OutputTokens = Add(current.OutputTokens, outputTokens);
                current.ProviderCost = Add(current.ProviderCost, providerCost);
                current.TotalNanoAiu = Add(current.TotalNanoAiu, totalNanoAiu);
                current.CacheReadTokens = Add(current.CacheReadTokens, cacheReadTokens);
                current.CacheWriteTokens = Add(current.CacheWriteTokens, cacheWriteTokens);
                current.ReasoningTokens = Add(current.ReasoningTokens, reasoningTokens);
                if (inputTokens is not null) current.InputTokenReports++;
                if (outputTokens is not null) current.OutputTokenReports++;
                if (providerCost is not null) current.ProviderCostReports++;
                if (totalNanoAiu is not null) current.TotalNanoAiuReports++;
                if (cacheReadTokens is not null) current.CacheReadTokenReports++;
                if (cacheWriteTokens is not null) current.CacheWriteTokenReports++;
                if (reasoningTokens is not null) current.ReasoningTokenReports++;
                if (quotaSnapshots is not null)
                {
                    current.QuotaSnapshotReports++;
                    foreach (var snapshot in quotaSnapshots)
                        current.QuotaSnapshots[snapshot.QuotaId] = snapshot;
                }
            });

        public void Complete(bool hasResults = true)
            => Finish(hasResults ? AiRequestState.Succeeded : AiRequestState.SucceededEmpty,
                AiRequestFailure.None, null);

        public void Cancel() => Finish(AiRequestState.Canceled, AiRequestFailure.None, null);

        public void Fail(AiRequestFailure failure, TimeSpan? retryAfter = null)
            => Finish(
                failure == AiRequestFailure.RateLimited
                    ? AiRequestState.RateLimited
                    : AiRequestState.Failed,
                failure,
                retryAfter);

        private void Finish(
            AiRequestState state,
            AiRequestFailure failure,
            TimeSpan? retryAfter)
            => owner.Update(generation, request, (current, aggregate) =>
            {
                current.State = state;
                current.Failure = failure;
                current.RetryAfter = retryAfter;
                current.EndedAt = owner._now();
                switch (state)
                {
                    case AiRequestState.Succeeded: aggregate.Succeeded++; break;
                    case AiRequestState.SucceededEmpty: aggregate.SucceededEmpty++; break;
                    case AiRequestState.Canceled: aggregate.Canceled++; break;
                    case AiRequestState.RateLimited: aggregate.RateLimited++; break;
                    default: aggregate.Failed++; break;
                }
                aggregate.InputTokens.Add(
                    current.InputTokens, current.InputTokenReports, current.CallsStarted);
                aggregate.OutputTokens.Add(
                    current.OutputTokens, current.OutputTokenReports, current.CallsStarted);
                aggregate.ProviderCost.Add(
                    current.ProviderCost, current.ProviderCostReports, current.CallsStarted);
                aggregate.TotalNanoAiu.Add(
                    current.TotalNanoAiu,
                    current.TotalNanoAiuReports,
                    current.CallsStarted);
                aggregate.CacheReadTokens.Add(
                    current.CacheReadTokens,
                    current.CacheReadTokenReports,
                    current.CallsStarted);
                aggregate.CacheWriteTokens.Add(
                    current.CacheWriteTokens,
                    current.CacheWriteTokenReports,
                    current.CallsStarted);
                aggregate.ReasoningTokens.Add(
                    current.ReasoningTokens,
                    current.ReasoningTokenReports,
                    current.CallsStarted);
                aggregate.QuotaSnapshots.Add(
                    current.QuotaSnapshots.Values,
                    current.QuotaSnapshotReports,
                    current.CallsStarted);
            });

        private static long? Add(long? current, long? value)
            => value is null ? current : (current ?? 0) + value.Value;

        private static double? Add(double? current, double? value)
            => value is null ? current : (current ?? 0) + value.Value;
    }

    internal sealed class RequestState(
        long id,
        AiCapability capability,
        DateTimeOffset startedAt,
        bool accepted)
    {
        public long Id { get; } = id;
        public AiCapability Capability { get; } = capability;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public bool Accepted { get; } = accepted;
        public AiRequestState State { get; set; } = AiRequestState.Running;
        public DateTimeOffset? EndedAt { get; set; }
        public int CallsStarted { get; set; }
        public AiRequestFailure Failure { get; set; }
        public TimeSpan? RetryAfter { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public double? ProviderCost { get; set; }
        public double? TotalNanoAiu { get; set; }
        public long? CacheReadTokens { get; set; }
        public long? CacheWriteTokens { get; set; }
        public long? ReasoningTokens { get; set; }
        public Dictionary<string, AiQuotaSnapshot> QuotaSnapshots { get; } =
            new(StringComparer.Ordinal);
        public int InputTokenReports { get; set; }
        public int OutputTokenReports { get; set; }
        public int ProviderCostReports { get; set; }
        public int TotalNanoAiuReports { get; set; }
        public int CacheReadTokenReports { get; set; }
        public int CacheWriteTokenReports { get; set; }
        public int ReasoningTokenReports { get; set; }
        public int QuotaSnapshotReports { get; set; }

        public AiRequestUsage Snapshot(DateTimeOffset now) => new(
            Id,
            Capability,
            State,
            StartedAt,
            EndedAt,
            (EndedAt ?? now) - StartedAt,
            CallsStarted,
            Failure,
            RetryAfter,
            InputTokens,
            OutputTokens,
            ProviderCost,
            TotalNanoAiu,
            new(
                QuotaSnapshots.Values.OrderBy(item => item.QuotaId, StringComparer.Ordinal).ToArray(),
                QuotaSnapshotReports,
                Math.Max(0, CallsStarted - QuotaSnapshotReports)),
            CacheReadTokens,
            CacheWriteTokens,
            ReasoningTokens);
    }

    private sealed class CapabilityState(AiCapability capability)
    {
        public AiCapability Capability { get; } = capability;
        public int RequestsStarted { get; set; }
        public int CallsStarted { get; set; }
        public int Succeeded { get; set; }
        public int SucceededEmpty { get; set; }
        public int Failed { get; set; }
        public int Canceled { get; set; }
        public int RateLimited { get; set; }
        public LongMetric InputTokens { get; } = new();
        public LongMetric OutputTokens { get; } = new();
        public DoubleMetric ProviderCost { get; } = new();
        public DoubleMetric TotalNanoAiu { get; } = new();
        public LongMetric CacheReadTokens { get; } = new();
        public LongMetric CacheWriteTokens { get; } = new();
        public LongMetric ReasoningTokens { get; } = new();
        public QuotaMetric QuotaSnapshots { get; } = new();

        public void Reset()
        {
            RequestsStarted = CallsStarted = Succeeded = SucceededEmpty =
                Failed = Canceled = RateLimited = 0;
            InputTokens.Reset();
            OutputTokens.Reset();
            ProviderCost.Reset();
            TotalNanoAiu.Reset();
            CacheReadTokens.Reset();
            CacheWriteTokens.Reset();
            ReasoningTokens.Reset();
            QuotaSnapshots.Reset();
        }

        public AiCapabilityUsage Snapshot() => new(
            Capability,
            RequestsStarted,
            CallsStarted,
            Succeeded,
            SucceededEmpty,
            Failed,
            Canceled,
            RateLimited,
            InputTokens.Snapshot(),
            OutputTokens.Snapshot(),
            new(ProviderCost.Snapshot(), Currency: null, Unit: null),
            TotalNanoAiu.Snapshot(),
            QuotaSnapshots.Snapshot(),
            CacheReadTokens.Snapshot(),
            CacheWriteTokens.Snapshot(),
            ReasoningTokens.Snapshot());
    }

    private sealed class LongMetric
    {
        private long _total;
        private int _reported;
        private int _unreported;

        public void Add(long? value, int reports, int calls)
        {
            if (value is { } reported)
            {
                _total += reported;
                _reported += reports;
                _unreported += Math.Max(0, calls - reports);
            }
            else
                _unreported += calls;
        }

        public AiReportedLong Snapshot() => new(_total, _reported, _unreported);
        public void Reset() => (_total, _reported, _unreported) = (0, 0, 0);
    }

    private sealed class DoubleMetric
    {
        private double _total;
        private int _reported;
        private int _unreported;

        public void Add(double? value, int reports, int calls)
        {
            if (value is { } reported)
            {
                _total += reported;
                _reported += reports;
                _unreported += Math.Max(0, calls - reports);
            }
            else
                _unreported += calls;
        }

        public AiReportedDouble Snapshot() => new(_total, _reported, _unreported);
        public void Reset() => (_total, _reported, _unreported) = (0, 0, 0);
    }

    private sealed class QuotaMetric
    {
        private readonly Dictionary<string, AiQuotaSnapshot> _latest = new(StringComparer.Ordinal);
        private int _reported;
        private int _unreported;

        public void Add(IEnumerable<AiQuotaSnapshot> values, int reports, int calls)
        {
            foreach (var snapshot in values)
                _latest[snapshot.QuotaId] = snapshot;
            _reported += reports;
            _unreported += Math.Max(0, calls - reports);
        }

        public AiQuotaSnapshots Snapshot() => new(
            _latest.Values.OrderBy(item => item.QuotaId, StringComparer.Ordinal).ToArray(),
            _reported,
            _unreported);

        public void Reset()
        {
            _latest.Clear();
            _reported = _unreported = 0;
        }
    }
}
