using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class PackRuntimeLoaderReuseTests : IDisposable
{
    private const string FabricVersion = "0.16.14";
    private const string MinecraftVersion = "1.20.1";
    private const string FabricProfile = "fabric-loader-" + FabricVersion + "-" + MinecraftVersion;
    private const string NeoForgeVersion = "21.1.248";
    private const string NeoForgeMinecraft = "1.21.1";
    private const string NeoForgeProfile = "neoforge-" + NeoForgeVersion;
    private const string RuntimeStateName = ".portable-runtime.json";
    private const string LoaderReceiptName = ".portable-loader.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "loader-reuse-" + Guid.NewGuid().ToString("N")));
    private readonly OfflineHandler _handler = new();

    public PackRuntimeLoaderReuseTests() => _paths.Ensure();

    [Fact]
    public async Task MatchingFabricDonorBecomesAnOfflineReceiptForAnotherPersonalPack()
    {
        var donor = CreatePack("Installed pack", MinecraftVersion, "fabric", FabricVersion);
        var personal = CreatePack("Personal pack", MinecraftVersion, "fabric", FabricVersion, javaMajor: 17);
        CreatePack("Another personal pack", MinecraftVersion, "fabric", FabricVersion);
        var profile = CreateProfiles(MinecraftVersion, FabricProfile);
        CreateLegacyState("Installed pack", donor, FabricProfile, profile);
        var java = CreateJava(personal);

        using var http = new HttpClient(_handler, disposeHandler: false);
        using var service = new PackRuntimeService(_paths, new Logger(_paths.LogFile), http);
        var prepared = await service.PrepareAsync("Personal pack", null, CancellationToken.None);

        Assert.Equal(FabricProfile, prepared.ProfileId);
        Assert.Equal(profile.Client, prepared.ClientJarPath);
        Assert.Equal(java, prepared.JavaPath);
        Assert.True(File.Exists(ReceiptPath(FabricProfile)));
        var warm = await service.PrepareAsync("Personal pack", null, CancellationToken.None);
        Assert.Equal(prepared.ClientJarPath, warm.ClientJarPath);
        Assert.Equal(prepared.JavaPath, warm.JavaPath);
        Assert.Equal(0, _handler.RequestCount);
        using (var state = JsonDocument.Parse(File.ReadAllText(StatePath("Personal pack"))))
            Assert.Equal(personal.DescriptorHash, state.RootElement.GetProperty("descriptorHash").GetString());

        // Remove every candidate v6 donor state. Only the shared receipt can
        // provide the matching proof for this pack's first preparation.
        File.Delete(StatePath("Installed pack"));
        File.Delete(StatePath("Personal pack"));
        var preparedAgain = await service.PrepareAsync("Another personal pack", null, CancellationToken.None);
        Assert.Equal(FabricProfile, preparedAgain.ProfileId);
        Assert.True(File.Exists(StatePath("Another personal pack")));
        Assert.Equal(0, _handler.RequestCount);
    }

    [Fact]
    public async Task DifferentFabricVersionDoesNotReuseLegacyDonor()
    {
        var donor = CreatePack("Installed pack", MinecraftVersion, "fabric", FabricVersion);
        var personal = CreatePack("Personal pack", MinecraftVersion, "fabric", "0.16.13");
        CreateLegacyState("Installed pack", donor, FabricProfile, CreateProfiles(MinecraftVersion, FabricProfile));
        CreateJava(personal);

        using var http = new HttpClient(_handler, disposeHandler: false);
        using var service = new PackRuntimeService(_paths, new Logger(_paths.LogFile), http);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareAsync("Personal pack", null, CancellationToken.None));

        Assert.True(_handler.RequestCount > 0);
        Assert.False(File.Exists(StatePath("Personal pack")));
    }

    [Fact]
    public async Task NeoForgeReceiptChecksGeneratedOutputEvenWithOriginalSizeAndTimestamp()
    {
        var installed = CreatePack("First personal pack", NeoForgeMinecraft, "neoforge", NeoForgeVersion);
        CreatePack("Second personal pack", NeoForgeMinecraft, "neoforge", NeoForgeVersion);
        var profile = CreateProfiles(NeoForgeMinecraft, NeoForgeProfile);
        var output = WriteFile(Path.Combine(SharedRuntimeStore.Libraries(_paths), "net", "neoforged",
            "minecraft-client-patched", NeoForgeVersion, $"minecraft-client-patched-{NeoForgeVersion}.jar"), "patched");
        WriteJson(ReceiptPath(NeoForgeProfile), new
        {
            schemaVersion = 1,
            identity = new { minecraftVersion = NeoForgeMinecraft, type = PackLoaderKind.NeoForge, version = NeoForgeVersion },
            profileId = NeoForgeProfile,
            librarySourceKey = "",
            loaderFiles = new[] { RelativePath(output) },
            files = Proof([.. profile.Files, output])
        });
        CreateJava(installed);

        using var http = new HttpClient(_handler, disposeHandler: false);
        using var service = new PackRuntimeService(_paths, new Logger(_paths.LogFile), http);
        var prepared = await service.PrepareAsync("First personal pack", null, CancellationToken.None);
        Assert.Equal(NeoForgeProfile, prepared.ProfileId);
        Assert.Equal(0, _handler.RequestCount);
        using (var state = JsonDocument.Parse(File.ReadAllText(StatePath("First personal pack"))))
            Assert.True(state.RootElement.GetProperty("files").TryGetProperty(RelativePath(output), out _));

        var original = new FileInfo(output);
        var originalTimestamp = original.LastWriteTimeUtc;
        var originalLength = original.Length;
        File.WriteAllText(output, "spoiled");
        File.SetLastWriteTimeUtc(output, originalTimestamp);
        Assert.Equal(originalLength, new FileInfo(output).Length);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(output));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareAsync("Second personal pack", null, CancellationToken.None));
        Assert.True(_handler.RequestCount > 0);
        Assert.False(File.Exists(StatePath("Second personal pack")));
    }

    private PackRuntimeDescriptor CreatePack(string name, string minecraft, string loaderType, string loaderVersion,
        int? javaMajor = null)
    {
        var directory = _paths.CombineUnderPacks(name);
        WriteJson(Path.Combine(directory, PackManifestService.ManifestFileName), new
        {
            schemaVersion = PackManifestService.CurrentSchemaVersion,
            minecraftVersion = minecraft,
            loader = new { type = loaderType, version = loaderVersion },
            javaMajorVersion = javaMajor
        });
        return PackManifestService.Load(directory);
    }

    private ProfileFixture CreateProfiles(string minecraft, string loaderProfile)
    {
        var versions = SharedRuntimeStore.Versions(_paths);
        var client = WriteFile(Path.Combine(versions, minecraft, minecraft + ".jar"), "fixture client");
        var clientSha1 = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(client)));
        const string assetId = "fixture-index";
        var assetIndex = WriteFile(Path.Combine(SharedRuntimeStore.Assets(_paths), "indexes", assetId + ".json"),
            """{"objects":{}}""");
        var baseJson = WriteJson(Path.Combine(versions, minecraft, minecraft + ".json"), new
        {
            id = minecraft,
            type = "release",
            mainClass = "net.minecraft.client.main.Main",
            assets = assetId,
            assetIndex = new
            {
                id = assetId,
                url = "https://fixture.invalid/index.json",
                sha1 = Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(assetIndex))),
                size = new FileInfo(assetIndex).Length
            },
            downloads = new
            {
                client = new
                {
                    url = $"https://piston-data.mojang.com/v1/objects/{clientSha1}/client.jar",
                    size = new FileInfo(client).Length,
                    sha1 = clientSha1
                }
            },
            libraries = Array.Empty<object>()
        });
        var loaderJson = WriteJson(Path.Combine(versions, loaderProfile, loaderProfile + ".json"), new
        {
            id = loaderProfile,
            inheritsFrom = minecraft,
            jar = loaderProfile.StartsWith("fabric-loader-", StringComparison.Ordinal) ? minecraft : null,
            type = "release",
            mainClass = "fixture.Loader",
            libraries = Array.Empty<object>()
        });
        return new ProfileFixture(client, [client, assetIndex, baseJson, loaderJson]);
    }

    private string CreateJava(PackRuntimeDescriptor descriptor)
    {
        var pin = JavaRuntimeCatalog.RequiredFor(descriptor);
        var home = Path.Combine(JavaRuntimeStorage.InstallParent(_paths.JavaRuntimes), pin.InstallDirectoryName);
        var javaw = WriteFile(Path.Combine(home, "bin", "javaw.exe"), "fixture");
        WriteFile(Path.Combine(home, "bin", "java.exe"), "fixture");
        WriteFile(Path.Combine(home, "lib", "modules"), "fixture");
        WriteFile(Path.Combine(home, "release"), $"JAVA_VERSION=\"{pin.JavaVersion}\"\n");
        File.WriteAllText(Path.Combine(home, ".portable-java.json"), JsonSerializer.Serialize(
            new PortableJavaRuntimeService.JavaRuntimeMarker
            {
                SchemaVersion = PortableJavaRuntimeService.MarkerCacheGeneration,
                RuntimeId = pin.RuntimeId,
                ArchiveSha256 = pin.ArchiveSha256,
                JavaVersion = pin.JavaVersion
            }));
        return javaw;
    }

    private void CreateLegacyState(string pack, PackRuntimeDescriptor descriptor, string id, ProfileFixture profile) =>
        WriteJson(StatePath(pack), new
        {
            schemaVersion = 6,
            descriptorHash = descriptor.DescriptorHash,
            profileId = id,
            clientJarRelativePath = RelativePath(profile.Client),
            files = Proof(profile.Files)
        });

    private Dictionary<string, object> Proof(IEnumerable<string> files) => files.ToDictionary(RelativePath, path =>
    {
        var file = new FileInfo(path);
        return (object)new
        {
            sizeBytes = file.Length,
            lastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
        };
    });

    private string RelativePath(string file) =>
        Path.GetRelativePath(SharedRuntimeStore.Anchor(_paths), file).Replace('\\', '/');
    private string StatePath(string pack) => Path.Combine(_paths.CombineUnderRuntimes(pack), RuntimeStateName);
    private string ReceiptPath(string profile) => Path.Combine(SharedRuntimeStore.Versions(_paths), profile, LoaderReceiptName);

    private static string WriteJson(string path, object value) => WriteFile(path, JsonSerializer.Serialize(value, JsonOptions));

    private static string WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        _handler.Dispose();
        TempTree.Delete(_paths.Root);
    }

    private sealed record ProfileFixture(string Client, string[] Files);

    private sealed class OfflineHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            throw new InvalidOperationException($"Fixture network access blocked for {request.RequestUri}");
        }
    }
}
