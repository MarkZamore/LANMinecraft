using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class LocalAiRecipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanmc-ai-recipe-" + Guid.NewGuid().ToString("N"));
    private const string RecipeFile = "launcher/provider-recipe.json";
    private const string ModFile = "mods/assistant-author.jar";
    private const string ModId = "assistant_fixture";
    private const string Model = "team/mini:reviewed";
    private const string NativeRoot = "AppData/Local/AssistantFixture/provider";
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() => TempTree.Delete(_root);
    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
    }
    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
        return output.ToArray();
    }
    private Fixture Setup(string? metadata = null)
    {
        var paths = new AppPaths(_root); paths.Ensure();
        var pack = paths.CombineUnderPacks("RecipePack"); var instance = paths.CombineUnderInstances("RecipePack");
        var home = Path.Combine(paths.Personal, "Home");
        var jar = Zip(("META-INF/mods.toml", Encoding.UTF8.GetBytes(metadata ?? "[[mods]]\nmodId = 'assistant_fixture'\n")));
        Write(pack, ModFile, jar); Write(instance, ModFile, jar);
        var archive = Zip(("ollama.exe", "synthetic provider executable"u8.ToArray()), ("lib/provider.bin", [7, 8, 9]));
        byte[] config = [1, 2], weights = [3, 4, 5, 6];
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2, config = new { digest = "sha256:" + Sha(config), size = config.Length },
            layers = new[] { new { digest = "sha256:" + Sha(weights), size = weights.Length } }
        });
        var recipe = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, provider = "ollama",
            runtime = new { version = "2.3.4", archiveUrl = "https://downloads.fixture.test/provider.zip", sizeBytes = archive.Length, sha256 = Sha(archive), installDirectory = NativeRoot },
            model = new { reference = Model, registryUrl = "https://models.fixture.test/", manifestSha256 = Sha(manifest), manifestBase64 = Convert.ToBase64String(manifest) }
        })!.AsObject();
        var descriptor = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, provider = "ollama", model = Model, modId = ModId, modPath = ModFile,
            modSha256 = Sha(jar), reserveMemoryMb = 2048, language = "en-US", recipeFile = RecipeFile, recipeSha256 = ""
        })!.AsObject();
        var fixture = new Fixture(paths, pack, instance, home, recipe, descriptor, manifest,
            new Dictionary<string, byte[]>
            {
                ["https://downloads.fixture.test/provider.zip"] = archive,
                ["https://models.fixture.test/v2/team/mini/blobs/sha256:" + Sha(config)] = config,
                ["https://models.fixture.test/v2/team/mini/blobs/sha256:" + Sha(weights)] = weights
            });
        fixture.SaveRecipe(); return fixture;
    }
    private sealed record Fixture(AppPaths Paths, string Pack, string Instance, string Home, JsonObject Recipe, JsonObject Descriptor,
        byte[] Manifest, Dictionary<string, byte[]> Files)
    {
        internal byte[] RecipeBytes => Encoding.UTF8.GetBytes(Recipe.ToJsonString());
        internal void SaveDescriptor() => Write(Pack, "launcher/local-ai.json", Encoding.UTF8.GetBytes(Descriptor.ToJsonString()));
        internal void SaveRecipe()
        {
            Write(Pack, RecipeFile, RecipeBytes); Descriptor["recipeSha256"] = Sha(RecipeBytes); SaveDescriptor();
        }
        internal LocalAiRequirements Requirements => LocalAiProvisioningService.ReadRequirements(Pack)!;
    }
    private sealed class Handler(Dictionary<string, byte[]> files, Action<HttpRequestMessage>? before = null) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!); before?.Invoke(request);
            if (!files.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected synthetic download URI.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    [Fact]
    public async Task Utf8BomMetadataKeepsLegacyParsingAndHashesEveryRecipeByte()
    {
        var fixture = Setup();
        var bomRecipe = Encoding.UTF8.GetPreamble().Concat(fixture.RecipeBytes).ToArray();
        Write(fixture.Pack, RecipeFile, bomRecipe);
        fixture.Descriptor["recipeSha256"] = Sha(bomRecipe);
        Write(fixture.Pack, "launcher/local-ai.json", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(fixture.Descriptor.ToJsonString())).ToArray());
        using var handler = new Handler(fixture.Files); using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(fixture.Paths, http);
        Assert.NotNull(await service.PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Equal(3, handler.Requests.Count);
        Write(fixture.Pack, RecipeFile, fixture.RecipeBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("# personal native choices\r\n[AISettings]\r\naiModel='personal'\r\n[VoiceSettings]\r\nuseTTS=false\r\n")]
    [InlineData("# The launcher must not parse or repair this native mod config\n[not valid TOML")]
    public async Task PinnedPackRecipeOwnsGenericDistributionAndPreservesEveryModSetting(string? config)
    {
        var fixture = Setup(); var requirements = fixture.Requirements;
        Assert.Equal(ModId, requirements.ModId); Assert.Equal(Model, requirements.Model); Assert.Equal("en-US", requirements.Language);
        Assert.Equal(RecipeFile, requirements.RecipeFile); Assert.Equal(Sha(fixture.RecipeBytes), requirements.RecipeSha256);
        const string configFile = "config/assistant_fixture-common.toml";
        if (config is not null) Write(fixture.Instance, configFile, Encoding.UTF8.GetBytes(config));
        var path = Path.Combine(fixture.Instance, configFile); var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        using var handler = new Handler(fixture.Files); using var http = new HttpClient(handler);
        var service = new LocalAiProvisioningService(fixture.Paths, http);
        var launch = (await service.PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default))!;
        Assert.Equal(Path.Combine(fixture.Home, NativeRoot.Replace('/', Path.DirectorySeparatorChar), "current", "ollama.exe"), launch.RuntimeExecutable);
        Assert.Equal(fixture.Manifest, File.ReadAllBytes(Path.Combine(launch.ModelsDirectory, "manifests/models.fixture.test/team/mini/reviewed")));
        Assert.Equal(3, handler.Requests.Count);
        var repeated = await service.PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default);
        Assert.NotNull(repeated); Assert.Equal(3, handler.Requests.Count);
        if (config is null) Assert.False(Directory.Exists(Path.Combine(fixture.Instance, "config")));
        else { Assert.Equal(Encoding.UTF8.GetBytes(config), File.ReadAllBytes(path)); Assert.Equal(time, File.GetLastWriteTimeUtc(path)); }
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance, "launcher-runtime")));
    }

    [Fact]
    public async Task LegacyRamMetadataHasNoRecipeFallbackInProductionPreparation()
    {
        var fixture = Setup(); fixture.Descriptor.Remove("recipeFile"); fixture.Descriptor.Remove("recipeSha256"); fixture.SaveDescriptor();
        Assert.Null(fixture.Requirements.RecipeFile); Assert.Equal(2048, fixture.Requirements.ReserveMemoryMb);
        using var handler = new Handler(new Dictionary<string, byte[]>()); using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiProvisioningService(fixture.Paths, http)
            .PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Contains("recipeFile", error.Message, StringComparison.Ordinal); Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Launcher, "LocalAI"))); Assert.False(Directory.Exists(fixture.Home));
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("provider", "\"remote\"")]
    [InlineData("runtime.archiveUrl", "\"http://downloads.fixture.test/runtime.zip\"")]
    [InlineData("runtime.archiveUrl", "\"https://fixture-user:fixture-pass@downloads.fixture.test/runtime.zip\"")]
    [InlineData("runtime.archiveUrl", "\"https://downloads.fixture.test/runtime.zip#fragment\"")]
    [InlineData("runtime.sizeBytes", "0")]
    [InlineData("runtime.sizeBytes", "\"100\"")]
    [InlineData("runtime.sha256", "\"wrong\"")]
    [InlineData("runtime.installDirectory", "\"../outside\"")]
    [InlineData("runtime.installDirectory", "\"/absolute\"")]
    [InlineData("runtime.installDirectory", "\"AppData/CON\"")]
    [InlineData("model.reference", "\"team/different:reviewed\"")]
    [InlineData("model.registryUrl", "\"https://models.fixture.test/path\"")]
    [InlineData("model.registryUrl", "\"https://models.fixture.test/?query=x\"")]
    [InlineData("model.registryUrl", "\"https://models.fixture.test:444/\"")]
    [InlineData("model.registryUrl", "\"http://models.fixture.test/\"")]
    [InlineData("model.registryUrl", "\"https://fixture-user@models.fixture.test/\"")]
    [InlineData("model.manifestSha256", "\"0000000000000000000000000000000000000000000000000000000000000000\"")]
    [InlineData("model.manifestBase64", "\"not Base64\"")]
    public void UnsafeRecipeDataIsRejectedEvenAfterItsOuterHashIsUpdated(string field, string value)
    {
        var fixture = Setup(); var parts = field.Split('.'); JsonNode parent = fixture.Recipe;
        foreach (var part in parts[..^1]) parent = parent[part]!;
        parent[parts[^1]] = JsonNode.Parse(value); fixture.SaveRecipe();
        Assert.Throws<InvalidDataException>(() => LocalAiRecipeReader.Read(fixture.Pack, fixture.Requirements));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("runtime")]
    [InlineData("model")]
    [InlineData("unknown")]
    public void DuplicateOrUnknownRecipeFieldsCannotHideBehindAValidSha(string where)
    {
        var fixture = Setup(); var json = fixture.Recipe.ToJsonString();
        json = where switch
        {
            "root" => json.Insert(1, "\"schemaVersion\":1,"),
            "runtime" => json.Replace("\"runtime\":{", "\"runtime\":{\"version\":\"duplicate\",", StringComparison.Ordinal),
            "model" => json.Replace("\"model\":{", "\"model\":{\"reference\":\"duplicate:tag\",", StringComparison.Ordinal),
            _ => json.Replace("\"runtime\":{", "\"runtime\":{\"unknown\":true,", StringComparison.Ordinal)
        };
        var bytes = Encoding.UTF8.GetBytes(json); Write(fixture.Pack, RecipeFile, bytes);
        fixture.Descriptor["recipeSha256"] = Sha(bytes); fixture.SaveDescriptor();
        Assert.Throws<InvalidDataException>(() => LocalAiRecipeReader.Read(fixture.Pack, fixture.Requirements));
    }

    [Theory]
    [InlineData("recipeFile", "\"../outside.json\"")]
    [InlineData("recipeFile", "\"config/provider.json\"")]
    [InlineData("recipeFile", "\"launcher/../outside.json\"")]
    [InlineData("recipeFile", "\"launcher/provider.jar\"")]
    [InlineData("recipeSha256", "\"invalid\"")]
    public void DescriptorRecipeBindingCannotEscapeLauncherOrLoseItsHash(string field, string value)
    {
        var fixture = Setup(); fixture.Descriptor[field] = JsonNode.Parse(value); fixture.SaveDescriptor();
        Assert.Throws<InvalidDataException>(() => LocalAiProvisioningService.ReadRequirements(fixture.Pack));
    }

    [Fact]
    public void RecipeRequiresBothBindingFieldsAndHasABoundedByteSize()
    {
        var fixture = Setup(); fixture.Descriptor.Remove("recipeSha256"); fixture.SaveDescriptor();
        Assert.Throws<InvalidDataException>(() => LocalAiProvisioningService.ReadRequirements(fixture.Pack));
        var bytes = new byte[LocalAiRecipeReader.MaximumRecipeBytes + 1]; Write(fixture.Pack, RecipeFile, bytes);
        fixture.Descriptor["recipeSha256"] = Sha(bytes); fixture.SaveDescriptor();
        Assert.Throws<InvalidDataException>(() => LocalAiRecipeReader.Read(fixture.Pack, fixture.Requirements));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("layer-type")]
    [InlineData("duplicate-blob")]
    [InlineData("upper-blob")]
    [InlineData("oversized")]
    public void PinnedModelManifestHasItsOwnSchemaDuplicateAndByteGuards(string variant)
    {
        var fixture = Setup(); var data = JsonNode.Parse(fixture.Manifest)!;
        byte[] bytes;
        if (variant == "oversized") bytes = new byte[LocalAiRecipeReader.MaximumManifestBytes + 1];
        else
        {
            if (variant == "layer-type") data["layers"]![0]!["size"] = "4";
            if (variant == "duplicate-blob") data["layers"]![0]!["digest"] = data["config"]!["digest"]!.GetValue<string>();
            if (variant == "upper-blob") data["layers"]![0]!["digest"] = "sha256:" + data["layers"]![0]!["digest"]!.GetValue<string>()[7..].ToUpperInvariant();
            var text = data.ToJsonString(); if (variant == "duplicate") text = text.Insert(1, "\"schemaVersion\":2,");
            bytes = Encoding.UTF8.GetBytes(text);
        }
        fixture.Recipe["model"]!["manifestBase64"] = Convert.ToBase64String(bytes);
        fixture.Recipe["model"]!["manifestSha256"] = Sha(bytes); fixture.SaveRecipe();
        Assert.Throws<InvalidDataException>(() => LocalAiRecipeReader.Read(fixture.Pack, fixture.Requirements));
    }

    [Fact]
    public void ExplicitRegistryReferenceMustMatchThePinnedOrigin()
    {
        var fixture = Setup(); const string full = "models.fixture.test/team/mini:reviewed";
        fixture.Descriptor["model"] = full; fixture.Recipe["model"]!["reference"] = full; fixture.SaveRecipe();
        var valid = LocalAiRecipeReader.Read(fixture.Pack, fixture.Requirements);
        Assert.Equal("team/mini", valid.Model.Repository);
        fixture.Recipe["model"]!["registryUrl"] = "https://another.fixture.test/"; fixture.SaveRecipe();
        Assert.Throws<InvalidDataException>(() => LocalAiRecipeReader.Read(fixture.Pack, fixture.Requirements));
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("description")]
    [InlineData("comment")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-entry")]
    public async Task ExactHashDoesNotTurnMetadataDecoysIntoTheDeclaredMod(string decoy)
    {
        var text = decoy switch
        {
            "dependency" => "[[mods]]\nmodId='other_fixture'\n[[dependencies.other_fixture]]\nmodId='assistant_fixture'\nmandatory=true\n",
            "description" => "[[mods]]\nmodId='other_fixture'\ndescription='''\n[[mods]]\nmodId='assistant_fixture'\n'''\n",
            "comment" => "[[mods]]\nmodId='other_fixture'\n# modId='assistant_fixture'\n",
            _ => "[[mods]]\nmodId='assistant_fixture'\nmodId='assistant_fixture'\n"
        };
        var fixture = Setup(text);
        if (decoy == "duplicate-entry")
        {
            var payload = Encoding.UTF8.GetBytes("[[mods]]\nmodId='assistant_fixture'\n");
            var jar = Zip(("META-INF/mods.toml", payload), ("META-INF/mods.toml", payload));
            Write(fixture.Pack, ModFile, jar); Write(fixture.Instance, ModFile, jar);
            fixture.Descriptor["modSha256"] = Sha(jar); fixture.SaveDescriptor();
        }
        using var handler = new Handler(new Dictionary<string, byte[]>()); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiProvisioningService(fixture.Paths, http)
            .PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Empty(handler.Requests); Assert.False(Directory.Exists(Path.Combine(fixture.Paths.Launcher, "LocalAI")));
    }

    [Fact]
    public async Task GenuineAdditionalModDeclarationsAndQuotedDescriptionsAreAccepted()
    {
        var fixture = Setup("description=\"\"\"not a table: [[mods]]\nmodId='decoy_fixture'\n\"\"\"\n[[mods]]\nmodId='other_fixture'\n[[mods]]\nmodId='assistant_fixture'\n");
        await LocalAiProvisioningService.VerifyModAsync(fixture.Pack, fixture.Requirements, default);
        Assert.Equal(ModId, fixture.Requirements.ModId);
    }

    [Fact]
    public async Task ChangedRecipeDuringDownloadCannotReturnLaunchAndVerifiedCacheRemainsReusable()
    {
        var fixture = Setup(); var original = fixture.RecipeBytes; var changed = false;
        using var handler = new Handler(fixture.Files, _ =>
        {
            if (changed) return; changed = true;
            Write(fixture.Pack, RecipeFile, [.. original, (byte)' ']);
        });
        using var http = new HttpClient(handler); var service = new LocalAiProvisioningService(fixture.Paths, http);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Contains("recipe", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance, "config"))); Assert.False(Directory.Exists(Path.Combine(fixture.Instance, "launcher-runtime")));
        var requests = handler.Requests.Count; Write(fixture.Pack, RecipeFile, original);
        Assert.NotNull(await service.PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Equal(requests, handler.Requests.Count);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sha")]
    public async Task ChangedDescriptorRecipeBindingCannotFinishEvenWhenTheNewRecipeIsValid(string changedField)
    {
        var fixture = Setup(); var changed = false;
        using var handler = new Handler(fixture.Files, _ =>
        {
            if (changed) return; changed = true;
            if (changedField == "file") { Write(fixture.Pack, "launcher/other-recipe.json", fixture.RecipeBytes); fixture.Descriptor["recipeFile"] = "launcher/other-recipe.json"; fixture.SaveDescriptor(); }
            else { fixture.Recipe["runtime"]!["version"] = "2.3.5"; fixture.SaveRecipe(); }
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiProvisioningService(fixture.Paths, http)
            .PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
        Assert.Contains("descriptor changed during preparation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Instance, "config"))); Assert.False(Directory.Exists(Path.Combine(fixture.Instance, "launcher-runtime")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecipeAndNativeInstallAncestorsCannotRedirectFilesThroughJunctions(bool install)
    {
        var fixture = Setup(); var external = Path.Combine(_root, "external-fixture"); Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "personal-sentinel.bin"); File.WriteAllBytes(sentinel, [9, 8, 7]);
        string junction;
        if (install)
        {
            var parent = Path.Combine(fixture.Home, "AppData", "Local"); Directory.CreateDirectory(parent);
            junction = Path.Combine(parent, "AssistantFixture");
        }
        else
        {
            junction = Path.Combine(fixture.Pack, "launcher", "recipes");
            Write(external, "provider.json", fixture.RecipeBytes); fixture.Descriptor["recipeFile"] = "launcher/recipes/provider.json"; fixture.SaveDescriptor();
        }
        SavesFolderService.CreateJunction(junction, external);
        try
        {
            using var handler = new Handler(new Dictionary<string, byte[]>()); using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() => new LocalAiProvisioningService(fixture.Paths, http)
                .PrepareAsync(fixture.Pack, fixture.Instance, fixture.Home, null, default));
            Assert.Empty(handler.Requests); Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(sentinel));
            Assert.False(Directory.Exists(Path.Combine(external, "provider", "current")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void RecipeCapabilityIsAcceptedAsAnExplicitManifestRequirement()
    {
        var sha = Sha([1]);
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 2, requiredLauncherFeatures = new[] { "local-ai-recipe-v1" }, revision = "recipe-capability", minecraftVersion = "1.20.1",
            files = new[] { new { path = "mods/author.jar", sizeBytes = 1, sha256 = sha } },
            assets = new[] { new { name = "author.jar", kind = "jar", sizeBytes = 1, sha256 = sha, covers = new[] { "mods/author.jar" } } }
        });
        PortablePackSyncService.ValidateSnapshot(manifest, Path.Combine(_root, "capability"));
    }
}
