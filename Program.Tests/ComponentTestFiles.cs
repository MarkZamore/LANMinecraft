using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>Uses the pinned external component fixture prepared before tests.</summary>
internal static class ComponentTestFiles
{
    private static readonly object CopyGate = new();
    internal static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
            throw new DirectoryNotFoundException("Launcher test source checkout was not found.");
        }
    }
    internal static string AssetsDirectory => Environment.GetEnvironmentVariable("LANMINECRAFT_COMPONENT_TEST_ASSETS")
        ?? throw new InvalidOperationException("Run tools/Prepare-ComponentTests.ps1 before executing component-dependent tests.");

    [ModuleInitializer]
    internal static void Initialize()
    {
        var catalog = Path.Combine(AssetsDirectory, "catalog.json");
        RuntimeComponentContext.SetDefault(RuntimeComponentSnapshot.Parse(File.ReadAllText(catalog), int.MaxValue));
        RuntimeComponentContext.TestPathResolver = Seed;
    }

    internal static string ArtifactPath(string id)
    {
        var artifact = RuntimeComponentContext.Current.Artifacts.Single(item => item.Id == id);
        var path = Path.Combine(AssetsDirectory, artifact.FileName);
        Verify(path, artifact.SizeBytes, artifact.Sha256);
        return path;
    }

    internal static Stream OpenArtifact(string id) => File.OpenRead(ArtifactPath(id));

    internal static void SeedInstallation(AppPaths paths)
    {
        var snapshot = RuntimeComponentContext.Current;
        var root = Path.Combine(paths.Launcher, "Components");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            catalogJson = snapshot.CatalogJson,
            catalogSha256 = snapshot.Sha256
        }));
        foreach (var artifact in snapshot.Artifacts) Seed(paths, artifact);
    }

    private static string Seed(AppPaths paths, RuntimeComponentArtifact artifact)
    {
        var source = Path.Combine(AssetsDirectory, artifact.FileName);
        Verify(source, artifact.SizeBytes, artifact.Sha256);
        var directory = Path.Combine(paths.Launcher, "Components", "objects", artifact.Sha256);
        var destination = Path.Combine(directory, artifact.FileName);
        lock (CopyGate)
        {
            if (File.Exists(destination) && Matches(destination, artifact.SizeBytes, artifact.Sha256)) return destination;
            Directory.CreateDirectory(directory);
            File.Copy(source, destination, overwrite: true);
            Verify(destination, artifact.SizeBytes, artifact.Sha256);
        }
        return destination;
    }

    private static void Verify(string path, long size, string hash)
    {
        if (!Matches(path, size, hash)) throw new InvalidDataException("The pinned component test fixture is missing or corrupt: " + path);
    }

    private static bool Matches(string path, long size, string hash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
