using MeetingCapture.Graph;

namespace MeetingCapture.Regression;

internal static class SignInChecks
{
    private sealed class Result : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("sign-in busy status is readable while success publication holds its guard",
            BusyReadDoesNotWaitForPublication);
        yield return ("sign-in success and failure release busy state and allow retry", async () =>
        {
            var observed = new List<Exception>();
            using var operation = new BoundedSignIn(TimeSpan.FromSeconds(5), observed.Add);
            var result = new Result();
            var publications = 0;
            await operation.RunAsync(_ => Task.FromResult(result), _ => publications++, CancellationToken.None);
            Check.Equal(1, publications);
            Check.True(!operation.IsBusy && !result.Disposed);
            result.Dispose();
            await Check.ThrowsAsync<IOException>(() => operation.RunAsync<Result>(
                _ => Task.FromException<Result>(new IOException("Synthetic auth failure")), _ => publications++, CancellationToken.None));
            Check.True(!operation.IsBusy);
            Check.Equal(1, publications);
            Check.Equal(1, observed.Count);
            await operation.RunAsync(_ => Task.FromResult(new Result()), value => value.Dispose(), CancellationToken.None);
        });
        yield return ("sign-in cancellation returns while late auth retains guard and cannot publish", async () =>
        {
            using var operation = new BoundedSignIn(TimeSpan.FromSeconds(5), _ => { });
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            var publications = 0;
            var pending = operation.RunAsync(_ => { started.SetResult(); return late.Task; },
                _ => publications++, cancellation.Token);
            await started.Task;
            cancellation.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => pending);
            Check.True(operation.IsBusy);
            await Check.ThrowsAsync<InvalidOperationException>(() => operation.RunAsync(
                _ => Task.FromResult(new Result()), _ => publications++, CancellationToken.None));
            var result = new Result();
            late.SetResult(result);
            while (operation.IsBusy) await Task.Yield();
            Check.True(!operation.IsBusy);
            Check.True(result.Disposed);
            Check.Equal(0, publications);
            await operation.RunAsync(_ => Task.FromResult(new Result()), value => value.Dispose(), CancellationToken.None);
        });
        yield return ("sign-in timeout restores caller without accepting late profile success", async () =>
        {
            using var operation = new BoundedSignIn(TimeSpan.FromMilliseconds(200), _ => { });
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = false;
            var pending = operation.RunAsync(_ => { started.SetResult(); return late.Task; },
                _ => published = true, CancellationToken.None);
            await started.Task;
            await Check.ThrowsAsync<TimeoutException>(() => pending);
            var result = new Result();
            late.SetResult(result);
            while (operation.IsBusy) await Task.Yield();
            Check.True(!published && result.Disposed);
        });
        yield return ("sign-in shutdown cancels and disposes late result without auth publication", async () =>
        {
            var operation = new BoundedSignIn(TimeSpan.FromSeconds(5), _ => { });
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var late = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            var published = false;
            var pending = operation.RunAsync(_ => { started.SetResult(); return late.Task; },
                _ => published = true, CancellationToken.None);
            await started.Task;
            operation.Dispose();
            await Check.ThrowsAsync<OperationCanceledException>(() => pending);
            var result = new Result();
            late.SetResult(result);
            while (operation.IsBusy) await Task.Yield();
            Check.True(!operation.IsBusy);
            Check.True(result.Disposed && !published);
            await Check.ThrowsAsync<ObjectDisposedException>(() => operation.RunAsync(
                _ => Task.FromResult(new Result()), _ => { }, CancellationToken.None));
        });
    }

    private static async Task BusyReadDoesNotWaitForPublication()
    {
        using var operation = new BoundedSignIn(TimeSpan.FromSeconds(5), _ => { });
        var authenticationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authentication = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublish = new ManualResetEventSlim();
        var run = operation.RunAsync(_ =>
        {
            authenticationStarted.TrySetResult();
            return authentication.Task;
        }, _ =>
        {
            enteredPublish.TrySetResult();
            releasePublish.Wait();
        }, CancellationToken.None);
        Task<bool>? busyRead = null;
        try
        {
            await authenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            authentication.SetResult(new Result());
            await enteredPublish.Task.WaitAsync(TimeSpan.FromSeconds(2));
            busyRead = Task.Run(() => operation.IsBusy);
            Check.True(await busyRead.WaitAsync(TimeSpan.FromSeconds(1)));
            Check.True(operation.IsBusy);
        }
        finally
        {
            authentication.TrySetResult(new Result());
            releasePublish.Set();
            await run;
            if (busyRead is not null) await busyRead;
        }
        Check.True(!operation.IsBusy);
    }
}
