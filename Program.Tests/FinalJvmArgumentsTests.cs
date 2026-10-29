using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class FinalJvmArgumentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "final JVM policy " + Guid.NewGuid().ToString("N"));
    private const string MainClass = "JvmPropertyPrecedenceProbe";
    private const string Logging = "-Dlog4j.configurationFile=";
    public void Dispose() => TempTree.Delete(_root);
    private string Pack(string? content)
    {
        var pack = Path.Combine(_root, "pack"); Directory.CreateDirectory(Path.Combine(pack, "launcher"));
        if (content is not null) File.WriteAllText(Path.Combine(pack, "launcher", "jvm-args.txt"), content);
        return pack;
    }
    private static JsonVersion Version(string? main = MainClass) => new(JsonDocument.Parse(JsonSerializer.Serialize(new { id = "fixture", mainClass = main })), new JsonVersionParserOptions());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualCmlLoggingDefaultAndPackChoiceReachTheJvmWithCorrectPrecedence(bool packOverride)
    {
        Directory.CreateDirectory(_root);
        var java = Environment.GetEnvironmentVariable("JVM_PRECEDENCE_TEST_JAVA") ?? Tool("java"); var javac = Tool("javac");
        await Run(javac, "--release", "17", "-d", _root, RepositoryFile("Program.Tests", "Fixtures", MainClass + ".java"));
        using var parent = new JsonVersion(JsonDocument.Parse("""{"id":"parent","mainClass":"JvmPropertyPrecedenceProbe","logging":{"client":{"argument":"-Dlog4j.configurationFile=${path}","file":{"id":"native default.xml"}}}}"""), new JsonVersionParserOptions());
        using var version = new JsonVersion(JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "child", inheritsFrom = "parent", arguments = new
            {
                jvm = new[] { "-cp", _root, "-Dfixture.native.keep=unchanged" },
                game = new[] { "application value с пробелами", "-Dlog4j.configurationFile=application-only" }
            }
        })), new JsonVersionParserOptions()) { ParentVersion = parent };
        var pack = Pack(packOverride ? Logging + "config/log4j2.xml\n-Dfixture.author=kept\n" : null);
        var options = new MLaunchOption
        {
            JavaPath = java, Path = new MinecraftPath(_root), Session = MSession.CreateOfflineSession("PolicyFixture"),
            MinimumRamMb = 32, MaximumRamMb = 96,
            ExtraJvmArguments = MinecraftProcessService.BuildBaseJvmArguments(_root, Path.Combine(_root, "tmp"),
                Path.Combine(_root, "home"), "", "", null).Select(value => new MArgument(value)).ToList()
        };
        var launcher = new MinecraftLauncher(new MinecraftPath(_root));
        using var process = launcher.BuildProcess(version, options);
        var nativeCommandLine = process.StartInfo.Arguments;
        var nativeBaseline = await CaptureUntouchedNative(process.StartInfo);
        var expectedNative = parent.Logging!.Argument!.Replace("${path}", options.Path.GetLogConfigFilePath(parent.Logging.LogFile!.Id!));
        Assert.Contains(expectedNative[Logging.Length..], nativeCommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain(Logging + "config/log4j2.xml", nativeCommandLine, StringComparison.Ordinal);

        var final = MinecraftProcessService.CompleteJvmArguments(process.StartInfo, version, pack);
        var boundary = final.ToList().IndexOf(MainClass);
        Assert.True(boundary > 0);
        Assert.Equal(packOverride ? Logging + "config/log4j2.xml" : expectedNative,
            Assert.Single(final.Take(boundary), value => value.StartsWith(Logging, StringComparison.Ordinal)));
        Assert.Equal(new[] { "application value с пробелами", "-Dlog4j.configurationFile=application-only" }, final.Skip(boundary + 1));
        Assert.Contains("-Xmx96m", final); Assert.Contains("-Xms32m", final);
        if (!packOverride) { Assert.Equal(nativeCommandLine, process.StartInfo.Arguments); Assert.Empty(process.StartInfo.ArgumentList); }
        process.StartInfo.UseShellExecute = false; process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true; process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await stdout; var error = await stderr;
        var diagnosticRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepositoryFile("Program", "Minecraft.csproj"))!, "..", "artifacts", "ci-419-encoding"));
        Directory.CreateDirectory(diagnosticRoot);
        File.WriteAllText(Path.Combine(diagnosticRoot, $"pack-{packOverride}.json"), JsonSerializer.Serialize(new
        {
            packOverride,
            win32GetACP = GetACP(),
            win32GetOEMCP = GetOEMCP(),
            dotnetCulture = System.Globalization.CultureInfo.CurrentCulture.Name,
            java,
            fixtureSourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RepositoryFile("Program.Tests", "Fixtures", MainClass + ".java")))).ToLowerInvariant(),
            expectedArgument0 = "application value с пробелами",
            expectedArgument0Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("application value с пробелами")),
            originalCmlArguments = nativeCommandLine,
            finalDotnetTokens = final,
            finalArgumentsString = process.StartInfo.Arguments,
            finalArgumentList = process.StartInfo.ArgumentList.ToArray(),
            baseline = new { nativeBaseline.ExitCode, nativeBaseline.Stdout, nativeBaseline.Stderr },
            processed = new { process.ExitCode, Stdout = output, Stderr = error }
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Assert.True(process.ExitCode == 0, output + error);
        Assert.Contains("LOGGING=" + (packOverride ? "config/log4j2.xml" : expectedNative[Logging.Length..]), output, StringComparison.Ordinal);
        Assert.Contains("KEEP=unchanged", output, StringComparison.Ordinal);
        Assert.Contains("ARG0=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("application value с пробелами")), output, StringComparison.Ordinal);
        Assert.Contains("ARG1=" + Convert.ToBase64String(Encoding.UTF8.GetBytes("-Dlog4j.configurationFile=application-only")), output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PropertiesAreGenericAndAgentsClasspathsAndApplicationTokensKeepTheirOrder(bool argumentList)
    {
        var pack = Pack("-Dfixture.choice=first\n-Dfixture.choice=last\n-Dfixture.empty\n-Dfixture.new=a=b\n-Xshare:off\n");
        using var version = Version();
        var native = new[] { "-javaagent:first.jar", "-Dfixture.choice=native", "-Dfixture.choice.extra=keep", "-Xmx123m", "-cp", "-Dfixture.choice=classpath-value", "-p", "module-path", "--add-modules", "ALL-MODULE-PATH", "--add-opens", "java.base/java.lang.invoke=cpw.mods.securejarhandler", "-javaagent:second.jar", "-Dfixture.empty=old", "-m", MainClass, "-Dfixture.choice=application" };
        var start = new ProcessStartInfo();
        if (argumentList) foreach (var arg in native) start.ArgumentList.Add(arg);
        else start.Arguments = string.Join(' ', native);
        var final = MinecraftProcessService.CompleteJvmArguments(start, version, pack);
        Assert.Equal(new[] { "-javaagent:first.jar", "-Dfixture.choice.extra=keep", "-Xmx123m", "-cp", "-Dfixture.choice=classpath-value", "-p", "module-path", "--add-modules", "ALL-MODULE-PATH", "--add-opens", "java.base/java.lang.invoke=cpw.mods.securejarhandler", "-javaagent:second.jar", "-Dfixture.choice=last", "-Dfixture.empty", "-Dfixture.new=a=b", "-Xshare:off", "-m", MainClass, "-Dfixture.choice=application" }, final);
        Assert.Equal(final, start.ArgumentList); Assert.Equal("", start.Arguments);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("ambiguous", true)]
    [InlineData("no-inherited-main", true)]
    [InlineData("missing", false)]
    [InlineData("ambiguous", false)]
    [InlineData("no-inherited-main", false)]
    public void MissingOrAmbiguousNativeBoundaryFailsWithoutChangingTheProcess(string shape, bool packOverride)
    {
        var pack = Pack(packOverride ? "-Dfixture.choice=author\n" : null);
        using var version = Version(shape == "no-inherited-main" ? null : MainClass);
        var start = new ProcessStartInfo { Arguments = shape == "ambiguous" ? MainClass + " " + MainClass : "-Dfixture.choice=native" };
        var before = start.Arguments;
        Assert.Throws<InvalidOperationException>(() => MinecraftProcessService.CompleteJvmArguments(start, version, pack));
        Assert.Equal(before, start.Arguments); Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void EmptyPackListLeavesArgumentListAndNativeDuplicatePropertiesUntouched()
    {
        using var version = Version(); var start = new ProcessStartInfo();
        foreach (var value in new[] { "-Dfixture.choice=one", "-Dfixture.choice=two", MainClass, "" }) start.ArgumentList.Add(value);
        var before = start.ArgumentList.ToArray();
        Assert.Equal(before, MinecraftProcessService.CompleteJvmArguments(start, version, Pack("# no overrides\n")));
        Assert.Equal(before, start.ArgumentList); Assert.Equal("", start.Arguments);
    }
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
    private static async Task<(int ExitCode, string Stdout, string Stderr)> CaptureUntouchedNative(ProcessStartInfo original)
    {
        var start = new ProcessStartInfo(original.FileName)
        {
            Arguments = original.Arguments, WorkingDirectory = original.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var entry in original.Environment) start.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
    private static string Tool(string name)
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        return string.IsNullOrWhiteSpace(home) ? name + ".exe" : Path.Combine(home, "bin", name + ".exe");
    }
    private static async Task Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
    }
    private static string RepositoryFile(params string[] parts)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = parts.Aggregate(current.FullName, Path.Combine); if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
