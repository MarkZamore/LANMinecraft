using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ControlsPresetRemoteSyncTests : IDisposable
{
    private const string PackName = PortablePackSyncService.DefaultPackRelativePath;
    private const string OriginalOptions = "renderDistance:12\nkey_key.forward:key.keyboard.w\nkey_key.jump:key.keyboard.space\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "preset-remote-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public async Task GitHubPreset_AddChangeRemoveAndRestore_UpdatesAvailabilityWithoutChangingPlayerOptions()
    {
        var (paths, pack, instance) = PrepareInstalledPack();
        var options = Path.Combine(instance, ControlsPresetService.OptionsFileName);
        var optionsBytes = File.ReadAllBytes(options);
        var unrelatedLauncherFile = Path.Combine(pack, "launcher", "local-note.txt");
        File.WriteAllText(unrelatedLauncherFile, "keep this local file");
        var mod = new PackFile("mods/existing.jar", Encoding.UTF8.GetBytes("do not download mods for a preset"));
        var presetOne = Preset("key_key.forward:key.keyboard.w\nkey_key.jump:key.keyboard.space\n");
        var presetTwo = Preset("key_key.forward:key.keyboard.up\nkey_key.jump:key.keyboard.space\n");
        using var handler = new MutableReleaseHandler(BuildRelease("one", mod, presetOne));
        using var http = new HttpClient(handler);
        var sync = CreateService(paths, http);
        var controls = new ControlsPresetService();

        Assert.False(controls.Evaluate(pack, instance).HasPreset);
        Assert.True(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        var added = controls.Evaluate(pack, instance);
        Assert.True(added.HasPreset);
        Assert.True(added.IsApplied);
        Assert.Equal(presetOne.Content, File.ReadAllBytes(PresetPath(pack)));
        Assert.False(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));

        handler.Release = BuildRelease("two", mod, presetTwo);
        Assert.True(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        var changed = controls.Evaluate(pack, instance);
        Assert.True(changed.HasPreset);
        Assert.False(changed.IsApplied);
        Assert.Equal("key.forward", changed.FirstDifference);
        Assert.Equal(presetTwo.Content, File.ReadAllBytes(PresetPath(pack)));

        handler.Release = BuildRelease("removed", mod);
        Assert.True(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        Assert.False(File.Exists(PresetPath(pack)));
        Assert.False(controls.Evaluate(pack, instance).HasPreset);
        Assert.False(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        Assert.Equal("keep this local file", File.ReadAllText(unrelatedLauncherFile));

        handler.Release = BuildRelease("restored", mod, presetOne);
        Assert.True(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        Assert.True(controls.Evaluate(pack, instance).IsApplied);
        Assert.Equal(optionsBytes, File.ReadAllBytes(options));
        Assert.DoesNotContain(handler.RequestedAssets, name => name.EndsWith("existing.jar", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(pack, "mods", "existing.jar")));
        Assert.Empty(Directory.EnumerateDirectories(pack, ".pack-sync-stage*"));
    }

    [Fact]
    public async Task UnavailableManifestOrCorruptLauncherAsset_KeepsThePreviousPreset()
    {
        var (paths, pack, instance) = PrepareInstalledPack();
        var oldPreset = Preset("key_key.forward:key.keyboard.w\n");
        File.WriteAllBytes(PresetPath(pack), oldPreset.Content);
        var options = File.ReadAllBytes(Path.Combine(instance, ControlsPresetService.OptionsFileName));
        using var handler = new MutableReleaseHandler([]);
        using var http = new HttpClient(handler);
        var sync = CreateService(paths, http);

        Assert.False(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        Assert.Equal(oldPreset.Content, File.ReadAllBytes(PresetPath(pack)));

        // The verified manifest no longer lists a preset, but its remaining
        // launcher payload is corrupt. A failed refresh must not remove the
        // last usable preset before all replacement data has been verified.
        var defaults = new PackFile("launcher/options-default.txt", Encoding.UTF8.GetBytes("gamma:0.5\n"));
        handler.Release = BuildRelease("preset-removed", defaults);
        var assetName = handler.Release.Keys.Single(name => name != PortablePackSyncService.PackManifestAssetName && name.EndsWith("launcher.zip", StringComparison.Ordinal));
        handler.Release[assetName] = new byte[handler.Release[assetName].Length];

        Assert.False(await sync.RefreshLauncherDataAsync(PackName, CancellationToken.None));
        Assert.Equal(oldPreset.Content, File.ReadAllBytes(PresetPath(pack)));
        Assert.False(File.Exists(Path.Combine(pack, "launcher", "options-default.txt")));
        Assert.Equal(options, File.ReadAllBytes(Path.Combine(instance, ControlsPresetService.OptionsFileName)));
        Assert.Empty(Directory.EnumerateDirectories(pack, ".pack-sync-stage*"));
    }

    [Fact]
    public async Task OversizedLauncherAsset_IsRejectedBeforeDownload_AndKeepsThePreset()
    {
        var (paths, pack, _) = PrepareInstalledPack();
        var oldPreset = Preset("key_key.forward:key.keyboard.w\n");
        File.WriteAllBytes(PresetPath(pack), oldPreset.Content);
        var oversized = new PackFile("launcher/oversized.txt", new byte[8 * 1024 * 1024 + 1]);
        using var handler = new MutableReleaseHandler(BuildRelease("too-large", oversized));
        using var http = new HttpClient(handler);

        Assert.False(await CreateService(paths, http).RefreshLauncherDataAsync(PackName, CancellationToken.None));

        Assert.Equal(oldPreset.Content, File.ReadAllBytes(PresetPath(pack)));
        Assert.Equal([PortablePackSyncService.PackManifestAssetName], handler.RequestedAssets);
        Assert.False(File.Exists(Path.Combine(pack, "launcher", "oversized.txt")));
    }

    private (AppPaths Paths, string Pack, string Instance) PrepareInstalledPack()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var pack = paths.CombineUnderPacks(PackName);
        var instance = paths.CombineUnderInstances(PackName);
        Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(pack, PackManifestService.ManifestFileName), "{\"schemaVersion\":1}");
        File.WriteAllText(Path.Combine(instance, ControlsPresetService.OptionsFileName), OriginalOptions);
        return (paths, pack, instance);
    }

    private PortablePackSyncService CreateService(AppPaths paths, HttpClient http) =>
        new(paths, new Logger(Path.Combine(_root, "launcher.log")), http, (_, _) => true);

    private static string PresetPath(string pack) => ControlsPresetService.GetPresetPath(pack);
    private static PackFile Preset(string text) => new(ControlsPresetService.PresetRelativePath, Encoding.UTF8.GetBytes(text));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Dictionary<string, byte[]> BuildRelease(string revision, params PackFile[] contents)
    {
        var files = contents.Append(new PackFile(PackManifestService.ManifestFileName, Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"))).ToArray();
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assets = new List<object>();
        var launcher = files.Where(file => file.Path.StartsWith("launcher/", StringComparison.Ordinal)).ToArray();
        if (launcher.Length != 0)
        {
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in launcher)
                {
                    using var entry = zip.CreateEntry(file.Path, CompressionLevel.NoCompression).Open();
                    entry.Write(file.Content);
                }
            }
            AddAsset("launcher.zip", "zipRoot", bytes.ToArray(), launcher.Select(file => file.Path).ToArray());
        }
        foreach (var file in files.Except(launcher)) AddAsset(Path.GetFileName(file.Path), "jar", file.Content, [file.Path]);
        payloads[PortablePackSyncService.PackManifestAssetName] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            packId = PackName,
            revision,
            files = files.Select(file => new { path = file.Path, sizeBytes = (long)file.Content.Length, sha256 = Hash(file.Content) }),
            assets
        });
        return payloads;

        void AddAsset(string suffix, string kind, byte[] bytes, string[] covers)
        {
            var name = (kind == "zipRoot" ? "r-" : "m-") + Hash(bytes)[..12] + "-" + suffix;
            payloads.Add(name, bytes);
            assets.Add(new { name, kind, sha256 = Hash(bytes), sizeBytes = (long)bytes.Length, covers });
        }
    }

    private sealed record PackFile(string Path, byte[] Content);

    private sealed class MutableReleaseHandler(Dictionary<string, byte[]> release) : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Release { get; set; } = release;
        public List<string> RequestedAssets { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var asset = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            RequestedAssets.Add(asset);
            return Task.FromResult(Release.TryGetValue(asset, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
