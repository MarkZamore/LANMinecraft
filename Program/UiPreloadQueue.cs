using System.Diagnostics;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>One cooperative, window-owned queue. No WPF work runs on worker threads.</summary>
public sealed class UiPreloadQueue : IDisposable
{
    private sealed class Job(string key, int priority, IEnumerator<Action> steps)
    {
        public readonly string Key = key;
        public readonly long Started = Stopwatch.GetTimestamp();
        public int Priority = priority;
        public readonly IEnumerator<Action> Steps = steps;
    }
    private readonly Dispatcher dispatcher;
    private readonly Action<Exception> failure;
    private readonly List<Job> jobs = [];
    private readonly HashSet<string> completed = [];
    private DispatcherOperation? pending;
    private bool disposed;
    public UiPreloadQueue(Dispatcher dispatcher, Action<Exception> failure)
    { this.dispatcher = dispatcher; this.failure = failure; }
    public void Enqueue(string key, int priority, IEnumerable<Action> steps)
    {
        dispatcher.VerifyAccess();
        if (disposed || completed.Contains(key) || jobs.Any(j => j.Key == key)) return;
        jobs.Add(new Job(key, priority, steps.GetEnumerator()));
        Schedule();
    }
    public void Promote(string key)
    {
        dispatcher.VerifyAccess();
        foreach (var job in jobs.Where(j => j.Key == key)) job.Priority = -1;
    }
    public string[] PendingKeys => jobs.Select(j => j.Key).ToArray();
    public long ExecutedSteps { get; private set; }
    public event Action<string, long>? Completed;
    public bool IsComplete(string key) => completed.Contains(key);
    public void Forget(string key)
    {
        dispatcher.VerifyAccess();
        completed.Remove(key);
        foreach (var job in jobs.Where(j => j.Key == key).ToArray())
        { jobs.Remove(job); job.Steps.Dispose(); }
    }
    private void Schedule()
    {
        if (!disposed && pending is null && jobs.Count > 0)
            pending = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Run));
    }
    private void Run()
    {
        pending = null;
        var clock = Stopwatch.StartNew();
        while (!disposed && jobs.Count > 0 && clock.Elapsed.TotalMilliseconds < 4)
        {
            var job = jobs.OrderBy(j => j.Priority).First();
            try
            {
                if (job.Steps.MoveNext()) { job.Steps.Current(); ExecutedSteps++; }
                else { jobs.Remove(job); job.Steps.Dispose(); completed.Add(job.Key); Completed?.Invoke(job.Key, (long)Stopwatch.GetElapsedTime(job.Started).TotalMilliseconds); }
            }
            catch (Exception ex) { jobs.Remove(job); job.Steps.Dispose(); failure(ex); }
        }
        Schedule();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pending?.Abort(); pending = null;
        foreach (var job in jobs) job.Steps.Dispose();
        jobs.Clear(); completed.Clear(); Completed = null;
    }
}
