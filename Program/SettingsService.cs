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
            ?? MemorySizingService.GetRecommendedMemoryGb(PackMemory, video, MeasuredMemory);
        if (settings.MaxHeapGb == wanted) return false;

        _logger?.Info(
            $"Memory for this pack ({DescribePack()}): a {wanted} GB Java heap, and about " +
            $"{MemorySizingService.GetNativeReserveGb(PackMemory, video, MeasuredMemory)} GB more beside it" +
            (remembered is null ? "." : ", which is what was set here last."));
        settings.MaxHeapGb = wanted;
        Save(settings);
        return true;
    }

    /// <summary>
    /// The number this pack was last set to by hand, held to what the machine
    /// allows, or null where it never was. A remembered number outlives the
    /// machine it was chosen on, so a file carried to a smaller one still comes
    /// back to a number that fits.
    /// </summary>
    public int? RememberedMemoryGb(AppSettings settings)
    {
        var pack = PackKey(settings);
        return pack.Length != 0 && settings.MemoryByPack.TryGetValue(pack, out var stored)
            ? MemorySizingService.ClampHeapGb(stored, PackMemory, VideoMemoryProfile.Measure(), MeasuredMemory)
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
        settings.MemoryByPack[pack] =
            MemorySizingService.ClampHeapGb(memoryGb, PackMemory, VideoMemoryProfile.Measure(), MeasuredMemory);
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
            return PackMemoryProfile.Measure(_paths.CombineUnderPacks(relativePath));
        }
        catch (InvalidOperationException)
        {
            return PackMemoryProfile.Unknown;
        }
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
            // The number has meant two things and now means the first of them
            // again. It was the Java heap; for a while it was everything the
            // game may take, marked by memorySettingIsWholeGame; and it is the
            // heap once more. A file from the middle of that is read through the
            // arithmetic it was written under, so the heap it was already
            // producing is the heap it keeps - top-level number and every
            // remembered pack alike, each by its own pack's weight. A file from
            // before it has a number that was always a heap, and is left alone.
            if (hasConfiguredMemory &&
                !HasJsonProperty(json, "memorySettingIsTheHeap") &&
                HasJsonProperty(json, "memorySettingIsWholeGame"))
            {
                CarryBudgetsAcrossToHeaps(settings, pack);
            }
            settings.MemorySettingIsTheHeap = true;
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
                    : settings.MaxHeapGb !=
                      MemorySizingService.GetRecommendedMemoryGb(PackMemoryProfile.Unknown);
                if (typedByHand) RememberMemoryForPack(settings, settings.MaxHeapGb);
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
            settings.MaxHeapGb < MemorySizingService.MinHeapGb ||
            settings.MaxHeapGb > MemorySizingService.MaxHeapGb)
        {
            settings.MaxHeapGb = MemorySizingService.GetRecommendedMemoryGb(
                pack, VideoMemoryProfile.Measure(), measured);
        }
        else
        {
            // Held to what this machine will leave this pack, not merely to what
            // a number may be. The box in the window has always refused more
            // than the machine can spare; the file did not, so a number written
            // into it by hand went straight to -Xmx and the two disagreed about
            // the same setting. A file is not a way around the window.
            settings.MaxHeapGb = MemorySizingService.ClampHeapGb(
                settings.MaxHeapGb, pack, VideoMemoryProfile.Measure(), measured);
        }

        settings.ClientRelativePath = settings.ClientRelativePath?.Trim() ?? "";

        // Case-insensitively keyed however the file spelled it: these are folder
        // names, and Windows does not think two spellings of one folder are two
        // folders. Held only to what a heap may be, not to what this machine
        // leaves any one pack - the ceiling is the selected pack's now, and
        // cutting pack A's number with pack B's reserve would be worse than
        // leaving it be. It meets the real ceiling when it is put in the field.
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
