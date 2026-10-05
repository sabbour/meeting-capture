using System.Reflection;
using MeetingCapture.WorkIq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

namespace MeetingCapture.Regression;

internal static class WorkIqAuthSnapshotChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("WorkIQ auth snapshot readers do not wait for the cache gate", ReadersDoNotWaitForCacheGate);
        yield return ("WorkIQ auth snapshot publishes coherent success, sign-out, and dispose revisions",
            StateTransitionsPublishCoherentSnapshot);
    }

    private static async Task ReadersDoNotWaitForCacheGate()
    {
        using var temporary = new TemporaryDirectory();
        using var service = CreateService(temporary.Path);
        var gate = GetField("_gate").GetValue(service)
            ?? throw new InvalidOperationException("WorkIQ state gate is unavailable.");
        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (gate)
            {
                gateHeld.TrySetResult();
                release.Wait();
            }
        });

        try
        {
            await gateHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var read = Task.Run(() =>
                (AuthState: service.AuthState, service.IsAuthenticated, service.IsAvailable, service.Revision));
            var observed = await read.WaitAsync(TimeSpan.FromSeconds(1));
            Check.Equal((false, 0L), observed.AuthState);
            Check.True(!observed.IsAuthenticated && !observed.IsAvailable);
            Check.Equal(0L, observed.Revision);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static Task StateTransitionsPublishCoherentSnapshot()
    {
        using var temporary = new TemporaryDirectory();
        var service = CreateService(temporary.Path);
        var disposed = false;
        try
        {
            PublishAuthenticatedState(service, 40);
            Check.Equal((true, 40L), service.AuthState);
            Check.True(service.IsAuthenticated && service.IsAvailable);
            Check.Equal(40L, service.Revision);

            var notifiedState = (IsAuthenticated: false, Revision: -1L);
            var notifications = 0;
            service.AuthStateChanged += (_, _) =>
            {
                notifiedState = service.AuthState;
                notifications++;
            };

            service.SignOut();
            Check.Equal((false, 41L), service.AuthState);
            Check.True(!service.IsAuthenticated && !service.IsAvailable);
            Check.Equal(41L, service.Revision);
            Check.Equal(1, notifications);
            Check.Equal((false, 41L), notifiedState);

            PublishAuthenticatedState(service, 42);
            Check.Equal((true, 42L), service.AuthState);
            Check.Equal(42L, service.Revision);

            service.Dispose();
            disposed = true;
            Check.Equal((false, 43L), service.AuthState);
            Check.True(!service.IsAuthenticated && !service.IsAvailable);
            Check.Equal(43L, service.Revision);
            Check.Equal(1, notifications);
        }
        finally
        {
            if (!disposed) service.Dispose();
        }
        return Task.CompletedTask;
    }

    private static WorkIqService CreateService(string cacheDirectory)
        => new(NullLogger<WorkIqService>.Instance,
            new ProtectedWorkIqCache(Path.Combine(cacheDirectory, "synthetic-workiq-cache.bin")));

    private static FieldInfo GetField(string name)
        => typeof(WorkIqService).GetField(name, PrivateInstance)
            ?? throw new InvalidOperationException($"WorkIQ field {name} is unavailable.");

    private static void PublishAuthenticatedState(WorkIqService service, long revision)
    {
        var gate = GetField("_gate").GetValue(service)
            ?? throw new InvalidOperationException("WorkIQ state gate is unavailable.");
        var publish = typeof(WorkIqService).GetMethod("PublishAuthStateLocked", PrivateInstance)
            ?? throw new InvalidOperationException("WorkIQ auth-state publisher is unavailable.");
        lock (gate)
        {
            GetField("_app").SetValue(service,
                DispatchProxy.Create<IPublicClientApplication, InertPublicClientApplicationProxy>());
            GetField("_revision").SetValue(service, revision);
            publish.Invoke(service, null);
        }
    }
}

public class InertPublicClientApplicationProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new NotSupportedException("The auth-state test proxy must not be invoked.");
}
