using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class SettingsService
{
    /// <summary>The launcher's one format version; see <see cref="PortableFormat"/>.</summary>
    public const int CurrentSchemaVersion = PortableFormat.SchemaVersion;

    private readonly AppPaths _paths;
    private readonly Logger? _logger;
    private readonly MeasuredMemoryStore _measuredMemory;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsService(AppPaths paths, Logger? logger = null)
    {
        _paths = paths;
        _logger = logger;
        _measuredMemory = new MeasuredMemoryStore(paths);
    }

    /// <summary>
    /// The pack the memory rules are sized against, as last measured. Unknown
    /// until a pack is installed, and re-measured whenever the player picks
    /// another build or one finishes downloading.
    /// </summary>
    public PackMemoryProfile PackMemory { get; private set; } = PackMemoryProfile.Unknown;

    /// <summary>
    /// What that pack was last seen holding beside its heap on this machine.
    /// Read from disk with the pack, because the field's answer has to be the
    /// launch's answer, and the launch uses the measurement wherever there is
    /// one.
    /// </summary>
    public MeasuredMemoryProfile MeasuredMemory { get; private set; } = MeasuredMemoryProfile.Unknown;

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
            MeasuredMemory = MeasuredMemoryProfile.Unknown;
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
        MeasuredMemory = _measuredMemory.Recall(
            relativePath, VideoMemoryProfile.Measure(), MemorySizingService.GetInstalledMemoryGb());

        return PackMemory;
    }

    /// <summary>
    /// Puts the field in step with the pack that is now selected: back to the
    /// number the player last set on this pack, or to what the launcher makes
    /// of its weight if they never set one. Returns true when the number moved.
    /// </summary>
    public bool ApplyPackMemory(AppSettings settings)
    {
        var video = VideoMemoryProfile.Measure();
        var remembered = RememberedMemoryGb(settings);
        var wanted = remembered
            ?? MemorySizingService.GetRecommendedDefaultMemoryGb(PackMemory, video, MeasuredMemory);
        if (settings.MaxMemoryGb == wanted) return false;

        _logger?.Info(
            $"Memory for this pack ({DescribePack()}): {wanted} GB for the game, " +
            $"of which {MemorySizingService.GetHeapGb(PackMemory, wanted, video, MeasuredMemory)} GB is the Java heap" +
            (remembered is null ? "." : ", which is what was set here last."));
        settings.MaxMemoryGb = wanted;
        Save(settings);
        return true;
    }

    /// <summary>
    /// The number this pack was last set to by hand, held to what the machine
    /// allows, or null where it never was. A remembered number outlives the
    /// machine it was chosen on, so a file carried to a smaller one still comes
    /// back to a number that fits.
    /// </summary>
    public static int? RememberedMemoryGb(AppSettings settings)
    {
        var pack = PackKey(settings);
        return pack.Length != 0 && settings.MemoryByPack.TryGetValue(pack, out var stored)
            ? MemorySizingService.ClampMemoryGb(stored)
            : null;
    }

    /// <summary>
    /// Writes down what the player set the field to for the pack in front of
    /// them. This happens on the edit rather than on the launch: a number
    /// chosen for a pack they then thought better of playing is still theirs
    /// the next time they come back to it.
    /// </summary>
    public void RememberMemoryForPack(AppSettings settings, int memoryGb)
    {
        var pack = PackKey(settings);
        if (pack.Length == 0) return;
        settings.MemoryByPack[pack] = MemorySizingService.ClampMemoryGb(memoryGb);
    }

    private static string PackKey(AppSettings settings) => settings.ClientRelativePath?.Trim() ?? "";

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
                    pack, settings.MaxMemoryGb, VideoMemoryProfile.Measure(), MeasuredMemory);
                _logger?.Info(
                    $"Memory setting carried across: {settings.MaxMemoryGb} GB of heap becomes {budget} GB for the whole game.");
                settings.MaxMemoryGb = budget;
            }
            settings.MemorySettingIsWholeGame = true;
            settings = ApplyFallbacks(
                settings, pack, MeasuredMemory, useRecommendedMemory: !hasConfiguredMemory);
            // Whose number is it, and which pack was it for? Up to schema 12
            // there was one answer for every pack, kept under a flag; a file
            // written then is read once and its number becomes the answer for
            // the pack that was selected when it was written, which is the only
            // pack it can honestly have been about. Where even the flag is
            // missing the file is older still and answers by arithmetic: a
            // number the launcher would have suggested is the launcher's, and
            // anything else was typed by hand.
            if (hasConfiguredMemory && !HasJsonProperty(json, "memoryByPack"))
            {
                var typedByHand = HasJsonProperty(json, "memoryChosenByPlayer")
                    ? ReadBoolean(json, "memoryChosenByPlayer")
                    : settings.MaxMemoryGb !=
                      MemorySizingService.GetRecommendedDefaultMemoryGb(PackMemoryProfile.Unknown);
                if (typedByHand) RememberMemoryForPack(settings, settings.MaxMemoryGb);
            }
            ApplyPackMemory(settings);
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
        settings = ApplyFallbacks(settings, PackMemory, MeasuredMemory);
        AtomicFile.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(settings, _options));
    }

    private static AppSettings ApplyFallbacks(
        AppSettings? source,
        PackMemoryProfile pack,
        MeasuredMemoryProfile measured = default,
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
            settings.MaxMemoryGb = MemorySizingService.GetRecommendedDefaultMemoryGb(
                pack, VideoMemoryProfile.Measure(), measured);
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

        // Held to the same limit as the box in the window, one pack at a time,
        // and case-insensitively keyed however the file spelled it: these are
        // folder names, and Windows does not think two spellings of one folder
        // are two folders.
        var byPack = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, memoryGb) in settings.MemoryByPack ?? [])
        {
            var key = name?.Trim() ?? "";
            if (key.Length == 0) continue;
            byPack[key] = MemorySizingService.ClampMemoryGb(memoryGb);
        }
        settings.MemoryByPack = byPack;

        settings.SkinPath = settings.SkinPath?.Trim() ?? "";
        settings.SelectedWorldRelativePath = settings.SelectedWorldRelativePath?.Trim() ?? "";

        return settings;
    }

    private AppSettings CreateSafeDefaults()
    {
        return ApplyFallbacks(new AppSettings(), PackMemory, MeasuredMemory, useRecommendedMemory: true);
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

    private static bool ReadBoolean(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.EnumerateObject().Any(property =>
                   string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                   property.Value.ValueKind == JsonValueKind.True);
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
