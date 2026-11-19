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
            PackMemory = PackMemoryProfile.Measure(
                _paths.CombineUnderPacks(relativePath), _paths.PackWeightsFile);
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
    /// Takes a weight worked out somewhere other than a pack folder - the
    /// manifest of a pack that is offered but not installed - and treats it as
    /// this pack's, so the estimate stops guessing from the size of the machine.
    /// </summary>
    /// <returns>False for a weight that says nothing, which changes nothing.</returns>
    public bool UsePackMemory(PackMemoryProfile profile)
    {
        // Never over an answer read from a real folder: that one counted the
        // mods inside the jars and this one could not.
        if (!profile.IsKnown || PackMemory.IsKnown) return false;
        PackMemory = profile;
        return true;
    }

    /// <summary>
    /// Reads a settings file written while the number meant the whole of the
    /// game and turns every number in it into the heap that number was already
    /// producing: the top-level one by the pack that is selected, and each
    /// remembered pack by its own weight, because a pack's room beside the heap
    /// is its own. Nobody's game changes size on the launch that changes the
    /// meaning - only the number they are shown.
    /// </summary>
    private void CarryBudgetsAcrossToHeaps(AppSettings settings, PackMemoryProfile pack)
    {
        var video = VideoMemoryProfile.Measure();
        var installedGb = MemorySizingService.GetInstalledMemoryGb();

        var heapGb = MemorySizingService.GetHeapForBudgetGb(pack, settings.MaxHeapGb, video, MeasuredMemory);
        _logger?.Info(
            $"Memory setting carried across: {settings.MaxHeapGb} GB for the whole game becomes a {heapGb} GB " +
            "Java heap, which is the -Xmx that number was already producing.");
        settings.MaxHeapGb = heapGb;

        var byPack = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, budgetGb) in settings.MemoryByPack ?? [])
        {
            var key = name?.Trim() ?? "";
            if (key.Length == 0) continue;
            byPack[key] = MemorySizingService.GetHeapForBudgetGb(
                WeighPack(key), budgetGb, video, _measuredMemory.Recall(key, video, installedGb));
        }
        settings.MemoryByPack = byPack;
    }

    /// <summary>What a pack folder weighs, without disturbing the selected one.</summary>
    private PackMemoryProfile WeighPack(string relativePath)
    {
        try
        {
            return PackMemoryProfile.Measure(
                _paths.CombineUnderPacks(relativePath), _paths.PackWeightsFile);
        }
        catch (InvalidOperationException)
        {
            return PackMemoryProfile.Unknown;
        }
    }

    public AppSettings Load()
    {
        // Migration failures must reach startup, before the generic settings
        // recovery can replace the player's selection with safe defaults.
        new TerraFirmaPackMigrationService(_paths, _logger).Migrate();
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
            RestoreLocalLl8Visibility(settings);
            PackMembership.Initialize(settings, _paths);
            if (!settings.MemoryIsGlobal && hasConfiguredMemory &&
                !HasJsonProperty(json, "memorySettingIsTheHeap") &&
                ReadBoolean(json, "memorySettingIsWholeGame"))
            {
                CarryBudgetsAcrossToHeaps(settings, MeasurePack(settings.ClientRelativePath));
            }
            settings.MaxHeapGb = ReadGlobalHeapGb(settings, json, hasConfiguredMemory);
            ApplyFallbacks(settings);
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
        // A new settings object may already hold an explicit "remove from list"
        // choice. Only loading an old on-disk file is allowed to undo 408's hide.
        settings.Ll8LocalVisibilityMigrated = true;
        PackMembership.Initialize(settings, _paths);
        ApplyFallbacks(settings);
        AtomicFile.WriteAllTextIfChanged(_paths.SettingsFile, JsonSerializer.Serialize(settings, _options));
    }

    private static int ReadGlobalHeapGb(AppSettings settings, string json, bool hasConfiguredMemory)
    {
        if (settings.MemoryIsGlobal && hasConfiguredMemory) return settings.MaxHeapGb;
        if (HasJsonProperty(json, "memoryByPack"))
        {
            var selectedPack = settings.ClientRelativePath?.Trim();
            foreach (var (pack, memory) in settings.MemoryByPack ?? [])
            {
                if (string.Equals(pack.Trim(), selectedPack, StringComparison.OrdinalIgnoreCase)) return memory;
            }
            return MemorySizingService.GetDefaultHeapGb();
        }
        return hasConfiguredMemory && (!HasJsonProperty(json, "memoryChosenByPlayer") ||
                                       ReadBoolean(json, "memoryChosenByPlayer"))
            ? settings.MaxHeapGb
            : MemorySizingService.GetDefaultHeapGb();
    }

    private static void ApplyFallbacks(AppSettings settings)
    {
        settings.SchemaVersion = CurrentSchemaVersion;
        // A save always writes the current meaning, including the very first
        // defaults file. Leaving this false made the next launch rewrite it.
        settings.MemorySettingIsTheHeap = true;
        settings.MemoryIsGlobal = true;
        settings.PlayerName = settings.PlayerName?.Trim() ?? "";
        settings.PreviousPlayerName = settings.PreviousPlayerName?.Trim() ?? "";

        settings.LocalIdentityId = settings.LocalIdentityId?.Trim() ?? "";
        settings.LocalIdentityName = settings.LocalIdentityName?.Trim() ?? "";
        settings.MaxHeapGb = settings.MaxHeapGb < MemorySizingService.MinHeapGb
            ? MemorySizingService.GetDefaultHeapGb()
            : MemorySizingService.ClampHeapGb(settings.MaxHeapGb);

        settings.ClientRelativePath = settings.ClientRelativePath?.Trim() ?? "";

        // Keep historical values for the pack migration and rollback paths.
        var byPack = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, memoryGb) in settings.MemoryByPack ?? [])
        {
            var key = name?.Trim() ?? "";
            if (key.Length == 0) continue;
            byPack[key] = Math.Clamp(memoryGb, MemorySizingService.MinHeapGb, MemorySizingService.MaxHeapGb);
        }
        settings.MemoryByPack = byPack;

        settings.SkinPath = settings.SkinPath?.Trim() ?? "";
        settings.SelectedWorldRelativePath = settings.SelectedWorldRelativePath?.Trim() ?? "";

    }

    private void RestoreLocalLl8Visibility(AppSettings settings)
    {
        if (settings.Ll8LocalVisibilityMigrated) return;
        if (MinecraftProcessService.HasPackData(_paths.CombineUnderPacks("LL8 Extended")))
            settings.HiddenPackRelativePaths?.RemoveAll(path =>
                string.Equals(path, "LL8 Extended", StringComparison.OrdinalIgnoreCase));
        settings.Ll8LocalVisibilityMigrated = true;
    }

    private AppSettings CreateSafeDefaults()
    {
        var settings = new AppSettings
        {
            Ll8LocalVisibilityMigrated = true,
            MaxHeapGb = MemorySizingService.GetDefaultHeapGb()
        };
        ApplyFallbacks(settings);
        PackMembership.Initialize(settings, _paths);
        return settings;
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
