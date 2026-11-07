using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class GameWindowPresentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "game-window-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData("modern")]
    [InlineData("legacy")]
    [InlineData("disabled")]
    [InlineData("icon-failure")]
    public async Task FirstVisibleWindowHasItsNameAndIcons_WithoutChangingHiddenOrFailedCreation(string mode)
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var runtime = Runtime(paths);
        using var profile = Profile();
        var arguments = GameWindowPresentation.Prepare(paths, runtime, profile,
            Path.Combine(paths.Packs, "Fixture мир"), Path.Combine(paths.Instances, "Fixture мир"));
        var overlay = new GameNoticeOverlayService(paths, new Logger(paths.LogFile));
        using var session = overlay.PrepareSession();
        var sources = Directory.GetFiles(RepositoryDirectory("Program.Tests", "Fixtures", "InitialWindow"), "*.java");
        var classes = Path.Combine(_root, "classes");
        Directory.CreateDirectory(classes);
        await Run("javac", new[] { "--release", "8", "-d", classes }.Concat(sources));
        var launch = new List<string> { "-Xverify:all", "-Djava.awt.headless=true" };
        launch.AddRange(session.JvmArguments);
        if (mode != "disabled") launch.AddRange(arguments);
        if (mode == "icon-failure") launch.Add("-Dfixture.window.iconFailure=true");
        launch.AddRange(["-cp", classes, "InitialWindowProbe", classes, mode]);
        var output = await Run("java", launch);
        Assert.Contains("WINDOW_INITIAL_PRESENTATION_PASS", output, StringComparison.Ordinal);
        if (mode == "icon-failure") Assert.Contains("[LauncherWindow] Initial window presentation failed", output, StringComparison.Ordinal);
        else Assert.DoesNotContain("[LauncherWindow] Initial window presentation failed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[LauncherWindow] Window creation hook failed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("IllegalClassFormatException", output, StringComparison.Ordinal);
        if (mode == "disabled") Assert.DoesNotContain("[LauncherWindow] Initial presentation hook installed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedRuntimeAssetsTakePriority_AndSourcesAreUnchanged()
    {
        var paths = new AppPaths(_root);
        paths.Ensure();
        var runtime = Runtime(paths);
        var small = EmbeddedIcon("icon_16x16.png");
        var large = EmbeddedIcon("icon_32x32.png");
#pragma warning disable CA5350 // Official asset protocol uses SHA-1.
        var hash = Convert.ToHexString(SHA1.HashData(small)).ToLowerInvariant();
#pragma warning restore CA5350
        var assets = SharedRuntimeStore.Assets(paths);
        var index = Path.Combine(assets, "indexes", "fixture.json");
        var asset = Path.Combine(assets, "objects", hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllBytes(asset, small);
        var indexBytes = JsonSerializer.SerializeToUtf8Bytes(new { objects = new Dictionary<string, object>
            { ["icons/icon_16x16.png"] = new { hash, size = small.Length } } });
        File.WriteAllBytes(index, indexBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.ClientJarPath)!);
        using (var archive = ZipFile.Open(runtime.ClientJarPath, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("icons/icon_16x16.png").Open()) output.Write(large);
        var originalClient = File.ReadAllBytes(runtime.ClientJarPath);
        using var profile = Profile(withAssets: true);
        var args = GameWindowPresentation.Prepare(paths, runtime, profile, Path.Combine(paths.Packs, "Fixture"), Path.Combine(paths.Instances, "Fixture"));
        var iconPath = Assert.Single(args, arg => arg.StartsWith("-Dlanminecraft.window.icons=", StringComparison.Ordinal)).Split('=', 2)[1];
        using (var archive = ZipFile.OpenRead(iconPath))
        using (var input = archive.GetEntry("icon_16x16.png")!.Open())
        using (var copy = new MemoryStream()) { input.CopyTo(copy); Assert.Equal(small, copy.ToArray()); }
        Assert.Equal(indexBytes, File.ReadAllBytes(index));
        Assert.Equal(small, File.ReadAllBytes(asset));
        Assert.Equal(originalClient, File.ReadAllBytes(runtime.ClientJarPath));
        Assert.Equal(args, GameWindowPresentation.Prepare(paths, runtime, profile,
            Path.Combine(paths.Packs, "Fixture"), Path.Combine(paths.Instances, "Fixture")));
    }

    private PreparedRuntime Runtime(AppPaths paths) => new(paths.Runtimes, "fixture", "unused-java.exe",
        Path.Combine(paths.Runtimes, "client.jar"),
        new PackRuntimeDescriptor(1, "26.3", new(PackLoaderKind.NeoForge, "26.3.0.99"), "", "fixture"));

    private static JsonVersion Profile(bool withAssets = false) => new(JsonDocument.Parse(withAssets
        ? """{"id":"fixture","assetIndex":{"id":"fixture","sha1":"","url":"https://example.invalid/index.json","size":1,"totalSize":1}}"""
        : """{"id":"fixture"}"""), new JsonVersionParserOptions());

    private static byte[] EmbeddedIcon(string name)
    {
        using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("Minecraft.WindowIconAssets.jar")!;
        using var archive = new ZipArchive(resource);
        using var input = archive.GetEntry("icons/" + name)!.Open();
        using var copy = new MemoryStream(); input.CopyTo(copy); return copy.ToArray();
    }

    private async Task<string> Run(string tool, IEnumerable<string> arguments)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(home) ? tool + ".exe" : Path.Combine(home, "bin", tool + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        var temp = Path.Combine(_root, "Temp");
        var profile = Path.Combine(_root, "Home");
        Directory.CreateDirectory(temp); Directory.CreateDirectory(profile);
        PortableJavaEnvironment.ConfigureEnvironment(start.Environment, temp, profile);
        var prefix = tool == "javac" ? "-J" : "";
        start.ArgumentList.Add(prefix + "-Djava.io.tmpdir=" + temp);
        start.ArgumentList.Add(prefix + "-Duser.home=" + profile);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        var text = await output + await error;
        Assert.True(process.ExitCode == 0, text);
        return text;
    }

    private static string RepositoryDirectory(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = parts.Aggregate(directory.FullName, Path.Combine);
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException(string.Join('/', parts));
    }
}
