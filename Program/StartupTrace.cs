using System.Diagnostics;
using System.Globalization;

namespace Minecraft;

/// <summary>
/// How long the launcher took to put its window in front of the player, and
/// where that time went. Somebody who waits with nothing on screen decides the
/// thing has hung, so this is the one measurement worth carrying in every
/// launch's log: without it, "у меня долго запускается" is a report nobody can
/// act on. One line, written once, after the window is actually painted.
/// </summary>
internal static class StartupTrace
{
    // The clock starts when this type is first touched, which is inside
    // OnStartup; everything before that - the runtime coming up, the bundle
    // being paged in, the scanner reading 140 megabytes - is already spent, and
    // asking the OS how old the process is, is the only way to see it.
    private static readonly long AlreadySpentMs = AgeOfThisProcess();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<(string Name, long At)> Marks = [];

    /// <summary>Milliseconds since the player double-clicked, near enough.</summary>
    public static long Elapsed => AlreadySpentMs + Clock.ElapsedMilliseconds;

    /// <summary>Names a moment. Cheap enough to call from the startup path.</summary>
    public static void Mark(string name)
    {
        lock (Marks) Marks.Add((name, Elapsed));
    }

    /// <summary>
    /// The whole run as one line: each mark with the time spent reaching it.
    /// </summary>
    public static string Describe()
    {
        lock (Marks)
        {
            if (Marks.Count == 0) return $"{Elapsed} мс";
            var parts = new List<string>(Marks.Count + 1);
            var previous = 0L;
            foreach (var (name, at) in Marks)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {at - previous}"));
                previous = at;
            }
            return string.Join(", ", parts) + $" = {previous} мс";
        }
    }

    private static long AgeOfThisProcess()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var age = (DateTime.Now - current.StartTime).TotalMilliseconds;
            return age > 0 && age < 600_000 ? (long)age : 0;
        }
        catch
        {
            // Without it the numbers start from here instead of from the
            // double-click, which is still worth having.
            return 0;
        }
    }
}
