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
