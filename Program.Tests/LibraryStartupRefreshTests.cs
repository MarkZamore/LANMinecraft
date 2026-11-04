using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Minecraft.Tests;

public sealed class LibraryStartupRefreshTests
{
    [Fact]
    public void EarlyOpenPublishesTheCompleteOverviewTogetherAfterTheBatchIsReady()
    {
        OnUi(() =>
        {
            using var fixture = new LibraryFixture();
            Assert.Empty(fixture.Cards.Items);
            fixture.WaitForBundleRequests();
            fixture.Host.Release("First-Pack");
            PumpUntil(() => fixture.Catalog.Find("First pack") is not null,
                "The first verified model did not finish behind the publication barrier.");
            Assert.False(fixture.Library.IsOverviewReady);
            Assert.Empty(fixture.VisibleCards());
            Assert.Empty(fixture.Cards.Items);
            Assert.Equal(Visibility.Visible, fixture.Library.Visibility);
            Assert.False(fixture.Catalog.IsOverviewReady);
            Assert.False(fixture.LibraryPreparation.IsCompleted);
            fixture.ReleaseRemaining();
            PumpUntil(() => fixture.Library.IsOverviewReady && fixture.VisibleCards().Length == 3,
                "The complete overview was not revealed in the already open library.");
            Assert.Equal(3, fixture.Cards.Items.Count);
            Assert.True(fixture.Catalog.IsOverviewReady);
            Assert.True(fixture.Catalog.IsReady);
        });
    }

    [Fact]
    public void RepeatedAtomicSnapshotsKeepTheOpenDetailPageAndItsState()
    {
        OnUi(() =>
        {
            using var fixture = new LibraryFixture();
            fixture.WaitForBundleRequests();
            fixture.ReleaseRemaining();
            PumpUntil(() => fixture.Library.IsOverviewReady, "The complete overview was not published.");
            var first = fixture.Cards.Items.Cast<CatalogPack>().Single(pack => pack.Entry.Id == "first");
            fixture.Library.ShowDetails(first);
            PumpUntil(() => fixture.Queue.PendingKeys.Length == 0, "The foreground detail page was not prepared.");
            var host = Assert.IsType<ContentControl>(fixture.Library.FindName("DetailHost"));
            var page = Assert.IsType<PackDetailPage>(host.Content);
            var scroll = Assert.IsType<ScrollViewer>(fixture.Library.FindName("DetailScroll"));
            scroll.ScrollToVerticalOffset(150);
            PumpUntil(() => scroll.VerticalOffset > 0, "The long detail page did not scroll.");
            var offset = scroll.VerticalOffset;
            var parent = first.Mods.Single(node => node.HasChildren);
            parent.IsExpanded = true;

            Task.Run(() => { for (var i = 0; i < 32; i++) fixture.Notify(); }).GetAwaiter().GetResult();
            Assert.Equal(1, fixture.PendingRefresh);
            Drain();
            // A complete replacement made from the same verified content keeps
            // the previous models/pages rather than rebuilding their controls.
            var replacement = fixture.Catalog.Packs.Select(pack => new CatalogPack
            {
                Entry = pack.Entry, Content = pack.Content, Preview = pack.Preview, ContentIdentity = pack.ContentIdentity
            }).ToArray();
            foreach (var pack in replacement) pack.BuildTree();
            fixture.Library.SetCatalog(replacement);
            Assert.Equal(3, fixture.Cards.Items.Count);
            Assert.Same(page, host.Content);
            Assert.Same(scroll, fixture.Library.FindName("DetailScroll"));
            Assert.Same(first, fixture.Cards.Items.Cast<CatalogPack>().Single(pack => pack.Entry.Id == "first"));
            Assert.Equal(offset, scroll.VerticalOffset, 2);
            Assert.True(parent.IsExpanded);
        });
    }

