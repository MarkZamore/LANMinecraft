using System.IO;

namespace Minecraft;

public sealed class AppPaths
{
    private string? _legacyPackRuntimeRoot;

    public static string ResolveApplicationRoot()
    {
        // The native host canonicalizes its guarded installation directory.
        // Use that spelling even when Explorer/shortcuts started an 8.3 alias.
        // Test-only component fixtures must never replace the real app root.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BootstrapLaunchContext.EnvironmentVariable)))
            return BootstrapLaunchContext.Current.InstallationRoot;
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
        Packs = CombineUnderService(LauncherStorageLayout.Packs);
        Launcher = CombineUnderService(LauncherStorageLayout.Launcher);
        CanonicalPackRuntimes = Path.Combine(Launcher, LauncherStorageLayout.PackRuntimes);
        LegacyPackRuntimes = Path.Combine(Launcher, LauncherStorageLayout.LegacyPackRuntimes);
        // A pre-schema installation can still be read by tools, or used by
        // the game while its runtime import is waiting for a safe startup.
        if (LauncherStorageLayout.ReadSchema(this) != LauncherStorageSchema.Current &&
            MaintenancePath.TryRead(this, LegacyPackRuntimes, out var attributes) && attributes.HasFlag(FileAttributes.Directory))
            UseLegacyPackRuntimes();
        JavaRuntimes = Path.Combine(Launcher, LauncherStorageLayout.JavaRuntimes);
        SharedRuntime = Path.Combine(Launcher, LauncherStorageLayout.Shared);
        SteamNative = Path.Combine(Launcher, LauncherStorageLayout.Steam);
        Personal = CombineUnderService(LauncherStorageLayout.Personal);
        Instances = Path.Combine(Personal, LauncherStorageLayout.Instances);
        PackConflicts = Path.Combine(Personal, LauncherStorageLayout.PackConflicts);
        Worlds = CombineUnderService(LauncherStorageLayout.Worlds);
    }

    public string Root { get; }
    public string Service { get; }
    public string Program { get; }
    public string Packs { get; }
    public string Launcher { get; }
    public string Runtimes => _legacyPackRuntimeRoot ?? CanonicalPackRuntimes;
    internal string CanonicalPackRuntimes { get; }
    internal string LegacyPackRuntimes { get; }

    internal void UseCanonicalPackRuntimes() => _legacyPackRuntimeRoot = null;
    internal void UseLegacyPackRuntimes() => _legacyPackRuntimeRoot = LegacyPackRuntimes;

    internal bool HasAlternatePackRuntimeRoot =>
        _legacyPackRuntimeRoot is not null && Directory.Exists(CanonicalPackRuntimes);

    internal IEnumerable<string> PackRuntimeRoots
    {
        get
        {
            yield return Runtimes;
            if (HasAlternatePackRuntimeRoot) yield return CanonicalPackRuntimes;
        }
    }

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
    public string SettingsFile => Path.Combine(Personal, LauncherStorageLayout.Settings);
    public string PlaytimeFile => Path.Combine(Personal, LauncherStorageLayout.Playtime);
    /// <summary>Copies of per-world documents taken before this build first rewrites them.</summary>
    public string IdentityBackups => Path.Combine(Personal, LauncherStorageLayout.Backups, "Identity");
    public string PackHashesFile => Path.Combine(Personal, LauncherStorageLayout.PackHashes);

    /// <summary>What each pack was last counted as carrying inside its own
    /// jars; see <see cref="PackWeightCache"/>.</summary>
    public string PackWeightsFile => Path.Combine(Personal, LauncherStorageLayout.PackWeights);
    public string WindowPlacementFile => Path.Combine(Personal, LauncherStorageLayout.WindowPlacement);
    public string MinecraftWindowPlacementFile => Path.Combine(Personal, LauncherStorageLayout.MinecraftWindowPlacement);
    public string SkinRegistryFile => Path.Combine(Personal, LauncherStorageLayout.SkinProfiles);
    public string LegacySkinRegistryFile => Path.Combine(Personal, LauncherStorageLayout.LegacySkinProfiles);
    /// <summary>Which player each in-game name belongs to, for the adapter.</summary>
    public string IdentityRegistryFile => Path.Combine(Personal, LauncherStorageLayout.IdentityProfiles);
    public string WaypointSyncStateFile => Path.Combine(Personal, LauncherStorageLayout.WaypointSync);
    public string WaypointConflicts => Path.Combine(Personal, LauncherStorageLayout.WaypointConflicts);
    /// <summary>
    /// Journals for interrupted player-profile writes. It sits beside Personal
    /// rather than under Personal\Temp because startup cleanup wipes Temp,
    /// which used to delete every journal before recovery could read it.
    /// </summary>
    public string ProfileTransactions => Path.Combine(Personal, LauncherStorageLayout.ProfileTransactions);

    /// <summary>Bug reports received from friends, one directory per report.</summary>
    public string BugReports => Path.Combine(Personal, LauncherStorageLayout.BugReports);
    public string Logs => Path.Combine(Personal, LauncherStorageLayout.Logs);
    public string LogFile => Path.Combine(Logs, LauncherStorageLayout.Log);

    public void Ensure()
    {
        Directory.CreateDirectory(Service);
        Directory.CreateDirectory(Packs);
        Directory.CreateDirectory(Launcher);
        Directory.CreateDirectory(Runtimes);
        Directory.CreateDirectory(JavaRuntimes);
        Directory.CreateDirectory(Personal);
        using (var parents = new GuardedFileSystem.DirectoryLocks())
        {
            if (!parents.HoldAncestors(Personal)) throw new IOException("Invalid personal storage directory.");
            Directory.CreateDirectory(Logs);
            if (!parents.Hold(Logs)) throw new IOException("Invalid launcher logs directory.");
        }
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
        if (!HasAlternatePackRuntimeRoot) return full;
        var relative = Path.GetRelativePath(Runtimes, full);
        var pack = relative.Split(Path.DirectorySeparatorChar)[0];
        if (UsesExistingAlternatePackRuntime(pack)) return Path.Combine(CanonicalPackRuntimes, relative);
        return full;
    }

    // Receipts are relative to Launcher and may predate the folder rename.
    // Keep the original spelling while that pack still uses the original root.
    internal string ResolvePackRuntimeReceiptPrefix(string relativePath)
    {
        var slash = relativePath.IndexOfAny(['/', '\\']);
        if (slash <= 0 ||
            !relativePath.AsSpan(0, slash).Equals(LauncherStorageLayout.LegacyPackRuntimes, StringComparison.OrdinalIgnoreCase))
            return relativePath;
        if (Runtimes == CanonicalPackRuntimes)
            return LauncherStorageLayout.PackRuntimes + relativePath[slash..];
        var packEnd = relativePath.IndexOfAny(['/', '\\'], slash + 1);
        if (packEnd > slash && HasAlternatePackRuntimeRoot &&
            UsesExistingAlternatePackRuntime(relativePath[(slash + 1)..packEnd]))
            return LauncherStorageLayout.PackRuntimes + relativePath[slash..];
        return relativePath;
    }

    private bool UsesExistingAlternatePackRuntime(string pack)
    {
        if (!PackCatalogService.ValidFolder(pack)) return false;
        var primary = Path.Combine(Runtimes, pack);
        var alternate = Path.Combine(CanonicalPackRuntimes, pack);
        return !Directory.Exists(primary) && !File.Exists(primary) &&
            MaintenancePath.TryRead(this, alternate, out var attributes) && attributes.HasFlag(FileAttributes.Directory);
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
