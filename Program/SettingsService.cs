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
            // Up to schema 11 the number was the Java heap alone; from 12 it is
            // everything the game may take. A stored heap is carried across as
            // the smallest budget that still leaves it, so no one's game shrinks
            // on the launch that changed the meaning.
            if (hasConfiguredMemory && !HasJsonProperty(json, "memorySettingIsWholeGame"))
            {
                var budget = MemorySizingService.GetBudgetForHeapGb(settings.MaxMemoryGb);
                _logger?.Info(
                    $"Memory setting carried across: {settings.MaxMemoryGb} GB of heap becomes {budget} GB for the whole game.");
                settings.MaxMemoryGb = budget;
            }
            settings.MemorySettingIsWholeGame = true;
            settings = ApplyFallbacks(settings, useRecommendedMemory: !hasConfiguredMemory);
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
        settings = ApplyFallbacks(settings);
        AtomicFile.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(settings, _options));
    }

    private static AppSettings ApplyFallbacks(AppSettings? source, bool useRecommendedMemory = false)
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
            settings.MaxMemoryGb = MemorySizingService.GetRecommendedDefaultMemoryGb();
        }
        else
        {
            settings.MaxMemoryGb = Math.Clamp(
                settings.MaxMemoryGb,
                MemorySizingService.MinMemoryGb,
                MemorySizingService.MaxMemoryGb);
        }

        settings.ClientRelativePath = settings.ClientRelativePath?.Trim() ?? "";
        settings.SkinPath = settings.SkinPath?.Trim() ?? "";
        settings.SelectedWorldRelativePath = settings.SelectedWorldRelativePath?.Trim() ?? "";

        return settings;
    }

    private static AppSettings CreateSafeDefaults()
    {
        return ApplyFallbacks(new AppSettings(), useRecommendedMemory: true);
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
