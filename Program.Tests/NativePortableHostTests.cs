using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Minecraft.Tests;

[CollectionDefinition("Native portable host", DisableParallelization = true)]
public sealed class NativePortableHostCollection { }

// Every process and filesystem mutation belongs to this disposable installation.
// A shared, freshly published payload avoids copying the private CLR per case.
[Collection("Native portable host")]
public sealed class NativePortableHostTests(NativePortableHostFixture fixture) : IClassFixture<NativePortableHostFixture>
{
    [Fact]
    public void OuterResourcesAndPacketKeepTheProductIdentityWithoutTheUnusedSdkApphost()
    {
        var version = FileVersionInfo.GetVersionInfo(fixture.Executable);
        Assert.Equal("1.0.65001.0", version.FileVersion);
        Assert.Equal("1.0.65001+" + NativePortableHostFixture.Revision, version.ProductVersion);
        var group = NativeHostResources.Read(fixture.Executable, 14, 1); // GROUP_ICON
        var icon = File.ReadAllBytes(Path.Combine(fixture.Repository, "Program", "Minecraft.ico"));
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
        var paths = fixture.Records.Select(record => record.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("PortabilityAuditHost.exe", paths);
        foreach (var required in new[] { "PortabilityAuditHost.dll", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wpfgfx_cor3.dll" })
            Assert.Contains(required, paths);
        Assert.True(File.Exists(Path.Combine(fixture.Payload, "PortabilityAuditHost.exe")), "The SDK apphost must exist before the production packer deliberately omits it.");
    }

    [Fact]
    public async Task ColdAndWarmStartsUseThePrivateRuntimePortableTempAndTheOuterStaProcess()
    {
        fixture.RemoveCache();
        var cold = await fixture.Probe();
        fixture.AssertPortable(cold);
        var entry = Path.Combine(fixture.Cache, "PortabilityAuditHost.dll");
        var timestamp = File.GetLastWriteTimeUtc(entry);
        var warm = await fixture.Probe();
        fixture.AssertPortable(warm);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(entry));
        Assert.Equal(cold.Json.GetProperty("appBaseDirectory").GetString(), warm.Json.GetProperty("appBaseDirectory").GetString());
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
        var original = Path.Combine(fixture.Root, "updater-original.exe");
        File.Copy(fixture.Executable, original);
        var originalHash = NativePortableHostFixture.HashFile(original);
        var marker = Path.Combine(fixture.Installation, "audit-restarted.txt");
        File.Delete(marker);
        var journal = Path.Combine(updates, UpdateService.InstallJournalFileName);
        using var source = fixture.Start(arguments: ["install", fixture.Installation, fixture.Executable, manifestFile, prepared,
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
            NativePortableHostFixture.SamePath(fixture.Executable, receipt.RootElement.GetProperty("processPath").GetString());
            NativePortableHostFixture.SamePath(fixture.Installation, receipt.RootElement.GetProperty("applicationRoot").GetString());
            var helperId = receipt.RootElement.GetProperty("helperProcessId").GetInt32();
            helper = Process.GetProcessById(helperId);
            Assert.Equal("powershell", helper.ProcessName, ignoreCase: true);
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            NativePortableHostFixture.SamePath(powerShell, helper.MainModule!.FileName);
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
            Assert.Equal(originalHash, NativePortableHostFixture.HashFile(fixture.Executable));
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
                    NativePortableHostFixture.SamePath(fixture.Executable, restartedProcess.MainModule!.FileName);
                    Assert.False(restartedProcess.SafeHandle.IsInvalid);
                    fixture.TrackProcess(restartedProcess, fixture.Executable);
                    await restartedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(0, restartedProcess.ExitCode);
                }
            }
            catch (ArgumentException) { } // The identified short-lived restart already exited.
            catch (InvalidOperationException) { } // It exited while its image information was opened.
            NativePortableHostFixture.SamePath(fixture.Executable, restarted.RootElement.GetProperty("processPath").GetString());
            NativePortableHostFixture.SamePath(fixture.Installation, restarted.RootElement.GetProperty("applicationRoot").GetString());
            NativePortableHostFixture.SamePath(fixture.Cache, restarted.RootElement.GetProperty("appBaseDirectory").GetString());
            Assert.Empty(restarted.RootElement.GetProperty("args").EnumerateArray());
            Assert.False(File.Exists(journal));
            Assert.Equal(expectedHash, NativePortableHostFixture.HashFile(fixture.Executable));
            Assert.Equal("1.0.65002.0", FileVersionInfo.GetVersionInfo(fixture.Executable).FileVersion);
            Assert.False(File.Exists(Path.Combine(fixture.Cache, "audit-restarted.txt")));
            Assert.Equal(fixture.Records.Select(record => record.Path).Order(StringComparer.Ordinal),
                Directory.EnumerateFiles(fixture.Cache, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(fixture.Cache, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
            fixture.AssertPortable(await fixture.Probe());
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
            fixture.StopOwnedNativeProcesses();
            await WaitUntil(() =>
            {
                try { File.Copy(original, fixture.Executable, overwrite: true); return true; }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(5));
            File.Delete(original);
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
        fixture.RemoveCache();
        using var first = fixture.Start(arguments: ["--rendering-probe", "--probe-hold-ms=8000"]);
        var firstErrors = first.StandardError.ReadToEndAsync();
        try
        {
            var line = await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.NotNull(line);
            using var entry = JsonDocument.Parse(line);
            Assert.Equal(first.Id, entry.RootElement.GetProperty("processId").GetInt32());
            Assert.Throws<IOException>(() =>
            {
                using var denied = File.Open(Path.Combine(fixture.Cache, "PortabilityAuditHost.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            });
            var second = await fixture.Probe(timeoutSeconds: 6);
            fixture.AssertPortable(second);
            Assert.False(first.HasExited, "A second managed entry must run while the first entry still owns its verified runtime files.");
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(first.ExitCode == 0, await firstErrors);
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
        NativeHostResources.ReplacePacket(fixture.Executable, packet);
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
        try { await fixture.AssertRejected(expected); }
        finally { NativeHostResources.ReplacePacket(fixture.Executable, fixture.Packet); }
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
    public string Cache => Path.Combine(Installation, "Minecraft", "Launcher", "Runtime", Convert.ToHexString(Packet.AsSpan(40, 32)).ToLowerInvariant());
    private int run;
    private readonly List<(int Id, DateTime StartUtc, string Executable)> ownedProcesses = [];
    private readonly DateTime createdUtc = DateTime.UtcNow;

    public NativePortableHostFixture()
    {
        Root = Path.Combine(Repository, "artifacts", "native-host-tests", Guid.NewGuid().ToString("N"));
        Installation = Path.Combine(Root, "installation Я 日本語");
        Directory.CreateDirectory(Installation);
    }

    public async Task InitializeAsync()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var projectRoot = Path.Combine(Root, "managed-project");
        Directory.CreateDirectory(projectRoot);
        var hostSource = SecurityElement.Escape(Path.Combine(Repository, "tools", "PortabilityAuditHost", "Program.cs"));
        var programProject = SecurityElement.Escape(Path.Combine(Repository, "Program", "Minecraft.csproj"));
        var project = Path.Combine(projectRoot, "PortabilityAuditHost.csproj");
        File.WriteAllText(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier><UseWPF>true</UseWPF>
                <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                <SelfContained>true</SelfContained><UseAppHost>true</UseAppHost>
                <PublishSingleFile>false</PublishSingleFile><IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>
                <ValidateExecutableReferencesMatchSelfContained>false</ValidateExecutableReferencesMatchSelfContained>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{{hostSource}}" Link="Program.cs" />
                <ProjectReference Include="{{programProject}}" AdditionalProperties="SkipPortableIdentityAdapter=true" />
                <RuntimeHostConfigurationOption Include="Switch.System.Windows.Media.MediaContext.DisableDirtyRectangles" Value="true" />
              </ItemGroup>
            </Project>
            """);
        await Command("dotnet", ["restore", project, "--no-dependencies", "-p:Configuration=" + configuration], "restore");
        await Command("dotnet", ["publish", project, "--no-restore", "-c", configuration, "-o", Payload,
            "-p:BuildProjectReferences=false", "-p:SkipPortableIdentityAdapter=true"], "publish");
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
            "-SourceRevisionId", revision, "-EntryAssembly", "PortabilityAuditHost.dll", "-ArtifactsDirectory", stage], stageName + "-pack");
    }

    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    public Task DisposeAsync()
    {
        // Only this exact newly-created GUID can be cleaned. Previous runs,
        // including failed or externally retained evidence, are never visited.
        var boundary = Path.GetFullPath(Path.Combine(Repository, "artifacts", "native-host-tests"));
        Assert.Equal(boundary, Path.GetDirectoryName(Path.GetFullPath(Root)), ignoreCase: true);
        Assert.True(Guid.TryParseExact(Path.GetFileName(Root), "N", out _));
        Assert.False((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0);
        var actions = new List<string>();
        foreach (var owned in ownedProcesses)
        {
            try
            {
                using var process = Process.GetProcessById(owned.Id);
                if (!process.HasExited && process.StartTime.ToUniversalTime() == owned.StartUtc &&
                    string.Equals(NormalizePath(process.MainModule!.FileName), NormalizePath(owned.Executable), StringComparison.OrdinalIgnoreCase))
                {
                    actions.Add("Kept bulky fixture files because an owned process is still running.");
                    File.WriteAllText(Path.Combine(Root, "cleanup.json"), JsonSerializer.Serialize(actions));
                    return Task.CompletedTask;
                }
            }
            catch (ArgumentException) { } // The exact owned process has exited.
            catch (InvalidOperationException) { }
            catch (Win32Exception)
            {
                actions.Add("Kept bulky fixture files because an owned process could not be checked.");
                File.WriteAllText(Path.Combine(Root, "cleanup.json"), JsonSerializer.Serialize(actions));
                return Task.CompletedTask;
            }
        }
        foreach (var name in new[] { "managed-project", "native-build", "native-update-build", "payload", Path.GetFileName(Installation) })
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
                foreach (var relative in new[] { "host-build.json", "native-build.log", Path.Combine("Minecraft", "Launcher", "bootstrap.log") })
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
        var original = Path.Combine(Root, "updater-original.exe");
        AssertInsideRoot(original);
        if (File.Exists(original) && (File.GetAttributes(original) & FileAttributes.ReparsePoint) == 0)
        {
            try { File.Delete(original); actions.Add("Removed newly owned updater baseline copy."); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { actions.Add("Kept updater baseline: " + error.GetType().Name + "."); }
        }
        File.WriteAllText(Path.Combine(Root, "cleanup.json"), JsonSerializer.Serialize(actions));
        Packet = [];
        Records = [];
        return Task.CompletedTask;
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

    public void TrackProcess(Process process, string executable) => ownedProcesses.Add((process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(executable)));

    public void StopOwnedNativeProcesses()
    {
        AssertInsideRoot(Executable);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.StartTime.ToUniversalTime() >= createdUtc.AddSeconds(-1) &&
                        string.Equals(NormalizePath(process.MainModule!.FileName), NormalizePath(Executable), StringComparison.OrdinalIgnoreCase))
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

    public Process Start(string? executable = null, string? cwd = null, string[]? arguments = null, bool allowPowerShell = false)
    {
        var start = new ProcessStartInfo(executable ?? Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? Root };
        foreach (var argument in arguments ?? ["--rendering-probe"]) start.ArgumentList.Add(argument);
        // The private SC runtime must work without an installed dotnet/runtime
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

    public async Task AssertRejected(string? expectedError = null)
    {
        var result = await Run(timeoutSeconds: 15);
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
        File.WriteAllText(Path.Combine(Root, $"run-{++run:D2}.json"), JsonSerializer.Serialize(new { process.Id, process.ExitCode, stdout, stderr }));
        return (process.Id, process.ExitCode, stdout, stderr);
    }

    public void AssertPortable(NativeHostProbe probe, string? executable = null)
    {
        var json = probe.Json;
        Assert.Equal(probe.ProcessId, json.GetProperty("processId").GetInt32());
        SamePath(executable ?? Executable, json.GetProperty("processPath").GetString());
        SamePath(Installation, json.GetProperty("applicationRoot").GetString());
        SamePath(Cache, json.GetProperty("appBaseDirectory").GetString());
        SamePath(Cache, json.GetProperty("runtimeDirectory").GetString());
        Assert.Equal("STA", json.GetProperty("entryApartment").GetString());
        var temp = Path.Combine(Installation, "Minecraft", "Launcher", "Temp");
        foreach (var property in new[] { "temporaryDirectory", "temporaryEnvironment", "temporaryEnvironmentAlias" })
            SamePath(temp, json.GetProperty(property).GetString());
        SamePath(Path.Combine(Installation, "Minecraft", "Launcher", "DotNetBundles"), json.GetProperty("bundleExtractEnvironment").GetString());
        var modules = json.GetProperty("runtimeModules");
        foreach (var name in new[] { "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "wpfgfx_cor3.dll" })
            SamePath(Path.Combine(Cache, name), modules.GetProperty(name).GetString());
        foreach (var module in modules.EnumerateObject())
            SamePath(Path.Combine(Cache, module.Name), module.Value.GetString());
        Assert.True(json.GetProperty("switchEnabled").GetBoolean());
        Assert.True(json.GetProperty("wpfCachedDisableDirtyRectangles").GetBoolean());
        Assert.Equal("SoftwareOnly", json.GetProperty("processRenderMode").GetString());
        Assert.Equal(5, json.GetProperty("preparedDialogWindows").GetInt32());
        Assert.Equal(0, json.GetProperty("windowsAfterDialogCleanup").GetInt32());
    }

    public static void SamePath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(NormalizePath(expected), NormalizePath(actual), ignoreCase: true);
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + full[8..];
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full[4..] : full;
    }

    public void AssertInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        Assert.StartsWith(Path.GetFullPath(Root).TrimEnd('\\') + "\\", full, StringComparison.OrdinalIgnoreCase);
    }

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
            case "duplicate path": records[harmless] = records[harmless] with { Name = Encoding.UTF8.GetBytes("HOSTFXR.DLL") }; break;
            case "file directory conflict": records[harmless] = records[harmless] with { Name = Encoding.UTF8.GetBytes("hostfxr.dll/child.dll") }; break;
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
    {
        var module = LoadLibraryEx(executable, IntPtr.Zero, 0x00000002 | 0x00000020);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var languages = new List<ushort>();
        ResourceLanguage callback = (_, _, _, language, _) => { languages.Add(language); return true; };
        try
        {
            if (!EnumResourceLanguages(module, (IntPtr)10, (IntPtr)101, callback, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FreeLibrary(module); }
        Assert.Single(languages);
        var update = BeginUpdateResource(executable, false);
        if (update == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!UpdateResource(update, (IntPtr)10, (IntPtr)101, languages[0], packet, (uint)packet.Length))
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
