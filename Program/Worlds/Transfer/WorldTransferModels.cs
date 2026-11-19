namespace Minecraft;

public sealed class WorldTransferHeader
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string MessageType { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string SenderName { get; set; } = "";

    /// <summary>The sender's Minecraft UUID; it names their data inside the world.</summary>
    public string SenderIdentityId { get; set; } = "";

    /// <summary>
    /// The sender's Steam account. The receiver rejects a header whose account
    /// is not the one Steam authenticated for this connection.
    /// </summary>
    public string SenderSteamId64 { get; set; } = "";

    public string SenderIdentityName { get; set; } = "";
    public string OwnerIdentityId { get; set; } = "";
    public string OwnerSteamId64 { get; set; } = "";
    public string OwnerIdentityName { get; set; } = "";
    public long Size { get; set; }
    public string WorldSha256 { get; set; } = "";
    public string PlayerManifestSha256 { get; set; } = "";
    public string WaypointManifestSha256 { get; set; } = "";
    public string FileName { get; set; } = "world.zip";
    public string WorldName { get; set; } = "World";
}

public sealed class WorldTransferProgressFrame
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string MessageType { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string Stage { get; set; } = "";
    public long Current { get; set; }
    public long Total { get; set; }
}

public sealed class WorldTransferAck
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public bool Ok { get; set; }
    public string Stage { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string Message { get; set; } = "";
    public string WorldSha256 { get; set; } = "";
    public string PlayerManifestSha256 { get; set; } = "";
    public string WaypointManifestSha256 { get; set; } = "";
}

public sealed class WorldTransferControl
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string TransferId { get; set; } = "";
    public string MessageType { get; set; } = "";
    // No default command: a frame that omits the field must never be mistaken
    // for a commit.
    public string Command { get; set; } = "";
}

public sealed class WorldTransferJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string TransferId { get; set; } = "";
    public string Role { get; set; } = "";
    public string State { get; set; } = "";
    public string SourceWorldPath { get; set; } = "";
    public string EscrowPath { get; set; } = "";
    public string InstalledWorldPath { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
