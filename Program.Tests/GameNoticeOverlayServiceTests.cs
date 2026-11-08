using System.Globalization;
using System.Text;

namespace Minecraft.Tests;

public sealed class GameNoticeOverlayServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowedNoticesUseDesktopWhileFullscreenUsesTheSameVerifiedChannel(bool native)
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession(native);
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.Heartbeat(session.Directory, backend: native ? "native-ui-v1" : "lwjgl3");
        fixture.Fullscreen = false;
        Assert.False(service.TryShow(new PeerNotice("Оконный режим", "На дисплее")));
        Assert.Contains("count=0", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")));
        fixture.Fullscreen = true;
        Assert.True(service.TryShow(new PeerNotice("Полный экран", "В кадре")));
        Assert.Contains("count=1", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")));
        fixture.ForegroundId++;
        service.RefreshDisplayScale(session.Directory);
        Assert.Contains("count=0", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")));
        fixture.ForegroundId--;
        Assert.True(service.TryShow(new PeerNotice("Снова полный экран", "В кадре")));
        fixture.Fullscreen = false;
        service.RefreshDisplayScale(session.Directory);
        Assert.Contains("count=0", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")));
        Assert.False(service.TryShow(new PeerNotice("Снова оконный", "На дисплее")));
        service.ProcessExited(Fixture.ProcessId);
    }
    [Theory]
    [InlineData(1.25, "1.25")]
    [InlineData(2, "2")]
    [InlineData(double.NaN, "1")]
    public void ForegroundDpiIsPublishedAndRefreshesForTheNextNotice(double scale, string expected)
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession();
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.Heartbeat(session.Directory);
        fixture.DisplayScale = scale;
        Assert.True(service.TryShow(new PeerNotice("Друг", "В игре")));
        var path = Path.Combine(session.Directory, "notices.properties");
        Assert.Contains("dpiScale=" + expected + "\n", File.ReadAllText(path), StringComparison.Ordinal);
        fixture.DisplayScale = 1.5;
        service.RefreshDisplayScale(session.Directory);
        Assert.Contains("dpiScale=1.5\n", File.ReadAllText(path), StringComparison.Ordinal);
        fixture.ForegroundId++;
        fixture.DisplayScale = 2;
        service.RefreshDisplayScale(session.Directory);
        Assert.Contains("dpiScale=1.5\n", File.ReadAllText(path), StringComparison.Ordinal);
        fixture.ForegroundId--;
        fixture.ActualStartTicks++;
        service.RefreshDisplayScale(session.Directory);
        Assert.Contains("dpiScale=1.5\n", File.ReadAllText(path), StringComparison.Ordinal);
        service.ProcessExited(Fixture.ProcessId);
    }

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

    [Fact]
    public void LongTextCannotInvalidateTheSharedSnapshotOrSplitAnEmoji()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession(native: true);
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.Heartbeat(session.Directory, backend: "native-ui-v1");
        Assert.True(service.TryShow(new PeerNotice(new string('Я', 159) + "😀tail", new string('Б', 319) + "😀tail")));
        Assert.True(service.TryShow(new PeerNotice(new string('A', 160) + "tail", new string('B', 320) + "tail")));
        var values = File.ReadAllLines(Path.Combine(session.Directory, "notices.properties"), Encoding.UTF8)
            .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
        Assert.Equal(new string('Я', 159), values["0.title"]);
        Assert.Equal(new string('Б', 319), values["0.body"]);
        Assert.Equal(new string('A', 160), values["1.title"]);
        Assert.Equal(new string('B', 320), values["1.body"]);
        Assert.Equal("2", values["count"]);
        service.ProcessExited(Fixture.ProcessId);
    }

    [Fact]
    public void NativeSession_RequiresOwnedForegroundChildAndFreshRendererAcrossRestart()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession(native: true);
        Assert.Empty(session.JvmArguments);
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.ForegroundId = Fixture.ProcessId + 10;
        fixture.Heartbeat(session.Directory, processId: fixture.ForegroundId, backend: "native-ui-v1");
        Assert.True(service.TryShow(new PeerNotice("Друг", "В игре")));
        fixture.DisplayScale = 2;
        service.RefreshDisplayScale(session.Directory);
        Assert.Contains("dpiScale=2\n", File.ReadAllText(Path.Combine(session.Directory, "notices.properties")), StringComparison.Ordinal);
        // An unrelated foreground window may not claim the launcher's native session.
        fixture.ForegroundId++;
        fixture.Heartbeat(session.Directory, processId: fixture.ForegroundId, backend: "native-ui-v1");
        Assert.False(service.TryShow(new PeerNotice("Чужой", "В игре")));
        fixture.ForegroundId--;
        foreach (var backend in new[] { "waiting", "unknown", "lwjgl3" })
        {
            fixture.Heartbeat(session.Directory, processId: fixture.ForegroundId, backend: backend);
            Assert.False(service.TryShow(new PeerNotice("Друг", "В игре")));
        }
        fixture.Heartbeat(session.Directory, processId: fixture.ForegroundId, backend: "native-ui-v1", stamp: fixture.Now.AddSeconds(-3));
        Assert.False(service.TryShow(new PeerNotice("Друг", "В игре")));
        var restarted = fixture.CreateService();
        fixture.Heartbeat(session.Directory, processId: fixture.ForegroundId, backend: "native-ui-v1");
        Assert.True(restarted.TryShow(new PeerNotice("После перезапуска", "В игре")));
        fixture.ActualStartTicks++;
        Assert.False(restarted.TryShow(new PeerNotice("PID повторно использован", "В игре")));
        service.ProcessExited(Fixture.ProcessId);
        restarted.ProcessExited(Fixture.ProcessId);
    }

    [Fact]
    public void SdlRendererUsesTheSameDeliveryContractAndRejectsUnknownBackend()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();
        using var session = service.PrepareSession();
        session.BindProcess(Fixture.ProcessId, Fixture.StartedTicks);
        fixture.Heartbeat(session.Directory, backend: "sdl3");
        Assert.True(service.TryShow(new PeerNotice("Друг", "Полный экран")));
        fixture.Heartbeat(session.Directory, backend: "native-ui-v1");
        Assert.False(service.TryShow(new PeerNotice("Друг", "Полный экран")));
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
        internal double DisplayScale { get; set; } = 1;
        internal bool Fullscreen { get; set; } = true;
        internal Fixture() { Paths = new AppPaths(_root); Paths.Ensure(); }
        internal GameNoticeOverlayService CreateService(AppPaths? paths = null) => new(paths ?? Paths,
            new Logger(Path.Combine(Paths.Personal, "notices.log")), () => Now, () => ForegroundId,
            processId => processId == ProcessId ? ActualStartTicks : null,
            () => new MemoryStream(Encoding.UTF8.GetBytes("launcher-owned fixture agent bytes"), writable: false),
            _ => DisplayScale, (root, candidate) => root == ProcessId && candidate == ProcessId + 10,
            _ => Fullscreen);
        internal void Heartbeat(string directory, int processId = ProcessId, DateTimeOffset? stamp = null, string error = "", string backend = "lwjgl3") =>
            GameNoticeOverlayService.WriteProperties(Path.Combine(directory, "heartbeat.properties"), new Dictionary<string, string>
            {
                ["timestampUnixMs"] = (stamp ?? Now).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                ["backend"] = backend, ["renderedCount"] = "0", ["pid"] = processId.ToString(CultureInfo.InvariantCulture), ["error"] = error
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
