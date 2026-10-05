using System.Collections;
using System.Reflection;
using GitHub.Copilot;
using MeetingCapture.Intelligence;

namespace MeetingCapture.Regression;

internal static class AiUsageChecks
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("AI usage maps and aggregates the SDK assistant usage event", Aggregation);
        yield return ("AI usage reset rejects late results from the prior recording", ResetAndLateResults);
        yield return ("pipeline resets AI usage for the recording session", PipelineReset);
    }

    private static Task Aggregation()
    {
        var now = new DateTimeOffset(2026, 9, 24, 7, 0, 0, TimeSpan.Zero);
        var monitor = new AiUsageMonitor(() => now);
        monitor.Reset("session-a");

        var intelligence = monitor.Begin(AiCapability.Intelligence);
        intelligence.CallStarted();
        now = now.AddSeconds(2);
        var active = monitor.GetSnapshot().Requests.Single();
        Check.Equal(AiRequestState.Running, active.State);
        Check.Equal(TimeSpan.FromSeconds(2), active.Elapsed);
        var providerUsage = CopilotUsageMapper.Map(CreateSdkUsageEvent());
        intelligence.ReportUsage(
            providerUsage.InputTokens,
            providerUsage.OutputTokens,
            providerUsage.ProviderCost,
            providerUsage.TotalNanoAiu,
            providerUsage.QuotaSnapshots,
            providerUsage.CacheReadTokens,
            providerUsage.CacheWriteTokens,
            providerUsage.ReasoningTokens);
        intelligence.Complete();

        var workIq = monitor.Begin(AiCapability.WorkIqRetrieval, "session-a");
        for (var i = 0; i < 4; i++) workIq.CallStarted();
        workIq.Complete(hasResults: false);

        var limited = monitor.Begin(AiCapability.Visualization);
        limited.CallStarted();
        limited.Fail(AiRequestFailure.RateLimited, TimeSpan.FromMinutes(3));

        var snapshot = monitor.GetSnapshot();
        var intelligenceTotals = snapshot.Capabilities.Single(
            item => item.Capability == AiCapability.Intelligence);
        Check.Equal(1, intelligenceTotals.RequestsStarted);
        Check.Equal(1, intelligenceTotals.CallsStarted);
        Check.Equal(120L, intelligenceTotals.InputTokens.Total);
        Check.True(intelligenceTotals.InputTokens.IsReported);
        Check.Equal(30L, intelligenceTotals.OutputTokens.Total);
        Check.Equal(20L, intelligenceTotals.CacheReadTokens.Total);
        Check.True(intelligenceTotals.CacheReadTokens.IsReported);
        Check.Equal(10L, intelligenceTotals.CacheWriteTokens.Total);
        Check.True(intelligenceTotals.CacheWriteTokens.IsReported);
        Check.Equal(5L, intelligenceTotals.ReasoningTokens.Total);
        Check.True(intelligenceTotals.ReasoningTokens.IsReported);
        Check.Equal(1.5d, intelligenceTotals.ProviderCost.Amount.Total);
        Check.True(intelligenceTotals.ProviderCost.Amount.IsReported);
        Check.Equal<string?>(null, intelligenceTotals.ProviderCost.Currency);
        Check.Equal<string?>(null, intelligenceTotals.ProviderCost.Unit);
        Check.Equal(420.25d, intelligenceTotals.TotalNanoAiu.Total);
        Check.True(intelligenceTotals.TotalNanoAiu.IsReported);
        Check.True(intelligenceTotals.QuotaSnapshots.IsReported);
        var quota = intelligenceTotals.QuotaSnapshots.Values.Single();
        Check.Equal("premium_interactions", quota.QuotaId);
        Check.Equal(300L, quota.EntitlementRequests);
        Check.Equal(42L, quota.UsedRequests);
        Check.Equal(86d, quota.RemainingPercentage);
        Check.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), quota.ResetDate);

        var workIqTotals = snapshot.Capabilities.Single(
            item => item.Capability == AiCapability.WorkIqRetrieval);
        Check.Equal(1, workIqTotals.RequestsStarted);
        Check.Equal(4, workIqTotals.CallsStarted);
        Check.Equal(1, workIqTotals.SucceededEmpty);
        Check.True(!workIqTotals.InputTokens.IsReported);
        Check.Equal(4, workIqTotals.InputTokens.UnreportedCalls);
        Check.True(!workIqTotals.ProviderCost.Amount.IsReported);
        Check.True(!workIqTotals.TotalNanoAiu.IsReported);
        Check.True(!workIqTotals.CacheReadTokens.IsReported);
        Check.True(!workIqTotals.CacheWriteTokens.IsReported);
        Check.True(!workIqTotals.ReasoningTokens.IsReported);
        Check.True(!workIqTotals.QuotaSnapshots.IsReported);

        var rateLimited = snapshot.Requests.Single(
            request => request.Capability == AiCapability.Visualization);
        Check.Equal(AiRequestState.RateLimited, rateLimited.State);
        Check.Equal(TimeSpan.FromMinutes(3), rateLimited.RetryAfter);
        return Task.CompletedTask;
    }

    private static Task ResetAndLateResults()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new AiUsageMonitor(() => now);
        monitor.Reset("session-a");
        var stale = monitor.Begin(AiCapability.Synthesis);
        stale.CallStarted();

        monitor.Reset("session-b");
        stale.ReportUsage(999, 999, 999, 999, []);
        stale.Complete();

        var current = monitor.Begin(AiCapability.CatchUp, "session-b");
        current.CallStarted();
        current.Cancel();
        var rejected = monitor.Begin(AiCapability.WorkIqRetrieval, "session-a");
        rejected.CallStarted();
        rejected.Complete();

        var snapshot = monitor.GetSnapshot();
        Check.Equal("session-b", snapshot.SessionId);
        Check.Equal(1, snapshot.Requests.Count);
        Check.Equal(AiCapability.CatchUp, snapshot.Requests[0].Capability);
        Check.Equal(AiRequestState.Canceled, snapshot.Requests[0].State);
        Check.True(snapshot.Capabilities.All(item =>
            item.InputTokens.Total == 0 &&
            item.ProviderCost.Amount.Total == 0 &&
            item.TotalNanoAiu.Total == 0 &&
            !item.QuotaSnapshots.IsReported));
        return Task.CompletedTask;
    }

    private static AssistantUsageData CreateSdkUsageEvent()
    {
#pragma warning disable GHCP001
        var data = new AssistantUsageData
        {
            Model = "test-model",
            InputTokens = 120,
            OutputTokens = 30,
            CacheReadTokens = 20,
            CacheWriteTokens = 10,
            ReasoningTokens = 5,
            Cost = 1.5,
            CopilotUsage = new AssistantUsageCopilotUsage { TotalNanoAiu = 420.25 }
        };
#pragma warning restore GHCP001

        var assembly = typeof(AssistantUsageData).Assembly;
        var quotaType = assembly.GetType("GitHub.Copilot.AssistantUsageQuotaSnapshot", true)!;
        var quota = Activator.CreateInstance(quotaType, nonPublic: true)!;
        Set(quota, "EntitlementRequests", 300L);
        Set(quota, "IsUnlimitedEntitlement", false);
        Set(quota, "Overage", 0d);
        Set(quota, "OverageAllowedWithExhaustedQuota", false);
        Set(quota, "RemainingPercentage", 86d);
        Set(quota, "ResetDate",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        Set(quota, "UsageAllowedWithExhaustedQuota", false);
        Set(quota, "UsedRequests", 42L);

        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), quotaType);
        var snapshots = (IDictionary)Activator.CreateInstance(dictionaryType)!;
        snapshots.Add("premium_interactions", quota);
        Set(data, "QuotaSnapshots", snapshots);
        return data;
    }

    private static void Set(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        property.SetValue(target, value);
    }

    private static async Task PipelineReset()
    {
        await using var fixture = new PipelineFixture();
        await fixture.Pipeline.StartManualAsync("Synthetic usage session", CancellationToken.None);
        Check.True(ReferenceEquals(fixture.Usage, fixture.Pipeline.AiUsage));
        Check.Equal(fixture.Pipeline.CurrentSessionId, fixture.Usage.GetSnapshot().SessionId);
    }
}
