using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Rules;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class LegacyFabricRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-fabric-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitJavaIsPinnedAndPartOfTheRuntimeIdentityWhileOldDescriptorsKeepTheirHash()
    {
        var original = Load("fabric", "1.18.2", "0.14.10");
        var canonical = "1\n1.18.2\nfabric\n0.14.10\n";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), original.DescriptorHash);
        Assert.Equal(original.DescriptorHash, Load("fabric", "1.18.2", "0.14.10", null).DescriptorHash);
        var legacy = Load("legacyfabric", "1.6.4", "0.18.2", 17);
        Assert.Equal(PackLoaderKind.LegacyFabric, legacy.Loader.Type);
        Assert.Equal(17, legacy.JavaMajorVersion);
        Assert.Equal(17, JavaRuntimeCatalog.RequiredFor(legacy).MajorVersion);
        Assert.NotEqual(legacy.DescriptorHash, Load("legacyfabric", "1.6.4", "0.18.2").DescriptorHash);
        Assert.NotEqual(legacy.DescriptorHash, Load("legacyfabric", "1.6.4", "0.18.2", 21).DescriptorHash);
        Assert.Equal(8, JavaRuntimeCatalog.RequiredFor(Load("legacyfabric", "1.6.4", "0.18.2")).MajorVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-17)]
    [InlineData(16)]
    [InlineData(99)]
    public void UnpinnedJavaIsRejectedBeforeRuntimePreparation(int major)
    {
        Assert.Throws<InvalidDataException>(() => Load("legacyfabric", "1.6.4", "0.18.2", major));
        var descriptor = new PackRuntimeDescriptor(1, "1.6.4", new(PackLoaderKind.LegacyFabric, "0.18.2"), "", "fixture")
        { JavaMajorVersion = major };
        Assert.Throws<InvalidDataException>(() => JavaRuntimeCatalog.RequiredFor(descriptor));
    }

    [Fact]
    public async Task OfficialProfileUsesItsOwnNamespaceAndRetainsLegacyMappingsAndLwjgl()
    {
        using var handler = new ProfileHandler();
        using var http = new HttpClient(handler);
        var path = new MinecraftPath(Path.Combine(_root, "runtime"));
        path.CreateDirs();
        var ordinaryDirectory = Path.Combine(path.Versions, "fabric-loader-0.18.2-1.6.4");
        Directory.CreateDirectory(ordinaryDirectory);
        var ordinaryProfile = Path.Combine(ordinaryDirectory, "fabric-loader-0.18.2-1.6.4.json");
        File.WriteAllText(ordinaryProfile, "existing ordinary Fabric profile");
        var profile = await LegacyFabricLoaderProvider.InstallProfileAsync(http, path, "1.6.4", "0.18.2",
            new Logger(Path.Combine(_root, "runtime.log")), default);
        Assert.Equal("legacyfabric-loader-0.18.2-1.6.4", profile);
        Assert.Equal("https://meta.legacyfabric.net/v2/versions/loader/1.6.4/0.18.2/profile/json", Assert.Single(handler.Requests).AbsoluteUri);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(path.Versions, profile, profile + ".json")))!;
        Assert.Equal(profile, saved["id"]!.GetValue<string>());
        Assert.Equal("1.6.4", saved["jar"]!.GetValue<string>());
        Assert.Equal("1.6.4", saved["inheritsFrom"]!.GetValue<string>());
        var libraries = saved["libraries"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("net.legacyfabric:intermediary:1.6.4", libraries);
        Assert.Contains("org.lwjgl.lwjgl:lwjgl:2.9.4+legacyfabric.17", libraries);
        Assert.Contains("org.lwjgl.lwjgl:lwjgl-platform:2.9.4+legacyfabric.17", libraries);
        Assert.Equal("natives-windows", saved["libraries"]![2]!["natives"]!["windows"]!.GetValue<string>());
        using var nativeJson = JsonDocument.Parse(saved["libraries"]![2]!.ToJsonString());
        var nativeLibrary = JsonLibraryParser.Parse(nativeJson.RootElement)!;
        var windows = new RulesEvaluatorContext(new LauncherOSRule { Name = "windows", Arch = "64" });
        var nativeFile = Assert.Single(LibraryFileExtractor.Extractor.ExtractTasks(
            "https://libraries.minecraft.net/", path, nativeLibrary, windows));
        const string nativePath = "org/lwjgl/lwjgl/lwjgl-platform/2.9.4+legacyfabric.17/" +
            "lwjgl-platform-2.9.4+legacyfabric.17-natives-windows.jar";
        Assert.Equal("https://maven.legacyfabric.net/" + nativePath, nativeFile.Url);
        Assert.Equal(Path.Combine(path.Library, nativePath.Replace('/', Path.DirectorySeparatorChar)), nativeFile.Path);
        Assert.Contains("META-INF/", nativeLibrary.ExtractExcludes);
        Assert.Equal("existing ordinary Fabric profile", File.ReadAllText(ordinaryProfile));
    }

    [Fact]
    public async Task AProfileForAnotherMinecraftIsRejectedBeforeWritingAProfile()
    {
        using var handler = new ProfileHandler("1.6.5");
        using var http = new HttpClient(handler);
        var path = new MinecraftPath(Path.Combine(_root, "runtime"));
        path.CreateDirs();
        await Assert.ThrowsAsync<InvalidDataException>(() => LegacyFabricLoaderProvider.InstallProfileAsync(http, path,
            "1.6.4", "0.18.2", new Logger(Path.Combine(_root, "runtime.log")), default));
        Assert.Empty(Directory.EnumerateDirectories(path.Versions));
    }

    [Fact]
    public async Task ColdPreparationUsesPinnedGameJavaWithoutRequestingMojangJava()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = paths.CombineUnderPacks("Legacy fixture");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, PackManifestService.ManifestFileName), """
            {"schemaVersion":1,"minecraftVersion":"1.6.4","loader":{"type":"legacyfabric","version":"0.18.2"},
             "clientJar":"client.jar","javaMajorVersion":17}
            """);
        var clientJar = Path.Combine(pack, "client.jar");
        using (var jar = ZipFile.Open(clientJar, ZipArchiveMode.Create))
            jar.CreateEntry("net/minecraft/client/main/Main.class");

        // A prepared portable Java install avoids running a JVM in this test.
        // The Mojang runtime and all Minecraft files start cold.
        var pin = JavaRuntimeCatalog.ForMajorVersion(17)!;
        var javaHome = Path.Combine(paths.JavaRuntimes, "runtime", "windows-x64", pin.InstallDirectoryName);
        Directory.CreateDirectory(Path.Combine(javaHome, "bin"));
        Directory.CreateDirectory(Path.Combine(javaHome, "lib"));
        File.WriteAllText(Path.Combine(javaHome, "bin", "java.exe"), "fixture");
        var javaw = Path.Combine(javaHome, "bin", "javaw.exe");
        File.WriteAllText(javaw, "fixture");
        File.WriteAllText(Path.Combine(javaHome, "lib", "modules"), "fixture");
        File.WriteAllText(Path.Combine(javaHome, "release"), $"JAVA_VERSION=\"{pin.JavaVersion}\"\n");
        File.WriteAllText(Path.Combine(javaHome, ".portable-java.json"),
            JsonSerializer.Serialize(new { SchemaVersion = 1, pin.RuntimeId, pin.ArchiveSha256 }));

        using var handler = new ColdRuntimeHandler(File.ReadAllBytes(clientJar));
        using var http = new HttpClient(handler);
        using var service = new PackRuntimeService(paths, new Logger(paths.LogFile), http);
        var runtime = await service.PrepareAsync("Legacy fixture", null, CancellationToken.None);

        Assert.Equal(javaw, runtime.JavaPath);
        Assert.Equal("legacyfabric-loader-0.18.2-1.6.4", runtime.ProfileId);
        Assert.Equal(File.ReadAllBytes(clientJar), File.ReadAllBytes(runtime.ClientJarPath));
        Assert.Equal("fixture library", File.ReadAllText(Path.Combine(
            SharedRuntimeStore.Libraries(paths), "fixture", "library", "1", "library-1.jar")));
        Assert.False(Directory.Exists(Path.Combine(SharedRuntimeStore.Runtime(paths), "windows-x64", "jre-legacy")));
        var requestsAfterPreparation = handler.RequestCount;

        var cached = await service.PrepareAsync("Legacy fixture", null, CancellationToken.None);
        Assert.Equal(runtime.ProfileId, cached.ProfileId);
        Assert.Equal(runtime.JavaPath, cached.JavaPath);
        Assert.Equal(requestsAfterPreparation, handler.RequestCount);
    }

    [Theory]
    [InlineData(8, "-verbose:gc")]
    [InlineData(17, "-Xlog:gc:stdout:time,uptime")]
    public void LoggingAndCompatibilityOptionsFollowTheSelectedJava(int major, string logging)
    {
        var args = MinecraftProcessService.BuildBaseJvmArguments(_root, _root, _root, "", "", null, major);
        Assert.Equal(logging, Assert.Single(args, arg => arg == "-verbose:gc" || arg.StartsWith("-Xlog:", StringComparison.Ordinal)));
        Assert.Empty(MinecraftProcessService.CompatibilityArgumentsFor(major));
    }

    private PackRuntimeDescriptor Load(string loader, string mc, string version, int? java = null)
    {
        Directory.CreateDirectory(_root);
        var node = new JsonObject
        {
            ["schemaVersion"] = 1, ["minecraftVersion"] = mc,
            ["loader"] = new JsonObject { ["type"] = loader, ["version"] = version }
        };
        if (java.HasValue) node["javaMajorVersion"] = java.Value;
        File.WriteAllText(Path.Combine(_root, PackManifestService.ManifestFileName), node.ToJsonString());
        return PackManifestService.Load(_root);
    }

    public void Dispose() => TempTree.Delete(_root);

    private sealed class ColdRuntimeHandler(byte[] clientJar) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requestCount);
            var response = request.RequestUri!.AbsoluteUri switch
            {
                "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json" => """
                    {"versions":[{"id":"1.6.4","type":"release","url":"https://fixture.test/1.6.4.json"}]}
                    """,
                "https://fixture.test/1.6.4.json" => $$$$"""
                    {"id":"1.6.4","type":"release","mainClass":"net.minecraft.client.main.Main",
                     "downloads":{"client":{"url":"https://fixture.test/client.jar","size":{{{{clientJar.Length}}}},
                       "sha1":"{{{{Convert.ToHexStringLower(SHA1.HashData(clientJar))}}}}"}},
                     "libraries":[{"name":"fixture:library:1","downloads":{"artifact":{"url":"https://fixture.test/library.jar"}}}]}
                    """,
                "https://fixture.test/library.jar" => "fixture library",
                "https://meta.legacyfabric.net/v2/versions/loader/1.6.4/0.18.2/profile/json" => """
                    {"id":"fabric-loader-0.18.2-1.6.4","inheritsFrom":"1.6.4",
                     "mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[]}
                    """,
                // Java metadata or a second client download would fail this test.
                _ => throw new InvalidOperationException($"Unexpected runtime request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8)
            });
        }
    }

    private sealed class ProfileHandler(string inheritsFrom = "1.6.4") : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"fabric-loader-0.18.2-1.6.4","inheritsFrom":"1.6.4","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","libraries":[
                    {"name":"net.legacyfabric:intermediary:1.6.4","url":"https://maven.legacyfabric.net/"},
                    {"name":"org.lwjgl.lwjgl:lwjgl:2.9.4+legacyfabric.17","url":"https://maven.legacyfabric.net/"},
                    {"name":"org.lwjgl.lwjgl:lwjgl-platform:2.9.4+legacyfabric.17","url":"https://maven.legacyfabric.net/","natives":{"windows":"natives-windows"},"extract":{"exclude":["META-INF/"]}}]}
                    """.Replace("\"inheritsFrom\":\"1.6.4\"", "\"inheritsFrom\":\"" + inheritsFrom + "\"", StringComparison.Ordinal), Encoding.UTF8, "application/json")
            });
        }
    }
}
