using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Minecraft;

/// <summary>
/// Flattens only a static window backdrop at its physical client size. Controls,
/// text and hit testing remain live, and the original brush remains the fallback.
/// </summary>
public sealed class PreparedWindowBackdrop : IDisposable
{
    private const long MaximumPixels = 16L * 1024 * 1024;
    private static readonly ConditionalWeakTable<Window, PreparedWindowBackdrop> Attachments = new();
    private static readonly DependencyPropertyDescriptor BackgroundDescriptor =
        DependencyPropertyDescriptor.FromProperty(Window.BackgroundProperty, typeof(Window));
    private static readonly DependencyProperty TrackedResourceProperty = DependencyProperty.RegisterAttached(
        "TrackedResource", typeof(Brush), typeof(PreparedWindowBackdrop),
        new PropertyMetadata(null, OnTrackedResourceChanged));
    private static int nextId;
    private readonly Window window;
    private readonly UiPreloadQueue queue;
    private readonly Func<Brush, int, int, DpiScale, BitmapSource> renderer;
    private readonly string prepareKey;
    private readonly string publishKey;
    private Brush? original;
    private object? resourceKey;
    private bool resourceIsLocal;
    private ImageBrush? cached;
    private Target cachedTarget;
    private long cachedSourceVersion;
    private Request? requested;
    private long generation;
    private long sourceVersion;
    private long failedGeneration = -1;
    private bool contentRendered;
    private bool changingBackground;
    private bool changingTrackedResource;
    private bool closed;
    private bool disposed;
    private bool workerRunning;
    private BackdropSurfaceCache? surfaces;
    private bool prepareSurfaces = true;

    internal readonly record struct PreparedFrame(BitmapSource Image, Size LogicalSize, long Generation);
    internal event EventHandler? PreparationChanged;
    internal BackdropSurfaceCache? SurfaceCache => surfaces;
    internal static bool TryFind(Window owner, out PreparedWindowBackdrop attachment) => Attachments.TryGetValue(owner, out attachment!);
    internal PreparedFrame? CurrentFrame
    {
        get
        {
            if (!IsReady || cached?.ImageSource is not BitmapSource image || image.Format != PixelFormats.Bgr32) return null;
            var root = VisualTreeHelper.GetChildrenCount(window) == 0 ? null : VisualTreeHelper.GetChild(window, 0) as FrameworkElement;
            var size = root?.RenderSize ?? new Size(image.Width, image.Height);
            return new PreparedFrame(image, size, generation);
        }
    }
    public bool PrepareSurfaceBackgrounds
    {
        get => prepareSurfaces;
        set { window.Dispatcher.VerifyAccess(); if (prepareSurfaces == value) return; prepareSurfaces = value; surfaces?.Invalidate(); }
    }
    public bool IsSurfacePreparationPending => surfaces?.IsPreparing ?? false;
    public long SurfacePreparationCount => surfaces?.PreparationCount ?? 0;
    public string? LastSurfaceError => surfaces?.LastError;

    private readonly record struct Target(int Width, int Height, double DpiX, double DpiY);
    private sealed record Request(long Generation, long SourceVersion, Target Target, Brush Brush);

    public bool IsReady { get; private set; }
    public bool IsPreparing => !disposed && workerRunning;
    public int TargetPixelWidth => requested?.Target.Width ?? 0;
    public int TargetPixelHeight => requested?.Target.Height ?? 0;
    public long PreparationCount { get; private set; }
    public long AppliedCount { get; private set; }
    public string? LastError { get; private set; }

    public static PreparedWindowBackdrop Attach(Window window, UiPreloadQueue queue) => Attach(window, queue, Rasterize);

