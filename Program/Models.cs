using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Minecraft;

public sealed class AppSettings
{
    /// <summary>
    /// 1 = the VPN/voice era (no version field on disk); 2 = Steam era. The
    /// launcher backs a file up once before it first writes the new shape, so
    /// an older build can be restored by hand.
    /// </summary>
    public int SchemaVersion { get; set; } = SettingsService.CurrentSchemaVersion;

    public string PlayerName { get; set; } = "";
    public string PreviousPlayerName { get; set; } = "";

    [JsonIgnore]
    public string LocalIdentityId { get; set; } = "";

    [JsonIgnore]
    public string LocalIdentityName { get; set; } = "";

    /// <summary>
    /// Everything the game may take: the Java heap and the room beside it for
    /// class data, compiled code and the buffers Sodium hands the driver.
    /// </summary>
    public int MaxMemoryGb { get; set; } = 16;

    /// <summary>
    /// False in a settings file written when this number was the heap alone.
    /// The launcher reads it once, converts, and never asks again.
    /// </summary>
    public bool MemorySettingIsWholeGame { get; set; }

    /// <summary>
    /// True once the number above is the player's own. Until then it is the
    /// launcher's suggestion, and it follows the pack: a build of nine hundred
    /// mods and a vanilla one do not want the same number, and the player who
    /// never touched the field should not have to work that out.
    /// </summary>
    public bool MemoryChosenByPlayer { get; set; }

    public string ClientRelativePath { get; set; } = "";
    public string SkinPath { get; set; } = "";
    public string SelectedWorldRelativePath { get; set; } = "";
}

/// <summary>A Steam friend a report can be sent to.</summary>
public sealed record DiagnosticLogTargetOption(SteamId64 SteamId, string DisplayName);

public sealed class WaypointProviderAnnouncement
{
    public string ProviderId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string WorldContextId { get; set; } = "";
}

/// <summary>
/// A Steam friend running this launcher, as the window shows them. The VPN era
/// tracked addresses, routes and round-trip times here; a peer is now an
/// account, and everything else comes from their rich presence.
/// </summary>
public sealed class PeerViewModel : INotifyPropertyChanged
{
    private string _personaName = "";
    private string _playerName = "";
    private string _minecraftUuid = "";
    private string _packHash = "";
    private string _localPackHash = "";
    private bool _isMinecraftRunning;
    private bool _isMinecraftPreparing;
    private bool _isSkinAvailable;
    private string _skinSha256 = "";
    private string _skinModel = "classic";
    private string _hostedWorldId = "";
    private int _protocolVersion;
    private int _waypointProtocolVersion;
    private IReadOnlyList<WaypointProviderAnnouncement> _waypointProviders = [];
    private int _diagnosticProtocolVersion;
    private DateTimeOffset _lastSeen;

    public required SteamId64 SteamId { get; init; }

    /// <summary>The Steam display name; always present, unlike the Minecraft nickname.</summary>
    public string PersonaName
    {
        get => _personaName;
        set { if (Set(ref _personaName, value ?? "")) OnPropertyChanged(nameof(DisplayName)); }
    }

    public string PlayerName
    {
        get => _playerName;
        set { if (Set(ref _playerName, value ?? "")) OnPropertyChanged(nameof(DisplayName)); }
    }

    /// <summary>Self-reported: it names their data, it never authorises anything.</summary>
    public string MinecraftUuid
    {
        get => _minecraftUuid;
        set => Set(ref _minecraftUuid, value ?? "");
    }

    public string PackHash
    {
        get => _packHash;
        set { if (Set(ref _packHash, value ?? "")) OnPropertyChanged(nameof(PackStatus)); }
    }

    public string LocalPackHash
    {
        get => _localPackHash;
        set { if (Set(ref _localPackHash, value ?? "")) OnPropertyChanged(nameof(PackStatus)); }
    }

    public bool IsMinecraftRunning
    {
        get => _isMinecraftRunning;
        set => Set(ref _isMinecraftRunning, value);
    }

    public bool IsMinecraftPreparing
    {
        get => _isMinecraftPreparing;
        set => Set(ref _isMinecraftPreparing, value);
    }

    public bool IsSkinAvailable
    {
        get => _isSkinAvailable;
        set => Set(ref _isSkinAvailable, value);
    }

    public string SkinSha256
    {
        get => _skinSha256;
        set => Set(ref _skinSha256, value ?? "");
    }

