using System.IO;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class WindowTitleSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "window-title-settings-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Minecraft 26.2")]
    [InlineData("Caves and Dungeons")]
    [InlineData("All The Mods 10")]
    [InlineData("All The Mods 9")]
    [InlineData("All the Mods 9 Modified")]
    [InlineData("LL8 Extended")]
    [InlineData("TerraFirma Rebirth")]
    [InlineData("Unknown future pack")]
    [InlineData("Пещеры и подземелья")]
    public void EveryProfileAutomaticallyUsesOnlyItsFolderName(string pack)
    {
        var settings = Load(Runtime(), pack);
        Assert.True(settings.Enabled);
        Assert.Equal(pack, settings.Text);
    }

    [Fact]
    public void CompetingAuthorProviders_DoNotDisableTheGlobalFinalTitle()
    {
        var gameDirectory = GameDirectory("All The Mods 10");
        var kube = Path.Combine(gameDirectory, "kubejs", "config", "client.json");
        var fancy = Path.Combine(gameDirectory, "config", "fancymenu", "options.txt");
        Write(kube, "{\"window_title\":\"All The Mods 10\"}");
        Write(fancy, "S:custom_window_title = 'Different author title';");
        var kubeBefore = File.ReadAllBytes(kube);
        var fancyBefore = File.ReadAllBytes(fancy);
        var kubeModified = File.GetLastWriteTimeUtc(kube);
        var fancyModified = File.GetLastWriteTimeUtc(fancy);
        var settings = Load(Runtime(), "All The Mods 10");
        Assert.True(settings.Enabled);
        Assert.Equal("All The Mods 10", settings.Text);
        Assert.Equal(kubeBefore, File.ReadAllBytes(kube));
        Assert.Equal(fancyBefore, File.ReadAllBytes(fancy));
        Assert.Equal(kubeModified, File.GetLastWriteTimeUtc(kube));
        Assert.Equal(fancyModified, File.GetLastWriteTimeUtc(fancy));
    }

    [Fact]
    public void AuthorConfigsAndUnpublishedTemplates_DoNotAffectPolicyOrFingerprint()
    {
        var before = Load(Runtime());
        Write(Path.Combine(_root, "Pack", "launcher", "window-title.txt"), "{networkStatus}\0\n{loaderName}");
        var provider = Path.Combine(GameDirectory(), "kubejs", "config", "client.json");
        Write(provider, "broken JSON\0");
        Assert.Equal(before, Load(Runtime()));
        File.Delete(provider);
        Assert.Equal(before, Load(Runtime()));
    }

    [Fact]
    public void RuntimeVersionLoaderAndProfileMetadata_DoNotAddExtraTitleText()
    {
        var first = Load(Runtime());
        var another = Runtime("99.1", PackLoaderKind.Quilt, "9.9") with
            { ProfileId = "unrelated-profile", ClientJarPath = "another-client.jar" };
        Assert.Equal(first, Load(another));
    }

    [Fact]
    public void FingerprintBindsTheGlobalPolicyAndPackName()
    {
        var first = Load(Runtime());
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            WindowTitleSettings.PolicyVersion + "\0Minecraft 26.2"))).ToLowerInvariant();
        Assert.Equal(expected, first.ContentSha256);
        var renamed = Load(Runtime(), "Renamed pack");
        Assert.Equal("Renamed pack", renamed.Text);
        Assert.NotEqual(first.ContentSha256, renamed.ContentSha256);
    }

    [Fact]
    public void InstanceNameUsesTheExistingSanitizersTrimming()
    {
        var settings = Load(Runtime(), " Trimmed pack ");
        Assert.Equal("Trimmed pack", settings.Text);
        Assert.Equal(Load(Runtime(), "Trimmed pack").ContentSha256, settings.ContentSha256);
    }

    private PreparedRuntime Runtime(string version = "26.2", PackLoaderKind loader = PackLoaderKind.NeoForge, string loaderVersion = "26.2.0.84") => new(
        _root, "profile", "java.exe", "client.jar",
        new PackRuntimeDescriptor(1, version, new PackLoaderDescriptor(loader, loaderVersion), "client.jar", "descriptor"));
    private string GameDirectory(string pack = "Minecraft 26.2") => Path.Combine(_root, "Instances", pack);
    private WindowTitleSettings Load(PreparedRuntime runtime, string pack = "Minecraft 26.2") =>
        WindowTitleSettings.Load(Path.Combine(_root, "Pack"), runtime, GameDirectory(pack));
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
