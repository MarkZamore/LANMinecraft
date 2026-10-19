using System.Diagnostics;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PortableJavaEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minecraft-java-environment-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void A_helper_uses_a_portable_temp_profile_and_jvm_properties()
    {
        var start = new ProcessStartInfo { FileName = "java.exe", UseShellExecute = false };
        start.Environment[SteamworksApiFacade.NoOverlayVariable] = "1";
        var parent = Path.Combine(_root, "Temp");
        string working;
        using (PortableJavaEnvironment.CreateToolScope(start, parent))
        {
            working = start.WorkingDirectory;
            Assert.StartsWith(parent + Path.DirectorySeparatorChar, working, StringComparison.OrdinalIgnoreCase);
            var temporary = Path.Combine(working, "Temp");
            var home = Path.Combine(working, "Home");
            Assert.Equal(temporary, start.Environment["TEMP"]);
            Assert.Equal(temporary, start.Environment["TMP"]);
            Assert.Equal(home, start.Environment["HOME"]);
            Assert.Equal(home, start.Environment["USERPROFILE"]);
            Assert.Equal(Path.Combine(home, "AppData", "Roaming"), start.Environment["APPDATA"]);
            Assert.Equal(Path.Combine(home, "AppData", "Local"), start.Environment["LOCALAPPDATA"]);
            Assert.Equal("1", start.Environment[SteamworksApiFacade.NoOverlayVariable]);
            Assert.Contains($"-Djava.io.tmpdir={temporary}", start.ArgumentList);
            Assert.Contains($"-Duser.home={home}", start.ArgumentList);
            Assert.True(Directory.Exists(temporary));
            Assert.True(Directory.Exists(start.Environment["APPDATA"]));
            Assert.True(Directory.Exists(start.Environment["LOCALAPPDATA"]));
            File.WriteAllText(Path.Combine(temporary, "native.tmp"), "tool output");
        }

        Assert.False(Directory.Exists(working));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void Helpers_keep_their_requested_working_directory_and_clean_after_failure()
    {
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "keep.txt");
        File.WriteAllText(marker, "existing work");
        var start = new ProcessStartInfo { FileName = "java.exe", WorkingDirectory = _root, UseShellExecute = false };

        void FailHelper()
        {
            using var scope = PortableJavaEnvironment.CreateToolScope(start, _root);
            Assert.Equal(_root, start.WorkingDirectory);
            throw new InvalidOperationException("The helper failed.");
        }
        Assert.Throws<InvalidOperationException>(FailHelper);

        Assert.Equal("existing work", File.ReadAllText(marker));
        Assert.Empty(Directory.EnumerateDirectories(_root, "java-tool-*"));
    }

    [Fact]
    public void Cleaning_one_helper_does_not_remove_another_helpers_files()
    {
        var first = new ProcessStartInfo { FileName = "java.exe", UseShellExecute = false };
        var second = new ProcessStartInfo { FileName = "java.exe", UseShellExecute = false };
        using var secondScope = PortableJavaEnvironment.CreateToolScope(second, _root);
        using (PortableJavaEnvironment.CreateToolScope(first, _root))
        {
            Assert.NotEqual(first.WorkingDirectory, second.WorkingDirectory);
        }

        Assert.False(Directory.Exists(first.WorkingDirectory));
        Assert.True(Directory.Exists(second.WorkingDirectory));
        Assert.True(Directory.Exists(second.Environment["TEMP"]));
    }

    [Fact]
    public async Task Cancelling_a_real_child_waits_for_its_exit_before_removing_its_profile()
    {
        var start = WaitingChild();
        string ownedRoot;
        using (var scope = PortableJavaEnvironment.CreateToolScope(start, _root))
        {
            ConfigureWaitingChild(start);
            ownedRoot = scope.RootDirectory;
            var child = scope.Start();
            try
            {
                Assert.False(child.HasExited);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => child.WaitForExitAsync(cancelled.Token));

                Assert.True(await scope.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(child.HasExited);
                Assert.True(Directory.Exists(ownedRoot));
            }
            finally
            {
                _ = await scope.StopAsync();
            }
        }

        Assert.False(Directory.Exists(ownedRoot));
    }

    [Fact]
    public void The_synchronous_probe_stop_also_confirms_exit_before_cleanup()
    {
        var start = WaitingChild();
        string ownedRoot;
        using (var scope = PortableJavaEnvironment.CreateToolScope(start, _root))
        {
            ConfigureWaitingChild(start);
            ownedRoot = scope.RootDirectory;
            var child = scope.Start();
            try
            {
                Assert.True(scope.Stop());
                Assert.True(child.HasExited);
            }
            finally
            {
                _ = scope.Stop();
            }
        }

        Assert.False(Directory.Exists(ownedRoot));
    }

    [Fact]
    public async Task Disposing_a_live_helper_preserves_its_scope_and_the_loader_cannot_delete_its_parent()
    {
        var paths = new AppPaths(_root);
        var loaderTemporary = Path.Combine(paths.Personal, "Temp", "RuntimeDownloads", "fixture");
        using var runtimes = new PackRuntimeService(paths, new Logger(paths.LogFile));
        var start = WaitingChild();
        var scope = PortableJavaEnvironment.CreateToolScope(start, loaderTemporary);
        ConfigureWaitingChild(start);
        var child = scope.Start();
        var processId = child.Id;
        using var survivor = Process.GetProcessById(processId);
        _ = survivor.Handle;
        try
        {
            Assert.False(child.HasExited);
            scope.Dispose();
            runtimes.CleanupTemporaryFiles("fixture");

            Assert.True(Directory.Exists(scope.RootDirectory));
            Assert.True(Directory.Exists(start.Environment["TEMP"]));
            Assert.False(survivor.HasExited);
        }
        finally
        {
            // The scope deliberately did not kill this process. The test owns
            // it and stops it independently before the fixture removes files.
            if (!survivor.HasExited) survivor.Kill(entireProcessTree: true);
            await survivor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            scope.Dispose();
        }
    }

    private static ProcessStartInfo WaitingChild() => new()
    {
        FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static void ConfigureWaitingChild(ProcessStartInfo start)
    {
        // This real disposable child stands in for a Java tool's lifetime;
        // ping does not accept the Java properties configured by the scope.
        start.ArgumentList.Clear();
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("60");
        start.ArgumentList.Add("127.0.0.1");
    }
}
