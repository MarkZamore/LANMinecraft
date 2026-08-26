using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PortableJavaRuntimeServiceTests : IDisposable
{
    private const string TestRuntimeId = "temurin-test-1";
    private const string TestJavaVersion = "25.0.3";
    private const string ArchivePrefix = "jdk-test/";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"minecraft-java-runtime-{Guid.NewGuid():N}");

    public void Dispose()
    {
        TempTree.Delete(_root);
    }

    [Fact]
    public void PinnedTemurinArtifact_MatchesAdoptium21_0_12_1_1()
    {
        Assert.Equal("temurin-21.0.12.1+1", PortableJavaRuntimeService.PinnedRuntimeId);
        Assert.Equal(21, PortableJavaRuntimeService.PinnedMajorVersion);
        Assert.Equal("java-21", PortableJavaRuntimeService.InstallDirectoryName);
        Assert.Equal(
            "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip",
            PortableJavaRuntimeService.ArchiveFileName);
        Assert.Equal(205_073_461, PortableJavaRuntimeService.ArchiveSizeBytes);
        Assert.Equal(
            "f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e",
            PortableJavaRuntimeService.ArchiveSha256);
        Assert.Equal(
            [
                "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/" +
                "OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip",
                "https://api.adoptium.net/v3/binary/version/jdk-21.0.12.1%2B1/windows/x64/jdk/hotspot/normal/eclipse"
            ],
            PortableJavaRuntimeService.DownloadUris.Select(uri => uri.AbsoluteUri));
    }

    /// <summary>
    /// And the version it is recognised by is what the runtime says about
    /// itself, not what its release is called. Every one of these was read out
    /// of the archive's own release file: a build named jdk-21.0.12.1+1 answers
    /// "21.0.12.1", one named jdk8u504-b01 answers "1.8.0_504". Guess it and
    /// the install is never recognised, so it is downloaded, extracted and
    /// thrown away again on every single launch.
    /// </summary>
    [Fact]
    public void EveryRuntimeIsRecognisedByWhatItCallsItself()
    {
        Assert.Equal("21.0.12.1", PortableJavaRuntimeService.PinnedJavaVersion);

        Assert.Equal("1.8.0_504", JavaRuntimeCatalog.ForMajorVersion(8)!.JavaVersion);
        Assert.Equal("17.0.20.1", JavaRuntimeCatalog.ForMajorVersion(17)!.JavaVersion);
        Assert.Equal("21.0.12.1", JavaRuntimeCatalog.ForMajorVersion(21)!.JavaVersion);
        Assert.Equal("25.0.4.1", JavaRuntimeCatalog.ForMajorVersion(25)!.JavaVersion);

        // The catalogue's 21 and the pin the service defaults to are one
        // runtime, so they may not drift apart.
        var pinned = JavaRuntimeCatalog.ForMajorVersion(21)!;
        Assert.Equal(PortableJavaRuntimeService.PinnedRuntimeId, pinned.RuntimeId);
        Assert.Equal(PortableJavaRuntimeService.PinnedJavaVersion, pinned.JavaVersion);
        Assert.Equal(PortableJavaRuntimeService.ArchiveFileName, pinned.ArchiveFileName);
        Assert.Equal(PortableJavaRuntimeService.ArchiveSizeBytes, pinned.ArchiveSizeBytes);
        Assert.Equal(PortableJavaRuntimeService.ArchiveSha256, pinned.ArchiveSha256);
        Assert.Equal(
            PortableJavaRuntimeService.DownloadUris.Select(uri => uri.AbsoluteUri),
            pinned.DownloadUris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public void JavaCompatibilityArguments_AskJava21ForNothingItWouldRefuse()
    {
        // The install-time flag probe runs exactly this list, and a JVM refuses
        // to start on an option it never heard of: every one of the escape
        // hatches below is a Java 24 or 25 option, so on the pinned 21 the game
        // is launched with none of them.
        Assert.Empty(MinecraftProcessService.JavaCompatibilityArguments);
        Assert.Equal(
            MinecraftProcessService.CompatibilityArgumentsFor(PortableJavaRuntimeService.PinnedMajorVersion),
            MinecraftProcessService.JavaCompatibilityArguments);
    }

    [Fact]
    public void CompatibilityArguments_HoldTheModernDoorsOpenWhenThePinMovesForward()
    {
        Assert.Empty(MinecraftProcessService.CompatibilityArgumentsFor(21));
        Assert.Equal(
            [
                "--illegal-native-access=allow",
                "--enable-native-access=ALL-UNNAMED",
                "--sun-misc-unsafe-memory-access=allow"
            ],
            MinecraftProcessService.CompatibilityArgumentsFor(24));
        Assert.Equal(
            [
                "--illegal-native-access=allow",
                "--enable-native-access=ALL-UNNAMED",
                "--sun-misc-unsafe-memory-access=allow",
                "-XX:+UseCompactObjectHeaders"
            ],
            MinecraftProcessService.CompatibilityArgumentsFor(25));
    }

    [Fact]
    public async Task MissingRuntime_DownloadsVerifiesExtractsAndStripsTheArchiveRoot()
    {
        var archive = BuildArchive();
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);
        var runtimeRoot = CreateRuntimeRoot();
        var progress = new List<RuntimePreparationProgress>();

        var prepared = await service.EnsureAsync(
            runtimeRoot,
            new Progress<RuntimePreparationProgress>(progress.Add),
            CancellationToken.None);

        var installRoot = Path.Combine(runtimeRoot, "runtime", "windows-x64", "java-25");
        Assert.Equal(installRoot, prepared.JavaHome);
        Assert.Equal(Path.Combine(installRoot, "bin", "javaw.exe"), prepared.JavaWPath);
        Assert.Equal(TestRuntimeId, prepared.RuntimeId);
        Assert.True(File.Exists(Path.Combine(installRoot, "bin", "java.exe")));
        Assert.True(File.Exists(Path.Combine(installRoot, "lib", "modules")));
        Assert.True(File.Exists(Path.Combine(installRoot, ".portable-java.json")));
        // The archive's leading jdk-test/ component must not survive.
        Assert.False(Directory.Exists(Path.Combine(installRoot, "jdk-test")));
        Assert.Single(handler.RequestUris);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(runtimeRoot, "runtime", "windows-x64"),
            ".java-25.install.*"));
    }

    /// <summary>
    /// The runtimes share one folder, so a sweep has to know the difference
    /// between a Java nothing pins any more and a Java another pack is about to
    /// want. A machine that plays a 1.20.1 pack beside a 1.21.1 one keeps both;
    /// a sweep that recognised only the runtime it was called about would
    /// delete the other on every launch and fetch it again on the next.
    /// </summary>
    [Fact]
    public async Task ARuntimeAnotherPackNeeds_SurvivesTheSweepAndAStrayOneDoesNot()
    {
        var archive = BuildArchive();
        var runtimeRoot = CreateRuntimeRoot();
        var siblings = Path.Combine(runtimeRoot, "runtime", "windows-x64");
        Directory.CreateDirectory(Path.Combine(siblings, "java-21"));
        Directory.CreateDirectory(Path.Combine(siblings, "java-11"));
        File.WriteAllText(Path.Combine(siblings, "java-21", "keep.txt"), "another pack's Java");
        File.WriteAllText(Path.Combine(siblings, "java-11", "stale.txt"), "nothing pins this");

        using var httpClient = new HttpClient(new RecordingHandler(_ => Success(archive)));
        // Twice: the sweep runs on the pass that finds the runtime already there.
        await CreateService(httpClient, archive).EnsureAsync(runtimeRoot, null, CancellationToken.None);
        await CreateService(httpClient, archive).EnsureAsync(runtimeRoot, null, CancellationToken.None);

        Assert.True(
            Directory.Exists(Path.Combine(siblings, "java-21")),
            "a catalogued runtime belongs to whichever pack needs it, not to this one");
        Assert.False(
            Directory.Exists(Path.Combine(siblings, "java-11")),
            "a runtime nothing pins any more is still swept");
    }

    [Fact]
    public async Task InstalledRuntime_SkipsTheNetworkEntirely()
    {
        var archive = BuildArchive();
        var runtimeRoot = CreateRuntimeRoot();
        var first = new RecordingHandler(_ => Success(archive));
        using (var httpClient = new HttpClient(first))
        {
            await CreateService(httpClient, archive)
                .EnsureAsync(runtimeRoot, null, CancellationToken.None);
        }

        var offline = new RecordingHandler(_ =>
            throw new InvalidOperationException("Network must not be used."));
        using var offlineClient = new HttpClient(offline);
        var prepared = await CreateService(offlineClient, archive)
            .EnsureAsync(runtimeRoot, null, CancellationToken.None);

        Assert.Equal(TestRuntimeId, prepared.RuntimeId);
        Assert.Empty(offline.RequestUris);
    }

    [Fact]
    public async Task DeletedExecutable_IsRepairedFromTheCachedArchiveWithoutNetwork()
    {
        var archive = BuildArchive();
        var runtimeRoot = CreateRuntimeRoot();
        var first = new RecordingHandler(_ => Success(archive));
        using (var httpClient = new HttpClient(first))
        {
            await CreateService(httpClient, archive)
                .EnsureAsync(runtimeRoot, null, CancellationToken.None);
        }
        var installRoot = Path.Combine(runtimeRoot, "runtime", "windows-x64", "java-25");
        File.Delete(Path.Combine(installRoot, "bin", "javaw.exe"));

        var offline = new RecordingHandler(_ =>
            throw new InvalidOperationException("Network must not be used."));
        using var offlineClient = new HttpClient(offline);
        await CreateService(offlineClient, archive)
            .EnsureAsync(runtimeRoot, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(installRoot, "bin", "javaw.exe")));
        Assert.Empty(offline.RequestUris);
    }

    [Fact]
    public async Task MarkerFromAnotherRuntime_TriggersReinstall()
    {
        var archive = BuildArchive();
        var runtimeRoot = CreateRuntimeRoot();
        var installRoot = Path.Combine(runtimeRoot, "runtime", "windows-x64", "java-25");
        Directory.CreateDirectory(Path.Combine(installRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(installRoot, "lib"));
        File.WriteAllText(Path.Combine(installRoot, "bin", "java.exe"), "stale");
        File.WriteAllText(Path.Combine(installRoot, "bin", "javaw.exe"), "stale");
        File.WriteAllText(Path.Combine(installRoot, "lib", "modules"), "stale");
        File.WriteAllText(Path.Combine(installRoot, "release"), "JAVA_VERSION=\"25.0.3\"\n");
        File.WriteAllText(
            Path.Combine(installRoot, ".portable-java.json"),
            "{\"schemaVersion\":1,\"runtimeId\":\"temurin-other\",\"archiveSha256\":\"\"," +
            "\"javaVersion\":\"25.0.3\",\"installedAtUtc\":\"2026-01-01T00:00:00+00:00\"}");

        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var prepared = await CreateService(httpClient, archive)
            .EnsureAsync(runtimeRoot, null, CancellationToken.None);

        Assert.Equal(TestRuntimeId, prepared.RuntimeId);
        Assert.Single(handler.RequestUris);
        Assert.Equal("java", File.ReadAllText(Path.Combine(installRoot, "bin", "java.exe")));
    }

    [Fact]
    public async Task TamperedArchive_FailsClosedAndLeavesNoInstall()
    {
        var archive = BuildArchive();
        var corrupted = archive.ToArray();
        corrupted[^1] ^= 0xFF;
        var handler = new RecordingHandler(_ => Success(corrupted));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);
        var runtimeRoot = CreateRuntimeRoot();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.EnsureAsync(runtimeRoot, null, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "runtime", "windows-x64", "java-25")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ArchiveEntryEscapingItsRoot_IsRejected()
    {
        var archive = BuildArchive(extraEntry: ArchivePrefix + "../escaped.txt");
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);
        var runtimeRoot = CreateRuntimeRoot();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.EnsureAsync(runtimeRoot, null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(runtimeRoot, "runtime", "windows-x64", "escaped.txt")));
    }

    [Fact]
    public async Task ArchiveWithoutTheExpectedRootDirectory_IsRejected()
    {
        var archive = BuildArchive(extraEntry: "unexpected/file.txt");
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.EnsureAsync(CreateRuntimeRoot(), null, CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseFileWithTheWrongVersion_FailsClosed()
    {
        var archive = BuildArchive(releaseVersion: "24.0.1");
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.EnsureAsync(CreateRuntimeRoot(), null, CancellationToken.None));
        Assert.Contains("24.0.1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsufficientDiskSpace_ThrowsBeforeDownloading()
    {
        var archive = BuildArchive();
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive, freeSpaceProbe: (_, _) => false);

        await Assert.ThrowsAsync<IOException>(
            () => service.EnsureAsync(CreateRuntimeRoot(), null, CancellationToken.None));

        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task Progress_ReportsInstallingJavaWithByteTotals()
    {
        var archive = BuildArchive();
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);
        var progress = new SynchronousProgress();

        await service.EnsureAsync(CreateRuntimeRoot(), progress, CancellationToken.None);

        Assert.All(progress.Reports, report =>
            Assert.Equal(RuntimePreparationStage.InstallingJava, report.Stage));
        Assert.Contains(progress.Reports, report => report.TotalBytes == archive.Length);
    }

    [Fact]
    public async Task FailingFirstSource_FallsBackToTheSecondAndSucceeds()
    {
        var archive = BuildArchive();
        var handler = new RecordingHandler(request =>
            request.RequestUri!.Host == "primary.test"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(
            httpClient,
            archive,
            downloadUris:
            [
                new Uri("https://primary.test/jdk-test.zip"),
                new Uri("https://mirror.test/jdk-test.zip")
            ]);

        var prepared = await service.EnsureAsync(CreateRuntimeRoot(), null, CancellationToken.None);

        Assert.Equal(TestRuntimeId, prepared.RuntimeId);
        Assert.Equal(
            ["primary.test", "mirror.test"],
            handler.RequestUris.Select(uri => uri.Host));
    }

    [Fact]
    public async Task RedirectToAForeignHost_IsRejectedWithoutInstalling()
    {
        var archive = BuildArchive();
        var handler = new RecordingHandler(request =>
        {
            var response = Success(archive);
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri("https://evil-githubusercontent.com/jdk-test.zip"));
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive);
        var runtimeRoot = CreateRuntimeRoot();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.EnsureAsync(runtimeRoot, null, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "runtime", "windows-x64", "java-25")));
    }

    private sealed class SynchronousProgress : IProgress<RuntimePreparationProgress>
    {
        public List<RuntimePreparationProgress> Reports { get; } = [];

        public void Report(RuntimePreparationProgress value) => Reports.Add(value);
    }

    private string CreateRuntimeRoot()
    {
        var runtimeRoot = Path.Combine(_root, "Minecraft", "Launcher", "Runtimes", "Infinity");
        Directory.CreateDirectory(runtimeRoot);
        return runtimeRoot;
    }

    private PortableJavaRuntimeService CreateService(
        HttpClient httpClient,
        byte[] archive,
        Func<string, long, bool>? freeSpaceProbe = null,
        IReadOnlyList<Uri>? downloadUris = null)
    {
        Directory.CreateDirectory(_root);
        var pin = new JavaRuntimePin(
            MajorVersion: 25,
            TestRuntimeId,
            TestJavaVersion,
            "java-25",
            "jdk-test.zip",
            ArchivePrefix,
            downloadUris ?? [new Uri("https://example.test/jdk-test.zip")],
            archive.LongLength,
            Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
            RequiredFreeSpaceBytes: 1024,
            // A synthetic java.exe cannot be executed, so the probe is off here;
            // the real pin keeps it on.
            VerifyFlags: false);
        return new PortableJavaRuntimeService(
            new AppPaths(_root),
            new Logger(Path.Combine(_root, "log.txt")),
            httpClient,
            pin,
            freeSpaceProbe ?? ((_, _) => true));
    }

    private static byte[] BuildArchive(
        string releaseVersion = TestJavaVersion,
        string? extraEntry = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ArchivePrefix + "bin/java.exe", "java");
            WriteEntry(archive, ArchivePrefix + "bin/javaw.exe", "javaw");
            WriteEntry(archive, ArchivePrefix + "lib/modules", "modules");
            WriteEntry(
                archive,
                ArchivePrefix + "release",
                $"IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"{releaseVersion}\"\n");
            if (extraEntry is not null) WriteEntry(archive, extraEntry, "extra");
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static HttpResponseMessage Success(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }
}
