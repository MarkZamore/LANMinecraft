using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class LegacySteamComponentCatalogTests
{
    [Fact]
    public void SourceBuiltLegacyPortHasExactCoverageAndSourceProvenanceWithoutAnInventedAuthorJar()
    {
        var catalog = SteamComponentCatalog.Parse(WithLegacy().ToJsonString());
        var port = Assert.Single(catalog.Builds, build => build.Loaders.Contains(PackLoaderKind.LegacyFabric));
        Assert.Null(port.AuthorFileName);
        Assert.Equal(new[] { 1, 6, 4 }, port.MinimumMinecraftVersion);
        Assert.Equal(new[] { 1, 6, 5 }, port.ExclusiveMaximumMinecraftVersion);
        Assert.Equal("https://github.com/Kamilhik/e4steam/tree/" + new string('b', 40), port.UpstreamDownloadUri.AbsoluteUri);
        Assert.Equal(20, catalog.Builds.Count);
    }

    [Fact]
    public void ANewCatalogKeepsTheActualHistoricalSourceRevisionOfUnchangedArtifacts()
    {
        var root = WithLegacy();
        root["sourceCommit"] = new string('d', 40);
        var catalog = SteamComponentCatalog.Parse(root.ToJsonString());
        Assert.Equal(new string('d', 40), catalog.SourceCommit);
        Assert.Equal(20, catalog.Builds.Count);
    }

    [Theory]
    [InlineData("wide-range")]
    [InlineData("other-loader")]
    [InlineData("invented-author")]
    [InlineData("unreviewed-kind")]
    [InlineData("no-class-pins")]
    [InlineData("wrong-class-hash")]
    [InlineData("class-path-escape")]
    [InlineData("wrong-source")]
    [InlineData("missing-upstream")]
    public void UnreviewedLegacyPortsAreRejected(string mutation)
    {
        var root = WithLegacy();
        var row = root["builds"]!.AsArray().Last()!;
        var derivation = row["derivation"]!;
        switch (mutation)
        {
            case "wide-range": row["exclusiveMaximumMinecraftVersion"] = new JsonArray(1, 7); break;
            case "other-loader": row["loaders"] = new JsonArray("Fabric"); break;
            case "invented-author": row["authorFileName"] = "e4steam-invented-upstream.jar"; break;
            case "unreviewed-kind": derivation["type"] = "byte-identical-author-artifact"; break;
            case "no-class-pins": derivation["sourceBuiltClassSha256"] = new JsonObject(); break;
            case "wrong-class-hash": derivation["sourceBuiltClassSha256"]!["link/e4steam/legacy164/LegacyRuntime.class"] = "bad"; break;
            case "class-path-escape": derivation["sourceBuiltClassSha256"] = new JsonObject { ["../LegacyRuntime.class"] = new string('a', 64) }; break;
            case "wrong-source": derivation["sourceCommit"] = "invalid-source"; break;
            case "missing-upstream": derivation["upstreamSourceCommit"] = ""; break;
        }
        Assert.Throws<InvalidDataException>(() => SteamComponentCatalog.Parse(root.ToJsonString()));
    }

    private static JsonObject WithLegacy()
    {
        var root = JsonNode.Parse(SteamComponentCatalog.HistoricalV2Json())!.AsObject();
        root["builds"]!.AsArray().Add(new JsonObject
        {
            ["version"] = "0.3.4", ["authorVersion"] = "0.3.4",
            ["fileName"] = "e4steam-legacy-fabric-mc1.6.4-v0.3.4-lf164.1.jar",
            ["sizeBytes"] = 3500000, ["sha256"] = new string('a', 64),
            ["loaders"] = new JsonArray("LegacyFabric"),
            ["minimumMinecraftVersion"] = new JsonArray(1, 6, 4),
            ["exclusiveMaximumMinecraftVersion"] = new JsonArray(1, 6, 5),
            ["profile"] = root["profile"]!.GetValue<string>(), ["deliveryTag"] = "legacy-fabric-164-v1",
            ["downloadUrl"] = "https://github.com/MarkZamore/e4steam/releases/download/legacy-fabric-164-v1/e4steam-legacy-fabric-mc1.6.4-v0.3.4-lf164.1.jar",
            ["metadataPath"] = "fabric.mod.json",
            ["derivation"] = new JsonObject
            {
                ["type"] = "source-built-legacy-port", ["sourceCommit"] = root["sourceCommit"]!.GetValue<string>(),
                ["upstreamSourceCommit"] = new string('b', 40),
                ["sourceBuiltClassSha256"] = new JsonObject { ["link/e4steam/legacy164/LegacyRuntime.class"] = new string('c', 64) }
            }
        });
        return root;
    }
}
