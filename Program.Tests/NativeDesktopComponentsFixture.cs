using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

// Serves the publisher's exact verified packets read-only. The test lock changes
// transport endpoints only; hashes and all payloads remain the published pins.
internal sealed class NativeDesktopComponentServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Task accept;
    private readonly IReadOnlyDictionary<string, NativeDesktopComponent> components;
    private readonly ConcurrentDictionary<string, int> attempts = new();
    public ConcurrentQueue<NativeDesktopRequest> Requests { get; } = new();
    public string? FaultKind { get; set; }
    public NativeDesktopFault Fault { get; set; }
    public int ChunkDelayMilliseconds { get; set; }
    public long DisconnectAfterBytes { get; set; } = 524288;
    public bool Offline { get; set; }
    public Uri BaseUri { get; }

    public NativeDesktopComponentServer(IReadOnlyDictionary<string, NativeDesktopComponent> components)
    {
        this.components = components;
        listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        accept = Accept();
    }

    public void ResetFaults()
    {
        Fault = NativeDesktopFault.None;
        FaultKind = null;
        ChunkDelayMilliseconds = 0;
        DisconnectAfterBytes = 524288;
        Offline = false;
        attempts.Clear();
    }

    public void WriteLock(string catalogJson, string destination)
    {
        var root = JsonNode.Parse(catalogJson)!.AsObject();
        foreach (var component in root["components"]!.AsArray())
        {
            var item = component!.AsObject();
            item["url"] = new Uri(BaseUri, item["kind"]!.GetValue<string>() + ".pak").AbsoluteUri;
            item["apiUrl"] = "";
        }
        File.WriteAllText(destination, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
                connections.Add(Serve(await listener.AcceptTcpClientAsync(stop.Token)));
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var request = await reader.ReadLineAsync(stop.Token);
            if (request is null || request.Length > 4096) return;
            var route = request.Split(' ');
            if (route.Length != 3 || route[0] != "GET") return;
            string? range = null;
            var read = request.Length;
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } header)
            {
                if ((read += header.Length) > 16384) return;
                if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = header[6..].Trim();
            }
            var kind = route[1].TrimStart('/').Split('?', 2)[0];
            if (!kind.EndsWith(".pak", StringComparison.Ordinal)) return;
            kind = kind[..^4];
            if (!components.TryGetValue(kind, out var component)) return;
            var attempt = attempts.AddOrUpdate(kind, 1, (_, value) => value + 1);
            var fault = kind == FaultKind ? Fault : NativeDesktopFault.None;
            var receipt = new NativeDesktopRequest(kind, range, attempt);
            Requests.Enqueue(receipt);
            if (Offline || fault == NativeDesktopFault.HttpFailureOnce && attempt == 1)
            {
                await stream.WriteAsync("HTTP/1.1 503 Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                receipt.Status = 503;
                return;
            }
            long offset = 0;
            if (range is not null)
            {
                if (!range.StartsWith("bytes=", StringComparison.Ordinal) || !range.EndsWith('-') ||
                    !long.TryParse(range.AsSpan(6, range.Length - 7), NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                    offset < 0 || offset >= component.SizeBytes)
                {
                    receipt.Status = 416;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{component.SizeBytes}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                    return;
                }
            }
            if (fault == NativeDesktopFault.IgnoreRange) offset = 0;
            var partial = range is not null && fault != NativeDesktopFault.IgnoreRange;
            receipt.Status = partial ? 206 : 200;
            var headerText = $"HTTP/1.1 {receipt.Status} {(partial ? "Partial Content" : "OK")}\r\n" +
                $"Content-Length: {component.SizeBytes - offset}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n" +
                (partial ? $"Content-Range: bytes {offset}-{component.SizeBytes - 1}/{component.SizeBytes}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headerText), stop.Token);
            await using var packet = new FileStream(component.PacketPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            packet.Position = offset;
            var buffer = new byte[65536];
            while (await packet.ReadAsync(buffer, stop.Token) is var count && count > 0)
            {
                if (fault == NativeDesktopFault.CorruptOnce && attempt == 1 && receipt.BytesSent == 0) buffer[count - 1] ^= 0xff;
                await stream.WriteAsync(buffer.AsMemory(0, count), stop.Token);
                Interlocked.Add(ref receipt.BytesSent, count);
                if (fault is NativeDesktopFault.DisconnectOnce or NativeDesktopFault.IgnoreRange && attempt == 1 && receipt.BytesSent >= DisconnectAfterBytes) return;
                if (ChunkDelayMilliseconds > 0) await Task.Delay(ChunkDelayMilliseconds, stop.Token);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await accept;
        await Task.WhenAll(connections);
        stop.Dispose();
    }
}

internal enum NativeDesktopFault { None, HttpFailureOnce, DisconnectOnce, IgnoreRange, CorruptOnce }
internal sealed record NativeDesktopComponent(string Kind, string Id, string EntryPath, long SizeBytes, string Sha256,
    long ManifestSizeBytes, string ManifestSha256, string PacketPath);
internal sealed class NativeDesktopRequest(string kind, string? range, int attempt)
{
    public string Kind { get; } = kind;
    public string? Range { get; } = range;
    public int Attempt { get; } = attempt;
    public int Status;
    public long BytesSent;
}
