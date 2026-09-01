using System.IO;

namespace Minecraft;

public sealed class AppPaths
{
    public static string ResolveApplicationRoot()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                return processDirectory;
            }
        }

        return AppContext.BaseDirectory;
    }

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        var driveRoot = Path.GetPathRoot(Root);
        if (string.Equals(Root.TrimEnd('\\'), driveRoot?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Application root cannot be a drive root.");
        }

        Service = CombineUnderRoot("Minecraft");
        Program = CombineUnderRoot("Program");
        Packs = CombineUnderService("Packs");
        Launcher = CombineUnderService("Launcher");
        Runtimes = Path.Combine(Launcher, "Runtimes");
        JavaRuntimes = Path.Combine(Launcher, "JavaRuntimes");
        SharedRuntime = Path.Combine(Launcher, "Shared");
        SteamNative = Path.Combine(Launcher, "Steam");
        Personal = CombineUnderService("Personal");
        Instances = Path.Combine(Personal, "Instances");
        PackConflicts = Path.Combine(Personal, "PackConflicts");
        Worlds = CombineUnderService("Worlds");
    }

    public string Root { get; }
    public string Service { get; }
    public string Program { get; }
    public string Packs { get; }
    public string Launcher { get; }
    public string Runtimes { get; }

    /// <summary>
    /// Where Java lives, once, for every pack.
    ///
    /// It used to live under each pack's own runtime folder, which meant a
    /// machine with four packs held four copies of the same three hundred
    /// megabyte JDK and downloaded a fifth for the next pack. Nothing about
    /// a Java install belongs to a pack: two packs on the same feature
    /// release want the same bytes, and the folder is named for the release
    /// rather than for whoever asked first.
    /// </summary>
    public string JavaRuntimes { get; }

    /// <summary>
    /// The game itself, once, for every build: Mojang's assets, the libraries,
    /// the version and loader profiles, and Mojang's own Java.
    ///
    /// It used to live under each build's runtime folder, which meant three
    /// builds on the same Minecraft held three copies of the same eight hundred
    /// megabytes of assets and downloaded a fourth for the next one. None of it
    /// belongs to a build: Mojang's asset store is addressed by the hash of its
    /// contents, libraries sit at a path made of their own version, and a
    /// version id names exactly one set of files. Two builds asking for the
    /// same thing want the same bytes.
    /// </summary>
    public string SharedRuntime { get; }
    public string SteamNative { get; }
    public string Personal { get; }
    public string Instances { get; }
    public string PackConflicts { get; }
    public string Worlds { get; }
    public string SettingsFile => Path.Combine(Personal, "settings.json");
    /// <summary>Copies of per-world documents taken before this build first rewrites them.</summary>
    public string IdentityBackups => Path.Combine(Personal, "Backups", "Identity");
    public string PackHashesFile => Path.Combine(Personal, "pack-hashes.json");
    public string WindowPlacementFile => Path.Combine(Personal, "window-placement.json");
    public string MinecraftWindowPlacementFile => Path.Combine(Personal, "minecraft-window-placement.json");
    public string SkinRegistryFile => Path.Combine(Personal, "skin-profiles.properties");
    /// <summary>Which player each in-game name belongs to, for the adapter.</summary>
    public string IdentityRegistryFile => Path.Combine(Personal, "identity-profiles.properties");
    public string WaypointSyncStateFile => Path.Combine(Personal, "waypoint-sync.json");
    public string WaypointConflicts => Path.Combine(Personal, "WaypointConflicts");
    /// <summary>
    /// Journals for interrupted player-profile writes. It sits beside Personal
    /// rather than under Personal\Temp because startup cleanup wipes Temp,
    /// which used to delete every journal before recovery could read it.
    /// </summary>
    public string ProfileTransactions => Path.Combine(Personal, "ProfileTransactions");

    /// <summary>Bug reports received from friends, one directory per report.</summary>
    public string BugReports => Path.Combine(Personal, "BugReports");
    public string LogFile => Path.Combine(Personal, "logs.log");

    public void Ensure()
    {
        Directory.CreateDirectory(Service);
        Directory.CreateDirectory(Packs);
        Directory.CreateDirectory(Launcher);
        Directory.CreateDirectory(Runtimes);
        Directory.CreateDirectory(JavaRuntimes);
        Directory.CreateDirectory(Personal);
        Directory.CreateDirectory(Instances);
        Directory.CreateDirectory(Worlds);
        Directory.CreateDirectory(BugReports);
        Directory.CreateDirectory(ProfileTransactions);
    }

    public string CombineUnderRoot(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        EnsureUnderRoot(full);
        return full;
    }

    public string CombineUnderService(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Service, relativePath));
        EnsureUnderRoot(full);
        return full;
    }

    public string CombineUnderPacks(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Packs, relativePath));
        EnsureUnderDirectory(full, Packs);
        return full;
    }

    public string CombineUnderInstances(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Instances, relativePath));
        EnsureUnderDirectory(full, Instances);
        return full;
    }

    public string CombineUnderRuntimes(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Runtimes, relativePath));
        EnsureUnderDirectory(full, Runtimes);
        return full;
    }

    public void EnsureUnderRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var rootWithSlash = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes portable root: {path}");
        }
    }

    private static void EnsureUnderDirectory(string path, string parent)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var basePath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(full, basePath, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes portable directory: {path}");
        }
    }
}
