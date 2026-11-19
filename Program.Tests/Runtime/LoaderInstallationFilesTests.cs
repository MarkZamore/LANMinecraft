using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Minecraft.Tests;

public sealed class LoaderInstallationFilesTests : IDisposable
{
    private const string Hash = "0123456789012345678901234567890123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loader-inventory-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ForgeCanonicalizesVersionAndPinsUnsignedProfileOutputsByRecipe()
    {
        var descriptor = Descriptor("1.20.1", PackLoaderKind.Forge, "47.4.0");
        var full = Descriptor("1.20.1", PackLoaderKind.Forge, "1.20.1-47.4.0");
        var path = LoaderInstallationFiles.GetInstallerPath(_root, descriptor);
        Assert.Equal(path, LoaderInstallationFiles.GetInstallerPath(_root, full));
        Assert.Equal(LoaderInstallationFiles.GetInstallerUri(descriptor), LoaderInstallationFiles.GetInstallerUri(full));
        WriteInstaller(path, $$$$"""
            {
                "minecraft":"1.20.1","version":"1.20.1-forge-47.4.0","json":"/version.json",
                "data":{
                    "PATCHED":{"client":"[net.minecraftforge:forge:1.20.1-47.4.0:client]"},
                    "PATCHED_SHA":{"client":"'{{{{Hash}}}}'"},
                    "MC_EXTRA":{"client":"[net.minecraft:client:1.20.1:extra]"},
                    "MC_EXTRA_SHA":{"client":"'{{{{Hash}}}}'"},
                    "MC_SRG":{"client":"[net.minecraft:client:1.20.1:srg]"}
                },
                "processors":[
                    {"sides":["client"],"outputs":{"{MC_EXTRA}":"{MC_EXTRA_SHA}"}},
                    {"sides":["server"],"outputs":{"{SERVER_ONLY}":"ignored"}}
                ]
            }
            """, $$$$"""
            {
                "id":"1.20.1-forge-47.4.0","inheritsFrom":"1.20.1",
                "libraries":[
                    {"name":"net.minecraftforge:forge:1.20.1-47.4.0:universal",
                     "downloads":{"artifact":{"sha1":"{{{{Hash}}}}"}}}
                ]
            }
            """);

        var files = LoaderInstallationFiles.Read(path, Path.Combine(_root, "libraries"), full, "1.20.1-forge-47.4.0");
        Assert.Equal(new[] { "client-1.20.1-extra.jar", "forge-1.20.1-47.4.0-client.jar", "forge-1.20.1-47.4.0-universal.jar" },
            files.Select(file => Path.GetFileName(file.Path)).Order(StringComparer.Ordinal));
        Assert.All(files, file => Assert.Equal(Hash, file.ExpectedSha1));
    }

    [Fact]
    public void NeoForgeRequiresPatchedClientEvenWhenItsDigestIsNotInTheInstaller()
    {
        var descriptor = Descriptor("26.3", PackLoaderKind.NeoForge, "26.3.0.99");
        var path = LoaderInstallationFiles.GetInstallerPath(_root, descriptor);
        WriteInstaller(path, $$$$"""
            {
                "minecraft":"26.3","version":"neoforge-26.3.0.99","json":"/version.json",
                "data":{"PATCHED":{"client":"[net.neoforged:minecraft-client-patched:26.3.0.99]"}},
                "libraries":[
                    {"name":"net.neoforged:neoforge:26.3.0.99:universal","downloads":{"artifact":{"sha1":"{{{{Hash}}}}"}}}
                ],
                "processors":[{"sides":["client"],"args":["--output","{PATCHED}"]}]
            }
            """, """
            {"id":"neoforge-26.3.0.99","inheritsFrom":"26.3","libraries":[]}
            """);
        var libraries = Path.Combine(_root, "libraries");

        var files = LoaderInstallationFiles.Read(path, libraries, descriptor);
        Assert.Null(Assert.Single(files, file => Path.GetFileName(file.Path) == "minecraft-client-patched-26.3.0.99.jar").ExpectedSha1);
        Assert.Equal(Hash, Assert.Single(files, file => Path.GetFileName(file.Path) == "neoforge-26.3.0.99-universal.jar").ExpectedSha1);
        Assert.Throws<InvalidDataException>(() => LoaderInstallationFiles.Read(path, libraries, descriptor, "neoforge-26.3.0.98"));
        Assert.Throws<InvalidDataException>(() => LoaderInstallationFiles.Read(path, libraries,
            Descriptor("26.2", PackLoaderKind.NeoForge, "26.3.0.99")));
    }

    [Fact]
    public void LegacyForgePinsTheInstallerEmbeddedCore()
    {
        const string embeddedName = "forge-1.12.2-14.23.5.2847-universal.jar";
        const string bytes = "the packaged installer core";
        const string coordinate = "net.minecraftforge:forge:1.12.2-14.23.5.2847";
        var descriptor = Descriptor("1.12.2", PackLoaderKind.Forge, "14.23.5.2847");
        var path = LoaderInstallationFiles.GetInstallerPath(_root, descriptor);
        WriteInstaller(path, $$$$"""
            {
                "install":{"minecraft":"1.12.2","target":"1.12.2-forge1.12.2-14.23.5.2847",
                    "path":"{{{{coordinate}}}}","filePath":"{{{{embeddedName}}}}"},
                "versionInfo":{"id":"1.12.2-forge1.12.2-14.23.5.2847","inheritsFrom":"1.12.2",
                    "libraries":[{"name":"{{{{coordinate}}}}"}]}
            }
            """, embeddedName: embeddedName, embeddedContent: bytes);

        var core = Assert.Single(LoaderInstallationFiles.Read(path, Path.Combine(_root, "libraries"), descriptor));
        Assert.Equal("forge-1.12.2-14.23.5.2847.jar", Path.GetFileName(core.Path));
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(bytes))), core.ExpectedSha1);
    }

    private static PackRuntimeDescriptor Descriptor(string minecraft, PackLoaderKind loader, string version) =>
        new(PackManifestService.CurrentSchemaVersion, minecraft, new PackLoaderDescriptor(loader, version), "", "");

    private static void WriteInstaller(string path, string recipe, string? profile = null,
        string? embeddedName = null, string? embeddedContent = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write("install_profile.json", recipe);
        if (profile is not null) Write("version.json", profile);
        if (embeddedName is not null) Write(embeddedName, embeddedContent!);

        void Write(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    public void Dispose() => TempTree.Delete(_root);
}
