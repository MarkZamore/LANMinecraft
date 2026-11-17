using System.Text.Json;
using System.Text.Json.Nodes;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The names and shapes the update chain runs on. A launcher downloads and
/// executes what update.json names, so the guard that it names the launcher's
/// own executable - and nothing else - is the one that keeps a wrong or
/// tampered manifest from installing a stranger's file.
/// </summary>
public sealed class UpdateChainIdentityTests
{
    [Fact]
    public void UpdateChainConstants_NameTheLauncherItInstalls()
    {
        Assert.Equal("LANMinecraft.exe", UpdateService.ExecutableAssetName);
        Assert.Equal("LANMinecraft.exe.candidate", UpdateService.InstallCandidateFileName);
        Assert.Equal("LANMinecraft.exe.bak", UpdateService.InstallBackupFileName);
        Assert.Equal("LANMinecraft", UpdateService.RepositoryName);
    }

    [Fact]
    public void ValidateManifest_RejectsAnAssetThatIsNotTheLauncher()
    {
        var manifest = ValidManifest();
        manifest.AssetName = "something-else.exe";

        var exception = Assert.Throws<InvalidOperationException>(
            () => UpdateService.ValidateManifest(manifest));
        Assert.Contains("unexpected asset name", exception.Message);
    }

    [Fact]
    public void DeserializeManifest_IgnoresLegacyDeltaMetadata()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(ValidManifest(), options)!;
        json["deltaPatches"] = new JsonArray
        {
            new JsonObject { ["assetName"] = "obsolete.bsdiff", ["algorithm"] = "obsolete" }
        };
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json.ToJsonString(), options)!;

        UpdateService.ValidateManifest(manifest);
        Assert.Equal(UpdateService.ExecutableAssetName, manifest.AssetName);
        Assert.Equal(new string('e', 64), manifest.Sha256);
        Assert.False(JsonSerializer.SerializeToNode(manifest, options)!.AsObject().ContainsKey("deltaPatches"));
    }

    [Fact]
    public void ValidateManifest_AcceptsTheShapeTheBuildPublishes()
    {
        var manifest = ValidManifest();
        UpdateService.ValidateManifest(manifest);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(manifest, options)!;
        Assert.False(json.AsObject().ContainsKey("deltaPatches"));
        // The published v2 contract already defaults an omitted list to empty.
        var legacy = JsonSerializer.Deserialize<LegacyV2Manifest>(json.ToJsonString(), options)!;
        Assert.Equal(UpdateService.ManifestSchemaVersion, legacy.SchemaVersion);
        Assert.Equal(manifest.AssetName, legacy.AssetName);
        Assert.Empty(legacy.DeltaPatches);
    }

    private sealed class LegacyV2Manifest
    {
        public LegacyV2Manifest() { }
        public int SchemaVersion { get; set; }
        public string AssetName { get; set; } = "";
        public List<JsonElement> DeltaPatches { get; set; } = [];
    }

    private static UpdateManifest ValidManifest() => new()
    {
        SchemaVersion = UpdateService.ManifestSchemaVersion,
        CommitSha = new string('a', 40),
        ReleaseNumber = 42,
        Version = "42",
        PublishedAtUtc = DateTimeOffset.UtcNow,
        AssetName = UpdateService.ExecutableAssetName,
        Sha256 = new string('e', 64),
        SizeBytes = 77_000_000
    };
}
