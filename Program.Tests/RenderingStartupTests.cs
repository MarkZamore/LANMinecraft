using System.Diagnostics;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class RenderingStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "minecraft-rendering-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task TheBuiltRuntimeConfiguration_DisablesDirtyRectanglesBeforeWpfCachesTheSwitch()
    {
        var repository = FindRepository();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var programOutput = Path.Combine(repository, "Program", "bin", configuration, "net10.0-windows", "win-x64");
        var hostOutput = Path.Combine(repository, "tools", "PortabilityAuditHost", "bin", configuration, "net10.0-windows", "win-x64");
        var runtimeConfiguration = Path.Combine(programOutput, "LANMinecraft.runtimeconfig.json");
        var hostAssembly = Path.Combine(hostOutput, "PortabilityAuditHost.dll");
        var hostDependencies = Path.Combine(hostOutput, "PortabilityAuditHost.deps.json");
        foreach (var path in new[] { runtimeConfiguration, hostAssembly, hostDependencies })
            Assert.True(File.Exists(path), $"Build the launcher tests before this check: {path}");

        using var runtime = JsonDocument.Parse(File.ReadAllText(runtimeConfiguration));
        if (runtime.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out _))
        {
            // Release carries its own CLR and WPF. A self-contained runtimeconfig
            // needs hostpolicy beside the entry DLL and the runtime-pack deps,
            // so run the probe in an isolated copy of that actual build output.
            foreach (var file in Directory.EnumerateFiles(programOutput, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(_root, Path.GetRelativePath(programOutput, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var isolatedHost = Path.Combine(_root, "PortabilityAuditHost.dll");
            File.Copy(hostAssembly, isolatedHost, overwrite: true);
            hostAssembly = isolatedHost;
            hostDependencies = Path.Combine(_root, "LANMinecraft.deps.json");
            Assert.True(File.Exists(hostDependencies));
            Assert.True(File.Exists(Path.Combine(_root, "hostpolicy.dll")));
        }

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repository
        };
        foreach (var argument in new[] { "exec", "--runtimeconfig", runtimeConfiguration,
                     "--depsfile", hostDependencies, hostAssembly, "--rendering-probe" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The fresh-process WPF rendering probe did not exit within 20 seconds.");
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"Rendering probe exited {process.ExitCode}: {error}\n{output}");
        using var result = JsonDocument.Parse(output);
        Assert.True(result.RootElement.GetProperty("switchDefined").GetBoolean());
        Assert.True(result.RootElement.GetProperty("switchEnabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("wpfCachedDisableDirtyRectangles").GetBoolean());
        Assert.Equal("Default", result.RootElement.GetProperty("processRenderModeBeforeApp").GetString());
        Assert.Equal("SoftwareOnly", result.RootElement.GetProperty("modeBeforeInitializeComponent").GetString());
        Assert.Equal("SoftwareOnly", result.RootElement.GetProperty("modeAfterInitializeComponent").GetString());
        Assert.Equal("SoftwareOnly", result.RootElement.GetProperty("processRenderMode").GetString());
        Assert.Equal(0, result.RootElement.GetProperty("windowsBeforeInitializeComponent").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("windowsAfterInitializeComponent").GetInt32());
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("The launcher repository was not found.");
    }
}
