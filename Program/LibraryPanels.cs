using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Minecraft;

/// <summary>Equal-width responsive columns; an addon group always stays intact.</summary>
public sealed class LibraryColumnsPanel : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(LibraryColumnsPanel),
        new FrameworkPropertyMetadata(240d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(LibraryColumnsPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty BalanceHeightProperty = DependencyProperty.Register(
        nameof(BalanceHeight), typeof(bool), typeof(LibraryColumnsPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FixedColumnWidthProperty = DependencyProperty.Register(
        nameof(FixedColumnWidth), typeof(double), typeof(LibraryColumnsPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(LibraryColumnsPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty UniformItemHeightProperty = DependencyProperty.Register(
        nameof(UniformItemHeight), typeof(bool), typeof(LibraryColumnsPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinimumColumnWidth { get => (double)GetValue(MinimumColumnWidthProperty); set => SetValue(MinimumColumnWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public bool BalanceHeight { get => (bool)GetValue(BalanceHeightProperty); set => SetValue(BalanceHeightProperty, value); }
    public double FixedColumnWidth { get => (double)GetValue(FixedColumnWidthProperty); set => SetValue(FixedColumnWidthProperty, value); }
    public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    public bool UniformItemHeight { get => (bool)GetValue(UniformItemHeightProperty); set => SetValue(UniformItemHeightProperty, value); }
    private readonly List<Rect> _rectangles = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinimumColumnWidth;
        var columns = Math.Min(Math.Max(1, InternalChildren.Count), Math.Max(1, (int)((width + Gap) / (MinimumColumnWidth + Gap))));
        var cell = double.IsFinite(FixedColumnWidth) ? Math.Min(width, FixedColumnWidth) :
            Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        var rowGap = double.IsFinite(RowGap) ? RowGap : Gap;
        _rectangles.Clear();
        var index = 0;
        foreach (UIElement child in InternalChildren) child.Measure(new Size(cell, double.PositiveInfinity));
        var uniformHeight = UniformItemHeight
            ? InternalChildren.Cast<UIElement>().Select(child => child.DesiredSize.Height).DefaultIfEmpty(0).Max() : 0;
        foreach (UIElement child in InternalChildren)
        {
            var height = UniformItemHeight ? uniformHeight : child.DesiredSize.Height;
            var column = BalanceHeight ? Array.IndexOf(heights, heights.Min()) : index % columns;
            var top = BalanceHeight ? heights[column] : heights.Take(columns).Max();
            if (!BalanceHeight)
            {
                if (column == 0 && index != 0) top = heights.Max();
                else if (column != 0) top = _rectangles[index - column].Top;
            }
            _rectangles.Add(new Rect(column * (cell + Gap), top, cell, height));
            heights[column] = top + height + rowGap;
            index++;
        }
        return new Size(width, InternalChildren.Count == 0 ? 0 : Math.Max(0, heights.Max() - rowGap));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(_rectangles[i]);
        return finalSize;
    }
}

public sealed class LibraryPreviewImage : Image
{
    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : 240;
        var size = new Size(width, width * 9 / 16);
        base.MeasureOverride(size);
        return size;
    }
}

/// <summary>Keep the standard Border contour; clip only its content inside the frame.</summary>
public sealed class RoundedClipBorder : Border
{
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty || e.Property == BorderThicknessProperty || e.Property == PaddingProperty)
            UpdateContentClip();
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        UpdateContentClip();
        return size;
    }

    private void UpdateContentClip()
    {
        if (Child is not { } child) return;
        // WPF Border measures CornerRadius at the middle of the stroke. Its
        // inner curve subtracts half the stroke, not the full border thickness.
        // Clipping the whole Border instead would cut its outer curve and AA.
        var inset = BorderThickness;
        var padding = Padding;
        var radius = CornerRadius;
        var size = child.RenderSize;
        var tl = new Size(Math.Max(0, radius.TopLeft - inset.Left / 2 - padding.Left), Math.Max(0, radius.TopLeft - inset.Top / 2 - padding.Top));
        var tr = new Size(Math.Max(0, radius.TopRight - inset.Right / 2 - padding.Right), Math.Max(0, radius.TopRight - inset.Top / 2 - padding.Top));
        var br = new Size(Math.Max(0, radius.BottomRight - inset.Right / 2 - padding.Right), Math.Max(0, radius.BottomRight - inset.Bottom / 2 - padding.Bottom));
        var bl = new Size(Math.Max(0, radius.BottomLeft - inset.Left / 2 - padding.Left), Math.Max(0, radius.BottomLeft - inset.Bottom / 2 - padding.Bottom));
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(tl.Width, 0), true, true);
            path.LineTo(new Point(size.Width - tr.Width, 0), true, false);
            path.ArcTo(new Point(size.Width, tr.Height), tr, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(size.Width, size.Height - br.Height), true, false);
            path.ArcTo(new Point(size.Width - br.Width, size.Height), br, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(bl.Width, size.Height), true, false);
            path.ArcTo(new Point(0, size.Height - bl.Height), bl, 0, false, SweepDirection.Clockwise, true, false);
            path.LineTo(new Point(0, tl.Height), true, false);
            path.ArcTo(new Point(tl.Width, 0), tl, 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        child.Clip = geometry;
    }
}
