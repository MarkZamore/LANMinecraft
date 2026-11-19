using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Minecraft.Tests;

public sealed class JavaLaunchArgumentFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "java launch " + Guid.NewGuid().ToString("N"));
    private string ArgumentDirectory => Path.Combine(_root, "Minecraft", "Launcher", "LaunchArguments");
    public void Dispose() => TempTree.Delete(_root);

    [Fact]
    public void Win32LimitMatchesDotNetQuotingAndKeepsOrdinaryAndJava8LaunchesUnchanged()
    {
        var start = new ProcessStartInfo(@"C:\Program Files\Java\bin\javaw.exe");
        string[] complicated = ["", "plain", "a b", "x\t", "x\\", "x \\", "x\\\\\"y", "🌿"];
        foreach (var value in complicated) start.ArgumentList.Add(value);
        var actualQuote = typeof(ProcessStartInfo)
            .GetMethod("BuildArguments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rendered = (string)actualQuote.Invoke(start, null)!;
        Assert.Equal(start.FileName.Length + rendered.Length + 4L, JavaLaunchArgumentFile.WindowsLength(start));
        start.ArgumentList.Clear();
        start.Arguments = new string('a', 32_767 - start.FileName.Length - 4);
        Assert.Equal(32_767, JavaLaunchArgumentFile.WindowsLength(start));
        var paths = new AppPaths(_root);
        Assert.Null(JavaLaunchArgumentFile.Prepare(paths, start, ["fixture"], 17, 0));
        Assert.Null(JavaLaunchArgumentFile.Prepare(paths, start, ["fixture"], 8, 0));
        Assert.False(Directory.Exists(ArgumentDirectory));
        start.Arguments += 'b';
        var error = Assert.Throws<InvalidOperationException>(() =>
            JavaLaunchArgumentFile.Prepare(paths, start, ["fixture"], 8, 0));
        Assert.Contains("Java 8", error.Message, StringComparison.Ordinal);
        Assert.Equal(32_768, JavaLaunchArgumentFile.WindowsLength(start));
        Assert.Empty(start.ArgumentList);
        Assert.False(Directory.Exists(ArgumentDirectory));
    }

    [Fact]
    public async Task NativeJavaLaunchOverTheWin32LimitPreservesTokensAndDeletesItsPrivateFile()
    {
        Directory.CreateDirectory(_root);
        var java = Environment.GetEnvironmentVariable("JVM_PRECEDENCE_TEST_JAVA") ?? Tool("java");
        var compile = NewProcess(Tool("javac"));
        foreach (var value in new[] { "--release", "17", "-d", _root, Fixture() }) compile.ArgumentList.Add(value);
        var compilation = await Run(compile);
        Assert.Equal(0, compilation.ExitCode);
        var start = NewProcess(java);
        var child = new[] { "", "space and \"quote\"", @"C:\with space\", "@literal", "#literal", "application value с пробелами" };
        const string property = "a quote \"x\" #literal, tabs\t and newline\r\n with slash\\";
        var filler = new string('x', 33_000);
        string[] arguments = ["-Dfile.encoding=UTF-8", "-Dfixture.value=" + property,
            "-Dfixture.long=" + filler, "-cp", _root, "JavaLaunchArgumentFileProbe", .. child];
        foreach (var value in arguments) start.ArgumentList.Add(value);
        var refused = Assert.Throws<Win32Exception>(() => Process.Start(start));
        Assert.Equal(206, refused.NativeErrorCode);
        var baseline = NewProcess(java);
        foreach (var value in arguments.Where(argument => !argument.StartsWith("-Dfixture.long=", StringComparison.Ordinal)))
            baseline.ArgumentList.Add(value);
        var expected = await Run(baseline);
        Assert.Equal(0, expected.ExitCode);
        var paths = new AppPaths(_root);
        string argumentFile;
        using (var file = JavaLaunchArgumentFile.Prepare(paths, start, arguments, 17, 5))
        {
            Assert.NotNull(file);
            var argument = start.ArgumentList[0];
            Assert.StartsWith("@", argument);
            argumentFile = argument[1..];
            Assert.Equal(ArgumentDirectory, Path.GetDirectoryName(argumentFile));
            Assert.Equal(child, start.ArgumentList.Skip(1));
            Assert.True(File.Exists(argumentFile));
            JavaLaunchArgumentFile.CleanupStale(paths);
            Assert.True(File.Exists(argumentFile));
            var actual = await Run(start);
            Assert.True(actual.ExitCode == 0, actual.Error);
            Assert.Contains("LENGTH=33000", actual.Output, StringComparison.Ordinal);
            Assert.Contains("VALUE=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(property)), actual.Output, StringComparison.Ordinal);
            Assert.Equal(expected.Output.Replace("LENGTH=0", "LENGTH=33000", StringComparison.Ordinal), actual.Output);
        }
        Assert.False(File.Exists(argumentFile));
        Assert.Empty(Directory.EnumerateFileSystemEntries(ArgumentDirectory));
    }

    [Fact]
    public void ExplicitJavaExpansionControlsStayDirectAndOnlyOwnedStaleFilesAreSwept()
    {
        var start = NewProcess(Tool("java"));
        string[] arguments = ["-Dfixture.long=" + new string('x', 33_000), "@existing.args",
            "--disable-@files", "fixture", "@literal"];
        foreach (var value in arguments) start.ArgumentList.Add(value);
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(ArgumentDirectory);
        var retained = Path.Combine(ArgumentDirectory, "java-launch-user.args");
        var stale = Path.Combine(ArgumentDirectory, "java-launch-" + Guid.NewGuid().ToString("N") + ".args");
        File.WriteAllText(retained, "personal");
        File.WriteAllText(stale, "generated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-2));
        using (var file = JavaLaunchArgumentFile.Prepare(paths, start, arguments, 17, 3))
        {
            Assert.NotNull(file);
            Assert.Equal(arguments.Skip(1), start.ArgumentList.Skip(1));
            var contents = File.ReadAllText(start.ArgumentList[0][1..]);
            Assert.StartsWith("\"-Dfixture.long=", contents);
            Assert.DoesNotContain("existing.args", contents, StringComparison.Ordinal);
            JavaLaunchArgumentFile.CleanupStale(paths);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(start.ArgumentList[0][1..]));
        }
        Assert.Equal(retained, Assert.Single(Directory.EnumerateFileSystemEntries(ArgumentDirectory)));
    }

    private ProcessStartInfo NewProcess(string executable) => new(executable)
    {
        WorkingDirectory = _root,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8
    };

    private static async Task<(int ExitCode, string Output, string Error)> Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private static string Tool(string tool) =>
        Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } home
            ? Path.Combine(home, "bin", tool + ".exe") : tool + ".exe";

    private static string Fixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "Program.Tests", "Fixtures", "JavaLaunchArgumentFileProbe.java");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("JavaLaunchArgumentFileProbe.java");
    }
}
