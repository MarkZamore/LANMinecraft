using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class HeapRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heap-retention-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(2048)]
    [InlineData(16384)]
    public void FinalCmlArgumentsRetainHeapAfterPackOptionsWithoutChangingHeapBoundsOrFiles(int maximum)
    {
        Directory.CreateDirectory(Path.Combine(_root, "launcher"));
        var file = Path.Combine(_root, "launcher", "jvm-args.txt");
        const string authored = "-XX:MaxHeapFreeRatio=70\n-Dfixture.author=kept\n";
        File.WriteAllText(file, authored);
        using var version = new JsonVersion(JsonDocument.Parse("""{"id":"fixture","mainClass":"FixtureMain","arguments":{"game":["-XX:MaxHeapFreeRatio=10"]}}"""), new JsonVersionParserOptions());
        using var process = new MinecraftLauncher(new MinecraftPath(_root)).BuildProcess(version, new MLaunchOption
        {
            JavaPath = "java.exe", Path = new MinecraftPath(_root), Session = MSession.CreateOfflineSession("Fixture"),
            MaximumRamMb = maximum, MinimumRamMb = MinecraftProcessService.InitialHeapMbFor(maximum),
            ExtraJvmArguments = MinecraftProcessService.HeapTuningArgumentsFor(maximum).Select(flag => new MArgument(flag)).ToList()
        });
        var final = MinecraftProcessService.CompleteJvmArguments(process.StartInfo, version, _root, retainCommittedHeap: true);
        var prefix = final.TakeWhile(arg => arg != "FixtureMain").ToArray();
        Assert.Equal(MinecraftProcessService.HeapRetentionArgument,
            Assert.Single(prefix, arg => arg.StartsWith("-XX:MaxHeapFreeRatio=", StringComparison.Ordinal)));
        Assert.Contains("-Xmx" + maximum + "m", prefix);
        Assert.Contains("-Xms1024m", prefix);
        Assert.Contains("-Dfixture.author=kept", prefix);
        Assert.DoesNotContain(prefix, arg => arg.StartsWith("-XX:MinHeapFreeRatio=", StringComparison.Ordinal));
        Assert.Contains("-XX:MaxHeapFreeRatio=10", final.Skip(prefix.Length + 1));
        Assert.Equal(authored, File.ReadAllText(file));
    }

    [Fact]
    public void RetentionPreservesClasspathOperandsAndModularEntrypointAndApplicationSuffix()
    {
        Directory.CreateDirectory(_root);
        using var version = new JsonVersion(JsonDocument.Parse("""{"id":"fixture","mainClass":"FixtureMain"}"""), new JsonVersionParserOptions());
        var start = new ProcessStartInfo("java.exe");
        foreach (var token in new[] { "-Xmx96m", "-XX:MaxHeapFreeRatio=40", "-cp", "-XX:MaxHeapFreeRatio=70", "-m", "FixtureMain", "-XX:MaxHeapFreeRatio=10" })
            start.ArgumentList.Add(token);
        var final = MinecraftProcessService.CompleteJvmArguments(start, version, _root, retainCommittedHeap: true);
        Assert.Equal(new[] { "-Xmx96m", "-cp", "-XX:MaxHeapFreeRatio=70", "-XX:MaxHeapFreeRatio=100", "-m", "FixtureMain", "-XX:MaxHeapFreeRatio=10" }, final);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RealG1GrowsAndCollectsWhileRetentionPreventsCommittedHeapShrink(bool large, bool retain, bool concurrent)
    {
        Directory.CreateDirectory(_root);
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var javac = string.IsNullOrWhiteSpace(javaHome) ? "javac.exe" : Path.Combine(javaHome, "bin", "javac.exe");
        var java = Environment.GetEnvironmentVariable("G1_HEAP_TEST_JAVA") ??
            (string.IsNullOrWhiteSpace(javaHome) ? "java.exe" : Path.Combine(javaHome, "bin", "java.exe"));
        await Run(javac, ["--release", "8", "-d", _root, RepositoryFile("Program.Tests", "Fixtures", "HeapRetentionProbe.java")]);
        var flags = MinecraftProcessService.HeapTuningArgumentsFor(large ? 16384 : 2048)
            .Select(flag => flag == MinecraftProcessService.HeapRetentionArgument && !retain ? "-XX:MaxHeapFreeRatio=70" : flag).ToList();
        // Keep the probe small and GC worker count reproducible. Full and
        // concurrent variants exercise the two native resizing sites.
        flags.AddRange(["-Xms32m", "-Xmx512m", "-XX:ParallelGCThreads=2", "-XX:ConcGCThreads=1"]);
        flags.AddRange(["-cp", _root, "HeapRetentionProbe", concurrent ? "concurrent" : "full"]);
        var output = await Run(java, flags);
        var outputDirectory = Environment.GetEnvironmentVariable("G1_HEAP_TEST_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"large-{large}-retain-{retain}-concurrent-{concurrent}.log"), output);
        }
        var samples = output.Split('\n').Where(line => line.StartsWith("HEAP=", StringComparison.Ordinal))
            .Select(line => line.Trim().Split(':'))
            .Select(parts => (Phase: parts[0], Committed: long.Parse(parts[1], CultureInfo.InvariantCulture),
                Used: long.Parse(parts[2], CultureInfo.InvariantCulture), Gcs: long.Parse(parts[3], CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal(12, samples.Length);
        Assert.True(samples.Max(sample => sample.Committed) > samples[0].Committed, "The probe must actually expand.\n" + output);
        Assert.All(samples, sample => Assert.InRange(sample.Committed, 32L * 1024 * 1024, 512L * 1024 * 1024));
        foreach (var cycle in new[] { 0, 1 })
        {
            var allocated = Assert.Single(samples, sample => sample.Phase == "HEAP=allocated" + cycle);
            var freed = samples.Last(sample => sample.Phase.StartsWith("HEAP=freed" + cycle, StringComparison.Ordinal));
            Assert.True(allocated.Used - freed.Used > 40L * 1024 * 1024, "Objects must still be collected.\n" + output);
            Assert.True(freed.Gcs > allocated.Gcs, "The collector must actually run.\n" + output);
        }
        var decreases = samples.Zip(samples.Skip(1)).Count(pair => pair.Second.Committed < pair.First.Committed);
        if (retain) Assert.True(decreases == 0, "Committed heap must never shrink.\n" + output);
        else Assert.True(decreases > 0, "The negative control must demonstrate native shrinking.\n" + output);
    }

    private async Task<string> Run(string tool, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _root,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var variable in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" }) start.Environment.Remove(variable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = parts.Aggregate(directory.FullName, Path.Combine); if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
