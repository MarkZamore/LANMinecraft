namespace Minecraft;

public sealed class WorldMetadata
{
    public int SchemaVersion { get; set; } = WorldMetadataService.CurrentSchemaVersion;
    public string WorldId { get; set; } = "";
    public string BuildName { get; set; } = "";
    public string BuildRelativePath { get; set; } = "";
    public string PackHash { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public string PlayerStorage { get; set; } = "";
    /// <summary>The proven owner of Data.Player, from a played session or a verified profile import.</summary>
    public string LegacyHostSteamId64 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string MarkedBy { get; set; } = "LANMinecraft.exe";
    /// <summary>The creator's Minecraft UUID. It is never rewritten once set.</summary>
    public string OwnerIdentityId { get; set; } = "";

    /// <summary>
    /// The creator's Steam account, back-filled when the owning player opens
    /// the world on a bound machine. Empty on worlds that predate Steam and on
    /// anyone else's copy.
    /// </summary>
    public string OwnerSteamId64 { get; set; } = "";

    public string OwnerIdentityName { get; set; } = "";
    public string CurrentHolderIdentityId { get; set; } = "";
    public string CurrentHolderSteamId64 { get; set; } = "";
    public string CurrentHolderIdentityName { get; set; } = "";
    public DateTimeOffset? LastSuccessfulTransferUtc { get; set; }
}

public sealed class WorldMetadataContext
{
    public required string BuildName { get; init; }
    public required string BuildRelativePath { get; init; }
    public required string PackHash { get; init; }
    public required string OwnerIdentityId { get; init; }
    public required string OwnerIdentityName { get; init; }

    /// <summary>Empty until the machine is bound to a Steam account.</summary>
    public string OwnerSteamId64 { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
}
