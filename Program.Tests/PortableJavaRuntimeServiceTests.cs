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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void PinnedTemurinArtifact_MatchesAdoptium25_0_3_9()
    {
        Assert.Equal("temurin-25.0.3+9", PortableJavaRuntimeService.PinnedRuntimeId);
        Assert.Equal("25.0.3", PortableJavaRuntimeService.PinnedJavaVersion);
        Assert.Equal("java-25", PortableJavaRuntimeService.InstallDirectoryName);
        Assert.Equal(
            "OpenJDK25U-jdk_x64_windows_hotspot_25.0.3_9.zip",
            PortableJavaRuntimeService.ArchiveFileName);
        Assert.Equal(141_131_903, PortableJavaRuntimeService.ArchiveSizeBytes);
        Assert.Equal(
            "709312cd0420296d9b9de917fe6e28a5b979e875ee5ab91783fb79bcd5857235",
            PortableJavaRuntimeService.ArchiveSha256);
        Assert.Equal(
            [
                "https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25.0.3%2B9/" +
                "OpenJDK25U-jdk_x64_windows_hotspot_25.0.3_9.zip",
                "https://api.adoptium.net/v3/binary/version/jdk-25.0.3%2B9/windows/x64/jdk/hotspot/normal/eclipse"
            ],
            PortableJavaRuntimeService.DownloadUris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public void JavaCompatibilityArguments_CoverTheJdk25PoliciesAndCompactHeaders()
    {
        // The install-time flag probe runs exactly this list. Runtimes that
        // installed before a flag was added skip the probe via the marker, so
        // every addition must be a guaranteed option on the pinned JDK.
        Assert.Equal(
            [
                "--illegal-native-access=allow",
                "--enable-native-access=ALL-UNNAMED",
                "--sun-misc-unsafe-memory-access=allow",
                "-XX:+UseCompactObjectHeaders"
            ],
            MinecraftProcessService.JavaCompatibilityArguments);
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
