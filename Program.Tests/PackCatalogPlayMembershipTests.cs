using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackCatalogPlayMembershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-play-membership-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GitHubRemovalHidesInstalledAndOfferedEntriesWithoutDeletingWorldsOrCustomPacks(bool installed)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var paths = new AppPaths(_root);
            paths.Ensure();
            var logger = new Logger(paths.LogFile);
            var entry = PackCatalogService.SeedSources().First(source => source.RelativePath == PortablePackSyncService.DefaultPackRelativePath);
            using var host = new IndexHost(entry);
            var catalog = new PackCatalogService(paths, logger, host.Client);
            catalog.RefreshOnceAsync().GetAwaiter().GetResult();
            const string custom = "LL8 Extended";
            var installedJar = Path.Combine(paths.Packs, entry.RelativePath, "mods", "keep.jar");
            if (installed) Write(installedJar, "existing managed mod bytes");
            var customJar = Path.Combine(paths.Packs, custom, "mods", "personal.jar");
            Write(customJar, "custom mod bytes");
            var world = Path.Combine(paths.Worlds, entry.RelativePath, "World", "level.dat");
            var region = Path.Combine(paths.Worlds, entry.RelativePath, "World", "region", "r.0.0.mca");
            Write(world, "existing world bytes");
            Write(region, "existing region bytes");
            var settings = new AppSettings
            {
                ClientRelativePath = custom,
                AddedPackRelativePaths = [entry.RelativePath],
                SelectedWorldRelativePath = custom + "/Personal world",
                MemoryByPack = new() { [entry.RelativePath] = 7, [custom] = 9 },
                MaxHeapGb = 9,
                MemorySettingIsTheHeap = true
            };
            var settingsService = new SettingsService(paths, logger);
            settingsService.Save(settings);
            var originalSettings = File.ReadAllBytes(paths.SettingsFile);

            var controls = WindowCanvasTests.LoadWindow();
            var dropdown = (CenteredDropDown)controls.FindName("BuildComboBox");
            var builds = new ObservableCollection<ClientBuildViewModel>();
            dropdown.ItemsSource = builds;
            var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            void Set(string name, object value) => typeof(MainWindow).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(window, value);
            void Refresh() => typeof(MainWindow).GetMethod("RefreshBuilds",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Set("_paths", paths);
            Set("_settings", settings);
            Set("_settingsService", settingsService);
            Set("_builds", builds);
            Set("BuildComboBox", dropdown);
            Set("_catalog", catalog);
            Refresh();
            Assert.Equal(2, builds.Count);
            Assert.Contains(builds, build => build.RelativePath == entry.RelativePath && build.IsInstalled == installed);
            Assert.Equal(custom, Assert.IsType<ClientBuildViewModel>(dropdown.SelectedItem).RelativePath);

            // A successful empty GitHub index is a real removal, while the
            // installed folder and personal world remain available on disk.
            host.Entry = null;
            var nextStartup = new PackCatalogService(paths, logger, host.Client);
            nextStartup.RefreshOnceAsync().GetAwaiter().GetResult();
            Set("_catalog", nextStartup);
            Refresh();
            AssertRemoved(nextStartup);

            // The saved authoritative empty index must keep the removed
            // directory from reappearing as a custom pack on an offline run.
            host.Offline = true;
            var offlineStartup = new PackCatalogService(paths, logger, host.Client);
            offlineStartup.RefreshOnceAsync().GetAwaiter().GetResult();
            Set("_catalog", offlineStartup);
            Refresh();
            AssertRemoved(offlineStartup);
            return true;

            void AssertRemoved(PackCatalogService current)
            {
                Assert.True(current.IsKnownCatalogPath(entry.RelativePath));
                Assert.True(current.IsWithdrawn(entry.RelativePath));
                Assert.False(current.IsKnownCatalogPath(custom));
                Assert.False(current.IsWithdrawn(custom));
                Assert.Empty(current.Sources);
                Assert.Empty(current.PacksForDisplay(settings));
                var remaining = Assert.Single(builds);
                Assert.Equal(custom, remaining.RelativePath);
                Assert.Same(remaining, dropdown.SelectedItem);
                Assert.Equal(custom, settings.ClientRelativePath);
                Assert.Equal(custom + "/Personal world", settings.SelectedWorldRelativePath);
                Assert.Equal(new[] { entry.RelativePath }, settings.AddedPackRelativePaths);
                Assert.Equal(7, settings.MemoryByPack[entry.RelativePath]);
                Assert.Equal(9, settings.MemoryByPack[custom]);
                Assert.Equal(originalSettings, File.ReadAllBytes(paths.SettingsFile));
                Assert.Equal("existing world bytes", File.ReadAllText(world));
                Assert.Equal("existing region bytes", File.ReadAllText(region));
                Assert.Equal("custom mod bytes", File.ReadAllText(customJar));
                if (installed) Assert.Equal("existing managed mod bytes", File.ReadAllText(installedJar));
                else Assert.False(Directory.Exists(paths.CombineUnderPacks(entry.RelativePath)));
            }
        });
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed class IndexHost : HttpMessageHandler
    {
        internal CatalogSource? Entry { get; set; }
        internal bool Offline { get; set; }
        internal HttpClient Client { get; }

        internal IndexHost(CatalogSource entry)
        {
            Entry = entry;
            Client = new HttpClient(this);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) return Reply(HttpStatusCode.ServiceUnavailable, []);
            if (request.RequestUri!.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal))
            {
                var index = new { schemaVersion = 1, packs = Entry is null ? Array.Empty<CatalogSource>() : new[] { Entry! } };
                return Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            return Reply(HttpStatusCode.ServiceUnavailable, []);
        }

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] bytes) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
    }
}
