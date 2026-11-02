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
    private bool applying;
    private BitmapSource? prepared;
    public PreparedImage()
    {
        Loaded += (_, _) => PrepareImage();
        IsVisibleChanged += (_, _) => { if (IsVisible) PrepareImage(); };
    }
    private int generation;
    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<(int, int), Task<BitmapSource>>> images = new();
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SourceProperty && !applying) { original = e.NewValue as BitmapSource; prepared = null; PrepareImage(); }
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    { base.OnRenderSizeChanged(sizeInfo); PrepareImage(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); PrepareImage(); }
    private async void PrepareImage()
    {
        var source = original;
        var version = ++generation;
        if (source is null || !source.IsFrozen || ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var factor = Stretch == Stretch.UniformToFill
            ? Math.Max(ActualWidth * dpi.DpiScaleX / source.PixelWidth, ActualHeight * dpi.DpiScaleY / source.PixelHeight)
            : Math.Min(ActualWidth * dpi.DpiScaleX / source.PixelWidth, ActualHeight * dpi.DpiScaleY / source.PixelHeight);
        // Retain native source detail when it is smaller than the destination.
        if (factor >= 1)
        {
            SetPreparedImage(source);
            return;
        }
        var size = (Math.Max(1, (int)Math.Ceiling(source.PixelWidth * factor)), Math.Max(1, (int)Math.Ceiling(source.PixelHeight * factor)));
        var cache = images.GetOrCreateValue(source);
        Task<BitmapSource> task;
        lock (cache)
        {
            if (!cache.TryGetValue(size, out task!))
            {
                // Bound obsolete resize variants. Live views retain their own current image.
                if (cache.Count >= 8) cache.Clear();
                cache[size] = task = Task.Run(() => Resize(source, size.Item1, size.Item2));
            }
        }
        try
        {
            var result = await task;
            if (version != generation) return;
            SetPreparedImage(result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        { /* The decoded original stays usable if a codec cannot resize it. */ }
    }
    private void SetPreparedImage(BitmapSource result)
    {
        prepared = result;
        // An async Source change invalidates Image's measure. Under a collapsed
        // owner (or a detached cached page), WPF cannot finish that layout and
        // later Popup/ScrollViewer LayoutUpdated callbacks can be starved.
        // Keep the decoded bitmap ready without invalidating that visual tree.
        if (!IsLoaded || !IsVisible || ReferenceEquals(Source, prepared)) return;
        applying = true;
        try { SetCurrentValue(SourceProperty, prepared); }
        finally { applying = false; }
    }
    private static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        var transformed = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        transformed.Freeze();
        var stride = (transformed.PixelWidth * transformed.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[checked(stride * transformed.PixelHeight)];
        transformed.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(transformed.PixelWidth, transformed.PixelHeight, transformed.DpiX, transformed.DpiY,
            transformed.Format, transformed.Palette, pixels, stride);
        result.Freeze();
        return result;
    }
}
