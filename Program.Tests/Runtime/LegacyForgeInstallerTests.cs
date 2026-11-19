using System.IO.Compression;
using Minecraft;

namespace Minecraft.Tests;

public sealed class LegacyForgeInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"legacy-forge-installer-{Guid.NewGuid():N}");

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(true, """{"install":{},"versionInfo":{},"optionals":[]}""", true)]
    [InlineData(false, """{"install":{},"versionInfo":{},"optionals":[]}""", false)]
    [InlineData(true, """{"spec":0,"install":{},"versionInfo":{}}""", false)]
    [InlineData(false, """{"spec":0,"json":"/version.json","processors":[]}""", false)]
    public void InstallerUsesTheClientActionOnlyForTheLegacyProfile(bool clientAction, string profile, bool expected)
    {
        Directory.CreateDirectory(_root);
        var installer = Path.Combine(_root, "forge-installer.jar");
        using (var archive = ZipFile.Open(installer, ZipArchiveMode.Create))
        {
            if (clientAction) archive.CreateEntry("net/minecraftforge/installer/InstallerAction.class");
            using var writer = new StreamWriter(archive.CreateEntry("install_profile.json").Open());
            writer.Write(profile);
        }

        Assert.Equal(expected, OfficialLoaderInstaller.UsesLegacyForgeClientAction(installer));
    }
}
