namespace Minecraft.Tests;

public sealed class UpdateCheckRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongOutageContinuesUntilAConfirmedAnswerThenStops(bool update)
    {
        var calls = 0;
        var delays = new List<double>();
        var answer = update ? UpdateCheckResult.Available(new UpdateManifest(), null) : UpdateCheckResult.UpToDate();
        var actual = await UpdateCheckRetry.UntilResolvedAsync(_ => Task.FromResult(
            ++calls <= 12 ? UpdateCheckResult.Unavailable("offline") : answer), CancellationToken.None,
            (delay, _) => { delays.Add(delay.TotalSeconds); return Task.CompletedTask; });
        Assert.Same(answer, actual);
        Assert.Equal(13, calls);
        Assert.Equal(new double[] { 5, 10, 20, 30, 30, 30, 30, 30, 30, 30, 30, 30 }, delays);
    }

    [Fact]
    public async Task ClosingDuringTheRetryDelayCancelsWithoutAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pending = UpdateCheckRetry.UntilResolvedAsync(_ =>
        {
            calls++;
            return Task.FromResult(UpdateCheckResult.Unavailable("offline"));
        }, cancellation.Token, async (_, token) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, calls);
    }
}
