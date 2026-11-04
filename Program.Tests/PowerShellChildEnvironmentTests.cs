using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft.Tests;

public sealed class PowerShellChildEnvironmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "portable updater 'quoted' " + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(root);

    [Fact]
    public async Task ScheduledUpdaterPassesItsAnalysisCacheToARealPowerShellChild()
    {
        var (paths, prepared, current) = Installation();
        var result = Path.Combine(paths.Root, "child-environment.json");
        var recipient = Path.Combine(paths.Root, "environment-recipient.ps1");
        File.WriteAllText(recipient, """
            param([string]$Output)
            $ErrorActionPreference = 'Stop'
            $value = [ordered]@{
                cachePath = $env:PSModuleAnalysisCachePath
                parentAlreadyExists = [IO.Directory]::Exists([IO.Path]::GetDirectoryName($env:PSModuleAnalysisCachePath))
                powerShellMajorVersion = $PSVersionTable.PSVersion.Major
            }
            [IO.File]::WriteAllText($Output, ($value | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
            """, Encoding.UTF8);
        var parentCache = Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath");
        Process? child = null;
        var service = new UpdateService(paths, new Logger(paths.LogFile), currentExecutablePath: current)
        {
            StartPowerShellHelper = start =>
            {
                // Exercise production StartInstall's environment and scheduling,
                // but let the actual PowerShell child report that environment
                // instead of replacing or restarting an executable.
                Assert.Equal("powershell.exe", start.FileName);
                Assert.False(start.UseShellExecute);
                Assert.Equal(paths.Root, start.WorkingDirectory);
                Assert.Contains(Path.Combine(paths.Personal, "Updates", UpdateService.InstallScriptFileName), start.ArgumentList);
                start.ArgumentList.Clear();
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", recipient, "-Output", result })
                    start.ArgumentList.Add(argument);
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                // Fixture isolation for PowerShell's policy probe, separate from
                // the production analysis-cache assertion below.
                var temporary = Path.Combine(paths.Root, "FixtureTemp");
                Directory.CreateDirectory(temporary);
                start.Environment["TEMP"] = temporary;
                start.Environment["TMP"] = temporary;
                return child = Process.Start(start);
            }
        };
        try
        {
            service.StartInstall(prepared, UpdateInstallMode.InstallOnExit);
            Assert.NotNull(child);
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(child.ExitCode == 0, await stderr);
            Assert.Equal(string.Empty, await stdout);
            using var document = JsonDocument.Parse(File.ReadAllText(result));
            var expected = Path.Combine(paths.Launcher, "PowerShell", "ModuleAnalysisCache");
            var actual = document.RootElement.GetProperty("cachePath").GetString();
            Assert.Equal(expected, actual);
            paths.EnsureUnderRoot(actual!);
            Assert.True(document.RootElement.GetProperty("parentAlreadyExists").GetBoolean());
            Assert.Equal(5, document.RootElement.GetProperty("powerShellMajorVersion").GetInt32());
            Assert.Equal(parentCache, Environment.GetEnvironmentVariable("PSModuleAnalysisCachePath"));
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                child.Dispose();
            }
        }
    }

    [Fact]
    public void CleanupAndFailedSchedulingKeepTheSharedAnalysisCacheEvenWhileItIsOpen()
    {
        var (paths, prepared, current) = Installation();
        var cache = Path.Combine(paths.Launcher, "PowerShell", "ModuleAnalysisCache");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "Existing cache fixture; no production helper reads this file.");
        using (File.Open(cache, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            StructureCleanupService.Run(paths);
            DeprecatedFileCleanupService.Run(paths);
            Assert.True(File.Exists(cache));
            var attempts = 0;
            var service = new UpdateService(paths, new Logger(paths.LogFile), currentExecutablePath: current)
            {
                StartPowerShellHelper = start =>
                {
                    attempts++;
                    Assert.Equal(cache, start.Environment["PSModuleAnalysisCachePath"]);
                    throw new Win32Exception(2, "Synthetic helper-start failure");
                }
            };
            Assert.Throws<Win32Exception>(() => service.StartInstall(prepared, UpdateInstallMode.InstallOnExit));
            Assert.Throws<Win32Exception>(() => service.StartInstall(prepared, UpdateInstallMode.InstallOnExit));
            Assert.Equal(2, attempts);
            Assert.True(File.Exists(cache));
        }
        Assert.Equal("Existing cache fixture; no production helper reads this file.", File.ReadAllText(cache));
    }

    private (AppPaths Paths, PreparedUpdate Prepared, string Current) Installation()
    {
        var paths = new AppPaths(root);
        paths.Ensure();
        var current = Path.Combine(root, UpdateService.ExecutableAssetName);
        File.WriteAllText(current, "Synthetic old image; never executed.");
        var ready = Path.Combine(paths.Personal, "Updates", "Ready");
        Directory.CreateDirectory(ready);
        var executable = Path.Combine(ready, UpdateService.ExecutableAssetName);
        var payload = Encoding.UTF8.GetBytes("Synthetic prepared image; never executed.");
        File.WriteAllBytes(executable, payload);
        var manifest = new UpdateManifest
        {
            SchemaVersion = UpdateService.ManifestSchemaVersion, CommitSha = new string('b', 40),
            ReleaseNumber = 2, Version = "fixture-2", PublishedAtUtc = DateTimeOffset.UtcNow,
            Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), SizeBytes = payload.Length
        };
        File.WriteAllText(Path.Combine(ready, UpdateService.ManifestAssetName), JsonSerializer.Serialize(manifest));
        return (paths, new PreparedUpdate(manifest, executable), current);
    }
}
