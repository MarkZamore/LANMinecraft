using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The embedded GitHub snapshot is a fallback, not a separately maintained
/// build list. A session's verified GitHub or disk index takes precedence.
/// </summary>
public sealed class KnownPackTests
{
    [Fact]
    public void AnAuthoritativeEmptyIndexCannotFallBackToEmbeddedSourcesOrOldMarkers()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "known-catalog-" + Guid.NewGuid().ToString("N")));
        try
        {
            paths.Ensure();
            Directory.CreateDirectory(Path.Combine(paths.Launcher, "Library"));
            File.WriteAllText(Path.Combine(paths.Launcher, "Library", "catalog.json"), """{"schemaVersion":1,"packs":[]}""");
            var pack = PortablePackSyncService.DefaultPackRelativePath;
            var directory = paths.CombineUnderPacks(pack);
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, PortablePackSyncService.SourceMarkerFileName);
            File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1, owner = PortablePackSyncService.DefaultPackSource.Owner,
                repo = "FormerRepository", tag = "pack-latest"
            }));
            var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
            var sync = new PortablePackSyncService(paths, new Logger(paths.LogFile)) { Catalog = catalog };

            Assert.True(catalog.IsWithdrawn(pack));
            Assert.Null(sync.TryResolveSource(pack));
            var before = File.ReadAllBytes(marker);
            sync.EnsureDefaultSourceMarker(pack);
            Assert.Equal(before, File.ReadAllBytes(marker));
            File.Delete(marker);
            Assert.Null(sync.TryResolveSource(pack));
            sync.EnsureDefaultSourceMarker(pack);
            Assert.False(File.Exists(marker));
        }
        finally { TempTree.Delete(paths.Root); }
    }

    [Fact]
    public void ExplicitCustomMarkersAndAReintroducedGitHubPackRemainAvailable()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "known-custom-" + Guid.NewGuid().ToString("N")));
        try
        {
            paths.Ensure();
            const string reintroduced = "LL8 Extended";
            var source = new PackSyncSource("Publisher", "Reintroduced-Pack", "stable");
            Directory.CreateDirectory(Path.Combine(paths.Launcher, "Library"));
            File.WriteAllText(Path.Combine(paths.Launcher, "Library", "catalog.json"),
                System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1,
                    packs = new[] { new CatalogSource("reintroduced-pack", reintroduced, source) } },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            const string custom = "My Custom Pack";
            var customDirectory = paths.CombineUnderPacks(custom);
            Directory.CreateDirectory(customDirectory);
            var customSource = new PackSyncSource("SomeoneElse", "Custom-Pack", "nightly");
            File.WriteAllText(Path.Combine(customDirectory, PortablePackSyncService.SourceMarkerFileName),
                System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, owner = customSource.Owner, repo = customSource.Repo, tag = customSource.Tag },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            var catalog = new PackCatalogService(paths, new Logger(paths.LogFile));
            var sync = new PortablePackSyncService(paths, new Logger(paths.LogFile)) { Catalog = catalog };

            Assert.False(catalog.IsWithdrawn(reintroduced));
            Assert.Equal(source, sync.TryResolveSource(reintroduced));
            Assert.True(PackMembership.Add(new AppSettings { AddedPackRelativePaths = [] }, reintroduced));
            Assert.False(catalog.IsKnownCatalogPath(custom));
            Assert.Equal(customSource, sync.TryResolveSource(custom));
        }
        finally { TempTree.Delete(paths.Root); }
    }

    [Fact]
    public void FallbackPacksAndSourcesComeFromTheEmbeddedGitHubIndex()
    {
        using var stream = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.catalog.json");
        Assert.NotNull(stream);
        using var document = System.Text.Json.JsonDocument.Parse(stream);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var snapshot = document.RootElement.GetProperty("packs").EnumerateArray().ToArray();
        var sources = PackCatalogService.SeedSources().ToArray();
        var known = PortablePackSyncService.KnownPacks;
        Assert.NotEmpty(snapshot);
        Assert.Equal(snapshot.Length, sources.Length);
        Assert.Equal(sources.Length, known.Count);
        for (var index = 0; index < snapshot.Length; index++)
        {
            var entry = snapshot[index];
            Assert.Equal(entry.GetProperty("id").GetString(), sources[index].Id);
            Assert.Equal(WorldLocations.CanonicalBuildPath(entry.GetProperty("relativePath").GetString()), sources[index].RelativePath);
            var source = entry.GetProperty("source");
            Assert.Equal(new PackSyncSource(source.GetProperty("owner").GetString()!,
                source.GetProperty("repo").GetString()!, source.GetProperty("tag").GetString()!), sources[index].Source);
            Assert.Equal(new PortablePackSyncService.KnownPack(sources[index].RelativePath, sources[index].Source), known[index]);
        }
    }

    [Fact]
    public void TheBuiltInPackIsTheFirstOne()
    {
        Assert.NotEmpty(PortablePackSyncService.KnownPacks);
        Assert.Equal(
            PortablePackSyncService.DefaultPackRelativePath,
            PortablePackSyncService.KnownPacks[0].RelativePath);
        Assert.Equal(
            PortablePackSyncService.DefaultPackSource,
            PortablePackSyncService.KnownPacks[0].Source);
    }

    [Fact]
    public void EveryKnownPackIsFetchable()
    {
        foreach (var pack in PortablePackSyncService.KnownPacks)
        {
            Assert.False(string.IsNullOrWhiteSpace(pack.RelativePath));
            Assert.False(string.IsNullOrWhiteSpace(pack.Source.Owner));
            Assert.False(string.IsNullOrWhiteSpace(pack.Source.Repo));
            Assert.False(string.IsNullOrWhiteSpace(pack.Source.Tag));
            // The name is a folder under Packs, so it must not try to leave it.
            Assert.DoesNotContain("..", pack.RelativePath, StringComparison.Ordinal);
            Assert.Equal(pack.Source, PortablePackSyncService.KnownSourceFor(pack.RelativePath));
        }
    }

    [Fact]
    public void NoTwoKnownPacksShareAName()
    {
        var names = PortablePackSyncService.KnownPacks
            .Select(pack => pack.RelativePath.ToLowerInvariant())
            .ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>
    /// The builds are offered side by side and each fetches from its own
    /// release. Two entries pointing at one release would pour the same files
    /// into two folders and call them different builds.
    /// </summary>
    [Fact]
    public void EveryOfferedBuildFetchesFromItsOwnRelease()
    {
        var sources = PortablePackSyncService.KnownPacks
            .Select(pack => $"{pack.Source.Owner}/{pack.Source.Repo}/{pack.Source.Tag}")
            .Select(source => source.ToLowerInvariant())
            .ToList();
        Assert.Equal(sources.Count, sources.Distinct().Count());
    }

    /// <summary>
    /// A name the list does not carry is not offered: nothing answers for it,
    /// and a folder called that is a pack of somebody's own as far as the
    /// launcher is concerned. An offered name the launcher cannot fetch reads
    /// to a player as a broken download rather than as a build that is not
    /// made any more.
    /// </summary>
    [Fact]
    public void ANameNobodyKnowsIsNotOffered()
    {
        const string unlisted = "Some Build";

        Assert.Null(PortablePackSyncService.KnownSourceFor(unlisted));
        Assert.DoesNotContain(
            PortablePackSyncService.KnownPacks,
            pack => pack.RelativePath.Equals(unlisted, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ACustomPackHasNoSource()
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor("SomebodysOwnPack"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingIsNotAPack(string? name)
    {
        Assert.Null(PortablePackSyncService.KnownSourceFor(name));
    }

    [Fact]
    public void SlashesAndCaseDoNotHideAKnownPack()
    {
        var known = PortablePackSyncService.KnownPacks[0].RelativePath;
        string[] written = [known.ToLowerInvariant(), known + "\\", $" {known} "];

        foreach (var name in written)
        {
            Assert.True(PortablePackSyncService.KnownSourceFor(name) is not null, name);
        }
    }
}
