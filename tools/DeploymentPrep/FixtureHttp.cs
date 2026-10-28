using System.Net;
using System.Net.Http;

sealed class FixtureHttp(string? packDist, string? componentCache) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri ?? throw new IOException("Fixture request has no URI.");
        Requests.Add(uri.GetLeftPart(UriPartial.Path));
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        string? path = null;
        if (uri.AbsoluteUri == "https://raw.githubusercontent.com/MarkZamore/LANMinecraft/pack-catalog/catalog.json")
        {
            const string index = """
                {"schemaVersion":1,"packs":[{"id":"minecraft-26-2","relativePath":"Minecraft 26.3",
                "legacyRelativePaths":["Minecraft 26.2"],"legacySources":[{"owner":"MarkZamore","repo":"Minecraft-26.2","tag":"pack-latest"}],
                "source":{"owner":"MarkZamore","repo":"Minecraft-26.3","tag":"pack-latest"}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(index, System.Text.Encoding.UTF8, "application/json") });
        }
        if (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/MarkZamore/e4steam/releases/download/", StringComparison.Ordinal) && componentCache is not null)
            path = Path.Combine(componentCache, name);
        else if (uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/MarkZamore/Minecraft-26.3/releases/download/pack-latest/", StringComparison.Ordinal) && packDist is not null)
            path = Path.Combine(packDist, name == "pack-manifest.json" ? name : "assets/" + name);
        if (path is null || !File.Exists(path)) throw new HttpRequestException("Fixture refuses network request: " + uri.GetLeftPart(UriPartial.Path));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new ByteArrayContent(File.ReadAllBytes(path)) });
    }
}

sealed class RecordingHttp : DelegatingHandler
{
    public List<string> Requests { get; } = [];
    public RecordingHttp() : base(new HttpClientHandler()) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        lock (Requests) Requests.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
        return base.SendAsync(request, token);
    }
}
