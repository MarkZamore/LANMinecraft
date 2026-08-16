using System.Text.Json;

namespace Minecraft.Tests;

/// <summary>
/// The two per-world documents that learned about Steam accounts:
/// .minecraft-portable-world.json (schema 5 -> 6) and
/// .minecraft-portable-players.json (schema 2 -> 3).
///
/// Every player already has worlds written by the previous build, so the cases
/// that matter are the ones where an old file meets the new code: it must still
/// be read, it must not lose its owner, and the Steam id must only ever be
/// learned from the machine that can actually know it.
/// </summary>
public sealed class WorldSteamSchemaTests : IDisposable
{
    private const ulong OwnerSteamId = 76561198000000001;
    private const ulong OtherSteamId = 76561198000000002;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-world-schema-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void AWorldFromThePreviousBuild_LearnsItsOwnersSteamAccount()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, "Chebupeli");
        var ownerUuid = Guid.NewGuid().ToString("D");
        WriteLegacyMetadata(world, ownerUuid, "MarkZamore");
        var service = new WorldMetadataService();

        Assert.True(service.TryWriteOwnerMetadata(
            world,
            ownerUuid,
            "MarkZamore",
            overwriteExistingOwner: false,
            ownerSteamId64: OwnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var metadata = service.Read(world);
        Assert.NotNull(metadata);
        Assert.Equal(ownerUuid, metadata.OwnerIdentityId);
        Assert.Equal(OwnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata.OwnerSteamId64);
        Assert.Equal(WorldMetadataService.CurrentSchemaVersion, metadata.SchemaVersion);
    }

    /// <summary>
    /// Only the owner's own machine may fill this in: on anyone else's copy the
    /// local Steam account belongs to a different player entirely.
    /// </summary>
    [Fact]
    public void SomebodyElsesWorld_IsNotStampedWithTheLocalSteamAccount()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, "Guest");
        var ownerUuid = Guid.NewGuid().ToString("D");
        WriteLegacyMetadata(world, ownerUuid, "anuvenn");
        var service = new WorldMetadataService();

        Assert.True(service.TryWriteOwnerMetadata(
            world,
            Guid.NewGuid().ToString("D"),
            "MarkZamore",
            overwriteExistingOwner: false,
            ownerSteamId64: OtherSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var metadata = service.Read(world);
        Assert.NotNull(metadata);
        Assert.Equal(ownerUuid, metadata.OwnerIdentityId);
        Assert.Equal("anuvenn", metadata.OwnerIdentityName);
        Assert.Empty(metadata.OwnerSteamId64);
    }

    [Fact]
    public void AMachineWithoutSteam_WritesNoSteamIdAtAll()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, "Offline");
        var ownerUuid = Guid.NewGuid().ToString("D");
        var service = new WorldMetadataService();

        Assert.True(service.TryWriteOwnerMetadata(world, ownerUuid, "MarkZamore"));
        Assert.True(service.TryWriteCurrentHolderMetadata(world, ownerUuid, "MarkZamore", transferred: false));

        var metadata = service.Read(world);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.OwnerSteamId64);
        Assert.Empty(metadata.CurrentHolderSteamId64);
        // An empty string is never written as a Steam id, so nothing can later
        // mistake "unknown" for "known to be blank".
        var raw = File.ReadAllText(Path.Combine(world, WorldMetadataService.MetadataFileName));
        Assert.DoesNotContain("\"ownerSteamId64\": \"7656", raw, StringComparison.Ordinal);
    }

    /// <summary>A document from a future build keeps its own version.</summary>
    [Fact]
    public void ANewerMetadataDocument_IsNotDowngraded()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, "Future");
        var ownerUuid = Guid.NewGuid().ToString("D");
        WriteLegacyMetadata(world, ownerUuid, "MarkZamore", schemaVersion: 99);
        var service = new WorldMetadataService();

        Assert.True(service.TryWriteCurrentHolderMetadata(world, ownerUuid, "MarkZamore", transferred: false));

        Assert.Equal(99, service.Read(world)!.SchemaVersion);
    }

    [Fact]
    public void APlayerManifestFromThePreviousBuild_IsAcceptedAndUpgradedOnce()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, "Chebupeli");
        var identity = TestIdentity.CreateContext(paths, "MarkZamore", OwnerSteamId);
        WritePlayerProfile(world, identity.PlayerUuid);
        var manifests = new WorldPlayerManifestService();

        // A v2 manifest, as the previous build wrote it.
        var legacy = manifests.Write(world, identity);
        Assert.Equal(WorldPlayerManifestService.CurrentSchemaVersion, legacy.SchemaVersion);
        DowngradeManifestToV2(world);

        var profiles = new WorldPlayerProfileService(paths);
        profiles.PrepareWorldForOutgoingTransfer(world, identity);

        var manifest = manifests.Read(world);
        Assert.NotNull(manifest);
        Assert.Equal(WorldPlayerManifestService.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(
            OwnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            manifest.CurrentHolderSteamId64);
        Assert.All(manifest.Players, player => Assert.Equal(player.MinecraftUuid, player.PortableUuid));

        // The pre-Steam file is kept once, outside the world, where it cannot
        // change the world's hash.
        var backups = Directory.GetDirectories(paths.IdentityBackups);
        Assert.Single(backups);
        Assert.True(File.Exists(Path.Combine(
            backups[0], "worlds", "Chebupeli", WorldPlayerManifestService.ManifestFileName)));

        // Running it again neither re-backs-up nor changes the shape.
        profiles.PrepareWorldForOutgoingTransfer(world, identity);
        Assert.Single(Directory.GetDirectories(paths.IdentityBackups));
        Assert.Single(Directory.GetDirectories(Path.Combine(backups[0], "worlds")));
    }

    [Fact]
    public void AManifestFromAFutureBuild_IsRejected()
    {
        var paths = CreatePaths();
        var world = CreateWorld(paths, "Future");
        var identity = TestIdentity.CreateContext(paths, "MarkZamore", OwnerSteamId);
        WritePlayerProfile(world, identity.PlayerUuid);
        var manifests = new WorldPlayerManifestService();
        manifests.Write(world, identity);
        SetManifestSchemaVersion(world, WorldPlayerManifestService.CurrentSchemaVersion + 1);

        var failure = Assert.Throws<InvalidDataException>(() => manifests.Validate(world));
        Assert.Contains("Unsupported player manifest schema", failure.Message, StringComparison.Ordinal);
    }

    private AppPaths CreatePaths()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        return paths;
    }

    private static string CreateWorld(AppPaths paths, string name)
    {
        var world = Path.Combine(paths.Worlds, name);
        Directory.CreateDirectory(world);
        new NbtFile("", new NbtCompoundTag()).Write(Path.Combine(world, "level.dat"));
        return world;
    }

    private static void WritePlayerProfile(string world, Guid playerUuid)
    {
        var playerData = Path.Combine(world, "playerdata");
        Directory.CreateDirectory(playerData);
        var root = new NbtCompoundTag();
        root.SetUuid(playerUuid);
        new NbtFile("", root).Write(Path.Combine(playerData, $"{playerUuid:D}.dat"));
    }

    /// <summary>Writes the document exactly as the pre-Steam build wrote it.</summary>
    private static void WriteLegacyMetadata(
        string world,
        string ownerUuid,
        string ownerName,
        int schemaVersion = 5)
    {
        var document = new
        {
            schemaVersion,
            worldId = Guid.NewGuid().ToString("D"),
            buildName = "Infinity",
            buildRelativePath = "Infinity",
            packHash = new string('a', 64),
            createdAtUtc = DateTimeOffset.UtcNow,
            markedBy = "Minecraft.exe",
            ownerIdentityId = ownerUuid,
            ownerIdentityName = ownerName,
            currentHolderIdentityId = ownerUuid,
            currentHolderIdentityName = ownerName,
            lastSuccessfulTransferUtc = (DateTimeOffset?)null
        };
        File.WriteAllText(
            Path.Combine(world, WorldMetadataService.MetadataFileName),
            JsonSerializer.Serialize(document, JsonOptions));
    }

    private static void DowngradeManifestToV2(string world) =>
        SetManifestSchemaVersion(world, WorldPlayerManifestService.MinimumSupportedSchemaVersion);

    private static void SetManifestSchemaVersion(string world, int schemaVersion)
    {
        var path = Path.Combine(world, WorldPlayerManifestService.ManifestFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var rewritten = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            rewritten[property.Name] = property.Name == "schemaVersion"
                ? schemaVersion
                : JsonSerializer.Deserialize<object>(property.Value.GetRawText(), JsonOptions);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(rewritten, JsonOptions));
    }
}
