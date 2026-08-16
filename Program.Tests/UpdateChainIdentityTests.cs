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
        Assert.Equal("LANMinecraft.bsdiff", UpdateService.DeltaPatchAssetName);
        Assert.Equal("LANMinecraft.exe.candidate", UpdateService.InstallCandidateFileName);
        Assert.Equal("LANMinecraft.exe.bak", UpdateService.InstallBackupFileName);
        Assert.Equal("Minecraft", UpdateService.RepositoryName);
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
    public void ValidateManifest_RejectsADeltaThatPatchesSomethingElse()
    {
        var manifest = ValidManifest();
        manifest.DeltaPatches = [NewDelta("something-else.bsdiff")];

        var exception = Assert.Throws<InvalidOperationException>(
            () => UpdateService.ValidateManifest(manifest));
        Assert.Contains("unexpected delta asset name", exception.Message);
    }

    [Fact]
    public void ValidateManifest_AcceptsTheShapeTheBuildPublishes()
    {
        var manifest = ValidManifest();
        manifest.DeltaPatches =
        [
            NewDelta(UpdateService.DeltaPatchAssetName),
            NewDelta("LANMinecraft.from-40.bsdiff", baseSha: new string('f', 64))
        ];

        UpdateService.ValidateManifest(manifest);
    }

    private static DeltaPatchManifest NewDelta(string assetName, string? baseSha = null) => new()
    {
        Algorithm = UpdateService.DeltaPatchAlgorithm,
        AlgorithmVersion = UpdateService.DeltaPatchAlgorithmVersion,
        BaseReleaseNumber = 41,
        BaseCommitSha = new string('b', 40),
        BaseSha256 = baseSha ?? new string('c', 64),
        AssetName = assetName,
        Sha256 = new string('d', 64),
        SizeBytes = 1024
    };

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