    [Fact]
    public void QueuedPresentationRefreshDoesNotRepopulateDisposedControlsAfterShutdownStarts()
    {
        OnUi(() =>
        {
            using var fixture = new LibraryFixture();
            fixture.WaitForBundleRequests();
            fixture.ReleaseRemaining();
            PumpUntil(() => fixture.Library.IsOverviewReady, "The complete overview was not published.");
            Task.Run(fixture.Notify).GetAwaiter().GetResult();
            Assert.Equal(1, fixture.PendingRefresh);
            fixture.StopAndDisposeLibrary();
            Assert.Null(fixture.Cards.ItemsSource);
            Drain();
            Assert.Equal(0, fixture.PendingRefresh);
            Assert.Null(fixture.Cards.ItemsSource);
            Assert.Empty(fixture.Cards.Items);
        });
    }

    private static void OnUi(Action work) => WindowCanvasTests.OnAStaThread(() =>
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { work(); return true; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private sealed class LibraryFixture : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly string root = Path.Combine(Path.GetTempPath(), "library-startup-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource lifetime = new();
        private readonly MainWindow owner;
        private readonly Window controls;
        private bool libraryDisposed;
        internal CatalogHost Host { get; } = new();
        internal PackCatalogService Catalog { get; }
        internal PackLibraryView Library { get; }
        internal UiPreloadQueue Queue { get; }
        internal ItemsControl Cards => Assert.IsType<ItemsControl>(Library.FindName("CatalogCards"));
        internal Task LibraryPreparation => (Task)Field("_libraryStartup").GetValue(owner)!;
        internal int PendingRefresh => (int)Field("_libraryCatalogRefreshPending").GetValue(owner)!;
        internal Action Notify { get; }

        internal LibraryFixture()
        {
            var paths = new AppPaths(root);
            paths.Ensure();
            Catalog = new PackCatalogService(paths, new Logger(paths.LogFile), Host.Client, stagedStartup: true);
            controls = WindowCanvasTests.LoadWindow();
            controls.WindowStartupLocation = WindowStartupLocation.Manual;
            controls.Left = -32000; controls.Top = -32000;
            controls.ShowInTaskbar = false; controls.ShowActivated = false;
            controls.Show();
            Library = (PackLibraryView)controls.FindName("LibraryView");
            Queue = new UiPreloadQueue(controls.Dispatcher, error => throw error);

            // Exercise production navigation/notification methods against real
            // compiled controls, without networking, Steam or a game process.
            owner = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            foreach (var field in typeof(MainWindow).GetFields(Fields))
                if (typeof(FrameworkElement).IsAssignableFrom(field.FieldType) && controls.FindName(field.Name) is FrameworkElement element)
                    field.SetValue(owner, element);
            Set("_settings", new AppSettings());
            Set("_catalog", Catalog);
            Set("_catalogLifetimeCts", lifetime);
            Set("_preload", Queue);
            Set("_launcherVisibility", new Dictionary<UIElement, Visibility>());
            Notify = (Action)Method("OnLibraryPresentationChanged").CreateDelegate(typeof(Action), owner);
            Catalog.PresentationChanged += Notify;
            // Run the production worker service directly so this fixture covers
            // navigation/publication without entering unrelated startup UI.
            Set("_libraryStartup", Task.Run(() => Catalog.PrepareLibraryAsync(lifetime.Token)));
            Method("ShowLibrary").Invoke(owner, [true]);
        }

        internal void WaitForBundleRequests() => PumpUntil(() => Host.AllRequested,
            "The independent bundle requests did not reach their gates.");
        internal void ReleaseRemaining()
        {
            Host.Release("First-Pack"); Host.Release("Second-Pack"); Host.Release("Last-Pack");
        }
        internal Button[] VisibleCards() => Descendants<Button>(Cards)
            .Where(button => button.IsVisible && button.ActualWidth > 0 && button.ActualHeight > 0).ToArray();
        internal void StopAndDisposeLibrary()
        {
            Set("_shutdownStarted", true);
            Catalog.PresentationChanged -= Notify;
            lifetime.Cancel();
            Queue.Dispose();
            if (!libraryDisposed) { Library.Dispose(); libraryDisposed = true; }
        }
        private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, Fields)!;
        private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, Fields)!;
        private void Set(string name, object value) => Field(name).SetValue(owner, value);
        public void Dispose()
        {
            StopAndDisposeLibrary();
            Host.Release("First-Pack"); Host.Release("Second-Pack"); Host.Release("Last-Pack");
            PumpUntil(() => LibraryPreparation.IsCompleted, "The background library operation did not cancel on close.");
            Assert.False(LibraryPreparation.IsFaulted, LibraryPreparation.Exception?.ToString());
            controls.Close(); lifetime.Dispose(); Host.Client.Dispose(); Host.Dispose();
            TempTree.Delete(root);
        }
    }

