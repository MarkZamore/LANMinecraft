using System.IO;
using System.Net;
using System.Net.Http;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ChangelogRemoteTests
{
    [Fact]
    public async Task RefreshCachesValidatedHistoryAndPreservesItAcrossFailuresAndOfflineStarts()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "agent-runs", "remote-history-540", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cache = Path.Combine(directory, "Changelog.md");
        const string initial = "## 1\n- First.";
        const string fresh = "## 2\n- Second.\n## 1\n- First.";
        try
        {
            using var handler = new HistoryHandler();
            using var http = new HttpClient(handler);
            Assert.Empty(ChangelogService.LoadCached(cache));
            handler.Content = initial;
            Assert.Single(await ChangelogService.RefreshAsync(cache, default, client: http));
            Assert.Equal(initial, File.ReadAllText(cache));
            handler.Content = fresh;
            Assert.Equal(2, (await ChangelogService.RefreshAsync(cache, default, client: http)).Count);
            Assert.Equal(fresh, File.ReadAllText(cache));
            Assert.Equal(2, ChangelogService.LoadCached(cache).Count);
            var timestamp = File.GetLastWriteTimeUtc(cache);
            await ChangelogService.RefreshAsync(cache, default, client: http);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(cache));
            foreach (var invalid in new[] { "", "<html>error</html>", "## 3\n- Missing versions.", new string('x', ChangelogService.MaximumBytes + 1) })
            {
                handler.Content = invalid;
                Assert.Equal(2, (await ChangelogService.RefreshAsync(cache, default, client: http)).Count);
                Assert.Equal(fresh, File.ReadAllText(cache));
            }
            handler.Offline = true;
            Assert.Equal(2, (await ChangelogService.RefreshAsync(cache, default, client: http)).Count);
            File.Delete(cache);
            Assert.Empty(await ChangelogService.RefreshAsync(cache, default, client: http));
            handler.Offline = false;
            handler.Content = initial;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChangelogService.RefreshAsync(cache, cancellation.Token, client: http));
            Assert.False(File.Exists(cache));
            File.WriteAllText(cache, "broken cache");
            Assert.Empty(ChangelogService.LoadCached(cache));
            Assert.Single(await ChangelogService.RefreshAsync(cache, default, client: http));
            Assert.True(handler.Requests > 1);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "artifacts", "agent-runs")) + Path.DirectorySeparatorChar, Path.GetFullPath(directory));
            TempTree.Delete(directory);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Program", "Changelog.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository missing");
    }

    private sealed class HistoryHandler : HttpMessageHandler
    {
        internal string Content = "";
        internal bool Offline;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            Assert.Contains("fresh=", request.RequestUri.Query);
            Assert.True(request.Headers.CacheControl?.NoCache);
            Requests++;
            if (Offline) throw new HttpRequestException("Offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Content) });
        }
    }
}
