using System.Text;
using System.Text.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;

namespace Minecraft.Tests;

public sealed class LaunchMemorySessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "launch-memory-session-" + Guid.NewGuid().ToString("N"));
    private const ulong GiB = 1024UL * 1024 * 1024;
    public void Dispose() => TempTree.Delete(_root);

    [Theory]
    [InlineData(16384, 1024)]
    [InlineData(6144, 1024)]
    [InlineData(4096, 1024)]
    [InlineData(512, 512)]
    [InlineData(96, 96)]
    public void ActualCmlCompositionPreservesMaximumAndBoundsInitialHeap(int maximum, int initial)
    {
        Directory.CreateDirectory(_root);
        using var version = new JsonVersion(JsonDocument.Parse("""{"id":"fixture","mainClass":"FixtureMain"}"""), new JsonVersionParserOptions());
        var launcher = new MinecraftLauncher(new MinecraftPath(_root));
        using var process = launcher.BuildProcess(version, new MLaunchOption
        {
            JavaPath = "java.exe", Path = new MinecraftPath(_root), Session = MSession.CreateOfflineSession("Fixture"),
            MaximumRamMb = maximum, MinimumRamMb = MinecraftProcessService.InitialHeapMbFor(maximum)
        });
        var arguments = MinecraftProcessService.CompleteJvmArguments(process.StartInfo, version, _root);
        Assert.Equal("-Xmx" + maximum + "m", Assert.Single(arguments, arg => arg.StartsWith("-Xmx", StringComparison.Ordinal)));
        Assert.Equal("-Xms" + initial + "m", Assert.Single(arguments, arg => arg.StartsWith("-Xms", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(9, 16, true)] // Original failure: plenty of total RAM, insufficient commit.
    [InlineData(1, 21, true)] // Low physical availability alone does not reject.
    [InlineData(31, 1, false)] // Physical RAM cannot substitute for commit.
    [InlineData(9, 2, false)] // Initial heap plus startup overhead does not fit.
    [InlineData(9, 3, true)] // Startup budget fits despite limited future room.
    public void AdmissionUsesCurrentSystemCommitAndNativeHeadroom(int physicalFree, int commitFree, bool accepted)
    {
        var snapshot = new SystemMemorySnapshot(32 * GiB, (ulong)physicalFree * GiB, 64 * GiB, (64UL - (ulong)commitFree) * GiB);
        Assert.Equal((ulong)commitFree * GiB, snapshot.AvailableCommitBytes);
        var error = LaunchMemoryCheck.Rejection(snapshot, 16384);
        Assert.Equal(accepted, error is null);
        if (!accepted)
        {
            Assert.Contains("commit", error, StringComparison.Ordinal);
            Assert.Contains("сохранён", error, StringComparison.Ordinal);
            Assert.DoesNotContain("увеличьте", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExactCommitBoundaryAndExhaustedSnapshotDoNotRoundOrUnderflow()
    {
        var required = LaunchMemoryCheck.StartupCommitBytes(16384);
        Assert.Equal(2 * GiB + GiB / 2, required);
        Assert.Equal(20 * GiB + GiB / 2, LaunchMemoryCheck.RequiredCommitBytes(16384, 4));
        Assert.Null(LaunchMemoryCheck.Rejection(new(32 * GiB, GiB, required, 0), 16384));
        Assert.NotNull(LaunchMemoryCheck.Rejection(new(32 * GiB, GiB, required - 1, 0), 16384));
        Assert.Equal(0UL, new SystemMemorySnapshot(32 * GiB, GiB, GiB, 2 * GiB).AvailableCommitBytes);
    }

    [Fact]
    public void LowerInitialHeapAllowsOriginalBudgetButWarnsAboutUnreservedFutureGrowth()
    {
        var original = new SystemMemorySnapshot(32 * GiB, 9 * GiB, 64 * GiB, 48 * GiB);
        Assert.Null(LaunchMemoryCheck.Rejection(original, 16384));
        Assert.NotNull(LaunchMemoryCheck.GrowthWarning(original, 16384, 6));
        var roomy = original with { CommitTotalBytes = 30 * GiB };
        Assert.Null(LaunchMemoryCheck.GrowthWarning(roomy, 16384, 6));
    }

    [Fact]
    public void RealWindowsSnapshotUsesBytesAndSeparatesPhysicalFromCommit()
    {
        var snapshot = Assert.IsType<SystemMemorySnapshot>(LaunchMemoryCheck.Read());
        Assert.True(snapshot.PhysicalTotalBytes > GiB);
        Assert.InRange(snapshot.PhysicalAvailableBytes, 0UL, snapshot.PhysicalTotalBytes);
        Assert.True(snapshot.CommitLimitBytes > GiB);
        Assert.Equal(snapshot.CommitLimitBytes > snapshot.CommitTotalBytes
            ? snapshot.CommitLimitBytes - snapshot.CommitTotalBytes : 0, snapshot.AvailableCommitBytes);
    }

    [Theory]
    [InlineData("os::commit_memory failed; DOS error/errno=1455")]
    [InlineData("Native memory allocation (mmap) failed to map bytes. OutOfMemoryError")]
    [InlineData("There is insufficient memory for the Java Runtime Environment to continue.")]
    [InlineData("CreateProcess error=1455")]
    public void NativeFailureIsNeverClassifiedAsSpentJavaHeap(string text)
    {
        foreach (var (console, tail) in new[] { (text, ""), ("", text) })
        {
            Assert.True(LaunchMemoryCheck.NamesNativeAllocationFailure(console, tail));
            Assert.False(MinecraftProcessService.NamesOutOfMemory(console, tail));
            Assert.Contains("Это не переполнение Java heap", LaunchMemoryCheck.FailureDetails(console, tail), StringComparison.Ordinal);
        }
        Assert.True(MinecraftProcessService.NamesOutOfMemory("java.lang.OutOfMemoryError: Java heap space", ""));
        Assert.False(LaunchMemoryCheck.NamesNativeAllocationFailure("java.lang.OutOfMemoryError: Java heap space", ""));
    }

    [Theory]
    [InlineData("untouched")]
    [InlineData("touch")]
    [InlineData("append")]
    [InlineData("truncate")]
    [InlineData("rotate")]
    [InlineData("rewrite")]
    [InlineData("missing")]
    [InlineData("partial")]
    public void SessionTailHandlesPreviousFilesAppendTruncationAndRotation(string mode)
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "latest.log");
        const string old = "OLD java.lang.OutOfMemoryError: Java heap space\nOLD Stopping!\n";
        if (mode != "missing") File.WriteAllText(path, mode == "partial" ? old.TrimEnd('\n') : old, new UTF8Encoding(false));
        var stamp = File.GetLastWriteTimeUtc(path);
        var session = new SessionGameLog(_root);
        switch (mode)
        {
            case "touch": File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); break;
            case "append": File.AppendAllText(path, "CURRENT\n"); break;
            case "partial": File.AppendAllText(path, " old continuation\nCURRENT\n"); break;
            case "rotate": File.Move(path, Path.Combine(logs, "previous.log")); File.WriteAllText(path, "CURRENT\n"); break;
            case "truncate": File.WriteAllText(path, "CURRENT\n"); break;
            case "rewrite": File.WriteAllText(path, "CURRENT".PadRight(old.Length - 1, '.') + "\n"); File.SetLastWriteTimeUtc(path, stamp); break;
            case "missing": File.WriteAllText(path, "CURRENT\n"); break;
        }
        // Both startup and later-exit paths read the same immutable boundary.
        var tail = session.Describe();
        Assert.Equal(tail, session.Describe());
        Assert.DoesNotContain("OLD", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopping!", tail, StringComparison.Ordinal);
        Assert.False(MinecraftProcessService.NamesOutOfMemory("", tail));
        if (mode is "untouched" or "touch") Assert.Empty(tail);
        else Assert.Contains("CURRENT", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionTailIsBoundedAndUnknownOrDeletedBoundariesFailClosed()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "latest.log");
        File.WriteAllText(path, "OLD\n");
        SessionGameLog unknown;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            unknown = new SessionGameLog(_root);
        File.AppendAllText(path, "CURRENT\n");
        Assert.Empty(unknown.Describe());
        var known = new SessionGameLog(_root);
        File.AppendAllText(path, new string('x', 2 * 1024 * 1024) + "\nCURRENT\n");
        var tail = known.Describe();
        Assert.Contains("CURRENT", tail, StringComparison.Ordinal);
        Assert.True(tail.Length < 64 * 1024);
        File.Delete(path);
        Assert.Empty(known.Describe());
        // Startup must not hash an unbounded historical log. Unknown suffixes
        // are omitted, while a subsequent rotation still gives a fresh file.
        File.WriteAllText(path, new string('x', 17 * 1024 * 1024));
        var oversized = new SessionGameLog(_root);
        File.AppendAllText(path, "\nCURRENT\n");
        Assert.Empty(oversized.Describe());
        File.Move(path, Path.Combine(logs, "large-old.log"));
        File.WriteAllText(path, "CURRENT\n");
        Assert.Contains("CURRENT", oversized.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualEarlyProcessFailureAndLaterDiagnosticContainOnlyCurrentOutput()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "latest.log"), "OLD OutOfMemoryError: Java heap space\nOLD Stopping!\n");
        var session = new SessionGameLog(_root);
        var buffer = new MinecraftProcessService.StartupOutputBuffer();
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d"); process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("echo CURRENT process output & echo Native memory allocation failed: DOS error/errno=1455 1>&2 & exit /b 1");
        process.OutputDataReceived += (_, e) => buffer.Append(e.Data);
        process.ErrorDataReceived += (_, e) => buffer.Append(e.Data);
        Assert.True(process.Start());
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        var early = MinecraftProcessService.DescribeProcessFailure(buffer, session);
        Assert.Contains("CURRENT process output", early, StringComparison.Ordinal);
        Assert.Contains("Это не переполнение Java heap", early, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD", early, StringComparison.Ordinal);
        // A later exit may have enough console traffic to evict the original error line.
        buffer.Append(new string('x', 16000));
        var later = MinecraftProcessService.DescribeProcessFailure(buffer, session);
        Assert.Contains("1455", later, StringComparison.Ordinal);
        Assert.Contains("Это не переполнение Java heap", later, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD", later, StringComparison.Ordinal);
        Assert.False(MinecraftProcessService.NamesOutOfMemory(buffer.Describe(), session.Describe()));
        buffer.Close();
    }
}
