using System.ComponentModel;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class MinecraftLaunchFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launch-failure-" + Guid.NewGuid().ToString("N"));
    private const string Heading = "Недостаточно оперативной памяти";
    private const string Detail = "Возможно, предыдущий сеанс игры ещё не завершился или память занята другими приложениями. Попробуйте запустить игру ещё раз.";
    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData("OpenJDK warning: os::commit_memory(0x400000000, 17179869184, 0) failed; error=���� (DOS error/errno=1455)")]
    [InlineData("# Native memory allocation (mmap) failed to map 17179869184 bytes. Error detail: G1 virtual space")]
    [InlineData("# There is insufficient memory for the Java Runtime Environment to continue.")]
    [InlineData("DOS error/errno=1455")]
    [InlineData("Could not reserve enough space for 16777216KB object heap")]
    [InlineData("NATIVE MEMORY ALLOCATION (MALLOC) FAILED")]
    public void NativeStartupRefusalShowsOnlyFriendlyTextAndLogsFullDiagnosticsFirst(string console)
    {
        const string oldTail = "\nOLD log: Stopping! java.lang.OutOfMemoryError";
        var error = MinecraftLaunchFailure.FromProcessExit(1, "\n" + console, oldTail);
        Assert.IsType<MinecraftMemoryAllocationException>(error);
        Assert.Contains(console, error.Message, StringComparison.Ordinal);
        Assert.Contains(oldTail, error.Message, StringComparison.Ordinal);
        Assert.False(MinecraftProcessService.NamesOutOfMemory(console, oldTail));
        var path = Path.Combine(_root, "launcher.log");
        var logger = new Logger(path);
        var calls = new List<string>();
        logger.LineWritten += _ => calls.Add("log");
        MinecraftLaunchFailure.Present(error, logger.Warn, (heading, detail) =>
        {
            Assert.Equal(new[] { "log" }, calls);
            calls.Add("dialog");
            Assert.Equal(Heading, heading);
            Assert.Equal(Detail, detail);
            Assert.DoesNotContain(console, detail, StringComparison.Ordinal);
            Assert.DoesNotContain("OLD", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("Minecraft exited", detail, StringComparison.Ordinal);
        });
        Assert.Equal(new[] { "log", "dialog" }, calls);
        Assert.Contains(error.Message, File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Unrecognized VM option 'InvalidOption'")]
    [InlineData("Could not create the Java Virtual Machine.")]
    [InlineData("java.lang.OutOfMemoryError: Java heap space")]
    [InlineData("Missing required mod fixture.dependency")]
    [InlineData("INFO: run id=1455")]
    [InlineData("")]
    public void UnrelatedFailureIsNotReclassifiedByOldLogOrGenericVmExit(string console)
    {
        const string staleLog = "\nOLD: Native memory allocation (mmap) failed. DOS error/errno=1455";
        var error = MinecraftLaunchFailure.FromProcessExit(1, console, staleLog);
        Assert.IsType<InvalidOperationException>(error);
        var message = MinecraftLaunchFailure.ForUser(error);
        Assert.Equal("Игра не запустилась", message.Heading);
        Assert.Equal(error.Message, message.Detail);
        Assert.False(MinecraftLaunchFailure.NamesNativeMemoryFailure(console));
        Assert.True(MinecraftProcessService.NamesOutOfMemory("java.lang.OutOfMemoryError: Java heap space", ""));
    }

    [Theory]
    [InlineData(1455, true)]
    [InlineData(8, true)]
    [InlineData(14, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(193, false)]
    [InlineData(0, false)]
    public void OnlyNativeMemoryStartCodesUseTheFriendlyDialog(int code, bool memoryFailure)
    {
        Assert.Equal(memoryFailure, MinecraftLaunchFailure.IsNativeMemoryError(code));
        if (!memoryFailure) return;
        var native = new Win32Exception(code, "Fixture CreateProcess failure");
        var error = MinecraftLaunchFailure.FromProcessStart(native);
        Assert.Same(native, error.InnerException);
        Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Equal((Heading, Detail), MinecraftLaunchFailure.ForUser(error));
    }

    [Theory]
    [InlineData(16384, 16384)]
    [InlineData(6144, 6144)]
    [InlineData(4096, 1024)]
    [InlineData(512, 512)]
    public void ActualCmlCompositionRestoresRelease435HeapPolicy(int maximum, int initial)
    {
        Directory.CreateDirectory(_root);
        using var version = new JsonVersion(JsonDocument.Parse("""{"id":"fixture","mainClass":"FixtureMain"}"""), new JsonVersionParserOptions());
        using var process = new MinecraftLauncher(new MinecraftPath(_root)).BuildProcess(version, new MLaunchOption
        {
            JavaPath = "java.exe", Path = new MinecraftPath(_root), Session = MSession.CreateOfflineSession("Fixture"),
            MaximumRamMb = maximum, MinimumRamMb = MinecraftProcessService.InitialHeapMbFor(maximum),
            ExtraJvmArguments = MinecraftProcessService.HeapTuningArgumentsFor(maximum).Select(flag => new MArgument(flag)).ToList()
        });
        var native = process.StartInfo.Arguments;
        var final = MinecraftProcessService.CompleteJvmArguments(process.StartInfo, version, _root);
        Assert.Equal("-Xmx" + maximum + "m", Assert.Single(final, flag => flag.StartsWith("-Xmx", StringComparison.Ordinal)));
        Assert.Equal("-Xms" + initial + "m", Assert.Single(final, flag => flag.StartsWith("-Xms", StringComparison.Ordinal)));
        Assert.DoesNotContain(final, flag => flag.StartsWith("-XX:MaxHeapFreeRatio=", StringComparison.Ordinal));
        Assert.Equal(native, process.StartInfo.Arguments);
        Assert.Empty(process.StartInfo.ArgumentList);
    }
}
