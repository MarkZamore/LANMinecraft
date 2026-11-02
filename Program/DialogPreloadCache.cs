using System.Windows;
namespace Minecraft;
internal static class DialogPreloadCache
{
    private static readonly Dictionary<Type, Window> ready = [];
    private static bool active;
    public static IEnumerable<Action> PreparePreset()
    { yield return () => { active = true; Store(ControlsPresetConfirmationDialog.Prepared()); }; }
    public static IEnumerable<Action> Prepare()
    {
        active = true;
        yield return () => Store(NoticeDialog.Prepared());
        yield return () => Store(WorldTransferConfirmationDialog.Prepared());
        yield return () => Store(BuildRemovalConfirmationDialog.Prepared());
        yield return () => Store(WorldRemovalConfirmationDialog.Prepared());
    }
    private static void Store(Window window)
    {
        if (ready.ContainsKey(window.GetType())) { window.Close(); return; }
        UiPreparation.Measure((FrameworkElement)window.Content, 500, new DpiScale(1, 1));
        ready[window.GetType()] = window;
    }
    public static T Take<T>(Func<T> create) where T : Window
    {
        var result = ready.Remove(typeof(T), out var window) ? (T)window : create();
        if (active) result.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => { if (active && !ready.ContainsKey(typeof(T))) Store(create()); }));
        return result;
    }
    public static void Clear()
    {
        active = false;
        var unused = ready.Values.ToArray();
        ready.Clear();
        // Constructed windows belong to Application.Windows even before Show.
        // Close them so OnLastWindowClose can terminate the launcher normally.
        foreach (var window in unused) window.Close();
    }
}
