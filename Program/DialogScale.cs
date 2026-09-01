using System.Windows;
using System.Windows.Media;

namespace Minecraft;

/// <summary>
/// Puts a dialog's type on the same scale as the launcher standing behind it.
/// </summary>
/// <remarks>
/// The main window is a fixed canvas of 820 by 574 inside a <c>Viewbox</c>, and
/// the window around it opens at 720 by 540 and can be dragged to any size that
/// keeps the ratio. So what a player sees is the design size times whatever the
/// Viewbox settled on - about six sevenths at the default size, less on a
/// smaller window.
///
/// A dialog has no Viewbox and drew its type at face value, which is how twelve
/// point text in a dialog came to stand next to the same twelve point text in
/// the launcher and be visibly larger. It is not that the sizes disagreed: they
/// were the same number on two different scales.
///
/// So the dialog's content is scaled by the factor the owner is using, measured
/// off the owner rather than assumed, and the window is left to size itself to
/// what that comes to. At the default window size a heading is a heading and a
/// line of body text matches the line of body text behind it exactly; when the
/// launcher is dragged larger, the next dialog opens larger with it.
/// </remarks>
internal static class DialogScale
{
    /// <summary>The element whose scale is the launcher's own.</summary>
    private const string CanvasName = "RootGrid";

    /// <summary>
    /// Scales <paramref name="content"/> to match the owner window's canvas.
    /// Does nothing when there is no owner, when it is not the launcher, or
    /// when it is already drawing at face value.
    /// </summary>
    public static void MatchOwner(Window dialog, FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(content);
        if (dialog.Owner is not { } owner) return;

        var scale = ScaleOf(owner);
        // Only ever shrink, and never to nothing: an owner mid-layout can
        // measure as anything, and a dialog that opens at a twentieth of its
        // size is worse than one that opens a little large.
        if (scale is not (> 0.4 and < 0.999)) return;
        content.LayoutTransform = new ScaleTransform(scale, scale);
    }

    /// <summary>
    /// How much smaller the owner is drawing its canvas than the canvas thinks
    /// it is. Measured by transforming a known distance rather than by reading
    /// sizes, because the Viewbox's factor is not a property anything exposes.
    /// </summary>
    private static double ScaleOf(Window owner)
    {
        try
        {
            if (owner.FindName(CanvasName) is not FrameworkElement canvas) return 0;
            if (!canvas.IsVisible || canvas.ActualWidth <= 0) return 0;

            var transform = canvas.TransformToAncestor(owner);
            var origin = transform.Transform(new Point(0, 0));
            var along = transform.Transform(new Point(100, 0));
            return (along.X - origin.X) / 100d;
        }
        catch (InvalidOperationException)
        {
            // The two are not in one visual tree - an owner that is closing, or
            // a dialog raised from somewhere that is not the launcher.
            return 0;
        }
    }
}