    private sealed class CatalogHost : HttpMessageHandler
    {
        private sealed class Gate
        {
            internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, byte[]> manifests = [];
        private readonly Dictionary<string, byte[]> bundles = [];
        private readonly ConcurrentDictionary<string, Gate> gates = new(StringComparer.Ordinal);
        private readonly CatalogSource[] entries;
        internal HttpClient Client { get; }
        internal bool AllRequested => gates.Values.All(gate => gate.Requested.Task.IsCompleted);
        internal bool IsReleased(string repository) => gates[repository].Released.Task.IsCompleted;
        internal void Release(string repository) => gates[repository].Released.TrySetResult();

        internal CatalogHost()
        {
            Client = new HttpClient(this);
            using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var archive = new ZipArchive(seed, ZipArchiveMode.Read);
            using var stream = archive.GetEntry("preview.jpg")!.Open();
            using var image = new MemoryStream(); stream.CopyTo(image);
            entries = [Add("first", "First pack", "First-Pack", image.ToArray()),
                Add("second", "Second pack", "Second-Pack", image.ToArray()),
                Add("last", "Last pack", "Last-Pack", image.ToArray())];
        }

        private CatalogSource Add(string id, string name, string repository, byte[] preview)
        {
            var entry = new CatalogSource(id, name, new PackSyncSource("LibraryStartupFixture", repository, "rolling"));
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var metadata = archive.CreateEntry("library.json").Open())
                    JsonSerializer.Serialize(metadata, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = id, RelativePath = name, Name = name, MinecraftVersion = "1.20.1", Revision = "one",
                        Summary = "An atomic overview reveals every ready card together.",
                        Description = string.Join(" ", Enumerable.Repeat("A long description preserves the detailed page scroll position.", 300)),
                        Mods = [new CatalogMod { Id = "parent", Name = "Parent mod", WebsiteUrl = "https://example.com/parent" },
                            new CatalogMod { Id = "addon", Name = "Addon", ParentId = "parent", WebsiteUrl = "https://example.com/addon" }]
                    }, Json);
                using var picture = archive.CreateEntry("preview.jpg").Open(); picture.Write(preview);
            }
            var bytes = output.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var bundleName = "library-" + hash + ".zip";
            var payload = Encoding.UTF8.GetBytes("fixture");
            var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
            manifests[repository] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, revision = "one", minecraftVersion = "1.20.1",
                files = new[] { new { path = "mods/payload.jar", sizeBytes = payload.Length, sha256 = payloadHash } },
                assets = new[] { new { name = "fixture.jar", kind = "jar", sizeBytes = payload.Length, sha256 = payloadHash, covers = new[] { "mods/payload.jar" } } },
                library = new { name = bundleName, sizeBytes = bytes.Length, sha256 = hash, revision = "one" }
            }, Json);
            bundles[repository + "/" + bundleName] = bytes;
            gates[repository] = new Gate();
            return entry;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal))
                return Reply(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, packs = entries }, Json));
            var repository = manifests.Keys.Single(repo => uri.AbsolutePath.Contains("/" + repo + "/", StringComparison.Ordinal));
            var name = Uri.UnescapeDataString(uri.Segments[^1]);
            if (name == "pack-manifest.json") return Reply(manifests[repository]);
            var gate = gates[repository];
            gate.Requested.TrySetResult();
            await gate.Released.Task.WaitAsync(token);
            return Reply(bundles[repository + "/" + name]);
        }
        private static HttpResponseMessage Reply(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.SystemIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> complete, string message)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var finished = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            finished = complete();
            if (finished || clock.Elapsed >= TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(finished, message);
    }
}
