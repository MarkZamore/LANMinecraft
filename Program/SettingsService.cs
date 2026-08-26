using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class SettingsService
{
    /// <summary>The launcher's one format version; see <see cref="PortableFormat"/>.</summary>
    public const int CurrentSchemaVersion = PortableFormat.SchemaVersion;

    private readonly AppPaths _paths;
    private readonly Logger? _logger;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsService(AppPaths paths, Logger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// The pack the memory rules are sized against, as last measured. Unknown
    /// until a pack is installed, and re-measured whenever the player picks
    /// another build or one finishes downloading.
    /// </summary>
    public PackMemoryProfile PackMemory { get; private set; } = PackMemoryProfile.Unknown;

    /// <summary>
    /// Looks at a pack folder and remembers what it weighs. Cheap enough for a
    /// build switch and far too dear for a keystroke, which is why the result is
    /// kept rather than taken again.
    /// </summary>
    public PackMemoryProfile MeasurePack(string? clientRelativePath)
    {
        var relativePath = clientRelativePath?.Trim() ?? "";
        if (relativePath.Length == 0)
        {
            PackMemory = PackMemoryProfile.Unknown;
            return PackMemory;
        }

        try
        {
            PackMemory = PackMemoryProfile.Measure(_paths.CombineUnderPacks(relativePath));
        }
        catch (InvalidOperationException)
        {
            // A path that escapes the portable root is not a pack.
            PackMemory = PackMemoryProfile.Unknown;
        }

        return PackMemory;
    }

    /// <summary>
    /// Puts the launcher's own suggestion back in step with the pack it is for.
    /// A number the player typed is left exactly as it is - for ever, and on
    /// every pack. Returns true when the number moved.
    /// </summary>
    public bool ApplyPackRecommendation(AppSettings settings)
    {
        if (settings.MemoryChosenByPlayer) return false;

        var recommended =
            MemorySizingService.GetRecommendedDefaultMemoryGb(PackMemory, VideoMemoryProfile.Measure());
        if (settings.MaxMemoryGb == recommended) return false;

        _logger?.Info(
            $"Memory for this pack ({DescribePack()}): {recommended} GB for the game, " +
            $"of which {MemorySizingService.GetHeapGb(PackMemory, recommended, VideoMemoryProfile.Measure())} GB " +
            "is the Java heap.");
        settings.MaxMemoryGb = recommended;
        Save(settings);
        return true;
    }

    private string DescribePack() =>
        PackMemory.IsKnown
            ? $"{PackMemory.ModCount} mods, Minecraft {PackMemory.MinecraftVersion ?? "unknown"}"
            : "not installed yet";

    public AppSettings Load()
    {
        var settingsFile = _paths.SettingsFile;

        if (!File.Exists(settingsFile))
        {
            var defaults = CreateSafeDefaults();
            TryPersistSafeDefaults(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(settingsFile);
            var hasConfiguredMemory = HasJsonProperty(json, "maxMemoryGb");
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
            var pack = MeasurePack(settings.ClientRelativePath);
            // Up to schema 11 the number was the Java heap alone; from 12 it is
            // everything the game may take. A stored heap is carried across as
            // the smallest budget that still leaves it, so no one's game shrinks
            // on the launch that changed the meaning.
            if (hasConfiguredMemory && !HasJsonProperty(json, "memorySettingIsWholeGame"))
            {
                var budget = MemorySizingService.GetBudgetForHeapGb(
                    pack, settings.MaxMemoryGb, VideoMemoryProfile.Measure());
                _logger?.Info(
                    $"Memory setting carried across: {settings.MaxMemoryGb} GB of heap becomes {budget} GB for the whole game.");
                settings.MaxMemoryGb = budget;
            }
            settings.MemorySettingIsWholeGame = true;
            // Whose number is it? A file written before the launcher asked is
            // asked once, and answers by arithmetic: a number the launcher would
            // have suggested on this machine is the launcher's, and from now on
            // it follows the pack; anything else was typed by hand and stays.
            if (hasConfiguredMemory && !HasJsonProperty(json, "memoryChosenByPlayer"))
            {
                settings.MemoryChosenByPlayer = settings.MaxMemoryGb !=
                    MemorySizingService.GetRecommendedDefaultMemoryGb(PackMemoryProfile.Unknown);
            }
            settings = ApplyFallbacks(settings, pack, useRecommendedMemory: !hasConfiguredMemory);
            ApplyPackRecommendation(settings);
            TryPersistSafeDefaults(settings);
            return settings;
        }
        catch
        {
            var defaults = CreateSafeDefaults();
            TryPersistSafeDefaults(defaults);
            return defaults;
        }
    }

    public void Save(AppSettings settings)
    {
        settings = ApplyFallbacks(settings, PackMemory);
        AtomicFile.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(settings, _options));
    }

    private static AppSettings ApplyFallbacks(
        AppSettings? source,
        PackMemoryProfile pack,
        bool useRecommendedMemory = false)
    {
        var settings = source ?? new AppSettings();

        settings.SchemaVersion = CurrentSchemaVersion;
        settings.PlayerName = settings.PlayerName?.Trim() ?? "";
        settings.PreviousPlayerName = settings.PreviousPlayerName?.Trim() ?? "";

        settings.LocalIdentityId = settings.LocalIdentityId?.Trim() ?? "";
        settings.LocalIdentityName = settings.LocalIdentityName?.Trim() ?? "";
        if (source is null || useRecommendedMemory ||
            settings.MaxMemoryGb < MemorySizingService.MinMemoryGb ||
            settings.MaxMemoryGb > MemorySizingService.MaxMemoryGb)
        {
            settings.MaxMemoryGb =
                MemorySizingService.GetRecommendedDefaultMemoryGb(pack, VideoMemoryProfile.Measure());
        }
        else
        {
            // Held to what this machine may be asked for, not merely to what a
            // number may be. The box in the window has always refused more than
            // the machine can spare; the file did not, so a number written into
            // it by hand went straight to -Xmx and the two disagreed about the
            // same setting. A file is not a way around the window.
            settings.MaxMemoryGb = MemorySizingService.ClampMemoryGb(settings.MaxMemoryGb);
        }

        settings.ClientRelativePath = settings.ClientRelativePath?.Trim() ?? "";
        settings.SkinPath = settings.SkinPath?.Trim() ?? "";
        settings.SelectedWorldRelativePath = settings.SelectedWorldRelativePath?.Trim() ?? "";

        return settings;
    }

    private AppSettings CreateSafeDefaults()
    {
        return ApplyFallbacks(new AppSettings(), PackMemory, useRecommendedMemory: true);
    }

    /// <summary>1 when the file predates the version field.</summary>
    internal static int ReadSchemaVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("schemaVersion", out var value) &&
                   value.TryGetInt32(out var version)
                ? version
                : 1;
        }
        catch (JsonException)
        {
            return 1;
        }
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.EnumerateObject().Any(property =>
                   string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private void TryPersistSafeDefaults(AppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch
        {
            // Settings persistence is optional on startup.
        }
    }
}
