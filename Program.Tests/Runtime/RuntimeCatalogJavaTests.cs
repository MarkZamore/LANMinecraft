using System.IO;
using System.Text.Json.Nodes;

namespace Minecraft.Tests;

public sealed class RuntimeCatalogJavaTests
{
    [Theory]
    [InlineData("schema")]
    [InlineData("default")]
    [InlineData("duplicate-major")]
    [InlineData("source")]
    [InlineData("hash")]
    [InlineData("empty-rules")]
    [InlineData("overlap")]
    [InlineData("world-sharing-java")]
    [InlineData("installer-versions")]
    public void InvalidRemoteJavaSelectionIsRejectedBeforeActivation(string change)
    {
        var json = JsonNode.Parse(RuntimeComponentContext.Current.JavaCatalogJson)!.AsObject();
        var releases = json["releases"]!.AsArray();
        switch (change)
        {
            case "schema": json["schemaVersion"] = 2; break;
            case "default": json["defaultMajorVersion"] = 999; break;
            case "duplicate-major": releases.Add(releases[0]!.DeepClone()); break;
            case "source": releases[0]!["downloadUris"] = new JsonArray("https://unapproved.example/runtime.zip"); break;
            case "hash": releases[0]!["archiveSha256"] = "invalid"; break;
            case "empty-rules": json["rules"] = new JsonArray(); break;
            case "overlap": json["rules"]!.AsArray().Add(json["rules"]![0]!.DeepClone()); break;
            case "world-sharing-java": json["worldSharingRules"]![0]!["javaMajorVersion"] = 999; break;
            case "installer-versions": json["installerJavaRules"]![0]!["loaderVersions"] = new JsonArray(); break;
        }
        Assert.Throws<InvalidDataException>(() => JavaRuntimeCatalog.ValidateJson(json.ToJsonString()));
    }
}
