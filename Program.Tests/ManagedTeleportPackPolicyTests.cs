using System.Security.Cryptography;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ManagedTeleportPackPolicyTests
{
    [Theory]
    [InlineData("Infinity")]
    [InlineData("infinity")]
    [InlineData(@"\Infinity\")]
    public void InfinityProfile_OptsIntoManagedGameplayLayer(string value)
    {
        Assert.True(ManagedTeleportPackPolicy.IsEnabledFor(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AnotherPack")]
    [InlineData("Packs/Infinity")]
    [InlineData("Infinity/Experimental")]
    public void OtherProfiles_RemainUntouched(string value)
    {
        Assert.False(ManagedTeleportPackPolicy.IsEnabledFor(value));
    }

    [Fact]
    public void ArtifactValidation_RequiresPinnedSizeAndHash()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MinecraftManagedTeleportPolicyTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            var bytes = "compatible artifact"u8.ToArray();
            var path = Path.Combine(root, "mods", "component.jar");
            File.WriteAllBytes(path, bytes);
            var artifact = new ManagedTeleportPackArtifact(
                "mods/component.jar",
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

            ManagedTeleportPackPolicy.ValidateArtifact(root, artifact);

            File.AppendAllText(path, "changed");
            Assert.Throws<NotSupportedException>(
                () => ManagedTeleportPackPolicy.ValidateArtifact(root, artifact));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnsupportedRuntime_IsRejectedBeforeComponentInspection()
    {
        var descriptor = new PackRuntimeDescriptor(
            1,
            "1.21.1",
            new PackLoaderDescriptor(PackLoaderKind.NeoForge, "21.1.999"),
            "client.jar",
            "descriptor");
        var instance = new PackInstanceContext(
            "pack",
            Path.GetTempPath(),
            "client.jar");

        Assert.Throws<NotSupportedException>(
            () => ManagedTeleportPackPolicy.Validate(descriptor, instance));
    }
}
