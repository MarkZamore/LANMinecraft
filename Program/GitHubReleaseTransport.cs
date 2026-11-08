using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Minecraft;

/// <summary>Official release fallback, with streaming integrity verification. Callers retain their own pins and installation policy.</summary>
internal static class GitHubReleaseTransport
{
    private const long MetadataLimit = 8 * 1024 * 1024;
    private const long JsonAssetLimit = 16 * 1024 * 1024;
    private static readonly ConditionalWeakTable<HttpClient, ConcurrentDictionary<string, MetadataSlot>> Metadata = new();
    private sealed class MetadataSlot
    {
        internal readonly SemaphoreSlim Gate = new(1);
        internal byte[]? Bytes;
        internal DateTimeOffset Expires;
    }

    internal static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await SendAsync(client, request, token).ConfigureAwait(false);
    }

    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, string uri, CancellationToken token) =>
        GetAsync(client, new Uri(uri), token);

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token,
        TimeSpan? primaryTimeout = null)
    {
        var original = request.RequestUri!;
        if (request.Method != HttpMethod.Get || !TryParse(original, out var target))
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        HttpResponseMessage? failed = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(primaryTimeout ?? TimeSpan.FromSeconds(8));
            try
            {
                var direct = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                try { RequireEndpoint(direct.RequestMessage?.RequestUri ?? original, original, allowCdn: true); }
                catch { direct.Dispose(); throw; }
                if (direct.IsSuccessStatusCode) return direct;
                if ((int)direct.StatusCode < 500 && direct.StatusCode is not
                    (HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)) return direct;
                failed = direct;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();

            var api = $"https://api.github.com/repos/{target.Owner}/{target.Repo}/releases/";
            var metadataUri = new Uri(api + "tags/" + Uri.EscapeDataString(target.Tag) + "?cache=" + Guid.NewGuid().ToString("N"));
            var bytes = await ReadMetadataAsync(client, metadataUri, target.Name, token).ConfigureAwait(false);
            if (bytes is null) return Missing(request);
            var identity = ReadIdentity(bytes, target.Name);
            if (identity is null) return Missing(request);

            // Do not resume a partial file through a different route: callers
            // see HTTP 200 and restart their partial file, preserving full SHA verification.
            var assetUri = new Uri(api + "assets/" + identity.Id);
            using var download = ApiRequest(assetUri);
            download.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var content = await client.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                RequireEndpoint(content.RequestMessage?.RequestUri ?? assetUri, assetUri, allowCdn: true);
                content.EnsureSuccessStatusCode();
                if (content.StatusCode != HttpStatusCode.OK ||
                    content.Content.Headers.ContentLength is { } length && length != identity.Size)
                    throw new InvalidDataException("GitHub API asset length or range differs from its metadata.");
                content.Content = new VerifiedContent(content.Content, identity.Size, identity.Sha256);
                if (target.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (identity.Size > JsonAssetLimit) throw new InvalidDataException("GitHub JSON asset exceeds its verification limit.");
                    // JSON consumers may finish parsing before EOF. Verify it
                    // completely before any manifest/catalog can be accepted.
                    await content.Content.LoadIntoBufferAsync(JsonAssetLimit, token).ConfigureAwait(false);
                }
                // The API hop has passed the strict endpoint check above. Keep
                // the logical approved source for existing caller redirect policies.
                content.RequestMessage = request;
                return content;
            }
            catch { content.Dispose(); throw; }
        }
        finally { failed?.Dispose(); }
    }

    private static HttpResponseMessage Missing(HttpRequestMessage request) => new(HttpStatusCode.NotFound) { RequestMessage = request };
    private sealed record Target(string Owner, string Repo, string Tag, string Name);
    private sealed record Identity(long Id, long Size, string Sha256);

    private static async Task<byte[]?> ReadMetadataAsync(HttpClient client, Uri uri, string name, CancellationToken token)
    {
        var slots = Metadata.GetValue(client, _ => new());
        if (slots.Count > 32)
            foreach (var entry in slots.OrderBy(entry => entry.Value.Expires).Take(slots.Count - 32))
                if (entry.Value.Gate.CurrentCount > 0) slots.TryRemove(entry.Key, out _);
        var slot = slots.GetOrAdd(uri.GetLeftPart(UriPartial.Path), _ => new());
        await slot.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && slot.Bytes is { } cached &&
                slot.Expires > DateTimeOffset.UtcNow && ReadIdentity(cached, name) is not null) return cached;
            using var request = ApiRequest(uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            RequireEndpoint(response.RequestMessage?.RequestUri ?? uri, uri, allowCdn: false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { slot.Bytes = null; return null; }
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MetadataLimit, token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (ReadIdentity(bytes, name) is not null)
            {
                slot.Bytes = bytes;
                slot.Expires = DateTimeOffset.UtcNow.AddMinutes(5);
            }
            return bytes;
        }
        finally { slot.Gate.Release(); }
    }

    private static bool TryParse(Uri uri, out Target target)
    {
        target = null!;
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        var parts = uri.AbsolutePath.Split('/');
        if (parts.Length != 7 || parts[3] != "releases" || parts[4] != "download") return false;
        var owner = Uri.UnescapeDataString(parts[1]);
        var repo = Uri.UnescapeDataString(parts[2]);
        var tag = Uri.UnescapeDataString(parts[5]);
        var name = Uri.UnescapeDataString(parts[6]);
        if (!Regex.IsMatch(owner, "^[A-Za-z0-9_-]+$") || !Regex.IsMatch(repo, "^[A-Za-z0-9_.-]+$") || repo is "." or ".." ||
            tag.Length == 0 || name.Length == 0 || tag is "." or ".." || name is "." or ".." ||
            name.Contains('/') || name.Contains('\\') || tag.Any(char.IsControl) || name.Any(char.IsControl)) return false;
        target = new(owner, repo, tag, name);
        return true;
    }

    private static Identity? ReadIdentity(byte[] bytes, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var matches = json.RootElement.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1) throw new InvalidDataException("Ambiguous GitHub release asset.");
            var asset = matches[0];
            var id = asset.GetProperty("id").GetInt64();
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (id <= 0 || size <= 0 || asset.GetProperty("state").GetString() != "uploaded" || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
                throw new InvalidDataException("Invalid GitHub release asset identity.");
            return new(id, size, digest[7..]);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid GitHub release metadata.", ex); }
    }

    private static HttpRequestMessage ApiRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("LANMinecraft/1.0");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        return request;
    }

    private static void RequireEndpoint(Uri effective, Uri expected, bool allowCdn)
    {
        if (effective.Scheme != "https" || !effective.IsDefaultPort || effective.UserInfo.Length != 0 ||
            !(effective.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) && effective.AbsolutePath == expected.AbsolutePath ||
              allowCdn && effective.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
            throw new InvalidDataException("GitHub release request reached an unapproved endpoint.");
    }

    private sealed class VerifiedContent(HttpContent original, long size, string sha256) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => CopyAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => CopyAsync(stream, token);
        private async Task CopyAsync(Stream output, CancellationToken token)
        {
            await using var input = await CreateContentReadStreamAsync(token).ConfigureAwait(false);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token) =>
            new VerifiedStream(await original.ReadAsStreamAsync(token).ConfigureAwait(false), size, sha256);
        protected override void Dispose(bool disposing) { if (disposing) original.Dispose(); base.Dispose(disposing); }
    }

    private sealed class VerifiedStream(Stream input, long size, string expected) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long count;
        private bool verified;
        private int Accept(ReadOnlySpan<byte> bytes)
        {
            count += bytes.Length;
            if (count > size) throw new InvalidDataException("GitHub asset exceeds its declared size.");
            if (!verified) hash.AppendData(bytes);
            if (bytes.Length == 0 || count == size)
            {
                if (count != size || !verified && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GitHub asset SHA256 or size does not match its metadata.");
                verified = true;
            }
            return bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int length) => Read(buffer.AsSpan(offset, length));
        public override int Read(Span<byte> buffer) => buffer.Length == 0 ? 0 : Accept(buffer[..input.Read(buffer)]);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.Length == 0) return 0;
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            return Accept(buffer.Span[..read]);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int length, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, length), token).AsTask();
        public override bool CanRead => input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { hash.Dispose(); input.Dispose(); } base.Dispose(disposing); }
    }
}
