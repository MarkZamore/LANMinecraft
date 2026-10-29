using System.IO;
using System.Net;
using System.Text.Json;

namespace Minecraft;

/// <summary>Passes explicitly requested launcher-created inputs to pack-owned startup code.</summary>
internal static class PackLaunchContextService
{
    internal const string DescriptorPath = "launcher/launch-context.json";
    internal const string RuntimeDirectory = "launcher-runtime";
    internal const string RuntimePath = RuntimeDirectory + "/launch-context.json";
    internal const string LocalAiEndpoint = "localAi.endpoint";

    internal static void Write(string packDirectory, string instanceDirectory, IReadOnlyDictionary<string, Uri> bindings)
    {
        var descriptor = OrdinaryPath(packDirectory, DescriptorPath);
        if (Directory.Exists(descriptor)) throw new InvalidDataException("Launch context descriptor must be a file.");
        if (!File.Exists(descriptor)) return;
        if (new FileInfo(descriptor).Length > 4096)
            throw new InvalidDataException("Launch context descriptor is too large.");
        using var document = JsonDocument.Parse(File.ReadAllText(descriptor), new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(field =>
                field.Name is not ("schemaVersion" or "requests") || !fields.Add(field.Name)) ||
            !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != 1 ||
            !root.TryGetProperty("requests", out var requests) || requests.ValueKind != JsonValueKind.Array ||
            requests.GetArrayLength() == 0)
            throw new InvalidDataException("Unsupported or duplicate launch context descriptor fields.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var request in requests.EnumerateArray())
        {
            if (request.ValueKind != JsonValueKind.String || request.GetString() is not LocalAiEndpoint ||
                values.ContainsKey(LocalAiEndpoint))
                throw new InvalidDataException("Unsupported or duplicate launch context request.");
            if (!bindings.TryGetValue(LocalAiEndpoint, out var endpoint) || endpoint is null || !endpoint.IsAbsoluteUri ||
                endpoint.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) ||
                !IPAddress.IsLoopback(address) || endpoint.Port <= 0 || endpoint.UserInfo.Length != 0 ||
                endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new InvalidDataException("The requested local AI endpoint was not created for this launch.");
            values.Add(LocalAiEndpoint, endpoint.GetLeftPart(UriPartial.Authority));
        }

        var destination = OrdinaryPath(instanceDirectory, RuntimePath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, values });
        // Keep an identical context untouched. A changed context is published in
        // one same-directory rename; failure never deletes the previous context.
        if (File.Exists(destination) && new FileInfo(destination).Length == bytes.Length &&
            File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes)) return;
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".launch-context." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Recheck the fixed destination after preparing the file; neither
            // pack data nor instance links may redirect this private output.
            _ = OrdinaryPath(instanceDirectory, RuntimePath);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string OrdinaryPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Launch context paths cannot pass through filesystem links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
}
