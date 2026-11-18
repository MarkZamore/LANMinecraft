using System.IO.Compression;

namespace Minecraft.Tests;

public sealed class LauncherAssetsTests
{
    [Fact]
    public void VerifiedPackageSuppliesOfflinePreviewsAndSvgGeometry()
    {
        var seeds = PackCatalogService.SeedSources();
        Assert.NotEmpty(seeds);
        foreach (var seed in seeds)
        {
            using var archive = new ZipArchive(Assert.IsAssignableFrom<Stream>(LauncherAssets.OpenLibrarySeed(seed.StorageId)));
            Assert.True(archive.GetEntry("library.json")?.Length > 0);
            Assert.True(archive.GetEntry("preview.jpg")?.Length > 0);
            Assert.DoesNotContain($"Minecraft.Library.{seed.StorageId}.zip", typeof(LauncherAssets).Assembly.GetManifestResourceNames());
        }

        foreach (var id in new[] { "back", "chevron", "requirements-dot", "trash" })
        {
            var geometry = LauncherAssets.Icon(id);
            Assert.True(geometry.IsFrozen);
            Assert.True(geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0);
        }
    }
}
