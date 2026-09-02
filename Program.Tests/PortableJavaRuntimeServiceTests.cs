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

    /// <summary>
    /// A runtime whose folder Windows is still holding goes into place anyway,
    /// once it lets go.
    /// </summary>
    /// <remarks>
    /// java.exe is run from the staging folder before the move, to check the
    /// launcher's JVM options against it, and Windows keeps an executable's
    /// image mapped for a breath after the process has gone - longer with a
    /// scanner reading a tree that appeared a second earlier. The move failed
    /// with "Access to the path ...\.java-17.install.8977cec7 is denied" and the
    /// whole install was thrown away and downloaded again. The lock here is a
    /// real open handle rather than a stand-in for one.
    /// </remarks>
    [Fact]
    public void AnInstallHeldOpenForAMoment_StillLandsInPlace()
    {
        var parent = Path.Combine(_root, "runtime");
        var stage = Path.Combine(parent, ".java-17.install.abc");
        var installed = Path.Combine(parent, "java-17");
        Directory.CreateDirectory(Path.Combine(stage, "bin"));
        File.WriteAllText(Path.Combine(stage, "bin", "java.exe"), "not really java");

        var held = File.Open(
            Path.Combine(stage, "bin", "java.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Run(() =>
        {
            Thread.Sleep(250);
            held.Dispose();
        });

        PortableJavaRuntimeService.PublishInstall(stage, installed);

        released.GetAwaiter().GetResult();
        Assert.False(Directory.Exists(stage));
        Assert.True(File.Exists(Path.Combine(installed, "bin", "java.exe")));
    }

    /// <summary>
    /// And a hold that never lets go is copied around rather than waited out.
    /// </summary>
    /// <remarks>
    /// This used to throw, and that is what a player saw four times in one
    /// evening: "Access to the path ...\.java-17.install.9fd5c780 is denied",
    /// then two hundred megabytes downloaded again on the next try. Renaming a
    /// folder needs it to be nobody's, and a scanner reading a tree that appeared
    /// a second ago will not give it up on anybody's schedule. Reading it is
    /// allowed the whole time, so the runtime is copied into place instead. The
    /// staging folder is left where it is; the sweep collects it.
    /// </remarks>
    [Fact]
    public void AnInstallHeldOpenForever_IsCopiedIntoPlaceInstead()
    {
        var parent = Path.Combine(_root, "runtime-stuck");
        var stage = Path.Combine(parent, ".java-17.install.def");
        var installed = Path.Combine(parent, "java-17");
        Directory.CreateDirectory(Path.Combine(stage, "bin"));
        File.WriteAllText(Path.Combine(stage, "bin", "java.exe"), "not really java");
        Directory.CreateDirectory(Path.Combine(stage, "lib"));
        File.WriteAllText(Path.Combine(stage, "lib", "modules"), "modules");

        using var held = File.Open(
            Path.Combine(stage, "bin", "java.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);

        PortableJavaRuntimeService.PublishInstall(stage, installed);

        Assert.Equal("not really java", File.ReadAllText(Path.Combine(installed, "bin", "java.exe")));
        Assert.Equal("modules", File.ReadAllText(Path.Combine(installed, "lib", "modules")));
        Assert.True(Directory.Exists(stage), "the folder that could not be moved is left for the sweep");
    }

    /// <summary>
    /// A destination that already exists is a different thing entirely, and no
    /// amount of waiting or copying makes it right.
    /// </summary>
    [Fact]
    public void AnInstallWhoseDestinationAppears_SaysSoRatherThanCopyingOverIt()
    {
        var parent = Path.Combine(_root, "runtime-taken");
        var stage = Path.Combine(parent, ".java-17.install.ghi");
        var installed = Path.Combine(parent, "java-17");
        Directory.CreateDirectory(Path.Combine(stage, "bin"));
        File.WriteAllText(Path.Combine(stage, "bin", "java.exe"), "not really java");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "occupied.txt"), "someone else");

        Assert.ThrowsAny<Exception>(() => PortableJavaRuntimeService.PublishInstall(stage, installed));
        Assert.True(File.Exists(Path.Combine(installed, "occupied.txt")));
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

    /// <summary>
    /// A runtime that refuses the launcher's JVM options leaves nothing behind
    /// that the next launch could take for a finished Java.
    /// </summary>
    /// <remarks>
    /// The probe runs after the move now, not before it. Running java.exe out of
    /// the staging folder is what made Directory.Move fail with "Access to the
    /// path ...\.java-17.install.3c821788 is denied", and three seconds of
    /// retries did not always outlast a scanner. So the runtime goes into place
    /// first and is asked afterwards, which means a rejected one has already
    /// been moved: the marker, written last, is what keeps it from counting.
    /// TryDescribeInstalled reads the marker first and calls a tree without one
    /// no install at all.
    ///
    /// The stand-in is a real executable rather than a text file, because this
    /// has to fail the probe rather than fail to start. where.exe exits 1 on the
    /// launcher's arguments, which is the shape of a runtime saying no.
    /// </remarks>
    [Fact]
    public async Task ARuntimeThatRefusesTheLaunchersOptions_LeavesNoInstallBehind()
    {
        var archive = BuildArchive(
            javaExe: File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "where.exe")));
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient, archive, verifyFlags: true);
        var runtimeRoot = CreateRuntimeRoot();

        var rejected = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.EnsureAsync(runtimeRoot, null, CancellationToken.None));
        // Pins it to the probe: without this the test would also pass on an
        // install that fell over somewhere earlier and never ran anything.
        Assert.Contains("rejected the launcher's JVM options", rejected.Message, StringComparison.Ordinal);

        var siblings = Path.Combine(runtimeRoot, "runtime", "windows-x64");
        Assert.False(
            File.Exists(Path.Combine(siblings, "java-25", ".portable-java.json")),
            "a runtime that failed its probe must not be left marked as installed");
        Assert.Empty(Directory.EnumerateDirectories(siblings, ".java-25.install.*"));
    }

    /// <summary>
    /// A staging directory a killed run left behind is collected even when it
    /// belongs to a runtime nothing pins any more - and one still being written
    /// is not.
    /// </summary>
    /// <remarks>
    /// Install() clears its own pin's leftovers before it starts, which covers a
    /// runtime that is still asked for. The one nobody will ask for again was
    /// covered by nothing: the superseded-install sweep matches java-&lt;major&gt;
    /// exactly and a staging name is not that, so a machine whose packs all moved
    /// to 1.21.1 kept a few hundred megabytes of half-extracted java-17 for good,
    /// counting against the free space every later install checks for.
    /// </remarks>
    [Fact]
    public async Task StagingFromAKilledRun_IsCollectedEvenForARuntimeNothingPinsNow()
    {
        var archive = BuildArchive();
        var handler = new RecordingHandler(_ => Success(archive));
        using var httpClient = new HttpClient(handler);
        var runtimeRoot = CreateRuntimeRoot();
        var siblings = Path.Combine(runtimeRoot, "runtime", "windows-x64");

        var abandoned = Path.Combine(siblings, ".java-17.install." + new string('a', 32));
        Directory.CreateDirectory(Path.Combine(abandoned, "bin"));
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromDays(2));

        // Freshly touched, so it stands for an extraction happening right now.
        var live = Path.Combine(siblings, ".java-21.install." + new string('b', 32));
        Directory.CreateDirectory(Path.Combine(live, "bin"));

        await CreateService(httpClient, archive).EnsureAsync(runtimeRoot, null, CancellationToken.None);

        Assert.False(
            Directory.Exists(abandoned),
            "a staging tree nothing will ever install again has to be collected");
        Assert.True(
            Directory.Exists(live),
            "a staging tree still being written must not be taken away mid-extraction");
    }

    private PortableJavaRuntimeService CreateService(
        HttpClient httpClient,
        byte[] archive,
        Func<string, long, bool>? freeSpaceProbe = null,
        IReadOnlyList<Uri>? downloadUris = null,
        bool verifyFlags = false)
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
            // A synthetic java.exe cannot be executed, so the probe is off
            // unless a test hands over one that really runs; the real pin keeps
            // it on.
            VerifyFlags: verifyFlags);
        return new PortableJavaRuntimeService(
            new AppPaths(_root),
            new Logger(Path.Combine(_root, "log.txt")),
            httpClient,
            pin,
            freeSpaceProbe ?? ((_, _) => true));
    }

    private static byte[] BuildArchive(
        string releaseVersion = TestJavaVersion,
        string? extraEntry = null,
        byte[]? javaExe = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (javaExe is null) WriteEntry(archive, ArchivePrefix + "bin/java.exe", "java");
            else WriteEntry(archive, ArchivePrefix + "bin/java.exe", javaExe);
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

    private static void WriteEntry(ZipArchive archive, string name, string content) =>
        WriteEntry(archive, name, Encoding.UTF8.GetBytes(content));

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
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
