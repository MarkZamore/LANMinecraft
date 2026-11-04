using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>One common opaque surface variant per window, anchored to its fixed photograph.</summary>
internal sealed class BackdropSurfaceCache : IDisposable
{
    private static readonly DependencyProperty CommonSurfaceProperty = DependencyProperty.RegisterAttached(
        "CommonSurface", typeof(Brush), typeof(BackdropSurfaceCache), new PropertyMetadata(null, OnCommonSurfaceChanged));
    private readonly Window window;
    private readonly PreparedWindowBackdrop backdrop;
    private readonly UiPreloadQueue queue;
    private readonly HashSet<BackdropSurfaceBorder> borders = [];
    private readonly HashSet<UIElement> transformOwners = [];
    private readonly HashSet<FrameworkElement> layoutTransformOwners = [];
    private static readonly DependencyPropertyDescriptor RenderTransformDescriptor =
        DependencyPropertyDescriptor.FromProperty(UIElement.RenderTransformProperty, typeof(UIElement));
    private static readonly DependencyPropertyDescriptor LayoutTransformDescriptor =
        DependencyPropertyDescriptor.FromProperty(FrameworkElement.LayoutTransformProperty, typeof(FrameworkElement));
    private readonly string key;
    private readonly string publishKey;
    private static int nextId;
    private readonly record struct Tint(Color Color, double Opacity);
    private Tint? tint;
    private BitmapSource? bitmap;
    private long frameGeneration = -1;
    private long version;
    private bool pending;
    private bool scheduled;
    private bool disposed;
    public bool IsPreparing => !disposed && (pending || scheduled);
    public long PreparationCount { get; private set; }
    public string? LastError { get; private set; }
    internal BackdropSurfaceCache(Window window, PreparedWindowBackdrop backdrop, UiPreloadQueue queue)
    {
        this.window = window; this.backdrop = backdrop; this.queue = queue;
        key = "window-surface:" + Interlocked.Increment(ref nextId);
        publishKey = key + ":publish";
        backdrop.PreparationChanged += OnBackdropChanged;
        window.LayoutUpdated += OnLayoutUpdated;
        window.SetResourceReference(CommonSurfaceProperty, "Brush.Surface");
        Visit(window);
    }
    private void Visit(DependencyObject root)
    {
        if (root is BackdropSurfaceBorder { IsBackdropSurface: true } surface) Register(surface);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) Visit(VisualTreeHelper.GetChild(root, index));
    }
    internal void Register(BackdropSurfaceBorder border)
    {
        border.Connect(this); borders.Add(border);
        DependencyObject? element = border;
        while (element is not null && !ReferenceEquals(element, window))
        {
            if (element is UIElement visual && transformOwners.Add(visual)) RenderTransformDescriptor.AddValueChanged(visual, OnTransformChanged);
            if (element is FrameworkElement layout && layoutTransformOwners.Add(layout)) LayoutTransformDescriptor.AddValueChanged(layout, OnTransformChanged);
            element = VisualTreeHelper.GetParent(element);
        }
        Schedule();
    }
    internal void Unregister(BackdropSurfaceBorder border) { borders.Remove(border); Schedule(); }
    private void OnBackdropChanged(object? sender, EventArgs args) => Invalidate();
    private static void OnCommonSurfaceChanged(DependencyObject dependency, DependencyPropertyChangedEventArgs args)
    {
        if (dependency is Window owner && PreparedWindowBackdrop.TryFind(owner, out var backdrop) &&
            backdrop.SurfaceCache is { disposed: false } cache) cache.Invalidate();
    }
    internal void Invalidate()
    {
        version++; bitmap = null; tint = null; frameGeneration = -1; LastError = null;
        foreach (var border in borders) border.ClearPrepared();
        Schedule();
    }
    internal void Schedule()
    {
        if (disposed || scheduled) return;
        scheduled = true;
        queue.Forget(key);
        queue.Enqueue(key, 30, Refresh());
    }
    private IEnumerable<Action> Refresh()
    {
        yield return () =>
        {
            scheduled = false;
            if (disposed) return;
            if (!backdrop.PrepareSurfaceBackgrounds || backdrop.CurrentFrame is not { } frame)
            { foreach (var border in borders) border.ClearPrepared(); return; }
            var eligible = borders.Select(border => (Border: border, Tint: EligibleTint(border)))
                .Where(item => item.Tint.HasValue).ToArray();
            var common = window.GetValue(CommonSurfaceProperty) as SolidColorBrush;
            var wanted = GetTint(common) ?? eligible.GroupBy(item => item.Tint!.Value).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key;
            if (wanted is null) { foreach (var border in borders) border.ClearPrepared(); return; }
            if (bitmap is not null && tint == wanted && frameGeneration == frame.Generation)
            { Apply(frame); return; }
            foreach (var border in borders) border.ClearPrepared();
            if (pending) return;
            pending = true; PreparationCount++; LastError = null;
            var requestVersion = ++version;
            var color = new SolidColorBrush(wanted.Value.Color) { Opacity = wanted.Value.Opacity }; color.Freeze();
            var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            PreparedImageWorkQueue.Enqueue(frame.Image, completion,
                () => !disposed && Volatile.Read(ref version) == requestVersion,
                () => Materialize(frame.Image, color));
            _ = PublishAsync(completion.Task, frame, wanted.Value, requestVersion);
        };
    }
    private async Task PublishAsync(Task<BitmapSource> task, PreparedWindowBackdrop.PreparedFrame frame, Tint requested, long requestVersion)
    {
        BitmapSource? result = null; Exception? failure = null;
        try { result = await task.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        if (window.Dispatcher.HasShutdownStarted) return;
        try
        {
            await window.Dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                queue.Forget(publishKey);
                queue.Enqueue(publishKey, 30, Publish(result, failure, frame, requested, requestVersion));
            }, DispatcherPriority.Background);
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) when (window.Dispatcher.HasShutdownStarted) { }
    }
    private IEnumerable<Action> Publish(BitmapSource? result, Exception? failure, PreparedWindowBackdrop.PreparedFrame frame, Tint requested, long requestVersion)
    {
        yield return () =>
        {
            pending = false;
            if (disposed) return;
            if (failure is not null) { LastError = failure.Message; return; }
            if (requestVersion == version && backdrop.CurrentFrame is { } current && current.Generation == frame.Generation &&
                result is not null && !ReferenceEquals(result, frame.Image))
            { bitmap = result; tint = requested; frameGeneration = frame.Generation; Apply(current); }
            Schedule();
        };
    }
    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        if (disposed || bitmap is null || backdrop.CurrentFrame is not { } frame || frame.Generation != frameGeneration) return;
        Apply(frame);
    }
    private void OnTransformChanged(object? sender, EventArgs args) => OnLayoutUpdated(sender, args);
    private void Apply(PreparedWindowBackdrop.PreparedFrame frame)
    {
        if (bitmap is null || tint is null) return;
        foreach (var border in borders)
        {
            if (!border.IsVisible) continue;
            if (EligibleTint(border) != tint) { border.ClearPrepared(); continue; }
            var origin = border.TransformToAncestor(window).Transform(new Point());
            border.UsePrepared(new(bitmap, new Rect(-origin.X, -origin.Y, frame.LogicalSize.Width, frame.LogicalSize.Height)));
        }
    }
    private Tint? EligibleTint(BackdropSurfaceBorder border)
    {
        if (!border.IsBackdropSurface || !border.IsLoaded || border.OpacityMask is not null || border.Effect is not null ||
            border.SourceBrush is not SolidColorBrush brush || GetTint(brush) is not { } requestedTint) return null;
        DependencyObject? ancestor = VisualTreeHelper.GetParent(border);
        while (ancestor is not null && !ReferenceEquals(ancestor, window))
        {
            if (ancestor is UIElement { Effect: not null } or UIElement { OpacityMask: not null }) return null;
            var background = ancestor switch { Border parent => parent.Background, Panel panel => panel.Background, Control control => control.Background, _ => null };
            if (Paints(background) && ancestor is not FrameworkElement { TemplatedParent: Window } &&
                !ReferenceEquals(ancestor, border.TemplatedParent)) return null;
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }
        if (ancestor is null) return null;
        var transform = border.TransformToAncestor(window);
        var origin = transform.Transform(new Point());
        var dpi = VisualTreeHelper.GetDpi(window);
        // A fractional physical origin would antialias the split between the
        // translucent perimeter and opaque interior. Keep the native brush for
        // that transient state; ordinary rounded layout is already aligned.
        if (Math.Abs(origin.X * dpi.DpiScaleX - Math.Round(origin.X * dpi.DpiScaleX)) > 0.000001 ||
            Math.Abs(origin.Y * dpi.DpiScaleY - Math.Round(origin.Y * dpi.DpiScaleY)) > 0.000001) return null;
        var x = transform.Transform(new Point(1, 0)); var y = transform.Transform(new Point(0, 1));
        if (Math.Abs(x.X - origin.X - 1) > 0.000001 || Math.Abs(x.Y - origin.Y) > 0.000001 ||
            Math.Abs(y.X - origin.X) > 0.000001 || Math.Abs(y.Y - origin.Y - 1) > 0.000001) return null;
        return requestedTint;
    }
    private static Tint? GetTint(SolidColorBrush? brush) => brush is null || brush.HasAnimatedProperties || !brush.CanFreeze ||
        brush.Opacity <= 0 || brush.Color.A == 0 || brush.Color.A == byte.MaxValue && brush.Opacity == 1 ? null : new Tint(brush.Color, brush.Opacity);
    private static bool Paints(Brush? brush) => brush is not null && brush.Opacity > 0 && (brush is not SolidColorBrush solid || solid.Color.A > 0);
    internal static BitmapSource Materialize(BitmapSource picture, SolidColorBrush color)
    {
        var bounds = new Rect(0, 0, picture.Width, picture.Height);
        var drawings = new DrawingGroup();
        drawings.Children.Add(new ImageDrawing(picture, bounds));
        drawings.Children.Add(new GeometryDrawing(color, null, new RectangleGeometry(bounds)));
        var brush = new DrawingBrush(drawings) { Stretch = Stretch.Fill }; brush.Freeze();
        return PreparedWindowBackdrop.Rasterize(brush, picture.PixelWidth, picture.PixelHeight, new DpiScale(picture.DpiX / 96, picture.DpiY / 96));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; version++;
        backdrop.PreparationChanged -= OnBackdropChanged;
        window.LayoutUpdated -= OnLayoutUpdated;
        foreach (var owner in transformOwners) RenderTransformDescriptor.RemoveValueChanged(owner, OnTransformChanged);
        foreach (var owner in layoutTransformOwners) LayoutTransformDescriptor.RemoveValueChanged(owner, OnTransformChanged);
        transformOwners.Clear(); layoutTransformOwners.Clear();
        window.ClearValue(CommonSurfaceProperty);
        queue.Forget(key);
        queue.Forget(publishKey);
        foreach (var border in borders) border.Disconnect(this);
        borders.Clear(); bitmap = null;
    }
}
