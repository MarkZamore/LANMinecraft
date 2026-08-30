using System.IO;
using System.Threading;

namespace Minecraft;

/// <summary>
/// One launcher at a time, and a second press of the icon brings the first one
/// forward instead of opening another window.
///
/// Two copies are not merely untidy: they hold the same settings file, the same
/// instance folders and the same Steam connection, and the second one to write
/// wins. A player who double-clicks - which is what an icon invites - would get
/// two, and nothing on screen would say which of them their next click reached.
/// </summary>
/// <remarks>
/// The names are per session (<c>Local\</c>), not machine-wide: two people
/// signed into the same computer each get their own launcher, which is the same
/// rule their settings and instances already follow.
///
/// A short wait covers the one moment two copies are legitimately alive: the
/// update helper starts the new executable after the old one exits, and "after"
/// is a few milliseconds of overlap on a slow machine. Half a second of
/// patience there costs a second copy nothing it would not have spent anyway.
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\LANMinecraft.SingleInstance";
    private const string SignalName = @"Local\LANMinecraft.ShowWindow";
    private static readonly TimeSpan RestartGrace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(50);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _listener;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle signal)
    {
        _mutex = mutex;
        _signal = signal;
        _listener = new Thread(Listen) { IsBackground = true, Name = "single-instance" };
        _listener.Start();
    }

    /// <summary>Raised when another copy was started and asked for this window.</summary>
    public event Action? AnotherInstanceStarted;

    /// <summary>
    /// Takes the guard, or hands back null when a launcher is already running.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire() => TryAcquire("");

    /// <summary>
    /// The same guard under a name of its own. Only the tests use this: they
    /// have to be able to take a guard while a real launcher is running on the
    /// same machine, which is most of the time on the machine it is written on.
    /// </summary>
    internal static SingleInstanceGuard? TryAcquire(string scope)
    {
        // The kernel decides who created the object, and exactly one caller is
        // told it did - which is the whole test, and it costs no ownership and
        // no thread affinity. A mutex that is waited on instead would be owned
        // by a thread, and a second attempt from the same thread would sail
        // straight through it.
        var deadline = DateTime.UtcNow + RestartGrace;
        while (true)
        {
            var mutex = new Mutex(false, MutexName + scope, out var createdNew);
            if (createdNew)
            {
                var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName + scope);
                return new SingleInstanceGuard(mutex, signal);
            }

            mutex.Dispose();
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(RetryPause);
        }
    }

    /// <summary>
    /// Asks the launcher that is already running to come forward. Silent when
    /// there is nothing listening: the caller is about to exit either way.
    /// </summary>
    public static void AskRunningInstanceToShowItself() => AskRunningInstanceToShowItself("");

    /// <summary>The same request, under a name of its own; see the note above.</summary>
    internal static void AskRunningInstanceToShowItself(string scope)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName + scope, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }
    }

    private void Listen()
    {
        var wait = new[] { _signal, _stopping.Token.WaitHandle };
        while (!_stopping.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(wait) != 0) return;
            AnotherInstanceStarted?.Invoke();
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        // Closing the handle is what hands the guard back: the kernel keeps the
        // object alive only while somebody holds one.
        _mutex.Dispose();
        _signal.Dispose();
        _stopping.Dispose();
    }
}