    internal static PreparedWindowBackdrop Attach(Window window, UiPreloadQueue queue,
        Func<Brush, int, int, DpiScale, BitmapSource> renderer)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(queue);
        window.Dispatcher.VerifyAccess();
        if (Attachments.TryGetValue(window, out var existing)) return existing;
        var attachment = new PreparedWindowBackdrop(window, queue, renderer);
        Attachments.Add(window, attachment);
        attachment.surfaces = new BackdropSurfaceCache(window, attachment, queue);
        return attachment;
    }

    private PreparedWindowBackdrop(Window window, UiPreloadQueue queue,
        Func<Brush, int, int, DpiScale, BitmapSource> renderer)
    {
        this.window = window;
        this.queue = queue;
        this.renderer = renderer;
        var id = Interlocked.Increment(ref nextId);
        prepareKey = "window-backdrop:" + id + ":prepare";
        publishKey = "window-backdrop:" + id + ":publish";
        SetOriginal(window.Background);
        TrackResource();
        window.ContentRendered += OnContentRendered;
        window.SizeChanged += OnSizeChanged;
        window.DpiChanged += OnDpiChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
        BackgroundDescriptor.AddValueChanged(window, OnBackgroundChanged);
        // Attaching after the first frame is useful to an isolated performance
        // probe. The launcher attaches before Loaded and waits for its first frame.
        if (window.IsLoaded) { contentRendered = true; Refresh(); }
    }

    private void OnContentRendered(object? sender, EventArgs e) { contentRendered = true; Refresh(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();
    private void OnDpiChanged(object sender, DpiChangedEventArgs e) => Refresh();
    private void OnStateChanged(object? sender, EventArgs e) => Refresh();
    private void OnOriginalChanged(object? sender, EventArgs e)
    {
        if (changingBackground || disposed) return;
        sourceVersion++;
        Invalidate();
    }
    private void OnClosed(object? sender, EventArgs e) { closed = true; Dispose(); }

    private void SetOriginal(Brush? brush)
    {
        if (original is { IsFrozen: false }) original.Changed -= OnOriginalChanged;
        original = brush;
        if (original is { IsFrozen: false }) original.Changed += OnOriginalChanged;
    }

    private void OnBackgroundChanged(object? sender, EventArgs e)
    {
        if (changingBackground || disposed || ReferenceEquals(window.Background, cached) ||
            ReferenceEquals(window.Background, original)) return;
        SetOriginal(window.Background);
        TrackResource();
        sourceVersion++;
        Invalidate();
    }

    private static void OnTrackedResourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not Window target || !Attachments.TryGetValue(target, out var attachment) ||
            attachment.disposed || attachment.changingTrackedResource ||
            ReferenceEquals(args.NewValue, attachment.original)) return;
        attachment.SetOriginal((Brush?)args.NewValue);
        attachment.sourceVersion++;
        attachment.Invalidate();
    }

    private void TrackResource()
    {
        // SetCurrentValue preserves the expression, but its effective cached
        // brush can hide resource invalidations from a Background observer. A
        // second reference on the same Window tracks the source in the same
        // resource scope without ever replacing a dictionary entry.
        object? key = null;
        var local = window.ReadLocalValue(Window.BackgroundProperty);
        if (local != DependencyProperty.UnsetValue)
        {
            var converter = TypeDescriptor.GetConverter(local);
            if (converter.CanConvertTo(typeof(MarkupExtension)) &&
                converter.ConvertTo(local, typeof(MarkupExtension)) is DynamicResourceExtension resource)
                key = resource.ResourceKey;
        }
        else
        {
            for (var style = window.Style; style is not null && key is null; style = style.BasedOn)
            {
                var setter = style.Setters.OfType<Setter>().LastOrDefault(item => item.Property == Window.BackgroundProperty);
                if (setter is null) continue;
                if (setter.Value is DynamicResourceExtension resource) key = resource.ResourceKey;
                break;
            }
        }
        changingTrackedResource = true;
        try
        {
            resourceKey = key;
            resourceIsLocal = local != DependencyProperty.UnsetValue;
            if (key is null) window.ClearValue(TrackedResourceProperty);
            else window.SetResourceReference(TrackedResourceProperty, key);
        }
        finally { changingTrackedResource = false; }
    }

    private void Invalidate()
    {
        ChangeBackground(original);
        cached = null;
        requested = null;
        generation++;
        IsReady = false;
        PreparationChanged?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    /// <summary>Coalesces a size/DPI/source change without a timer or a wait on the UI thread.</summary>
    public void Refresh()
    {
        window.Dispatcher.VerifyAccess();
        if (disposed || !contentRendered || window.WindowState == WindowState.Minimized) return;
        var target = ClientTarget(window);
        if (target.Width < 1 || target.Height < 1) return;
        if (requested is { } current && current.Target == target && current.SourceVersion == sourceVersion &&
            failedGeneration != current.Generation)
        {
            if (!IsReady && !workerRunning) SchedulePreparation();
            return;
        }

        generation++;
        IsReady = false;
        LastError = null;
        PreparationChanged?.Invoke(this, EventArgs.Empty);
        if (cached is not null && cachedTarget == target && cachedSourceVersion == sourceVersion)
        {
            requested = new Request(generation, sourceVersion, target, original!);
            ChangeBackground(cached);
            IsReady = true;
            PreparationChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        ChangeBackground(original);
        // A solid brush is already a cheap fill. Unsupported/unfreezable
        // backdrops keep their original rendering without a quality reduction.
        if (original is not DrawingBrush and not ImageBrush || !original.CanFreeze ||
            (long)target.Width * target.Height > MaximumPixels)
        {
            requested = null;
            return;
        }
        var snapshot = original.CloneCurrentValue();
        if (!snapshot.CanFreeze) { requested = null; return; }
        snapshot.Freeze();
        requested = new Request(generation, sourceVersion, target, snapshot);
        if (!workerRunning) SchedulePreparation();
    }

    private void SchedulePreparation()
    {
        queue.Forget(prepareKey);
        queue.Enqueue(prepareKey, 25, StartLatest());
    }

    private IEnumerable<Action> StartLatest()
    {
        yield return () =>
        {
            if (disposed || workerRunning || IsReady || window.WindowState == WindowState.Minimized || requested is not { } request) return;
            workerRunning = true;
            PreparationCount++;
            var worker = new Thread(() => RenderOnWorker(request))
            {
                IsBackground = true,
                Name = "Launcher backdrop preparation",
                Priority = ThreadPriority.BelowNormal
            };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        };
    }

    private void RenderOnWorker(Request request)
    {
        BitmapSource? bitmap = null;
        Exception? failure = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            var target = request.Target;
            bitmap = renderer(request.Brush, target.Width, target.Height, new DpiScale(target.DpiX, target.DpiY));
            if (!bitmap.IsFrozen) throw new InvalidOperationException("A prepared backdrop must be frozen.");
        }
        catch (Exception error) { failure = error; }
        finally { dispatcher.InvokeShutdown(); }
        try
        {
            if (window.Dispatcher.HasShutdownStarted) return;
            _ = window.Dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                queue.Forget(publishKey);
                queue.Enqueue(publishKey, 25, Publish(request, bitmap, failure));
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException) { /* The owning dispatcher has closed. */ }
    }

    private IEnumerable<Action> Publish(Request request, BitmapSource? bitmap, Exception? failure)
    {
        yield return () =>
        {
            workerRunning = false;
            if (disposed) return;
            if (window.WindowState == WindowState.Minimized) return;
            if (request.Generation == generation && failure is not null)
            {
                LastError = failure.Message;
                failedGeneration = generation;
                return;
            }
            if (request.Generation == generation && request.Target == ClientTarget(window) && bitmap is not null)
            {
                // The exact viewport contains one whole tile. Tile wrapping
                // enables WPF's integer-translation copy path; None becomes
                // Extend internally and resamples even a 1:1 software bitmap.
                cached = new ImageBrush(bitmap) { Stretch = Stretch.Fill, TileMode = TileMode.Tile };
                cached.Freeze();
                cachedTarget = request.Target;
                cachedSourceVersion = request.SourceVersion;
                ChangeBackground(cached);
                AppliedCount++;
                IsReady = true;
                PreparationChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Refresh catches a DPI/layout change that preceded its event.
                Refresh();
                if (requested is not null && !IsReady) SchedulePreparation();
            }
        };
    }

    private void ChangeBackground(Brush? brush)
    {
        // Resource invalidation has already installed its new value before it
        // notifies us. Do not write that value back inside the resource tree walk
        // or turn an unchanged expression value into a new current-value override.
        if (ReferenceEquals(window.Background, brush)) return;
        changingBackground = true;
        try { window.SetCurrentValue(Window.BackgroundProperty, brush); }
        finally { changingBackground = false; }
    }

    private static Target ClientTarget(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var rectangle)) return default;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Target(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    internal static BitmapSource Rasterize(Brush brush, int width, int height, DpiScale dpi)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(brush, null, new Rect(0, 0, width / dpi.DpiScaleX, height / dpi.DpiScaleY));
        var rendered = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rendered.Render(visual);
        // Detach the retained result from the worker's WPF rendering channel.
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        rendered.CopyPixels(pixels, stride, 0);
        // An opaque source should remain an opaque bitmap resource. Retaining
        // Pbgra32 needlessly sends a full-window backdrop through alpha blending
        // on every software repaint. Do not discard meaningful transparency.
        var opaque = true;
        for (var offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] != byte.MaxValue) { opaque = false; break; }
        var result = BitmapSource.Create(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            opaque ? PixelFormats.Bgr32 : PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        window.Dispatcher.VerifyAccess();
        disposed = true;
        surfaces?.Dispose(); surfaces = null;
        PreparationChanged = null;
        generation++;
        queue.Forget(prepareKey);
        queue.Forget(publishKey);
        window.ContentRendered -= OnContentRendered;
        window.SizeChanged -= OnSizeChanged;
        window.DpiChanged -= OnDpiChanged;
        window.StateChanged -= OnStateChanged;
        window.Closed -= OnClosed;
        BackgroundDescriptor.RemoveValueChanged(window, OnBackgroundChanged);
        window.ClearValue(TrackedResourceProperty);
        if (original is { IsFrozen: false }) original.Changed -= OnOriginalChanged;
        if (!closed && ReferenceEquals(window.Background, cached))
        {
            // Remove the current-value override rather than replacing it with
            // another one, so the original expression remains live after detach.
            changingBackground = true;
            try
            {
                if (resourceKey is not null && resourceIsLocal)
                    window.SetResourceReference(Window.BackgroundProperty, resourceKey);
                else window.InvalidateProperty(Window.BackgroundProperty);
            }
            finally { changingBackground = false; }
        }
        cached = null;
        requested = null;
        IsReady = false;
        Attachments.Remove(window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr handle, out ClientRectangle rectangle);
}
