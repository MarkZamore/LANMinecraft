using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Minecraft;

/// <summary>Opt-in decoration caching; all content, bindings and native Border rendering remain live.</summary>
public class BackdropSurfaceBorder : Border
{
    public static readonly DependencyProperty IsBackdropSurfaceProperty = DependencyProperty.Register(
        nameof(IsBackdropSurface), typeof(bool), typeof(BackdropSurfaceBorder),
        new FrameworkPropertyMetadata(false, OnEligibilityChanged));
    private Brush? source;
    private ImageBrush? prepared;
    private BackdropSurfaceCache? owner;
    private bool needsRedraw;

    static BackdropSurfaceBorder() => BackgroundProperty.OverrideMetadata(typeof(BackdropSurfaceBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBackgroundChanged));
    public BackdropSurfaceBorder()
    {
        Loaded += (_, _) => { Connect(); RedrawWhenVisible(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) owner?.Schedule(); RedrawWhenVisible(); };
        Unloaded += (_, _) =>
        {
            owner?.Unregister(this);
            Disconnect(owner);
        };
    }
    public bool IsBackdropSurface { get => (bool)GetValue(IsBackdropSurfaceProperty); set => SetValue(IsBackdropSurfaceProperty, value); }
    public bool IsBackdropPrepared => prepared is not null;
    internal Brush? SourceBrush { get { SynchronizeSource(); return source; } }
    private void SynchronizeSource()
    {
        var value = Background;
        if (!ReferenceEquals(source, value))
        {
            if (source is { IsFrozen: false }) source.Changed -= OnSourceChanged;
            source = value;
            if (owner is not null && source is { IsFrozen: false }) source.Changed += OnSourceChanged;
        }
    }
    private static void OnBackgroundChanged(DependencyObject dependency, DependencyPropertyChangedEventArgs args)
    {
        var border = (BackdropSurfaceBorder)dependency;
        border.SynchronizeSource(); border.ClearPrepared(); border.owner?.Schedule();
    }
    private void OnSourceChanged(object? sender, EventArgs args) { ClearPrepared(); owner?.Schedule(); }
    private static void OnEligibilityChanged(DependencyObject dependency, DependencyPropertyChangedEventArgs args)
    {
        var border = (BackdropSurfaceBorder)dependency;
        border.ClearPrepared();
        if (border.IsLoaded) border.Connect();
    }
    private void Connect()
    {
        if (!IsBackdropSurface || Window.GetWindow(this) is not { } window ||
            !PreparedWindowBackdrop.TryFind(window, out var backdrop)) return;
        backdrop.SurfaceCache?.Register(this);
    }
    internal void Connect(BackdropSurfaceCache manager)
    {
        if (ReferenceEquals(owner, manager)) return;
        owner?.Unregister(this);
        if (source is { IsFrozen: false }) source.Changed -= OnSourceChanged;
        owner = manager;
        if (source is { IsFrozen: false }) source.Changed += OnSourceChanged;
        ClearPrepared();
    }
    internal void Disconnect(BackdropSurfaceCache? manager)
    {
        if (!ReferenceEquals(owner, manager)) return;
        if (source is { IsFrozen: false }) source.Changed -= OnSourceChanged;
        owner = null; ClearPrepared();
    }
    internal void UsePrepared(BitmapSourceAndViewport preparedSurface)
    {
        var replace = prepared is null || !ReferenceEquals(prepared.ImageSource, preparedSurface.Image);
        if (!replace && prepared!.Viewport == preparedSurface.Viewport) return;
        if (replace)
            prepared = new ImageBrush(preparedSurface.Image) { Stretch = Stretch.Fill, TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        if (prepared!.Viewport != preparedSurface.Viewport) prepared.Viewport = preparedSurface.Viewport;
        if (replace) InvalidateDecoration();
    }
    internal void ClearPrepared()
    {
        if (prepared is null) return;
        // Hidden cached pages must not retain obsolete full-window variants in
        // their old drawing data or enqueue invisible layout work to drop them.
        if (!IsLoaded || !IsVisible) prepared.ImageSource = null;
        prepared = null; InvalidateDecoration();
    }
    private void InvalidateDecoration() { needsRedraw = true; RedrawWhenVisible(); }
    private void RedrawWhenVisible() { if (needsRedraw && IsLoaded && IsVisible) InvalidateVisual(); }
    protected override void OnRender(DrawingContext drawingContext)
    {
        needsRedraw = false;
        SynchronizeSource();
        if (prepared is null || source is null) { base.OnRender(drawingContext); return; }
        // Native Border draws its stroke before its background. At antialiased
        // contour pixels the original translucent fill must still blend over
        // that stroke. Replace only a pixel-aligned, fully covered interior.
        var native = new DrawingGroup();
        using (var recording = native.Open()) base.OnRender(recording);
        drawingContext.DrawDrawing(ReplaceInterior(native, FindBackground(native)));
    }
    private GeometryDrawing? FindBackground(Drawing drawing)
    {
        if (drawing is GeometryDrawing { Pen: null } geometry && ReferenceEquals(geometry.Brush, source)) return geometry;
        GeometryDrawing? found = null;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children) found = FindBackground(child) ?? found;
        return found;
    }
    private Drawing ReplaceInterior(Drawing drawing, GeometryDrawing? background)
    {
        if (drawing is GeometryDrawing geometry && ReferenceEquals(geometry, background))
        {
            var bounds = geometry.Geometry.Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            var radius = Math.Max(Math.Max(CornerRadius.TopLeft, CornerRadius.TopRight), Math.Max(CornerRadius.BottomLeft, CornerRadius.BottomRight));
            var left = Math.Ceiling((bounds.Left + radius) * dpi.DpiScaleX + 1) / dpi.DpiScaleX;
            var top = Math.Ceiling((bounds.Top + radius) * dpi.DpiScaleY + 1) / dpi.DpiScaleY;
            var right = Math.Floor((bounds.Right - radius) * dpi.DpiScaleX - 1) / dpi.DpiScaleX;
            var bottom = Math.Floor((bounds.Bottom - radius) * dpi.DpiScaleY - 1) / dpi.DpiScaleY;
            if (right <= left || bottom <= top) return geometry;
            var interior = new RectangleGeometry(new Rect(new Point(left, top), new Point(right, bottom)));
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, geometry.Geometry, interior);
            interior.Freeze(); ring.Freeze();
            var result = new DrawingGroup();
            result.Children.Add(new GeometryDrawing(source, null, ring));
            result.Children.Add(new GeometryDrawing(prepared, null, interior));
            return result;
        }
        if (drawing is DrawingGroup group)
        {
            var result = new DrawingGroup { Transform = group.Transform, Opacity = group.Opacity, OpacityMask = group.OpacityMask,
                ClipGeometry = group.ClipGeometry, GuidelineSet = group.GuidelineSet };
            foreach (var child in group.Children) result.Children.Add(ReplaceInterior(child, background));
            return result;
        }
        return drawing;
    }
    internal readonly record struct BitmapSourceAndViewport(System.Windows.Media.Imaging.BitmapSource Image, Rect Viewport);
}
