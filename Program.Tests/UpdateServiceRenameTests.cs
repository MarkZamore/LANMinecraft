using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Pins the LANMinecraft release identity: the four update-chain names and the
/// manifest gate that keeps a renamed launcher from ever installing an asset
/// published under the pre-rename name.
/// </summary>
public sealed class UpdateServiceRenameTests
{
    [Fact]
    public void UpdateChainConstants_UseLanMinecraftNames()
    {
        Assert.Equal("LANMinecraft.exe", UpdateService.ExecutableAssetName);
        Assert.Equal("LANMinecraft.bsdiff", UpdateService.DeltaPatchAssetName);
        Assert.Equal("LANMinecraft.exe.candidate", UpdateService.InstallCandidateFileName);
        Assert.Equal("LANMinecraft.exe.bak", UpdateService.InstallBackupFileName);
        Assert.Equal("Minecraft", UpdateService.RepositoryName);
    }

    [Fact]
    public void ValidateManifest_RejectsPreRenameAssetName()
    {
        var manifest = ValidManifest();
        manifest.AssetName = "Minecraft.exe";

        var exception = Assert.Throws<InvalidOperationException>(
            () => UpdateService.ValidateManifest(manifest));
        Assert.Contains("unexpected asset name", exception.Message);
    }

    [Fact]
    public void ValidateManifest_RejectsPreRenameDeltaAssetName()
    {
        var manifest = ValidManifest();
        manifest.DeltaPatch = new DeltaPatchManifest
        {
            Algorithm = UpdateService.DeltaPatchAlgorithm,
            AlgorithmVersion = UpdateService.DeltaPatchAlgorithmVersion,
            BaseReleaseNumber = 36,
            BaseCommitSha = new string('b', 40),
            BaseSha256 = new string('c', 64),
            AssetName = "Minecraft.bsdiff",
            Sha256 = new string('d', 64),
            SizeBytes = 1024
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => UpdateService.ValidateManifest(manifest));
        Assert.Contains("unexpected delta asset name", exception.Message);
    }

    [Fact]
    public void ValidateManifest_AcceptsRenamedAssets()
    {
        var manifest = ValidManifest();
        manifest.DeltaPatch = new DeltaPatchManifest
        {
            Algorithm = UpdateService.DeltaPatchAlgorithm,
            AlgorithmVersion = UpdateService.DeltaPatchAlgorithmVersion,
            BaseReleaseNumber = 36,
            BaseCommitSha = new string('b', 40),
            BaseSha256 = new string('c', 64),
            AssetName = UpdateService.DeltaPatchAssetName,
            Sha256 = new string('d', 64),
            SizeBytes = 1024
        };
        manifest.DeltaPatches = [manifest.DeltaPatch];

        UpdateService.ValidateManifest(manifest);
    }

    private static UpdateManifest ValidManifest() => new()
    {
        SchemaVersion = UpdateService.ManifestSchemaVersion,
        CommitSha = new string('a', 40),
        ReleaseNumber = 37,
        Version = "37",
        PublishedAtUtc = DateTimeOffset.UtcNow,
        AssetName = UpdateService.ExecutableAssetName,
        Sha256 = new string('e', 64),
        SizeBytes = 77_000_000
    };
}
