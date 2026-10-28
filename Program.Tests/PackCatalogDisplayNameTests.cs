using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class PackCatalogDisplayNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-name-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupCatalogueNameUpdateRefreshesPlayListWhileKeepingTheSameInstallationAndPlayerSettings(bool installed)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var paths = new AppPaths(_root);
            paths.Ensure();
            var logger = new Logger(paths.LogFile);
            using var host = new NameHost();
            var catalog = new PackCatalogService(paths, logger, host.Client);
            catalog.RefreshOnceAsync().GetAwaiter().GetResult();
            var packDirectory = paths.CombineUnderPacks(NameHost.Folder);
            var jar = Path.Combine(packDirectory, "mods", "keep.jar");
            if (installed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
                File.WriteAllText(jar, "unchanged mod contents");
            }
            var world = Path.Combine(paths.Worlds, NameHost.Folder, "World", "level.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(world)!);
            File.WriteAllText(world, "unchanged world contents");
            var settings = new AppSettings
            {
                ClientRelativePath = NameHost.Folder,
                AddedPackRelativePaths = [NameHost.Folder],
                HiddenPackRelativePaths = ["Another hidden pack"],
                SelectedWorldRelativePath = NameHost.Folder + "/World",
                MemoryByPack = new() { [NameHost.Folder] = 7 },
                MaxHeapGb = 7,
                MemorySettingIsTheHeap = true
            };
            var settingsService = new SettingsService(paths, logger);
            settingsService.Save(settings);
            var originalSettings = File.ReadAllBytes(paths.SettingsFile);
            var originalHeap = settings.MaxHeapGb;

            // Use the real dropdown and production refresh method without
            // starting the window's Steam, game or networking services.
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
            Set("_catalog", catalog);
            Set("_builds", builds);
            Set("BuildComboBox", dropdown);
            Refresh();
            var suffix = installed ? "" : "*";
            var previous = Assert.Single(builds);
            Assert.Equal(NameHost.OriginalName + suffix, previous.Name);
            Assert.Same(previous, dropdown.SelectedItem);

            // The next startup first reads yesterday's verified cache, then
            // receives a new display name from the same GitHub source/path.
            host.Name = NameHost.UpdatedName;
            var nextStartup = new PackCatalogService(paths, logger, host.Client);
            Assert.Equal(NameHost.OriginalName, nextStartup.Find(NameHost.Folder)!.Name);
            Set("_catalog", nextStartup);
            nextStartup.RefreshOnceAsync().GetAwaiter().GetResult();
            Assert.Equal(NameHost.UpdatedName, nextStartup.Find(NameHost.Folder)!.Name);
            Refresh();

            var renamed = Assert.Single(builds);
            Assert.Equal(NameHost.UpdatedName + suffix, renamed.Name);
            Assert.Equal(NameHost.Folder, renamed.RelativePath);
            Assert.Equal(packDirectory, renamed.FullPath);
            Assert.Equal(installed, renamed.IsInstalled);
            Assert.Same(renamed, dropdown.SelectedItem);
            Assert.Equal(NameHost.Folder, settings.ClientRelativePath);
            Assert.Equal(NameHost.Folder + "/World", settings.SelectedWorldRelativePath);
            Assert.Equal(new[] { NameHost.Folder }, settings.AddedPackRelativePaths);
            Assert.Equal(new[] { "Another hidden pack" }, settings.HiddenPackRelativePaths);
            Assert.Equal(7, settings.MemoryByPack[NameHost.Folder]);
            Assert.Equal(originalHeap, settings.MaxHeapGb);
            Assert.Equal(originalSettings, File.ReadAllBytes(paths.SettingsFile));
            Assert.Equal("unchanged world contents", File.ReadAllText(world));
            if (installed) Assert.Equal("unchanged mod contents", File.ReadAllText(jar));
            else Assert.False(Directory.Exists(packDirectory));
            Assert.False(Directory.Exists(paths.CombineUnderPacks(NameHost.OriginalName)));
            Assert.False(Directory.Exists(paths.CombineUnderPacks(NameHost.UpdatedName)));
            return true;
        });
    }

    private sealed class NameHost : HttpMessageHandler
    {
        internal const string Folder = "Stable installed folder";
        internal const string OriginalName = "Original published title";
        internal const string UpdatedName = "Updated published title";
        internal string Name { get; set; } = OriginalName;
        internal HttpClient Client { get; }
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        private static readonly PackSyncSource Source = new("MarkZamore", "Metadata-Name-Test", "pack-latest");
        private readonly byte[] _preview;

        internal NameHost()
        {
            Client = new HttpClient(this);
            using var seed = typeof(PackCatalogService).Assembly.GetManifestResourceStream("Minecraft.Library.minecraft-26-2.zip")!;
            using var zip = new ZipArchive(seed, ZipArchiveMode.Read);
            using var image = zip.GetEntry("preview.jpg")!.Open();
            using var output = new MemoryStream();
            image.CopyTo(output);
            _preview = output.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("catalog.json", StringComparison.Ordinal))
                return Json(new { schemaVersion = 1, packs = new[] { new CatalogSource("metadata-name-test", Folder, Source) } });
            if (!uri.AbsolutePath.Contains("/Metadata-Name-Test/", StringComparison.Ordinal))
                return Reply(HttpStatusCode.NotFound, []);
            var bundle = Bundle();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bundle));
            if (uri.AbsolutePath.EndsWith("pack-manifest.json", StringComparison.Ordinal))
            {
                byte[] payload = [1];
                var payloadHash = Convert.ToHexStringLower(SHA256.HashData(payload));
                return Json(new
                {
                    schemaVersion = 1, revision = Name, minecraftVersion = "26.2",
                    files = new[] { new { path = "mods/payload.jar", sha256 = payloadHash, sizeBytes = payload.Length } },
                    assets = new[] { new { name = "payload.jar", kind = "jar", sha256 = payloadHash, sizeBytes = payload.Length, covers = new[] { "mods/payload.jar" } } },
                    library = new { name = "library-" + hash + ".zip", sha256 = hash, sizeBytes = bundle.Length, revision = Name }
                });
            }
            return uri.AbsolutePath.EndsWith("library-" + hash + ".zip", StringComparison.Ordinal)
                ? Reply(HttpStatusCode.OK, bundle) : Reply(HttpStatusCode.NotFound, []);
        }

        private byte[] Bundle()
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var metadataEntry = zip.CreateEntry("library.json");
                metadataEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using (var metadata = metadataEntry.Open())
                    JsonSerializer.Serialize(metadata, new CatalogDescription
                    {
                        SchemaVersion = 1, Id = "metadata-name-test", RelativePath = Folder, Name = Name,
                        MinecraftVersion = "26.2", Revision = Name, Summary = "Summary", Description = "Description"
                    }, Options);
                var imageEntry = zip.CreateEntry("preview.jpg");
                imageEntry.LastWriteTime = metadataEntry.LastWriteTime;
                using var image = imageEntry.Open();
                image.Write(_preview);
            }
            return output.ToArray();
        }

        private static Task<HttpResponseMessage> Json(object value) => Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, byte[] bytes) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) });
    }
}
