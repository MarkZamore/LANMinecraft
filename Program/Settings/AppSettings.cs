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
    /// The Java heap, which is what <c>-Xmx</c> is set to and what the game
    /// reports. The room beside it - class data, compiled code and the buffers
    /// Sodium hands the driver - is kept out of the largest number this field
    /// accepts rather than out of this one.
    /// </summary>
    [JsonPropertyName("maxMemoryGb")]
    public int MaxHeapGb { get; set; } = MemorySizingService.MaxRecommendedHeapGb / 2;

    /// <summary>
    /// Absent from a settings file written while this number meant the whole of
    /// the game. The launcher reads it once, converts, and never asks again.
    /// </summary>
    public bool MemorySettingIsTheHeap { get; set; }

    /// <summary>
    /// Marks files where maxMemoryGb applies to all packs.
    /// </summary>
    public bool MemoryIsGlobal { get; set; }

    /// <summary>Historical settings retained for migration and older launchers.</summary>
    public Dictionary<string, int> MemoryByPack { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ClientRelativePath { get; set; } = "";
    /// <summary>Null is an old settings file; an empty list deliberately has no catalog packs.</summary>
    public List<string>? AddedPackRelativePaths { get; set; }
    /// <summary>Explicit list removals, including automatically discovered local packs.</summary>
    public List<string> HiddenPackRelativePaths { get; set; } = [];
    /// <summary>One-time repair of the forced LL8 hiding introduced in release 408.</summary>
    public bool Ll8LocalVisibilityMigrated { get; set; }

    public string SkinPath { get; set; } = "";
    public string SelectedWorldRelativePath { get; set; } = "";
}
