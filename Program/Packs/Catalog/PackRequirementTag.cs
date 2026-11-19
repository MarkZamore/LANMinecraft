namespace Minecraft;

/// <summary>A library recommendation. It does not change game settings or gate launch.</summary>
public sealed record PackRequirementTag(string Level, string Label)
{
    public static PackRequirementTag Low { get; } = new("low", "Низкие требования");
    public static PackRequirementTag Medium { get; } = new("medium", "Средние требования");
    public static PackRequirementTag High { get; } = new("high", "Высокие требования");
    public static IReadOnlyList<PackRequirementTag> All { get; } = Array.AsReadOnly(new[] { Low, Medium, High });

    internal static PackRequirementTag? FromCatalogCode(string? level) => level switch
    {
        "low" => Low,
        "medium" => Medium,
        "high" => High,
        _ => null
    };

    internal static PackRequirementTag Estimate(string minecraftVersion, int loadedMods, bool isNative)
    {
        if (loadedMods >= 350) return High;
        if (isNative) return Medium;
        var heap = MemorySizingService.GetRecommendedHeapGb(new PackMemoryProfile(loadedMods, 0, 0, minecraftVersion));
        if (heap >= 7) return High;
        // New calendar-version Java builds have a modern graphics baseline
        // even when there are few mods and the heap remains small.
        var versionMajor = minecraftVersion.AsSpan();
        var separator = versionMajor.IndexOf('.');
        if (separator >= 0) versionMajor = versionMajor[..separator];
        var currentJava = int.TryParse(versionMajor, out var major) && major >= 26;
        return heap >= 4 || currentJava ? Medium : Low;
    }
}