    public string SkinModel
    {
        get => _skinModel;
        set => Set(ref _skinModel, string.Equals(value, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic");
    }

    public string HostedWorldId
    {
        get => _hostedWorldId;
        set => Set(ref _hostedWorldId, value ?? "");
    }

    /// <summary>The launcher protocol the peer publishes in its presence.</summary>
    public int ProtocolVersion
    {
        get => _protocolVersion;
        set
        {
            if (!Set(ref _protocolVersion, value)) return;
            OnPropertyChanged(nameof(IsPresenceKnown));
            OnPropertyChanged(nameof(IsCompatible));
            OnPropertyChanged(nameof(SupportsDiagnosticLogs));
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public int WaypointProtocolVersion
    {
        get => _waypointProtocolVersion;
        set => Set(ref _waypointProtocolVersion, value);
    }

    public IReadOnlyList<WaypointProviderAnnouncement> WaypointProviders
    {
        get => _waypointProviders;
        set => Set(ref _waypointProviders, value ?? []);
    }

    public int DiagnosticProtocolVersion
    {
        get => _diagnosticProtocolVersion;
        set { if (Set(ref _diagnosticProtocolVersion, value)) OnPropertyChanged(nameof(SupportsDiagnosticLogs)); }
    }

    public DateTimeOffset LastSeen
    {
        get => _lastSeen;
        set { if (Set(ref _lastSeen, value)) OnPropertyChanged(nameof(LastSeenText)); }
    }

    /// <summary>
    /// Whether we know what this peer speaks at all. Zero means their presence
    /// could not be read - they are listed because we hold a connection to
    /// them, which is proof they are there and no evidence about their version.
    /// No launcher announces zero.
    /// </summary>
    public bool IsPresenceKnown => ProtocolVersion > 0;

    /// <summary>
    /// False only when we know they cannot understand us. An unread presence is
    /// not a version mismatch: two players on the same build were told to update
    /// because Steam would not serve one of them, and the transfer refused to
    /// start. The handshake checks versions properly and says so - guessing
    /// here only made it lie.
    /// </summary>
    public bool IsCompatible => !IsPresenceKnown || PortableFormat.CanSpeak(ProtocolVersion);

    public bool SupportsDiagnosticLogs =>
        IsCompatible &&
        (!IsPresenceKnown ||
         (PortableFormat.CanSpeak(DiagnosticProtocolVersion) && DiagnosticProtocolVersion > 0));

    /// <summary>Both names when they differ, because either one may be the familiar one.</summary>
    public string DisplayName
    {
        get
        {
            var minecraftName = string.IsNullOrWhiteSpace(PlayerName) ? "" : PlayerName.Trim();
            var steamName = string.IsNullOrWhiteSpace(PersonaName) ? "" : PersonaName.Trim();
            var name = minecraftName.Length == 0
                ? steamName.Length == 0 ? "Неизвестный игрок" : steamName
                : steamName.Length == 0 ||
                  string.Equals(minecraftName, steamName, StringComparison.OrdinalIgnoreCase)
                    ? minecraftName
                    : $"{minecraftName} ({steamName})";
            // Saying it in the list is the only way the pair of them find out
            // which side has to update - but only when it is true.
            if (!IsPresenceKnown) return $"{name} — на связи, данные Steam недоступны";
            return IsCompatible ? name : $"{name} — нужно обновить лаунчер";
        }
    }

    public string PackStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackHash) || string.IsNullOrWhiteSpace(LocalPackHash)) return "";
            return string.Equals(PackHash, LocalPackHash, StringComparison.OrdinalIgnoreCase)
                ? "сборка совпадает"
                : "другая сборка";
        }
    }

    public string LastSeenText => LastSeen == default
        ? ""
        : LastSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Copies everything a peer told us about itself through Steam.</summary>
    public void Apply(SteamPeerPresence presence, string localPackHash)
    {
        ArgumentNullException.ThrowIfNull(presence);
        PersonaName = presence.PersonaName;
        PlayerName = presence.PlayerName;
        MinecraftUuid = presence.MinecraftUuid;
        PackHash = presence.PackHash;
        LocalPackHash = localPackHash;
        IsMinecraftRunning = presence.IsMinecraftRunning;
        IsMinecraftPreparing = presence.IsMinecraftPreparing;
        IsSkinAvailable = presence.IsSkinAvailable;
        SkinSha256 = presence.SkinSha256;
        SkinModel = presence.SkinModel;
        HostedWorldId = presence.HostedWorldId;
        ProtocolVersion = presence.ProtocolVersion;
        WaypointProtocolVersion = presence.WaypointProtocolVersion;
        WaypointProviders = presence.WaypointProviders;
        DiagnosticProtocolVersion = presence.DiagnosticProtocolVersion;
        LastSeen = DateTimeOffset.Now;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WorldViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string BuildName { get; init; }
    /// <summary>The build folder this world records, used to offer it only there.</summary>
    public string BuildRelativePath { get; init; } = "";
    public string DisplayName => $"{Name} ({BuildName})";
}

public sealed class ClientBuildViewModel
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public bool IsInstalled { get; init; } = true;
}

/// <summary>One row of the "Что нового" list.</summary>
public sealed class ChangelogEntryViewModel
{
    public required int Version { get; init; }
    public required string Text { get; init; }
    public string Title => $"Версия {Version}";
}

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

public sealed class WorldMetadata
{
    public int SchemaVersion { get; set; } = WorldMetadataService.CurrentSchemaVersion;
    public string WorldId { get; set; } = "";
    public string BuildName { get; set; } = "";
    public string BuildRelativePath { get; set; } = "";
    public string PackHash { get; set; } = "";
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
}

