using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>One below-input-priority raster worker; obsolete variants never allocate pixel buffers.</summary>
internal static class PreparedImageWorkQueue
{
    private sealed record Work(BitmapSource Fallback, TaskCompletionSource<BitmapSource> Completion,
        Func<bool> IsCurrent, Func<BitmapSource> Render)
    {
        public int Priority;
    }
    private static readonly object Gate = new();
    private static readonly List<Work> Pending = [];
    private static bool running;

    internal static void Enqueue(BitmapSource fallback, TaskCompletionSource<BitmapSource> completion,
        Func<bool> isCurrent, Func<BitmapSource> render)
    {
        lock (Gate)
        {
            while (Pending.Count >= 64)
            {
                var dropped = Pending.OrderByDescending(item => item.Priority).First();
                Pending.Remove(dropped);
                dropped.Completion.TrySetResult(dropped.Fallback);
            }
            Pending.Add(new Work(fallback, completion, isCurrent, render));
            if (running) return;
            running = true;
        }
        StartNext();
    }

    internal static void Promote(Task<BitmapSource> task)
    {
        lock (Gate)
            foreach (var work in Pending.Where(item => ReferenceEquals(item.Completion.Task, task))) work.Priority = -1;
    }

    private static void StartNext()
    {
        Work? work;
        lock (Gate)
        {
            if (Pending.Count == 0) { running = false; return; }
            work = Pending.OrderBy(item => item.Priority).First();
            Pending.Remove(work);
        }
        var worker = new Thread(() =>
        {
            try
            {
                work.Completion.TrySetResult(work.IsCurrent() ? work.Render() : work.Fallback);
            }
            catch (Exception error) { work.Completion.TrySetException(error); }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                StartNext();
            }
        }) { IsBackground = true, Name = "Launcher image preparation", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }
}
