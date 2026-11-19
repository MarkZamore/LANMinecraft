using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;

namespace Minecraft;

/// <summary>Short, reversible addon reveals that leave ordinary automatic layout in place.</summary>
public static class LibraryTreeMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(LibraryTreeMotion), new PropertyMetadata(false, EnabledChanged));
    public static bool GetIsEnabled(DependencyObject owner) => (bool)owner.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject owner, bool value) => owner.SetValue(IsEnabledProperty, value);

    private sealed class Transition { public int Version; public bool Listening; }
    private static readonly ConditionalWeakTable<TreeViewItem, Transition> Transitions = new();

    private static void EnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not TreeViewItem item) return;
        if ((bool)e.NewValue)
        {
            item.Loaded += Loaded;
            item.Unloaded += Unloaded;
            if (item.IsLoaded) Attach(item);
        }
        else
        {
            item.Loaded -= Loaded;
            item.Unloaded -= Unloaded;
            Detach(item);
        }
    }

    private static void Loaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource)) Attach((TreeViewItem)sender);
    }

    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource)) Detach((TreeViewItem)sender);
    }

    private static void Attach(TreeViewItem item)
    {
        var transition = Transitions.GetOrCreateValue(item);
        if (!transition.Listening)
        {
            item.Expanded += ExpandedChanged;
            item.Collapsed += ExpandedChanged;
            transition.Listening = true;
        }
        Update(item, animate: false);
    }

    private static void Detach(TreeViewItem item)
    {
        var transition = Transitions.GetOrCreateValue(item);
        item.Expanded -= ExpandedChanged;
        item.Collapsed -= ExpandedChanged;
        transition.Listening = false;
        Update(item, animate: false);
    }

    private static void ExpandedChanged(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource)) Update((TreeViewItem)sender, animate: true);
    }

    private static void Update(TreeViewItem item, bool animate)
    {
        item.ApplyTemplate();
        if (item.Template?.FindName("Expander", item) is ToggleButton toggle)
        {
            toggle.ApplyTemplate();
            if (toggle.Template?.FindName("Chevron", toggle) is Path arrow)
                ControlMotion.TurnChevron(arrow, item.IsExpanded, animate && item.IsLoaded);
        }
        if (item.Template?.FindName("ChildrenReveal", item) is not Border content) return;
        var transition = Transitions.GetOrCreateValue(item);
        var version = ++transition.Version;
        var shown = content.Visibility == Visibility.Collapsed ? 0 :
            double.IsNaN(content.Height) ? content.ActualHeight : content.Height;
        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.ClearValue(FrameworkElement.HeightProperty);
        if (!animate || !item.IsLoaded || !SystemParameters.ClientAreaAnimation || !item.HasItems)
        {
            content.ClearValue(UIElement.VisibilityProperty);
            return;
        }

        content.Visibility = Visibility.Visible;
        // Measure the unrestrained presenter rather than its animated wrapper.
        if (content.Child is not UIElement child) return;
        var width = content.Parent is Grid grid
            ? grid.ColumnDefinitions[Grid.GetColumn(content)].ActualWidth : content.ActualWidth;
        child.Measure(new Size(Math.Max(1, width > 0 ? width : item.ActualWidth), double.PositiveInfinity));
        var target = item.IsExpanded ? child.DesiredSize.Height : 0;
        // Keep the base value at the currently displayed height until WPF starts
        // the replacement clock, including reversals within the same render tick.
        content.Height = shown;
        var animation = new DoubleAnimation(shown, target, (Duration)item.FindResource("Motion.Chevron"))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) =>
        {
            if (transition.Version != version) return;
            content.BeginAnimation(FrameworkElement.HeightProperty, null);
            content.ClearValue(FrameworkElement.HeightProperty);
            content.ClearValue(UIElement.VisibilityProperty);
        };
        content.BeginAnimation(FrameworkElement.HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
