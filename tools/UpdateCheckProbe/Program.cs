using System.IO;
using System.Net.Http;
using System.Text.Json;
using Minecraft;

if (args.Length > 0 && args[0] == "--release-matrix") return await ReleaseMatrix.RunAsync(args[1..]);

// Read-only live probe of the production check. No launcher startup or download
// of executable updates; output stays in the assigned task directory.
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!output.StartsWith(Path.Combine(root, "artifacts", "agent-runs") + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use an owned agent-run directory.");
Directory.CreateDirectory(output);
using var handler = new ProbeHandler();
using var client = new HttpClient(handler);
var service = new UpdateService(new AppPaths(root), new Logger(Path.Combine(output, "probe.log")), client, args[2]);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var result = await service.CheckAsync(deadline.Token, attempts: 1, attemptTimeout: TimeSpan.FromSeconds(25));
var proof = new { currentCommit = args[2], result.IsUnavailable, result.IsUpdateAvailable,
    release = result.Manifest?.ReleaseNumber, requests = handler.Requests };
var json = JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "live-check.json"), json);
Console.WriteLine(json);
return result.IsUnavailable ? 1 : 0;

sealed class ProbeHandler : DelegatingHandler
{
    internal List<object> Requests { get; } = [];
    internal ProbeHandler() : base(new HttpClientHandler()) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!.GetLeftPart(UriPartial.Path);
        try
        {
            var response = await base.SendAsync(request, token);
            Requests.Add(new { uri, status = (int)response.StatusCode });
            return response;
        }
        catch (OperationCanceledException)
        {
            Requests.Add(new { uri, status = "timeout/cancelled" });
            throw;
        }
    }
}
