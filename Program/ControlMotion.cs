using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Minecraft;

internal static class ControlMotion
{
    private sealed class CategoryTransition { public int Version; }
    private static readonly ConditionalWeakTable<Expander, CategoryTransition> CategoryTransitions = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Expander), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(CategoryLoaded));
        EventManager.RegisterClassHandler(typeof(Expander), Expander.ExpandedEvent,
            new RoutedEventHandler(CategoryChanged));
        EventManager.RegisterClassHandler(typeof(Expander), Expander.CollapsedEvent,
            new RoutedEventHandler(CategoryChanged));
    }

    private static void CategoryLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander category && e.OriginalSource == category && HasLauncherTheme(category))
            UpdateCategory(category, animate: false);
    }

    private static void CategoryChanged(object sender, RoutedEventArgs e)
    {
        if (sender is Expander category && e.OriginalSource == category && HasLauncherTheme(category))
            UpdateCategory(category, animate: true);
    }

    private static bool HasLauncherTheme(Expander category) =>
        category.TryFindResource("Motion.SectionExpand") is Duration;

    public static void UpdateCategory(Expander category, bool animate)
    {
        category.ApplyTemplate();
        if (category.Template.FindName("HeaderToggle", category) is ToggleButton header)
        {
            header.ApplyTemplate();
            if (header.Template.FindName("Chevron", header) is Path chevron)
                TurnChevron(chevron, category.IsExpanded, animate && category.IsLoaded);
        }
        if (category.Template.FindName("SectionContent", category) is not Border content) return;
        var shownHeight = double.IsNaN(content.Height) ? content.ActualHeight : content.Height;
        var shownOpacity = content.Opacity;
        var reversing = content.HasAnimatedProperties;
        var transition = CategoryTransitions.GetOrCreateValue(category);
        var version = ++transition.Version;
        content.BeginAnimation(FrameworkElement.HeightProperty, null);
        content.ClearValue(FrameworkElement.HeightProperty);
        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Visibility = Visibility.Visible;
        if (!animate || !category.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            content.ClearValue(UIElement.VisibilityProperty);
            return;
        }
        content.Measure(new Size(Math.Max(1, category.ActualWidth), double.PositiveInfinity));
        var height = content.DesiredSize.Height;
        var from = reversing ? Math.Max(0, shownHeight) : category.IsExpanded ? 0 : shownHeight > 0 ? shownHeight : height;
        var target = category.IsExpanded ? height : 0;
        content.ClipToBounds = true;
        content.Height = target;
        var reveal = new DoubleAnimation(from, target, Duration(category,
            category.IsExpanded ? "Motion.SectionExpand" : "Motion.SectionCollapse"))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        reveal.Completed += (_, _) =>
        {
            if (transition.Version != version) return;
            content.BeginAnimation(FrameworkElement.HeightProperty, null);
            content.ClearValue(FrameworkElement.HeightProperty);
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.ClearValue(UIElement.VisibilityProperty);
        };
        content.BeginAnimation(FrameworkElement.HeightProperty, reveal);
        content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(
            category.IsExpanded && !reversing ? 0 : shownOpacity, category.IsExpanded ? 1 : 0,
            Duration(category, "Motion.SectionFade")));
    }

    public static void TurnChevron(Path chevron, bool expanded, bool animate)
    {
        var transform = chevron.RenderTransform as RotateTransform;
        if (transform is null || transform.IsFrozen)
            chevron.RenderTransform = transform = transform?.Clone() ?? new RotateTransform();
        var from = transform.Angle;
        var target = expanded ? 0 : -90;
        transform.BeginAnimation(RotateTransform.AngleProperty, null);
        transform.Angle = target;
        if (animate && SystemParameters.ClientAreaAnimation && Math.Abs(from - target) > 0.001)
            transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(
                from, target, Duration(chevron, "Motion.Chevron"))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }

    private static Duration Duration(FrameworkElement owner, string key) => (Duration)owner.FindResource(key);
}
