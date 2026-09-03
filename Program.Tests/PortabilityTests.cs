using System.IO;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The launcher is a folder somebody copies to a stick, and nothing it does
/// may leave that folder.
/// </summary>
/// <remarks>
/// This is checked by reading the sources rather than by launching anything,
/// because the leak that prompted it could not be caught any other way: the
/// launcher installs e4steam, e4steam unpacks three Steam native libraries
/// into <c>$user.home/.e4steam-steam-natives</c>, and it reads no property
/// that would move them. Nothing in the launcher's own code was wrong and
/// three DLLs still ended up in the player's profile. The fix is to give the
/// game a home inside the folder; the test is here so that nobody removes it
/// without knowing what it was for.
/// </remarks>
public sealed class PortabilityTests
{
    /// <summary>
    /// Files that may name a path outside the folder, and why. Every one of
    /// these is about cleaning up after somebody else or about reading a path
    /// rather than writing to it.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // The single-file host extracts native libraries to %TEMP%\.net before
        // the launcher's own code runs, and only an environment variable set
        // before the process starts could move it. So the launcher sweeps up
        // instead: this file finds those directories in order to delete them.
        ["LogCleanupService.cs"] = "deletes what the .NET single-file host leaves in %TEMP%",
        // Reads the profile path so it can take it back out of a log before
        // the log is sent to a friend.
        ["SupportLogSanitizer.cs"] = "redacts the profile path out of shared logs",
    };

    private static readonly string[] Escapes =
    [
        "Path.GetTempPath(",
        "Environment.SpecialFolder",
        "Environment.GetFolderPath",
        "Environment.CurrentDirectory",
    ];

    /// <summary>
    /// Files that may name the registry at all, and why. Reading is allowed
    /// where it is the only way to ask the question; writing is allowed nowhere.
    /// </summary>
    private static readonly Dictionary<string, string> RegistryReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // How much memory the video card has is not a question Windows answers
        // any other way, and the launcher has to size a heap against it.
        ["VideoMemoryProfile.cs"] = "reads the display-adapter class key to size video memory",
    };

    /// <summary>Every way of writing to the registry, in each language here.</summary>
    private static readonly string[] RegistryWrites =
    [
        // .NET. SetValue is deliberately absent: WPF spells its dependency
        // properties the same way, and a token that fires on CenteredDropDown
        // would be turned off within the week.
        "CreateSubKey",
        "DeleteSubKey",
        "DeleteValue",
        "advapi32",
        // A permanent environment variable is a registry value wearing a
        // friendlier name.
        "EnvironmentVariableTarget.User",
        "EnvironmentVariableTarget.Machine",
        "setx ",
        // Processes that would do it on the launcher's behalf.
        "reg.exe",
        "regedit",
        "regsvr32",
        "WScript.Shell",
        "RegWrite",
        // PowerShell, which is how the updater and the cleanup run.
        "Set-ItemProperty",
        "New-ItemProperty",
        "Remove-ItemProperty",
        "HKCU:",
        "HKLM:",
        // Java. This one is not hypothetical: java.util.prefs is what puts
        // HKCU\\Software\\JavaSoft\\Prefs on a machine that never installed Java.
        "java.util.prefs",
        "Preferences.userRoot",
        "Preferences.systemRoot",
    ];

    /// <summary>
    /// Writes that can only be recognised inside a file already allowed to hold
    /// a registry key.
    /// </summary>
    /// <remarks>
    /// RegistryKey.SetValue writes to the registry and DependencyObject.SetValue
    /// is how WPF spells a property, and a search through text cannot tell which
    /// of the two it is looking at. Held against the whole launcher the word
    /// would fail on a combo box, so it is not held against the whole launcher:
    /// a file on the reader list has no WPF in it and never will, and there the
    /// word can only mean the first.
    ///
    /// Everywhere else the pair below is unnecessary anyway. Writing needs a key,
    /// a key comes from one of the roots or from a RegistryKey, and both of those
    /// are already caught - so a file that does not appear here cannot be holding
    /// one to write to.
    /// </remarks>
    private static readonly string[] RegistryWritesWhereAKeyIsHeld =
    [
        "SetValue(",
        // The writable overload. It opens nothing by itself, but a file that
        // asks for write access is not reading any more.
        "writable: true",
        "RegistryKeyPermissionCheck.ReadWriteSubTree",
    ];

    /// <summary>Every way of reaching the registry at all, to read or to write.</summary>
    private static readonly string[] RegistryReach =
    [
        "Registry.LocalMachine",
        "Registry.CurrentUser",
        "Registry.ClassesRoot",
        "Registry.Users",
        "Registry.CurrentConfig",
        "RegistryKey",
        "using Microsoft.Win32;",
    ];

    /// <summary>
    /// The launcher leaves the machine's registry alone.
    /// </summary>
    /// <remarks>
    /// A folder somebody copies to a stick is only portable if deleting the
    /// folder is the whole of removing it, and a registry value is the one
    /// thing deleting the folder cannot take with it. The test above cannot see
    /// this: it looks for four ways of naming a path outside the folder, and a
    /// registry write names no path at all.
    ///
    /// Two rules, because a write is not the only thing worth holding down. No
    /// file may write, whatever its reason; and only a file with a reason
    /// written down here may reach for the registry even to read, so that the
    /// second one to try has to say why in front of somebody.
    ///
    /// What this cannot promise, and should not be read as promising: Windows
    /// itself keeps records about every program that runs - the firewall rule
    /// it asks about, the friendly name the shell caches, the game bar noticing
    /// a JVM. None of that is the launcher writing, and none of it is in this
    /// test's power.
    /// </remarks>
    [Fact]
    public void NothingTheLauncherDoes_WritesToTheRegistry()
    {
        var writers = new List<string>();
        var readers = new List<string>();

        foreach (var file in LauncherSources())
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);

            foreach (var write in RegistryWrites)
            {
                if (text.Contains(write, StringComparison.OrdinalIgnoreCase)) writers.Add($"{name}: {write}");
            }

            // The one file allowed to hold a key is the one file where a write
            // could still hide, because reaching for the registry is exactly
            // what it is permitted to do.
            if (RegistryReaders.ContainsKey(name))
            {
                foreach (var write in RegistryWritesWhereAKeyIsHeld)
                {
                    if (text.Contains(write, StringComparison.Ordinal)) writers.Add($"{name}: {write}");
                }
                continue;
            }

            foreach (var reach in RegistryReach)
            {
                if (text.Contains(reach, StringComparison.Ordinal)) readers.Add($"{name}: {reach}");
            }
        }

        Assert.True(
            writers.Count == 0,
            "Deleting the folder has to be the whole of removing the launcher, and a registry " +
            $"value does not go with it: {string.Join(", ", writers)}");
        Assert.True(
            readers.Count == 0,
            "Only a file named in RegistryReaders may reach for the registry, and only to read. " +
            $"Add it there with a reason if the read is genuinely the only way: {string.Join(", ", readers)}");
    }

    [Fact]
    public void NoLauncherCode_ReachesOutsideTheFolder()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ProgramDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Allowed.ContainsKey(name))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var escape in Escapes)
            {
                if (text.Contains(escape, StringComparison.Ordinal)) offenders.Add($"{name}: {escape}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Everything the launcher writes belongs under its own folder. Add the file to the " +
            $"allow-list with a reason if it truly must reach outside: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The game is given a home inside the folder. Mods reach for one -
    /// e4steam unconditionally, others when they keep a cache - and without
    /// this every one of them writes into the player's profile instead.
    /// </summary>
    [Fact]
    public void TheGameIsGivenAHome_InsideTheFolder()
    {
        var launch = File.ReadAllText(Path.Combine(ProgramDirectory(), "MinecraftProcessService.cs"));

        Assert.Contains("-Duser.home=", launch, StringComparison.Ordinal);
        Assert.Contains("-Djava.io.tmpdir=", launch, StringComparison.Ordinal);
    }

    /// <summary>And every folder the launcher knows is under the root.</summary>
    [Fact]
    public void EveryPathTheLauncherKnows_IsUnderTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"minecraft-portability-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(root);
            var expected = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            string[] everywhere =
            [
                paths.Service, paths.Program, paths.Packs, paths.Launcher, paths.Runtimes,
                paths.JavaRuntimes, paths.SteamNative, paths.Personal, paths.Instances,
                paths.PackConflicts, paths.Worlds, paths.SettingsFile, paths.IdentityBackups,
                paths.PackHashesFile, paths.WindowPlacementFile, paths.MinecraftWindowPlacementFile,
                paths.SkinRegistryFile, paths.WaypointSyncStateFile, paths.WaypointConflicts,
                paths.ProfileTransactions, paths.BugReports, paths.LogFile
            ];

            foreach (var path in everywhere)
            {
                Assert.StartsWith(expected, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /// <summary>
    /// Everything the launcher is built out of, not only its C#. The updater
    /// and the log cleanup run PowerShell, the identity adapters are Java, and
    /// a project file can run a command of its own at build time - a registry
    /// write put in any of those would be as real as one in a service class.
    /// </summary>
    private static IEnumerable<string> LauncherSources() =>
        new[] { "*.cs", "*.ps1", "*.java", "*.csproj" }
            .SelectMany(pattern =>
                Directory.EnumerateFiles(ProgramDirectory(), pattern, SearchOption.AllDirectories))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string ProgramDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Program");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Program directory was not found");
    }
}
