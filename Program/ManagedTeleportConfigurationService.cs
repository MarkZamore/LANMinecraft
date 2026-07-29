using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed record ManagedTeleportConfigurationResult(
    IReadOnlyList<string> ChangedPaths);

/// <summary>
/// Applies the launcher-owned teleport command policy to a prepared instance.
/// This service must run after the managed FTB Essentials component is installed
/// and before Minecraft starts.
/// </summary>
public sealed class ManagedTeleportConfigurationService
{
    public const string CommandScriptRelativePath =
        "kubejs/server_scripts/portable/portable_teleport_commands.js";

    private const string CommandScriptResourceName =
        "Minecraft.PortableTeleportCommands.js";
    private const string FtbConfigRelativePath =
        "config/ftbessentials.snbt";
    private const string FtbDefaultConfigRelativePath =
        "defaultconfigs/ftbessentials-server.snbt";
    private const string FtbWorldConfigRelativePath =
        "serverconfig/ftbessentials-server.snbt";

    private static readonly object ApplyGate = new();
    private static readonly Regex ObjectStartPattern = new(
        @"^\s*(?<key>[A-Za-z0-9_.-]+)\s*:\s*\{\s*,?\s*(?:#.*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ObjectEndPattern = new(
        @"^\s*}\s*,?\s*(?:#.*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ScalarPattern = new(
        @"^(?<prefix>\s*(?<key>[A-Za-z0-9_.-]+)\s*:\s*)" +
        @"(?<value>true|false|-?[0-9]+)(?<suffix>\s*,?\s*(?:#.*)?)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex FlatSettingPattern = new(
        @"^(?<indent>\s*)(?<key>[A-Za-z0-9_.-]+)\s*[:=].*$",
        RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> FtbValues =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["register_alias_as_well_as_namespace"] = "false",
                ["register_to_namespace"] = "false",
                ["admin.extinguish.enabled"] = "false",
                ["admin.feed.enabled"] = "false",
                ["admin.fly.enabled"] = "false",
                ["admin.god.enabled"] = "false",
                ["admin.heal.enabled"] = "false",
                ["admin.invsee.enabled"] = "false",
                ["admin.kit.enabled"] = "false",
                ["admin.mute.enabled"] = "false",
                ["admin.speed.enabled"] = "false",
                ["admin.tp_offline.enabled"] = "false",
                ["integration.team_bases_spawn_override"] = "false",
                ["misc.anvil.enabled"] = "false",
                ["misc.crafting.enabled"] = "false",
                ["misc.enderchest.enabled"] = "false",
                ["misc.hat.enabled"] = "false",
                ["misc.kickme.enabled"] = "false",
                ["misc.leaderboard.enabled"] = "false",
                ["misc.near.enabled"] = "false",
                ["misc.nick.enabled"] = "false",
                ["misc.rec.enabled"] = "false",
                ["misc.smithing.enabled"] = "false",
                ["misc.stonecutter.enabled"] = "false",
                ["misc.trashcan.enabled"] = "false",
                ["teleportation.back.enabled"] = "false",
                ["teleportation.home.cooldown"] = "10",
                ["teleportation.home.enabled"] = "true",
                ["teleportation.home.max"] = "1",
                ["teleportation.home.warmup"] = "0",
                ["teleportation.jump.enabled"] = "false",
                ["teleportation.playerspawn.enabled"] = "false",
                ["teleportation.rtp.enabled"] = "false",
                ["teleportation.spawn.cooldown"] = "10",
                ["teleportation.spawn.enabled"] = "true",
                ["teleportation.spawn.warmup"] = "0",
                ["teleportation.tpa.cooldown"] = "10",
                ["teleportation.tpa.enabled"] = "true",
                ["teleportation.tpa.warmup"] = "0",
                ["teleportation.tpl.enabled"] = "false",
                ["teleportation.tpx.enabled"] = "true",
                ["teleportation.warp.enabled"] = "false"
            });

    private static readonly IReadOnlyDictionary<string, string> XaeroProfileValues =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["default_waypoint_teleport_format"] =
                    "/portable_waypoint_tp {x} {y} {z}",
                ["default_waypoint_teleport_rotation_format"] =
                    "/portable_waypoint_tp {x} {y} {z} {yaw}",
                ["waypoint_teleport_cross_dimension"] = "true",
                ["waypoint_partial_y_teleport"] = "true"
            });

    private static readonly IReadOnlyDictionary<string, string> XaeroWorldValues =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["teleportationEnabled"] = "true",
                ["usingDefaultTeleportCommand"] = "true"
            });

    public ManagedTeleportConfigurationResult Apply(
        string gameDirectory,
        string? worldsRoot = null,
        string? worldBuildRelativePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        lock (ApplyGate)
        {
            return ApplyCore(
                gameDirectory,
                worldsRoot,
                worldBuildRelativePath);
        }
    }

    internal static string RewriteFtbEssentialsConfig(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var path = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new StringBuilder(contents.Length);

        foreach (var line in EnumerateLines(contents))
        {
            var content = line.Content;
            var objectStart = ObjectStartPattern.Match(content);
            if (objectStart.Success)
            {
                path.Add(objectStart.Groups["key"].Value);
                output.Append(content).Append(line.Terminator);
                continue;
            }

            if (ObjectEndPattern.IsMatch(content))
            {
                if (path.Count > 0) path.RemoveAt(path.Count - 1);
                output.Append(content).Append(line.Terminator);
                continue;
            }

            var scalar = ScalarPattern.Match(content);
            if (!scalar.Success)
            {
                output.Append(content).Append(line.Terminator);
                continue;
            }

            var key = scalar.Groups["key"].Value;
            var fullPath = path.Count == 0
                ? key
                : string.Join(".", path) + "." + key;
            if (!FtbValues.TryGetValue(fullPath, out var replacement))
            {
                output.Append(content).Append(line.Terminator);
                continue;
            }
            if (!seen.Add(fullPath))
            {
                throw new InvalidDataException(
                    $"FTB Essentials setting is duplicated: {fullPath}");
            }

            output
                .Append(scalar.Groups["prefix"].Value)
                .Append(replacement)
                .Append(scalar.Groups["suffix"].Value)
                .Append(line.Terminator);
        }

        var missing = FtbValues.Keys.Where(key => !seen.Contains(key)).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException(
                "The installed FTB Essentials configuration schema is incompatible. " +
                "Missing settings: " +
                string.Join(", ", missing));
        }
        return output.ToString();
    }

    internal static string RewriteXaeroProfile(string contents) =>
        RewriteFlatConfig(contents, XaeroProfileValues, " = ");

    internal static string RewriteXaeroWorldConfig(string contents) =>
        RewriteFlatConfig(contents, XaeroWorldValues, ":");

    private static ManagedTeleportConfigurationResult ApplyCore(
        string gameDirectory,
        string? worldsRoot,
        string? worldBuildRelativePath)
    {
        var gameRoot = Path.GetFullPath(gameDirectory);
        EnsureOrdinaryDirectory(gameRoot, createIfMissing: false);

        var worldRoot = string.IsNullOrWhiteSpace(worldsRoot)
            ? null
            : Path.GetFullPath(worldsRoot);
        if (worldRoot is not null && Directory.Exists(worldRoot))
        {
            EnsureOrdinaryDirectory(worldRoot, createIfMissing: false);
        }

        var planned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ftbConfigPath = UnderRoot(gameRoot, FtbConfigRelativePath);
        EnsureNoReparsePointInExistingPath(ftbConfigPath, gameRoot);
        EnsureOrdinaryFile(ftbConfigPath, required: true);
        var patchedFtbConfig = RewriteFtbEssentialsConfig(
            File.ReadAllText(ftbConfigPath));
        Plan(planned, ftbConfigPath, patchedFtbConfig);

        var defaultConfigPath = UnderRoot(gameRoot, FtbDefaultConfigRelativePath);
        EnsureNoReparsePointInExistingPath(defaultConfigPath, gameRoot);
        var patchedDefaultConfig = File.Exists(defaultConfigPath)
            ? RewriteExistingFtbConfig(defaultConfigPath)
            : patchedFtbConfig;
        Plan(planned, defaultConfigPath, patchedDefaultConfig);

        if (worldRoot is not null &&
            Directory.Exists(worldRoot) &&
            !string.IsNullOrWhiteSpace(worldBuildRelativePath))
        {
            var worldMetadata = new WorldMetadataService();
            foreach (var worldDirectory in EnumerateWorldDirectories(worldRoot))
            {
                var metadataPath = UnderRoot(
                    worldDirectory,
                    WorldMetadataService.MetadataFileName);
                EnsureNoReparsePointInExistingPath(
                    metadataPath,
                    worldDirectory);
                EnsureOrdinaryFile(metadataPath, required: false);
                var metadata = worldMetadata.Read(worldDirectory);
                if (!IsSamePackRelativePath(
                        metadata?.BuildRelativePath,
                        worldBuildRelativePath))
                {
                    continue;
                }

                var worldConfigPath = UnderRoot(
                    worldDirectory,
                    FtbWorldConfigRelativePath);
                EnsureNoReparsePointInExistingPath(
                    worldConfigPath,
                    worldDirectory);
                var hasWorldMarker =
                    File.Exists(Path.Combine(worldDirectory, "level.dat")) ||
                    File.Exists(Path.Combine(worldDirectory, "level.dat_old")) ||
                    File.Exists(worldConfigPath);
                if (!hasWorldMarker) continue;

                var worldConfig = File.Exists(worldConfigPath)
                    ? RewriteExistingFtbConfig(worldConfigPath)
                    : patchedDefaultConfig;
                Plan(planned, worldConfigPath, worldConfig);
            }
        }

        var scriptPath = UnderRoot(gameRoot, CommandScriptRelativePath);
        Plan(planned, scriptPath, ReadCommandScript());
        PlanXaeroProfiles(gameRoot, planned);
        PlanXaeroWorldConfigs(gameRoot, planned);

        foreach (var entry in planned)
        {
            EnsureNoReparsePointInExistingPath(
                entry.Key,
                IsUnderDirectory(entry.Key, gameRoot)
                    ? gameRoot
                    : worldRoot ?? throw new InvalidOperationException(
                        "A planned configuration path has no trusted root."));
            EnsureOrdinaryFile(entry.Key, required: false);
        }

        var changedPaths = new List<string>();
        foreach (var entry in planned)
        {
            if (File.Exists(entry.Key) &&
                string.Equals(
                    File.ReadAllText(entry.Key),
                    entry.Value,
                    StringComparison.Ordinal))
            {
                continue;
            }

            AtomicFile.WriteAllText(entry.Key, entry.Value);
            changedPaths.Add(entry.Key);
        }

        return new ManagedTeleportConfigurationResult(
            new ReadOnlyCollection<string>(changedPaths));
    }

    private static string RewriteExistingFtbConfig(string path)
    {
        EnsureOrdinaryFile(path, required: true);
        return RewriteFtbEssentialsConfig(File.ReadAllText(path));
    }

    private static void PlanXaeroProfiles(
        string gameRoot,
        IDictionary<string, string> planned)
    {
        var profilesRoot = UnderRoot(
            gameRoot,
            "config/xaero/minimap/profiles");
        EnsureNoReparsePointInExistingPath(profilesRoot, gameRoot);
        var profiles = new List<string>();
        if (Directory.Exists(profilesRoot))
        {
            EnsureOrdinaryDirectory(profilesRoot, createIfMissing: false);
            profiles.AddRange(
                Directory.EnumerateFiles(
                    profilesRoot,
                    "*.cfg",
                    SearchOption.TopDirectoryOnly));
        }

        if (profiles.Count == 0)
        {
            profiles.Add(Path.Combine(profilesRoot, "default.cfg"));
        }

        foreach (var profile in profiles)
        {
            EnsureNoReparsePointInExistingPath(profile, gameRoot);
            EnsureOrdinaryFile(profile, required: false);
            var contents = File.Exists(profile)
                ? File.ReadAllText(profile)
                : string.Empty;
            Plan(planned, profile, RewriteXaeroProfile(contents));
        }
    }

    private static void PlanXaeroWorldConfigs(
        string gameRoot,
        IDictionary<string, string> planned)
    {
        var xaeroRoot = UnderRoot(gameRoot, "xaero/minimap");
        EnsureNoReparsePointInExistingPath(xaeroRoot, gameRoot);
        if (!Directory.Exists(xaeroRoot)) return;
        EnsureOrdinaryDirectory(xaeroRoot, createIfMissing: false);

        foreach (var configPath in EnumerateOrdinaryFiles(xaeroRoot, "config.txt"))
        {
            Plan(
                planned,
                configPath,
                RewriteXaeroWorldConfig(File.ReadAllText(configPath)));
        }
    }

    private static string RewriteFlatConfig(
        string contents,
        IReadOnlyDictionary<string, string> desired,
        string separator)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new StringBuilder(contents.Length + 256);
        var newline = DetectNewline(contents);

        foreach (var line in EnumerateLines(contents))
        {
            var match = FlatSettingPattern.Match(line.Content);
            if (!match.Success ||
                !desired.TryGetValue(match.Groups["key"].Value, out var value))
            {
                output.Append(line.Content).Append(line.Terminator);
                continue;
            }

            var key = match.Groups["key"].Value;
            if (!seen.Add(key))
            {
                throw new InvalidDataException(
                    $"Xaero setting is duplicated: {key}");
            }
            output
                .Append(match.Groups["indent"].Value)
                .Append(key)
                .Append(separator)
                .Append(value)
                .Append(line.Terminator);
        }

        var missing = desired.Where(entry => !seen.Contains(entry.Key)).ToArray();
        if (missing.Length == 0) return output.ToString();

        if (output.Length > 0 && !EndsWithNewline(output))
        {
            output.Append(newline);
        }
        foreach (var entry in missing)
        {
            output
                .Append(entry.Key)
                .Append(separator)
                .Append(entry.Value)
                .Append(newline);
        }
        return output.ToString();
    }

    private static IEnumerable<string> EnumerateWorldDirectories(string worldsRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     worldsRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                yield return info.FullName;
            }
        }
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(
        string root,
        string fileName)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         fileName,
                         SearchOption.TopDirectoryOnly))
            {
                EnsureOrdinaryFile(file, required: true);
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(info.FullName);
                }
            }
        }
    }

    private static IEnumerable<TextLine> EnumerateLines(string contents)
    {
        var offset = 0;
        while (offset < contents.Length)
        {
            var end = offset;
            while (end < contents.Length &&
                   contents[end] != '\r' &&
                   contents[end] != '\n')
            {
                end++;
            }

            var terminatorLength = 0;
            if (end < contents.Length)
            {
                terminatorLength =
                    contents[end] == '\r' &&
                    end + 1 < contents.Length &&
                    contents[end + 1] == '\n'
                        ? 2
                        : 1;
            }
            yield return new TextLine(
                contents[offset..end],
                contents.Substring(end, terminatorLength));
            offset = end + terminatorLength;
        }
    }

    private static string DetectNewline(string contents) =>
        contents.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";

    private static bool EndsWithNewline(StringBuilder builder) =>
        builder.Length > 0 &&
        (builder[^1] == '\r' || builder[^1] == '\n');

    private static string ReadCommandScript()
    {
        using var stream = typeof(ManagedTeleportConfigurationService)
            .Assembly
            .GetManifestResourceStream(CommandScriptResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded teleport command script is missing: {CommandScriptResourceName}");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string UnderRoot(string root, string relativePath)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderDirectory(path, root))
        {
            throw new InvalidOperationException(
                $"Managed teleport path escapes its root: {path}");
        }
        return path;
    }

    private static bool IsUnderDirectory(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePackRelativePath(string? left, string? right)
    {
        static string Normalize(string? value) =>
            (value ?? string.Empty)
                .Replace('\\', '/')
                .Trim('/');

        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return normalizedLeft.Length != 0 &&
               normalizedRight.Length != 0 &&
               string.Equals(
                   normalizedLeft,
                   normalizedRight,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureOrdinaryDirectory(
        string path,
        bool createIfMissing)
    {
        if (createIfMissing) Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Managed teleport directory does not exist: {path}");
        }
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"Managed teleport directory cannot be a reparse point: {path}");
        }
    }

    private static void EnsureOrdinaryFile(string path, bool required)
    {
        if (!File.Exists(path))
        {
            if (required)
            {
                throw new FileNotFoundException(
                    "Managed teleport configuration is missing.",
                    path);
            }
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"Managed teleport file cannot be a reparse point: {path}");
        }
    }

    private static void EnsureNoReparsePointInExistingPath(
        string path,
        string root)
    {
        if (!IsUnderDirectory(path, root))
        {
            throw new InvalidOperationException(
                $"Managed teleport path is outside its trusted root: {path}");
        }

        var current = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        while (current is not null && IsUnderDirectory(current, fullRoot))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Managed teleport path contains a reparse point: {current}");
            }
            if (string.Equals(
                current.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                fullRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void Plan(
        IDictionary<string, string> planned,
        string path,
        string contents)
    {
        if (!planned.TryAdd(Path.GetFullPath(path), contents))
        {
            throw new InvalidOperationException(
                $"Managed teleport path was planned more than once: {path}");
        }
    }

    private readonly record struct TextLine(
        string Content,
        string Terminator);
}
