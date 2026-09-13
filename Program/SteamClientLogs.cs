using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Minecraft;

/// <summary>
/// The Steam client's own record of its connection to Steam's servers.
/// </summary>
/// <remarks>
/// When a guest's launcher says Steam went away, the question is whether Steam
/// itself lost its servers - a network blip, a laptop waking, the account
/// signed in elsewhere - and only the Steam client's connection_log.txt says.
/// It lived on each player's machine and nowhere else, so it has to travel with
/// a report. The folder is found through the running Steam process, which
/// knows where it was installed, rather than through the registry.
/// </remarks>
public static partial class SteamClientLogs
{
    public const string ConnectionLogName = "connection_log.txt";

    /// <summary>SteamID64 of account 0 in the individual universe; a SteamID3 is the difference.</summary>
    private const ulong IndividualAccountBase = 76561197960265728;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [GeneratedRegex(@"\[U:1:(?<account>\d+)\]")]
    private static partial Regex AccountId();

    [GeneratedRegex(@"(?<=Using JWT )\d+")]
    private static partial Regex TokenId();

    /// <summary>The logs folder of the running Steam client, or null when there is none to find.</summary>
    /// <remarks>
    /// Asked with the limited query right, which Windows grants even for a
    /// Steam started as administrator - reading the process's modules is not.
    /// </remarks>
    public static string? FindDirectory()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("steam");
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        string? found = null;
        foreach (var process in processes)
        {
            try
            {
                if (found is not null) continue;
                var executable = ImagePathOf(process.Id);
                var directory = executable is null ? null : Path.GetDirectoryName(executable);
                if (directory is null) continue;
                var logs = Path.Combine(directory, "logs");
                if (Directory.Exists(logs)) found = logs;
            }
            catch (InvalidOperationException)
            {
                // The process ended between listing and asking.
            }
            finally
            {
                process.Dispose();
            }
        }
        return found;
    }

    private static string? ImagePathOf(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// The lines of the connection log from the last <paramref name="window"/>,
    /// keeping the last <paramref name="maxChars"/> characters when there are more.
    /// </summary>
    /// <remarks>
    /// Steam stamps each entry with its local time as [yyyy-MM-dd HH:mm:ss]. A
    /// line without a stamp belongs to the entry above it. The log belongs to
    /// the Steam installation, not to one account, so on a shared computer it
    /// also holds whoever else signed in that day: any account other than the
    /// sender's is masked, and so are the ids of the sign-in tokens.
    /// </remarks>
    public static IReadOnlyList<string> RecentConnectionLines(
        string path, DateTime nowLocal, TimeSpan window, int maxChars, ulong? ownSteamId64 = null)
    {
        var from = nowLocal - window;
        var own = ownSteamId64 is { } id && id > IndividualAccountBase
            ? (id - IndividualAccountBase).ToString(CultureInfo.InvariantCulture)
            : null;
        var kept = new Queue<string>();
        long size = 0;
        var inWindow = false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } raw)
        {
            if (TryReadStamp(raw, out var stamp)) inWindow = stamp >= from;
            if (!inWindow) continue;

            var line = AccountId().Replace(raw, match =>
            {
                var account = match.Groups["account"].Value;
                return account == "0" || account == own ? match.Value : "[U:1:<другой аккаунт>]";
            });
            line = TokenId().Replace(line, "<id>");
            kept.Enqueue(line);
            size += line.Length + 1;
            while (size > maxChars && kept.Count > 0)
            {
                size -= kept.Dequeue().Length + 1;
            }
        }
        return [.. kept];
    }

    private static bool TryReadStamp(string line, out DateTime stamp)
    {
        stamp = default;
        return line.Length >= 21 && line[0] == '[' && line[20] == ']' &&
               DateTime.TryParseExact(
                   line.AsSpan(1, 19), "yyyy-MM-dd HH:mm:ss",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
