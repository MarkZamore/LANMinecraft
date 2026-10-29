using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class PackLaunchContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanmc-launch-context-" + Guid.NewGuid().ToString("N"));
    private string Pack => Path.Combine(_root, "pack");
    private string Instance => Path.Combine(_root, "instance");
    private string Output => Path.Combine(Instance, "launcher-runtime", "launch-context.json");
    private const string Descriptor = """{"schemaVersion":1,"requests":["localAi.endpoint"]}""";
    private static Dictionary<string, Uri> Bindings(string endpoint = "http://127.0.0.1:12345") =>
        new(StringComparer.Ordinal) { ["localAi.endpoint"] = new Uri(endpoint) };
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void AbsentDescriptorDoesNotCreateOrChangeFiles()
    {
        PackLaunchContextService.Write(Pack, Instance, Bindings());
        Assert.False(Directory.Exists(_root));
        Write(Instance, "launcher-runtime/launch-context.json", "previous private context");
        var time = File.GetLastWriteTimeUtc(Output);
        PackLaunchContextService.Write(Pack, Instance, Bindings());
        Assert.Equal("previous private context", File.ReadAllText(Output));
        Assert.Equal(time, File.GetLastWriteTimeUtc(Output));
    }

    [Fact]
    public void RequestedBindingsArePrivateToTheInstanceAndReplaceOnlyTheirContext()
    {
        Write(Pack, PackLaunchContextService.DescriptorPath, Descriptor);
        const string personal = "# Personal voice choices\r\n[VoiceSettings]\r\nuseTTS = false\r\n";
        var config = Write(Instance, "config/verity-common.toml", personal);
        var configTime = File.GetLastWriteTimeUtc(config);
        var bindings = Bindings();
        bindings["private.user-input"] = new Uri("https://private-user:private-password@example.test/private?secret=key");
        PackLaunchContextService.Write(Pack, Instance, bindings);
        var bytes = File.ReadAllBytes(Output);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        using (var document = JsonDocument.Parse(bytes))
        {
            Assert.Equal(2, document.RootElement.EnumerateObject().Count());
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            var value = Assert.Single(document.RootElement.GetProperty("values").EnumerateObject());
            Assert.Equal("localAi.endpoint", value.Name);
            Assert.Equal("http://127.0.0.1:12345", value.Value.GetString());
        }
        Assert.DoesNotContain("private", File.ReadAllText(Output), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, File.ReadAllText(Output), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(Pack, "launcher-runtime")));
        var time = File.GetLastWriteTimeUtc(Output);
        PackLaunchContextService.Write(Pack, Instance, bindings);
        Assert.Equal(time, File.GetLastWriteTimeUtc(Output));
        PackLaunchContextService.Write(Pack, Instance, Bindings("http://127.0.0.1:23456"));
        Assert.Contains("http://127.0.0.1:23456", File.ReadAllText(Output));
        Assert.Equal(Encoding.UTF8.GetBytes(personal), File.ReadAllBytes(config));
        Assert.Equal(configTime, File.GetLastWriteTimeUtc(config));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Output)!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":"1","requests":["localAi.endpoint"]}""")]
    [InlineData("""{"schemaVersion":2,"requests":["localAi.endpoint"]}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"requests":["localAi.endpoint"]}""")]
    [InlineData("""{"schemaVersion":1,"requests":["localAi.endpoint"],"requests":["localAi.endpoint"]}""")]
    [InlineData("""{"schemaVersion":1,"requests":null}""")]
    [InlineData("""{"schemaVersion":1,"requests":"localAi.endpoint"}""")]
    [InlineData("""{"schemaVersion":1,"requests":[]}""")]
    [InlineData("""{"schemaVersion":1,"requests":[null]}""")]
    [InlineData("""{"schemaVersion":1,"requests":[1]}""")]
    [InlineData("""{"schemaVersion":1,"requests":["localAi.endpoint","localAi.endpoint"]}""")]
    [InlineData("""{"schemaVersion":1,"requests":["HOME"]}""")]
    [InlineData("""{"schemaVersion":1,"requests":["localAi.endpoint","OLLAMA_API_KEY"]}""")]
    [InlineData("""{"schemaVersion":1,"requests":["localAi.endpoint"],"output":"../outside.json"}""")]
    public void InvalidDescriptorsLeaveExistingContextUntouched(string descriptor)
    {
        Write(Pack, PackLaunchContextService.DescriptorPath, descriptor);
        Write(Instance, PackLaunchContextService.RuntimePath, "previous context");
        var time = File.GetLastWriteTimeUtc(Output);
        Assert.Throws<InvalidDataException>(() => PackLaunchContextService.Write(Pack, Instance, Bindings()));
        Assert.Equal("previous context", File.ReadAllText(Output));
        Assert.Equal(time, File.GetLastWriteTimeUtc(Output));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Output)!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://127.0.0.1:12345")]
    [InlineData("http://example.test:12345")]
    [InlineData("http://user:password@127.0.0.1:12345")]
    [InlineData("http://127.0.0.1:12345/config")]
    [InlineData("http://127.0.0.1:12345?key=private")]
    [InlineData("http://127.0.0.1:12345#private")]
    public void MissingOrUnsafeBindingCannotCreateAnOutput(string? endpoint)
    {
        Write(Pack, PackLaunchContextService.DescriptorPath, Descriptor);
        var bindings = endpoint is null ? new Dictionary<string, Uri>() : Bindings(endpoint);
        Assert.Throws<InvalidDataException>(() => PackLaunchContextService.Write(Pack, Instance, bindings));
        Assert.False(Directory.Exists(Instance));
    }

    [Fact]
    public void OversizedOrMalformedDescriptorCannotCreateAnOutput()
    {
        Write(Pack, PackLaunchContextService.DescriptorPath, new string(' ', 4097));
        Assert.Throws<InvalidDataException>(() => PackLaunchContextService.Write(Pack, Instance, Bindings()));
        Write(Pack, PackLaunchContextService.DescriptorPath, "{malformed");
        Assert.ThrowsAny<JsonException>(() => PackLaunchContextService.Write(Pack, Instance, Bindings()));
        File.Delete(Path.Combine(Pack, "launcher", "launch-context.json"));
        Directory.CreateDirectory(Path.Combine(Pack, "launcher", "launch-context.json"));
        Assert.Throws<InvalidDataException>(() => PackLaunchContextService.Write(Pack, Instance, Bindings()));
        Assert.False(Directory.Exists(Instance));
    }

    [Theory]
    [InlineData("descriptor")]
    [InlineData("output")]
    [InlineData("instance-parent")]
    public void FilesystemLinksCannotRedirectDescriptorOrContext(string location)
    {
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        if (location == "descriptor")
        {
            Directory.CreateDirectory(Pack);
            Write(external, "launch-context.json", Descriptor);
            SavesFolderService.CreateJunction(Path.Combine(Pack, "launcher"), external);
        }
        else
        {
            Write(Pack, PackLaunchContextService.DescriptorPath, Descriptor);
            if (location == "output")
            {
                Directory.CreateDirectory(Instance);
                SavesFolderService.CreateJunction(Path.Combine(Instance, "launcher-runtime"), external);
            }
            else SavesFolderService.CreateJunction(Instance, external);
        }
        Assert.Throws<InvalidDataException>(() => PackLaunchContextService.Write(Pack, Instance, Bindings()));
        Assert.False(File.Exists(Path.Combine(external, "launcher-runtime", "launch-context.json")));
        if (location != "descriptor") Assert.Empty(Directory.GetFileSystemEntries(external));
        else Assert.Equal(Descriptor, File.ReadAllText(Path.Combine(external, "launch-context.json")));
    }

    [Fact]
    public void FailedAtomicReplacementPreservesPriorBytesAndRemovesTemporaryFile()
    {
        Write(Pack, PackLaunchContextService.DescriptorPath, Descriptor);
        Write(Instance, PackLaunchContextService.RuntimePath, "previous context");
        using (File.Open(Output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => PackLaunchContextService.Write(Pack, Instance, Bindings()));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Replacement unexpectedly succeeded.");
        }
        Assert.Equal("previous context", File.ReadAllText(Output));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Output)!));
        PackLaunchContextService.Write(Pack, Instance, Bindings());
        Assert.Contains("http://127.0.0.1:12345", File.ReadAllText(Output));
    }

    [Fact]
    public async Task RuntimeDirectoryIsNeverCopiedOrDeletedByPackSynchronization()
    {
        var paths = new AppPaths(_root); paths.Ensure();
        const string name = "ContextOwnership";
        var pack = paths.CombineUnderPacks(name);
        Write(pack, PackManifestService.ManifestFileName,
            """{"schemaVersion":1,"minecraftVersion":"1.20.1","loader":{"type":"vanilla"},"clientJar":"client.jar"}""");
        Write(pack, "client.jar", "client-fixture");
        Write(pack, "launcher-runtime/launch-context.json", "untrusted published context");
        using var service = new PackInstanceService(paths, new Logger(paths.LogFile));
        var game = (await service.PrepareAsync(name)).GameDirectory;
        var runtime = Path.Combine(game, "launcher-runtime");
        Assert.False(Directory.Exists(runtime));
        var context = Write(game, "launcher-runtime/launch-context.json", "private runtime context");
        var time = File.GetLastWriteTimeUtc(context);
        // Even an old tracking record cannot turn newly instance-owned data
        // into a pack-managed retirement on the next synchronization.
        var statePath = Path.Combine(game, ".portable-instance.json");
        var state = JsonNode.Parse(File.ReadAllText(statePath))!;
        state["files"]!["launcher-runtime/launch-context.json"] = JsonSerializer.SerializeToNode(new
        {
            sizeBytes = new FileInfo(context).Length, lastWriteUtcTicks = time.Ticks,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(context)))
        });
        File.WriteAllText(statePath, state.ToJsonString());
        await service.PrepareAsync(name);
        File.Delete(Path.Combine(pack, "launcher-runtime", "launch-context.json"));
        await service.PrepareAsync(name);
        Assert.Equal("private runtime context", File.ReadAllText(context));
        Assert.Equal(time, File.GetLastWriteTimeUtc(context));
    }

    [Fact]
    public void PublishedManifestsCannotShipPrivateRuntimeContext()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = "context-fixture", minecraftVersion = "1.20.1",
            files = new[] { new { path = "Launcher-Runtime/launch-context.json", sizeBytes = 1, sha256 = new string('a', 64) } },
            assets = new[] { new { name = "runtime.zip", kind = "zipRoot", sizeBytes = 1, sha256 = new string('a', 64),
                covers = new[] { "Launcher-Runtime/launch-context.json" } } }
        });
        Assert.Throws<InvalidDataException>(() => PortablePackSyncService.ValidateSnapshot(manifest, Pack));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ProductionLaunchWritesContextAfterBindingsAndBeforeJavaStarts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Program", "MinecraftProcessService.cs")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "Program", "MinecraftProcessService.cs"));
        var environment = source.IndexOf("localAi?.ConfigureEnvironment(", StringComparison.Ordinal);
        var context = source.IndexOf("PackLaunchContextService.Write(", environment, StringComparison.Ordinal);
        var start = source.IndexOf("minecraftProcess.Start()", environment, StringComparison.Ordinal);
        Assert.True(environment >= 0 && context > environment && start > context);
        Assert.Contains("[PackLaunchContextService.LocalAiEndpoint] = localAi.Endpoint", source[context..start]);
        Assert.DoesNotContain("StartInfo.Environment", source[context..start]);
    }

    private static string Write(string root, string relative, string text)
    {
        var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return file;
    }
}
