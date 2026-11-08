namespace Minecraft;

internal static class UpdateCheckRetry
{
    internal static async Task<UpdateCheckResult> UntilResolvedAsync(
        Func<CancellationToken, Task<UpdateCheckResult>> check, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var seconds = 5;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = await check(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.IsUnavailable) return result;
            await delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            seconds = Math.Min(seconds * 2, 30);
        }
    }
}
