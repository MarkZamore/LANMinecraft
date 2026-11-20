using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Minecraft.Tests;

[CollectionDefinition("Native portable host", DisableParallelization = true)]
public sealed class NativePortableHostCollection { }

// Every process and filesystem mutation belongs to this disposable installation.
// A shared verified component source avoids copying runtime packets per case.
[Collection("Native portable host")]
public sealed class NativePortableHostTests(NativePortableHostFixture fixture) : IClassFixture<NativePortableHostFixture>
{
    [Fact]
    public async Task OuterResourcesAndPacketKeepTheProductIdentityWithoutTheUnusedSdkApphost()
    {
        var version = FileVersionInfo.GetVersionInfo(fixture.Executable);
        Assert.Equal("65001.0.0.0", version.FileVersion);
        Assert.Equal((65001, 0, 0, 0),
            (version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart));
        Assert.Equal("1.0.65001+" + NativePortableHostFixture.Revision, version.ProductVersion);
        var group = NativeHostResources.Read(fixture.Executable, 14, 1); // GROUP_ICON
        var icon = File.ReadAllBytes(Path.Combine(fixture.Repository, "Program", "Assets", "icon.ico"));
        var iconCount = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        Assert.Equal(iconCount, BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4)));
        for (var index = 0; index < iconCount; index++)
        {
            var entry = icon.AsSpan(6 + index * 16, 16);
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]));
            var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
            var resourceId = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(6 + index * 14 + 12));
            Assert.Equal(icon.AsSpan(offset, size).ToArray(), NativeHostResources.Read(fixture.Executable, 3, resourceId));
        }
        var manifestBytes = NativeHostResources.Read(fixture.Executable, 24, 1);
        var manifest = XDocument.Parse(Encoding.UTF8.GetString(manifestBytes).TrimStart('\uFEFF').TrimEnd('\0'));
        var original = XDocument.Load(Path.Combine(fixture.Repository, "Program", "app.manifest"));
        foreach (var name in new[] { "requestedExecutionLevel", "dpiAware", "dpiAwareness" })
        {
            var actual = manifest.Descendants().Single(node => node.Name.LocalName == name);
            var expected = original.Descendants().Single(node => node.Name.LocalName == name);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Value.Trim(), actual.Value.Trim());
            Assert.Equal(expected.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                    .Select(attribute => (attribute.Name, attribute.Value)).OrderBy(attribute => attribute.Name.ToString()),
                actual.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
                    .Select(attribute => (attribute.Name, attribute.Value)).OrderBy(attribute => attribute.Name.ToString()));
        }
        Assert.Equal(fixture.Packet, NativeHostResources.Read(fixture.Executable, 10, 101));
        var fonts = Path.Combine(fixture.Repository, "Program", "Assets", "Fonts");
        Assert.Equal(File.ReadAllBytes(Path.Combine(fonts, "Roboto-Regular.ttf")), NativeHostResources.Read(fixture.Executable, 10, 103));
        Assert.Equal(File.ReadAllBytes(Path.Combine(fonts, "Roboto-SemiBold.ttf")), NativeHostResources.Read(fixture.Executable, 10, 104));
        var paths = fixture.Records.Select(record => record.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PortabilityAuditHost.exe", paths);
        foreach (var required in new[] { "PortabilityAuditHost.dll", "PortabilityAuditHost.runtimeconfig.json", "PortabilityAuditHost.deps.json" })
            Assert.Contains(required, paths);
        foreach (var external in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wpfgfx_cor3.dll", "Background.png", "steam_api64.dll" })
            Assert.DoesNotContain(external, paths);
        fixture.AssertDependencyResource();
        Assert.True(File.Exists(Path.Combine(fixture.Payload, "PortabilityAuditHost.exe")), "The SDK apphost must exist before the production packer deliberately omits it.");
        await VerifyPlaytimeObserver();
    }

    private async Task VerifyPlaytimeObserver()
    {
        var paths = new AppPaths(fixture.Installation);
        var logger = new Logger(paths.LogFile);
        Directory.CreateDirectory(paths.Personal);
        var legacyFile = Path.Combine(paths.Personal, "time");
        File.WriteAllText(legacyFile, "Previous format");
        File.WriteAllText(paths.PlaytimeFile, "{\r\n  \"Another-\\u0052epo\": 45000\r\n}\r\n", new UTF8Encoding(true));
        var gameInfo = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60")
            { UseShellExecute = false, CreateNoWindow = true };
        using var game = Process.Start(gameInfo)!;
        var source = new CatalogSource("new-identifier", "Fixture",
            new PackSyncSource("fixture", "Better-Vanilla", "latest"));
        var key = Playtime.Key(source);
        Assert.Equal("Better-Vanilla", key);
        Assert.Equal(key, Playtime.LaunchKey("Fixture", [source], source.Source));
        Assert.Null(Playtime.LaunchKey("Fixture", [source], null));
        Assert.Null(Playtime.LaunchKey("Personal copy", [source], source.Source));
        Assert.Null(Playtime.LaunchKey("Fixture", [source], source.Source with { Owner = "other-owner" }));
        var args = new[]
        {
            "--track-playtime",
            game.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            game.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Playtime.Timestamp()!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key
        };
        using var observer = StartObserver();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!Playtime.Read(paths, logger).Packs.ContainsKey(key) && !observer.HasExited)
                await Task.Delay(50, timeout.Token);
            Assert.False(observer.HasExited);
            Assert.Equal(0L, Playtime.Read(paths, logger).Packs[key]);
            using (var duplicate = StartObserver())
            {
                try
                {
                    await duplicate.WaitForExitAsync(timeout.Token);
                    Assert.Equal(183, duplicate.ExitCode);
                }
                finally { if (!duplicate.HasExited) duplicate.Kill(); }
            }
            await Prune("{\"another-repo\":0,\"better-vanilla\":0}");
            Assert.Equal(45000, Playtime.Read(paths, logger).Total);
            await Task.Delay(250, timeout.Token);
            game.Kill();
            await observer.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, observer.ExitCode);
            var total = Playtime.Read(paths, logger);
            Assert.Equal(45000, total.Packs["another-repo"]);
            Assert.Equal(total.Packs[key] + 45000, total.Total);
            Assert.InRange(total.Packs["better-vanilla"], 250, 15000);
            Assert.Equal("Previous format", File.ReadAllText(legacyFile));
            using (var json = JsonDocument.Parse(File.ReadAllBytes(paths.PlaytimeFile)))
                Assert.Equal(total.Packs[key], json.RootElement.GetProperty("Better-Vanilla").GetInt64());
            var copied = new AppPaths(Path.Combine(fixture.Root, "copied playtime"));
            Directory.CreateDirectory(copied.Personal);
            try
            {
                File.Copy(paths.PlaytimeFile, copied.PlaytimeFile);
                Assert.Equal(total.Total, Playtime.Read(copied, logger).Total);
            }
            finally
            {
                File.Delete(copied.PlaytimeFile);
                Directory.Delete(copied.Personal);
                Directory.Delete(copied.Service);
                Directory.Delete(copied.Root);
            }
            await Prune("{\"better-vanilla\":0}");
            Assert.Equal(total.Packs[key], Playtime.Read(paths, logger).Total);
            await Prune("{}");
            Assert.Empty(Playtime.Read(paths, logger).Packs);
        }
        finally
        {
            if (!game.HasExited) game.Kill();
            if (!observer.HasExited) observer.Kill();
            await observer.WaitForExitAsync();
            await game.WaitForExitAsync();
            File.Delete(paths.PlaytimeFile);
            File.Delete(legacyFile);
        }

        Process StartObserver()
        {
            var info = new ProcessStartInfo(fixture.Executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var value in args) info.ArgumentList.Add(value);
            return Process.Start(info)!;
        }

        async Task Prune(string json)
        {
            var info = new ProcessStartInfo(fixture.Executable, "--prune-playtime")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            using var cleanup = Process.Start(info)!;
            try
            {
                await cleanup.StandardInput.WriteAsync(json);
                cleanup.StandardInput.Close();
                await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, cleanup.ExitCode);
            }
            finally { if (!cleanup.HasExited) cleanup.Kill(); }
        }
    }

    [Fact]
    public async Task ColdAndWarmStartsUseThePrivateRuntimePortableTempAndTheOuterStaProcess()
    {
        fixture.RemoveCache();
        var cold = await fixture.Probe();
        fixture.AssertPortable(cold);
        var entry = Path.Combine(fixture.Cache, "PortabilityAuditHost.dll");
        var timestamp = File.GetLastWriteTimeUtc(entry);
        var componentRequests = fixture.ComponentRequests;
        var warm = await fixture.Probe();
        fixture.AssertPortable(warm);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(entry));
        Assert.Equal(cold.Json.GetProperty("appBaseDirectory").GetString(), warm.Json.GetProperty("appBaseDirectory").GetString());
        Assert.Equal(componentRequests, fixture.ComponentRequests);
        var shortExecutable = fixture.ShortExecutablePath();
        if (shortExecutable is not null)
        {
            fixture.AssertPortable(await fixture.Probe(shortExecutable), fixture.Executable);
            Assert.Equal(componentRequests, fixture.ComponentRequests);
        }
        Assert.False(Directory.Exists(Path.Combine(fixture.Installation, "Minecraft", "Launcher", "Temp", ".net")));
    }

    [Fact]
    public async Task NativeDirectoryGuardsPermitAtomicManagedWritesAndInactiveGenerationPruning()
    {
        var probe = await fixture.Probe(arguments: ["--rendering-probe", "--probe-storage"]);
        fixture.AssertPortable(probe);
        var storage = probe.Json.GetProperty("storage");
        Assert.Equal(5, storage.GetProperty("written").GetArrayLength());
        foreach (var value in storage.GetProperty("written").EnumerateArray())
        {
            var path = value.GetString()!;
            fixture.AssertInsideRoot(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
            Assert.Equal("replacement", File.ReadAllText(path));
        }
        Assert.Equal(new[] { true, true, false, false }, storage.GetProperty("retained").EnumerateArray().Select(value => value.GetBoolean()));
        Assert.True(storage.GetProperty("ownershipLeasesRetained").GetBoolean());
        Assert.True(storage.GetProperty("activeGenerationRetained").GetBoolean());
    }

    [Fact]
    public async Task NativeUpdaterWaitsForTheOuterProcessAndRestartsTheVerifiedReplacementAtItsPortableRoot()
    {
        fixture.AssertPortable(await fixture.Probe());
        var componentRequests = fixture.ComponentRequests;
        var paths = new AppPaths(fixture.Installation);
        paths.Ensure();
        var updates = Path.Combine(paths.Personal, "Updates");
        var ready = Path.Combine(updates, "Ready");
        Directory.CreateDirectory(ready);
        var prepared = Path.Combine(ready, UpdateService.ExecutableAssetName);
        await fixture.Pack(prepared, 65002, "native-update-build", new string('b', 40));
        var expectedHash = NativePortableHostFixture.HashFile(prepared);
        var manifest = new UpdateManifest
        {
            SchemaVersion = UpdateService.ManifestSchemaVersion, CommitSha = new string('b', 40), ReleaseNumber = 65002,
            Version = "native-host-65002", PublishedAtUtc = DateTimeOffset.UtcNow,
            Sha256 = expectedHash, SizeBytes = new FileInfo(prepared).Length
        };
        var manifestFile = Path.Combine(ready, UpdateService.ManifestAssetName);
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var target = Path.Combine(fixture.Installation, "updater-probe.exe");
        fixture.AssertInsideRoot(target);
        File.Copy(fixture.Executable, target);
        var originalHash = NativePortableHostFixture.HashFile(target);
        var marker = Path.Combine(fixture.Installation, "audit-restarted.txt");
        File.Delete(marker);
        var journal = Path.Combine(updates, UpdateService.InstallJournalFileName);
        using var source = fixture.Start(executable: target, arguments: ["install", fixture.Installation, target, manifestFile, prepared,
            UpdateInstallMode.InstallAndRestart.ToString(), "--probe-hold-ms=8000"], allowPowerShell: true);
        var sourceErrors = source.StandardError.ReadToEndAsync();
        Process? helper = null;
        try
        {
            var line = await source.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(line);
            File.WriteAllText(Path.Combine(fixture.Root, "native-update-source.json"), line);
            using var receipt = JsonDocument.Parse(line);
            Assert.Equal(source.Id, receipt.RootElement.GetProperty("processId").GetInt32());
            NativePortableHostFixture.SamePath(target, receipt.RootElement.GetProperty("processPath").GetString());
            NativePortableHostFixture.SamePath(fixture.Installation, receipt.RootElement.GetProperty("applicationRoot").GetString());
            var helperId = receipt.RootElement.GetProperty("helperProcessId").GetInt32();
            helper = Process.GetProcessById(helperId);
            Assert.Equal("powershell", helper.ProcessName, ignoreCase: true);
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            NativePortableHostFixture.SamePath(powerShell, NativePortableHostFixture.ProcessImagePath(helper));
            Assert.Equal(receipt.RootElement.GetProperty("helperStartUtc").GetDateTime(), helper.StartTime.ToUniversalTime());
            // GetProcessById stores only a PID. Retain its handle while alive:
            // ExitCode otherwise cannot be read after the helper has vanished.
            Assert.False(helper.SafeHandle.IsInvalid);
            fixture.TrackProcess(helper, powerShell);
            UpdateInstallJournal? applying = null;
            await WaitUntil(() =>
            {
                applying = ReadJournal(journal);
                return applying?.Status == UpdateInstallStatus.Applying;
            }, TimeSpan.FromSeconds(6));
            Assert.False(source.HasExited);
            Assert.Equal(source.Id, applying!.SourceProcessId);
            Assert.Equal(helper.Id, applying.HelperProcessId);
            Assert.Equal(expectedHash, applying.TargetSha256);
            Assert.Equal(originalHash, NativePortableHostFixture.HashFile(target));
            File.WriteAllText(Path.Combine(fixture.Root, "native-update-applying.json"), JsonSerializer.Serialize(applying));
            await source.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(source.ExitCode == 0, await sourceErrors.WaitAsync(TimeSpan.FromSeconds(35)));
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, helper.ExitCode);
            File.Copy(Path.Combine(updates, "update.log"), Path.Combine(fixture.Root, "native-update.log"), overwrite: true);
            byte[] markerBytes = [];
            await WaitUntil(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(marker);
                    using var document = JsonDocument.Parse(bytes);
                    markerBytes = bytes;
                    return document.RootElement.TryGetProperty("processId", out _);
                }
                catch (Exception error) when (error is IOException or JsonException) { return false; }
            }, TimeSpan.FromSeconds(20));
            File.WriteAllBytes(Path.Combine(fixture.Root, "native-update-restarted.json"), markerBytes);
            using var restarted = JsonDocument.Parse(markerBytes);
            var restartedId = restarted.RootElement.GetProperty("processId").GetInt32();
            Assert.NotEqual(source.Id, restartedId);
            try
            {
                using var restartedProcess = Process.GetProcessById(restartedId);
                if (!restartedProcess.HasExited)
                {
                    NativePortableHostFixture.SamePath(target, NativePortableHostFixture.ProcessImagePath(restartedProcess));
                    Assert.False(restartedProcess.SafeHandle.IsInvalid);
                    fixture.TrackProcess(restartedProcess, target);
                    await restartedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(0, restartedProcess.ExitCode);
                }
            }
            catch (ArgumentException) { } // The identified short-lived restart already exited.
            catch (InvalidOperationException) { } // It exited while its image information was opened.
            NativePortableHostFixture.SamePath(target, restarted.RootElement.GetProperty("processPath").GetString());
            NativePortableHostFixture.SamePath(fixture.Installation, restarted.RootElement.GetProperty("applicationRoot").GetString());
            NativePortableHostFixture.SamePath(fixture.Cache, restarted.RootElement.GetProperty("appBaseDirectory").GetString());
            Assert.Empty(restarted.RootElement.GetProperty("args").EnumerateArray());
            Assert.False(File.Exists(journal));
            Assert.Equal(expectedHash, NativePortableHostFixture.HashFile(target));
            Assert.Equal("65002.0.0.0", FileVersionInfo.GetVersionInfo(target).FileVersion);
            Assert.False(File.Exists(Path.Combine(fixture.Cache, "audit-restarted.txt")));
            Assert.Equal(fixture.Records.Select(record => record.Path).Order(StringComparer.Ordinal),
                Directory.EnumerateFiles(fixture.Cache, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(fixture.Cache, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
            fixture.AssertPortable(await fixture.Probe(target), target);
            Assert.Equal(componentRequests, fixture.ComponentRequests);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(fixture.Root, "native-update-failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            if (!source.HasExited) { source.Kill(entireProcessTree: true); await source.WaitForExitAsync(); }
            if (helper is not null)
            {
                if (!helper.HasExited) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
                helper.Dispose();
            }
            fixture.StopOwnedNativeProcesses(target);
            // The installation teardown can remove this image if Windows has
            // not released it yet.
            try { File.Delete(target); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Assert.Equal(originalHash, NativePortableHostFixture.HashFile(fixture.Executable));
    }

    private static UpdateInstallJournal? ReadJournal(string path)
    {
        try { return JsonSerializer.Deserialize<UpdateInstallJournal>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The native updater fixture did not reach its expected state.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task UnicodeRenameForeignWorkingDirectoryAndQuotedArgumentsPreserveTheirIdentity()
    {
        var renamed = Path.Combine(fixture.Installation, "Лаунчер с пробелом.exe");
        var cwd = Path.Combine(fixture.Root, "unrelated cwd");
        Directory.CreateDirectory(cwd);
        // Invalid decoys must never win private/system DLL lookup.
        foreach (var name in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wpfgfx_cor3.dll" })
            File.WriteAllText(Path.Combine(cwd, name), "Not a native library.");
        var arguments = new[] { "--rendering-probe", "", "слова 日本語", "a\"b", "trailing\\", "--skip-prestart-update=fixture-nonce" };
        File.Move(fixture.Executable, renamed);
        try
        {
            var result = await fixture.Probe(renamed, cwd, arguments);
            fixture.AssertPortable(result, renamed);
            Assert.Equal(arguments, result.Json.GetProperty("args").EnumerateArray().Select(value => value.GetString()));
        }
        finally { File.Move(renamed, fixture.Executable); }
    }

    [Fact]
    public async Task ParallelStartsReleasePreparationBeforeManagedExecutionAndHoldVerifiedFilesReadOnly()
    {
        fixture.AssertPortable(await fixture.Probe(timeoutSeconds: 90));
        fixture.RemoveCache();
        fixture.RemoveExternalCache("desktop-runtime");
        var runtimeRequests = fixture.Server.Requests.Count(request => request.Kind == "desktop-runtime");
        using var first = fixture.Start(arguments: ["--rendering-probe", "--probe-hold-ms=8000"]);
        var firstErrors = first.StandardError.ReadToEndAsync();
        try
        {
            var line = await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.NotNull(line);
            using var entry = JsonDocument.Parse(line);
            Assert.Equal(first.Id, entry.RootElement.GetProperty("processId").GetInt32());
            Assert.Throws<IOException>(() =>
            {
                using var denied = File.Open(Path.Combine(fixture.Cache, "PortabilityAuditHost.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
            var core = Path.Combine(fixture.ComponentCache("desktop-runtime"), "shared", "Microsoft.NETCore.App",
                fixture.Components["desktop-runtime"].EntryPath.Split('/')[2], "coreclr.dll");
            Assert.Throws<IOException>(() => { using var denied = File.Open(core, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            var second = await fixture.Probe(timeoutSeconds: 6);
            fixture.AssertPortable(second);
            Assert.False(first.HasExited, "A second managed entry must run while the first entry still owns its verified runtime files.");
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(first.ExitCode == 0, await firstErrors);
            Assert.Equal(runtimeRequests + 1, fixture.Server.Requests.Count(request => request.Kind == "desktop-runtime"));
        }
        finally
        {
            if (!first.HasExited) { first.Kill(entireProcessTree: true); await first.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACorruptOrIncompleteCacheIsRepairedBeforeManagedEntry(bool missing)
    {
        fixture.AssertPortable(await fixture.Probe());
        var target = Path.Combine(fixture.Cache, "PortabilityAuditHost.dll");
        if (missing) File.Delete(target);
        else File.WriteAllText(target, "Interrupted or corrupt private payload.");
        fixture.AssertPortable(await fixture.Probe());
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Payload, "PortabilityAuditHost.dll"))), SHA256.HashData(File.ReadAllBytes(target)));
    }

    [Fact]
    public async Task AnUnlistedCacheFileIsRejectedBeforeManagedEntry()
    {
        fixture.AssertPortable(await fixture.Probe());
        var extra = Path.Combine(fixture.Cache, "unlisted.dll");
        File.WriteAllText(extra, "An unexpected cache item must not be loaded or silently accepted.");
        try { await fixture.AssertRejected("Unexpected runtime cache file"); }
        finally { File.Delete(extra); }
    }

    [Fact]
    public async Task AHardLinkedCacheFileIsRejectedWithoutChangingTheOutsideCopy()
    {
        fixture.AssertPortable(await fixture.Probe());
        var target = Path.Combine(fixture.Cache, "PortabilityAuditHost.dll");
        var outside = Path.Combine(fixture.Root, "outside-copy.dll");
        File.Copy(target, outside, overwrite: true);
        var hash = SHA256.HashData(File.ReadAllBytes(outside));
        File.Delete(target);
        try
        {
            if (!CreateHardLink(@"\\?\" + Path.GetFullPath(target), @"\\?\" + Path.GetFullPath(outside), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            await fixture.AssertRejected("Hard-linked file in the launcher runtime cache");
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(outside)));
        }
        finally { File.Delete(target); File.Copy(outside, target); }
    }

    [Fact]
    public async Task AReparsedInstallationParentCannotWriteOutsideTheInstallation()
    {
        fixture.AssertPortable(await fixture.Probe());
        var minecraft = Path.Combine(fixture.Installation, "Minecraft");
        var held = Path.Combine(fixture.Root, "held-minecraft");
        var outside = Path.Combine(fixture.Root, "junction-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "Unchanged.");
        fixture.AssertInsideRoot(minecraft);
        fixture.AssertInsideRoot(held);
        Directory.Move(minecraft, held);
        try
        {
            SavesFolderService.CreateJunction(minecraft, outside);
            await fixture.AssertRejected("Reparse point or unexpected file type in the launcher runtime cache");
            Assert.Equal(new[] { "sentinel.txt" }, Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
            Assert.Equal("Unchanged.", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        }
        finally
        {
            if (Directory.Exists(minecraft))
            {
                Assert.True((File.GetAttributes(minecraft) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(minecraft, recursive: false);
            }
            Directory.Move(held, minecraft);
        }
    }

    [Theory]
    [InlineData("body hash")]
    [InlineData("bounds")]
    [InlineData("reserved")]
    [InlineData("trailing bytes")]
    [InlineData("invalid UTF8")]
    [InlineData("unsafe path")]
    [InlineData("duplicate path")]
    [InlineData("file directory conflict")]
    [InlineData("file hash")]
    public async Task MalformedPacketsAreRejectedBeforeAnyManagedCodeRuns(string mutation)
    {
        var packet = fixture.MutatePacket(mutation);
        var probe = Path.Combine(fixture.Installation, $"malformed-{Guid.NewGuid():N}.exe");
        fixture.AssertInsideRoot(probe);
        File.Copy(fixture.Executable, probe);
        var expected = mutation switch
        {
            "body hash" => "Launcher runtime packet SHA256 mismatch",
            "bounds" or "trailing bytes" => "Invalid launcher runtime packet bounds",
            "reserved" => "Invalid launcher runtime packet header",
            "invalid UTF8" => "Invalid UTF-8 runtime path",
            "unsafe path" => "Non-canonical runtime path",
            "duplicate path" => "Duplicate, overlapping or out-of-bounds runtime file",
            "file directory conflict" => "Runtime file conflicts with a directory",
            "file hash" => "Embedded runtime file SHA256 mismatch",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        try
        {
            NativeHostResources.ReplacePacket(probe, packet);
            await fixture.AssertRejected(expected, probe);
        }
        finally
        {
            // The installation teardown also owns this file if Windows still
            // holds it after the probe exits.
            try { File.Delete(probe); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task ExternalColdPreparationDownloadsPinnedComponentsOnceAndRunsOfflineWarm()
    {
        fixture.Server.ResetFaults();
        foreach (var kind in fixture.Components.Keys) fixture.RemoveExternalCache(kind);
        var before = fixture.ComponentRequests;
        try
        {
            fixture.AssertPortable(await fixture.Probe(timeoutSeconds: 90));
            Assert.Equal(before + 3, fixture.ComponentRequests);
            foreach (var component in fixture.Components.Values)
            {
                var cache = fixture.ComponentCache(component.Kind);
                Assert.True(File.Exists(Path.Combine(cache, component.EntryPath.Replace('/', Path.DirectorySeparatorChar))));
                Assert.Equal(component.ManifestSha256, NativePortableHostFixture.HashFile(cache + ".manifest"), ignoreCase: true);
                Assert.Equal($"LMDESK01\n{component.Kind}\n{component.Sha256}\n", File.ReadAllText(cache + ".lease"));
                Assert.False(File.Exists(cache + ".part"));
            }
            var requests = fixture.ComponentRequests;
            fixture.Server.Offline = true;
            fixture.AssertPortable(await fixture.Probe());
            Assert.Equal(requests, fixture.ComponentRequests);
        }
        finally { fixture.Server.ResetFaults(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingNativePreparationRemovesItsPartialAndNeverEntersManagedCode(bool afterError)
    {
        fixture.Server.ResetFaults();
        fixture.RemoveExternalCache("desktop-runtime");
        fixture.Server.ChunkDelayMilliseconds = afterError ? 0 : 4;
        fixture.Server.FaultKind = "desktop-runtime";
        fixture.Server.Fault = afterError ? NativeDesktopFault.DisconnectOnce : NativeDesktopFault.None;
        using var process = fixture.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            var window = await NativeDesktopPreparationUi.Wait(process, afterError ? NativeDesktopPreparationUi.RetryReady : null);
            await WaitUntil(() => File.Exists(fixture.ComponentCache("desktop-runtime") + ".part") &&
                new FileInfo(fixture.ComponentCache("desktop-runtime") + ".part").Length > 0, TimeSpan.FromSeconds(15));
            NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Cancel);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1223, process.ExitCode);
            Assert.Equal("", (await output).Trim());
            Assert.Contains("LANMinecraft bootstrap cancelled", await error, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ComponentCache("desktop-runtime") + ".part"));
            fixture.AssertComponentUnpublished("desktop-runtime");
            fixture.Server.ResetFaults();
            fixture.AssertPortable(await fixture.Probe(timeoutSeconds: 90));
            if (afterError)
                foreach (var failedKind in new[] { "assets", "steam-native" })
                    await CancelImmediatelyAfterRetryWithAnEarlierPartial(failedKind);
        }
        finally
        {
            fixture.Server.ResetFaults();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private async Task CancelImmediatelyAfterRetryWithAnEarlierPartial(string kind)
    {
        fixture.Server.ResetFaults();
        fixture.RemoveExternalCache(kind);
        fixture.Server.FaultKind = kind;
        fixture.Server.Fault = NativeDesktopFault.DisconnectOnce;
        fixture.Server.DisconnectAfterBytes = 131072;
        using var process = fixture.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            var window = await NativeDesktopPreparationUi.Wait(process, NativeDesktopPreparationUi.RetryReady);
            var partial = fixture.ComponentCache(kind) + ".part";
            Assert.True(File.Exists(partial));
            Assert.True(new FileInfo(partial).Length > 0);
            var failedRequests = fixture.Server.Requests.Count(request => request.Kind == kind);
            NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Retry);
            NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Cancel);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1223, process.ExitCode);
            Assert.Equal("", (await output).Trim());
            Assert.Contains("LANMinecraft bootstrap cancelled", await error, StringComparison.Ordinal);
            // Retry is still checking the previously cached runtime. This
            // proves the old failed component's partial also gets removed.
            Assert.Equal(failedRequests, fixture.Server.Requests.Count(request => request.Kind == kind));
            Assert.False(File.Exists(partial));
        }
        finally
        {
            fixture.Server.ResetFaults();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedExternalTransferResumesAfterNativeRetryAndHandlesIgnoredRange(bool ignoreRange)
    {
        fixture.Server.ResetFaults();
        fixture.RemoveExternalCache("desktop-runtime");
        fixture.Server.FaultKind = "desktop-runtime";
        fixture.Server.Fault = ignoreRange ? NativeDesktopFault.IgnoreRange : NativeDesktopFault.DisconnectOnce;
        var before = fixture.ComponentRequests;
        using var process = fixture.Start();
        try
        {
            var window = await NativeDesktopPreparationUi.Wait(process, NativeDesktopPreparationUi.RetryReady);
            var partial = fixture.ComponentCache("desktop-runtime") + ".part";
            Assert.True(File.Exists(partial));
            var length = new FileInfo(partial).Length;
            Assert.InRange(length, 1, fixture.Components["desktop-runtime"].SizeBytes - 1);
            fixture.AssertComponentUnpublished("desktop-runtime");
            NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Retry);
            fixture.AssertPortable(await fixture.CompleteProbe(process, 90));
            var resumed = fixture.Server.Requests.Skip(before).Where(request => request.Kind == "desktop-runtime").ToArray();
            Assert.Equal(2, resumed.Length);
            Assert.Null(resumed[0].Range);
            Assert.Equal("bytes=" + length + "-", resumed[1].Range);
            Assert.Equal(ignoreRange ? 200 : 206, resumed[1].Status);
            Assert.False(File.Exists(partial));
        }
        finally
        {
            fixture.Server.ResetFaults();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public async Task CorruptExternalDownloadStaysUnpublishedUntilNativeRetryVerifiesThePinnedBytes()
    {
        fixture.Server.ResetFaults();
        fixture.RemoveExternalCache("desktop-runtime");
        fixture.Server.FaultKind = "desktop-runtime";
        fixture.Server.Fault = NativeDesktopFault.CorruptOnce;
        using var process = fixture.Start();
        try
        {
            var window = await NativeDesktopPreparationUi.Wait(process, NativeDesktopPreparationUi.RetryReady, 90);
            fixture.AssertComponentUnpublished("desktop-runtime");
            NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Retry);
            fixture.AssertPortable(await fixture.CompleteProbe(process, 90));
            var requests = fixture.ComponentRequests;
            fixture.Server.Offline = true;
            fixture.AssertPortable(await fixture.Probe());
            Assert.Equal(requests, fixture.ComponentRequests);
        }
        finally
        {
            fixture.Server.ResetFaults();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DamagedExternalRuntimeIsRepairedFromThePinnedSource(bool missing)
    {
        fixture.AssertPortable(await fixture.Probe(timeoutSeconds: 90));
        var target = Path.Combine(fixture.ComponentCache("desktop-runtime"), "shared", "Microsoft.NETCore.App",
            fixture.Components["desktop-runtime"].EntryPath.Split('/')[2], "coreclr.dll");
        if (missing) File.Delete(target);
        else File.WriteAllText(target, "Corrupt private pinned runtime, system .NET must not substitute it.");
        var requests = fixture.ComponentRequests;
        fixture.AssertPortable(await fixture.Probe(timeoutSeconds: 90));
        Assert.Equal(requests + 1, fixture.ComponentRequests);
    }

    [Fact]
    public async Task PreparationUiStartsCompactlyAndDoesNotRedrawUnchangedFailureState()
    {
        var observations = new List<object>();
        foreach (var show in new[] { ProcessWindowStyle.Normal, ProcessWindowStyle.Maximized, ProcessWindowStyle.Minimized })
        {
            fixture.Server.ResetFaults();
            fixture.RemoveExternalCache("desktop-runtime");
            fixture.Server.FaultKind = "desktop-runtime";
            fixture.Server.Fault = NativeDesktopFault.HttpFailureOnce;
            using var process = fixture.Start(windowStyle: show);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                var window = await NativeDesktopPreparationUi.Wait(process, NativeDesktopPreparationUi.RetryReady);
                observations.Add(new { callerWindowStyle = show.ToString(),
                    initial = NativeDesktopPreparationUi.AssertInitialCompact(process, window),
                    idle = NativeDesktopPreparationUi.AssertIdleDoesNotMutateControls(process, window) });
                NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Cancel);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(1223, process.ExitCode);
                Assert.Equal("", (await output).Trim());
                Assert.Contains("LANMinecraft bootstrap cancelled", await error, StringComparison.Ordinal);
            }
            finally
            {
                fixture.Server.ResetFaults();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Task.WhenAll(output, error);
            }
        }
        File.WriteAllText(Path.Combine(fixture.Root, "native-preparation-compact-idle.json"), JsonSerializer.Serialize(observations));
    }

    [Fact]
    public async Task PreparationUiKeepsControlsReadableThroughProgrammaticDpiChanges()
    {
        var layouts = new List<object>();
        foreach (var afterError in new[] { false, true })
        {
            fixture.Server.ResetFaults();
            fixture.RemoveExternalCache("desktop-runtime");
            fixture.Server.FaultKind = "desktop-runtime";
            fixture.Server.Fault = afterError ? NativeDesktopFault.HttpFailureOnce : NativeDesktopFault.None;
            fixture.Server.ChunkDelayMilliseconds = afterError ? 0 : 4;
            using var process = fixture.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                var window = await NativeDesktopPreparationUi.Wait(process,
                    afterError ? NativeDesktopPreparationUi.RetryReady : NativeDesktopPreparationUi.HasProgressMetrics);
                foreach (var errorCode in afterError ? new[] { 0, 5, 112 } : new[] { -1 })
                {
                    if (afterError) NativeDesktopPreparationUi.ErrorText(process, window, errorCode);
                    foreach (var dpi in new[] { 96, 120, 144, 192 })
                        layouts.Add(new { state = afterError ? "retryable-error" : "active-transfer", errorCode,
                            layout = NativeDesktopPreparationUi.AssertLayout(process, window, dpi) });
                }
                NativeDesktopPreparationUi.Command(process, window, NativeDesktopPreparationUi.Cancel);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(1223, process.ExitCode);
                Assert.Equal("", (await output).Trim());
                Assert.Contains("LANMinecraft bootstrap cancelled", await error, StringComparison.Ordinal);
            }
            finally
            {
                fixture.Server.ResetFaults();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        File.WriteAllText(Path.Combine(fixture.Root, "native-preparation-dpi.json"), JsonSerializer.Serialize(layouts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidExternalDependencyDescriptorsAreRejectedBeforeDownload(bool duplicateKind)
    {
        var descriptor = NativeHostResources.Read(fixture.Executable, 10, 102);
        if (duplicateKind) BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(16), 2);
        else descriptor[0] ^= 0xff;
        var executable = Path.Combine(fixture.Installation, "invalid-component-descriptor.exe");
        File.Copy(fixture.Executable, executable, overwrite: true);
        try
        {
            NativeHostResources.ReplaceResource(executable, 102, descriptor);
            var requests = fixture.ComponentRequests;
            await fixture.AssertRejected(executable: executable);
            Assert.Equal(requests, fixture.ComponentRequests);
        }
        finally { File.Delete(executable); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

public sealed class NativePortableHostFixture : IAsyncLifetime
{
    public const string Revision = "1234567890abcdef1234567890abcdef12345678";
    public string Repository { get; } = FindRepository();
    public string Root { get; }
    public string Installation { get; }
    public string Executable => Path.Combine(Installation, "LANMinecraft.exe");
    public string Payload => Path.Combine(Root, "payload");
    public byte[] Packet { get; private set; } = [];
    public NativeHostRecord[] Records { get; private set; } = [];
    public string Bootstrap => Path.Combine(Installation, "Minecraft", "Launcher", "Bootstrap");
    public string Cache => Path.Combine(Bootstrap, "app", Convert.ToHexString(Packet.AsSpan(40, 32)).ToLowerInvariant());
    internal IReadOnlyDictionary<string, NativeDesktopComponent> Components { get; private set; } = new Dictionary<string, NativeDesktopComponent>();
    internal NativeDesktopComponentServer Server { get; private set; } = null!;
    public int ComponentRequests => Server.Requests.Count;
    private string ComponentLock => Path.Combine(Root, "desktop-components.test.lock.json");
    private string RuntimeVersion => Components["desktop-runtime"].EntryPath.Split('/')[2];
    private int run;
    private readonly List<(int Id, DateTime StartUtc, string Executable)> ownedProcesses = [];
    private readonly DateTime createdUtc = DateTime.UtcNow;
    private readonly string fixtureId;

    public NativePortableHostFixture()
    {
        var boundary = Path.Combine(Repository, "artifacts", "agent-runs");
        for (var ancestor = new DirectoryInfo(boundary); ancestor is not null && ancestor.FullName.Length >= Repository.Length; ancestor = ancestor.Parent)
            if (ancestor.Exists) Assert.False((ancestor.Attributes & FileAttributes.ReparsePoint) != 0);
        Directory.CreateDirectory(boundary);
        string? selectedRoot = null;
        string? selectedId = null;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var id = Guid.NewGuid().ToString("N");
            var candidate = Path.Combine(boundary, "n-" + id[..8]);
            if (CreateFixtureDirectory(candidate, IntPtr.Zero)) { selectedRoot = candidate; selectedId = id; break; }
            var error = Marshal.GetLastWin32Error();
            if (error is not (80 or 183)) throw new Win32Exception(error);
        }
        Root = selectedRoot ?? throw new IOException("Could not allocate a fresh isolated native fixture.");
        fixtureId = selectedId!;
        Installation = Root;
        using var marker = new FileStream(Path.Combine(Root, "RUN.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new
        {
            owner = "NativePortableHostTests", purpose = "Isolated native external-desktop regression fixture",
            fixtureId, createdUtc, expectedBytes = 1024L * 1024 * 1024, deleteByUtc = createdUtc.AddHours(4),
            requiredResults = new[] { "run-*.json", "native-build-host-build.json", "cleanup.json", "component-requests.json" }
        });
    }

    public async Task InitializeAsync()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var shared = Environment.GetEnvironmentVariable("LANMINECRAFT_DESKTOP_TEST_COMPONENTS")
            ?? throw new InvalidOperationException("Run tools/Prepare-DesktopComponentTests.ps1 before native tests.");
        var catalogJson = File.ReadAllText(Path.Combine(shared, "desktop-components.json"));
        File.WriteAllText(Path.Combine(Root, "desktop-components.snapshot.json"), catalogJson);
        Components = LoadComponents(catalogJson);
        Server = new NativeDesktopComponentServer(Components);
        Server.WriteLock(catalogJson, ComponentLock);
        var project = Path.Combine(Repository, "tools", "NativeDesktopAuditHost", "NativeDesktopAuditHost.csproj");
        await Command("dotnet", ["restore", project, "--no-dependencies", "--disable-parallel", "-nr:false", "-p:Configuration=" + configuration,
            "-p:RuntimeFrameworkVersion=" + RuntimeVersion], "restore");
        await Command("dotnet", ["publish", project, "--no-restore", "-c", configuration, "-o", Payload,
            "--disable-build-servers", "-m:1", "-nr:false", "-p:BuildProjectReferences=false", "-p:SkipPortableIdentityAdapter=true",
            "-p:RuntimeFrameworkVersion=" + RuntimeVersion], "publish");
        using (var runtimeConfig = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Payload, "PortabilityAuditHost.runtimeconfig.json"))))
        {
            var options = runtimeConfig.RootElement.GetProperty("runtimeOptions");
            Assert.Equal("Disable", options.GetProperty("rollForward").GetString());
            foreach (var framework in options.GetProperty("frameworks").EnumerateArray())
                Assert.Equal(RuntimeVersion, framework.GetProperty("version").GetString());
        }
        await Pack(Executable, 65001, "native-build", Revision);
        var stage = Path.Combine(Root, "native-build");
        Packet = File.ReadAllBytes(Path.Combine(stage, "payload.packet"));
        Records = ReadRecords(Packet);
    }

    public async Task Pack(string output, int release, string stageName, string revision)
    {
        AssertInsideRoot(output);
        var stage = Path.Combine(Root, stageName);
        AssertInsideRoot(stage);
        await Command("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(Repository, "Program", "Bootstrap", "Build-PortableHost.ps1"),
            "-PayloadDirectory", Payload, "-OutputExecutable", output, "-ReleaseNumber", release.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-SourceRevisionId", revision, "-EntryAssembly", "PortabilityAuditHost.dll", "-ArtifactsDirectory", stage,
            "-ComponentLockPath", ComponentLock, "-AllowTestComponentSources"], stageName + "-pack");
    }

    private IReadOnlyDictionary<string, NativeDesktopComponent> LoadComponents(string catalogJson)
    {
        var shared = Environment.GetEnvironmentVariable("LANMINECRAFT_DESKTOP_TEST_COMPONENTS");
        if (string.IsNullOrWhiteSpace(shared))
            throw new InvalidOperationException("Run tools/Prepare-DesktopComponentTests.ps1 once and set LANMINECRAFT_DESKTOP_TEST_COMPONENTS before native tests. System .NET is not a fallback.");
        using var document = JsonDocument.Parse(catalogJson);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("protocolVersion").GetInt32());
        var components = new Dictionary<string, NativeDesktopComponent>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.GetProperty("components").EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString()!;
            var url = new Uri(item.GetProperty("url").GetString()!);
            var store = BootstrapLaunchContext.TestComponentStore(shared, kind);
            var packet = Path.Combine(store, Path.GetFileName(url.AbsolutePath));
            var component = new NativeDesktopComponent(kind, item.GetProperty("id").GetString()!, item.GetProperty("entryPath").GetString()!,
                item.GetProperty("sizeBytes").GetInt64(), item.GetProperty("sha256").GetString()!,
                item.GetProperty("manifestSizeBytes").GetInt64(), item.GetProperty("manifestSha256").GetString()!, packet);
            Assert.Equal(component.SizeBytes, new FileInfo(packet).Length);
            Assert.Equal(component.Sha256, HashFile(packet), ignoreCase: true);
            var manifest = Path.Combine(store, kind + ".manifest");
            Assert.Equal(component.ManifestSizeBytes, new FileInfo(manifest).Length);
            Assert.Equal(component.ManifestSha256, HashFile(manifest), ignoreCase: true);
            using (var stream = File.OpenRead(packet))
            {
                var prefix = new byte[checked((int)component.ManifestSizeBytes)];
                stream.ReadExactly(prefix);
                Assert.Equal(File.ReadAllBytes(manifest), prefix);
            }
            components.Add(kind, component);
        }
        Assert.Equal(new[] { "assets", "desktop-runtime", "steam-native" }, components.Keys.Order(StringComparer.Ordinal));
        return components;
    }

    public string ComponentCache(string kind) => Path.Combine(Bootstrap, kind, Components[kind].Sha256);

    public void AssertComponentUnpublished(string kind)
    {
        var component = Components[kind];
        Assert.False(File.Exists(Path.Combine(ComponentCache(kind), component.EntryPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(ComponentCache(kind) + ".manifest"));
    }

    public void RemoveExternalCache(string kind)
    {
        var directory = Path.Combine(Bootstrap, kind);
        AssertInsideRoot(directory);
        if (!Directory.Exists(directory)) return;
        Assert.True(HasOnlyOrdinaryDescendants(directory));
        Directory.Delete(directory, recursive: true);
    }

    public void AssertDependencyResource()
    {
        using var stream = new MemoryStream(NativeHostResources.Read(Executable, 10, 102));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        Assert.Equal("LMDESK01", Encoding.ASCII.GetString(reader.ReadBytes(8)));
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        foreach (var kind in new[] { "desktop-runtime", "assets", "steam-native" })
        {
            var expected = Components[kind];
            Assert.Equal((uint)(kind == "desktop-runtime" ? 1 : kind == "assets" ? 2 : 3), reader.ReadUInt32());
            Assert.Equal(expected.Id, ReadString());
            Assert.Equal(new Uri(Server.BaseUri, kind + ".pak").AbsoluteUri, ReadString());
            Assert.Equal("", ReadString());
            Assert.Equal(expected.EntryPath, ReadString());
            Assert.Equal((ulong)expected.SizeBytes, reader.ReadUInt64());
            Assert.Equal(Convert.FromHexString(expected.Sha256), reader.ReadBytes(32));
            Assert.Equal((ulong)expected.ManifestSizeBytes, reader.ReadUInt64());
            Assert.Equal(Convert.FromHexString(expected.ManifestSha256), reader.ReadBytes(32));
        }
        Assert.Equal(stream.Length, stream.Position);
        string ReadString() => new UTF8Encoding(false, true).GetString(reader.ReadBytes(checked((int)reader.ReadUInt32())));
    }

    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public async Task DisposeAsync()
    {
        // Only this exact newly-created GUID can be cleaned. Previous runs,
        // including failed or externally retained evidence, are never visited.
        var boundary = Path.GetFullPath(Path.Combine(Repository, "artifacts", "agent-runs"));
        Assert.Equal(boundary, Path.GetDirectoryName(Path.GetFullPath(Root)), ignoreCase: true);
        Assert.True(Guid.TryParseExact(fixtureId, "N", out _));
        Assert.Equal("n-" + fixtureId[..8], Path.GetFileName(Root));
        using (var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "RUN.json"))))
            Assert.Equal(fixtureId, marker.RootElement.GetProperty("fixtureId").GetString());
        Assert.False((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0);
        var actions = new List<string>();
        try
        {
            foreach (var owned in ownedProcesses)
            {
                try
                {
                    using var process = Process.GetProcessById(owned.Id);
                    if (!process.HasExited && process.StartTime.ToUniversalTime() == owned.StartUtc &&
                        string.Equals(NormalizePath(ProcessImagePath(process)), NormalizePath(owned.Executable), StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        actions.Add("Stopped exactly identified owned process " + owned.Id + ".");
                    }
                }
                catch (ArgumentException) { } // The exact owned process has exited.
                catch (InvalidOperationException) { }
                catch (Win32Exception)
                {
                    actions.Add("Kept bulky fixture files because an owned process could not be checked.");
                    File.WriteAllText(Path.Combine(Root, "cleanup.json"), JsonSerializer.Serialize(actions));
                    return;
                }
            }
        }
        finally
        {
            if (Server is not null)
            {
                await Server.DisposeAsync();
                File.WriteAllText(Path.Combine(Root, "component-requests.json"), JsonSerializer.Serialize(Server.Requests.Select(request =>
                    new { request.Kind, request.Range, request.Attempt, request.Status, request.BytesSent })));
            }
        }
        foreach (var name in new[] { "native-build", "native-update-build", "payload", "Minecraft", "inherited-temp", "held-minecraft", "junction-target", "unrelated cwd" })
        {
            var directory = Path.Combine(Root, name);
            AssertInsideRoot(directory);
            if (!Directory.Exists(directory)) continue;
            try
            {
                if (!HasOnlyOrdinaryDescendants(directory))
                {
                    actions.Add("Kept " + name + " because a reparse entry is present.");
                    continue;
                }
                foreach (var relative in new[] { "host-build.json", "native-build.log", Path.Combine("Launcher", "bootstrap.log") })
                {
                    var evidence = Path.Combine(directory, relative);
                    if (File.Exists(evidence)) File.Copy(evidence, Path.Combine(Root, name + "-" + Path.GetFileName(evidence)), overwrite: true);
                }
                Directory.Delete(directory, recursive: true);
                actions.Add("Removed newly owned bulk directory " + name + ".");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                actions.Add("Kept " + name + ": " + error.GetType().Name + ".");
            }
        }
        foreach (var file in Directory.EnumerateFiles(Root))
        {
            var name = Path.GetFileName(file);
            var malformed = name.StartsWith("malformed-", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal) &&
                Guid.TryParseExact(name["malformed-".Length..^4], "N", out _);
            if (!malformed && name is not ("LANMinecraft.exe" or "updater-probe.exe" or "Лаунчер с пробелом.exe" or "invalid-component-descriptor.exe" or "outside-copy.dll")) continue;
            AssertInsideRoot(file);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { actions.Add("Kept linked fixture file " + name + "."); continue; }
            try { File.Delete(file); actions.Add("Removed owned generated file " + name + "."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { actions.Add("Kept " + name + ": " + error.GetType().Name + "."); }
        }
        File.WriteAllText(Path.Combine(Root, "cleanup.json"), JsonSerializer.Serialize(actions));
        Packet = [];
        Records = [];
    }

    private bool HasOnlyOrdinaryDescendants(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            AssertInsideRoot(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                AssertInsideRoot(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return true;
    }

    // Reading MainModule enumerates a process's not-yet-initialized loader and
    // can fail with ERROR_PARTIAL_COPY immediately after starting PowerShell.
    // Query the executable directly while retaining the exact process handle.
    public static string ProcessImagePath(Process process)
    {
        var buffer = new char[32768];
        var size = buffer.Length;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, buffer, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new string(buffer, 0, size);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags, [Out] char[] name, ref int size);

    public void TrackProcess(Process process, string executable) => ownedProcesses.Add((process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(executable)));

    public void StopOwnedNativeProcesses(string executable)
    {
        AssertInsideRoot(executable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.StartTime.ToUniversalTime() >= createdUtc.AddSeconds(-1) &&
                        string.Equals(NormalizePath(ProcessImagePath(process)), NormalizePath(executable), StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        Assert.True(process.WaitForExit(10000), "The owned native restart fixture did not stop.");
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { } // Other processes with the same name are not touched.
            }
    }

    private async Task Command(string executable, string[] arguments, string name)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Repository };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture command did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        var log = (await output) + (await error);
        File.WriteAllText(Path.Combine(Root, name + ".log"), log);
        Assert.True(process.ExitCode == 0, $"Native fixture {name} failed ({process.ExitCode}); see {Root}. {log[^Math.Min(log.Length, 3000)..]}");
    }

    public Process Start(string? executable = null, string? cwd = null, string[]? arguments = null, bool allowPowerShell = false,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal)
    {
        var start = new ProcessStartInfo(executable ?? Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? Root, WindowStyle = windowStyle };
        foreach (var argument in arguments ?? ["--rendering-probe"]) start.ArgumentList.Add(argument);
        // The private pinned runtime must work without an installed dotnet/runtime
        // on PATH. Do not spoof Windows Known Folders or the user's profile.
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        start.Environment["PATH"] = system32 + (allowPowerShell ? ";" + Path.Combine(system32, "WindowsPowerShell", "v1.0") : string.Empty);
        start.Environment["DOTNET_ROOT"] = Path.Combine(Root, "no-installed-runtime");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(Root, "no-installed-runtime");
        var inheritedTemp = Path.Combine(Root, "inherited-temp");
        Directory.CreateDirectory(inheritedTemp);
        start.Environment["TEMP"] = inheritedTemp;
        start.Environment["TMP"] = inheritedTemp;
        var process = Process.Start(start) ?? throw new InvalidOperationException("The owned native fixture did not start.");
        TrackProcess(process, executable ?? Executable);
        return process;
    }

    public async Task<NativeHostProbe> Probe(string? executable = null, string? cwd = null, string[]? arguments = null, int timeoutSeconds = 25)
    {
        var result = await Run(executable, cwd, arguments, timeoutSeconds);
        Assert.True(result.ExitCode == 0, $"Native probe exited {result.ExitCode}: {result.Error}\n{result.Output}");
        using var json = JsonDocument.Parse(result.Output);
        return new NativeHostProbe(result.ProcessId, json.RootElement.Clone());
    }

    public async Task<NativeHostProbe> CompleteProbe(Process process, int timeoutSeconds)
    {
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        var stdout = await output;
        var stderr = await error;
        File.WriteAllText(Path.Combine(Root, $"run-{Interlocked.Increment(ref run):D2}.json"), JsonSerializer.Serialize(new { process.Id, process.ExitCode, stdout, stderr }));
        Assert.True(process.ExitCode == 0, $"Native probe exited {process.ExitCode}: {stderr}\n{stdout}");
        using var document = JsonDocument.Parse(stdout);
        return new NativeHostProbe(process.Id, document.RootElement.Clone());
    }

    public async Task AssertRejected(string? expectedError = null, string? executable = null)
    {
        var result = await Run(executable, timeoutSeconds: 15);
        Assert.Equal(120, result.ExitCode);
        Assert.Contains("LANMinecraft bootstrap failed:", result.Error, StringComparison.Ordinal);
        if (expectedError is not null) Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output.Trim());
    }

    private async Task<(int ProcessId, int ExitCode, string Output, string Error)> Run(string? executable = null, string? cwd = null, string[]? arguments = null, int timeoutSeconds = 25)
    {
        using var process = Start(executable, cwd, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        var stdout = await output;
        var stderr = await error;
        File.WriteAllText(Path.Combine(Root, $"run-{Interlocked.Increment(ref run):D2}.json"), JsonSerializer.Serialize(new { process.Id, process.ExitCode, stdout, stderr }));
        return (process.Id, process.ExitCode, stdout, stderr);
    }

    public void AssertPortable(NativeHostProbe probe, string? executable = null)
    {
        var json = probe.Json;
        Assert.Equal(probe.ProcessId, json.GetProperty("processId").GetInt32());
        SamePath(executable ?? Executable, json.GetProperty("processPath").GetString());
        SamePath(Installation, json.GetProperty("applicationRoot").GetString());
        Assert.Equal(Path.GetFullPath(Installation), Path.GetFullPath(json.GetProperty("applicationRoot").GetString()!), ignoreCase: true);
        SamePath(Cache, json.GetProperty("appBaseDirectory").GetString());
        var runtime = ComponentCache("desktop-runtime");
        var core = Path.Combine(runtime, "shared", "Microsoft.NETCore.App", RuntimeVersion);
        var desktop = Path.Combine(runtime, "shared", "Microsoft.WindowsDesktop.App", RuntimeVersion);
        SamePath(core, json.GetProperty("runtimeDirectory").GetString());
        Assert.Equal("STA", json.GetProperty("entryApartment").GetString());
        var temp = Path.Combine(Installation, "Minecraft", "Launcher", "Temp");
        foreach (var property in new[] { "temporaryDirectory", "temporaryEnvironment", "temporaryEnvironmentAlias" })
            SamePath(temp, json.GetProperty(property).GetString());
        SamePath(Path.Combine(Installation, "Minecraft", "Launcher", "DotNetBundles"), json.GetProperty("bundleExtractEnvironment").GetString());
        var modules = json.GetProperty("runtimeModules");
        SamePath(Path.Combine(runtime, Components["desktop-runtime"].EntryPath.Replace('/', Path.DirectorySeparatorChar)), modules.GetProperty("hostfxr.dll").GetString());
        foreach (var name in new[] { "hostpolicy.dll", "coreclr.dll" })
            SamePath(Path.Combine(core, name), modules.GetProperty(name).GetString());
        SamePath(Path.Combine(desktop, "wpfgfx_cor3.dll"), modules.GetProperty("wpfgfx_cor3.dll").GetString());
        var pen = Assert.Single(modules.EnumerateObject(), module => module.Name.Equals("PenImc_cor3.dll", StringComparison.OrdinalIgnoreCase));
        SamePath(Path.Combine(desktop, "PenImc_cor3.dll"), pen.Value.GetString());
        foreach (var module in modules.EnumerateObject())
            Assert.StartsWith(NormalizePath(runtime) + Path.DirectorySeparatorChar, NormalizePath(module.Value.GetString()!), StringComparison.OrdinalIgnoreCase);
        Assert.True(json.GetProperty("switchEnabled").GetBoolean());
        Assert.True(json.GetProperty("wpfCachedDisableDirtyRectangles").GetBoolean());
        Assert.Equal("SoftwareOnly", json.GetProperty("processRenderMode").GetString());
        Assert.Equal(5, json.GetProperty("preparedDialogWindows").GetInt32());
        Assert.Equal(0, json.GetProperty("windowsAfterDialogCleanup").GetInt32());
        Assert.True(json.GetProperty("probeWindowContentRendered").GetBoolean());
        Assert.True(json.GetProperty("probeWindowClosed").GetBoolean());
        Assert.True(json.GetProperty("probeWindowHadHwnd").GetBoolean());
        Assert.True(json.GetProperty("probeStartupSuppressed").GetBoolean());
        Assert.True(json.GetProperty("tabletDeviceCount").GetInt32() >= 0);
    }

    public static void SamePath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(File.Exists(expected) || Directory.Exists(expected), "Expected portable path is missing: " + expected);
        Assert.True(File.Exists(actual) || Directory.Exists(actual), "Reported portable path is missing: " + actual);
        Assert.Equal(NormalizePath(expected), NormalizePath(actual), ignoreCase: true);
    }

    public string? ShortExecutablePath()
    {
        var buffer = new char[32768];
        var length = GetShortPathName(Executable, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length) return null;
        var shortPath = new string(buffer, 0, (int)length);
        if (string.Equals(shortPath, Executable, StringComparison.OrdinalIgnoreCase)) return null;
        SamePath(Executable, shortPath);
        File.WriteAllText(Path.Combine(Root, "short-executable-path.json"), JsonSerializer.Serialize(new { shortPath, canonicalPath = Executable }));
        return shortPath;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateFixtureDirectory(string path, IntPtr securityAttributes);

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (File.Exists(full) || Directory.Exists(full))
        {
            var extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
                : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
            using var handle = OpenPhysicalPath(extended, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
            full = new string(buffer, 0, (int)length).TrimEnd('\\');
        }
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + full[8..];
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
    }

    public void AssertInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var ancestor = full;
        while (!File.Exists(ancestor) && !Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("No existing cleanup ancestor.");
        var canonical = Path.GetFullPath(Path.Combine(NormalizePath(ancestor), Path.GetRelativePath(ancestor, full)));
        Assert.StartsWith(NormalizePath(Root) + "\\", canonical, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenPhysicalPath(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, [Out] char[] path, uint size, uint flags);

    public void RemoveCache()
    {
        AssertInsideRoot(Cache);
        if (Directory.Exists(Cache)) Directory.Delete(Cache, recursive: true);
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Program", "Bootstrap", "Build-PortableHost.ps1"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the native host source checkout.");
    }

    public byte[] MutatePacket(string mutation)
    {
        var records = Records.Select(record => record with { Name = record.Name.ToArray(), Hash = record.Hash.ToArray() }).ToArray();
        var harmless = Array.FindIndex(records, record => record.Path is not ("hostfxr.dll" or "hostpolicy.dll" or "coreclr.dll" or "PortabilityAuditHost.dll"));
        Assert.True(harmless >= 0);
        switch (mutation)
        {
            case "invalid UTF8": records[harmless] = records[harmless] with { Name = [0xff] }; break;
            case "unsafe path": records[harmless] = records[harmless] with { Name = Encoding.UTF8.GetBytes("../outside.dll") }; break;
            case "duplicate path": records[harmless] = records[harmless] with { Name = Encoding.UTF8.GetBytes("PORTABILITYAUDITHOST.DLL") }; break;
            case "file directory conflict": records[harmless] = records[harmless] with { Name = Encoding.UTF8.GetBytes("PortabilityAuditHost.dll/child.dll") }; break;
            case "file hash": records[harmless].Hash[0] ^= 0xff; break;
        }
        using var table = new MemoryStream();
        using (var writer = new BinaryWriter(table, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Packet.AsSpan(72, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(Packet.AsSpan(16)))));
            foreach (var record in records)
            {
                writer.Write((uint)record.Name.Length); writer.Write(record.Name);
                writer.Write(record.Offset); writer.Write(record.Length); writer.Write(record.Hash);
            }
        }
        var oldTableLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(Packet.AsSpan(24)));
        var tableBytes = table.ToArray();
        var dataLength = Packet.Length - 72 - oldTableLength;
        var bytes = new byte[72 + tableBytes.Length + dataLength + (mutation == "trailing bytes" ? 1 : 0)];
        Packet.AsSpan(0, 72).CopyTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), (ulong)tableBytes.Length);
        tableBytes.CopyTo(bytes, 72);
        Packet.AsSpan(72 + oldTableLength).CopyTo(bytes.AsSpan(72 + tableBytes.Length));
        SHA256.HashData(bytes.AsSpan(72), bytes.AsSpan(40, 32));
        switch (mutation)
        {
            case "body hash": bytes[40] ^= 0xff; break;
            case "bounds": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), ulong.MaxValue); break;
            case "reserved": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1); break;
        }
        return bytes;
    }

    private static NativeHostRecord[] ReadRecords(byte[] packet)
    {
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)));
        var position = 72 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)));
        var records = new NativeHostRecord[count];
        for (var index = 0; index < count; index++)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(position))); position += 4;
            var name = packet.AsSpan(position, length).ToArray(); position += length;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(position)); position += 8;
            var size = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(position)); position += 8;
            var hash = packet.AsSpan(position, 32).ToArray(); position += 32;
            records[index] = new NativeHostRecord(name, offset, size, hash);
        }
        return records;
    }
}

public sealed record NativeHostProbe(int ProcessId, JsonElement Json);
public sealed record NativeHostRecord(byte[] Name, ulong Offset, ulong Length, byte[] Hash)
{
    public string Path => Encoding.UTF8.GetString(Name);
}

internal static class NativeHostResources
{
    public static byte[] Read(string executable, int type, int id)
    {
        var module = LoadLibraryEx(executable, IntPtr.Zero, 0x00000002 | 0x00000020); // DATAFILE | IMAGE_RESOURCE
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var resource = FindResource(module, (IntPtr)id, (IntPtr)type);
            if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var length = checked((int)SizeofResource(module, resource));
            var data = LockResource(LoadResource(module, resource));
            if (data == IntPtr.Zero || length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length); return bytes;
        }
        finally { FreeLibrary(module); }
    }

    public static void ReplacePacket(string executable, byte[] packet)
        => ReplaceResource(executable, 101, packet);

    public static void ReplaceResource(string executable, int id, byte[] packet)
    {
        var module = LoadLibraryEx(executable, IntPtr.Zero, 0x00000002 | 0x00000020);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var languages = new List<ushort>();
        ResourceLanguage callback = (_, _, _, language, _) => { languages.Add(language); return true; };
        try
        {
            if (!EnumResourceLanguages(module, (IntPtr)10, (IntPtr)id, callback, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FreeLibrary(module); }
        Assert.Single(languages);
        var update = BeginUpdateResource(executable, false);
        if (update == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!UpdateResource(update, (IntPtr)10, (IntPtr)id, languages[0], packet, (uint)packet.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!EndUpdateResource(update, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            update = IntPtr.Zero;
        }
        finally { if (update != IntPtr.Zero) EndUpdateResource(update, true); }
    }

    private delegate bool ResourceLanguage(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceLanguagesW", SetLastError = true)]
    private static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, ResourceLanguage callback, IntPtr parameter);
    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResource(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr update, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
