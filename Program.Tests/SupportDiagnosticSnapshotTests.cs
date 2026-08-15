using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class SupportDiagnosticSnapshotTests
{
    [Fact]
    public void VersionFallback_ReadsSelectedManifestAndPreparedJavaMetadataOnly()
    {
        using var fixture = new TemporaryPortableRoot();
        const string pack = "SelectedPack";
        var descriptor = WritePackManifest(fixture.Paths, pack, "1.21.1");
        var runtimeRoot = fixture.Paths.CombineUnderRuntimes(pack);
        var javaHome = Path.Combine(runtimeRoot, "java", "runtime");
        var javaExecutable = Path.Combine(javaHome, "bin", "javaw.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(javaExecutable)!);
        File.WriteAllBytes(javaExecutable, [0x4d, 0x5a]);
        File.WriteAllText(
            Path.Combine(javaHome, "release"),
            "JAVA_VERSION=\"21.0.8+9-LTS\"\nIMPLEMENTOR=\"Test\"\n");
        WriteRuntimeState(
            runtimeRoot,
            descriptor.DescriptorHash,
            "fabric-loader-0.16.14-1.21.1",
            Path.GetRelativePath(runtimeRoot, javaExecutable));
        var filesBefore = EnumerateRelativeFiles(fixture.Root);

        var fallback =
            SupportDiagnosticSnapshotBuilder.ResolveReadOnlyVersionFallback(
                fixture.Paths,
                pack);
        var merged = SupportDiagnosticSnapshotBuilder.MergeRuntimeVersionFallback(
            new Dictionary<string, string>
            {
                ["game.version"] = string.Empty,
                ["game.profile"] = string.Empty
            },
            fallback);

        Assert.Equal("1.21.1", fallback.MinecraftVersion);
        Assert.Equal("21.0.8+9-LTS", fallback.JavaVersion);
        Assert.Equal("fabric-loader-0.16.14-1.21.1", fallback.ProfileId);
        Assert.Equal("1.21.1", merged["game.version"]);
        Assert.Equal("fabric-loader-0.16.14-1.21.1", merged["game.profile"]);
        Assert.Equal(filesBefore, EnumerateRelativeFiles(fixture.Root));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Personal, "Temp")));
    }

    [Fact]
    public void VersionFallback_DoesNotOverrideLiveRuntimeValues()
    {
        var fallback = new SupportVersionFallback(
            "1.21.1",
            "21.0.8",
            "cached-profile");

        var merged = SupportDiagnosticSnapshotBuilder.MergeRuntimeVersionFallback(
            new Dictionary<string, string>
            {
                ["game.version"] = "1.21.4",
                ["game.profile"] = "live-profile"
            },
            fallback);

        Assert.Equal("1.21.4", merged["game.version"]);
        Assert.Equal("live-profile", merged["game.profile"]);
    }

    [Fact]
    public void VersionFallback_RejectsRuntimePathEscapeAndStaleDescriptor()
    {
        using var fixture = new TemporaryPortableRoot();
        const string pack = "SelectedPack";
        var descriptor = WritePackManifest(fixture.Paths, pack, "1.21.1");
        var runtimeRoot = fixture.Paths.CombineUnderRuntimes(pack);
        var outsideHome = Path.Combine(fixture.Paths.Personal, "OutsideJava");
        var outsideJava = Path.Combine(outsideHome, "bin", "javaw.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideJava)!);
        File.WriteAllBytes(outsideJava, [0x4d, 0x5a]);
        File.WriteAllText(
            Path.Combine(outsideHome, "release"),
            "JAVA_VERSION=\"malicious\"\n");
        WriteRuntimeState(
            runtimeRoot,
            descriptor.DescriptorHash,
            "profile",
            Path.GetRelativePath(runtimeRoot, outsideJava));

        var escaped =
            SupportDiagnosticSnapshotBuilder.ResolveReadOnlyVersionFallback(
                fixture.Paths,
                pack);
        Assert.Equal("1.21.1", escaped.MinecraftVersion);
        Assert.Empty(escaped.JavaVersion);
        Assert.Empty(escaped.ProfileId);

        var internalJava = Path.Combine(runtimeRoot, "java", "bin", "javaw.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(internalJava)!);
        File.WriteAllBytes(internalJava, [0x4d, 0x5a]);
        File.WriteAllText(
            Path.Combine(runtimeRoot, "release"),
            "JAVA_VERSION=\"21.0.8\"\n");
        WriteRuntimeState(
            runtimeRoot,
            new string('0', 64),
            "stale-profile",
            Path.GetRelativePath(runtimeRoot, internalJava));

        var stale =
            SupportDiagnosticSnapshotBuilder.ResolveReadOnlyVersionFallback(
                fixture.Paths,
                pack);
        Assert.Equal("1.21.1", stale.MinecraftVersion);
        Assert.Empty(stale.JavaVersion);
        Assert.Empty(stale.ProfileId);
    }

    private static PackRuntimeDescriptor WritePackManifest(
        AppPaths paths,
        string pack,
        string minecraftVersion)
    {
        var packRoot = paths.CombineUnderPacks(pack);
        Directory.CreateDirectory(packRoot);
        File.WriteAllText(
            Path.Combine(packRoot, PackManifestService.ManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = PackManifestService.CurrentSchemaVersion,
                minecraftVersion,
                loader = new { type = "fabric", version = "0.16.14" },
                clientJar = "client.jar"
            }));
        return PackManifestService.Load(packRoot);
    }

    private static void WriteRuntimeState(
        string runtimeRoot,
        string descriptorHash,
        string profileId,
        string javaPath)
    {
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(
            Path.Combine(runtimeRoot, ".portable-runtime.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                descriptorHash,
                profileId,
                javaPathRelativePath = javaPath.Replace('\\', '/'),
                javaRuntimeId = PortableJavaRuntimeService.PinnedRuntimeId
            }));
    }

    private static string[] EnumerateRelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private sealed class TemporaryPortableRoot : IDisposable
    {
        public TemporaryPortableRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "MinecraftDiagnosticSnapshotTests",
                Guid.NewGuid().ToString("N"));
            Paths = new AppPaths(Root);
            Paths.Ensure();
        }

        public string Root { get; }
        public AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
