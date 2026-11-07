using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft;

/// <summary>The flat, derived asset files explicitly requested by Mojang's index.</summary>
internal static class DerivedAssetLayout
{
    internal sealed record Copy(string Source, string Destination, long Size, string Sha1);

    internal static IReadOnlyList<Copy> Read(string indexPath, string assets, string resources)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(indexPath));
        var root = json.RootElement;
        var virtualFiles = root.TryGetProperty("virtual", out var v) && v.ValueKind == JsonValueKind.True;
        var resourceFiles = root.TryGetProperty("map_to_resources", out var r) && r.ValueKind == JsonValueKind.True;
        if (!virtualFiles && !resourceFiles) return [];
        var id = Path.GetFileNameWithoutExtension(indexPath);
        if (id is "." or ".." || id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new InvalidDataException("Invalid legacy asset index ID.");
        var result = new List<Copy>();
        foreach (var item in root.GetProperty("objects").EnumerateObject())
        {
            var name = item.Name;
            if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name) ||
                name.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("An asset index contains an unsafe flat path.");
            var hash = item.Value.GetProperty("hash").GetString() ?? "";
            var size = item.Value.GetProperty("size").GetInt64();
            if (hash.Length != 40 || !hash.All(Uri.IsHexDigit) || size < 0)
                throw new InvalidDataException("An asset index contains an invalid content pin.");
            hash = hash.ToLowerInvariant();
            var source = Path.GetFullPath(Path.Combine(assets, "objects", hash[..2], hash));
            if (virtualFiles) result.Add(new(source, Path.GetFullPath(Path.Combine(assets, "virtual", id, name)), size, hash));
            if (resourceFiles) result.Add(new(source, Path.GetFullPath(Path.Combine(resources, name)), size, hash));
        }
        return result;
    }

    internal static int Restore(string indexPath, string assets, string resources, CancellationToken token)
    {
        var count = 0;
        foreach (var copy in Read(indexPath, assets, resources))
        {
            token.ThrowIfCancellationRequested();
            OrdinaryPath(copy.Destination);
            if (Matches(copy.Destination, copy)) continue;
            OrdinaryPath(copy.Source);
            if (!Matches(copy.Source, copy))
                throw new InvalidDataException("A required flat asset cannot be restored from its verified object: " + Path.GetFileName(copy.Source));
            Directory.CreateDirectory(Path.GetDirectoryName(copy.Destination)!);
            var temporary = copy.Destination + "." + Guid.NewGuid().ToString("N") + ".asset-tmp";
            try
            {
                File.Copy(copy.Source, temporary, overwrite: false);
                if (!Matches(temporary, copy)) throw new InvalidDataException("An asset object changed while its flat copy was restored.");
                File.Move(temporary, copy.Destination, overwrite: true);
                count++;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return count;
    }

    private static bool Matches(string path, Copy copy)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != copy.Size) return false;
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(input)) == copy.Sha1;
    }

    private static void OrdinaryPath(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A derived asset cannot be a filesystem link.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A derived asset directory cannot be a filesystem link.");
    }
}
