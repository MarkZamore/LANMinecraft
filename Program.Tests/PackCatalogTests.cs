using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-test-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    public PackCatalogTests()
    {
        _paths = new AppPaths(_root);
        _paths.Ensure();
        _logger = new Logger(_paths.LogFile);
    }
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void MigrationKeepsInstalledAndSelectedButAnEmptyListStaysEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_paths.Packs, "All The Mods 10", "mods"));
        var old = new AppSettings { ClientRelativePath = "TerraFirma Rebirth" };
        PackMembership.Initialize(old, _paths);
        Assert.Equal(new[] { "All The Mods 10", "TerraFirma Rebirth" }, old.AddedPackRelativePaths);
        old.AddedPackRelativePaths = [];
        PackMembership.Initialize(old, _paths);
        Assert.Empty(old.AddedPackRelativePaths);
        var defaults = new AppSettings();
        PackMembership.Initialize(defaults, new AppPaths(Path.Combine(_root, "Fresh")), fresh: true);
        Assert.Equal(new[] { "LL8 Extended" }, defaults.AddedPackRelativePaths);
    }

    [Fact]
    public void AddDoesNotDownloadAndRemovalSurvivesSettingsReload()
    {
        var settings = new AppSettings { AddedPackRelativePaths = [] };
        var java = Path.Combine(_paths.JavaRuntimes, "17", "release");
        Directory.CreateDirectory(Path.GetDirectoryName(java)!);
        File.WriteAllText(java, "keep this Java runtime");
        Assert.True(PackMembership.Add(settings, "LL8 Extended"));
        Assert.False(PackMembership.Add(settings, "ll8 extended"));
        Assert.False(Directory.Exists(Path.Combine(_paths.Packs, "LL8 Extended")));
        Assert.True(PackMembership.Remove(settings, "LL8 Extended"));
        var service = new SettingsService(_paths);
        service.Save(settings);
        Assert.Empty(service.Load().AddedPackRelativePaths!);
        Assert.Equal("keep this Java runtime", File.ReadAllText(java));
    }

    [Fact]
    public async Task NewPackIsDiscoveredOnceAndPlayUsesTheStartupVersionWithoutManifestRequests()
    {
        using var host = new CatalogHost();
        var service = new PackCatalogService(_paths, _logger, host.Client);
        var task = service.RefreshOnceAsync();
        Assert.Same(task, service.RefreshOnceAsync());
        await task;
        Assert.True(service.IsReady);
        Assert.Contains(service.Packs, p => p.Entry.RelativePath == CatalogHost.Folder);
        var before = host.Requests.Count;
        var sync = new PortablePackSyncService(_paths, _logger, host.Client) { Catalog = service };
        Assert.Equal(host.Source, sync.TryResolveSource(CatalogHost.Folder));
        await sync.WeighFromSourceAsync(CatalogHost.Folder, default);
        var result = await sync.SyncAsync(CatalogHost.Folder, null, default);
        Assert.Equal(PackSyncOutcome.Installed, result.Outcome);
        Assert.Equal(before + 1, host.Requests.Count);
        Assert.EndsWith("/payload.jar", host.Requests.Last(), StringComparison.Ordinal);
        Assert.Equal(CatalogHost.Payload, File.ReadAllBytes(Path.Combine(_paths.Packs, CatalogHost.Folder, "mods", "payload.jar")));
        await service.RefreshOnceAsync();
        Assert.Equal(before + 1, host.Requests.Count);
    }

    [Fact]
    public async Task OfflineAndCorruptRemoteBundleKeepTheLastVerifiedCatalogAndManifest()
    {
        using var host = new CatalogHost();
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync();
        host.Offline = true;
        var cached = new PackCatalogService(_paths, _logger, host.Client);
        Assert.Contains(cached.Packs, p => p.Entry.RelativePath == CatalogHost.Folder);
        await cached.RefreshOnceAsync();
        Assert.Contains("revision", cached.SessionManifest(host.Source), StringComparison.Ordinal);
        host.Offline = false;
        host.CorruptBundle = true;
        var again = new PackCatalogService(_paths, _logger, host.Client);
        await again.RefreshOnceAsync();
        Assert.Equal(CatalogHost.Revision, again.Find(CatalogHost.Folder)!.Content.Revision);
    }

    [Fact]
    public async Task CustomSourceIsFrozenAtStartupWithoutBecomingACatalogMembership()
    {
        using var host = new CatalogHost { InvalidIndex = true };
        var path = Path.Combine(_paths.Packs, "My Custom Pack");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, PortablePackSyncService.SourceMarkerFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, owner = host.Source.Owner, repo = host.Source.Repo, tag = host.Source.Tag }));
        var service = new PackCatalogService(_paths, _logger, host.Client);
        await service.RefreshOnceAsync();
        Assert.Null(service.Find("My Custom Pack"));
        Assert.Equal(4, service.Packs.Count);
        var sync = new PortablePackSyncService(_paths, _logger, host.Client) { Catalog = service };
        Assert.Equal(host.Source, sync.TryResolveSource("My Custom Pack"));
        Assert.Contains("revision", service.SessionManifest(host.Source), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledStartupStillFreezesOfflineSnapshotAndDoesNotRetryOnNavigation()
    {
        using var host = new CatalogHost();
        var service = new PackCatalogService(_paths, _logger, host.Client);
        await service.RefreshOnceAsync(new CancellationToken(true));
        var count = host.Requests.Count;
        Assert.True(service.IsReady);
        Assert.Equal(4, service.Packs.Count);
        await service.RefreshOnceAsync();
        Assert.Equal(count, host.Requests.Count);
    }

    [Fact]
    public async Task CorruptDiskCacheCanBeRepairedByAValidStartupDownload()
    {
        using var host = new CatalogHost();
        await new PackCatalogService(_paths, _logger, host.Client).RefreshOnceAsync();
        var path = Directory.GetFiles(Path.Combine(_paths.Launcher, "Library"), "*.json")
            .Single(p => Path.GetFileName(p) != "catalog.json");
        File.WriteAllText(path, "corrupt cache");
        var service = new PackCatalogService(_paths, _logger, host.Client);
        Assert.Null(service.Find(CatalogHost.Folder));
        await service.RefreshOnceAsync();
        Assert.NotNull(service.Find(CatalogHost.Folder));
        Assert.Contains("revision", service.SessionManifest(host.Source), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFrozenPayloadRequestsRestartAndNeverOverwritesAnInstalledFile()
    {
        using var host = new CatalogHost();
        var service = new PackCatalogService(_paths, _logger, host.Client);
        await service.RefreshOnceAsync();
        var pack = Path.Combine(_paths.Packs, CatalogHost.Folder);
        Directory.CreateDirectory(Path.Combine(pack, "mods"));
        File.WriteAllText(Path.Combine(pack, "portable-pack.json"), "{}");
        var original = Path.Combine(pack, "mods", "payload.jar");
        File.WriteAllText(original, "keep me");
        host.MissingPayload = true;
        var sync = new PortablePackSyncService(_paths, _logger, host.Client) { Catalog = service };
        var error = await Assert.ThrowsAsync<PackSessionExpiredException>(() => sync.SyncAsync(CatalogHost.Folder, null, default));
        Assert.Contains("Перезапустите", error.Message, StringComparison.Ordinal);
        Assert.Equal("keep me", File.ReadAllText(original));
    }

    [Fact]
    public async Task MalformedIndexCannotEscapePortableRootAndOfflineSeedIsAlwaysAvailable()
    {
        using var host = new CatalogHost { InvalidIndex = true };
        var service = new PackCatalogService(_paths, _logger, host.Client);
        await service.RefreshOnceAsync();
        Assert.Equal(4, service.Packs.Count);
        Assert.DoesNotContain(service.Sources, p => p.RelativePath.Contains("..", StringComparison.Ordinal));
        Assert.False(PackCatalogService.ValidFolder("../escape"));
    }

    [Theory]
    [InlineData(804, 521, 1)]
    [InlineData(804, 521, 1.5)]
    [InlineData(1904, 1001, 2)]
    public void LibraryFitsRealControlsAndTreeExpansionSurvivesNavigation(double width, double height, double dpi)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var view = (PackLibraryView)window.FindName("LibraryView");
            var catalog = new PackCatalogService(_paths, _logger, new HttpClient(new OfflineHandler()));
            view.SetCatalog(catalog.Packs);
            foreach (UIElement child in ((Grid)window.FindName("RootGrid")).Children)
                if (child != view) child.Visibility = Visibility.Hidden;
            view.Visibility = Visibility.Visible;
            System.Windows.Media.VisualTreeHelper.SetRootDpi(window, new DpiScale(dpi, dpi));
            WindowCanvasTests.LayoutWindow(window, new Size(width, height));
            var actions = (Grid)window.FindName("LaunchActions");
            var back = (Button)view.FindName("ReturnLauncherButton");
            Assert.InRange(Math.Abs(back.ActualWidth - ((Button)window.FindName("PlayButton")).ActualWidth), 0, 1);
            Assert.True(back.ActualHeight >= 44);
            var play = (Button)window.FindName("PlayButton");
            Assert.Equal(play.FontFamily, back.FontFamily);
            Assert.Equal(play.FontSize, back.FontSize);
            Assert.Equal(play.FontWeight, back.FontWeight);
            var list = (ScrollViewer)view.FindName("CatalogScroll");
            Assert.InRange(list.ScrollableWidth, 0, 0.5);
            var panel = VisualChildren<LibraryColumnsPanel>((ItemsControl)view.FindName("CatalogCards")).Single();
            Assert.Equal(new Thickness(), list.Margin);
            var cards = panel.Children.Cast<FrameworkElement>().ToArray();
            Assert.Equal(4, cards.Length);
            Assert.All(cards, card =>
            {
                Assert.Equal(232, card.ActualWidth, 3);
                Assert.Equal(cards[0].ActualHeight, card.ActualHeight, 3);
            });
            if (width == 804)
            {
                var bounds = cards.Select(card => card.TransformToAncestor(panel).TransformBounds(new Rect(card.RenderSize))).ToArray();
                Assert.Equal(bounds[0].Top, bounds[1].Top, 3);
                Assert.Equal(bounds[0].Top, bounds[2].Top, 3);
                Assert.True(bounds[3].Top >= bounds[0].Bottom + 15);
            }
            foreach (var summary in VisualChildren<TextBlock>((ItemsControl)view.FindName("CatalogCards"))
                         .Where(text => catalog.Packs.Any(pack => pack.Summary == text.Text)))
            {
                Assert.True(double.IsPositiveInfinity(summary.MaxHeight));
                Assert.Equal(TextTrimming.None, summary.TextTrimming);
                Assert.True(summary.ActualHeight + summary.Margin.Top + summary.Margin.Bottom >= summary.DesiredSize.Height - 1);
            }
            var sizes = panel.Children.Cast<FrameworkElement>().Select(card => card.RenderSize).ToArray();
            WindowCanvasTests.LayoutWindow(window, new Size(width + 400, height));
            Assert.Equal(sizes, panel.Children.Cast<FrameworkElement>().Select(card => card.RenderSize));
            WindowCanvasTests.LayoutWindow(window, new Size(width, height));
            var pack = catalog.Find("LL8 Extended")!;
            var root = pack.Mods.First(m => m.HasChildren);
            root.IsExpanded = true;
            view.ShowDetails(pack);
            WindowCanvasTests.LayoutWindow(window, new Size(width, height));
            var details = (ScrollViewer)view.FindName("DetailScroll");
            Assert.InRange(details.ScrollableWidth, 0, 0.5);
            Assert.True(details.ScrollableHeight > 0);
            view.ShowCatalog();
            view.ShowDetails(pack);
            Assert.True(root.IsExpanded);
            Assert.Equal(new[] { 3d, 2d }, new[] { actions.ColumnDefinitions[0].Width.Value, actions.ColumnDefinitions[2].Width.Value });
            return true;
        });
    }

    [Fact]
    public void AllSeedModsHaveProjectPagesAndOldCachedMetadataUsesTheSameOfflineLinks()
    {
        var service = new PackCatalogService(_paths, _logger, new HttpClient(new OfflineHandler()));
        foreach (var pack in service.Packs)
        {
            Assert.DoesNotContain("\n", pack.Description, StringComparison.Ordinal);
            foreach (var mod in pack.Content.Mods)
            {
                if (!mod.WebsiteAvailable)
                {
                    Assert.Null(new CatalogModNode(mod, pack.Content.Id).WebsiteUri);
                    Assert.Null(new CatalogModNode(new CatalogMod { Id = mod.Id, Name = mod.Name }, pack.Content.Id).WebsiteUri);
                    continue;
                }
                var page = Assert.IsType<Uri>(new CatalogModNode(mod, pack.Content.Id).WebsiteUri);
                Assert.Equal("https", page.Scheme);
                Assert.Equal(page, new CatalogModNode(new CatalogMod { Id = mod.Id, Name = mod.Name }, pack.Content.Id).WebsiteUri);
            }
        }
        Assert.Null(new CatalogModNode(new CatalogMod { Id = "unknown", WebsiteUrl = "javascript:alert(1)" }).WebsiteUri);
        Assert.Null(new CatalogModNode(new CatalogMod { Id = "unknown", WebsiteUrl = "file:///C:/mod.jar" }).WebsiteUri);
    }

    [Fact]
    public void ModAddonAndLibraryLinksOpenTheirOwnPagesWithoutChangingExpansion()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var view = (PackLibraryView)window.FindName("LibraryView");
            var catalog = new PackCatalogService(_paths, _logger, new HttpClient(new OfflineHandler()));
            var pack = catalog.Find("LL8 Extended")!;
            var parent = pack.Mods.First(node => node.HasChildren);
            parent.IsExpanded = true;
            view.Visibility = Visibility.Visible;
            view.ShowDetails(pack);
            WindowCanvasTests.LayoutWindow(window, new Size(1104, 761));
            var opened = new List<Uri>();
            view.ModPageRequested += opened.Add;
            foreach (var node in new[] { parent, parent.Children[0], pack.Libraries[0] })
            {
                var link = VisualChildren<TextBlock>(view).SelectMany(text => text.Inlines.OfType<Hyperlink>())
                    .Single(link => ReferenceEquals(link.DataContext, node));
                Assert.Equal(node.WebsiteUri, link.NavigateUri);
                link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                Assert.Equal(node.WebsiteUri, opened[^1]);
                Assert.True(parent.IsExpanded);
            }
            var unavailable = pack.Mods.Single(node => node.Name == "FapCraft");
            var disabled = VisualChildren<TextBlock>(view).SelectMany(text => text.Inlines.OfType<Hyperlink>())
                .Single(link => ReferenceEquals(link.DataContext, unavailable));
            Assert.False(disabled.IsEnabled);
            Assert.Equal(Cursors.Arrow, disabled.Cursor);
            Assert.Null(disabled.NavigateUri);
            disabled.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal(3, opened.Count);
            return true;
        });
    }

    [Fact]
    public async Task UnsafeProjectPageCannotReplaceTheVerifiedCatalogue()
    {
        using var host = new CatalogHost("javascript:alert(1)");
        var service = new PackCatalogService(_paths, _logger, host.Client);
        await service.RefreshOnceAsync();
        Assert.Null(service.Find(CatalogHost.Folder));
        Assert.Equal(4, service.Packs.Count);
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T matching) yield return matching;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class CatalogHost : HttpMessageHandler
    {
        internal const string Folder = "New Pack";
        internal const string Revision = "test-revision";
        internal static readonly byte[] Payload = [1, 2, 3, 4];
        internal PackSyncSource Source { get; } = new("MarkZamore", "New-Pack", "pack-latest");
        internal HttpClient Client { get; }
        internal List<string> Requests { get; } = [];
        internal bool Offline, InvalidIndex, CorruptBundle, MissingPayload;
        private readonly byte[] _bundle;
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        internal CatalogHost(string? projectPage = null)
        {
            Client = new HttpClient(this);
            using var original = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var zip = new ZipArchive(original, ZipArchiveMode.Read);
            using var reader = zip.GetEntry("library.json")!.Open();
            var description = JsonSerializer.Deserialize<CatalogDescription>(reader, Options)!;
            if (projectPage is not null) description.Mods = [new CatalogMod { Id = "example", Name = "Example", WebsiteUrl = projectPage }];
            description.Id = "new-pack";
            description.Name = Folder;
            description.RelativePath = Folder;
            description.Revision = Revision;
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var writer = archive.CreateEntry("library.json").Open()) JsonSerializer.Serialize(writer, description, Options);
                using var source = zip.GetEntry("preview.jpg")!.Open();
                using var image = archive.CreateEntry("preview.jpg").Open();
                source.CopyTo(image);
            }
            _bundle = output.ToArray();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            if (request.RequestUri.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal))
                return Json(new { schemaVersion = 1, packs = new[] { new CatalogSource("new-pack", InvalidIndex ? "../escape" : Folder, Source) } });
            if (!url.Contains("/New-Pack/", StringComparison.Ordinal)) return Reply(HttpStatusCode.NotFound, []);
            if (url.EndsWith("pack-manifest.json", StringComparison.Ordinal))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(_bundle));
                var payloadHash = Convert.ToHexStringLower(SHA256.HashData(Payload));
                return Json(new { schemaVersion = 1, revision = CorruptBundle ? "changed" : Revision, minecraftVersion = "26.2",
                    files = new[] { new { path = "mods/payload.jar", sha256 = payloadHash, sizeBytes = Payload.Length } },
                    assets = new[] { new { name = "payload.jar", kind = "jar", sha256 = payloadHash, sizeBytes = Payload.Length, covers = new[] { "mods/payload.jar" } } },
                    library = new { name = "library-" + hash + ".zip", sha256 = hash, sizeBytes = _bundle.Length, revision = CorruptBundle ? "changed" : Revision } });
            }
            if (url.EndsWith("payload.jar", StringComparison.Ordinal)) return Reply(MissingPayload ? HttpStatusCode.NotFound : HttpStatusCode.OK, Payload);
            if (url.Contains("library-", StringComparison.Ordinal)) return Reply(HttpStatusCode.OK, _bundle);
            return Reply(HttpStatusCode.NotFound, []);
        }
        private static Task<HttpResponseMessage> Json(object value) => Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] payload) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) });
    }
}
