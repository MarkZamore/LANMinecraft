using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// The game keeps yesterday's sessions gzipped, and a report used to carry
/// those files through a UTF-8 reader: 68 % of every byte came out as a
/// replacement character, so the archives arrived unreadable and unrecoverable.
/// They are unpacked now, and only their tail is kept.
/// </summary>
public sealed class BugReportGzipTests
{
    [Fact]
    public void AGzippedLog_ArrivesAsItsOwnText()
    {
        var path = WriteGzippedLog(Enumerable.Range(1, 200).Select(n => $"[строка {n}] привет, мир"));
        try
        {
            var text = ReadTail(path, maxBytes: 64 * 1024);

            Assert.Contains("[строка 200] привет, мир", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ALongGzippedLog_KeepsItsEndAndNotItsStart()
    {
        var path = WriteGzippedLog(Enumerable.Range(1, 20_000).Select(n => $"[{n:D5}] {new string('x', 200)}"));
        try
        {
            var text = ReadTail(path, maxBytes: 16 * 1024);

            Assert.Contains("[20000]", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[00001]", text, StringComparison.Ordinal);
            Assert.True(text.Length <= 16 * 1024, "the tail should be bounded by what was asked for");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteGzippedLog(IEnumerable<string> lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "ll8-log-" + Guid.NewGuid().ToString("N") + ".log.gz");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        foreach (var line in lines) writer.WriteLine(line);
        return path;
    }

    /// <summary>Calls the service's own reader, so the test cannot drift from it.</summary>
    private static string ReadTail(string path, int maxBytes)
    {
        var type = typeof(BugReportService);
        var service = (BugReportService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        SetField(service, "_sanitizer", SupportLogSanitizer.CreateDefault(new AppPaths(Path.GetTempPath())));
        var method = type.GetMethod("ReadSanitizedTail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)method.Invoke(service, [path, maxBytes])!;
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }
}
