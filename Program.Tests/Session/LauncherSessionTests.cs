namespace Minecraft.Tests;

public sealed class LauncherSessionTests
{
    [Fact]
    public async Task ClosingStopsAdmissionAndWaitsForAnOperationToReleaseItsResources()
    {
        await using var session = new LauncherSession();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = session.RunAsync("test operation", async () =>
        {
            using var registration = session.LifetimeToken.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await release.Task;
            throw new IOException("Operation failed while shutting down.");
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.BeginClosing();
            Assert.True(session.PlayToken.IsCancellationRequested);
            Assert.True(session.CatalogToken.IsCancellationRequested);
            Assert.False(session.LifetimeToken.IsCancellationRequested);
            var departureWasPublishedBeforeCancellation = false;
            var closing = session.CloseAsync(() =>
            {
                departureWasPublishedBeforeCancellation = !session.LifetimeToken.IsCancellationRequested;
                throw new IOException("Steam is unavailable.");
            });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(departureWasPublishedBeforeCancellation);
            Assert.False(closing.IsCompleted);
            await session.RunAsync("too late", () => throw new InvalidOperationException("Shutdown admitted new work."));
            release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => operation);
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(closing, session.CloseAsync());
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
