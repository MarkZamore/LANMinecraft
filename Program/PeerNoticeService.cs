using System.Windows;

namespace Minecraft;

/// <summary>
/// Puts notices about other players in the corner of the screen and keeps them
/// in order.
/// </summary>
/// <remarks>
/// The bottom left corner of the work area, newest at the bottom, older ones
/// pushed up. The primary monitor, and the left corner rather than the right:
/// the right one is where Windows puts its own notifications, and two things
/// arriving in the same corner cover each other.
/// </remarks>
public sealed class PeerNoticeService(Window owner, Logger? logger = null) : IDisposable
{
    // Three at once is the point at which a corner stops being readable. A
    // fourth pushes the oldest off rather than growing the stack, because the
    // one that has been there longest is the one already read.
    private const int MostAtOnce = 3;

    private readonly List<PeerNoticeWindow> _open = [];
    private bool _disposed;

    /// <summary>Shows one notice.</summary>
    public void Show(PeerNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_disposed) return;

        try
        {
            while (_open.Count >= MostAtOnce) _open[0].Dismiss();

            var window = new PeerNoticeWindow(owner, notice);
            window.Closed += (_, _) =>
            {
                _open.Remove(window);
                Restack();
            };
            _open.Add(window);
            window.Show();
            Restack();
        }
        catch (Exception ex)
        {
            logger?.Warn($"Could not show a notice about another player: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Copied first: dismissing raises Closed, which edits the list.
        foreach (var window in _open.ToArray()) window.Dismiss();
        _open.Clear();
    }

    /// <summary>
    /// Lays the open notices out from the bottom up, so that one going away
    /// closes the gap instead of leaving one.
    /// </summary>
    private void Restack()
    {
        var area = SystemParameters.WorkArea;
        // The gap the launcher leaves at the edge of its own canvas, used again
        // here rather than a number of this window's own.
        var margin = Application.Current?.TryFindResource("Gap.Canvas") is Thickness gap ? gap.Left : 0;
        var bottom = area.Bottom - margin;

        for (var index = _open.Count - 1; index >= 0; index--)
        {
            var window = _open[index];
            if (window.ActualHeight <= 0) continue;
            window.Left = area.Left + margin;
            window.Top = bottom - window.ActualHeight;
            bottom -= window.ActualHeight + margin;
        }
    }
}
