using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Minecraft;

internal static class UiPreparation
{
    public static IEnumerable<Action> Prepare(FrameworkElement root)
    {
        yield return () =>
        {
            FrameworkElement? ancestor = root;
            while (ancestor is not null && ancestor.ActualWidth < 1)
                ancestor = LogicalTreeHelper.GetParent(ancestor) as FrameworkElement;
            Measure(root, Math.Max(1, ancestor?.ActualWidth ?? 1), VisualTreeHelper.GetDpi(root));
        };
        var pending = new Queue<FrameworkElement>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var element))
        {
            yield return () =>
            {
                element.ApplyTemplate();
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                    if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child) pending.Enqueue(child);
            };
        }
    }
    public static void Measure(FrameworkElement root, double width, DpiScale dpi)
    {
        if (root.IsLoaded && root.IsVisible) { root.UpdateLayout(); return; }
        if (VisualTreeHelper.GetParent(root) is null) VisualTreeHelper.SetRootDpi(root, dpi);
        root.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        root.Arrange(new Rect(new Point(), root.DesiredSize));
    }
}
