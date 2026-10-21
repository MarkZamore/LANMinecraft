using System.Globalization;
using System.Text;

namespace Minecraft.Tests;

public sealed class GameNoticeOverlayServiceTests
{
    [Fact]
    public void ReadyForegroundGame_ReceivesThreeNoticesWithoutDesktopPresentation()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession();
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.Heartbeat(session.Directory);
        for (var index = 0; index < 4; index++) Assert.True(service.TryShow(new PeerNotice("Друг " + index, "Зашёл в сборку")));
        var snapshot = File.ReadAllText(Path.Combine(session.Directory, "notices.properties"), Encoding.UTF8);
        Assert.Contains("count=3\n", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Друг\\ 0", snapshot, StringComparison.Ordinal);
        Assert.Contains("2.title=Друг\\ 3", snapshot, StringComparison.Ordinal);
        Assert.Contains("Зашёл", snapshot, StringComparison.Ordinal);
        fixture.Now += TimeSpan.FromSeconds(8);
        fixture.Heartbeat(session.Directory);
        Assert.True(service.TryShow(new PeerNotice("Новый", "Зашёл в LANMinecraft")));
        snapshot = File.ReadAllText(Path.Combine(session.Directory, "notices.properties"), Encoding.UTF8);
        Assert.Contains("count=1\n", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Друг", snapshot, StringComparison.Ordinal);
        service.ProcessExited(Fixture.ProcessId);
    }

    [Theory]
    [InlineData("background")]
    [InlineData("stale")]
    [InlineData("wrongPid")]
    [InlineData("pidReused")]
    [InlineData("futureTimestamp")]
    [InlineData("error")]
    [InlineData("missing")]
    public void UnavailableRenderer_DoesNotSuppressDesktopNotice(string failure)
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession();
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        if (failure != "missing") fixture.Heartbeat(session.Directory,
            processId: failure == "wrongPid" ? Fixture.ProcessId + 1 : Fixture.ProcessId,
            stamp: failure == "stale" ? fixture.Now.AddSeconds(-3) : failure == "futureTimestamp" ? fixture.Now.AddSeconds(3) : fixture.Now,
            error: failure == "error" ? "OpenGL renderer unavailable" : "");
        if (failure == "background") fixture.ForegroundId = Fixture.ProcessId + 1;
        if (failure == "pidReused") fixture.ActualStartTicks++;
        Assert.False(service.TryShow(new PeerNotice("Друг", "Зашёл в сборку")));
        Assert.Contains("count=0\n", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")), StringComparison.Ordinal);
        service.ProcessExited(Fixture.ProcessId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LauncherRestart_AdoptsOnlyTheSameGameProcess(bool reusedProcessId)
    {
        using var fixture = new Fixture();
        var old = fixture.CreateService();
        using var session = old.PrepareSession();
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.Heartbeat(session.Directory);
        Assert.True(old.TryShow(new PeerNotice("Старое", "сообщение")));
        if (reusedProcessId) fixture.ActualStartTicks++;
        var restarted = fixture.CreateService();
        Assert.Equal(!reusedProcessId, restarted.TryShow(new PeerNotice("Новое", "сообщение")));
        if (!reusedProcessId)
            Assert.DoesNotContain("Старое", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")), StringComparison.Ordinal);
        old.ProcessExited(Fixture.ProcessId);
        restarted.ProcessExited(Fixture.ProcessId);
    }

    [Fact]
    public void AgentLaunchArguments_AreIndependentOfMinecraftAndLiveOutsideMods()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession();
        var argument = Assert.Single(session.JvmArguments, value => value.StartsWith("-javaagent:", StringComparison.Ordinal));
        var agentPath = argument["-javaagent:".Length..];
        Assert.True(File.Exists(agentPath));
        Assert.StartsWith(Path.Combine(fixture.Paths.Launcher, "NoticeOverlay", "Agent"), agentPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identity", argument, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mods", argument, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("-Dlanminecraft.notice.directory=" + session.Directory, session.JvmArguments[1]);
    }

    [Fact]
    public void ExtractionAndIpc_StayUnderPortableRoot_AndRejectAnAncestorJunction()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using (var session = service.PrepareSession())
        {
            session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
            fixture.Heartbeat(session.Directory);
            Assert.True(service.TryShow(new PeerNotice("Друг", "Зашёл в сборку")));
            foreach (var file in Directory.EnumerateFiles(fixture.Paths.Root, "*", SearchOption.AllDirectories))
                Assert.StartsWith(fixture.Paths.Root + Path.DirectorySeparatorChar, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            service.ProcessExited(Fixture.ProcessId);
        }
        var junctionRoot = Path.Combine(fixture.Paths.Launcher, "RelocatedApplication");
        var outsideRoot = Path.Combine(fixture.Paths.Root, "UnrelatedFiles");
        Directory.CreateDirectory(outsideRoot);
        var marker = Path.Combine(outsideRoot, "keep.txt");
        File.WriteAllText(marker, "unchanged");
        SavesFolderService.CreateJunction(junctionRoot, outsideRoot);
        try
        {
            var redirected = new AppPaths(junctionRoot);
            var redirectedService = fixture.CreateService(redirected);
            Assert.Throws<IOException>(() => redirectedService.PrepareSession());
            Assert.Equal("unchanged", File.ReadAllText(marker));
            Assert.Single(Directory.EnumerateFileSystemEntries(outsideRoot));
        }
        finally { Directory.Delete(junctionRoot); }
    }

    private sealed class Fixture : IDisposable
    {
        internal const int ProcessId = 718;
        internal const long StartedTicks = 638000000000000000;
        private readonly string _root = Path.Combine(RepositoryRoot(), "Program.Tests", "TestResults", "NoticeOverlayService", Guid.NewGuid().ToString("N"));
        internal AppPaths Paths { get; }
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(1800000000000);
        internal int ForegroundId { get; set; } = ProcessId;
        internal long ActualStartTicks { get; set; } = StartedTicks;
        internal Fixture() { Paths = new AppPaths(_root); Paths.Ensure(); }
        internal GameNoticeOverlayService CreateService(AppPaths? paths = null) => new(paths ?? Paths,
            new Logger(Path.Combine(Paths.Personal, "notices.log")), () => Now, () => ForegroundId,
            processId => processId == ProcessId ? ActualStartTicks : null,
            () => new MemoryStream(Encoding.UTF8.GetBytes("launcher-owned fixture agent bytes"), writable: false));
        internal void Heartbeat(string directory, int processId = ProcessId, DateTimeOffset? stamp = null, string error = "") =>
            GameNoticeOverlayService.WriteProperties(Path.Combine(directory, "heartbeat.properties"), new Dictionary<string, string>
            {
                ["timestampUnixMs"] = (stamp ?? Now).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["backend"] = "lwjgl3", ["renderedCount"] = "0", ["pid"] = processId.ToString(CultureInfo.InvariantCulture), ["error"] = error
            });
        public void Dispose() => TempTree.Delete(_root);

        private static string RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Program", "Minecraft.csproj"))) return directory.FullName;
            throw new DirectoryNotFoundException("Launcher repository root was not found.");
        }
    }
}
