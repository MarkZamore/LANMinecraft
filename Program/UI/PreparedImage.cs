using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Minecraft;

/// <summary>Materialize a size-specific image once on a worker; keep interactive text vector based.</summary>
public class PreparedImage : Image
{
    private BitmapSource? original;
    private BitmapSource? prepared;
    private ImageBrush? stamp;
    private ImageSource? drawnSource;
    public bool IsPreparationPending { get; private set; }
    public PreparedImage()
    {
        Loaded += (_, _) => PrepareImage();
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareImage(); };
    }
    private int generation;
    private readonly record struct Variant(int Width, int Height, Stretch Stretch, StretchDirection Direction,
        HorizontalAlignment Horizontal, VerticalAlignment Vertical, double DpiX, double DpiY);
    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<Variant, Task<BitmapSource>>> images = new();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SourceProperty) { original = e.NewValue as BitmapSource; prepared = null; stamp = null; PrepareImage(); }
        else if (e.Property == StretchProperty || e.Property == StretchDirectionProperty ||
            e.Property == HorizontalAlignmentProperty || e.Property == VerticalAlignmentProperty) { prepared = null; stamp = null; PrepareImage(); }
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var size = base.MeasureOverride(constraint);
        // Native Image retains a large UniformToFill ink size outside its slot.
        // Keep only the viewport; DrawSource preserves the identical crop.
        if (double.IsFinite(constraint.Width)) size.Width = Math.Min(size.Width, constraint.Width);
        if (double.IsFinite(constraint.Height)) size.Height = Math.Min(size.Height, constraint.Height);
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize) => finalSize;
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    { base.OnRenderSizeChanged(sizeInfo); prepared = null; stamp = null; PrepareImage(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); prepared = null; stamp = null; PrepareImage(); }
    private async void PrepareImage()
    {
        var source = original;
        var version = ++generation;
        IsPreparationPending = false;
        if (source is null || !source.IsFrozen || ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new Variant(Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)), Stretch, StretchDirection,
            HorizontalAlignment, VerticalAlignment, dpi.DpiScaleX, dpi.DpiScaleY);
        if ((long)size.Width * size.Height > 16L * 1024 * 1024)
        {
            SetPreparedImage(source);
            return;
        }
        var cache = images.GetOrCreateValue(source);
        Task<BitmapSource> task;
        lock (cache)
        {
            if (!cache.TryGetValue(size, out task!))
            {
                // Bound obsolete resize variants. Live views retain their own current image.
                if (cache.Count >= 4 || cache.Keys.Sum(key => (long)key.Width * key.Height) + (long)size.Width * size.Height > 16L * 1024 * 1024)
                    cache.Clear();
                var completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
                cache[size] = task = completion.Task;
                PreparedImageWorkQueue.Enqueue(source, completion,
                    () => { lock (cache) return cache.TryGetValue(size, out var current) && ReferenceEquals(current, completion.Task); },
                    () => Resize(source, size));
            }
        }
        if (IsVisible) PreparedImageWorkQueue.Promote(task);
        try
        {
            IsPreparationPending = true;
            var result = await task;
            if (version != generation) return;
            SetPreparedImage(result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException)
        { /* The decoded original stays usable if a codec cannot resize it. */ }
        finally { if (version == generation) IsPreparationPending = false; }
    }
    private void SetPreparedImage(BitmapSource result)
    {
        prepared = ReferenceEquals(result, original) ? null : result;
        stamp = prepared is null ? null : new ImageBrush(prepared) { Stretch = Stretch.Fill, TileMode = TileMode.Tile };
        stamp?.Freeze();
        // Rounded raster dimensions can have a slightly different aspect ratio.
        // Keep the bound source authoritative for WPF's measure/arrange; swapping
        // it can alternate RenderSize and starve scrolling's LayoutUpdated work.
        if (IsLoaded && IsVisible && !ReferenceEquals(drawnSource, prepared)) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        ImageSource? image = prepared ?? Source;
        drawnSource = image;
        if (image is null) return;
        if (stamp is not null) drawingContext.DrawRectangle(stamp, null, new Rect(new Point(), RenderSize));
        else DrawSource(drawingContext, image, RenderSize, Stretch, StretchDirection, HorizontalAlignment, VerticalAlignment);
    }
    private static void DrawSource(DrawingContext drawing, ImageSource source, Size size, Stretch stretch, StretchDirection direction,
        HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        if (source.Width <= 0 || source.Height <= 0) return;
        var scaleX = size.Width / source.Width;
        var scaleY = size.Height / source.Height;
        if (stretch == Stretch.Uniform) scaleX = scaleY = Math.Min(scaleX, scaleY);
        else if (stretch == Stretch.UniformToFill) scaleX = scaleY = Math.Max(scaleX, scaleY);
        else if (stretch == Stretch.None) scaleX = scaleY = 1;
        if (direction == StretchDirection.UpOnly) { scaleX = Math.Max(1, scaleX); scaleY = Math.Max(1, scaleY); }
        else if (direction == StretchDirection.DownOnly) { scaleX = Math.Min(1, scaleX); scaleY = Math.Min(1, scaleY); }
        var width = source.Width * scaleX;
        var height = source.Height * scaleY;
        var x = horizontal switch { HorizontalAlignment.Left => 0, HorizontalAlignment.Right => size.Width - width,
            HorizontalAlignment.Center => (size.Width - width) / 2, _ => Math.Max(0, (size.Width - width) / 2) };
        var y = vertical switch { VerticalAlignment.Top => 0, VerticalAlignment.Bottom => size.Height - height,
            VerticalAlignment.Center => (size.Height - height) / 2, _ => Math.Max(0, (size.Height - height) / 2) };
        drawing.PushClip(new RectangleGeometry(new Rect(new Point(), size)));
        drawing.DrawImage(source, new Rect(x, y, width, height));
        drawing.Pop();
    }
    private static BitmapSource Resize(BitmapSource source, Variant size)
    {
        var dpi = new DpiScale(size.DpiX, size.DpiY);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            DrawSource(drawing, source, new Size(size.Width / size.DpiX, size.Height / size.DpiY), size.Stretch, size.Direction,
                size.Horizontal, size.Vertical);
        var rendered = new RenderTargetBitmap(size.Width, size.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var stride = checked(size.Width * 4);
        var pixels = new byte[checked(stride * size.Height)];
        rendered.CopyPixels(pixels, stride, 0);
        var opaque = true;
        for (var offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] != byte.MaxValue) { opaque = false; break; }
        var result = BitmapSource.Create(size.Width, size.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            opaque ? PixelFormats.Bgr32 : PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
