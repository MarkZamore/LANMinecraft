using System.IO;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// Deleting a build.
///
/// The whole of the risk in this feature is in one place: the instance's
/// <c>saves</c> is a folder of directory junctions pointing into
/// <c>Worlds</c>, and a delete that walks through one of them destroys a world
/// the player asked to keep. So the first test here is that one, and it is
/// written with a real junction rather than a stand-in, because a stand-in
/// would pass whatever the code did.
/// </summary>
public sealed class BuildRemovalServiceTests : IDisposable
{
    private const string Build = "Some Pack";
    private const string Other = "Another Pack";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"minecraft-removal-{Guid.NewGuid():N}");

    private readonly AppPaths _paths;

    public BuildRemovalServiceTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
        _paths.Ensure();
    }

    public void Dispose()
    {
        try
        {
            TempTree.Delete(_root);
        }
        catch
        {
        }
    }

    /// <summary>
    /// The one that matters. A world lives under Worlds; the instance holds a
    /// junction to it; the build is removed with the worlds kept. The junction
    /// must go and the world must still be there, with its files.
    /// </summary>
    [Fact]
    public void AWorldBehindAJunction_SurvivesTheBuildThatPointedAtIt()
    {
        Install(Build, "1.21.1");
        var world = MakeWorld(Build, "Дом");
        var saves = Path.Combine(_paths.Instances, Build, "saves");
        Directory.CreateDirectory(saves);
        SavesFolderService.CreateJunction(Path.Combine(saves, "Дом"), world);
        Assert.True(Directory.Exists(Path.Combine(saves, "Дом")));

        var service = new BuildRemovalService(_paths);
        var outcome = service.Remove(service.Plan(Build, worldsToo: false));

        Assert.True(outcome.Complete);
        Assert.False(Directory.Exists(Path.Combine(_paths.Instances, Build)));
        Assert.True(Directory.Exists(world));
        Assert.True(File.Exists(Path.Combine(world, "level.dat")));
    }

    /// <summary>And with the worlds asked for, the world goes too.</summary>
    [Fact]
    public void WithTheWorlds_TheWorldsGo()
    {
        Install(Build, "1.21.1");
        var world = MakeWorld(Build, "Дом");
        var saves = Path.Combine(_paths.Instances, Build, "saves");
        Directory.CreateDirectory(saves);
        SavesFolderService.CreateJunction(Path.Combine(saves, "Дом"), world);

        var service = new BuildRemovalService(_paths);
        var plan = service.Plan(Build, worldsToo: true);
        Assert.Equal(1, plan.Worlds);

        var outcome = service.Remove(plan);
        Assert.True(outcome.Complete);
        Assert.Equal(1, outcome.Worlds);
        Assert.False(Directory.Exists(world));
        Assert.False(Directory.Exists(WorldLocations.ForBuild(_paths.Worlds, Build)));
    }

    /// <summary>
    /// A build is not one folder, and the point of the feature is the rest of
    /// them: the instance, the prepared runtime, the conflict folders and what
    /// a launch left in Temp.
    /// </summary>
    [Fact]
    public void EveryFolderTheBuildOwns_Goes()
    {
        Install(Build, "1.21.1");
        var owned = new[]
        {
            Path.Combine(_paths.Packs, Build),
            Path.Combine(_paths.Instances, Build),
            Path.Combine(_paths.Runtimes, Build),
            Path.Combine(_paths.PackConflicts, Build),
            Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads", Build),
            Path.Combine(_paths.Personal, "Temp", "Java", Build)
        };
        foreach (var directory in owned) Write(Path.Combine(directory, "deep", "file.txt"), "x");

        var service = new BuildRemovalService(_paths);
        var plan = service.Plan(Build, worldsToo: false);
        Assert.Equal(owned.Length, plan.Directories.Count);

        var outcome = service.Remove(plan);
        Assert.True(outcome.Complete);
        Assert.All(owned, directory => Assert.False(Directory.Exists(directory)));
        // And the empty parents those left behind.
        Assert.False(Directory.Exists(Path.Combine(_paths.Personal, "Temp")));
    }

    /// <summary>A build that is removed takes nothing of anybody else's.</summary>
    [Fact]
    public void TheOtherBuildIsUntouched()
    {
        Install(Build, "1.21.1");
        Install(Other, "1.21.1");
        Write(Path.Combine(_paths.Instances, Other, "options.txt"), "renderDistance:8");
        var otherWorld = MakeWorld(Other, "Соседний");

        var service = new BuildRemovalService(_paths);
        service.Remove(service.Plan(Build, worldsToo: true));

        Assert.True(Directory.Exists(Path.Combine(_paths.Packs, Other)));
        Assert.True(File.Exists(Path.Combine(_paths.Instances, Other, "options.txt")));
        Assert.True(Directory.Exists(otherWorld));
    }

    /// <summary>
    /// Java is shared, so it goes only when the build that pinned it was the
    /// last one asking for that feature release - the unpacked JDK and the
    /// archive it came out of together.
    /// </summary>
    [Fact]
    public void TheJavaNobodyElseNeeds_GoesWithTheBuild()
    {
        Install(Build, "1.21.1");     // Java 21
        Install(Other, "1.20.1");     // Java 17
        var twentyOne = InstallJava("java-21", "temurin-21.0.12.1_1");
        var seventeen = InstallJava("java-17", "temurin-17.0.20.1_1");

        var service = new BuildRemovalService(_paths);
        var plan = service.Plan(Build, worldsToo: false);
        Assert.Equal([21], plan.Java);

        var outcome = service.Remove(plan);
        Assert.Equal([21], outcome.Java);
        Assert.All(twentyOne, directory => Assert.False(Directory.Exists(directory)));
        Assert.All(seventeen, directory => Assert.True(Directory.Exists(directory)));
    }

    /// <summary>And it stays when another build on the disk runs on it.</summary>
    [Fact]
    public void TheJavaAnotherBuildRunsOn_Stays()
    {
        Install(Build, "1.21.1");
        Install(Other, "1.21.1");
        var twentyOne = InstallJava("java-21", "temurin-21.0.12.1_1");

        var service = new BuildRemovalService(_paths);
        var plan = service.Plan(Build, worldsToo: false);
        Assert.Empty(plan.Java);

        service.Remove(plan);
        Assert.All(twentyOne, directory => Assert.True(Directory.Exists(directory)));
    }

    /// <summary>
    /// A pack whose manifest will not parse still counts as needing Java: the
    /// safe way round is a runtime folder that stays, not a working build left
    /// to download three hundred megabytes again.
    /// </summary>
    [Fact]
    public void APackWhoseManifestIsRubbish_StillHoldsItsJava()
    {
        Install(Build, "1.21.1");
        Directory.CreateDirectory(Path.Combine(_paths.Packs, Other));
        Write(Path.Combine(_paths.Packs, Other, "portable-pack.json"), "{ not json");
        var twentyOne = InstallJava("java-21", "temurin-21.0.12.1_1");

        var service = new BuildRemovalService(_paths);
        Assert.Empty(service.Plan(Build, worldsToo: false).Java);
        service.Remove(service.Plan(Build, worldsToo: false));
        Assert.All(twentyOne, directory => Assert.True(Directory.Exists(directory)));
    }

    /// <summary>
    /// The name comes from a folder listing, but a delete is not the place to
    /// take that on trust: anything that would land outside the folder it was
    /// looked up in names nothing at all.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("..\\..\\Windows")]
    [InlineData("Some Pack\\mods")]
    public void ANameThatTriesToLeaveItsFolder_RemovesNothing(string name)
    {
        Install(Build, "1.21.1");
        var service = new BuildRemovalService(_paths);

        Assert.Empty(service.Plan(name, worldsToo: true).Directories);
        Assert.True(Directory.Exists(Path.Combine(_paths.Packs, Build)));
    }

    /// <summary>
    /// The settings keep a number per pack and a selected pack. Both are about
    /// a build that no longer exists once it is gone.
    /// </summary>
    [Fact]
    public void TheSettingsForget()
    {
        var settings = new AppSettings
        {
            ClientRelativePath = Build,
            SelectedWorldRelativePath = $"{Build}/Дом"
        };
        settings.MemoryByPack[Build] = 6;
        settings.MemoryByPack[Other] = 12;

        Assert.True(BuildRemovalService.Forget(settings, Build));
        Assert.Equal("", settings.ClientRelativePath);
        Assert.Equal("", settings.SelectedWorldRelativePath);
        Assert.False(settings.MemoryByPack.ContainsKey(Build));
        Assert.Equal(12, settings.MemoryByPack[Other]);
    }

    /// <summary>Removing a build that was not the selected one leaves the selection alone.</summary>
    [Fact]
    public void TheSettingsKeepASelectionThatIsNotTheBuildThatWent()
    {
        var settings = new AppSettings { ClientRelativePath = Other };
        settings.MemoryByPack[Build] = 6;

        Assert.True(BuildRemovalService.Forget(settings, Build));
        Assert.Equal(Other, settings.ClientRelativePath);
    }

    /// <summary>Nothing to remove is not a failure; it is a build already gone.</summary>
    [Fact]
    public void RemovingWhatIsNotThere_Succeeds()
    {
        var service = new BuildRemovalService(_paths);
        var outcome = service.Remove(service.Plan("Never Existed", worldsToo: true));
        Assert.True(outcome.Complete);
        Assert.Equal(0, outcome.Directories);
    }

    /// <summary>A read-only jar is not a reason to leave half a build behind.</summary>
    [Fact]
    public void AReadOnlyFile_DoesNotStopIt()
    {
        Install(Build, "1.21.1");
        var jar = Path.Combine(_paths.Packs, Build, "mods", "locked.jar");
        Write(jar, "x");
        new FileInfo(jar).IsReadOnly = true;

        var service = new BuildRemovalService(_paths);
        Assert.True(service.Remove(service.Plan(Build, worldsToo: false)).Complete);
        Assert.False(Directory.Exists(Path.Combine(_paths.Packs, Build)));
    }

    private void Install(string build, string minecraftVersion)
    {
        var pack = Path.Combine(_paths.Packs, build);
        Write(
            Path.Combine(pack, "portable-pack.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                minecraftVersion,
                loader = new { type = "neoforge", version = "21.1.194" },
                clientJar = ""
            }));
        Write(Path.Combine(pack, "mods", "a.jar"), "jar");
        Write(Path.Combine(_paths.Instances, build, "options.txt"), "renderDistance:8");
    }

    /// <summary>Both halves of an installed Java: the unpacked JDK and the archive.</summary>
    private string[] InstallJava(string installName, string archiveName)
    {
        var install = Path.Combine(_paths.JavaRuntimes, "runtime", "windows-x64", installName);
        var archive = Path.Combine(_paths.Launcher, "ManagedComponents", "java-runtime", archiveName);
        Write(Path.Combine(install, "bin", "javaw.exe"), "exe");
        Write(Path.Combine(archive, "jdk.zip"), "zip");
        return [install, archive];
    }

    private string MakeWorld(string build, string name)
    {
        var world = Path.Combine(WorldLocations.ForBuild(_paths.Worlds, build), name);
        Write(Path.Combine(world, "level.dat"), "world");
        return world;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
