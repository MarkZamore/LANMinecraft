using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class SettingsService
{
    /// <summary>Schema written by this build; see <see cref="AppSettings.SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 2;

    private readonly AppPaths _paths;
    private readonly Logger? _logger;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const double MinVoiceMasterVolume = 0d;
    private const double MaxVoiceMasterVolume = 2d;
    private const string DefaultVoicePttMode = "Off";
    private const string DefaultVoicePushToTalkBinding = "Key:V";

    public SettingsService(AppPaths paths, Logger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Load()
    {
        var settingsFile = ResolveSettingsFileToRead();

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
            // Load always re-saves, so a pre-Steam file loses its VPN and voice
            // keys the moment this build starts. Keep one copy of the original.
            // The version has to come from the JSON: a missing field means the
            // pre-Steam schema, while the property itself defaults to current.
            TryBackUpBeforeMigration(settingsFile, ReadSchemaVersion(json));
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

    private string ResolveSettingsFileToRead()
    {
        if (File.Exists(_paths.SettingsFile))
        {
            return _paths.SettingsFile;
        }

        return _paths.LegacySettingsFiles.FirstOrDefault(File.Exists) ?? _paths.SettingsFile;
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
        settings.VoiceInputDeviceId = settings.VoiceInputDeviceId?.Trim() ?? "";
        settings.VoiceOutputDeviceId = settings.VoiceOutputDeviceId?.Trim() ?? "";
        settings.VoicePushToTalkKey = string.IsNullOrWhiteSpace(settings.VoicePushToTalkKey)
            ? "V"
            : settings.VoicePushToTalkKey.Trim();
        settings.VoiceMasterVolume = Math.Clamp(settings.VoiceMasterVolume, MinVoiceMasterVolume, MaxVoiceMasterVolume);
        settings.VoicePttMode = NormalizePttMode(settings.VoicePttMode);
        settings.VoicePushToTalkBinding = NormalizePttBinding(settings.VoicePushToTalkBinding, settings.VoicePushToTalkKey);
        settings.VoiceInputVolume = Math.Clamp(settings.VoiceInputVolume, MinVoiceMasterVolume, MaxVoiceMasterVolume);
        settings.VoiceOutputVolume = Math.Clamp(settings.VoiceOutputVolume, MinVoiceMasterVolume, MaxVoiceMasterVolume);

        return settings;
    }

    private static string NormalizePttMode(string? value)
    {
        var mode = value?.Trim();
        return mode is "Off" or "Hold" or "Toggle" ? mode : DefaultVoicePttMode;
    }

    private static string NormalizePttBinding(string? value, string? legacyKey)
    {
        var binding = value?.Trim();
        if (!string.IsNullOrWhiteSpace(binding) &&
            (binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase) ||
             binding.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase)))
        {
            return binding;
        }

        var key = string.IsNullOrWhiteSpace(legacyKey) ? "V" : legacyKey.Trim();
        return string.IsNullOrWhiteSpace(key) ? DefaultVoicePushToTalkBinding : $"Key:{key}";
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

    /// <summary>
    /// Copies the settings file next to itself once, the first time this build
    /// reads a file written by an older schema. Never overwrites an existing
    /// backup, and never blocks startup when it fails.
    /// </summary>
    internal string? TryBackUpBeforeMigration(string settingsFile, int loadedSchemaVersion)
    {
        if (loadedSchemaVersion >= CurrentSchemaVersion) return null;
        try
        {
            var backupDirectory = Path.Combine(_paths.Personal, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(
                backupDirectory,
                $"settings-v{loadedSchemaVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            if (File.Exists(backup)) return backup;
            File.Copy(settingsFile, backup);
            _logger?.Info(
                $"Settings migrated to schema {CurrentSchemaVersion}; the previous file was copied to {backup}.");
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.Warn($"Settings backup before migration failed: {ex.Message}");
            return null;
        }
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
