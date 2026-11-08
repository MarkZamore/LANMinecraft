using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

internal sealed record LauncherReleaseAsset(HttpResponseMessage Response, long? Size = null, string? Sha256 = null) : IDisposable
{
    public void Dispose() => Response.Dispose();

    internal static async Task<LauncherReleaseAsset> OpenAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        // Reserve time for the independent official API route within the
        // caller's check deadline. Never let one failing gateway consume it all.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = Request(uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new(response);
            response.Dispose();
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        var prefix = $"/{UpdateService.RepositoryOwner}/{UpdateService.RepositoryName}/releases/download/{UpdateService.ReleaseTag}/";
        if (uri.Scheme != "https" || uri.Host != "github.com" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new HttpRequestException("No official API fallback for this update address.");
        var name = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        if (name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("Invalid release asset name.");
        var api = $"https://api.github.com/repos/{UpdateService.RepositoryOwner}/{UpdateService.RepositoryName}/releases/";
        using var metadataRequest = Request(new Uri(api + "tags/" + UpdateService.ReleaseTag +
            "?cache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        using var metadata = await client.SendAsync(metadataRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        metadata.EnsureSuccessStatusCode();
        await metadata.Content.LoadIntoBufferAsync(1024 * 1024, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await metadata.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        var matches = json.RootElement.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Release asset is absent or ambiguous.");
        var asset = matches[0];
        var id = asset.GetProperty("id").GetInt64();
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (id <= 0 || size <= 0 || asset.GetProperty("state").GetString() != "uploaded" ||
            !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$")) throw new InvalidDataException("Invalid release asset identity.");
        using var download = Request(new Uri(api + "assets/" + id));
        download.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var content = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!content.IsSuccessStatusCode)
        {
            content.Dispose();
            throw new HttpRequestException("Official release asset download failed.");
        }
        return new(content, size, digest[7..]);
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LANMinecraft/1.0");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        return request;
    }
}
